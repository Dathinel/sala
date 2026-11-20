//! Infraestructura local compilada exclusivamente con `prueba`.
use std::{
    io::{self, Write as _},
    path::Path,
    sync::{Arc, Mutex},
    time::Duration,
};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt},
    net::{TcpListener, TcpStream},
    sync::mpsc,
    task::{JoinHandle, JoinSet},
    time::{Instant, sleep_until},
};

pub async fn ejecutar(args: &[String]) -> io::Result<()> {
    let latencia = match args.iter().position(|a| a == "--latencia-ms") {
        Some(i) => args
            .get(i + 1)
            .and_then(|s| s.parse::<u64>().ok())
            .ok_or_else(|| {
                io::Error::other("«--latencia-ms» necesita un número entero de milisegundos.")
            })?,
        None => 0,
    };
    let captura = match args.iter().position(|a| a == "--captura") {
        Some(i) => Some(Path::new(
            args.get(i + 1)
                .filter(|s| !s.starts_with("--"))
                .ok_or_else(|| io::Error::other("«--captura» necesita un archivo."))?,
        )),
        None => None,
    };
    let (url, servidor) = abrir_rele(args.iter().any(|a| a == "--sin-tls")).await?;
    let mut proxy = if latencia > 0 || captura.is_some() {
        Some(Proxy::abrir_con_captura(&url, Duration::from_millis(latencia), captura).await?)
    } else {
        None
    };
    // stdout solo transporta esta línea; ningún diagnóstico ni certificado.
    {
        let linea = serde_json::json!({"url":proxy.as_ref().map(|p| p.url.as_str()).unwrap_or(url.as_str()).trim_end_matches('/')});
        let mut salida = std::io::stdout().lock();
        writeln!(salida, "{linea}")?;
        salida.flush()?;
    }
    let mut entrada = tokio::io::stdin();
    let mut buffer = [0; 1024];
    let resultado = loop {
        tokio::select! {
            lectura = entrada.read(&mut buffer) => match lectura {
                Ok(0) => break Ok(()),
                Ok(_) => {}
                Err(e) => break Err(e),
            },
            resultado = async {
                match proxy.as_mut() {
                    Some(p) => (&mut p.tarea).await,
                    None => std::future::pending().await,
                }
            } => {
                proxy.take();
                break resultado.unwrap_or_else(|_| Err(io::Error::other("Falló el proxy local.")));
            }
        }
    };
    if let Some(proxy) = proxy {
        proxy.cerrar().await;
    }
    drop(servidor);
    resultado
}

async fn abrir_rele(sin_tls: bool) -> io::Result<(iroh::RelayUrl, iroh_relay::server::Server)> {
    if !sin_tls {
        let (_, url, servidor) = iroh::test_utils::run_relay_server_with(false)
            .await
            .map_err(|_| io::Error::other("No se pudo abrir el relé local."))?;
        return Ok((url, servidor));
    }
    use iroh_relay::server::{RelayConfig, Server, ServerConfig};
    let mut relay = RelayConfig::new((std::net::Ipv4Addr::LOCALHOST, 0));
    relay.tls = None;
    relay.key_cache_capacity = Some(1024);
    let mut config = ServerConfig::default();
    config.relay = Some(relay);
    let servidor = Server::spawn(config)
        .await
        .map_err(|_| io::Error::other("No se pudo abrir el relé local."))?;
    let direccion = servidor
        .http_addr()
        .ok_or_else(|| io::Error::other("El relé local no tiene puerto HTTP."))?;
    let url = format!("http://{direccion}")
        .parse()
        .map_err(|_| io::Error::other("URL del relé local inválida."))?;
    Ok((url, servidor))
}

struct Captura {
    archivo: Mutex<std::fs::File>,
    errores: mpsc::UnboundedSender<()>,
}

impl Captura {
    fn escribir(&self, datos: &[u8]) -> io::Result<()> {
        let resultado = (|| {
            let mut archivo = self
                .archivo
                .lock()
                .map_err(|_| io::Error::other("Captura interrumpida."))?;
            archivo.write_all(datos)?;
            archivo.flush()
        })();
        resultado.map_err(|_| {
            let _ = self.errores.send(());
            io::Error::other("No se pudo escribir la captura del relé.")
        })
    }
}

pub struct Proxy {
    pub url: String,
    tarea: JoinHandle<io::Result<()>>,
}

