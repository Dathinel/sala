# Licencias de terceros

El código de Sala es MIT (`LICENSE`). No entra nada GPL ni no libre.

## `dathinel-red` (Rust)

375 crates fijados en `red/Cargo.lock`, empezando por **iroh 1.4.1** (MIT o Apache-2.0). La lista completa con la licencia de cada uno está en [docs/licencias-crates.md](docs/licencias-crates.md); `red/deny.toml` fija qué licencias se admiten y la CI lo comprueba con `cargo deny check` en cada PR.

| Licencia | Cuántos | Nota |
|---|---|---|
| MIT o Apache-2.0 (casi siempre las dos a elegir) | la gran mayoría | |
| Unicode-3.0, ISC, BSD-2/3-Clause, Zlib, CC0-1.0, Unlicense, CDLA-Permissive-2.0 (`webpki-roots`) | unos 35 | Permisivas |
| **MPL-2.0**: `attohttpc` 0.30.1 | 1 | Copyleft débil por archivo: se cumple publicando los archivos de ese crate si se modifican (no se modifican; su fuente está en crates.io). Entra por el mapeo de puertos UPnP de iroh (`portmapper`), que ayuda a conectar directo detrás de un router |
| `r-efi` (MIT o Apache-2.0 o LGPL-2.1+) | 2 | Se usa como MIT; solo existe para UEFI, no se compila en Windows, Linux ni macOS |

Aviso ignorado con su razón en `red/deny.toml`: RUSTSEC-2024-0436 (`paste` sin mantenimiento; macro de compilación que entra por netlink en Linux, sin código en el binario).

## `Dathinel.Sala` (C#)

Sin paquetes de NuGet: solo la biblioteca estándar de .NET 10 (MIT). Las pruebas usan xUnit 2.9.3 (Apache-2.0) y Microsoft.NET.Test.Sdk 18.10.1 (MIT), que no se distribuyen.

`src/Rodaje.Modules.Protocol` es código propio de Daniel (autoría solo de Daniel y Claude según `git log` en Rodaje), copiado de Rodaje (`91eab90a`) y publicado aquí con MIT, como aprobó Daniel con el repositorio (D-067 §10.1).

## Herramientas de compilación (no se distribuyen)

SDK de .NET 10.0.401 (MIT), Rust 1.100.0 (MIT o Apache-2.0) con rustup 1.29.3, cargo-deny 0.20.2 (MIT o Apache-2.0), todos en `.entorno\` con huella fijada (`build/preparar-entorno.ps1`).
