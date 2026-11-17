//! La sala del lado del transporte (api.md §2 y §3): endpoint de iroh, billetes, quién entra, canal ordenado por par
//! y la cola de eventos que la app lee con `recibir`. No sabe de roles, orden ni reservas: eso es de Dathinel.Sala.

use std::collections::{HashMap, HashSet, VecDeque};
use std::net::{IpAddr, SocketAddr};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use iroh::endpoint::{Connection, IncomingAddr, RecvStream, SendStream, VarInt, presets};
use iroh::{Endpoint, EndpointAddr, EndpointId, RelayMode, RelayUrl};
use rand::RngCore;
use serde_json::{Value, json};
use tokio::sync::{Notify, mpsc, oneshot};

use crate::billete::{self, Billete};
use crate::hex;
use crate::marco::{self, ErrorMarco};

pub const ALPN: &[u8] = b"dathinel/sala/1";
const MAX_HOLA: usize = 4096;
const MAX_CONEXIONES: usize = 16;
const MAX_PENDIENTES: usize = 4;
const MAX_FALSOS_POR_MINUTO: usize = 20;
const COLA_POR_PAR: usize = 4 << 20;
const LOTE_RECIBIR: usize = 1000 * 1024;
const MAX_EVENTOS_BYTES: usize = 64 << 20;
const ESPERA_RECIBIR: Duration = Duration::from_secs(25);
const ESPERA_HOLA: Duration = Duration::from_secs(10);
const ESPERA_APROBACION: Duration = Duration::from_secs(600);
const RITMO_MEDIO: f64 = 100.0;
const RITMO_RAFAGA: f64 = 400.0;

/// Códigos de cierre de conexión QUIC (los ve el otro lado como motivo de `sale` o `cerrada`).
pub(crate) mod cierre {
    pub const CERRO: u32 = 1;
    pub const EXPULSADO: u32 = 2;
    pub const REVOCADO: u32 = 3;
    pub const RITMO: u32 = 4;
    pub const MARCO: u32 = 5;
    pub const REEMPLAZADA: u32 = 6;
    pub const SALUDO: u32 = 7;
    pub const TRASPASO: u32 = 8;
}

fn motivo(codigo: u64) -> &'static str {
    match codigo as u32 {
        cierre::EXPULSADO => "expulsado",
        cierre::REVOCADO => "revocado",
        cierre::RITMO => "ritmo",
        cierre::MARCO => "marco",
        cierre::REEMPLAZADA => "reemplazada",
        cierre::TRASPASO => "traspaso",
        _ => "cerro",
    }
}

/// Error de una llamada: código del protocolo y mensaje para una persona.
#[derive(Debug)]
pub struct Fallo(pub &'static str, pub String);

fn fallo<T>(code: &'static str, msg: impl Into<String>) -> Result<T, Fallo> {
    Err(Fallo(code, msg.into()))
}

#[derive(PartialEq, Clone, Copy)]
enum Uso {
    Libre,
    EnTramite,
    Gastado,
}

struct BilleteVivo {
    secreto: [u8; 32],
    caduca: u64,
    un_uso: bool,
    uso: Uso,
}

pub(crate) struct Par {
    pub(crate) conn: Connection,
    tx: mpsc::UnboundedSender<Vec<u8>>,
    cola: Arc<AtomicUsize>,
    callada: Arc<AtomicBool>,
}

#[derive(Default)]
pub(crate) struct Estado {
    anclados: HashSet<EndpointId>,
    vetados: HashSet<EndpointId>,
    billetes: HashMap<String, BilleteVivo>,
    pendientes: HashMap<EndpointId, (oneshot::Sender<bool>, String)>,
    pub(crate) pares: HashMap<EndpointId, Par>,
    falsos: HashMap<String, VecDeque<Instant>>,
}

pub struct Sala {
    pub(crate) ep: Endpoint,
    pub(crate) anfitrion: AtomicBool,
    app: String,
    app_version: String,
    rele: Option<String>,
    pub(crate) carpeta: PathBuf,
    pub(crate) estado: Mutex<Estado>,
    pub(crate) blobs: Mutex<crate::blobs::Blobs>,
    eventos: Mutex<VecDeque<Value>>,
    hay_eventos: Notify,
    /// Bytes de mensajes en `eventos` que la app aún no leyó.
    pendiente: AtomicUsize,
}

static SALA: OnceLock<Mutex<Option<Arc<Sala>>>> = OnceLock::new();
static ABRIENDO: AtomicBool = AtomicBool::new(false);

fn ranura() -> &'static Mutex<Option<Arc<Sala>>> {
    SALA.get_or_init(Mutex::default)
}

fn actual() -> Result<Arc<Sala>, Fallo> {
    ranura()
        .lock()
        .unwrap()
        .clone()
        .ok_or(Fallo("no-abierta", "No hay ninguna sala abierta.".into()))
}

