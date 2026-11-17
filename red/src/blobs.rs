//! Blobs por huella (api.md §5.7): un stream QUIC bidireccional por blob, aparte del canal ordenado y con menos
//! prioridad. Quien pide manda `{"huella","tamano"}` y recibe los bytes en crudo; quien ofrece solo sirve lo que su
//! app ofreció, y las rutas (de ambos lados) quedan dentro de la carpeta de sala.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::{Duration, Instant};

use crate::limites::{Cupos, Reserva};
use iroh::EndpointId;
use iroh::endpoint::{Connection, RecvStream, SendStream, VarInt};
use rand::RngCore;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::Notify;

use crate::hex;
use crate::marco;
use crate::sala::{Fallo, Sala};

pub const MAX_BLOB: u64 = 4 << 30;
const MAX_LISTA: usize = 1024;
const POR_PAR: usize = 4;
const TOTAL: usize = 16;
const TROZO: usize = 64 * 1024;
const AVANCE: Duration = Duration::from_millis(250);

/// Códigos de reinicio del stream de un blob (api.md §3).
mod corte {
    pub const NO_OFRECIDO: u32 = 1;
    pub const CANCELADO: u32 = 2;
    pub const LECTURA: u32 = 3;
    pub const TAMANO: u32 = 4;
    pub const OCUPADO: u32 = 5;
}

#[derive(Default)]
pub struct Blobs {
    ofrecidos: HashMap<String, (PathBuf, u64)>,
    descargas: HashMap<u64, Descarga>,
    sirviendo: Cupos,
    cerrando: bool,
}

static INTENTO: AtomicU64 = AtomicU64::new(1);

struct Descarga {
    de: EndpointId,
    destino: PathBuf,
    cancelar: Arc<Cancelacion>,
    fin: Arc<Notify>,
}

#[derive(Default)]
struct Cancelacion {
    pedida: AtomicBool,
    aviso: Notify,
}

impl Cancelacion {
    fn pedir(&self) {
        self.pedida.store(true, Ordering::SeqCst);
        self.aviso.notify_one();
    }
}

impl Blobs {
    fn reservar_descarga(
        &mut self,
        de: EndpointId,
        destino: PathBuf,
    ) -> Result<(u64, Arc<Cancelacion>, Arc<Notify>), Fallo> {
        if self.cerrando
            || self.descargas.len() >= TOTAL
            || self.descargas.values().filter(|d| d.de == de).count() >= POR_PAR
            || self.descargas.values().any(|d| d.destino == destino)
        {
            return Err(Fallo(
                "ocupado",
                "No hay cupo o el destino ya está en uso.".into(),
            ));
        }
        let intento = INTENTO
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |n| n.checked_add(1))
            .map_err(|_| {
                Fallo(
                    "ocupado",
                    "Se agotaron los identificadores de intento.".into(),
                )
            })?;
        let cancelar = Arc::new(Cancelacion::default());
        let fin = Arc::new(Notify::new());
        self.descargas.insert(
            intento,
            Descarga {
                de,
                destino,
                cancelar: cancelar.clone(),
                fin: fin.clone(),
            },
        );
        Ok((intento, cancelar, fin))
    }

    fn reservar_servicio(&self, de: EndpointId) -> Option<Reserva> {
        self.sirviendo.reservar(de, POR_PAR, TOTAL)
    }
}

fn nombre_parte(destino: &Path) -> PathBuf {
    let mut azar = [0u8; 8];
    rand::rng().fill_bytes(&mut azar);
    let mut nombre = destino.as_os_str().to_os_string();
    nombre.push(format!(".{}.parte", hex::codificar(&azar)));
    PathBuf::from(nombre)
}

fn abrir_parte(carpeta: &Path, parte: &Path) -> Result<tokio::fs::File, &'static str> {
    // La apertura síncrona exclusiva no puede abandonarse con una creación pendiente en otro hilo.
    let f = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(parte)
        .map_err(|_| "disco")?;
    let confinado = carpeta
        .canonicalize()
        .ok()
        .zip(parte.canonicalize().ok())
        .is_some_and(|(base, real)| real.starts_with(base));
    if !confinado {
        drop(f);
        let _ = std::fs::remove_file(parte);
        return Err("disco");
    }
    Ok(tokio::fs::File::from_std(f))
}

