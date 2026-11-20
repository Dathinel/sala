using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dathinel.Sala;

/// <summary>Camino que usa una conexión ahora mismo (docs/api.md §2.3).</summary>
public enum Camino { Desconocido, Directo, Rele }

/// <param name="ReleSolicitado">Lo que pidió la app: <c>n0</c>, <c>ninguno</c> o la URL de un relé propio.</param>
/// <param name="ReleUrl">El relé de casa en uso; <c>null</c> sin relé o sin conexión.</param>
/// <param name="ReleConectado">El equipo ya contactó con su relé: sin él, desde fuera de la red local no entra nadie.</param>
public sealed record DiagnosticoRed(string ReleSolicitado, string? ReleUrl, bool ReleConectado, bool SoloRele, bool Mdns);

/// <param name="Rele">URL del relé del camino elegido, si <paramref name="Camino"/> es <see cref="Camino.Rele"/>.</param>
/// <param name="Rtt">Ida y vuelta del camino elegido, medida por QUIC con el tráfico normal.</param>
/// <param name="PerdidaPct">Paquetes perdidos entre enviados de toda la conexión; <c>null</c> con pocos datos.</param>
public sealed record DiagnosticoPar(string Clave, string Alias, Camino Camino, string? Rele, TimeSpan? Rtt, double? PerdidaPct,
    string? AppVersion, int? Protocolo, DateTimeOffset? ConectadoDesde, DateTimeOffset? UltimaActividad, int CambiosDeCamino,
    long EnviadosB, long RecibidosB)
{
    /// <summary>Las cuatro palabras de la huella de la clave del par.</summary>
    public string Huella => Dathinel.Sala.Huella.Palabras(Clave);
}

public sealed record Caida(string Clave, string Motivo, DateTimeOffset En);

