//! Hexadecimal en minúscula para claves y secretos.

use iroh::{EndpointId, SecretKey};

pub fn codificar(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

pub fn bytes32(s: &str) -> Option<[u8; 32]> {
    if s.len() != 64 || !s.bytes().all(|c| matches!(c, b'0'..=b'9' | b'a'..=b'f')) {
        return None;
    }
    let mut out = [0u8; 32];
    for (i, o) in out.iter_mut().enumerate() {
        *o = u8::from_str_radix(&s[2 * i..2 * i + 2], 16).ok()?;
    }
    Some(out)
}

pub fn clave(s: &str) -> Option<EndpointId> {
    EndpointId::from_bytes(&bytes32(s)?).ok()
}

pub fn secreta(s: &str) -> Option<SecretKey> {
    Some(SecretKey::from_bytes(&bytes32(s)?))
}

pub fn de_clave(k: &EndpointId) -> String {
    codificar(k.as_bytes())
}
