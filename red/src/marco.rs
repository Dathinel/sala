//! Marcos: 4 bytes de largo en little-endian + JSON UTF-8, en la tubería con la app y entre equipos.

use serde_json::Value;
use tokio::io::{AsyncRead, AsyncReadExt};

/// Marco de la tubería local (protocolo de módulos v1).
pub const MAX_LOCAL: usize = 1 << 20;
/// Marco del canal ordenado entre equipos (api.md §3).
pub const MAX_PAR: usize = 960 * 1024;
/// Anidamiento máximo del JSON, como el protocolo de módulos.
pub const MAX_PROFUNDIDAD: usize = 32;
/// Deja cuatro envoltorios para el evento de la tubería local.
pub const MAX_PROFUNDIDAD_PAR: usize = 28;

#[derive(Debug)]
pub enum ErrorMarco {
    Fin,
    Largo,
    Json,
    Io,
}

/// Lee un marco y lo devuelve como JSON validado (objeto, anidamiento, UTF-8 estricto).
/// `Fin` solo si la conexión se cerró limpia entre dos marcos.
pub async fn leer<R: AsyncRead + Unpin>(
    r: &mut R,
    max: usize,
    profundidad: usize,
) -> Result<Value, ErrorMarco> {
    leer_interno(r, max, profundidad, None).await
}

pub async fn leer_limitado<R: AsyncRead + Unpin>(
    r: &mut R,
    cubo: &mut crate::limites::Cubo,
) -> Result<Value, ErrorMarco> {
    leer_interno(r, MAX_PAR, MAX_PROFUNDIDAD_PAR, Some(cubo)).await
}

async fn leer_interno<R: AsyncRead + Unpin>(
    r: &mut R,
    max: usize,
    profundidad: usize,
    mut cubo: Option<&mut crate::limites::Cubo>,
) -> Result<Value, ErrorMarco> {
    if let Some(c) = cubo.as_mut() {
        c.esperar(4).await;
    }
    let mut cab = [0u8; 4];
    let mut n = 0;
    while n < 4 {
        match r.read(&mut cab[n..]).await {
            Ok(0) if n == 0 => return Err(ErrorMarco::Fin),
            Ok(0) => return Err(ErrorMarco::Io),
            Ok(k) => n += k,
            Err(_) => return Err(ErrorMarco::Io),
        }
    }
    let largo = i32::from_le_bytes(cab) as i64;
    if largo < 1 || largo as usize > max {
        return Err(ErrorMarco::Largo);
    }
    let mut buf = vec![0u8; largo as usize];
    for trozo in buf.chunks_mut(64 * 1024) {
        if let Some(c) = cubo.as_mut() {
            c.esperar(trozo.len()).await;
        }
        r.read_exact(trozo).await.map_err(|_| ErrorMarco::Io)?;
    }
    analizar(&buf, profundidad)
}

pub fn analizar(buf: &[u8], max_profundidad: usize) -> Result<Value, ErrorMarco> {
    // serde_json rechaza UTF-8 inválido y sustitutos UTF-16 sueltos.
    let v: Value = serde_json::from_slice(buf).map_err(|_| ErrorMarco::Json)?;
    if !v.is_object() {
        return Err(ErrorMarco::Json);
    }
    if profundidad(&v) > max_profundidad {
        return Err(ErrorMarco::Json);
    }
    Ok(v)
}

pub fn profundidad(v: &Value) -> usize {
    match v {
        Value::Array(a) => 1 + a.iter().map(profundidad).max().unwrap_or(0),
        Value::Object(o) => 1 + o.values().map(profundidad).max().unwrap_or(0),
        _ => 0,
    }
}

/// Cabecera + JSON listo para escribir de una vez.
pub fn armar(v: &Value) -> Vec<u8> {
    let json = serde_json::to_vec(v).expect("un Value siempre se serializa");
    let mut out = Vec::with_capacity(json.len() + 4);
    out.extend_from_slice(&(json.len() as i32).to_le_bytes());
    out.extend_from_slice(&json);
    out
}

pub fn texto<'a>(v: &'a Value, campo: &str) -> Option<&'a str> {
    v.get(campo).and_then(Value::as_str)
}

#[cfg(test)]
mod pruebas {
    use super::*;

    #[tokio::test]
    async fn rechaza_largos_fuera_de_rango_sin_reservar() {
        for largo in [0i32, -1, (MAX_PAR + 1) as i32] {
            let b = largo.to_le_bytes();
            assert!(matches!(
                leer(&mut &b[..], MAX_PAR, MAX_PROFUNDIDAD_PAR).await,
                Err(ErrorMarco::Largo)
            ));
        }
    }

    #[tokio::test]
    async fn fin_limpio_y_marco_cortado() {
        assert!(matches!(
            leer(&mut &[][..], MAX_PAR, MAX_PROFUNDIDAD_PAR).await,
            Err(ErrorMarco::Fin)
        ));
        assert!(matches!(
            leer(&mut &[5u8, 0][..], MAX_PAR, MAX_PROFUNDIDAD_PAR).await,
            Err(ErrorMarco::Io)
        ));
        let mut m = 10i32.to_le_bytes().to_vec();
        m.extend_from_slice(b"{}");
        assert!(matches!(
            leer(&mut &m[..], MAX_PAR, MAX_PROFUNDIDAD_PAR).await,
            Err(ErrorMarco::Io)
        ));
    }

    #[test]
    fn json_estricto() {
        assert!(analizar(br#"{"a":"\ud800"}"#, MAX_PROFUNDIDAD).is_err());
        assert!(analizar(br#"{"a":"\ud83d\ude00"}"#, MAX_PROFUNDIDAD).is_ok());
        assert!(analizar(&[b'{', b'"', 0xff, b'"', b':', b'1', b'}'], MAX_PROFUNDIDAD).is_err());
        assert!(analizar(b"[1]", MAX_PROFUNDIDAD).is_err());
        let hondo = format!("{{\"a\":{}{}}}", "[".repeat(40), "]".repeat(40));
        assert!(analizar(hondo.as_bytes(), MAX_PROFUNDIDAD).is_err());
    }

    #[test]
    fn conformidad_marcos_compartida_con_csharp() {
        let vectores: Value =
            serde_json::from_str(include_str!("../../pruebas/conformidad/limites.json")).unwrap();
        assert_eq!(vectores["profundidadMarco"], MAX_PROFUNDIDAD_PAR);
        assert_eq!(vectores["marcoMaxBytes"], MAX_PAR);
        assert_eq!(vectores["profundidadLocal"], MAX_PROFUNDIDAD);
        for caso in vectores["casos"].as_array().unwrap() {
            let bytes = caso["json"].as_str().unwrap().as_bytes();
            assert_eq!(
                analizar(bytes, MAX_PROFUNDIDAD_PAR).is_ok(),
                caso["marco"].as_bool().unwrap(),
                "{}",
                caso["nombre"]
            );
        }
        let profundo = format!("{{\"a\":{}{}}}", "[".repeat(28), "]".repeat(28));
        assert!(analizar(profundo.as_bytes(), MAX_PROFUNDIDAD).is_ok());
        assert!(analizar(profundo.as_bytes(), MAX_PROFUNDIDAD_PAR).is_err());
    }

    #[tokio::test]
    async fn ida_y_vuelta() {
        let v = serde_json::json!({"type":"hola","n":1});
        let b = armar(&v);
        assert_eq!(
            leer(&mut &b[..], MAX_PAR, MAX_PROFUNDIDAD_PAR)
                .await
                .unwrap(),
            v
        );
    }
}
