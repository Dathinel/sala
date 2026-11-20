# Seguridad de Sala

Modelo de amenazas de la capa común. Los doce huecos de Unicomp (D-017 §8.6.6) pasan aquí tal cual, con el control que pone Sala y la prueba que lo fija; después, lo que añade Rodaje (D-067 §5). Lo que es de una app (validar su carga, sus lectores de archivos) queda en su propio documento.

## Activos, atacantes y límites

- **Activos:** que no se ejecute nada ajeno en el equipo; el disco y la red del usuario; el proyecto abierto.
- **Atacantes:** quien consigue un enlace; un miembro malicioso o con el equipo comprometido; un anfitrión malicioso; la red y el operador del relé; otro proceso del mismo equipo contra la tubería de `dathinel-red`.
- **Límites:** par → `dathinel-red` (la red; todo es hostil hasta el saludo); `dathinel-red` → `Dathinel.Sala` (la tubería local; el proceso es de confianza, viene del catálogo firmado); anfitrión → invitado y al revés (cada lado desconfía del otro); `Dathinel.Sala` → app (la carga es opaca y la valida la app).

## Los doce huecos de Unicomp

| # | Hueco | Control en Sala | Prueba |
|---|---|---|---|
| 1 | El enlace se filtra | Aprobación a mano de cada clave nueva; caducidad (24 h por defecto, máximo 7 días); un solo uso por defecto, en trámite desde que se presenta (nadie más lo usa a la vez) y gastado al aceptar o rechazar; `revocar` invalida uno o todos; 4 solicitudes pendientes que solo ocupan billetes válidos; 20 billetes falsos por minuto por IP | Billete caducado, gastado, revocado y de otra sala: no entran y la app no ve nada |
| 2 | Suplantación («soy Luis») | La identidad es la clave TLS de la conexión iroh, no el alias; la app ve la huella de cuatro palabras (44 bits) y la clave queda anclada; un alias conocido con otra clave se marca «otra clave» | Mismo alias, otra clave: llega como `solicitud`, no como el miembro anclado |
| 3 | Datos maliciosos de un miembro | Marco ≤ 960 KiB, JSON estricto (UTF-8, sustitutos sueltos, 28 niveles entre pares para que quepan los envoltorios de la tubería local de 32), tipos y campos validados antes de usarlos; vectores compartidos Rust-C# en `pruebas/conformidad/limites.json`; la carga llega a `validar` de la app sin que Sala la interprete | Marcos enormes, truncados, mal codificados, anidados: corta esa conexión y el anfitrión sigue |
| 4 | Acceso al disco o a la red del anfitrión | No hay mensaje para leer, listar ni abrir nada; no hay red IP virtual ni puertos para otros programas; las rutas solo existen entre la app y **su** proceso, nunca viajan, y el proceso exige que `destino` y `ruta` queden dentro de la `carpeta` de sala; un blob solo se sirve si su dueño lo ofreció por huella | Carga con `C:\…`, `\\equipo\x`, `file:` y `..`: llega intacta a `validar`, el disco no se toca; pedir una huella no ofrecida falla |
| 5 | La IP pública a la vista de los pares | Inevitable en conexión directa; opción `soloRele` (sin transportes IP: ni se anuncia ni se prueba una dirección directa; el operador del relé sí ve la IP). Verificado con iroh 1.4.1 | `ReleTests`: con `soloRele` el billete y `Direcciones` no llevan direcciones y el camino es el relé |
| 6 | Saturar al anfitrión | 16 conexiones (las apps ponen menos), 100 marcos/s por par con ráfaga de 400 en el transporte, 20 cargas/s por miembro en la sala, presencia descartada por encima de 20/s, 8 MiB/s por conexión con contrapresión, colas por ordenar de 64 cargas y 16 MiB por miembro (64 MiB en total), historial de 64 MiB con respaldo en disco acotado, expulsar | Ráfagas por encima del límite: corta o rechaza a ese par; los demás siguen recibiendo en orden |
| 7 | El operador del relé | Ve las IP, quién habla con quién, cuándo y cuánto, nunca el contenido (QUIC con TLS 1.3 de extremo a extremo); relé propio configurable; el relé público de n0 es para desarrollo, sin garantía y con límite de tráfico | `ReleTests`: captura de lo que ve el relé sin texto legible |
| 8 | Un anfitrión malicioso o equivocado | Cada invitado comprueba el mensaje entero antes de aplicar nada (marcos, `epoca`, `seq`, ids, claves, roles, tamaños) y la app vuelve a validar cada `orden`; cada `orden` lleva autor; la copia local y el historial son de la app | `orden` de época vieja o con `seq` repetido: se descarta |
| 8b | Un invitado aislado se proclama anfitrión | Solo el suplente nombrado, tras 60 s sin anfitrión; lo siguen solo quienes también lo perdieron | Suplente que pierde la red él solo: los demás siguen con el anfitrión |
| 9 | Versiones distintas | El `hola` lleva `sala`, `app` y `appVersion`; otra app (Rodaje contra Unicomp) u otra versión no entra y el `adios` dice cuál | Las dos, entre dos procesos |
| 10 | Dependencias de Rust | Toolchain y crates fijados (`rust-toolchain.toml`, `Cargo.lock`, `=1.4.1`), `cargo deny` con licencias y avisos de seguridad en la CI, nada GPL | `cargo deny check` en la CI |
| 11 | El cortafuegos de Windows pregunta | `dathinel-red` solo escucha tras `abrir-sala` o `unirse`; la app explica antes que Windows preguntará | A mano, en cada app |
| 12 | Pérdida del dispositivo de un miembro | El anfitrión lo expulsa con veto; `Identidad.CargarOCrear` guarda la clave privada con DPAPI (usuario actual) en Windows y en un archivo 0600 en Linux y macOS (rechaza uno con permisos de grupo u otros; sin llavero del sistema todavía) | Clave vetada con billete válido: no entra; `IdentidadTests` |