async fn cancelar_o<F: std::future::Future<Output = Result<(), &'static str>>>(
    cancelar: &Cancelacion,
    operacion: F,
) -> Result<(), &'static str> {
    tokio::select! {
        biased;
        _ = cancelar.aviso.notified() => Err("cancelado"),
        r = operacion => r,
    }
}

async fn terminar_archivo(
    mut f: tokio::fs::File,
    carpeta: &Path,
    parte: &Path,
    destino: &Path,
    resultado: Result<(), &'static str>,
    cancelar: &Cancelacion,
) -> Result<(), &'static str> {
    // flush espera también cualquier escritura de tokio que siguiera en su hilo al cancelar.
    let escritura = f.flush().await.map_err(|_| "disco");
    let mut resultado = resultado.and(escritura);
    if resultado.is_ok() {
        resultado = f.sync_all().await.map_err(|_| "disco");
    }
    drop(f);
    if cancelar.pedida.load(Ordering::SeqCst) {
        resultado = Err("cancelado");
    }
    if resultado.is_ok() {
        resultado = dentro(carpeta, destino, false)
            .map_err(|_| "disco")
            .and_then(|_| std::fs::rename(parte, destino).map_err(|_| "disco"));
    }
    if resultado.is_err() {
        std::fs::remove_file(parte).map_err(|_| "disco")?;
    }
    resultado
}

fn invalido<T>(m: &str) -> Result<T, Fallo> {
    Err(Fallo("invalid-parameters", m.into()))
}

fn huella(v: &Value) -> Result<String, Fallo> {
    match v.as_str() {
        Some(h) if hex::bytes32(h).is_some() => Ok(h.to_string()),
        _ => invalido("Una huella son 64 hexadecimales en minúscula."),
    }
}

/// `ruta` resuelta tiene que quedar dentro de `carpeta` (sin `..`, enlaces ni otra unidad que la saquen).
fn dentro(carpeta: &Path, ruta: &Path, existe: bool) -> Result<PathBuf, Fallo> {
    if !ruta.is_absolute() {
        return invalido("La ruta tiene que ser absoluta.");
    }
    let base = carpeta
        .canonicalize()
        .map_err(|_| Fallo("invalid-parameters", "La carpeta de sala no existe.".into()))?;
    let real = if existe {
        ruta.canonicalize()
            .map_err(|_| Fallo("invalid-parameters", "El archivo no existe.".into()))?
    } else {
        let nombre = ruta.file_name().ok_or(Fallo(
            "invalid-parameters",
            "Falta el nombre del archivo.".into(),
        ))?;
        let padre = ruta
            .parent()
            .and_then(|p| p.canonicalize().ok())
            .ok_or(Fallo(
                "invalid-parameters",
                "La carpeta de destino no existe.".into(),
            ))?;
        padre.join(nombre)
    };
    if !real.starts_with(&base) {
        return invalido("La ruta queda fuera de la carpeta de sala.");
    }
    Ok(real)
}

impl Sala {
    pub fn blob_ofrecer(&self, p: &Value) -> Result<Value, Fallo> {
        let lista = p
            .get("blobs")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        if lista.is_empty() || lista.len() > MAX_LISTA {
            return invalido("«blobs» va de 1 a 1024.");
        }
        let mut nuevos = Vec::new();
        for b in &lista {
            let h = huella(&b["huella"])?;
            let tamano = b["tamano"]
                .as_u64()
                .filter(|t| *t <= MAX_BLOB)
                .ok_or(Fallo("invalid-parameters", "Tamaño inválido.".into()))?;
            let ruta = dentro(
                &self.carpeta,
                Path::new(b["ruta"].as_str().unwrap_or("")),
                true,
            )?;
            nuevos.push((h, (ruta, tamano)));
        }
        self.blobs.lock().unwrap().ofrecidos.extend(nuevos);
        Ok(Value::Null)
    }

    pub fn blob_retirar(&self, p: &Value) -> Result<Value, Fallo> {
        let mut b = self.blobs.lock().unwrap();
        for h in p
            .get("huellas")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default()
        {
            b.ofrecidos.remove(h.as_str().unwrap_or(""));
        }
        Ok(Value::Null)
    }

