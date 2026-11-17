//! Archivos de prueba confinados al target de este worktree.

use std::path::PathBuf;

pub struct Carpeta(pub PathBuf);

impl Carpeta {
    pub fn nueva() -> Self {
        let ruta = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("target")
            .join(format!("prueba-{:016x}", rand::random::<u64>()));
        std::fs::create_dir_all(&ruta).unwrap();
        Self(ruta.canonicalize().unwrap())
    }
}

impl Drop for Carpeta {
    fn drop(&mut self) {
        std::fs::remove_dir_all(&self.0).unwrap();
    }
}
