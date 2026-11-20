//! Infraestructura local compilada exclusivamente con `prueba`.
use std::{
    io::{self, Write as _},
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
    let (_, url, servidor) = iroh::test_utils::run_relay_server_with(false)
        .await
        .map_err(|_| io::Error::other("No se pudo abrir el relé local."))?;
    let proxy = if latencia > 0 {
        Some(Proxy::abrir(&url, Duration::from_millis(latencia)).await?)
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
        match entrada.read(&mut buffer).await {
            Ok(0) => break Ok(()),
            Ok(_) => {}
            Err(e) => break Err(e),
        }
    };
    if let Some(proxy) = proxy {
        proxy.cerrar().await;
    }
    drop(servidor);
    resultado
}

pub struct Proxy {
    pub url: String,
    tarea: JoinHandle<()>,
}

impl Proxy {
    pub async fn abrir(destino: &iroh::RelayUrl, latencia: Duration) -> io::Result<Self> {
        let puerto = destino
            .port()
            .ok_or_else(|| io::Error::other("El relé local no tiene puerto."))?;
        let escucha = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).await?;
        let url = format!("https://{}", escucha.local_addr()?);
        let tarea = tokio::spawn(async move {
            let mut conexiones = JoinSet::new();
            loop {
                tokio::select! {
                    aceptada = escucha.accept() => {
                        let Ok((cliente,_)) = aceptada else { break };
                        conexiones.spawn(async move {
                            let Ok(servidor) = TcpStream::connect((std::net::Ipv4Addr::LOCALHOST,puerto)).await else { return };
                            let (cr,cw) = cliente.into_split();
                            let (sr,sw) = servidor.into_split();
                            let _ = tokio::try_join!(retrasar(cr,sw,latencia),retrasar(sr,cw,latencia));
                        });
                    }
                    _ = conexiones.join_next(), if !conexiones.is_empty() => {}
                }
            }
            conexiones.abort_all();
            while conexiones.join_next().await.is_some() {}
        });
        Ok(Self { url, tarea })
    }

    pub async fn cerrar(self) {
        self.tarea.abort();
        let _ = self.tarea.await;
    }
}

/// Leer y retrasar son operaciones independientes. Cada fragmento vence N ms después
/// de su recepción, sin acumular N ms por fragmento ni alterar el orden TCP.
async fn retrasar<R: AsyncRead + Unpin, W: AsyncWrite + Unpin>(
    mut entrada: R,
    mut salida: W,
    latencia: Duration,
) -> io::Result<()> {
    let (tx, mut rx) = mpsc::unbounded_channel();
    let leer = async move {
        let mut buffer = vec![0; 64 * 1024];
        loop {
            let n = entrada.read(&mut buffer).await?;
            if n == 0 {
                break;
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
        let tarea = tokio::spawn(retrasar(entrada, salida, Duration::from_millis(300)));
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
