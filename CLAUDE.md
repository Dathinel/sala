# Sala

Capa común de trabajo en grupo de Rodaje y Unicomp (decisión de Daniel, 15-nov-2026: repositorio aparte, público, MIT, como submódulo de los dos). Diseño: `Rodaje\docs\propuestas\colaboracion.md` (D-067) y, en Unicomp, D-017 §8.6. Contrato propio: `docs/api.md`; amenazas: `docs/seguridad.md`.

## Reglas
- Solo lo común: transporte (iroh, relé público de n0 con opción propia), sala (billetes, aprobación, claves ancladas, roles, reservas por clave de texto, orden central de cargas opacas, presencia, épocas y traspaso, blobs por huella) y el protocolo de módulos. Nada que sepa de proyectos de Rodaje ni de montajes de Unicomp.
- Ningún mensaje lleva rutas ni órdenes: cargas opacas que cada app valida.
- Un cambio de la API pasa por las sesiones de Rodaje y Unicomp (buzón `D:\Programas de uso libre\coordinacion-rodaje-unicomp.md`).
- Entorno: `build\preparar-entorno.ps1` (SDK .NET 10.0.401, Rust 1.100.0, cargo-deny 0.20.2 en `.entorno\`); después `. build\entorno.ps1`. Nada global.
- `src/Rodaje.Modules.Protocol` es copia de Rodaje (`91eab90a`) hasta el bloque de integración, que lo mueve y deja a Rodaje usándolo desde el submódulo. No editarlo aquí sin llevar el cambio a Rodaje.

## Estado
- 16-nov: API v1 revisada por Unicomp y Gemini (#1); núcleo de la fase B (#2).
- 17-nov, bloque 2 (#4): reservas por clave y presencia; reconexión automática con cargas pendientes (mismo endpoint: `unirse` otra vez en el mismo proceso); traspaso con el historial como blob y `ascender` con el mismo endpoint; suplente tras `EsperaSuplente`; blobs por huella en streams aparte (rutas confinadas a la carpeta de sala, tope antes de abrir el stream, segunda comprobación en C#); `Identidad.CargarOCrear` (DPAPI en Windows, archivo 0600 en Linux y macOS, sin llavero; hecho por Devin); mDNS con `iroh-mdns-address-lookup` 0.6.1; billete libre si el invitado se va esperando aprobación; pruebas de billete caducado, más de 4 solicitudes y conexión 17. 41 pruebas de C# y 7 de Rust en la CI de los tres sistemas; la de mDNS no corre en el Mac de la CI (macOS 15 pide permiso de «Red local»). Escrito sin compilar en local (pausa del equipo): todo verificado solo en la CI.

## Siguiente
1. Revisión del bloque 2 por otro modelo (no se lanzó: pausa de la Cuadrilla).
2. Probar mDNS en un Mac de verdad y decidir cómo pedir el permiso de red local en la app de Mac.
3. Bloque de integración: mover `Rodaje.Modules.Protocol` con su historial y poner `externo/sala` en Rodaje y Unicomp.
