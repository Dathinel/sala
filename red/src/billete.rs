//! Billete de invitación (api.md §4): `dathinel1.` + base64url sin relleno de un JSON.

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde::{Deserialize, Serialize};

const PREFIJO: &str = "dathinel1.";
const MAX_BILLETE: usize = 4096;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Billete {
    pub app: String,
    pub anfitrion: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rele: Option<String>,
    #[serde(default)]
    pub direcciones: Vec<String>,
    pub id: String,
    pub secreto: String,
    pub caduca: u64,
}

impl Billete {
    pub fn codificar(&self) -> String {
        PREFIJO.to_string()
            + &URL_SAFE_NO_PAD.encode(serde_json::to_vec(self).expect("serializable"))
    }

    pub fn decodificar(texto: &str) -> Option<Billete> {
        if texto.len() > MAX_BILLETE {
            return None;
        }
        let bytes = URL_SAFE_NO_PAD.decode(texto.strip_prefix(PREFIJO)?).ok()?;
        let b: Billete = serde_json::from_slice(&bytes).ok()?;
        (crate::hex::clave(&b.anfitrion).is_some()
            && crate::hex::bytes32(&b.secreto).is_some()
            && es_id(&b.id))
        .then_some(b)
    }
}

pub fn es_id(s: &str) -> bool {
    s.len() == 16
        && s.bytes()
            .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
}

#[cfg(test)]
mod pruebas {
    use super::*;

    #[test]
    fn ida_y_vuelta_y_basura() {
        let b = Billete {
            app: "rodaje".into(),
            anfitrion: crate::hex::de_clave(&iroh::SecretKey::from_bytes(&[7; 32]).public()),
            rele: None,
            direcciones: vec!["127.0.0.1:5".into()],
            id: "0123456789abcdef".into(),
            secreto: "cd".repeat(32),
            caduca: 9,
        };
        assert_eq!(Billete::decodificar(&b.codificar()), Some(b.clone()));
        assert_eq!(Billete::decodificar("dathinel1.!!"), None);
        assert_eq!(
            Billete::decodificar(&b.codificar().replace("dathinel1.", "dathinel2.")),
            None
        );
        let mut malo = b;
        malo.secreto = "zz".into();
        assert_eq!(Billete::decodificar(&malo.codificar()), None);
    }
}