pub async fn cerrar_todo() {
    let s = ranura().lock().unwrap().take();
    if let Some(s) = s {
        s.ep.close().await;
    }
}

fn ahora() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

pub async fn llamar(funcion: &str, p: Value, cancelar: Arc<Notify>) -> Result<Value, Fallo> {
    match funcion {
        "abrir-sala" => abrir(p, true, cancelar).await,
        "unirse" => abrir(p, false, cancelar).await,
        "billete" => actual()?.billete(&p),
        "revocar" => actual()?.revocar(&p),
        "aceptar" => actual()?.decidir(&p, true),
        "rechazar" => actual()?.decidir(&p, false),
        "expulsar" => actual()?.expulsar(&p),
        "enviar" => actual()?.enviar(&p),
        "recibir" => actual()?.recibir(&p, cancelar).await,
        "estado" => Ok(actual()?.estado()),
        "ascender" => actual()?.ascender(&p),
        "blob-ofrecer" => actual()?.blob_ofrecer(&p),
        "blob-retirar" => actual()?.blob_retirar(&p),
        "blob-pedir" => actual()?.blob_pedir(&p),
        "blob-cancelar" => actual()?.blob_cancelar(&p),
        "cerrar" => {
            cerrar_todo().await;
            Ok(Value::Null)
        }
        _ => fallo(
            "unknown-function",
            format!("dathinel-red no tiene la función «{funcion}»."),
        ),
    }
}

fn param<'a>(p: &'a Value, campo: &str) -> Result<&'a str, Fallo> {
    marco::texto(p, campo).ok_or_else(|| Fallo("invalid-parameters", format!("Falta «{campo}».")))
}

fn param_clave(p: &Value, campo: &str) -> Result<EndpointId, Fallo> {
    hex::clave(param(p, campo)?)
        .ok_or_else(|| Fallo("invalid-parameters", format!("«{campo}» no es una clave.")))
}

fn texto_corto(p: &Value, campo: &str) -> Result<String, Fallo> {
    let t = param(p, campo)?;
    if t.is_empty() || t.chars().count() > 64 || t.chars().any(char::is_control) {
        return fallo(
            "invalid-parameters",
            format!("«{campo}» vacío, largo o con caracteres de control."),
        );
    }
    Ok(t.to_string())
}

/// `abrir-sala` (anfitrión) o `unirse` (invitado): crea el endpoint con la red pedida.
async fn abrir(p: Value, anfitrion: bool, cancelar: Arc<Notify>) -> Result<Value, Fallo> {
    let existente = ranura().lock().unwrap().clone();
    if let Some(sala) = existente.filter(|s| !anfitrion && !s.es_anfitrion()) {
        if ABRIENDO.swap(true, Ordering::SeqCst) {
            return fallo("ocupado", "Ya se está conectando.");
        }
        sala.callar_pares(cierre::REEMPLAZADA);
        let alias = texto_corto(&p, "alias");
        let r = match alias {
            Ok(alias) => tokio::select! {
                r = sala.clone().entrar(&p, alias) => r,
                _ = cancelar.notified() => fallo("cancelled", "Cancelado."),
            },
            Err(e) => Err(e),
        };
        ABRIENDO.store(false, Ordering::SeqCst);
        return r;
    }
    if ranura().lock().unwrap().is_some() || ABRIENDO.swap(true, Ordering::SeqCst) {
        return fallo("ya-abierta", "Ya hay una sala abierta en este proceso.");
    }
    let r = abrir_una(p, anfitrion, cancelar).await;
    ABRIENDO.store(false, Ordering::SeqCst);
    r
}

