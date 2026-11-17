//! dathinel-red: el transporte de Sala. Lo abre la app como un módulo (`--rodaje-pipe <nombre o ruta>`).

mod billete;
mod hex;
mod marco;
mod modulo;
mod sala;

use std::time::Duration;

const ARG_TUBERIA: &str = "--rodaje-pipe";

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    let Some(nombre) = args
        .iter()
        .position(|a| a == ARG_TUBERIA)
        .and_then(|i| args.get(i + 1))
    else {
        eprintln!("Este programa es el módulo de red de Sala; lo abre la app.");
        std::process::exit(64);
    };

    #[cfg(unix)]
    {
        // Si la app muere de golpe, el proceso no se queda vivo (en Windows lo hace el Job Object de la app).
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

    let codigo = match conectar(nombre).await {
        Ok(c) => c,
        Err(e) => {
            eprintln!("No se pudo conectar con la app: {e}");
            2
        }
    };
    std::process::exit(codigo);
}

#[cfg(windows)]
async fn conectar(nombre: &str) -> std::io::Result<i32> {
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
    Ok(modulo::atender(tuberia).await)
}

#[cfg(unix)]
async fn conectar(ruta: &str) -> std::io::Result<i32> {
    if !ruta.starts_with('/') || ruta.contains("/../") {
        return Err(std::io::Error::other("ruta de socket inválida"));
    }
    let socket = tokio::time::timeout(
        Duration::from_secs(10),
        tokio::net::UnixStream::connect(ruta),
    )
    .await??;
    Ok(modulo::atender(socket).await)
}
