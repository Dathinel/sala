namespace Dathinel.Sala;

/// <summary>
/// Avisos en español que comparten Rodaje y Unicomp (docs/internet.md es el mismo fondo, para leerlo fuera de la app).
/// Las apps los muestran tal cual o los enlazan.
/// </summary>
public static class Textos
{
    /// <summary>Qué es el relé y qué ve.</summary>
    public const string Rele =
        "Si dos equipos no pueden conectarse directamente (casi siempre por la red de una universidad o empresa, o por los datos del móvil), " +
        "La app usa un relé: un servidor intermediario que reenvía los mensajes. Todo va cifrado de extremo a extremo entre los dos equipos " +
        "(TLS 1.3): el relé ve las direcciones IP, cuándo hablan y cuántos datos, nunca el contenido. Por defecto se usa un relé público " +
        "de n0 (los creadores de iroh), pensado para desarrollo y pruebas: sin garantía de servicio y con límite de tráfico; sirve para editar, " +
        "no para mover medios grandes. En Preferencias se puede poner un relé propio. En conexión directa, en cambio, cada uno ve la dirección " +
        "IP del otro; «Solo por relé» la oculta a la otra persona (no al relé), a cambio de más latencia.";

    /// <summary>El cortafuegos de Windows.</summary>
    public const string Cortafuegos =
        "La primera vez que hospedes o entres en una sala, Windows puede preguntar si permite «dathinel-red» (el programa de red de la app). " +
        "Pulsa Permitir en redes privadas (y en públicas solo si no hay otra manera, por ejemplo en un café). Si pulsaste Cancelar o Bloquear: " +
        "Inicio, «Permitir una aplicación a través del Firewall de Windows», Cambiar la configuración, busca dathinel-red y marca Privada. " +
        "Si queda bloqueado, La app sigue funcionando por el relé, solo que más lento y nunca en directo.";

    /// <summary>Redes que bloquean UDP.</summary>
    public const string RedSinUdp =
        "Algunas redes (universidades, empresas, hoteles) bloquean el tráfico UDP, el que usa la conexión directa. La app lo detecta y sigue por el relé, " +
        "que viaja como una página web segura (puerto 443, el que casi nunca se bloquea): funciona igual, algo más lento. Si aun así no conecta, " +
        "prueba con los datos del móvil o con un relé propio en el puerto 443 (Preferencias).";

    /// <summary>Cuando el equipo no logró contactar con su relé.</summary>
    public const string ReleSinConectar =
        "No se pudo contactar con el relé. Sin él, solo entrarán equipos de tu misma red. Revisa tu conexión a internet y el cortafuegos, " +
        "o prueba otra red (por ejemplo los datos del móvil).";

    /// <summary>Cómo leer la ficha de diagnóstico por teléfono.</summary>
    public const string ComoLeerElDiagnostico =
        "Camino: «directo» es lo mejor; «por relé» es normal en universidades, empresas y datos del móvil. " +
        "Ida y vuelta: menos de 100 ms va muy bien, de 100 a 250 ms bien, y por encima de 250 ms se nota al editar. " +
        "Pérdida: menos de 1 % bien; más de 5 % la conexión es mala. " +
        "Versión: si no son iguales, actualiza la que esté más atrás (el mensaje de la app dice cuál). " +
        "Última caída: dice por qué se cortó la última vez y hace cuánto.";

    /// <summary>El motivo de un <c>sale</c> (<c>cerro</c>, <c>red</c>, <c>ritmo</c>, <c>marco</c>, <c>expulsado</c>) o de <c>Cerrada</c>, en una frase.</summary>
    public static string MotivoDeSalida(string motivo) => motivo switch
    {
        "cerro" => "El otro equipo cerró la sala o salió.",
        "red" => "Se cortó la conexión de red.",
        "ritmo" => "Se cortó porque llegaban demasiadas peticiones muy deprisa.",
        "marco" => "Se cortó porque el otro equipo mandó datos que no eran válidos.",
        "expulsado" => "El anfitrión te sacó de la sala.",
        "revocado" => "La invitación se anuló.",
        "traspaso" => "La sala pasó a otro anfitrión.",
        "reemplazada" => "La conexión se renovó.",
        "anfitrion-cerro" => "El anfitrión cerró la sala.",
        _ => "La conexión terminó por un motivo que esta versión no conoce.",
    };
}