async fn abrir_una(p: Value, anfitrion: bool, cancelar: Arc<Notify>) -> Result<Value, Fallo> {
    let secreta = hex::secreta(param(&p, "identidad")?).ok_or(Fallo(
        "invalid-parameters",
        "«identidad» no es una clave privada.".into(),
    ))?;
    let app = texto_corto(&p, "app")?;
    let app_version = texto_corto(&p, "appVersion")?;
    let carpeta = PathBuf::from(param(&p, "carpeta")?);
    if !carpeta.is_absolute() {
        return fallo(
            "invalid-parameters",
            "«carpeta» tiene que ser una ruta absoluta.",
        );
    }
    let red = p.get("red").cloned().unwrap_or(json!({}));
    let mdns = red.get("mdns").and_then(Value::as_bool).unwrap_or(false);
    let rele = marco::texto(&red, "rele").unwrap_or("n0").to_string();
    let mut b = match rele.as_str() {
        "n0" => Endpoint::builder(presets::N0),
        "ninguno" => Endpoint::builder(presets::Minimal).relay_mode(RelayMode::Disabled),
        url if url.starts_with("https://") => {
            let u: RelayUrl = url
                .parse()
                .map_err(|_| Fallo("invalid-parameters", "URL de relé inválida.".into()))?;
            Endpoint::builder(presets::Minimal).relay_mode(RelayMode::Custom(u.into()))
        }
        _ => {
            return fallo(
                "invalid-parameters",
                "«rele» tiene que ser n0, ninguno o una URL https.",
            );
        }
    };
    if red
        .get("soloRele")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        if rele == "ninguno" {
            return fallo("invalid-parameters", "«soloRele» necesita un relé.");
        }
        b = b.clear_ip_transports();
    }
    if mdns {
        b = b.address_lookup(
            iroh_mdns_address_lookup::MdnsAddressLookup::builder().service_name("dathinel-sala"),
        );
    }
    b = b.secret_key(secreta);
    b = b.alpns(vec![ALPN.to_vec()]);
    let ep = b
        .bind()
        .await
        .map_err(|e| Fallo("sin-conexion", format!("No se pudo abrir la red: {e}")))?;
    let sala = Arc::new(Sala {
        ep,
        anfitrion: AtomicBool::new(anfitrion),
        app,
        app_version,
        rele: (rele != "ninguno").then_some(rele.clone()),
        carpeta,
        estado: Mutex::default(),
        eventos: Mutex::default(),
        hay_eventos: Notify::new(),
        pendiente: AtomicUsize::new(0),
        blobs: Mutex::default(),
    });

    if anfitrion {
        let anclados = p
            .get("anclados")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        {
            let mut e = sala.estado.lock().unwrap();
            for a in &anclados {
                let k = a.as_str().and_then(hex::clave).ok_or(Fallo(
                    "invalid-parameters",
                    "Clave anclada inválida.".into(),
                ))?;
                e.anclados.insert(k);
            }
        }
        *ranura().lock().unwrap() = Some(sala.clone());
        tokio::spawn(sala.clone().aceptar_conexiones());
        Ok(json!({"clave": hex::de_clave(&sala.ep.id()), "direcciones": sala.direcciones()}))
    } else {
        let alias = texto_corto(&p, "alias")?;
        let r = tokio::select! {
            r = sala.clone().entrar(&p, alias) => r,
            _ = cancelar.notified() => fallo("cancelled", "Cancelado."),
        };
        match r {
            Ok(v) => {
                *ranura().lock().unwrap() = Some(sala);
                Ok(v)
            }
            Err(e) => {
                sala.ep.close().await;
                Err(e)
            }
        }
    }
}

impl Sala {
    fn direcciones(&self) -> Vec<String> {
        let mut d: Vec<String> = self.ep.addr().ip_addrs().map(|a| a.to_string()).collect();
        // Para dos procesos del mismo equipo (pruebas y dos instancias en un PC).
        for s in self.ep.bound_sockets() {
            let lo = if s.is_ipv4() {
                SocketAddr::new(IpAddr::from([127, 0, 0, 1]), s.port())
            } else {
                continue;
            };
            if !d.contains(&lo.to_string()) {
                d.push(lo.to_string());
            }
        }
        d
    }

    pub(crate) fn es_anfitrion(&self) -> bool {
        self.anfitrion.load(Ordering::SeqCst)
    }

    pub(crate) fn evento(&self, v: Value) {
        self.eventos.lock().unwrap().push_back(v);
        self.hay_eventos.notify_one();
    }

    fn billete(&self, p: &Value) -> Result<Value, Fallo> {
        self.solo_anfitrion()?;
        let caduca_en = p.get("caducaEn").and_then(Value::as_u64).unwrap_or(86_400);
        if !(5..=604_800).contains(&caduca_en) {
            return fallo("invalid-parameters", "«caducaEn» va de 5 s a 7 días.");
        }
        let mut secreto = [0u8; 32];
        rand::rng().fill_bytes(&mut secreto);
        let mut idb = [0u8; 8];
        rand::rng().fill_bytes(&mut idb);
        let id = hex::codificar(&idb);
        let caduca = ahora() + caduca_en;
        self.estado.lock().unwrap().billetes.insert(
            id.clone(),
            BilleteVivo {
                secreto,
                caduca,
                un_uso: p.get("unUso").and_then(Value::as_bool).unwrap_or(true),
                uso: Uso::Libre,
            },
        );
        let b = Billete {
            app: self.app.clone(),
            anfitrion: hex::de_clave(&self.ep.id()),
            rele: self
                .rele
                .as_ref()
                .filter(|r| r.starts_with("https://"))
                .cloned(),
            direcciones: self.direcciones(),
            id: id.clone(),
            secreto: hex::codificar(&secreto),
            caduca,
        };
        Ok(json!({"billete": b.codificar(), "id": id}))
    }