## Lo que añade Rodaje

| Amenaza | Control |
|---|---|
| Nada abierto por defecto | `dathinel-red` no escucha hasta `abrir-sala`/`unirse` y la app lo cierra al cerrar la sala; con `rele: ninguno` y `mdns` (fase 1 de Rodaje) solo hay red local |
| Rutas en un parche (hash NTLM por una ruta UNC) | Sala nunca abre ni resuelve nada que venga en una carga, una clave de reserva o un mensaje; las claves de reserva tienen gramática cerrada (`[a-z0-9:/._-]`, sin `..`, sin `\`). Quitar rutas del parche es de la app (`WireSanitizer` de Rodaje, lista de permitidos) |
| Blobs | Solo por huella SHA-256 y tamaño exacto; el destino lo elige quien pide, dentro de su carpeta; se escribe en `.parte` y se renombra solo si coinciden; la app vuelve a comprobarlo y lo abre con su lector normal |
| Roles | `mirar` no puede mandar cargas ni reservas: lo rechaza el anfitrión aunque el cliente lo mande |
| Otro proceso del equipo contra la tubería | Protocolo de módulos: `CurrentUserOnly` y PID del cliente comprobado en Windows; socket 0700 y `SO_PEERCRED`/`LOCAL_PEERPID` en Unix |
| mDNS | Apagado por defecto. Encendido, anuncia la clave pública del equipo en la red local (servicio `dathinel-sala`): cualquiera en esa red sabe que hay una sala y puede intentar entrar, pero sin billete ni clave anclada no pasa del saludo |
| Blobs servidos | Solo lo ofrecido, por huella y tamaño exactos, 4 por par y 16 en total; quien ofrece no puede escribir en el equipo de quien pide; `destino` y `ruta` confinados a la carpeta de sala (también contra `..` y enlaces, se resuelven antes de comparar) |
| Traspaso y suplente | Solo el anfitrión traspasa y solo a un miembro editor conectado, que tiene que confirmar con la época y el seq preparados (si no, el anfitrión sigue); el suplente es el que nombró el anfitrión y solo tras `EsperaSuplente` sin él; los demás solo lo siguen si también lo perdieron |
| Lo que queda expuesto | La IP a los pares en conexión directa; metadatos al relé; el alias, color y clave pública de cada uno a los demás miembros |

## Lo que no cubre

- Un miembro aprobado con rol `editar` puede editar sus reservas: la sala no juzga si un cambio es bueno, solo si viene de quien puede hacerlo. Lo arregla el historial de cada app.
- Denegación de servicio desde la red contra la dirección del anfitrión (UDP): la mitiga QUIC (sin estado hasta la validación de dirección), no Sala.
- El relé público de n0 tiene límite de tráfico y no garantiza servicio: para proxies y blobs grandes, conexión directa o relé propio.
