//! Comparación numérica sin depender de SemVer: los ceros finales no cambian el orden.
use std::cmp::Ordering;

pub fn comparar(a: &str, b: &str) -> Option<Ordering> {
    fn partes(s: &str) -> Option<Vec<&str>> {
        s.split('.')
            .map(|p| {
                if p.is_empty() || !p.bytes().all(|c| c.is_ascii_digit()) {
                    None
                } else {
                    Some(p.trim_start_matches('0'))
                }
            })
            .collect()
    }
    let (a, b) = (partes(a)?, partes(b)?);
    for i in 0..a.len().max(b.len()) {
        let (x, y) = (
            a.get(i).copied().unwrap_or(""),
            b.get(i).copied().unwrap_or(""),
        );
        let orden = x.len().cmp(&y.len()).then_with(|| x.cmp(y));
        if orden != Ordering::Equal {
            return Some(orden);
        }
    }
    Some(Ordering::Equal)
}

pub fn nombre(app: &str) -> String {
    let mut letras = app.chars();
    match letras.next() {
        Some(c) => c.to_uppercase().collect::<String>() + letras.as_str(),
        None => String::new(),
    }
}

pub fn detalle_protocolo(app: &str) -> String {
    let app = nombre(app);
    format!(
        "Tu {app} y esta sala hablan versiones distintas del protocolo de sala: actualiza la que esté más atrás."
    )
}

pub fn detalle(app: &str, tuya: &str, suya: &str) -> String {
    let app = nombre(app);
    match comparar(tuya, suya) {
        Some(Ordering::Less) => {
            format!("Tu {app} es el {tuya} y esta sala usa el {suya}: actualiza tu {app}.")
        }
        Some(Ordering::Greater) => format!(
            "Esta sala usa el {suya} y tu {app} es el {tuya}: pide a quien te invitó que actualice el suyo."
        ),
        _ => format!(
            "Tu {app} es el {tuya} y esta sala usa el {suya}: las dos versiones tienen que ser la misma; actualiza la que esté más atrás."
        ),
    }
}

#[cfg(test)]
mod pruebas {
    use super::*;
    #[test]
    fn tabla_comparacion() {
        use Ordering::*;
        for (a, b, esperado) in [
            ("1.0.9", "1.0.10", Some(Less)),
            ("1.0", "1.0.0", Some(Equal)),
            ("01.000.04", "1.0.3", Some(Greater)),
            ("1.0.4-beta", "1.0.4", None),
            ("", "1.0", None),
            ("1..0", "1.0", None),
            ("+1", "1", None),
            ("99999999999999999999999999999", "2", Some(Greater)),
            ("0", "0.0.0", Some(Equal)),
        ] {
            assert_eq!(comparar(a, b), esperado, "{a} / {b}");
            assert_eq!(comparar(b, a), esperado.map(Ordering::reverse));
        }
    }
    #[test]
    fn protocolo_distinto_tiene_su_propio_texto() {
        assert!(
            detalle_protocolo("rodaje")
                .starts_with("Tu Rodaje y esta sala hablan versiones distintas del protocolo")
        );
    }
    #[test]
    fn tabla_textos() {
        for (tuya, suya, esperado) in [
            (
                "1.0.3",
                "1.0.4",
                "Tu Rodaje es el 1.0.3 y esta sala usa el 1.0.4: actualiza tu Rodaje.",
            ),
            (
                "1.0.4",
                "1.0.3",
                "Esta sala usa el 1.0.3 y tu Rodaje es el 1.0.4: pide a quien te invitó que actualice el suyo.",
            ),
            (
                "1.0.4-beta",
                "1.0.4",
                "Tu Rodaje es el 1.0.4-beta y esta sala usa el 1.0.4: las dos versiones tienen que ser la misma; actualiza la que esté más atrás.",
            ),
            (
                "1.0",
                "1.0.0",
                "Tu Rodaje es el 1.0 y esta sala usa el 1.0.0: las dos versiones tienen que ser la misma; actualiza la que esté más atrás.",
            ),
        ] {
            assert_eq!(detalle("rodaje", tuya, suya), esperado);
        }
    }
}
