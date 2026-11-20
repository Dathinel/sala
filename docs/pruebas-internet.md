# Pruebas de internet (B13, 20-nov-2026)

Dos instancias de `dathinel-red` con identidades distintas en un solo PC (Windows 11, Bogotá, internet doméstica), con la CI como segunda comprobación en Windows, Linux y macOS. Qué se probó, cómo repetirlo y qué salió.

## Cómo repetirlo

```powershell
. build\entorno.ps1
cd red;  cargo build --locked --features prueba      # el binario de pruebas lleva el relé local: el de producción se compila sin el feature
cd ..;   $env:SALA_RED = "$PWD\red\target\debug\dathinel-red.exe"
dotnet test tests\Dathinel.Sala.Tests --filter "Categoria=Rele"                       # relé local, también en la CI
dotnet test tests\Dathinel.Sala.Tests --filter "Categoria=Internet" --logger "console;verbosity=detailed"   # relé público de n0: a mano
```

`dathinel-red --rele-prueba [--latencia-ms N] [--sin-tls --captura archivo]` levanta un relé de iroh en local (con retraso fijo por tramo y, si se pide, sin TLS para guardar lo que ve el operador). Solo existe con `--features prueba`; `releInseguro` (no verificar el certificado del relé de pruebas) tampoco existe sin él y el binario normal lo rechaza.

## Qué se comprueba y con qué prueba

| Criterio | Prueba | Resultado |
|---|---|---|
| Dos instancias conectan solo por relé y dicen que es relé | `ReleTests.Dos_equipos_solo_por_rele_…` | Camino `rele`, URL del relé, versión y alias de cada par; `soloRele` sin direcciones IP propias; el billete lleva el relé |
| Directo cuando se puede | `ReleTests.Sin_solo_rele_…` | Con direcciones disponibles pasa a `directo` solo |
| Reconexión por relé | `ReleTests.Si_se_corta_el_proceso_de_red_…` y la de Rust | Vuelve por el relé; 1 reconexión contada; una edición posterior entra |
| Latencia por relé | `ReleTests.Una_edicion_por_rele_…` | Ver abajo |
| Captura sin texto legible | `rele_prueba::sin_tls_captura_no_revela_el_marcador` (Rust) | Relé sin TLS con captura de los dos sentidos: `MARCADOR-SECRETO-8d41` mandado por la conexión no aparece en lo que ve el relé |
| Comprobación de versión | `ReleTests.Otra_version_no_entra_…`, `version.rs` | `1.0.3` contra `1.0.4`, `1.0.10`, `1.0.4-beta`: no entra y el mensaje dice a quién le toca actualizar |
| Relé que no existe | `ReleTests.Un_rele_que_no_existe_…` | `abrir-sala` no cuelga (5 s) y el diagnóstico dice «Relé: sin conectar» |

## Cifras con relé local (retraso fijo por tramo)

PC cargado con las pruebas de otras sesiones. Ida y vuelta de una edición = desde `EnviarAsync` hasta el `orden` de vuelta (invitado → relé → anfitrión → relé → invitado).

| Prueba | Resultado |
|---|---|
| Entrar por relé local (de `UnirseAsync` a la bienvenida) | 144 ms (49 ms según `dathinel-red`) |
| Edición con 30 ms por tramo, 60 cargas | p50 170 ms, p95 234 ms, máx 282 ms; RTT de QUIC 156 ms |
| Reconexión tras matar el proceso de red del invitado | 194 ms |

## Cifras con el relé público de n0

Relé de casa elegido: `use1-1.relay.n0.iroh.link` (Virginia, EE. UU.; n0 no tiene relé en Sudamérica). `soloRele`, tres salas de prueba y 40 cargas de unos 20 bytes por vuelta: unos 5 KB en total, sin bucles de carga (la política de n0: solo para desarrollo y uso personal, con límite de tráfico que no publican).

| Medida | Tres tandas |
|---|---|
| Entrar (de `UnirseAsync` a la bienvenida) | 1101 a 1894 ms (9 salas: mediana 1228 ms) |
| Edición ida y vuelta | p50 191, 188 y 184 ms; p95 740, 342 y 247 ms; máx 1538, 343 y 328 ms |
| RTT de QUIC | 271, 191 y 191 ms |
| Pérdida de paquetes | 0 % |
| Reconexión (matar el proceso de red) | 1510, 1543 y 1505 ms |

Lectura honesta:

- La mediana es estable (unos 185 ms); la cola no: el p95 pasó de 740 a 247 ms según la carga del PC y de n0 en ese momento. **El criterio «p95 < 250 ms» con el relé público desde Colombia se cumple a ratos, no siempre**: el relé está en EE. UU. y cada edición cruza cuatro tramos (invitado al relé, relé al anfitrión y los dos de vuelta). Con las dos instancias en el mismo PC los cuatro son de este PC al relé; entre dos países serán los de cada uno, que pueden ser más cortos o más largos.
- Para el usuario no es lo que parece: Rodaje aplica la edición al instante en local y los ~190 ms son hasta que el anfitrión la confirma.
- Si molesta, la salida es un relé propio más cerca de los dos (un servidor pequeño con `iroh-relay` y un nombre con certificado, en el puerto 443): decisión de Daniel (coste mensual).
- Pérdida con paquetes perdidos simulados: **no se hizo**. Sin permisos de administrador no hay `netem` ni `clumsy` en el PC ni en la CI de GitHub, y el retraso por tramo del relé de pruebas es TCP (la pérdida se vería como más retraso, no como pérdida de paquetes de QUIC). Queda como medida a mano en la prueba entre países (`Rodaje/docs/prueba-colaboracion.md` pide anotar la pérdida).

## Lo que ve un operador de relé

Relé sin TLS con captura (los mismos bytes que ve el servidor del relé tras quitar el TLS de la conexión): el contenido de la conexión QUIC (TLS 1.3 de extremo a extremo) no aparece. Tampoco aparece el nombre del protocolo (`dathinel/sala/1`): la prueba lo busca y no está. Lo que sí ve el operador son las IP, la hora y el volumen. Está dicho en `Textos.Rele` y en `docs/internet.md`.