    pub fn blob_cancelar(&self, p: &Value) -> Result<Value, Fallo> {
        let intento = p["intento"].as_u64().filter(|n| *n != 0).ok_or(Fallo(
            "invalid-parameters",
            "Falta «intento» (u64 positivo).".into(),
        ))?;
        if let Some(d) = self.blobs.lock().unwrap().descargas.get(&intento) {
            d.cancelar.pedir();
        }
        Ok(Value::Null)
    }

    pub(crate) async fn cancelar_descargas(&self) {
        let finales: Vec<_> = {
            let mut b = self.blobs.lock().unwrap();
            b.cerrando = true;
            b.descargas
                .values()
                .map(|d| {
                    d.cancelar.pedir();
                    d.fin.clone()
                })
                .collect()
        };
        for fin in finales {
            fin.notified().await;
        }
    }

    pub fn blob_pedir(self: &Arc<Self>, p: &Value) -> Result<Value, Fallo> {
        let h = huella(&p["huella"])?;
        let tamano = p["tamano"]
            .as_u64()
            .ok_or(Fallo("invalid-parameters", "Falta «tamano».".into()))?;
        if tamano > MAX_BLOB {
            return Err(Fallo(
                "demasiado-grande",
                "Un blob no pasa de 4 GiB.".into(),
            ));
        }
        let de = p["de"]
            .as_str()
            .and_then(hex::clave)
            .ok_or(Fallo("invalid-parameters", "«de» no es una clave.".into()))?;
        let destino = dentro(
            &self.carpeta,
            Path::new(p["destino"].as_str().unwrap_or("")),
            false,
        )?;
        let conn = self
            .estado
            .lock()
            .unwrap()
            .pares
            .get(&de)
            .map(|par| par.conn.clone())
            .ok_or(Fallo("desconocido", "Ese par no está conectado.".into()))?;
        let mut b = self.blobs.lock().unwrap();
        let (intento, cancelar, fin) = b.reservar_descarga(de, destino.clone())?;
        let sala = self.clone();
        tokio::spawn(async move {
            let parte = nombre_parte(&destino);
            let r = match abrir_parte(&sala.carpeta, &parte) {
                Ok(mut f) => {
                    let r = cancelar_o(&cancelar, bajar(&sala, &conn, &h, tamano, intento, &mut f))
                        .await;
                    terminar_archivo(f, &sala.carpeta, &parte, &destino, r, &cancelar).await
                }
                Err(e) => Err(e),
            };
            // Solo esta tarea termina el intento y emite su resultado, también al cancelar o cerrar.
            let mut b = sala.blobs.lock().unwrap();
            sala.evento(match r {
                Ok(()) => json!({"tipo":"blob-fin","huella":h,"intento":intento,"ok":true}),
                Err(e) => {
                    json!({"tipo":"blob-fin","huella":h,"intento":intento,"ok":false,"error":e})
                }
            });
            b.descargas.remove(&intento);
            fin.notify_one();
        });
        Ok(json!({"intento": intento}))
    }
}

async fn bajar(
    sala: &Sala,
    conn: &Connection,
    h: &str,
    tamano: u64,
    intento: u64,
    f: &mut tokio::fs::File,
) -> Result<(), &'static str> {
    let (mut envio, mut recibo) = conn.open_bi().await.map_err(|_| "red")?;
    envio
        .write_all(&marco::armar(&json!({"huella": h, "tamano": tamano})))
        .await
        .map_err(|_| "red")?;
    let _ = envio.finish();
    let mut hash = Sha256::new();
    let mut llevo = 0u64;
    let mut ultimo = Instant::now();
    loop {
        let trozo = match recibo.read_chunk(TROZO).await {
            Ok(Some(t)) => t,
            Ok(None) => break,
            Err(iroh::endpoint::ReadError::Reset(c)) => {
                return Err(match c.into_inner() as u32 {
                    corte::NO_OFRECIDO => "no-ofrecido",
                    corte::CANCELADO => "cancelado",
                    corte::TAMANO => "huella",
                    corte::OCUPADO => "ocupado",
                    _ => "red",
                });
            }
            Err(_) => return Err("red"),
        };
        llevo += trozo.len() as u64;
        if llevo > tamano {
            let _ = recibo.stop(VarInt::from_u32(corte::TAMANO));
            return Err("huella");
        }
        hash.update(&trozo);
        f.write_all(&trozo).await.map_err(|_| "disco")?;
        if ultimo.elapsed() >= AVANCE {
            ultimo = Instant::now();
            sala.evento(
                json!({"tipo":"blob-avance","huella":h,"intento":intento,"recibidos":llevo}),
            );
        }
    }
    if llevo != tamano || hex::codificar(&hash.finalize()) != h {
        return Err("huella");
    }
    Ok(())
}

