# Sala

Capa común de trabajo en grupo de Rodaje y Unicomp (decisión de Daniel, 15-nov-2026: repositorio aparte, público, MIT, como submódulo de los dos). Diseño: `Rodaje\docs\propuestas\colaboracion.md` (D-067) y, en Unicomp, D-017 §8.6. Contrato propio: `docs/api.md`; amenazas: `docs/seguridad.md`.

## Reglas
- Solo lo común: transporte (iroh, relé público de n0 con opción propia), sala (billetes, aprobación, claves ancladas, roles, reservas por clave de texto, orden central de cargas opacas, presencia, épocas y traspaso, blobs por huella) y el protocolo de módulos. Nada que sepa de proyectos de Rodaje ni de montajes de Unicomp.
- Ningún mensaje lleva rutas ni órdenes: cargas opacas que cada app valida.
- Un cambio de la API pasa por las sesiones de Rodaje y Unicomp (buzón `D:\Programas de uso libre\coordinacion-rodaje-unicomp.md`).
- Entorno: `build\preparar-entorno.ps1` (SDK .NET 10.0.401, Rust 1.100.0, cargo-deny 0.20.2 en `.entorno\`); después `. build\entorno.ps1`. Nada global.
- `src/Rodaje.Modules.Protocol` es copia de Rodaje (`91eab90a`) hasta el bloque de integración, que lo mueve y deja a Rodaje usándolo desde el submódulo. No editarlo aquí sin llevar el cambio a Rodaje.

## Estado
16-nov: API v1 revisada por Unicomp y Gemini y unida (#1). Fase B: `dathinel-red` (abrir, billetes, aceptar/rechazar/expulsar/revocar, enviar/recibir, estado, cerrar) y `Dathinel.Sala` (miembros y roles, orden central con `validar` asíncrono y tope, directos, ritmo, ids repetidos, épocas, desfasado), 22 pruebas de C# con procesos reales y 6 de Rust, en verde en Windows, Ubuntu 24.04 y macOS 15 (#2). Huella de cuatro palabras con `palabras.txt` (2048, prefijos de 4 letras distintos; Gemini). Revisión del núcleo por Gemini: 9 de sus 14 hallazgos arreglados; quedan (sin hacer) avisar antes de que un par perdido a mitad de la aprobación libere su billete y pruebas de billete caducado, de más de 4 solicitudes y de la conexión 17.

## Siguiente
1. Reservas y presencia (§5.4, §5.5), reconexión automática con cargas pendientes (§5.6), traspaso y suplente, blobs (§5.7: `blob-ofrecer` en lista, `blob-pedir` por eventos, `carpeta` confinada).
2. `Identidad` guardada con DPAPI / archivo 0600; mDNS (crate aparte de iroh) para la fase de red local de Rodaje.
3. Bloque de integración: mover `Rodaje.Modules.Protocol` con su historial y poner `externo/sala` en Rodaje y Unicomp.
