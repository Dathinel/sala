using System.Text.Json;

namespace Dathinel.Sala;

public enum Rol { Mirar, Editar }

/// <summary>Un miembro de la sala. La identidad es la clave (64 hex); el alias es solo un nombre.</summary>
public sealed record Miembro(string Clave, string Alias, Rol Rol);

/// <summary>Respuesta de la app a una carga: entra o no, con un código corto y un detalle para la persona.</summary>
public sealed record Veredicto(bool Aceptada, string? Motivo = null, string? Detalle = null)
{
    public static readonly Veredicto Si = new(true);
    public static Veredicto No(string motivo, string? detalle = null) => new(false, motivo, detalle);
}

/// <summary>Lo que la sala le pasa a la app por cada carga que entró, en orden de <see cref="Seq"/>.</summary>
public sealed record Ordenado(long Epoca, long Seq, Guid Id, string Autor, JsonElement Datos);

public sealed record Rechazo(Guid Id, string Motivo, string? Detalle);

public sealed record Solicitud(string Clave, string Alias)
{
    /// <summary>Las cuatro palabras para comparar la clave de viva voz.</summary>
    public string Huella => Dathinel.Sala.Huella.Palabras(Clave);
}

public sealed record Directo(string De, JsonElement Datos);

/// <summary>En qué va <c>UnirseAsync</c> (y cada reconexión): para la barra o la frase de «Conectando…» de la app.</summary>
public enum FaseConexion
{
    /// <summary>Preparando la red y buscando al anfitrión.</summary>
    Buscando,
    /// <summary>Marcando al anfitrión (directo o por relé); con reintentos, <c>Diagnostico.Intento</c> dice cuál.</summary>
    Conectando,
    /// <summary>Con el anfitrión; esperando que apruebe la entrada (hasta 10 min).</summary>
    EsperandoAprobacion,
    /// <summary>Admitido; recibiendo el estado de la sala.</summary>
    Sincronizando,
    Conectado,
    /// <summary>Se perdió al anfitrión; la sala reintenta sola.</summary>
    Desconectado,
}

/// <summary>
/// Avance de una descarga de blob, listo para una barra con porcentaje, «quedan N s» y «último avance hace N s»
/// (docs/progreso-operaciones.md de Rodaje). Cancelar es cancelar el <see cref="CancellationToken"/> de <c>PedirBlobAsync</c>.
/// </summary>
/// <param name="Hecho">Bytes ya bajados.</param>
/// <param name="Total">Tamaño exacto del blob.</param>
/// <param name="Restante">Estimado con la velocidad media desde el inicio; <c>null</c> mientras no haya datos para estimar.</param>
public sealed record ProgresoBlob(string De, string Huella, long Intento, long Hecho, long Total, DateTimeOffset Inicio,
    DateTimeOffset UltimoAvance, double BytesPorSegundo, TimeSpan? Restante)
{
    public double Fraccion => Total <= 0 ? 1 : Math.Clamp((double)Hecho / Total, 0, 1);
}

/// <summary>Cómo sale a la red: relés de n0 (por defecto), ninguno (solo direcciones directas) o un relé propio.</summary>
/// <param name="Mdns">Descubrir al anfitrión en la red local por su clave, sin direcciones (anuncia el equipo en la red local).</param>
public sealed record Red(string Rele = "n0", bool SoloRele = false, bool Mdns = false)
{
    /// <summary>Solo para las pruebas con el relé local de <c>dathinel-red --rele-prueba</c>: el binario de producción lo rechaza.</summary>
    internal bool ReleInseguro { get; init; }

    public static readonly Red N0 = new();
    public static readonly Red SinRele = new("ninguno");
}

/// <param name="Ejecutable">Ruta de dathinel-red.</param>
/// <param name="Carpeta">Carpeta de sala de la app (absoluta): las rutas de blobs quedan dentro de ella.</param>
public sealed record OpcionesSala(string App, string AppVersion, string Alias, string Ejecutable, string Carpeta)
{
    public Red Red { get; init; } = Red.N0;
    public TimeSpan TopeValidar { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxMiembros { get; init; } = 16;
    /// <summary>Una reserva sin actividad de su dueño se suelta sola pasado este tiempo.</summary>
    public TimeSpan ReservaInactiva { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>Sin anfitrión este tiempo, el suplente puede tomar la sala y los demás lo buscan.</summary>
    public TimeSpan EsperaSuplente { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Error de la sala: rechazo del anfitrión, versión distinta, billete inválido, sin conexión…</summary>
public sealed class SalaException(string codigo, string mensaje, Exception? interna = null) : Exception(mensaje, interna)
{
    public string Codigo { get; } = codigo;
}