    fn revocar(&self, p: &Value) -> Result<Value, Fallo> {
        self.solo_anfitrion()?;
        let mut e = self.estado.lock().unwrap();
        match marco::texto(p, "id") {
            Some(id) => {
                e.billetes.remove(id);
            }
            None => e.billetes.clear(),
        }
        // Quien esperaba aprobación con un billete revocado ya no entra.
        let caidos: Vec<EndpointId> = e
            .pendientes
            .iter()
            .filter(|(_, (_, b))| !e.billetes.contains_key(b))
            .map(|(k, _)| *k)
            .collect();
        for k in caidos {
            if let Some((tx, _)) = e.pendientes.remove(&k) {
                let _ = tx.send(false);
            }
        }
        Ok(Value::Null)
    }

    fn decidir(&self, p: &Value, si: bool) -> Result<Value, Fallo> {
        self.solo_anfitrion()?;
        let k = param_clave(p, "clave")?;
        let tx = self.estado.lock().unwrap().pendientes.remove(&k);
        match tx {
            Some((tx, _)) => {
                let _ = tx.send(si);
                Ok(Value::Null)
            }
            None => fallo("desconocido", "Esa clave no está esperando aprobación."),
        }
    }

    fn expulsar(&self, p: &Value) -> Result<Value, Fallo> {
        self.solo_anfitrion()?;
        let k = param_clave(p, "clave")?;
        let mut e = self.estado.lock().unwrap();
        if p.get("vetar").and_then(Value::as_bool).unwrap_or(true) {
            e.anclados.remove(&k);
            e.vetados.insert(k);
        }
        match e.pares.remove(&k) {
            Some(par) => {
                par.conn
                    .close(VarInt::from_u32(cierre::EXPULSADO), b"expulsado");
                Ok(Value::Null)
            }
            None => Ok(Value::Null),
        }
    }

    fn enviar(&self, p: &Value) -> Result<Value, Fallo> {
        let datos = p.get("datos").filter(|d| d.is_object()).ok_or(Fallo(
            "invalid-parameters",
            "«datos» tiene que ser un objeto.".into(),
        ))?;
        let marco = marco::armar(datos);
        if marco.len() - 4 > marco::MAX_PAR {
            return fallo(
                "demasiado-grande",
                "El mensaje pasa de 960 KiB: va como blob.",
            );
        }
        let e = self.estado.lock().unwrap();
        let destinos: Vec<EndpointId> = match p.get("a").and_then(Value::as_array) {
            Some(a) if !a.is_empty() => {
                let mut v = Vec::new();
                for k in a {
                    let k = k.as_str().and_then(hex::clave).ok_or(Fallo(
                        "invalid-parameters",
                        "Clave de destino inválida.".into(),
                    ))?;
                    if !e.pares.contains_key(&k) {
                        return fallo("desconocido", "Ese destino no está conectado.");
                    }
                    v.push(k);
                }
                v
            }
            _ => e.pares.keys().copied().collect(),
        };
        let difusion = !p
            .get("a")
            .and_then(Value::as_array)
            .is_some_and(|a| !a.is_empty());
        let llena =
            |k: &EndpointId| e.pares[k].cola.load(Ordering::Relaxed) + marco.len() > COLA_POR_PAR;
        // A destinos concretos, todo o nada; en una difusión se salta a quien va atrasado (lo repone al sincronizar).
        if !difusion && destinos.iter().any(llena) {
            return fallo("ocupado-red", "La cola de salida está llena; reintenta.");
        }
        let (llenos, listos): (Vec<EndpointId>, Vec<EndpointId>) =
            destinos.into_iter().partition(llena);
        for k in &listos {
            let par = &e.pares[k];
            par.cola.fetch_add(marco.len(), Ordering::Relaxed);
            let _ = par.tx.send(marco.clone());
        }
        let llenos: Vec<String> = llenos.iter().map(hex::de_clave).collect();
        Ok(json!({"encolados": listos.len(), "llenos": llenos}))
    }

    async fn recibir(&self, p: &Value, cancelar: Arc<Notify>) -> Result<Value, Fallo> {
        let max = p
            .get("max")
            .and_then(Value::as_u64)
            .unwrap_or(256)
            .clamp(1, 1024) as usize;
        let limite = tokio::time::Instant::now() + ESPERA_RECIBIR;
        loop {
            {
                let mut cola = self.eventos.lock().unwrap();
                let mut lote = Vec::new();
                let mut bytes = 0;
                while lote.len() < max {
                    let Some(ev) = cola.front() else { break };
                    let n = serde_json::to_vec(ev).map(|v| v.len()).unwrap_or(0) + 1;
                    if !lote.is_empty() && bytes + n > LOTE_RECIBIR {
                        break;
                    }
                    bytes += n;
                    let mut ev = cola.pop_front().unwrap();
                    if let Some(m) = ev.as_object_mut().and_then(|o| o.remove("_n")) {
                        self.pendiente
                            .fetch_sub(m.as_u64().unwrap_or(0) as usize, Ordering::Relaxed);
                    }
                    lote.push(ev);
                }
                if !lote.is_empty() {
                    if !cola.is_empty() {
                        self.hay_eventos.notify_one();
                    }
                    return Ok(json!({ "eventos": lote }));
                }
            }
            tokio::select! {
                _ = self.hay_eventos.notified() => {}
                _ = tokio::time::sleep_until(limite) => return Ok(json!({"eventos": []})),
                _ = cancelar.notified() => return fallo("cancelled", "Cancelado."),
            }
        }
    }

