# API de Sala, versión 1

Contrato de `dathinel-red` (proceso de Rust) y de `Dathinel.Sala` (biblioteca de C#), la capa común de trabajo en grupo de Rodaje y Unicomp. Parte de D-067 de Rodaje (`docs/propuestas/colaboracion.md` §2 y §3.4) y de D-017 de Unicomp (`docs/diseno-equipo-animacion-plataformas.md` §8.6). Lo que no está aquí es de cada app.

Estado: v1 implementada (bloque 2) y corregida tras la revisión independiente (bloque 3, 17-nov-2026). Solo reglas vigentes: la historia de las revisiones está en `docs/historia.md`.

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
- Se abre como cualquier módulo: la app lo lanza con `ModuleProcess` (o `ModuleManager`) y le habla el **protocolo de módulos v1 sin cambios** (`Rodaje/docs/protocolo-modulos.md`): tubería con nombre `CurrentUserOnly` en Windows, socket Unix 0700 en Linux y macOS (`--rodaje-pipe <nombre o ruta>`), marco de 4 bytes + JSON de hasta 1 MiB, `hello`/`welcome`, `invoke`/`result`/`error`, `cancel`, `ping`. El proceso comprueba que lo abrió el usuario actual (lo hace la tubería) y se cierra si muere la app: al cerrarse la tubería en todos los sistemas, y además con `getppid` en Unix. El Job Object de Windows, si la app lo quiere, lo pone ella (Sala no lo crea).
- `welcome`: `module.id` = `dathinel-red`, `module.version` = la del crate, y las funciones de §2.
- Un proceso = **una sala**, como anfitrión o como invitado. Arranca con `abrir-sala` o `unirse` y termina con `cerrar` o `shutdown`. No escucha nada antes de la primera de esas dos llamadas (nada abierto por defecto).
- **Llamadas a la vez:** hasta 4 (`busy` después). Solo hay dos llamadas largas, `recibir` (siempre) y `unirse` (al entrar): las descargas de blobs **no** ocupan una llamada, empiezan con `blob-pedir`, que vuelve enseguida, y avisan por eventos. Así quedan libres al menos dos para `enviar` y lo demás. `ping` se contesta siempre, también durante un `recibir` largo.
- Errores de llamada (`error` con `id`, la conexión con la app sigue): los códigos del protocolo más estos: `no-abierta` (no hay sala), `ya-abierta`, `rechazado` (el anfitrión dijo que no), `version` (otra app u otra versión: el mensaje dice cuál), `billete-invalido`, `sin-conexion`, `ocupado-red` (cola de salida llena: reintentar, contrapresión), `desconocido` (clave que no está en la sala), `huella` (un blob no coincide), `demasiado-grande`.

## 2. Funciones de `dathinel-red`

Parámetros y resultados en JSON (`parameters` y `value` del `invoke`). Claves: 64 hexadecimales en minúscula (clave pública Ed25519 de iroh, el `EndpointId`). Tiempos en segundos Unix.

| Función | Lado | Parámetros | Resultado |
|---|---|---|---|
| `abrir-sala` | anfitrión | `identidad` (64 hex, clave **privada**), `app`, `appVersion`, `red` (§2.1), `carpeta` (ruta absoluta de la carpeta de sala de la app), `anclados` (lista de claves que entran sin billete) | `clave` (la pública), `direcciones`, `red` (§2.3). Con relé, espera hasta 5 s a que el equipo lo contacte; si no llega, sigue abierta y `red.releConectado` es `false` (la app avisa) |
| `billete` | anfitrión | `caducaEn` (segundos, 5–604 800; 86 400 por defecto), `unUso` (sí por defecto) | `billete` (texto, §4), `id`, `releConectado` |
| `revocar` | anfitrión | `id`? | — Sin `id`, invalida **todos** los billetes vivos; con `id`, solo ese. Los miembros ya anclados siguen. Retira las solicitudes afectadas con `solicitud-retirada` (`revocado`); el invitado recibe `adios` `billete-invalido` |
| `aceptar` | anfitrión | `clave` | — La clave pasa a anclada mientras viva el proceso; la app la guarda para la próxima vez |
| `rechazar` | anfitrión | `clave`, `motivo`? | — |
| `expulsar` | anfitrión | `clave`, `motivo`?, `vetar` (sí por defecto) | — Cierra su conexión; con `vetar`, la saca de anclados y ningún billete la vuelve a dejar entrar en esta sala |
| `unirse` | invitado | `identidad`, `app`, `appVersion`, `alias`, `red`, `carpeta`, y **o** `billete` **o** `anfitrion` + `direcciones`? (para volver sin billete, ya anclado) | `anfitrion` (clave), `anfitrionVersion`, `via` (`directo` o `rele`), `rele` (URL del relé si `via` es `rele`), `conexionMs` (de empezar a marcar a estar admitido, sin contar la espera de la aprobación), `intentos` (cuántas direcciones o caminos probó), `anfitrionDirecciones`, `direcciones` (las propias). **Llamada larga:** espera la aprobación hasta 10 min; se cancela con `cancel`. Un invitado puede volver a llamarla en el mismo proceso (reconexión o cambio de anfitrión): conserva su endpoint y sus direcciones |
| `ascender` | invitado | `anclados` | `clave`, `direcciones`. El invitado pasa a anfitrión **con el mismo endpoint** (las direcciones que ya conocen los demás): corta con el anfitrión anterior (cierre `traspaso`) y empieza a aceptar conexiones. Lo usan el traspaso y el suplente |
| `enviar` | los dos | `a` (lista de claves; vacía o ausente = todos los conectados; en el invitado solo vale el anfitrión), `datos` (objeto JSON, la carga de la sala, ≤ 960 KiB serializado y profundidad ≤ 28) | `encolados` (cuántos destinos) y `llenos` (claves que se saltaron). A destinos concretos es todo o nada (`ocupado-red`); en una difusión se salta a quien tiene la cola llena, que repone lo perdido al ver el hueco y `sincronizar` |
| `recibir` | los dos | `max` (eventos, 1–1024) | `eventos` (§2.2). **Llamada larga:** vuelve en cuanto hay algo o vacía a los 25 s; una sola a la vez. El lote se corta antes de pasar de 1000 KiB serializado: un `mensaje` de 960 KiB va solo, y lo que no cabe sale en la siguiente |
| `blob-ofrecer` | los dos | `blobs`: lista (1–1024) de `{huella (SHA-256, 64 hex), tamano, ruta}` (archivos **locales** de quien ofrece, dentro de `carpeta`) | — El proceso los sirve a los pares que los pidan mientras estén ofrecidos. El archivo **no debe cambiar** mientras esté ofrecido: se lee al servirlo, y quien pide comprueba la huella (si cambió, la descarga falla con `huella`). Lo seguro es ofrecer copias de un almacén por contenido dentro de la carpeta de sala |
| `blob-retirar` | los dos | `huellas` (lista) | — |
| `blob-pedir` | los dos | `de` (clave), `huella`, `tamano` (exacto; > 4 GiB se rechaza sin abrir el stream), `destino` (ruta local que elige la app; tiene que quedar dentro de `carpeta` una vez resuelta, o `invalid-parameters`) | `{"intento": <u64>}`. Contador del proceso desde 1. Vuelve enseguida; `blob-avance` (≤ 4/s) y `blob-fin` identifican el intento. Máximo 4 descargas por par y 16 en total; destino resuelto ya en uso → `ocupado`. Crea `<destino>.<16 hex aleatorios>.parte` en exclusiva (`create_new`, también rechaza enlaces). Comprueba que el temporal resuelto sigue dentro de `carpeta`; si no, lo borra y da `disco`. Comprueba tamaño y SHA-256; si no coinciden, borra y da `huella`. Antes de renombrar comprueba otra vez el padre del destino; si queda fuera, borra y da `disco` |
| `blob-cancelar` | los dos | `intento` (u64) | — Señala la cancelación cooperativa. La tarea borra su `.parte` y emite un solo `blob-fin` con `error: "cancelado"`; no se aborta. Un intento ya terminado no hace nada |
| `estado` | los dos | — | El diagnóstico de §2.3: `clave`, `direcciones`, `rol`, `red` (cómo salió y si el relé está conectado), `pares` (camino, latencia, pérdida, versiones, actividad) y `ultimaCaida` |
| `cerrar` | los dos | `motivo`? | — Cancela cooperativamente las descargas, espera su limpieza y su `blob-fin` (`cancelado`), cierra todo y deja el proceso listo para salir |

### 2.1 `red`

```json
{"rele":"n0","mdns":false,"soloRele":false}
```

- `rele`: `"n0"` (relés públicos de n0, por defecto; Daniel, 15-nov), `"ninguno"` (solo red local o direcciones directas; la fase 1 de Rodaje), o una URL `https://…` de un relé propio (Preferencias de cada app). Con relé, el equipo mantiene una conexión al relé de su elección (el «relé de casa») y lo usa para que los demás lo encuentren y para el camino de respaldo; se conecta **directo** en cuanto la perforación de NAT lo consigue (QUIC cambia de camino sin cortar) y vuelve al relé si el directo se cae.
- `mdns`: descubrimiento en la red local con `iroh-mdns-address-lookup` 0.6.1 (de n0, MIT o Apache-2.0), servicio `dathinel-sala`. Encendido, el equipo **anuncia su clave en la red local** a quien escuche; sirve para entrar o volver sin direcciones. Apagado por defecto; Rodaje lo enciende en su fase 1.
- `soloRele`: no abrir ningún transporte IP: ni se anuncia ni se prueba una dirección directa y los pares no ven la IP de este equipo (sí la ve el operador del relé). Verificado con iroh 1.4.1 (`clear_ip_transports`). Con `rele: "ninguno"` la función falla con `invalid-parameters`. Cuesta latencia (todo pasa por el relé) y el relé de n0 tiene límite de tráfico: no es para proxies ni blobs grandes.
- El billete que crea un anfitrión lleva su relé de casa (§4) y el invitado marca directamente a él; el invitado usa **su** `red.rele` como relé de casa propio, no el del billete.

### 2.3 Diagnóstico: resultado de `estado`

Para el panel de conexión de la app (Rodaje B11) y para leerlo por teléfono. Lo calcula `dathinel-red` de lo que iroh ya sabe de cada conexión; no mide nada por su cuenta ni manda tráfico extra. Cada campo está siempre presente (`null` si no se sabe). Tiempos en segundos Unix; ningún valor lleva rutas ni la IP de nadie.

```json
{
  "clave": "<64 hex>", "direcciones": ["…"], "rol": "anfitrion",
  "red": {"rele": "n0", "releUrl": "https://…", "releConectado": true, "soloRele": false, "mdns": false},
  "conexion": {"fase": "listo", "intento": 0},
  "pares": [{
    "clave": "<64 hex>", "alias": "Luis",
    "via": "rele", "rele": "https://…",
    "rttMs": 118, "perdidosPct": 0.4, "paquetesEnviados": 5120, "paquetesPerdidos": 21,
    "enviadosB": 1048576, "recibidosB": 524288,
    "appVersion": "1.0.4", "protocolo": 1,
    "conectadoDesde": 1763400000, "ultimaActividad": 1763400123, "cambiosDeCamino": 1
  }],
  "ultimaCaida": {"clave": "<64 hex>", "motivo": "red", "en": 1763399000}
}
```

- `red`: lo que se pidió y lo que hay. `releUrl` es el relé de casa en uso (`null` con `rele: "ninguno"` o sin conexión); `releConectado` es `false` mientras el equipo no haya contactado con él: sin relé, desde fuera de la red local no entra nadie.
- `conexion`: la fase de `unirse` (`buscando`, `conectando` con su `intento` de 1 a 3, `esperando-aprobacion`, `sincronizando`; `listo` en el anfitrión y fuera de una entrada). **`estado` se puede llamar mientras `unirse` espera**: el proceso ya tiene su clave (`clave`) y su fase, para enseñar las cuatro palabras al invitado y una barra de pasos. `releConectado` también es `true` si el camino en uso es un relé aunque el relé de casa aún no haya contestado.
- `via`: camino **elegido** ahora, `directo` o `rele`; `rele` es la URL del relé de ese camino (`null` si es directo). `cambiosDeCamino`: veces que el camino elegido cambió desde que el par entró.
- `rttMs`: ida y vuelta del camino elegido que mide QUIC (con las muestras del propio tráfico, no un `ping` aparte; `null` si aún no hay). `perdidosPct`: paquetes perdidos entre enviados de toda la conexión, con un decimal (`null` con menos de 100 enviados).
- `appVersion` y `protocolo` son los del `hola` del par (en el anfitrión) o los del `admitido` (en el invitado). Con la comprobación de §3.1, un par de otra versión no llega a estar en `pares`.
- `ultimaActividad`: el último marco del canal ordenado recibido de ese par (cuenta también la presencia).
- `ultimaCaida`: la última conexión admitida que se cortó en este proceso, con su `motivo` (los de `sale`: `cerro`, `red`, `ritmo`, `marco`, `expulsado`). `null` si no hubo ninguna. Las apps lo traducen a texto con `Textos.MotivoDeSalida` (§5.9).

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
- `appVersion` distinta → `adios` con `version`, y el mensaje dice **a quién le toca actualizar** (a quien tenga la versión más baja, por comparación numérica por partes; si no se puede comparar, a los dos): «Tu Rodaje es el 1.0.3 y esta sala usa el 1.0.4: actualiza tu Rodaje.» o «Esta sala usa el 1.0.3 y tu Rodaje es el 1.0.4: pide a quien te invitó que actualice el suyo.». El `adios` lleva además `appVersion` (la del anfitrión) y `sala`; `admitido` lleva los mismos dos campos. `sala` (protocolo) distinta → el mismo `adios` y el mismo reparto. Nunca un cierre mudo: si el anfitrión no llega a decirlo, el invitado lo dice con el motivo del cierre.
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
- `rele`: el **relé de casa** del anfitrión al crear el billete: su URL propia o, con `n0`, la que eligió iroh (n0 tiene varios por región y depende de la red). El invitado marca esa URL y además puede encontrarlo por el descubrimiento de n0. Si el anfitrión aún no contactó con ningún relé, `rele` falta y el resultado de `billete` trae `releConectado: false` para que la app avise.
- Sin relé (`rele` ausente) solo sirve con `direcciones`, mDNS o el descubrimiento de n0: es el de la fase 1 de Rodaje.

## 5. `Dathinel.Sala` (C#)

Biblioteca `net10.0`, compatible con AOT y sin reflexión (System.Text.Json con generador; la CI publica y ejecuta con Native AOT el consumidor mínimo `ejemplos/Consumidor`). Abre `dathinel-red` con el protocolo de módulos y hace la sala encima. El cliente del protocolo es `Rodaje.Modules.Protocol`, que pasa a este repositorio (la extracción que D-031 dejó para el segundo programa; se queda con su nombre para que Rodaje y sus módulos solo cambien la ruta del proyecto en el bloque de integración). Los mensajes de sala viajan como `datos` de `enviar` y `mensaje`.

### 5.1 Lo que ve la app

```csharp
string identidad = Identidad.CargarOCrear(archivoIdentidad);   // DPAPI en Windows, archivo 0600 en Unix
OpcionesSala opciones = new("rodaje", "1.0.4", "Ana", rutaDathinelRed, carpetaDeSala)
{
    Red = Red.N0,
    TopeValidar = TimeSpan.FromSeconds(30),
};

await using Sala sala = await Sala.HospedarAsync(identidad, opciones, anclados, validar);
string enlace = "rodaje://unirse/" + await sala.NuevoBilleteAsync(TimeSpan.FromHours(24), unUso: true);
sala.SolicitudRecibida += s => _ = sala.AceptarAsync(s.Clave, Rol.Editar);   // «Luis quiere entrar · río-mesa-luna-ocho» (s.Huella)

await using Sala invitado = await Sala.UnirseAsync(identidad, opciones, billete,
    suscribir: s => s.Ordenada += o => { /* aplicar o.Datos en orden o.Seq */ });
Guid id = await invitado.EnviarAsync(datos, reservas: ["pista:a2"]);   // la respuesta llega como Ordenada o Rechazada
```

`ejemplos/Consumidor/Program.cs` es este mismo uso, compilado y ejecutado en la CI.

| Miembro | Qué hace |
|---|---|
| `HospedarAsync(identidad, opciones, anclados, validar, epoca = 1, seq = 0, suscribir)` | Abre la sala. `anclados`: los miembros que la app guardó (clave, alias, rol; menos que `MaxMiembros`). `validar`: `Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>`, §5.3 |
| `UnirseAsync(identidad, opciones, billete \| anfitrion + direcciones, epoca = 0, ultimoSeq = 0, suscribir, validar, alEsperar)` | Entra; vuelve tras la aprobación y la `bienvenida`. `suscribir(sala)` se llama antes de conectar, para no perder los `orden` que reponen justo después. `validar` solo se usa si llega a anfitrión (traspaso o suplente). `alEsperar(clave)` se llama con la clave pública propia en cuanto el proceso de red la tiene, mientras se espera la aprobación (hasta 10 min): la app enseña las cuatro palabras (`Huella.Palabras`) para dictárselas al anfitrión |
| `NuevoBilleteAsync`, `RevocarAsync`, `AceptarAsync(clave, rol)`, `RechazarAsync`, `ExpulsarAsync`, `CambiarRolAsync(clave, rol)` | Anfitrión. `CambiarRolAsync` pasa por el mismo turno que el orden; a `Mirar` suelta sus reservas |
| `EnviarAsync(datos, reservas)` | Propone una carga y vuelve con su id. En el anfitrión pasa por el mismo `validar` sin red. Sin anfitrión queda pendiente (`Pendientes`) y sale al volver |
| `EnviarDirectoAsync(datos, a)` | Mensaje de la app **fuera del orden**: ni se valida ni se numera ni entra en el historial; a un miembro o a todos (en el invitado, solo al anfitrión). Para el manifiesto de la instantánea, pedir huellas o anunciar blobs |
| `ReservarAsync(clave)`, `SoltarAsync(clave)`, `Reservas` | §5.4 |
| `PresenciaAsync(datos)` | §5.5 |
| `OfrecerBlobsAsync`, `RetirarBlobsAsync`, `PedirBlobAsync` | §5.7 |
| `TraspasarAsync(clave)`, `TomarAnfitrionAsync()`, `ConfirmarInstantaneaAsync(epoca, seq)` | §5.6 |
| `Miembros`, `Conectados`, `Clave`, `Anfitrion`, `Suplente`, `Epoca`, `Seq`, `Rol`, `EsAnfitrion`, `Direcciones`, `Pendientes` | Estado |
| Eventos | `SolicitudRecibida`, `SolicitudRetirada`, `MiembroEntra`, `MiembroSale`, `MiembrosCambiaron`, `Ordenada`, `Rechazada`, `DirectoRecibido`, `ReservaCambia`, `PresenciaRecibida`, `BlobAvance`, `Desfasada`, `Superada`, `Desconectada`, `Reconectada`, `Cerrada` |

`Identidad.CargarOCrear(archivo)` guarda la clave privada: en Windows cifrada con DPAPI del usuario actual; en Linux y macOS en un archivo 0600 (se rechaza si el grupo u otros pueden leerlo), sin llavero del sistema por ahora. Publica sin sobrescribir: si dos instancias la crean a la vez, las dos devuelven la que quedó guardada. Un archivo dañado da `SalaException` `identidad` y nunca se pisa.

`Huella.Palabras(clave)` da las cuatro palabras: SHA-256 de los 32 bytes de la clave, 44 primeros bits en cuatro grupos de 11, cada uno un índice en una lista propia de 2048 palabras en español. Encontrar otra clave con las mismas cuatro palabras cuesta del orden de 2⁴⁴ claves; la identidad sigue siendo la clave anclada, la huella es para compararla en voz alta. El color de cada miembro no viaja: cada app lo saca de su clave.

### 5.2 Mensajes de sala

Todos con `type` y la `epoca` de quien manda **ahora** (la pone `Dathinel.Sala` al enviar). Tamaños medidos como salen por la tubería local: el JSON con lo no ASCII escapado. Cada lado lee y comprueba el mensaje entero (tipos, rangos, claves de 64 hex, roles `mirar`/`editar`, alias, pertenencia, tamaños) antes de cambiar nada: un campo mal y se descarta entero.

| `type` | Sentido | Campos |
|---|---|---|
| `sincronizar` | invitado → anfitrión, al entrar y al volver | `ultimoSeq`, `direcciones`, `anfitrion`? (el de su época, para desempatar dos anfitriones) |
| `bienvenida` | anfitrión → invitado, respuesta a **cada** `sincronizar` | `seq`, `miembros` `[{clave, alias, rol}]` (≤ 16), `reservas`, `suplente`? `{clave, direcciones}`. Después van los `orden` que le faltan, de `ultimoSeq + 1` a `seq`, y la última presencia de los demás |
| `carga` | invitado → anfitrión, dirigida | `id` (GUID del autor), `datos` (objeto, ≤ 900 KiB, ≤ 27 niveles), `reservas` (≤ 64) |
| `orden` | anfitrión → todos, el autor incluido | `seq`, `id`, `autor`, `datos`, `aceptada` (época en que se aceptó; la `epoca` del envoltorio puede ser mayor si lo repone un anfitrión posterior) |
| `ya-ordenada` | anfitrión → autor | `id`, `seq`: respuesta a un repetido cuyo `orden` ya no se guarda (§5.3) |
| `rechazado` | anfitrión → autor | `id`, `motivo` (código, ≤ 64), `detalle`? (≤ 4096) |
| `desfasado` | anfitrión → invitado | `desde` (el `seq` más viejo que guarda): la app pide el estado entero y confirma con `ConfirmarInstantaneaAsync` |
| `seq` | anfitrión → invitado | `seq`: el invitado se saltó órdenes con la cola llena; si va por detrás, `sincronizar` |
| `reservar`, `soltar` | invitado → anfitrión | `clave` |
| `reserva` | anfitrión → todos | `clave`, `de` (clave o nada si quedó libre) |
| `directo` | en los dos sentidos (el invitado, solo al anfitrión) | `datos` (objeto, ≤ 900 KiB). No se reenvía |
| `presencia` | en los dos sentidos | `datos` (objeto, ≤ 4 KiB); del anfitrión lleva además `de` |
| `miembros` | anfitrión → todos | la lista entera, el suplente y `conectados` (claves de los miembros conectados ahora, el anfitrión incluido; opcional: sin él, todos cuentan como conectados), al cambiar (entrada, salida, expulsión, cambio de rol) |
| `traspaso-oferta` | anfitrión → sucesor | `nueva` (época), `seq`, `historial` `{huella, tamano}` del blob con el historial de reposición |
| `traspaso-listo`, `traspaso-fallo` | sucesor → anfitrión | `nueva`, `seq` / `motivo` |
| `traspaso-cancelado` | anfitrión → sucesor | `nueva` |
| `traspaso` | anfitrión → todos, tras el `traspaso-listo` | `a` (clave), `nueva`, `seq`, `direcciones` |
| `adios` | cualquiera | `motivo` |

Nunca hay un mensaje para leer, listar o abrir archivos, ni para ejecutar nada. La única ruta que existe en la API es la que la app le da **a su propio** proceso (`ruta`, `destino`), y nunca viaja.

### 5.3 Orden central

- El anfitrión tiene `epoca` y `seq`. Por cada `carga`: (1) ritmo (§5.8) y tamaño; (2) cola: como mucho 64 cargas y 16 MiB por miembro y 64 MiB en total esperando, lo demás sale `rechazado` `ocupado` (recuperable: reintentar); (3) el id ya visto → la misma respuesta (`orden` o `rechazado`, o `ya-ordenada` si el orden ya no se guarda); (4) rol `mirar` → `rol`; (5) alguna reserva de `reservas` no es del autor → `reserva`; (6) `validar` de la app → `rechazado` con su motivo, o `tiempo` si no contesta en `TopeValidar`; (7) **otra vez** pertenencia, rol y reservas: si cambiaron mientras se validaba (expulsión, `CambiarRolAsync`, reserva suelta o caducada), no entra; si no, `seq + 1` y `orden` a todos.
- `validar` corre de a una, en el orden de llegada, en otro hilo y con el turno de `_mando` tomado (nunca a la vez que un traspaso o un cambio de rol). **Contrato** con Rodaje y Unicomp (Daniel, 17-nov): valida sobre una copia, devuelve pronto el `ValueTask`, coopera con la cancelación del token y no aplica nada hasta recibir el `Ordenado`. Si lo incumple, `tiempo` sale igual a su hora, pero la ejecución abandonada sigue viva hasta que acabe: Sala no la aísla en otro proceso.
- El invitado aplica los `orden` por `seq` creciente; uno con `seq` repetido se ignora, un hueco hace pedir `sincronizar` otra vez (salvo tras `desfasado`, hasta que la app confirme su instantánea). Un mensaje con `epoca` menor que la conocida se descarta; una `bienvenida` así lanza `Superada`.
- Historial para reponer: los últimos 4096 `orden` o 64 MiB, en anfitrión e invitados; más atrás, `desfasado`.
- Deduplicación (Daniel, 17-nov): se guardan los ids, por miembro, de sus últimas 8192 cargas aceptadas (solo el `seq`) y 1024 rechazadas (la respuesta). Los `orden` que salen de la memoria pasan a un respaldo en disco (`<carpeta>/.sala/respaldo-*.jsonl`), con retención explícita: dos archivos que rotan, como mucho 1 GiB y 131 072 órdenes, borrados al cerrar la sala. Un repetido cuyo orden ya no está en ninguno recibe `ya-ordenada`.
- Como cada `orden` lleva su `id` y su autor, cualquier miembro puede rehacer la tabla de ids aceptados con lo que recibió: es lo que hace un anfitrión nuevo (§5.6).
- **Predicción local** (aplicar antes de que llegue el `orden`, y retirar al recibir `rechazado`) es de la app.

### 5.4 Reservas

- Clave de reserva: texto de 1 a 128 caracteres `[a-z0-9:/._-]` (anclado al principio y al final del texto: un salto de línea no cuela), sin `..`, sin empezar por `/` ni terminar en `/`. Es una **etiqueta**, nunca una ruta. Rodaje usa `pista:a2` y `proyecto`; Unicomp `seccion:cad/tapa`.
- Suave: `reservar` da la reserva si está libre o ya es tuya; si es de otro, `reserva` sin cambio. Se suelta sola a los **120 s sin actividad del dueño** (`OpcionesSala.ReservaInactiva`; Daniel, 17-nov): renueva **cada `carga` recibida del dueño** (entre o no) y cada `presencia`. También con `soltar`, al expulsar al dueño o al pasarlo a `mirar`. **No** se suelta al caerse la conexión. Reloj monotónico: cambiar la hora del equipo no la adelanta.
- Qué exige reserva y qué significa lo decide la app al llamar a `EnviarAsync` con `reservas` y en su `validar`.
- Hasta 64 reservas por miembro; `reservar` ≤ 5/s.

### 5.5 Presencia

Objeto opaco ≤ 4 KiB (cabezal y selección en Rodaje, cursor 3D en Unicomp). El anfitrión guarda **la última de cada miembro conectado** (se borra al salir) y la reparte a los demás con `de`, también a quien entra, tras su `bienvenida`. **≤ 20/s por miembro**: lo de más se descarta en silencio. No entra en el orden ni en el registro.

### 5.6 Épocas, traspaso y reconexión

- **Turno único (`_mando`):** ordenar una carga, traspasar, ascender, cada intento de reconexión y cambiar un rol pasan de uno en uno. Hay un solo ordenador en toda la vida de la sala (solo ordena mientras este equipo es el anfitrión) y un solo bucle de reconexión.
- **Reconexión:** el invitado que pierde al anfitrión reintenta solo a 1, 2, 4… hasta 30 s, con `unirse` por `anfitrion` (ya anclado) y `sincronizar` con su `ultimoSeq`. Las cargas sin respuesta se reenvían con su mismo `id`; las que no salieron por cola llena las reintenta el vigía cada segundo.
- **Traspaso con el anfitrión conectado** (Daniel, 17-nov): `TraspasarAsync(clave)` toma el turno (ninguna validación a medias), fotografía el historial, lo ofrece como blob y manda `traspaso-oferta` al sucesor. El sucesor lo baja, lo junta con el suyo (lo que ya tenía aplicado solo se guarda; lo nuevo se aplica en orden), exige continuidad hasta el `seq` anunciado y contesta `traspaso-listo` con la época y el `seq` preparados. **Solo entonces** el anfitrión manda `traspaso` a todos y pasa a invitado; el sucesor asciende con `ascender` en su mismo proceso, rehace la tabla de ids y vuelve a ordenar sus cargas pendientes (salvo las que el historial ya trae). Si el sucesor contesta `traspaso-fallo`, se va o no confirma en 60 s, `TraspasarAsync` falla con `traspaso`, el anfitrión sigue al mando y nada cambia; desconectarse o el paso del tiempo nunca cuentan como aceptación. El blob y su archivo se retiran siempre.
- **Anfitrión caído:** solo el `suplente` de la última `bienvenida`/`miembros` (el invitado conectado más antiguo con rol editar que ya dio sus direcciones) puede `TomarAnfitrionAsync()`, y solo tras **60 s seguidos** sin anfitrión (`OpcionesSala.EsperaSuplente`, reloj monotónico): abre con `epoca + 1`, el `seq` que tenía y los ids de los `orden` que recibió. Los demás lo buscan **solo si ellos también perdieron al anfitrión**. Al salir el suplente, el anfitrión anuncia otro.
- **Instantánea tras `desfasado`** (Daniel, 17-nov): la app pide y aplica el estado entero (directo y blobs) y llama a `ConfirmarInstantaneaAsync(epoca, seq)`: la sala sigue desde ahí, sin recrearse, y pide lo que vino después.
- **Dos anfitriones:** manda la época mayor; a igualdad, la clave menor. Un anfitrión que recibe un `sincronizar` con `epoca` mayor, o igual con un `anfitrion` de clave menor, lanza `Superada(epoca, anfitrion)` y contesta solo la `bienvenida`; un invitado que recibe una `bienvenida` de época menor, o igual de un anfitrión de clave mayor que el que conocía, lanza `Superada` (si aún entraba, `UnirseAsync` falla con `superada`). La app cierra esa sala, entra en la otra y trata lo confirmado desde la separación como «Cambios que no entraron» (Rodaje) o por su unión (Unicomp).

### 5.7 Blobs (canal aparte)

Para lo que no cabe en un marco: el proyecto inicial, parches grandes, proxies, mallas y `.glb`. Por **huella** SHA-256 y tamaño, nunca por nombre.

1. Quien lo tiene lo ofrece a su proceso (`OfrecerBlobsAsync([(ruta, huella, tamano), …])`, en lote) y anuncia la huella con un `directo` o dentro de una carga.
2. Quien lo necesita lo pide (`PedirBlobAsync(de, huella, tamano, destino, tope, ct)`); el destino lo elige la app dentro de su carpeta de sala. Un `tamano` mayor que `tope` se rechaza antes de abrir el stream.
3. `dathinel-red` lo baja por un stream propio a un temporal exclusivo, comprueba tamaño y SHA-256 y renombra; la app vuelve a comprobarlo antes de abrirlo con su lector normal.

Cada descarga es un `intento`: un final tardío nunca completa otra descarga de la misma huella. Cancelar el `ct` espera, hasta 10 s, a que el proceso borre el temporal y después relanza la cancelación; cerrar la sala termina la espera con `SalaException` `cerrada`. Un par solo puede bajar lo que el otro ofreció. El tamaño máximo lo pone quien pide (Rodaje 64 MiB para el proyecto; Unicomp 500 MB el proyecto y 100 MB una malla).

### 5.8 Límites de la sala

| Qué | Límite |
|---|---|
| Miembros | 16 en la capa; cada app pone su tope (`MaxMiembros`, Unicomp 8), que vale también para los anclados |
| `carga.datos` | 900 KiB y 27 niveles; más grande, va como blob y la carga lleva la huella |
| Cargas | **20/s por miembro**, ráfaga de 40: lo que pasa → `rechazado` `ritmo`; 60 rechazos por ritmo en 3 s → expulsión. Las apps mandan **una carga por paso de deshacer**, y lo continuo agrupado cada ~100 ms |
| Cola por ordenar | 64 cargas y 16 MiB por miembro, 64 MiB en total → `ocupado` |
| Directos | 900 KiB; cuentan en el mismo ritmo que las cargas |
| `validar` | `TopeValidar`, 30 s por defecto; al vencer, `rechazado` `tiempo` |
| Reservas | 64 por miembro, `reservar` ≤ 5/s, 120 s sin actividad |
| Presencia | 4 KiB, 20/s (lo de más se descarta) |
| Ids recordados | por miembro, 8192 aceptados y 1024 rechazados |
| Historial para reconexión | 4096 `orden` o 64 MiB; respaldo en disco de 1 GiB o 131 072 órdenes |
| Alias | 1–40 caracteres, sin caracteres de control |

Los límites compartidos con `dathinel-red` (marco, profundidad, carga, presencia) están en `pruebas/conformidad/limites.json`, que prueban los dos lados.

### 5.9 Diagnóstico y textos (C#)

```csharp
Diagnostico d = await sala.DiagnosticoAsync();        // §2.3 tipado; una llamada local barata, se puede pedir cada segundo
foreach (DiagnosticoPar p in d.Pares)                  // Camino (Directo|Rele|Desconocido), Rele, Rtt, PerdidaPct, AppVersion,
    Console.WriteLine(p.Alias + ": " + p.Camino);      // Protocolo, UltimaActividad, CambiosDeCamino, EnviadosB, RecibidosB
string ficha = d.ParaTelefono();                       // «Camino: por relé · Ida y vuelta: 118 ms · Pérdida: 0,4 % · …»
string porQue = Textos.MotivoDeSalida("red");          // «Se cortó la conexión de red.»
```

- `Diagnostico`: `Clave`, `EsAnfitrion`, `Red` (`ReleSolicitado`, `ReleUrl`, `ReleConectado`, `SoloRele`, `Mdns`), `Pares`, `UltimaCaida` (`Clave`, `Motivo`, `En`) y lo que cuenta `Dathinel.Sala` por su cuenta: `Reconexiones`, `ConexionMs` (de `UnirseAsync`) y `UltimaReconexion`.
- `ParaTelefono()`: texto corto y estable, de seis líneas como mucho, para leerlo en voz alta: camino, relé (el nombre del servidor de la URL, nunca una IP), ida y vuelta, pérdida, versiones propia y del otro, hace cuánto hubo actividad y el motivo de la última caída. Con las cuatro palabras de huella, no los 64 hex; sin rutas ni IP.
- `Textos`: los avisos en español que comparten Rodaje y Unicomp (`Rele`, `Cortafuegos`, `RedSinUdp`, `MotivoDeSalida`, `ComoLeerElDiagnostico`), con el mismo fondo que `docs/internet.md`: qué ve el relé, el cortafuegos de Windows, redes que bloquean UDP y cómo leer el diagnóstico por teléfono. Las apps los muestran tal cual o los enlazan.
- `SalaException.Codigo == "version"` lleva el texto de §3.1, listo para mostrar.
- **Progreso de operaciones largas** (barra con porcentaje, «quedan N s», «último avance hace N s» y cancelar, `docs/progreso-operaciones.md` de Rodaje):
  - Entrada y reconexión: `Sala.Fase` y `Sala.FaseCambia` (`FaseConexion`: `Buscando`, `Conectando` con `Sala.Intento` de 1 a 3, `EsperandoAprobacion` (hasta 10 min), `Sincronizando`, `Conectado`, `Desconectado`; el anfitrión siempre `Conectado`). `UnirseAsync(..., alEsperar: clave => …)` da la clave propia mientras espera. `Diagnostico.Fase` e `Intento` lo repiten. Cancelar la entrada es el `CancellationToken` de `UnirseAsync`.
  - Descargas de blobs: `PedirBlobAsync(de, huella, tamano, destino, tope, progreso, ct)` (la sobrecarga sin `progreso` sigue) y el evento `Sala.BlobProgreso` dan `ProgresoBlob`: `De`, `Huella`, `Intento`, `Hecho`, `Total` (el tamaño exacto), `Fraccion`, `Inicio`, `UltimoAvance`, `BytesPorSegundo` (media desde el inicio), `Restante` (`null` sin datos para estimar o al terminar). Uno con `Hecho` 0 al empezar, otros como mucho 4 por segundo y uno con `Hecho == Total` al acabar bien. Cancelar es cancelar el `CancellationToken`: borra el temporal y lanza `OperationCanceledException`. `BlobAvance(huella, recibidos)` sigue como estaba.
  - Lo que **no** hay: avance de lo que este equipo *sirve* a otro (solo bytes totales por par en `Diagnostico`); entra con la fase 4 (proxies), que es cuando se mandan blobs grandes.

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