impl Proxy {
    #[cfg(test)]
    pub async fn abrir(destino: &iroh::RelayUrl, latencia: Duration) -> io::Result<Self> {
        Self::abrir_con_captura(destino, latencia, None).await
    }

    pub async fn abrir_con_captura(
        destino: &iroh::RelayUrl,
        latencia: Duration,
        archivo: Option<&Path>,
    ) -> io::Result<Self> {
        let (errores, mut fallos) = mpsc::unbounded_channel();
        let captura = archivo
            .map(|ruta| {
                std::fs::File::create(ruta)
                    .map(|archivo| {
                        Arc::new(Captura {
                            archivo: Mutex::new(archivo),
                            errores,
                        })
                    })
                    .map_err(|_| io::Error::other("No se pudo crear la captura del relé."))
            })
            .transpose()?;
        let puerto = destino
            .port()
            .ok_or_else(|| io::Error::other("El relé local no tiene puerto."))?;
        let escucha = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).await?;
        let url = format!("{}://{}", destino.scheme(), escucha.local_addr()?);
        let tarea = tokio::spawn(async move {
            let mut conexiones = JoinSet::new();
            loop {
                tokio::select! {
                    aceptada = escucha.accept() => {
                        let Ok((cliente,_)) = aceptada else { break };
                        let captura = captura.clone();
                        conexiones.spawn(async move {
                            let Ok(servidor) = TcpStream::connect((std::net::Ipv4Addr::LOCALHOST,puerto)).await else { return };
                            let (cr,cw) = cliente.into_split();
                            let (sr,sw) = servidor.into_split();
                            let _ = tokio::try_join!(
                                retrasar(cr,sw,latencia,captura.clone()),
                                retrasar(sr,cw,latencia,captura)
                            );
                        });
                    }
                    _ = conexiones.join_next(), if !conexiones.is_empty() => {}
                    Some(()) = fallos.recv() => {
                        return Err(io::Error::other("No se pudo escribir la captura del relé."));
                    }
                }
            }
            conexiones.abort_all();
            while conexiones.join_next().await.is_some() {}
            Ok(())
        });
        Ok(Self { url, tarea })
    }

    pub async fn cerrar(mut self) {
        self.tarea.abort();
        let _ = (&mut self.tarea).await;
    }
}

impl Drop for Proxy {
    fn drop(&mut self) {
        self.tarea.abort();
    }
}

/// Leer y retrasar son operaciones independientes. Cada fragmento vence N ms después
/// de su recepción, sin acumular N ms por fragmento ni alterar el orden TCP.
async fn retrasar<R: AsyncRead + Unpin, W: AsyncWrite + Unpin>(
    mut entrada: R,
    mut salida: W,
    latencia: Duration,
    captura: Option<Arc<Captura>>,
) -> io::Result<()> {
    let (tx, mut rx) = mpsc::unbounded_channel();
    let leer = async move {
        let mut buffer = vec![0; 64 * 1024];
        loop {
            let n = entrada.read(&mut buffer).await?;
            if n == 0 {
                break;
            }
            if let Some(captura) = &captura {
                captura.escribir(&buffer[..n])?;
            }
            let cuando = Instant::now()
                .checked_add(latencia)
                .ok_or_else(|| io::Error::other("La latencia solicitada es demasiado grande."))?;
            if tx.send((cuando, buffer[..n].to_vec())).is_err() {
                break;
            }
        }
        Ok::<_, io::Error>(())
    };
    let escribir = async move {
        while let Some((cuando, datos)) = rx.recv().await {
            sleep_until(cuando).await;
            salida.write_all(&datos).await?;
        }
        salida.shutdown().await
    };
    tokio::try_join!(leer, escribir).map(|_| ())
}

#[cfg(test)]
mod pruebas {
    use super::*;
    use crate::sala::ALPN;
    use iroh::{Endpoint, RelayMode, endpoint::presets, tls::CaTlsConfig};

    struct ArchivoCaptura(std::path::PathBuf);

    impl ArchivoCaptura {
        fn nuevo() -> Self {
            Self(
                std::env::current_dir()
                    .expect("carpeta de pruebas")
                    .join(format!("captura-rele-{:032x}.bin", rand::random::<u128>())),
            )
        }
    }

