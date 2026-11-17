# API de Sala, versión 1

Contrato de `dathinel-red` (proceso de Rust) y de `Dathinel.Sala` (biblioteca de C#), la capa común de trabajo en grupo de Rodaje y Unicomp. Parte de D-067 de Rodaje (`docs/propuestas/colaboracion.md` §2 y §3.4) y de D-017 de Unicomp (`docs/diseno-equipo-animacion-plataformas.md` §8.6). Lo que no está aquí es de cada app.

Estado: v1 implementada entera en el bloque 2 (17-nov-2026). Revisado por la sesión de Unicomp (16-nov-2026, buzón `coordinacion-rodaje-unicomp.md`): entran el mensaje `directo`, `validar` asíncrono con tope, `blob-ofrecer` en lista, el historial en el traspaso y la recomendación de una carga por paso de deshacer.

## 0. En una página

```
 App (Rodaje o Unicomp)
   │  API tipada: Sala.Hospedar / Sala.Unirse, eventos, Enviar, Reservar, Presencia, blobs
 Dathinel.Sala (C#)            ← la sala: miembros y roles, orden central, reservas, presencia, épocas
   │  protocolo de módulos v1 (tubería con nombre / socket Unix, marcos de 1 MiB, invoke/result)
 dathinel-red (Rust, iroh)     ← el transporte: identidad, billetes, quién puede entrar, límites, blobs
   │  QUIC + TLS 1.3, ALPN dathinel/sala/1, directo o por relé
 los demás equipos
```

| Capa | Hace | No hace |
|---|---|---|
| `dathinel-red` | Clave del equipo, conexiones iroh, billetes y su secreto, dejar pasar solo claves ancladas o aceptadas, límites de marco y de ritmo por par, canal ordenado y canal de blobs aparte, comprobar huella de blobs | Saber qué es un miembro con rol, un orden, una reserva o el proyecto |
| `Dathinel.Sala` | Miembros (alias, rol, clave anclada, huella de cuatro palabras), orden central de cargas opacas, reservas por clave de texto, presencia, épocas, reconexión, traspaso | Abrir, validar ni interpretar la carga: eso lo hace la app con una función que le pasa a la sala |
| App | Qué es un cambio y cómo se valida; dónde se guardan las claves ancladas, el registro y los blobs; la interfaz | Hablar con la red directamente |

Topología en estrella: cada invitado tiene **una** conexión, con el anfitrión. Los invitados no se conectan entre sí; lo que va de uno a otro pasa por el anfitrión.

## 1. El proceso `dathinel-red`

- Rust estable fijado en `rust-toolchain.toml`, **iroh 1.4.1** fijado en `Cargo.lock` (`=1.4.1` en `Cargo.toml`), ALPN único **`dathinel/sala/1`**: una conexión con otro ALPN se cierra sin leerla.
- Se abre como cualquier módulo: la app lo lanza con `ModuleProcess` (o `ModuleManager`) y le habla el **protocolo de módulos v1 sin cambios** (`Rodaje/docs/protocolo-modulos.md`): tubería con nombre `CurrentUserOnly` en Windows, socket Unix 0700 en Linux y macOS (`--rodaje-pipe <nombre o ruta>`), marco de 4 bytes + JSON de hasta 1 MiB, `hello`/`welcome`, `invoke`/`result`/`error`, `cancel`, `ping`. El proceso comprueba que lo abrió el usuario actual (lo hace la tubería) y se cierra si muere la app (`getppid` en Unix, Job Object en Windows).
- `welcome`: `module.id` = `dathinel-red`, `module.version` = la del crate, y las funciones de §2.
- Un proceso = **una sala**, como anfitrión o como invitado. Arranca con `abrir-sala` o `unirse` y termina con `cerrar` o `shutdown`. No escucha nada antes de la primera de esas dos llamadas (nada abierto por defecto).
- **Llamadas a la vez:** hasta 4 (`busy` después). Solo hay dos llamadas largas, `recibir` (siempre) y `unirse` (al entrar): las descargas de blobs **no** ocupan una llamada, empiezan con `blob-pedir`, que vuelve enseguida, y avisan por eventos. Así quedan libres al menos dos para `enviar` y lo demás. `ping` se contesta siempre, también durante un `recibir` largo.
- Errores de llamada (`error` con `id`, la conexión con la app sigue): los códigos del protocolo más estos: `no-abierta` (no hay sala), `ya-abierta`, `rechazado` (el anfitrión dijo que no), `version` (otra app u otra versión: el mensaje dice cuál), `billete-invalido`, `sin-conexion`, `ocupado-red` (cola de salida llena: reintentar, contrapresión), `desconocido` (clave que no está en la sala), `huella` (un blob no coincide), `demasiado-grande`.

## 2. Funciones de `dathinel-red`

Parámetros y resultados en JSON (`parameters` y `value` del `invoke`). Claves: 64 hexadecimales en minúscula (clave pública Ed25519 de iroh, el `EndpointId`). Tiempos en segundos Unix.

| Función | Lado | Parámetros | Resultado |
|---|---|---|---|
| `abrir-sala` | anfitrión | `identidad` (64 hex, clave **privada**), `app`, `appVersion`, `red` (§2.1), `carpeta` (ruta absoluta de la carpeta de sala de la app), `anclados` (lista de claves que entran sin billete) | `clave` (la pública), `direcciones` |
| `billete` | anfitrión | `caducaEn` (segundos, 5–604 800; 86 400 por defecto), `unUso` (sí por defecto) | `billete` (texto, §4), `id` |
| `revocar` | anfitrión | `id`? | — Sin `id`, invalida **todos** los billetes vivos; con `id`, solo ese. Los miembros ya anclados siguen. Retira las solicitudes afectadas con `solicitud-retirada` (`revocado`); el invitado recibe `adios` `billete-invalido` |
| `aceptar` | anfitrión | `clave` | — La clave pasa a anclada mientras viva el proceso; la app la guarda para la próxima vez |
| `rechazar` | anfitrión | `clave`, `motivo`? | — |
| `expulsar` | anfitrión | `clave`, `motivo`?, `vetar` (sí por defecto) | — Cierra su conexión; con `vetar`, la saca de anclados y ningún billete la vuelve a dejar entrar en esta sala |
| `unirse` | invitado | `identidad`, `app`, `appVersion`, `alias`, `red`, `carpeta`, y **o** `billete` **o** `anfitrion` + `direcciones`? (para volver sin billete, ya anclado) | `anfitrion` (clave), `via` (`directo` o `rele`), `anfitrionDirecciones`, `direcciones` (las propias). **Llamada larga:** espera la aprobación hasta 10 min; se cancela con `cancel`. Un invitado puede volver a llamarla en el mismo proceso (reconexión o cambio de anfitrión): conserva su endpoint y sus direcciones |
| `ascender` | invitado | `anclados` | `clave`, `direcciones`. El invitado pasa a anfitrión **con el mismo endpoint** (las direcciones que ya conocen los demás): corta con el anfitrión anterior (cierre `traspaso`) y empieza a aceptar conexiones. Lo usan el traspaso y el suplente |
| `enviar` | los dos | `a` (lista de claves; vacía o ausente = todos los conectados; en el invitado solo vale el anfitrión), `datos` (objeto JSON, la carga de la sala, ≤ 960 KiB serializado y profundidad ≤ 28) | `encolados` (cuántos destinos) y `llenos` (claves que se saltaron). A destinos concretos es todo o nada (`ocupado-red`); en una difusión se salta a quien tiene la cola llena, que repone lo perdido al ver el hueco y `sincronizar` |
| `recibir` | los dos | `max` (eventos, 1–1024) | `eventos` (§2.2). **Llamada larga:** vuelve en cuanto hay algo o vacía a los 25 s; una sola a la vez. El lote se corta antes de pasar de 1000 KiB serializado: un `mensaje` de 960 KiB va solo, y lo que no cabe sale en la siguiente |
| `blob-ofrecer` | los dos | `blobs`: lista (1–1024) de `{huella (SHA-256, 64 hex), tamano, ruta}` (archivos **locales** de quien ofrece, dentro de `carpeta`) | — El proceso los sirve a los pares que los pidan mientras estén ofrecidos. El archivo **no debe cambiar** mientras esté ofrecido: se lee al servirlo, y quien pide comprueba la huella (si cambió, la descarga falla con `huella`). Lo seguro es ofrecer copias de un almacén por contenido dentro de la carpeta de sala |
| `blob-retirar` | los dos | `huellas` (lista) | — |
| `blob-pedir` | los dos | `de` (clave), `huella`, `tamano` (exacto; > 4 GiB se rechaza sin abrir el stream), `destino` (ruta local que elige la app; tiene que quedar dentro de `carpeta` una vez resuelta, o `invalid-parameters`) | `{"intento": <u64>}`. Contador del proceso desde 1. Vuelve enseguida; `blob-avance` (≤ 4/s) y `blob-fin` identifican el intento. Máximo 4 descargas por par y 16 en total; destino resuelto ya en uso → `ocupado`. Crea `<destino>.<16 hex aleatorios>.parte` en exclusiva (`create_new`, también rechaza enlaces). Comprueba que el temporal resuelto sigue dentro de `carpeta`; si no, lo borra y da `disco`. Comprueba tamaño y SHA-256; si no coinciden, borra y da `huella`. Antes de renombrar comprueba otra vez el padre del destino; si queda fuera, borra y da `disco` |
| `blob-cancelar` | los dos | `intento` (u64) | — Señala la cancelación cooperativa. La tarea borra su `.parte` y emite un solo `blob-fin` con `error: "cancelado"`; no se aborta. Un intento ya terminado no hace nada |
| `estado` | los dos | — | `clave`, `direcciones`, `rol` (`anfitrion`/`invitado`), `pares`: `[{clave, via, rttMs, enviadosB, recibidosB}]` |
| `cerrar` | los dos | `motivo`? | — Cancela cooperativamente las descargas, espera su limpieza y su `blob-fin` (`cancelado`), cierra todo y deja el proceso listo para salir |

### 2.1 `red`

```json
{"rele":"n0","mdns":false,"soloRele":false}
```

- `rele`: `"n0"` (relés públicos de n0, por defecto; Daniel, 15-nov), `"ninguno"` (solo red local o direcciones directas; la fase 1 de Rodaje), o una URL `https://…` de un relé propio (Preferencias de cada app).
- `mdns`: descubrimiento en la red local con `iroh-mdns-address-lookup` 0.6.1 (de n0, MIT o Apache-2.0), servicio `dathinel-sala`. Encendido, el equipo **anuncia su clave en la red local** a quien escuche; sirve para entrar o volver sin direcciones. Apagado por defecto; Rodaje lo enciende en su fase 1.
- `soloRele`: no intentar conexión directa, para no mostrar la IP a los pares. **A verificar** contra la API de iroh 1.4 en la fase de internet; si no se puede, la función falla con `invalid-parameters` en vez de prometerlo.

### 2.2 Eventos de `recibir`

| `tipo` | Lado | Campos | Cuándo |
|---|---|---|---|
| `solicitud` | anfitrión | `clave`, `alias`, `appVersion`, `billete` (id) | Alguien con un billete válido quiere entrar. Hasta 4 pendientes a la vez; más se rechazan sin avisar |
| `solicitud-retirada` | anfitrión | `clave` (64 hex), `motivo` (`se-fue`, `tiempo`, `revocado`) | Se quitó una solicitud sin `aceptar` ni `rechazar`: el invitado se fue, venció la aprobación o se revocó su billete |
| `entra` | los dos | `clave`, `alias`, `via` | Conexión admitida (aceptada o anclada) |
| `sale` | los dos | `clave`, `motivo` (`cerro`, `red`, `ritmo`, `marco`, `expulsado`) | Se cortó una conexión |
| `mensaje` | los dos | `de`, `datos` | Un marco del canal ordenado de un par admitido, en orden de llegada por par |
| `blob-avance` | los dos | `huella`, `intento` (u64), `recibidos` | Como mucho 4/s por descarga |
| `blob-fin` | los dos | `huella`, `intento` (u64), `ok`, `error`? (`huella`, `no-ofrecido`, `cancelado`, `disco`, `red`, `ocupado`) | Terminó un intento de descarga; un solo final por intento |
| `cerrada` | invitado | `motivo` (`expulsado`, `revocado`, `anfitrion-cerro`) | La sala terminó para este equipo |

El proceso solo pasa mensajes y blobs de pares admitidos. Las solicitudes y sus retiradas son eventos de admisión. Los contadores `enviadosB` y `recibidosB` de `estado` son los bytes de datagramas de la conexión que da `Connection::stats()` de iroh 1.4.1; incluyen el tráfico QUIC, también los blobs.

## 3. Entre equipos: dentro de QUIC

Una conexión iroh por invitado, con ALPN `dathinel/sala/1`. Streams:

| Stream | Quién lo abre | Para qué |
|---|---|---|
| **Canal ordenado** | el invitado, el primero (bidireccional) | Saludo y después los marcos de la sala, en orden y fiables |
| **Blob** | quien lo pide, uno por blob (bidireccional) | Cabecera `{"huella":…,"tamano":…}` y, de vuelta, los bytes en crudo. Prioridad menor que el canal ordenado: un blob de 500 MB no retrasa una edición. Si no puede servirlo, quien ofrece corta el stream con un código de aplicación QUIC: `1` no ofrecido (o retirado), `2` cancelado, `3` error de lectura, `4` tamaño distinto, `5` ocupado. El servidor reserva el cupo antes de leer la cabecera y lo conserva hasta terminar, incluida la espera de `stopped` |

Marco del canal ordenado: 4 bytes de largo en little-endian + JSON UTF-8 estricto, **1 B a 960 KiB**, anidamiento ≤ **28** (también el `hola`), las mismas reglas de lectura que el protocolo de módulos (campos desconocidos se ignoran; un `type` desconocido válido se salta; lo demás cierra la conexión).

Un marco de profundidad 29 cierra solo esa conexión con `marco`. La tubería local conserva el límite de 32: el marco del par llega a la app dentro de cuatro envoltorios.

### 3.1 Saludo (lo hace `dathinel-red`, antes de dejar pasar nada)

Invitado → anfitrión, primer marco:

```json
{"type":"hola","sala":1,"app":"rodaje","appVersion":"1.0.4","alias":"Luis","billete":"<id>","secreto":"<64 hex>"}
```

- `app` distinta → `{"type":"adios","motivo":"version","detalle":"Esta sala es de Unicomp."}` y cierre. **Rodaje y Unicomp no comparten sala** (Daniel, 15-nov).
- `appVersion` distinta → `adios` con `version` y la versión del anfitrión: el invitado sabe cuál le falta.
- Clave anclada (y no vetada) → admitida sin billete; `billete` y `secreto` pueden faltar.
- Si no: el par `billete`+`secreto` tiene que ser de un billete vivo (comparación en tiempo constante), no caducado y, si es de un uso, no usado ni en trámite. Si falla → `adios` `billete-invalido` enseguida, sin evento para la app y sin ocupar un hueco de solicitud. Si vale → evento `solicitud` y el invitado espera; `aceptar` → `{"type":"admitido"}`; `rechazar` → `adios` `rechazado`. Un billete de un uso queda **en trámite** desde que se presenta bien (nadie más puede usarlo mientras) y **gastado** al aceptar o rechazar; si el invitado se va antes, vuelve a estar libre.
- La identidad del invitado no la dice el `hola`: es la clave del par TLS de la conexión iroh. El alias es solo un nombre.

### 3.2 Límites del transporte, por par

| Qué | Límite | Al pasarse |
|---|---|---|
| Marco del canal ordenado | 960 KiB y profundidad 28 | Cierra la conexión (`marco`) |
| Ritmo de marcos | El anfitrión recibe de cada invitado 100/s de media, ráfaga 400; el invitado recibe del anfitrión 1600/s, ráfaga 6400 (agrega a 16 miembros) | Cierra la conexión (`ritmo`) |
| Bytes por el canal ordenado | Cubo por conexión: 8 MiB/s de media y ráfaga de 8 MiB, incluida la cabecera | Espera saldo antes de seguir leyendo (`tokio::time::sleep`, contrapresión de QUIC); no cierra |
| Saludo | 10 s para el `hola`; 10 min esperando aprobación | Cierra |
| Solicitudes pendientes | 4, y solo las ocupan billetes válidos; comprobación y reserva en la misma sección crítica | Cierra sin avisar a la app |
| Billetes falsos | 20 por minuto por dirección IP de origen (no en todo el proceso: un atacante no agota el cupo de los demás) | Cierra sin leer el `hola` |
| Conexiones admitidas | 16 (las apps ponen su tope debajo: Unicomp 8); reserva atómica antes de escribir `admitido`, liberada en cualquier salida | `adios` `llena` |
| Blobs en curso | Tanto al pedir como al servir: 4 por par y 16 en total por sala, con cupos separados por sentido; tamaño ≤ 4 GiB y el que diga quien lo pide | `blob-pedir` → `ocupado`; al servir, corta el stream con `5` |
| Cola de salida | 4 MiB por par | `enviar` → `ocupado-red` |

## 4. Billete

`dathinel1.<base64url sin relleno de JSON>`; el JSON:

```json
{"app":"rodaje","anfitrion":"<64 hex>","rele":"https://…","direcciones":["192.168.1.20:51234"],"id":"<16 hex>","secreto":"<64 hex>","caduca":1763400000}
```

- `secreto`: 256 bits aleatorios por billete. `caduca` e `unUso` los hace cumplir el anfitrión; el `caduca` del billete es solo para que el invitado avise antes de intentarlo.
- Cada app lo envuelve en su enlace (`rodaje://unirse/<billete>`, `unicomp://unirse/<billete>`) y su QR. El billete no lleva rutas, nombres de proyecto ni alias.
- Sin relé (`rele` ausente) solo sirve con `direcciones` o mDNS: es el de la fase 1 de Rodaje.

## 5. `Dathinel.Sala` (C#)

Biblioteca `net10.0`, compatible con AOT y sin reflexión (System.Text.Json con generador). Abre `dathinel-red` con el protocolo de módulos y hace la sala encima. El cliente del protocolo es `Rodaje.Modules.Protocol`, que pasa a este repositorio (la extracción que D-031 dejó para el segundo programa; se queda con su nombre para que Rodaje y sus módulos solo cambien la ruta del proyecto en el bloque de integración). Los mensajes de sala viajan como `datos` de `enviar` y `mensaje`.

### 5.1 Lo que ve la app

```csharp
var identidad = Identidad.CargarOCrear(carpetaPerfil);          // DPAPI en Windows, archivo 0600 en Unix
var opciones = new OpcionesSala("rodaje", "1.0.4", alias: "Ana", red: Red.N0, ejecutable: rutaDathinelRed)
    { TopeValidar = TimeSpan.FromSeconds(30) };

await using Sala sala = await Sala.HospedarAsync(identidad, opciones, anclados, validar, ct);
string enlace = "rodaje://unirse/" + await sala.NuevoBilleteAsync(TimeSpan.FromHours(24), unUso: true);
sala.Solicitud += s => /* «Luis quiere entrar · río-mesa-luna-ocho» */ sala.AceptarAsync(s.Clave, Rol.Editar);

await using Sala invitado = await Sala.UnirseAsync(identidad, opciones, billete, ct);
invitado.Ordenado += o => /* aplicar o.Datos en orden o.Seq */;
await invitado.EnviarAsync(datos, reservas: ["pista:A2"]);       // vuelve con el id; la respuesta llega como Ordenado o Rechazado
```

| Miembro | Qué hace |
|---|---|
| `HospedarAsync(identidad, opciones, anclados, validar, epoca = 1, seq = 0)` | Abre la sala. `anclados`: los miembros que la app guardó (clave, alias, rol). `validar`: `Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>` que la sala llama **en orden**, una carga cada vez, antes de darle número; asíncrono para no ocupar un hilo (Unicomp regenera el CAD) |
| `UnirseAsync(identidad, opciones, billete \| anfitrion, ultimoSeq = 0, epoca = 0, suscribir)` | Entra; vuelve tras la aprobación y el `bienvenida`. `suscribir(sala)` se llama antes de conectar, para no perder los `orden` que reponen justo después de la bienvenida (igual en `HospedarAsync`) |
| `NuevoBilleteAsync`, `RevocarAsync`, `AceptarAsync(clave, rol)`, `RechazarAsync`, `ExpulsarAsync`, `CambiarRolAsync` | Anfitrión |
| `EnviarAsync(datos, reservas)` | Propone una carga. En el anfitrión pasa por el mismo `validar` sin red |
| `EnviarDirectoAsync(a, datos)` | Mensaje de la app **fuera del orden**: ni se valida ni se numera ni entra en el historial; a un miembro o a todos (en el invitado, solo al anfitrión). Para el manifiesto de la instantánea al entrar o tras `desfasado`, pedir huellas sueltas o anunciar blobs |
| `ReservarAsync(clave)`, `SoltarAsync(clave)`, `Reservas` | §5.4 |
| `PresenciaAsync(datos)` | §5.5 |
| `OfrecerBlobsAsync`, `RetirarBlobsAsync`, `PedirBlobAsync` | §5.7 |
| `TraspasarAsync(clave)`, `TomarAnfitrionAsync()` | §5.6 |
| `Miembros`, `Epoca`, `Seq`, `Rol`, `EsAnfitrion` | Estado |
| Eventos | `Solicitud`, `MiembroEntra`, `MiembroSale`, `Ordenado`, `Rechazado`, `Directo`, `ReservaCambia`, `Presencia`, `Desfasado`, `Superada`, `Cerrada` |

`Identidad.CargarOCrear(archivo)` guarda la clave privada: en Windows cifrada con DPAPI del usuario actual; en Linux y macOS en un archivo 0600 (se rechaza si el grupo u otros pueden leerlo), sin llavero del sistema por ahora (Secret Service o Keychain serían el paso siguiente). Escritura atómica; un archivo dañado da `SalaException` `identidad` y nunca se pisa.

`Huella.Palabras(clave)` da las cuatro palabras: SHA-256 de los 32 bytes de la clave, 44 primeros bits en cuatro grupos de 11, cada uno un índice en una lista propia de 2048 palabras en español (sin tildes ambiguas ni palabras que se confundan de viva voz). Encontrar otra clave con las mismas cuatro palabras cuesta del orden de 2⁴⁴ claves; la identidad sigue siendo la clave anclada, la huella es para compararla en voz alta.

### 5.2 Mensajes de sala

Todos con `type` y la `epoca` del anfitrión. Tamaños medidos en el JSON serializado.

| `type` | Sentido | Campos |
|---|---|---|
| `sincronizar` | invitado → anfitrión, al entrar y al volver | `epoca` (la que conoce), `ultimoSeq` |
| `bienvenida` | anfitrión → invitado, respuesta a **cada** `sincronizar` | `epoca`, `seq`, `miembros` `[{clave, alias, rol, color}]`, `reservas`, `suplente`? `{clave, direcciones}` (las del suplente, para encontrarlo sin relé ni mDNS). Después van los `orden` que le faltan, de `ultimoSeq + 1` a `seq`: al llegar a `seq` el invitado está al día |
| `carga` | invitado → anfitrión | `id` (GUID del autor), `datos` (objeto, ≤ 900 KiB), `reservas` (claves que necesita, ≤ 64) |
| `orden` | anfitrión → todos, el autor incluido | `epoca`, `seq`, `id`, `autor`, `datos` |
| `rechazado` | anfitrión → autor | `id`, `motivo` (código), `detalle`? (≤ 4096) |
| `desfasado` | anfitrión → invitado | `desde` (el `seq` más viejo que guarda): la app tiene que pedir el estado entero (blob) |
| `reservar`, `soltar` | invitado → anfitrión | `clave` |
| `reserva` | anfitrión → todos | `clave`, `de` (clave o nada si quedó libre) |
| `directo` | en los dos sentidos (el invitado, solo al anfitrión) | `datos` (objeto, ≤ 900 KiB). No se reenvía: va de quien lo manda a su destino, y el evento `Directo` dice de quién |
| `presencia` | en los dos sentidos | `datos` (objeto, ≤ 4 KiB); del anfitrión lleva además `de` |
| `miembros` | anfitrión → todos | la lista entera, al cambiar |
| `traspaso` | anfitrión → todos | `a` (clave), `epoca` (la nueva), `seq`, `historial` (huella y tamaño del blob con el historial de reposición, solo para el nuevo anfitrión) |
| `adios` | cualquiera | `motivo` |

Nunca hay un mensaje para leer, listar o abrir archivos, ni para ejecutar nada. La única ruta que existe en la API es la que la app le da **a su propio** proceso (`ruta`, `destino`), y nunca viaja.

### 5.3 Orden central

- El anfitrión tiene `epoca` y `seq`. Por cada `carga`: (1) el id ya visto (se guardan, **por miembro**, los ids de sus últimas 8192 cargas aceptadas y de sus 1024 últimas rechazadas: las ráfagas de un miembro no borran los ids de otro) → reenvía al autor la misma respuesta (`orden` o `rechazado`): reenviar tras un corte es seguro; (2) rol `mirar` → `rechazado` `rol`; (3) alguna reserva de `reservas` no es del autor → `rechazado` `reserva`; (4) `validar` de la app → `rechazado` con su motivo; si no contesta en `TopeValidar` (30 s por defecto, lo pone la app) se cancela su token y la carga sale `rechazado` `tiempo`, para que un validador colgado no pare la sala; o (5) `seq + 1` y `orden` a todos.
- `validar` corre de a una, en el orden de llegada al anfitrión.
- El invitado aplica los `orden` por `seq` creciente; uno con `seq` repetido se ignora, un hueco hace pedir `sincronizar` otra vez. Uno con `epoca` menor que la conocida se descarta (anfitrión viejo).
- El anfitrión guarda en memoria los últimos 4096 `orden` (o 64 MiB) para reponer a quien vuelve; más atrás, `desfasado`.
- Como cada `orden` lleva su `id` y su autor, cualquier miembro puede rehacer la tabla de ids aceptados con lo que recibió: es lo que hace un anfitrión nuevo (§5.6) para no aceptar dos veces una carga que el viejo ya había ordenado.
- **Predicción local** (aplicar antes de que llegue el `orden`, y retirar al recibir `rechazado`) es de la app: la sala solo le dice qué entró y qué no.

### 5.4 Reservas

- Clave de reserva: texto de 1 a 128 caracteres `[a-z0-9:/._-]`, sin `..`, sin empezar por `/` ni terminar en `/`. Es una **etiqueta**, nunca una ruta: la sala no la abre ni la compara con nada del disco. Rodaje usa `pista:A2` y `proyecto`; Unicomp `seccion:cad/tapa`.
- Suave: `reservar` da la reserva si está libre o ya es tuya; si es de otro, `reserva` sin cambio (y el evento dice de quién). Cada `carga` o `presencia` del dueño la renueva; se suelta sola a los **120 s** sin actividad (`OpcionesSala.ReservaInactiva`), con `soltar` o al expulsar al dueño. **No** se suelta al caerse la conexión: así quien vuelve antes de los 120 s manda sus cargas pendientes sobre lo que tenía reservado.
- Qué exige reserva y qué significa (`proyecto` excluye todo, por ejemplo) lo decide la app al llamar a `EnviarAsync` con `reservas` y en su `validar`.
- Hasta 64 reservas por miembro; `reservar` ≤ 5/s.

### 5.5 Presencia

Objeto opaco ≤ 4 KiB (cabezal y selección en Rodaje, cursor 3D en Unicomp). El anfitrión guarda el último de cada uno y lo reparte a los demás con `de`. **≤ 20/s por miembro**: lo que pasa de ahí se descarta en silencio (no cierra). No entra en el orden ni en el registro.

### 5.6 Épocas, traspaso y reconexión

- **Reconexión:** el invitado que pierde al anfitrión reintenta solo a 1, 2, 4… hasta 30 s, con `unirse` por `anfitrion` (ya anclado) y `sincronizar` con su `ultimoSeq`. Las cargas sin respuesta se reenvían con su mismo `id`. Lo que la app guarde mientras tanto (el registro de Rodaje, el de Unicomp) es suyo.
- **Traspaso con el anfitrión conectado:** `TraspasarAsync(clave)` ofrece su historial de reposición (los últimos `orden`) como blob y manda `traspaso` (época + 1) con el `seq` actual y la huella de ese blob; el nuevo lo baja, abre `dathinel-red` como anfitrión con los mismos anclados y `epoca` nueva; los demás se reconectan a su clave. El viejo pasa a invitado.
- **Anfitrión caído:** solo el `suplente` de la última `bienvenida`/`miembros` (el invitado conectado más antiguo con rol editar que ya dio sus direcciones en `sincronizar`; `bienvenida` y `miembros` llevan `suplente {clave, direcciones}`) puede `TomarAnfitrionAsync()`, y solo tras **60 s seguidos** sin anfitrión (`OpcionesSala.EsperaSuplente`), con `ascender` en su mismo proceso: abre con `epoca + 1`, el `seq` que tenía y los ids de los `orden` que recibió. Los demás lo buscan por su clave y sus `direcciones` **solo si ellos también perdieron al anfitrión**; quien sigue conectado al anfitrión de siempre se queda con él. Así una caída de un solo invitado no derroca a un anfitrión vivo: el suplente aislado se queda solo en su época, y al volver a ver al anfitrión de verdad la app lo trata como «dos anfitriones». Aquí no hay historial que pasar: el suplente repone desde lo que él recibió, y a quien le falte algo más viejo le llega `desfasado` y la app manda la instantánea con `directo` y blobs.
- **Dos anfitriones:** manda la época mayor; a igualdad, la clave menor. Un anfitrión que recibe un `sincronizar` con `epoca` mayor que la suya, o un invitado que ve un anfitrión con `epoca` menor, lanza `Superada(epoca, anfitrion)`: la app cierra esa sala, entra en la otra y trata lo confirmado desde la separación como «Cambios que no entraron» (Rodaje) o por su unión (Unicomp).

### 5.7 Blobs (canal aparte)

Para lo que no cabe en un marco: el proyecto inicial, parches grandes, proxies, mallas y `.glb`. Por **huella** SHA-256 y tamaño, nunca por nombre.

1. Quien lo tiene lo ofrece a su proceso (`OfrecerBlobsAsync([(ruta, huella, tamano), …])`, en lote) y anuncia la huella con un `directo` o dentro de una carga.
2. Quien lo necesita lo pide (`PedirBlobAsync(de, huella, tamano, destino, tope)`); el destino lo elige la app dentro de su carpeta de sala. Un `tamano` mayor que `tope` se rechaza antes de abrir el stream.
3. `dathinel-red` lo baja por un stream propio, separado del canal ordenado, comprueba tamaño y SHA-256 y renombra; la app vuelve a comprobarlo antes de abrirlo con su lector normal.

Un par solo puede bajar lo que el otro ofreció; el que manda no elige ni ve rutas. El tamaño máximo lo pone quien pide (Rodaje 64 MiB para el proyecto; Unicomp 500 MB el proyecto y 100 MB una malla).

### 5.8 Límites de la sala

| Qué | Límite |
|---|---|
| Miembros | 16 en la capa; cada app pone su tope (Unicomp 8) |
| `carga.datos` | 900 KiB; más grande, va como blob y la carga lleva la huella |
| Cargas | **20/s por miembro**, ráfaga de 40: lo que pasa → `rechazado` `ritmo`; 60 rechazos por ritmo en 3 s (seguir al doble del ritmo) → expulsión. Las apps mandan **una carga por paso de deshacer** (arrastrar, pegar o importar es una sola), y lo continuo (texto, arrastres) agrupado cada ~100 ms; la presencia va aparte |
| Directos | 900 KiB; cuentan en el mismo ritmo que las cargas |
| `validar` | `TopeValidar`, 30 s por defecto; al vencer, `rechazado` `tiempo` |
| Reservas | 64 por miembro, `reservar` ≤ 5/s, 120 s sin actividad |
| Presencia | 4 KiB, 20/s (lo de más se descarta) |
| Ids recordados | por miembro, 8192 aceptados y 1024 rechazados |
| Historial para reconexión | 4096 `orden` o 64 MiB |
| Alias | 1–40 caracteres, sin caracteres de control |

## 6. Lo que pone cada app encima

| | Rodaje | Unicomp |
|---|---|---|
| `datos` de una carga | Parche por elemento (D-067 §3.2), sin rutas | Operación sobre `Uid` (§8.6.3) |
| `validar` | `WireSanitizer` + parche sobre copia candidata + `ModelValidator.Validate(Project)` | Operación sobre copia candidata + `CircuitViews` + regeneración del CAD tocado + comprobaciones |
| `directo` | Pedir proxies, «Exportar en el anfitrión» | Manifiesto de la instantánea, pedir huellas, anunciar blobs |
| Reservas | `pista:<id>`, `proyecto` | `seccion:pieza/<uid>`, `seccion:circuito`, `codigo:<uid>/<ruta normalizada>` |
| Presencia | Cabezal y selección | Cursor 3D y selección |
| Blobs | `.rodaje` inicial, parches > 900 KiB, LUT, máscaras, análisis, proxies (fase 4, no por el relé de n0) | `.unicompdir` por bloques, mallas, `.glb` |
| Lo que guarda | `<proyecto>.sala\registro.jsonl`, claves ancladas | `equipo/miembros.json`, registro de operaciones |
| Conflictos al volver | «Cambios que no entraron» | Unión semántica de E3 |
| Enlace | `rodaje://unirse/<billete>` | `unicomp://unirse/<billete>` |

## 7. Pruebas que fija este contrato

- **Proceso:** dos `dathinel-red` en el mismo equipo (Windows, y Linux y macOS en la CI): abrir, billete, unirse, aceptar y rechazar, 10 000 marcos en orden, cerrar; billete caducado, gastado, revocado o de otra sala no entra; otra app o versión no entra y dice por qué; marco de más de 960 KiB y ritmo de más cortan la conexión sin tumbar el proceso.
- **Sala (hostiles, en C#):** mensajes enormes, ritmo, claves no ancladas, cargas de un rol `mirar`, reservas ajenas, `orden` de época vieja, ids repetidos, un `validar` que no contesta (sale `tiempo` y la sala sigue), un blob pedido con `tamano` mayor que el tope de quien pide (se rechaza antes de abrir el stream), una carga con rutas (`C:\…`, `\\equipo\x`, `file:`, `..`) que la sala pasa intacta a `validar` sin tocar el disco, claves de reserva con `..` o `\`, JSON truncado o con sustitutos sueltos. Criterio: nada tumba al anfitrión, nada escribe fuera de la carpeta de sala y el estado no cambia.

## 8. Fuera de esta versión

Malla entre invitados; más de una sala por proceso; cifrado de los blobs en reposo; NAT64 o IPv6 solo; cualquier cosa que comparta una red IP (Tailscale, VPN). Visor a lo Parsec y salas mixtas Rodaje-Unicomp: no (Daniel, 15-nov).

## 9. Revisiones (16-nov-2026)

**Sesión de Unicomp** (buzón, «Sesión de Unicomp: revisión de la API de sala»): la adopta y cambia su diseño a `dathinel-red` y `dathinel/sala/1`. Incorporado: mensaje `directo` fuera del orden, `validar` asíncrono con `TopeValidar` y rechazo `tiempo`, `blob-ofrecer` en lista y archivo ofrecido inmutable, historial de reposición en el traspaso, una carga por paso de deshacer (§5.8), y la prueba hostil del blob mayor que el tope.

**Gemini** (`gemini-3.8-flash-high`, lectura, 14 hallazgos). Incorporado: el suplente solo toma la sala tras 60 s sin anfitrión y solo lo siguen quienes también lo perdieron (1); descargas de blobs sin ocupar una llamada de la tubería, con eventos `blob-avance` y `blob-fin` (2); lote de `recibir` cortado antes de 1000 KiB (3); límite de billetes falsos por IP y sin ocupar huecos de solicitud (4); billete de un uso en trámite desde que se presenta y gastado también al rechazar (5); direcciones del suplente en `bienvenida` (6); `directo` y `validar` asíncrono (7, 8, ya pedidos por Unicomp); tabla de ids rehecha desde los `orden` en el anfitrión nuevo (9); `carpeta` de sala y `destino` y `ruta` confinados a ella en el proceso (10); `bienvenida` como respuesta a cada `sincronizar` (11); `revocar` de un solo billete (12); ids recordados por miembro (13); códigos QUIC al cortar un blob (14).

Descartado: consenso entre miembros o arrendamiento firmado para elegir anfitrión (es mucho código para un grupo pequeño; D-067 §12 ya lo descartó). Sus preguntas: el relé (n0 por defecto, propio opcional) y las salas partidas ya las decidió Daniel el 15-nov; subir `MaxConcurrentCalls` no hace falta con las descargas por eventos; la licencia MIT del protocolo extraído la aprobó Daniel con el repositorio (D-067 §10.1), y su autoría es solo de Daniel y Claude (`git log` de `src/Rodaje.Modules.Protocol`).