    fn estado(&self) -> Value {
        let e = self.estado.lock().unwrap();
        let pares: Vec<Value> = e
            .pares
            .iter()
            .map(|(k, par)| {
                let (via, rtt) = via(&par.conn);
                json!({"clave": hex::de_clave(k), "via": via, "rttMs": rtt})
            })
            .collect();
        json!({"clave": hex::de_clave(&self.ep.id()), "direcciones": self.direcciones(), "rol": if self.es_anfitrion() {"anfitrion"} else {"invitado"}, "pares": pares})
    }

    /// El invitado pasa a anfitrión con el mismo endpoint (mismas direcciones): traspaso o suplente (api.md §5.6).
    fn ascender(self: &Arc<Self>, p: &Value) -> Result<Value, Fallo> {
        if self.es_anfitrion() {
            return fallo("invalid-parameters", "Ya es el anfitrión.");
        }
        let mut anclados = HashSet::new();
        for a in p
            .get("anclados")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default()
        {
            anclados.insert(a.as_str().and_then(hex::clave).ok_or(Fallo(
                "invalid-parameters",
                "Clave anclada inválida.".into(),
            ))?);
        }
        self.estado.lock().unwrap().anclados = anclados;
        self.callar_pares(cierre::TRASPASO);
        self.anfitrion.store(true, Ordering::SeqCst);
        tokio::spawn(self.clone().aceptar_conexiones());
        Ok(json!({"clave": hex::de_clave(&self.ep.id()), "direcciones": self.direcciones()}))
    }

    /// Cierra todas las conexiones sin avisar a la app (ella pidió el cambio) con `codigo` para el otro lado.
    fn callar_pares(&self, codigo: u32) {
        let viejos: Vec<Par> = self.estado.lock().unwrap().pares.drain().map(|(_, par)| par).collect();
        for par in viejos {
            par.callada.store(true, Ordering::SeqCst);
            par.conn.close(VarInt::from_u32(codigo), b"");
        }
    }

    fn solo_anfitrion(&self) -> Result<(), Fallo> {
        if self.es_anfitrion() {
            Ok(())
        } else {
            fallo("invalid-parameters", "Solo el anfitrión puede hacerlo.")
        }
    }

    // ---------- anfitrión: entrada de conexiones ----------

    async fn aceptar_conexiones(self: Arc<Self>) {
        while let Some(incoming) = self.ep.accept().await {
            let origen = match incoming.remote_addr() {
                IncomingAddr::Ip(a) => a.ip().to_string(),
                _ => "rele".to_string(),
            };
            if self.demasiados_falsos(&origen, false) {
                incoming.refuse();
                continue;
            }
            let sala = self.clone();
            tokio::spawn(async move {
                let Ok(acc) = incoming.accept() else { return };
                let Ok(conn) = acc.await else { return };
                sala.saludo_entrante(conn, origen).await;
            });
        }
    }

    /// Cuenta (si `anotar`) y consulta los billetes falsos por origen en el último minuto.
    fn demasiados_falsos(&self, origen: &str, anotar: bool) -> bool {
        // Por relé no hay IP: contarlos juntos dejaría a un atacante cerrar la puerta a todos los que llegan por relé.
        if origen == "rele" {
            return false;
        }
        let mut e = self.estado.lock().unwrap();
        let hace_un_minuto = Instant::now() - Duration::from_secs(60);
        let q = e.falsos.entry(origen.to_string()).or_default();
        while q.front().is_some_and(|t| *t < hace_un_minuto) {
            q.pop_front();
        }
        if anotar {
            q.push_back(Instant::now());
        }
        let n = q.len();
        if n == 0 {
            e.falsos.remove(origen);
        }
        n >= MAX_FALSOS_POR_MINUTO
    }

