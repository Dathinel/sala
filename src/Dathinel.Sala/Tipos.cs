using System.Security.Cryptography;
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

/// <summary>Cómo sale a la red: relés de n0 (por defecto), ninguno (solo direcciones directas) o un relé propio.</summary>
public sealed record Red(string Rele = "n0", bool SoloRele = false)
{
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
}

/// <summary>Clave privada del equipo (Ed25519 de iroh) en 64 hex.</summary>
public static class Identidad
{
    // shortcut: solo genera; guardarla con DPAPI (Windows) o en un archivo 0600 (Unix) llega con la integración en las apps.
    public static string Nueva() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Error de la sala: rechazo del anfitrión, versión distinta, billete inválido, sin conexión…</summary>
public sealed class SalaException(string codigo, string mensaje, Exception? interna = null) : Exception(mensaje, interna)
{
    public string Codigo { get; } = codigo;
}
