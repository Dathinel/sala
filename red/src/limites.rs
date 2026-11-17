//! Reservas de cupos y cubo de fichas compartidos por los lectores del transporte.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use iroh::EndpointId;

#[derive(Clone, Default)]
pub struct Cupos(Arc<Mutex<HashMap<EndpointId, usize>>>);

pub struct Reserva {
    cupos: Cupos,
    quien: EndpointId,
}

impl Cupos {
    pub fn reservar(&self, quien: EndpointId, por_par: usize, total: usize) -> Option<Reserva> {
        let mut usados = self.0.lock().unwrap();
        if usados.values().sum::<usize>() >= total
            || usados.get(&quien).copied().unwrap_or(0) >= por_par
        {
            return None;
        }
        *usados.entry(quien).or_default() += 1;
        Some(Reserva {
            cupos: self.clone(),
            quien,
        })
    }
}

impl Drop for Reserva {
    fn drop(&mut self) {
        let mut usados = self.cupos.0.lock().unwrap();
        let n = usados.get_mut(&self.quien).expect("reserva viva");
        *n -= 1;
        if *n == 0 {
            usados.remove(&self.quien);
        }
    }
}

pub const BYTES_SEGUNDO: usize = 8 << 20;

pub struct Cubo {
    saldo: f64,
    rafaga: f64,
    ritmo: f64,
    ultimo: Instant,
}

impl Cubo {
    pub fn nuevo(ritmo: usize, rafaga: usize, ahora: Instant) -> Self {
        Self {
            saldo: rafaga as f64,
            rafaga: rafaga as f64,
            ritmo: ritmo as f64,
            ultimo: ahora,
        }
    }

    pub fn marcos(anfitrion: bool, ahora: Instant) -> Self {
        if anfitrion {
            Self::nuevo(100, 400, ahora)
        } else {
            Self::nuevo(1600, 6400, ahora)
        }
    }

    /// Si falta saldo no lo gasta: devuelve cuánto esperar para reintentar.
    pub fn tomar(&mut self, cantidad: usize, ahora: Instant) -> Result<(), Duration> {
        self.saldo = (self.saldo
            + ahora.saturating_duration_since(self.ultimo).as_secs_f64() * self.ritmo)
            .min(self.rafaga);
        self.ultimo = ahora;
        if self.saldo < cantidad as f64 {
            return Err(Duration::from_secs_f64(
                (cantidad as f64 - self.saldo) / self.ritmo,
            ));
        }
        self.saldo -= cantidad as f64;
        Ok(())
    }

    pub async fn esperar(&mut self, cantidad: usize) {
        while let Err(demora) = self.tomar(cantidad, Instant::now()) {
            tokio::time::sleep(demora).await;
        }
    }
}

#[cfg(test)]
mod pruebas {
    use super::*;
    use std::sync::Barrier;

    #[test]
    fn cubo_bytes_espera_y_recarga_sin_superar_rafaga() {
        let t = Instant::now();
        let mut c = Cubo::nuevo(BYTES_SEGUNDO, BYTES_SEGUNDO, t);
        assert!(c.tomar(BYTES_SEGUNDO, t).is_ok());
        assert_eq!(
            c.tomar(BYTES_SEGUNDO / 2, t),
            Err(Duration::from_millis(500))
        );
        assert!(
            c.tomar(BYTES_SEGUNDO / 2, t + Duration::from_millis(500))
                .is_ok()
        );
        assert!(c.tomar(1, t + Duration::from_millis(500)).is_err());
        assert!(c.tomar(BYTES_SEGUNDO, t + Duration::from_secs(10)).is_ok());
        assert!(c.tomar(1, t + Duration::from_secs(10)).is_err());
    }

    #[test]
    fn marcos_limita_invitado_y_anfitrion_agregado() {
        let t = Instant::now();
        for (anfitrion, rafaga, ritmo) in [(true, 400, 100), (false, 6400, 1600)] {
            let mut c = Cubo::marcos(anfitrion, t);
            assert!(c.tomar(rafaga, t).is_ok());
            assert!(c.tomar(1, t).is_err());
            assert!(c.tomar(ritmo, t + Duration::from_secs(1)).is_ok());
            assert!(c.tomar(1, t + Duration::from_secs(1)).is_err());
        }
    }

    #[test]
    fn reservas_concurrentes_no_superan_cupos_y_drop_libera() {
        for (por_par, total) in [(16, 16), (4, 16)] {
            let cupos = Cupos::default();
            let barrera = Arc::new(Barrier::new(65));
            std::thread::scope(|s| {
                let tareas: Vec<_> = (0..64)
                    .map(|i| {
                        let cupos = cupos.clone();
                        let barrera = barrera.clone();
                        s.spawn(move || {
                            let k = iroh::SecretKey::from_bytes(&[(i % 8) as u8; 32]).public();
                            barrera.wait();
                            let reserva = cupos.reservar(k, por_par, total);
                            barrera.wait();
                            reserva
                        })
                    })
                    .collect();
                barrera.wait();
                barrera.wait();
                let reservas: Vec<_> = tareas
                    .into_iter()
                    .filter_map(|t| t.join().unwrap())
                    .collect();
                assert_eq!(reservas.len(), total);
                let usados = cupos.0.lock().unwrap();
                assert!(usados.values().all(|n| *n <= por_par));
                drop(usados);
                drop(reservas);
                assert!(cupos.0.lock().unwrap().is_empty());
            });
        }
    }
}