    async fn saludo_entrante(self: Arc<Self>, conn: Connection, origen: String) {
        let quien = conn.remote_id();
        let adios = |motivo: &str, detalle: String| json!({"type":"adios","motivo":motivo,"detalle":detalle});
        let Ok(Ok((mut envio, mut recibo))) =
            tokio::time::timeout(ESPERA_HOLA, conn.accept_bi()).await
        else {
            conn.close(VarInt::from_u32(cierre::SALUDO), b"sin saludo");
            return;
        };
        let hola = match tokio::time::timeout(ESPERA_HOLA, marco::leer(&mut recibo, MAX_HOLA)).await
        {
            Ok(Ok(h)) if marco::texto(&h, "type") == Some("hola") => h,
            _ => {
                conn.close(VarInt::from_u32(cierre::SALUDO), b"saludo invalido");
                return;
            }
        };
        let rechazo = if hola.get("sala").and_then(Value::as_i64) != Some(1) {
            Some(adios(
                "version",
                "Esta sala habla la versión 1 del protocolo de sala.".into(),
            ))
        } else if marco::texto(&hola, "app") != Some(self.app.as_str()) {
            Some(adios("version", format!("Esta sala es de {}.", self.app)))
        } else if marco::texto(&hola, "appVersion") != Some(self.app_version.as_str()) {
            Some(adios(
                "version",
                format!("El anfitrión usa la versión {}.", self.app_version),
            ))
        } else if self.estado.lock().unwrap().vetados.contains(&quien) {
            Some(adios("rechazado", "Esta sala no te deja entrar.".into()))
        } else {
            None
        };
        if let Some(r) = rechazo {
            return despedir(conn, &mut envio, &r).await;
        }
        let alias: String = marco::texto(&hola, "alias")
            .unwrap_or("")
            .chars()
            .filter(|c| !c.is_control())
            .take(40)
            .collect();

        let anclado = self.estado.lock().unwrap().anclados.contains(&quien);
        if !anclado {
            let billete_id = match self.comprobar_billete(&hola, quien) {
                Ok(id) => Some(id),
                Err(motivo) => {
                    if motivo == "billete-invalido" {
                        self.demasiados_falsos(&origen, true);
                    }
                    return despedir(conn, &mut envio, &adios(motivo, String::new())).await;
                }
            };
            let (tx, rx) = oneshot::channel();
            self.estado
                .lock()
                .unwrap()
                .pendientes
                .insert(quien, (tx, billete_id.clone().unwrap_or_default()));
            self.evento(
                json!({"tipo":"solicitud","clave":hex::de_clave(&quien),"alias":alias,
                "appVersion":self.app_version,"billete":billete_id}),
            );
            let mut sonda = [0u8; 1];
            let decision = tokio::select! {
                d = rx => d.ok(),
                _ = tokio::time::sleep(ESPERA_APROBACION) => None,
                _ = conn.closed() => None,
                // Mientras espera, el invitado no manda nada: si el canal da algo (cierre, reinicio), se fue.
                _ = recibo.read(&mut sonda) => None,
            };
            let aceptado = {
                let mut e = self.estado.lock().unwrap();
                e.pendientes.remove(&quien);
                let id = billete_id.as_deref().unwrap_or_default();
                if let Some(b) = e.billetes.get_mut(id).filter(|b| b.un_uso) {
                    b.uso = if decision.is_some() {
                        Uso::Gastado
                    } else {
                        Uso::Libre
                    };
                }
                if decision == Some(true) {
                    e.anclados.insert(quien);
                }
                decision
            };
            match aceptado {
                Some(true) => {}
                Some(false) => {
                    return despedir(conn, &mut envio, &adios("rechazado", String::new())).await;
                }
                None => {
                    conn.close(VarInt::from_u32(cierre::CERRO), b"sin respuesta");
                    return;
                }
            }
        }

        let llena = {
            let e = self.estado.lock().unwrap();
            !e.pares.contains_key(&quien) && e.pares.len() >= MAX_CONEXIONES
        };
        if llena {
            return despedir(
                conn,
                &mut envio,
                &adios("llena", "La sala está llena.".into()),
            )
            .await;
        }
        if envio
            .write_all(&marco::armar(&json!({"type":"admitido"})))
            .await
            .is_err()
        {
            return;
        }
        let (via, _) = via(&conn);
        self.evento(json!({"tipo":"entra","clave":hex::de_clave(&quien),"alias":alias,"via":via}));
        self.registrar(conn, envio, recibo);
    }

