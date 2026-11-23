//! dathinel-red: el transporte de Sala. Lo abre la app como un módulo (`--rodaje-pipe <nombre o ruta>`).

mod billete;
mod blobs;
mod diagnostico;
#[cfg(test)]
mod pruebas;
#[cfg(feature = "prueba")]
mod rele_prueba;
mod sala;
mod version;

use std::sync::Arc;

use dathinel_red::modulo::{self, FUNCIONES_SALA, Fallo, Info, Servicio};
use dathinel_red::{hex, limites, marco};
use serde_json::Value;
use tokio::sync::Notify;

static INFO: Info = Info {
    id: "dathinel-red",
    nombre: "Red de Sala",
    version: env!("CARGO_PKG_VERSION"),
    funciones: FUNCIONES_SALA,
};

struct Sala;

impl Servicio for Sala {
    async fn llamar(
        &self,
        funcion: String,
        p: Value,
        cancelar: Arc<Notify>,
    ) -> Result<Value, Fallo> {
        sala::llamar(&funcion, p, cancelar).await
    }
    async fn cerrar(&self) {
        sala::cerrar_todo().await;
    }
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    modulo::vigilar_padre();

    #[cfg(feature = "prueba")]
    if args.iter().any(|a| a == "--rele-prueba") {
        let codigo = match rele_prueba::ejecutar(&args).await {
            Ok(()) => 0,
            Err(e) => {
                eprintln!("No se pudo ejecutar el relé de pruebas: {e}");
                2
            }
        };
        std::process::exit(codigo);
    }

    std::process::exit(modulo::ejecutar(&args, &INFO, Arc::new(Sala)).await);
}