/// Atiende los streams de blob que abre un par en su conexión (el primero, el canal ordenado, ya se aceptó antes).
pub async fn servir(sala: Arc<Sala>, conn: Connection) {
    let quien = conn.remote_id();
    while let Ok((envio, recibo)) = conn.accept_bi().await {
        let sala = sala.clone();
        tokio::spawn(async move { servir_uno(&sala, quien, envio, recibo).await });
    }
}

async fn servir_uno(sala: &Sala, quien: EndpointId, mut envio: SendStream, mut recibo: RecvStream) {
    let reserva = sala.blobs.lock().unwrap().reservar_servicio(quien);
    let Some(_reserva) = reserva else {
        let _ = envio.reset(VarInt::from_u32(corte::OCUPADO));
        let _ = recibo.stop(VarInt::from_u32(corte::OCUPADO));
        return;
    };
    let pedido = match tokio::time::timeout(
        Duration::from_secs(10),
        marco::leer(&mut recibo, 4096, marco::MAX_PROFUNDIDAD_PAR),
    )
    .await
    {
        Ok(Ok(v)) => v,
        _ => {
            let _ = envio.reset(VarInt::from_u32(corte::NO_OFRECIDO));
            return;
        }
    };
    let h = pedido["huella"].as_str().unwrap_or("").to_string();
    let ofrecido = sala.blobs.lock().unwrap().ofrecidos.get(&h).cloned();
    let Some((ruta, tamano)) = ofrecido else {
        let _ = envio.reset(VarInt::from_u32(corte::NO_OFRECIDO));
        return;
    };
    if pedido["tamano"].as_u64() != Some(tamano) {
        let _ = envio.reset(VarInt::from_u32(corte::TAMANO));
        return;
    }
    let codigo = enviar_archivo(&mut envio, &ruta, tamano).await;
    match codigo {
        Ok(()) => {
            let _ = envio.finish();
            let _ = tokio::time::timeout(Duration::from_secs(30), envio.stopped()).await;
        }
        Err(c) => {
            let _ = envio.reset(VarInt::from_u32(c));
        }
    }
}

async fn enviar_archivo(envio: &mut SendStream, ruta: &Path, tamano: u64) -> Result<(), u32> {
    let _ = envio.set_priority(-1);
    let mut f = tokio::fs::File::open(ruta)
        .await
        .map_err(|_| corte::LECTURA)?;
    let largo = f.metadata().await.map_err(|_| corte::LECTURA)?.len();
    if largo != tamano {
        return Err(corte::TAMANO);
    }
    let mut buf = vec![0u8; TROZO];
    let mut quedan = tamano;
    while quedan > 0 {
        let n = f.read(&mut buf).await.map_err(|_| corte::LECTURA)?;
        if n == 0 {
            return Err(corte::TAMANO);
        }
        let n = n.min(quedan as usize);
        envio
            .write_all(&buf[..n])
            .await
            .map_err(|_| corte::CANCELADO)?;
        quedan -= n as u64;
    }
    Ok(())
}

#[cfg(test)]
mod pruebas {
    use super::*;
    use crate::pruebas::Carpeta;

    fn clave(n: u8) -> EndpointId {
        iroh::SecretKey::from_bytes(&[n; 32]).public()
    }

