# Sala

Trabajo en grupo para las aplicaciones de Dathinel ([Rodaje](https://github.com/Dathinel/rodaje) y Unicomp): una persona abre una sala en su equipo, invita con un enlace y aprueba a cada miembro; todos editan su propia copia y el anfitrión ordena los cambios. Sin servidor propio ni red privada: la conexión va de equipo a equipo con [iroh](https://github.com/n0-computer/iroh) y, si no hay camino directo, por un relé que solo ve tráfico cifrado.

Contiene la parte común: el proceso `dathinel-red` (Rust) y el cliente `Dathinel.Sala` (C#), con el protocolo de módulos que nació en Rodaje. Lo propio de cada aplicación (qué es un cambio, cómo se valida) se queda en ella. Se usa como submódulo.

**Estado:** diseño. La propuesta está en `docs/` de Rodaje (`docs/propuestas/colaboracion.md`, D-067); el contrato de esta biblioteca, en [docs/api.md](docs/api.md) cuando esté.

Licencia MIT.