    impl Drop for ArchivoCaptura {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    #[tokio::test]
    async fn sin_tls_captura_no_revela_el_marcador() {
        let archivo = ArchivoCaptura::nuevo();
        tokio::time::timeout(Duration::from_secs(45), async {
            let (url, _servidor) = abrir_rele(true).await.expect("relé HTTP");
            assert_eq!(url.scheme(), "http");
            let proxy = Proxy::abrir_con_captura(&url, Duration::ZERO, Some(&archivo.0))
                .await
                .expect("proxy con captura");
            assert_eq!(
                std::fs::metadata(&archivo.0).expect("archivo creado").len(),
                0
            );
            let url: iroh::RelayUrl = proxy.url.parse().expect("URL proxy HTTP");
            assert_eq!(url.scheme(), "http");
            let crear = || {
                Endpoint::builder(presets::Minimal)
                    .relay_mode(RelayMode::Custom(url.clone().into()))
                    .clear_ip_transports()
                    .alpns(vec![ALPN.to_vec()])
                    .bind()
            };
            let a = crear().await.expect("endpoint a");
            let b = crear().await.expect("endpoint b");
            tokio::join!(a.online(), b.online());
            assert!(a.bound_sockets().is_empty());
            assert!(b.bound_sockets().is_empty());
            let aceptar = async { a.accept().await.expect("entrada").await.expect("aceptada") };
            let (cliente, servidor) = tokio::join!(b.connect(a.addr(), ALPN), aceptar);
            let cliente = cliente.expect("conexión por relé HTTP");
            let (mut tx, mut rx) = cliente.open_bi().await.expect("stream");
            const MARCADOR: &[u8] = b"MARCADOR-SECRETO-8d41";
            tx.write_all(MARCADOR).await.expect("envío secreto");
            let (mut stx, mut srx) = servidor.accept_bi().await.expect("stream remoto");
            let mut recibido = vec![0; MARCADOR.len()];
            srx.read_exact(&mut recibido)
                .await
                .expect("recepción secreta");
            assert_eq!(recibido, MARCADOR);
            stx.write_all(MARCADOR).await.expect("eco secreto");
            rx.read_exact(&mut recibido).await.expect("eco recibido");
            assert_eq!(recibido, MARCADOR);
            assert!(
                cliente
                    .paths()
                    .iter()
                    .any(|p| p.is_selected() && p.is_relay())
            );
            assert!(
                servidor
                    .paths()
                    .iter()
                    .any(|p| p.is_selected() && p.is_relay())
            );
            // Se lee mientras el relé y el proxy siguen abiertos: comprueba el flush.
            let bytes = std::fs::read(&archivo.0).expect("captura visible");
            assert!(bytes.len() > 1024);
            assert!(!bytes.windows(MARCADOR.len()).any(|v| v == MARCADOR));
            let alpn_visible = bytes.windows(ALPN.len()).any(|v| v == ALPN);
            println!("ALPN dathinel/sala/1 visible al operador: {alpn_visible}");
            // Su presencia no es obligatoria: depende de la protección del handshake.
            if alpn_visible {
                assert!(String::from_utf8_lossy(&bytes).contains("dathinel/sala/1"));
            }
            a.close().await;
            b.close().await;
            proxy.cerrar().await;
        })
        .await
        .expect("plazo de captura por relé HTTP");
    }

