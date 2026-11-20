# Internet: relé, cortafuegos y diagnóstico

Lo que una persona necesita saber cuando una sala de Rodaje o Unicomp funciona por internet. Los mismos textos están en `Dathinel.Sala.Textos`, para que las apps los muestren sin reescribirlos. Contrato técnico: `docs/api.md` §2.1, §2.3 y §5.9; amenazas: `docs/seguridad.md`.

## Cómo conecta

1. El anfitrión crea una invitación. Lleva su clave, un secreto de un solo uso y la dirección de su **relé de casa**.
2. El invitado marca primero por el relé (siempre llega: es un servidor en internet) y, en paralelo, intenta abrir un camino **directo** con el anfitrión.
3. Si el directo funciona, la conexión cambia a él sin cortarse. Si no (casi todas las redes de universidad, empresa o datos del móvil), sigue por el relé.
4. Si el directo se cae, vuelve al relé; si el relé se cae, reintenta solo (1, 2, 4… hasta 30 segundos) y manda lo pendiente al volver.

## Qué ve el relé

- Todo lo que viaja va **cifrado de extremo a extremo** entre los dos equipos (QUIC con TLS 1.3, claves de cada equipo). El relé reenvía paquetes que no puede abrir.
- El relé sí ve las **direcciones IP** de los dos equipos, **cuándo** hablan y **cuántos datos**. Nunca el contenido: ni proyectos, ni nombres de medios, ni alias.
- Relé público de n0 (por defecto): gratuito, "para desarrollo y uso personal", sin garantía de servicio, con límite de tráfico que no publican y que puede cambiar, y n0 vigila abusos. Sirve para editar; **no se mandan proxies ni medios grandes por él**. Para algo más serio, un relé propio (Preferencias de la app): cualquier servidor con `iroh-relay` y un nombre con certificado, en el puerto 443.
- Conexión directa: cada uno ve la IP del otro. **«Solo por relé»** hace que ninguno la vea (el relé sí); cuesta latencia porque todo pasa por el servidor.

## El cortafuegos de Windows

`dathinel-red` solo escucha cuando hospedas o entras en una sala. La primera vez, Windows pregunta si lo permite.

- Pulsa **Permitir** en redes privadas. En redes públicas (café, hotel), solo si no hay otra manera.
- Si pulsaste Cancelar o Bloquear: Inicio, «Permitir una aplicación a través del Firewall de Windows», **Cambiar la configuración**, busca `dathinel-red` y marca **Privada**. Si no aparece, «Permitir otra aplicación», y elige `dathinel-red.exe` de la carpeta de instalación.
- Si queda bloqueado no se rompe nada: la sala sigue por el relé, solo más lenta y nunca en directo.

## Redes que bloquean UDP

La conexión directa usa UDP. Muchas redes de universidad, empresa y hotel lo bloquean o aíslan a los equipos entre sí (dos portátiles en el mismo salón no se ven). El relé viaja como una página web segura (HTTPS, puerto 443), que casi nunca se bloquea: la sala funciona igual, algo más lenta. Si tampoco conecta: prueba con los datos del móvil, o pon un relé propio en el puerto 443.

## Leer el diagnóstico por teléfono

El panel de conexión de la app, y `Diagnostico.ParaTelefono()`, dan una ficha de seis líneas:

```
Camino: por relé (relé de n0)
Ida y vuelta: 118 ms
Pérdida: 0,4 %
Versión: tuya 1.0.4 · de Luis 1.0.4 (iguales)
Actividad: hace 2 s
Última caída: Se cortó la conexión de red, hace 5 min
```

- **Camino:** `directo` es lo mejor. `por relé` es normal en universidades, empresas y datos del móvil.
- **Ida y vuelta:** menos de 100 ms va muy bien; de 100 a 250 ms, bien; por encima de 250 ms se nota al editar.
- **Pérdida:** menos de 1 % bien; más de 5 % la conexión es mala (wifi flojo o red saturada).
- **Versión:** si no son iguales, Rodaje no deja entrar y dice a quién le toca actualizar: a quien tenga la versión más baja.
- **Actividad:** hace cuánto llegó algo del otro equipo. Si pasan decenas de segundos y el otro está editando, algo se cortó.
- **Última caída:** por qué se cortó la última conexión y hace cuánto.
- La ficha no lleva IP, rutas ni claves enteras: se puede leer o pegar sin riesgo.

## Si nada conecta: orden de comprobación

1. La invitación: ¿caducó o ya se usó? El anfitrión crea otra.
2. Las versiones de las dos apps: tienen que ser la misma.
3. El anfitrión: ¿el panel dice que el relé está conectado? Si no, su internet o su cortafuegos.
4. Probar con los datos del móvil en un lado.
5. Probar «Solo por relé» en los dos: si así conecta, el directo estaba fallando y el relé funciona.
6. Anotar el diagnóstico de cada lado y pasarlo (ver `Rodaje/docs/prueba-colaboracion.md`).
