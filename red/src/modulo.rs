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
use crate::sala::{self, Fallo};

pub const MAX_LLAMADAS: usize = 4;

const FUNCIONES: &[(&str, &str, bool)] = &[
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
pub async fn atender<S: AsyncRead + AsyncWrite + Send + 'static>(flujo: S) -> i32 {
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

    let codigo = bucle(&mut lector, &tx).await;
    sala::cerrar_todo().await;
    drop(tx);
    let _ = tokio::time::timeout(Duration::from_secs(2), escritura).await;
    codigo
}

async fn bucle<R: AsyncRead + Unpin>(lector: &mut R, tx: &mpsc::UnboundedSender<Value>) -> i32 {
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
            "message": format!("dathinel-red habla el protocolo 1–1 y la app {min}–{max}.")}));
        return 2;
    }
    let funciones: Vec<Value> = FUNCIONES
        .iter()
        .map(|(id, nombre, larga)| json!({"id":id,"name":nombre,"cancellable":larga,"reportsProgress":false}))
        .collect();
    let _ = tx.send(json!({"type":"welcome","protocol":1,"module":{
        "id":"dathinel-red","version":env!("CARGO_PKG_VERSION"),"name":"Red de Sala","functions":funciones,"device":"cpu"}}));

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
                    let _ = tx.send(json!({"type":"error","id":id,"code":"busy","message":"dathinel-red está ocupado."}));
                    continue;
                }
                let cancelar = Arc::new(Notify::new());
                a.insert(id, cancelar.clone());
                drop(a);
                let (tx, activas, funcion) = (tx.clone(), activas.clone(), funcion.to_string());
                let parametros = m.get("parameters").cloned().unwrap_or(Value::Null);
                tokio::spawn(async move {
                    let respuesta = match sala::llamar(&funcion, parametros, cancelar).await {
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