    /// El billete del `hola`: vivo, con su secreto, no caducado y, si es de un uso, libre. Lo deja en trámite.
    fn comprobar_billete(&self, hola: &Value, quien: EndpointId) -> Result<String, &'static str> {
        let id = marco::texto(hola, "billete")
            .filter(|i| billete::es_id(i))
            .ok_or("billete-invalido")?;
        let secreto = marco::texto(hola, "secreto")
            .and_then(hex::bytes32)
            .ok_or("billete-invalido")?;
        let mut e = self.estado.lock().unwrap();
        if e.pendientes.len() >= MAX_PENDIENTES || e.pendientes.contains_key(&quien) {
            return Err("ocupado");
        }
        let b = e.billetes.get_mut(id).ok_or("billete-invalido")?;
        let igual = b
            .secreto
            .iter()
            .zip(secreto.iter())
            .fold(0u8, |acc, (x, y)| acc | (x ^ y))
            == 0;
        if !igual || b.caduca < ahora() || (b.un_uso && b.uso != Uso::Libre) {
            return Err("billete-invalido");
        }
        if b.un_uso {
            b.uso = Uso::EnTramite;
        }
        Ok(id.to_string())
    }

    // ---------- invitado: entrar ----------

    async fn entrar(self: Arc<Self>, p: &Value, alias: String) -> Result<Value, Fallo> {
        let (destino, billete_id, secreto) = if let Some(t) = marco::texto(p, "billete") {
            let b = Billete::decodificar(t)
                .ok_or(Fallo("billete-invalido", "El enlace no es válido.".into()))?;
            if b.app != self.app {
                return fallo("version", format!("El enlace es de {}.", b.app));
            }
            if b.caduca < ahora() {
                return fallo("billete-invalido", "El enlace caducó.");
            }
            let addr = direccion(
                hex::clave(&b.anfitrion).unwrap(),
                b.rele.as_deref(),
                &b.direcciones,
            )?;
            (addr, Some(b.id), Some(b.secreto))
        } else {
            let k = param_clave(p, "anfitrion")?;
            let dirs: Vec<String> = p
                .get("direcciones")
                .and_then(Value::as_array)
                .map(|a| {
                    a.iter()
                        .filter_map(|d| d.as_str().map(String::from))
                        .collect()
                })
                .unwrap_or_default();
            (
                direccion(k, marco::texto(p, "rele").or(self.rele.as_deref()), &dirs)?,
                None,
                None,
            )
        };
        let anfitrion = destino.id;
        let dirs_anfitrion: Vec<String> = destino.ip_addrs().map(|a| a.to_string()).collect();
        let conn = tokio::time::timeout(Duration::from_secs(10), self.ep.connect(destino, ALPN))
            .await
            .map_err(|_| {
                Fallo(
                    "sin-conexion",
                    "No se encontró al anfitrión en 10 s.".into(),
                )
            })?
            .map_err(|e| {
                Fallo(
                    "sin-conexion",
                    format!("No se pudo conectar con el anfitrión: {e}"),
                )
            })?;
        let (mut envio, mut recibo) = conn
            .open_bi()
            .await
            .map_err(|e| Fallo("sin-conexion", e.to_string()))?;
        let mut hola = json!({"type":"hola","sala":1,"app":self.app,"appVersion":self.app_version,"alias":alias});
        if let (Some(id), Some(s)) = (billete_id, secreto) {
            hola["billete"] = json!(id);
            hola["secreto"] = json!(s);
        }
        envio
            .write_all(&marco::armar(&hola))
            .await
            .map_err(|e| Fallo("sin-conexion", e.to_string()))?;
        let r = tokio::time::timeout(
            ESPERA_APROBACION + ESPERA_HOLA,
            marco::leer(&mut recibo, MAX_HOLA),
        )
        .await
        .map_err(|_| Fallo("rechazado", "El anfitrión no respondió.".into()))?;
        let r = match r {
            Ok(r) => r,
            Err(_) => {
                let m = conn
                    .close_reason()
                    .map(|c| c.to_string())
                    .unwrap_or_default();
                return fallo(
                    "rechazado",
                    format!("El anfitrión cerró sin responder. {m}"),
                );
            }
        };
        match marco::texto(&r, "type") {
            Some("admitido") => {
                let (via, _) = via(&conn);
                self.registrar(conn, envio, recibo);
                Ok(json!({"anfitrion": hex::de_clave(&anfitrion), "via": via,
                    "anfitrionDirecciones": dirs_anfitrion, "direcciones": self.direcciones()}))
            }
            Some("adios") => {
                let code = match marco::texto(&r, "motivo") {
                    Some("version") => "version",
                    Some("billete-invalido") => "billete-invalido",
                    Some("llena") => "llena",
                    Some("ocupado") => "ocupado",
                    _ => "rechazado",
                };
                fallo(
                    code,
                    marco::texto(&r, "detalle")
                        .unwrap_or("El anfitrión no te dejó entrar.")
                        .to_string(),
                )
            }
            _ => fallo("rechazado", "Respuesta inesperada del anfitrión."),
        }
    }

    // ---------- un par admitido: canal ordenado ----------

    fn registrar(
        self: &Arc<Self>,
        conn: Connection,
        mut envio: SendStream,
        mut recibo: RecvStream,
    ) {
        let quien = conn.remote_id();
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let cola = Arc::new(AtomicUsize::new(0));
        let callada = Arc::new(AtomicBool::new(false));
        let viejo = {
            let mut e = self.estado.lock().unwrap();
            if e.vetados.contains(&quien) {
                drop(e);
                conn.close(VarInt::from_u32(cierre::EXPULSADO), b"expulsado");
                return;
            }
            e.pares.insert(
                quien,
                Par {
                    conn: conn.clone(),
                    tx,
                    cola: cola.clone(),
                    callada: callada.clone(),
                },
            )
        };
        if let Some(v) = viejo {
            v.conn
                .close(VarInt::from_u32(cierre::REEMPLAZADA), b"otra conexion");
        }

        tokio::spawn(async move {
            while let Some(m) = rx.recv().await {
                let n = m.len();
                let ok = envio.write_all(&m).await.is_ok();
                cola.fetch_sub(n, Ordering::Relaxed);
                if !ok {
                    break;
                }
            }
        });

        tokio::spawn(crate::blobs::servir(self.clone(), conn.clone()));

        let sala = self.clone();
        tokio::spawn(async move {
            let clave = hex::de_clave(&quien);
            let mut fichas = RITMO_RAFAGA;
            let mut ultimo = Instant::now();
            let cierre_propio = loop {
                match marco::leer(&mut recibo, marco::MAX_PAR).await {
                    Ok(datos) => {
                        let t = Instant::now();
                        fichas = (fichas + t.duration_since(ultimo).as_secs_f64() * RITMO_MEDIO)
                            .min(RITMO_RAFAGA);
                        ultimo = t;
                        // Solo el anfitrión limita: él reparte lo de todos, así que un invitado recibe más de 100/s.
                        if sala.es_anfitrion() && fichas < 1.0 {
                            break Some(cierre::RITMO);
                        }
                        fichas -= 1.0;
                        // Si la app no lee, se deja de leer el canal y QUIC frena al otro lado.
                        while sala.pendiente.load(Ordering::Relaxed) > MAX_EVENTOS_BYTES {
                            tokio::time::sleep(Duration::from_millis(10)).await;
                        }
                        let n = serde_json::to_vec(&datos).map(|v| v.len()).unwrap_or(0);
                        sala.pendiente.fetch_add(n, Ordering::Relaxed);
                        sala.evento(json!({"tipo":"mensaje","de":clave,"datos":datos,"_n":n}));
                    }
                    Err(ErrorMarco::Fin) => break None,
                    Err(ErrorMarco::Largo) | Err(ErrorMarco::Json) => break Some(cierre::MARCO),
                    Err(ErrorMarco::Io) => break None,
                }
            };
            match cierre_propio {
                Some(c) => conn.close(VarInt::from_u32(c), b""),
                // El otro terminó el canal: se cierra ya, sin esperar al plazo de inactividad de QUIC.
                None if conn.close_reason().is_none() => {
                    conn.close(VarInt::from_u32(cierre::CERRO), b"")
                }
                None => {}
            }
            let razon = conn.closed().await;
            let codigo = cierre_propio.map(u64::from).or_else(|| match &razon {
                iroh::endpoint::ConnectionError::ApplicationClosed(a) => {
                    Some(a.error_code.into_inner())
                }
                iroh::endpoint::ConnectionError::LocallyClosed => Some(cierre::CERRO as u64),
                _ => None,
            });
            let mot = codigo.map(motivo).unwrap_or("red");
            let reemplazada = codigo == Some(cierre::REEMPLAZADA as u64);
            {
                let mut e = sala.estado.lock().unwrap();
                if e.pares
                    .get(&quien)
                    .is_some_and(|p| p.conn.stable_id() == conn.stable_id())
                {
                    e.pares.remove(&quien);
                }
            }
            if !reemplazada && !callada.load(Ordering::SeqCst) {
                sala.evento(json!({"tipo":"sale","clave":clave,"motivo":mot}));
                if !sala.es_anfitrion() {
                    let m = if mot == "cerro" {
                        "anfitrion-cerro"
                    } else {
                        mot
                    };
                    sala.evento(json!({"tipo":"cerrada","motivo":m}));
                }
            }
        });
    }
}

