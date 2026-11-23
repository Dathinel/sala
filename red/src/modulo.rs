//! Lado del módulo del protocolo de módulos v1 (Rodaje/docs/protocolo-modulos.md): saludo, invoke/result/error,
//! cancel, ping y shutdown, con hasta 4 llamadas a la vez.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use serde_json::{Value, json};
use std::sync::Mutex;
use tokio::io::{AsyncRead, AsyncWrite, AsyncWriteExt};
use tokio::sync::{Notify, mpsc};

use crate::marco::{self, ErrorMarco};

pub const MAX_LLAMADAS: usize = 4;

/// Error de una llamada: código del protocolo y mensaje para una persona.
#[derive(Debug)]
pub struct Fallo(pub &'static str, pub String);

/// Qué módulo es: va en el `welcome`. Cada función es (id, nombre, larga y cancelable).
pub struct Info {
    pub id: &'static str,
    pub nombre: &'static str,
    pub version: &'static str,
    pub funciones: &'static [(&'static str, &'static str, bool)],
}

/// Lo que hace el módulo: atender llamadas y cerrar todo al terminar la conexión.
pub trait Servicio: Send + Sync + 'static {
    fn llamar(
        &self,
        funcion: String,
        parametros: Value,
        cancelar: Arc<Notify>,
    ) -> impl Future<Output = Result<Value, Fallo>> + Send;
    fn cerrar(&self) -> impl Future<Output = ()> + Send;
}

pub const FUNCIONES_SALA: &[(&str, &str, bool)] = &[
    ("abrir-sala", "Abrir una sala como anfitrión", false),
    ("billete", "Crear un billete de invitación", false),
    ("revocar", "Invalidar billetes", false),
    ("aceptar", "Aceptar a quien pide entrar", false),
    ("rechazar", "Rechazar a quien pide entrar", false),
    ("expulsar", "Expulsar a un miembro", false),
    ("unirse", "Entrar en una sala", true),
    ("enviar", "Mandar un mensaje de sala", false),
    ("recibir", "Esperar lo que llega de la red", true),
    ("estado", "Estado de la sala", false),
    ("ascender", "Pasar de invitado a anfitrión", false),
    ("blob-ofrecer", "Ofrecer blobs por huella", false),
    ("blob-retirar", "Retirar blobs ofrecidos", false),
    ("blob-pedir", "Bajar un blob de un par", false),
    ("blob-cancelar", "Cortar una descarga", false),
    ("cerrar", "Cerrar la sala", false),
];

pub fn es_identificador(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 64
        && s.bytes()
            .all(|c| matches!(c, b'a'..=b'z' | b'0'..=b'9' | b'.' | b'-'))
        && !s.starts_with(['.', '-'])
        && !s.ends_with(['.', '-'])
        && !s.contains("..")
        && !s.contains("--")
        && !s.contains(".-")
        && !s.contains("-.")
}

/// Atiende la conexión con la app hasta `shutdown` o el cierre. Devuelve el código de salida (0 bien, 2 protocolo).
pub async fn atender<S: AsyncRead + AsyncWrite + Send + 'static, M: Servicio>(
    flujo: S,
    info: &'static Info,
    servicio: Arc<M>,
) -> i32 {
    let (mut lector, mut escritor) = tokio::io::split(flujo);
    let (tx, mut rx) = mpsc::unbounded_channel::<Value>();
    let escritura = tokio::spawn(async move {
        while let Some(m) = rx.recv().await {
            if escritor.write_all(&marco::armar(&m)).await.is_err()
                || escritor.flush().await.is_err()
            {
                break;
            }
        }
    });

    let codigo = bucle(&mut lector, &tx, info, &servicio).await;
    servicio.cerrar().await;
    drop(tx);
    let _ = tokio::time::timeout(Duration::from_secs(2), escritura).await;
    codigo
}

