# Sala

Trabajo en grupo para las aplicaciones de Dathinel ([Rodaje](https://github.com/Dathinel/rodaje) y Unicomp): una persona abre una sala en su equipo, invita con un enlace y aprueba a cada miembro; todos editan su propia copia y el anfitrión ordena los cambios. Sin servidor propio ni red privada: la conexión va de equipo a equipo con [iroh](https://github.com/n0-computer/iroh) y, si no hay camino directo, por un relé que solo ve tráfico cifrado.

Contiene la parte común: el proceso `dathinel-red` (Rust, `red/`), el cliente `Dathinel.Sala` (C#, `src/Dathinel.Sala`) y el protocolo de módulos que nació en Rodaje (`src/Rodaje.Modules.Protocol`). Lo propio de cada aplicación (qué es un cambio, cómo se valida) se queda en ella. Se usa como submódulo.

- Contrato: [docs/api.md](docs/api.md). Modelo de amenazas: [docs/seguridad.md](docs/seguridad.md).
- Hecho: billetes, aprobación, claves ancladas, roles, orden central con validación de la app, mensajes directos, límites de tamaño y ritmo. Falta: reservas, presencia, traspaso, reconexión automática y blobs.

## Compilar y probar

Todo va en `.entorno\` (nada global; versiones y huellas fijadas):

```
powershell -NoProfile -ExecutionPolicy Bypass -File build\preparar-entorno.ps1
. build\entorno.ps1                      # o: source build/entorno.sh
cd red; cargo build; cargo test; cd ..
dotnet test Sala.slnx
```

Las pruebas de C# abren dos y tres `dathinel-red` reales en el mismo equipo: 10 000 mensajes en orden, billetes gastados, revocados y de otra app, roles, cargas con rutas, ritmo, épocas viejas. La CI las corre en Windows, Linux y macOS.

Licencia MIT. Terceros: [LICENCIAS.md](LICENCIAS.md).