    #[test]
    fn destino_resuelto_duplicado_da_ocupado() {
        let carpeta = Carpeta::nueva();
        std::fs::create_dir(carpeta.0.join("sub")).unwrap();
        let a = dentro(&carpeta.0, &carpeta.0.join("dato"), false).unwrap();
        let b = dentro(&carpeta.0, &carpeta.0.join("sub/../dato"), false).unwrap();
        let mut blobs = Blobs::default();
        let (intento, _, _) = blobs.reservar_descarga(clave(1), a.clone()).unwrap();
        assert_eq!(blobs.reservar_descarga(clave(2), b).err().unwrap().0, "ocupado");
        blobs.descargas.remove(&intento);
        let (siguiente, _, _) = blobs.reservar_descarga(clave(2), a).unwrap();
        assert!(siguiente > intento);
    }

    #[test]
    fn descargas_respetan_cuatro_por_par_y_dieciseis_totales() {
        let mut blobs = Blobs::default();
        for par in 0..4 {
            for n in 0..POR_PAR {
                blobs.reservar_descarga(clave(par), PathBuf::from(format!("{par}-{n}"))).unwrap();
            }
            assert_eq!(blobs.reservar_descarga(clave(par), PathBuf::from("extra")).err().unwrap().0, "ocupado");
        }
        assert_eq!(blobs.descargas.len(), TOTAL);
        assert_eq!(blobs.reservar_descarga(clave(5), PathBuf::from("extra")).err().unwrap().0, "ocupado");
        let intento = *blobs.descargas.keys().next().unwrap();
        blobs.descargas.remove(&intento);
        assert!(blobs.reservar_descarga(clave(5), PathBuf::from("extra")).is_ok());
        blobs.cerrando = true;
        assert_eq!(blobs.reservar_descarga(clave(6), PathBuf::from("otro")).err().unwrap().0, "ocupado");
    }

    #[test]
    fn servicios_reservan_cupos_por_sala_y_drop_los_libera() {
        let blobs = Blobs::default();
        let mut reservas = Vec::new();
        for par in 0..4 {
            for _ in 0..POR_PAR { reservas.push(blobs.reservar_servicio(clave(par)).unwrap()); }
            assert!(blobs.reservar_servicio(clave(par)).is_none());
        }
        assert!(blobs.reservar_servicio(clave(5)).is_none());
        let otra_sala = Blobs::default();
        assert!(otra_sala.reservar_servicio(clave(5)).is_some());
        reservas.pop();
        assert!(blobs.reservar_servicio(clave(5)).is_some());
        drop(reservas);
        assert!(blobs.reservar_servicio(clave(0)).is_some());
    }

    #[tokio::test]
    async fn cancelar_limpia_parte_y_preserva_destino() {
        let carpeta = Carpeta::nueva();
        let destino = carpeta.0.join("dato");
        std::fs::write(&destino, b"anterior").unwrap();
        let parte = nombre_parte(&destino);
        let mut f = abrir_parte(&carpeta.0, &parte).unwrap();
        let cancelar = Cancelacion::default();
        let (mut escritor, mut lector) = tokio::io::duplex(64);
        let operacion = async {
            let mut bytes = [0u8; 4];
            lector.read_exact(&mut bytes).await.unwrap();
            f.write_all(&bytes).await.unwrap();
            cancelar.pedir();
            std::future::pending::<Result<(), &'static str>>().await
        };
        escritor.write_all(b"dato").await.unwrap();
        let r = cancelar_o(&cancelar, operacion).await;
        assert_eq!(r, Err("cancelado"));
        assert_eq!(terminar_archivo(f, &carpeta.0, &parte, &destino, r, &cancelar).await, Err("cancelado"));
        assert!(!parte.exists());
        assert_eq!(std::fs::read(&destino).unwrap(), b"anterior");
    }

    #[tokio::test]
    async fn cancelar_antes_de_esperar_y_antes_de_renombrar() {
        let carpeta = Carpeta::nueva();
        let destino = carpeta.0.join("dato");
        let parte = nombre_parte(&destino);
        let f = abrir_parte(&carpeta.0, &parte).unwrap();
        let cancelar = Cancelacion::default();
        cancelar.pedir();
        assert_eq!(cancelar_o(&cancelar, std::future::pending()).await, Err("cancelado"));
        assert_eq!(terminar_archivo(f, &carpeta.0, &parte, &destino, Ok(()), &cancelar).await, Err("cancelado"));
        assert!(!parte.exists());
        assert!(!destino.exists());
    }

