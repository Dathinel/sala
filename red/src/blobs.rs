//! Blobs por huella (api.md §5.7): un stream QUIC bidireccional por blob, aparte del canal ordenado y con menos
//! prioridad. Quien pide manda `{"huella","tamano"}` y recibe los bytes en crudo; quien ofrece solo sirve lo que su
//! app ofreció, y las rutas (de ambos lados) quedan dentro de la carpeta de sala.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use iroh::EndpointId;
use iroh::endpoint::{Connection, RecvStream, SendStream, VarInt};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::task::AbortHandle;

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
    descargas: HashMap<String, AbortHandle>,
    sirviendo: HashMap<EndpointId, usize>,
}

static SIRVIENDO_TOTAL: AtomicUsize = AtomicUsize::new(0);

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
        let h = huella(&p["huella"])?;
        if let Some(a) = self.blobs.lock().unwrap().descargas.remove(&h) {
            a.abort();
            self.evento(json!({"tipo":"blob-fin","huella":h,"ok":false,"error":"cancelado"}));
        }
        Ok(Value::Null)
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
        if b.descargas.contains_key(&h) {
            return Err(Fallo("ocupado", "Ese blob ya se está bajando.".into()));
        }
        if b.descargas.len() >= TOTAL {
            return Err(Fallo("ocupado", "Hay 16 descargas en curso.".into()));
        }
        let sala = self.clone();
        let hh = h.clone();
        let tarea = tokio::spawn(async move {
            let parte = destino.with_extension(
                destino
                    .extension()
                    .map(|e| format!("{}.parte", e.to_string_lossy()))
                    .unwrap_or("parte".into()),
            );
            let r = bajar(&sala, &conn, &hh, tamano, &parte, &destino).await;
            if r.is_err() {
                let _ = tokio::fs::remove_file(&parte).await;
            }
            sala.blobs.lock().unwrap().descargas.remove(&hh);
            sala.evento(match r {
                Ok(()) => json!({"tipo":"blob-fin","huella":hh,"ok":true}),
                Err(e) => json!({"tipo":"blob-fin","huella":hh,"ok":false,"error":e}),
            });
        });
        b.descargas.insert(h, tarea.abort_handle());
        Ok(Value::Null)
    }
}

async fn bajar(
    sala: &Sala,
    conn: &Connection,
    h: &str,
    tamano: u64,
    parte: &Path,
    destino: &Path,
) -> Result<(), &'static str> {
    let (mut envio, mut recibo) = conn.open_bi().await.map_err(|_| "red")?;
    envio
        .write_all(&marco::armar(&json!({"huella": h, "tamano": tamano})))
        .await
        .map_err(|_| "red")?;
    let _ = envio.finish();
    let mut f = tokio::fs::File::create(parte).await.map_err(|_| "disco")?;
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
            sala.evento(json!({"tipo":"blob-avance","huella":h,"recibidos":llevo}));
        }
    }
    f.flush().await.map_err(|_| "disco")?;
    f.sync_all().await.map_err(|_| "disco")?;
    drop(f);
    if llevo != tamano || hex::codificar(&hash.finalize()) != h {
        return Err("huella");
    }
    tokio::fs::rename(parte, destino).await.map_err(|_| "disco")
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
    let pedido =
        match tokio::time::timeout(Duration::from_secs(10), marco::leer(&mut recibo, 4096)).await {
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
    // Cupos: 4 por par, 16 en total.
    {
        let mut b = sala.blobs.lock().unwrap();
        let n = b.sirviendo.entry(quien).or_default();
        if *n >= POR_PAR || SIRVIENDO_TOTAL.load(Ordering::SeqCst) >= TOTAL {
            drop(b);
            let _ = envio.reset(VarInt::from_u32(corte::OCUPADO));
            return;
        }
        *n += 1;
        SIRVIENDO_TOTAL.fetch_add(1, Ordering::SeqCst);
    }
    let codigo = enviar_archivo(&mut envio, &ruta, tamano).await;
    {
        let mut b = sala.blobs.lock().unwrap();
        if let Some(n) = b.sirviendo.get_mut(&quien) {
            *n -= 1;
            if *n == 0 {
                b.sirviendo.remove(&quien);
            }
        }
        SIRVIENDO_TOTAL.fetch_sub(1, Ordering::SeqCst);
    }
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

    #[test]
    fn rutas_fuera_de_la_carpeta_no_valen() {
        let base = std::env::temp_dir().join(format!("sala-blobs-{}", std::process::id()));
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