async fn bucle<R: AsyncRead + Unpin, M: Servicio>(
    lector: &mut R,
    tx: &mpsc::UnboundedSender<Value>,
    info: &'static Info,
    servicio: &Arc<M>,
) -> i32 {
    let hello = match tokio::time::timeout(
        Duration::from_secs(30),
        marco::leer(lector, marco::MAX_LOCAL, marco::MAX_PROFUNDIDAD),
    )
    .await
    {
        Ok(Ok(v)) => v,
        _ => return 2,
    };
    if marco::texto(&hello, "type") != Some("hello") {
        let _ = tx.send(json!({"type":"error","code":"malformed","message":"Lo primero tiene que ser «hello»."}));
        return 2;
    }
    let min = hello
        .get("minProtocol")
        .and_then(Value::as_i64)
        .unwrap_or(0);
    let max = hello
        .get("maxProtocol")
        .and_then(Value::as_i64)
        .unwrap_or(0);
    if min < 1 || max < min || min > 1 {
        let _ = tx.send(json!({"type":"error","code":"version-incompatible",
            "message": format!("{} habla el protocolo 1–1 y la app {min}–{max}.", info.id)}));
        return 2;
    }
    let funciones: Vec<Value> = info
        .funciones
        .iter()
        .map(|(id, nombre, larga)| json!({"id":id,"name":nombre,"cancellable":larga,"reportsProgress":false}))
        .collect();
    let _ = tx.send(json!({"type":"welcome","protocol":1,"module":{
        "id":info.id,"version":info.version,"name":info.nombre,"functions":funciones,"device":"cpu"}}));

    let activas: Arc<Mutex<HashMap<i64, Arc<Notify>>>> = Arc::default();
    loop {
        let m = match marco::leer(lector, marco::MAX_LOCAL, marco::MAX_PROFUNDIDAD).await {
            Ok(m) => m,
            Err(ErrorMarco::Fin) => return 0,
            Err(_) => return 2,
        };
        let tipo = marco::texto(&m, "type").unwrap_or("");
        match tipo {
            "invoke" => {
                let (Some(id), Some(funcion)) = (
                    m.get("id").and_then(Value::as_i64),
                    marco::texto(&m, "function"),
                ) else {
                    return malformado(tx, "invoke sin id o sin función");
                };
                if id <= 0 || !es_identificador(funcion) {
                    return malformado(tx, "id de llamada o función inválidos");
                }
                let mut a = activas.lock().unwrap();
                if a.contains_key(&id) {
                    return malformado(tx, "id de llamada repetido");
                }
                if a.len() >= MAX_LLAMADAS {
                    let _ = tx.send(json!({"type":"error","id":id,"code":"busy","message":format!("{} está ocupado.", info.id)}));
                    continue;
                }
                let cancelar = Arc::new(Notify::new());
                a.insert(id, cancelar.clone());
                drop(a);
                let (tx, activas, funcion) = (tx.clone(), activas.clone(), funcion.to_string());
                let parametros = m.get("parameters").cloned().unwrap_or(Value::Null);
                let servicio = servicio.clone();
                tokio::spawn(async move {
                    let respuesta = match servicio.llamar(funcion, parametros, cancelar).await {
                        Ok(v) => json!({"type":"result","id":id,"value":v}),
                        Err(Fallo(code, msg)) => {
                            json!({"type":"error","id":id,"code":code,"message":msg})
                        }
                    };
                    activas.lock().unwrap().remove(&id);
                    let _ = tx.send(respuesta);
                });
            }
            "cancel" => {
                if let Some(c) = m
                    .get("id")
                    .and_then(Value::as_i64)
                    .and_then(|id| activas.lock().unwrap().get(&id).cloned())
                {
                    c.notify_one();
                }
            }
            "ping" => {
                let _ =
                    tx.send(json!({"type":"pong","id":m.get("id").cloned().unwrap_or(json!(0))}));
            }
            "shutdown" => return 0,
            t if es_identificador(t)
                && !matches!(
                    t,
                    "hello" | "welcome" | "result" | "error" | "progress" | "pong" | "log"
                ) => {}
            _ => return malformado(tx, "mensaje que la app no manda"),
        }
    }
}

const ARG_TUBERIA: &str = "--rodaje-pipe";

/// Si la app muere de golpe, el proceso no se queda vivo (en Windows lo hace el Job Object de la app).
pub fn vigilar_padre() {
    #[cfg(unix)]
    {
        let padre = std::os::unix::process::parent_id();
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_millis(250)).await;
                if std::os::unix::process::parent_id() != padre {
                    std::process::exit(0);
                }
            }
        });
    }
}

/// Conecta con la app por `--rodaje-pipe <nombre o ruta>` y la atiende. Devuelve el código de salida.
pub async fn ejecutar<M: Servicio>(args: &[String], info: &'static Info, servicio: Arc<M>) -> i32 {
    let Some(nombre) = args
        .iter()
        .position(|a| a == ARG_TUBERIA)
        .and_then(|i| args.get(i + 1))
    else {
        eprintln!(
            "Este programa es el módulo {} ({}); lo abre la app.",
            info.id, info.nombre
        );
        return 64;
    };
    match conectar(nombre, info, servicio).await {
        Ok(c) => c,
        Err(e) => {
            eprintln!("No se pudo conectar con la app: {e}");
            2
        }
    }
}

#[cfg(windows)]
async fn conectar<M: Servicio>(
    nombre: &str,
    info: &'static Info,
    servicio: Arc<M>,
) -> std::io::Result<i32> {
    use tokio::net::windows::named_pipe::ClientOptions;
    let valido = (1..=128).contains(&nombre.len())
        && nombre
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || b"._-".contains(&c));
    if !valido {
        return Err(std::io::Error::other("nombre de tubería inválido"));
    }
    let ruta = format!(r"\\.\pipe\{nombre}");
    let limite = tokio::time::Instant::now() + Duration::from_secs(10);
    let tuberia = loop {
        match ClientOptions::new().open(&ruta) {
            Ok(t) => break t,
            Err(e) if tokio::time::Instant::now() < limite && e.raw_os_error() == Some(231) => {
                // ERROR_PIPE_BUSY
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(e) => return Err(e),
        }
    };
    Ok(atender(tuberia, info, servicio).await)
}

#[cfg(unix)]
async fn conectar<M: Servicio>(
    ruta: &str,
    info: &'static Info,
    servicio: Arc<M>,
) -> std::io::Result<i32> {
    if !ruta.starts_with('/') || ruta.contains("/../") {
        return Err(std::io::Error::other("ruta de socket inválida"));
    }
    let socket = tokio::time::timeout(
        Duration::from_secs(10),
        tokio::net::UnixStream::connect(ruta),
    )
    .await??;
    Ok(atender(socket, info, servicio).await)
}

fn malformado(tx: &mpsc::UnboundedSender<Value>, porque: &str) -> i32 {
    let _ = tx.send(
        json!({"type":"error","code":"malformed","message":format!("Mensaje inválido: {porque}.")}),
    );
    2
}

#[cfg(test)]
mod pruebas {
    use super::*;

    #[test]
    fn identificadores() {
        for s in ["eco", "abrir-sala", "v2", "a.b"] {
            assert!(es_identificador(s), "{s}");
        }
        for s in ["", "Eco", "-a", "a-", "a..b", "../x", "a b"] {
            assert!(!es_identificador(s), "{s}");
        }
    }
}
