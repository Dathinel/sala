//! Constructores puros del contrato de diagnóstico (§2.3).
use serde::Serialize;
use serde_json::{Value, json};

#[derive(Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Par {
    pub clave: String,
    pub alias: Option<String>,
    pub via: Option<&'static str>,
    pub rele: Option<String>,
    pub rtt_ms: Option<u64>,
    pub perdidos_pct: Option<f64>,
    pub paquetes_enviados: u64,
    pub paquetes_perdidos: u64,
    pub enviados_b: u64,
    pub recibidos_b: u64,
    pub app_version: Option<String>,
    pub protocolo: Option<u64>,
    pub conectado_desde: u64,
    pub ultima_actividad: u64,
    pub cambios_de_camino: u64,
}

pub fn perdidos_pct(enviados: u64, perdidos: u64) -> Option<f64> {
    (enviados >= 100).then(|| (perdidos as f64 * 1000.0 / enviados as f64).round() / 10.0)
}

pub fn red(rele: &str, url: Option<String>, conectado: bool, solo: bool, mdns: bool) -> Value {
    json!({"rele":rele,"releUrl":url,"releConectado":conectado,"soloRele":solo,"mdns":mdns})
}

pub fn estado(
    clave: String,
    direcciones: Vec<String>,
    anfitrion: bool,
    red: Value,
    conexion: Value,
    pares: Vec<Par>,
    caida: Option<Value>,
) -> Value {
    json!({"clave":clave,"direcciones":direcciones,"rol":if anfitrion {"anfitrion"} else {"invitado"},"red":red,"conexion":conexion,"pares":pares,"ultimaCaida":caida})
}

#[cfg(test)]
mod pruebas {
    use super::*;
    #[test]
    fn tabla_perdida() {
        for (enviados, perdidos, esperado) in [
            (0, 0, None),
            (99, 1, None),
            (100, 1, Some(1.0)),
            (5120, 21, Some(0.4)),
            (300, 2, Some(0.7)),
            (100, 0, Some(0.0)),
        ] {
            assert_eq!(perdidos_pct(enviados, perdidos), esperado);
        }
    }
    #[test]
    fn contrato_json_completo_y_nulos() {
        let par = Par {
            clave: "par".into(),
            ..Par::default()
        };
        let vacio = estado(
            "propia".into(),
            vec![],
            false,
            red("ninguno", None, false, false, false),
            json!({"fase":"listo","intento":0}),
            vec![par],
            None,
        );
        assert_eq!(
            vacio,
            json!({"clave":"propia","direcciones":[],"rol":"invitado","red":{"rele":"ninguno","releUrl":null,"releConectado":false,"soloRele":false,"mdns":false},"conexion":{"fase":"listo","intento":0},"pares":[{"clave":"par","alias":null,"via":null,"rele":null,"rttMs":null,"perdidosPct":null,"paquetesEnviados":0,"paquetesPerdidos":0,"enviadosB":0,"recibidosB":0,"appVersion":null,"protocolo":null,"conectadoDesde":0,"ultimaActividad":0,"cambiosDeCamino":0}],"ultimaCaida":null})
        );
        let par = Par {
            clave: "par".into(),
            alias: Some("Luis".into()),
            via: Some("rele"),
            rele: Some("https://relay.example/".into()),
            rtt_ms: Some(118),
            perdidos_pct: perdidos_pct(5120, 21),
            paquetes_enviados: 5120,
            paquetes_perdidos: 21,
            enviados_b: 1048576,
            recibidos_b: 524288,
            app_version: Some("1.0.4".into()),
            protocolo: Some(1),
            conectado_desde: 1763400000,
            ultima_actividad: 1763400123,
            cambios_de_camino: 1,
        };
        let caida = json!({"clave":"anterior","motivo":"red","en":1763399000});
        let completo = estado(
            "propia".into(),
            vec![],
            true,
            red(
                "n0",
                Some("https://relay.example/".into()),
                true,
                true,
                false,
            ),
            json!({"fase":"sincronizando","intento":2}),
            vec![par],
            Some(caida.clone()),
        );
        assert_eq!(
            completo["pares"][0],
            json!({"clave":"par","alias":"Luis","via":"rele","rele":"https://relay.example/","rttMs":118,"perdidosPct":0.4,"paquetesEnviados":5120,"paquetesPerdidos":21,"enviadosB":1048576,"recibidosB":524288,"appVersion":"1.0.4","protocolo":1,"conectadoDesde":1763400000,"ultimaActividad":1763400123,"cambiosDeCamino":1})
        );
        assert_eq!(completo["ultimaCaida"], caida);
        assert_eq!(
            completo["red"],
            json!({"rele":"n0","releUrl":"https://relay.example/","releConectado":true,"soloRele":true,"mdns":false})
        );
        assert_eq!(completo["rol"], "anfitrion");
        assert_eq!(
            completo["conexion"],
            json!({"fase":"sincronizando","intento":2})
        );
    }
}