/// <summary>Cómo va la conexión: lo que la app enseña en su panel de conexión y lo que se lee por teléfono.</summary>
public sealed record Diagnostico(string Clave, bool EsAnfitrion, string AppVersion, DiagnosticoRed Red, IReadOnlyList<DiagnosticoPar> Pares,
    Caida? UltimaCaida, int Reconexiones, long? ConexionMs, DateTimeOffset? UltimaReconexion)
{
    /// <summary>La respuesta de <c>estado</c> (§2.3) más lo que solo sabe <c>Dathinel.Sala</c>. Lo que falte o venga mal queda en <c>null</c>.</summary>
    internal static Diagnostico Leer(JsonElement e, string appVersion, string releSolicitado, bool esAnfitrion,
        int reconexiones, long? conexionMs, DateTimeOffset? ultimaReconexion, Caida? caidaLocal)
    {
        JsonElement red = Propiedad(e, "red");
        DiagnosticoRed r = new(Texto(red, "rele") ?? releSolicitado, Texto(red, "releUrl"), Booleano(red, "releConectado"),
            Booleano(red, "soloRele"), Booleano(red, "mdns"));
        List<DiagnosticoPar> pares = [];
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("pares", out JsonElement lista) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement p in lista.EnumerateArray())
            {
                if (Texto(p, "clave") is not { } clave) continue;
                pares.Add(new DiagnosticoPar(clave, Texto(p, "alias") ?? "", Texto(p, "via") switch { "directo" => Camino.Directo, "rele" => Camino.Rele, _ => Camino.Desconocido },
                    Texto(p, "rele"), Entero(p, "rttMs") is { } ms ? TimeSpan.FromMilliseconds(ms) : null, Decimal(p, "perdidosPct"),
                    Texto(p, "appVersion"), Entero(p, "protocolo") is { } pr ? (int)pr : null, Fecha(p, "conectadoDesde"), Fecha(p, "ultimaActividad"),
                    (int)(Entero(p, "cambiosDeCamino") ?? 0), Entero(p, "enviadosB") ?? 0, Entero(p, "recibidosB") ?? 0));
            }
        }
        Caida? caida = null;
        JsonElement c = Propiedad(e, "ultimaCaida");
        if (Texto(c, "clave") is { } ck && Texto(c, "motivo") is { } cm && Fecha(c, "en") is { } cf) caida = new Caida(ck, cm, cf);
        if (caidaLocal is not null && (caida is null || caidaLocal.En > caida.En)) caida = caidaLocal;
        return new Diagnostico(Texto(e, "clave") ?? "", esAnfitrion, appVersion, r, pares, caida, reconexiones, conexionMs, ultimaReconexion);
    }

    /// <summary>
    /// Ficha corta (seis líneas como mucho) para leerla en voz alta: camino, ida y vuelta, pérdida, versiones, actividad y última caída.
    /// Sin IP, sin rutas y sin claves enteras; un par de otra versión no llega a estar en <see cref="Pares"/> (§3.1).
    /// </summary>
    public string ParaTelefono(DateTimeOffset? ahora = null)
    {
        DateTimeOffset t = ahora ?? DateTimeOffset.UtcNow;
        StringBuilder s = new();
        if (Pares.Count == 0)
        {
            s.Append("Camino: sin conexión con nadie\n");
            if (!Red.ReleConectado && Red.ReleSolicitado != "ninguno") s.Append("Relé: sin conectar; desde fuera de tu red no entrará nadie\n");
        }
        else
        {
            // En el invitado, el único par es el anfitrión; en el anfitrión, el peor (más lento) es el que importa por teléfono.
            DiagnosticoPar p = Pares.OrderByDescending(x => x.Rtt ?? TimeSpan.Zero).First();
            string nombre = p.Alias.Length > 0 ? p.Alias : p.Huella;
            s.Append("Camino: ").Append(DescribirCamino(p)).Append(Pares.Count > 1 ? $" (el más lento de {Pares.Count}: {nombre})" : "").Append('\n');
            s.Append("Ida y vuelta: ").Append(p.Rtt is { } rtt ? $"{(long)rtt.TotalMilliseconds} ms" : "sin medir").Append('\n');
            s.Append("Pérdida: ").Append(p.PerdidaPct is { } pc ? pc.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %" : "sin datos").Append('\n');
            string suya = p.AppVersion ?? "desconocida";
            s.Append("Versión: tuya ").Append(AppVersion).Append(" · de ").Append(nombre).Append(' ').Append(suya)
                .Append(suya == AppVersion ? " (iguales)" : " (distintas)").Append('\n');
            s.Append("Actividad: ").Append(p.UltimaActividad is { } a ? "hace " + Duracion(t - a) : "ninguna todavía").Append('\n');
        }
        s.Append("Última caída: ").Append(UltimaCaida is { } k ? Textos.MotivoDeSalida(k.Motivo).TrimEnd('.') + ", hace " + Duracion(t - k.En) : "ninguna");
        return s.ToString();
    }

    private string DescribirCamino(DiagnosticoPar p) => p.Camino switch
    {
        Camino.Directo => "directo",
        Camino.Rele => "por relé (" + (Red.ReleSolicitado == "n0" ? "relé de n0" : "relé propio" + (NombreServidor(p.Rele ?? Red.ReleUrl) is { } h ? " " + h : "")) + ")",
        _ => "sin determinar",
    };

    private static string? NombreServidor(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : null;

    private static string Duracion(TimeSpan d)
    {
        double s = Math.Max(0, d.TotalSeconds);
        if (s < 90) return $"{(int)s} s";
        if (s < 5400) return $"{(int)Math.Round(s / 60)} min";
        return $"{(int)Math.Round(s / 3600)} h";
    }

    private static JsonElement Propiedad(JsonElement e, string nombre) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nombre, out JsonElement v) ? v : default;

    private static string? Texto(JsonElement e, string nombre) =>
        Propiedad(e, nombre) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static bool Booleano(JsonElement e, string nombre) => Propiedad(e, nombre) is { ValueKind: JsonValueKind.True };

    private static long? Entero(JsonElement e, string nombre) =>
        Propiedad(e, nombre) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out long n) ? n : null;

    private static double? Decimal(JsonElement e, string nombre) =>
        Propiedad(e, nombre) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out double d) ? d : null;

    private static DateTimeOffset? Fecha(JsonElement e, string nombre) =>
        Entero(e, nombre) is { } s && s is > 0 and < 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}