    #[tokio::test]
    async fn captura_trunca_y_guarda_ambos_sentidos_sin_separadores() {
        let archivo = ArchivoCaptura::nuevo();
        std::fs::write(&archivo.0, b"contenido anterior").expect("archivo previo");
        tokio::time::timeout(Duration::from_secs(5), async {
            let destino = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))
                .await
                .expect("destino TCP");
            let url = format!("http://{}", destino.local_addr().expect("puerto"))
                .parse()
                .expect("URL");
            let proxy = Proxy::abrir_con_captura(&url, Duration::ZERO, Some(&archivo.0))
                .await
                .expect("proxy");
            assert_eq!(std::fs::read(&archivo.0).expect("truncado"), b"");
            let url: iroh::RelayUrl = proxy.url.parse().expect("URL proxy");
            let mut cliente =
                TcpStream::connect((std::net::Ipv4Addr::LOCALHOST, url.port().expect("puerto")))
                    .await
                    .expect("cliente");
            let (mut servidor, _) = destino.accept().await.expect("servidor");
            cliente.write_all(b"ida").await.expect("ida");
            let mut buffer = [0; 3];
            servidor
                .read_exact(&mut buffer)
                .await
                .expect("ida recibida");
            assert_eq!(&buffer, b"ida");
            servidor.write_all(b"eco").await.expect("vuelta");
            cliente
                .read_exact(&mut buffer)
                .await
                .expect("vuelta recibida");
            assert_eq!(&buffer, b"eco");
            assert_eq!(
                std::fs::read(&archivo.0).expect("captura abierta"),
                b"idaeco"
            );
            proxy.cerrar().await;
        })
        .await
        .expect("plazo de captura TCP");
    }

    #[tokio::test]
    async fn solo_rele_rtt_y_reconexion() {
        tokio::time::timeout(Duration::from_secs(45), async {
            let (_, url, _servidor) = iroh::test_utils::run_relay_server_with(false)
                .await
                .expect("relé local");
            let proxy = Proxy::abrir(&url, Duration::from_millis(20))
                .await
                .expect("proxy local");
            let url: iroh::RelayUrl = proxy.url.parse().expect("URL proxy");
            let crear = || {
                Endpoint::builder(presets::Minimal)
                    .relay_mode(RelayMode::Custom(url.clone().into()))
                    .ca_tls_config(CaTlsConfig::insecure_skip_verify())
                    .clear_ip_transports()
                    .alpns(vec![ALPN.to_vec()])
                    .bind()
            };
            let a = crear().await.expect("endpoint a");
            let b = crear().await.expect("endpoint b");
            tokio::join!(a.online(), b.online());
            assert_eq!(a.addr().ip_addrs().count(), 0);
            assert_eq!(b.addr().ip_addrs().count(), 0);
            assert!(a.bound_sockets().is_empty());
            assert!(b.bound_sockets().is_empty());
            for vuelta in 0..2 {
                let aceptar = async { a.accept().await.expect("entrada").await.expect("aceptada") };
                let (cliente, servidor) = tokio::join!(b.connect(a.addr(), ALPN), aceptar);
                let cliente = cliente.expect("conexión solo por relé");
                assert_eq!(cliente.alpn(), ALPN);
                let (mut tx, mut rx) = cliente.open_bi().await.expect("canal");
                tx.write_all(b"hola").await.expect("envío");
                let (mut stx, mut srx) = servidor.accept_bi().await.expect("canal remoto");
                let mut buf = [0; 4];
                srx.read_exact(&mut buf).await.expect("lectura remota");
                assert_eq!(&buf, b"hola");
                stx.write_all(&buf).await.expect("eco");
                rx.read_exact(&mut buf).await.expect("eco recibido");
                let paths = cliente.paths();
                let elegida = paths
                    .iter()
                    .find(|p| p.is_selected())
                    .expect("camino elegido");
                assert!(elegida.is_relay());
                assert!(elegida.rtt() > Duration::ZERO);
                println!("Vuelta {vuelta}: RTT QUIC {:?}", elegida.rtt());
                assert!(
                    servidor
                        .paths()
                        .iter()
                        .any(|p| p.is_selected() && p.is_relay())
                );
                cliente.close(1u32.into(), b"reconectar");
                servidor.closed().await;
                drop((cliente, servidor));
                tokio::time::sleep(Duration::from_millis(100)).await;
            }
            a.close().await;
            b.close().await;
            proxy.cerrar().await;
        })
        .await
        .expect("plazo de prueba solo por relé");
    }

    #[tokio::test]
    async fn cola_retrasa_sin_limitar_caudal_y_conserva_orden() {
        let (mut emisor, entrada) = tokio::io::duplex(8192);
        let (salida, mut receptor) = tokio::io::duplex(8192);
        let tarea = tokio::spawn(retrasar(entrada, salida, Duration::from_millis(300), None));
        let inicio = Instant::now();
        emisor.write_all(&[1; 1024]).await.expect("primero");
        tokio::time::sleep(Duration::from_millis(30)).await;
        emisor.write_all(&[2; 1024]).await.expect("segundo");
        emisor.shutdown().await.expect("fin");
        let mut primero = [0; 1024];
        tokio::time::timeout(Duration::from_secs(2), receptor.read_exact(&mut primero))
            .await
            .expect("plazo")
            .expect("lectura");
        assert!(inicio.elapsed() >= Duration::from_millis(300));
        assert_eq!(primero, [1; 1024]);
        let mut segundo = Vec::new();
        // Un sleep por fragmento tardaría otros 300 ms: la cola vence cada uno
        // según su recepción, por lo que el segundo llega unos 30 ms después.
        tokio::time::timeout(
            Duration::from_millis(150),
            receptor.read_to_end(&mut segundo),
        )
        .await
        .expect("sin retraso acumulado")
        .expect("segundo");
        assert_eq!(segundo, vec![2; 1024]);
        tarea.await.expect("tarea").expect("proxy");
    }
}