async fn despedir(conn: Connection, envio: &mut SendStream, adios: &Value) {
    let _ = envio.write_all(&marco::armar(adios)).await;
    let _ = envio.finish();
    // Que el adiós salga antes de cerrar; si el otro no lo lee, se corta igual.
    let _ = tokio::time::timeout(Duration::from_secs(2), envio.stopped()).await;
    conn.close(VarInt::from_u32(cierre::SALUDO), b"adios");
}

fn direccion(id: EndpointId, rele: Option<&str>, dirs: &[String]) -> Result<EndpointAddr, Fallo> {
    let mut a = EndpointAddr::new(id);
    if let Some(r) = rele.filter(|r| r.starts_with("https://")) {
        a = a.with_relay_url(
            r.parse()
                .map_err(|_| Fallo("billete-invalido", "Relé inválido.".into()))?,
        );
    }
    for d in dirs.iter().take(16) {
        a = a.with_ip_addr(
            d.parse()
                .map_err(|_| Fallo("billete-invalido", "Dirección inválida.".into()))?,
        );
    }
    Ok(a)
}

fn via(conn: &Connection) -> (&'static str, u64) {
    let paths = conn.paths();
    for p in paths.iter() {
        if p.is_selected() {
            return (
                if p.is_relay() { "rele" } else { "directo" },
                p.rtt().as_millis() as u64,
            );
        }
    }
    ("directo", 0)
}