    #[tokio::test]
    async fn parte_exclusiva_no_trunca_y_nombre_es_aleatorio() {
        let carpeta = Carpeta::nueva();
        let destino = carpeta.0.join("dato.bin");
        let parte = nombre_parte(&destino);
        assert_ne!(parte, nombre_parte(&destino));
        let nombre = parte.file_name().unwrap().to_str().unwrap();
        let azar = nombre.strip_prefix("dato.bin.").unwrap().strip_suffix(".parte").unwrap();
        assert_eq!(azar.len(), 16);
        assert!(azar.bytes().all(|c| c.is_ascii_hexdigit()));
        std::fs::write(&parte, b"intacto").unwrap();
        assert!(abrir_parte(&carpeta.0, &parte).is_err());
        assert_eq!(std::fs::read(&parte).unwrap(), b"intacto");
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn enlaces_en_parte_y_destino_no_tocan_archivo_exterior() {
        use std::os::unix::fs::symlink;
        let sala = Carpeta::nueva();
        let fuera = Carpeta::nueva();
        let externo = fuera.0.join("intacto");
        std::fs::write(&externo, b"intacto").unwrap();
        let destino = sala.0.join("dato");
        let parte = nombre_parte(&destino);
        symlink(&externo, &parte).unwrap();
        assert!(abrir_parte(&sala.0, &parte).is_err());
        assert_eq!(std::fs::read(&externo).unwrap(), b"intacto");
        let parte = nombre_parte(&destino);
        symlink(&externo, &destino).unwrap();
        let mut f = abrir_parte(&sala.0, &parte).unwrap();
        f.write_all(b"nuevo").await.unwrap();
        terminar_archivo(f, &sala.0, &parte, &destino, Ok(()), &Cancelacion::default()).await.unwrap();
        assert_eq!(std::fs::read(&externo).unwrap(), b"intacto");
        assert_eq!(std::fs::read(&destino).unwrap(), b"nuevo");
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn padre_sustituido_por_enlace_impide_rename() {
        use std::os::unix::fs::symlink;
        let sala = Carpeta::nueva();
        let fuera = Carpeta::nueva();
        let padre = sala.0.join("sub");
        std::fs::create_dir(&padre).unwrap();
        let destino = padre.join("dato");
        // El temporal puede quedar en otro directorio de la misma sala.
        let parte = nombre_parte(&sala.0.join("dato"));
        let mut f = abrir_parte(&sala.0, &parte).unwrap();
        f.write_all(b"nuevo").await.unwrap();
        std::fs::remove_dir(&padre).unwrap();
        symlink(&fuera.0, &padre).unwrap();
        assert_eq!(terminar_archivo(f, &sala.0, &parte, &destino, Ok(()), &Cancelacion::default()).await, Err("disco"));
        assert!(!parte.exists());
        assert!(!fuera.0.join("dato").exists());
        // También rechaza y borra un temporal creado por un padre ya sustituido.
        let parte = nombre_parte(&destino);
        assert!(matches!(abrir_parte(&sala.0, &parte), Err("disco")));
        assert_eq!(std::fs::read_dir(&fuera.0).unwrap().count(), 0);
    }

    #[test]
    fn rutas_fuera_de_la_carpeta_no_valen() {
        let base = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("target")
            .join(format!("sala-blobs-{}", std::process::id()));
        std::fs::create_dir_all(base.join("sub")).unwrap();
        assert!(dentro(&base, &base.join("sub").join("a.bin"), false).is_ok());
        assert!(
            dentro(
                &base,
                &base.join("sub").join("..").join("..").join("x.bin"),
                false
            )
            .is_err()
        );
        assert!(dentro(&base, Path::new("relativa.bin"), false).is_err());
        assert!(dentro(&base, &std::env::temp_dir().join("fuera.bin"), false).is_err());
        assert!(dentro(&base, &base.join("no-existe").join("a.bin"), false).is_err());
        let _ = std::fs::remove_dir_all(&base);
    }
}
