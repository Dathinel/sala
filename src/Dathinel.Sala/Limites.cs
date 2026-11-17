using System.Text.Json;

namespace Dathinel.Sala;

/// <summary>
/// Límites de lo que entra y sale (docs/api.md §3 y §5.8), medidos como los serializa este lado: con los caracteres no
/// ASCII escapados, que es lo que de verdad cruza la tubería local. Los mismos números están en
/// <c>pruebas/conformidad/limites.json</c>, que prueban también los de Rust.
/// </summary>
internal static class Limites
{
    /// <summary>Un marco entre pares llega dentro de 4 envoltorios (result, value, eventos, evento) y la tubería admite 32.</summary>
    public const int ProfundidadMarco = 28;
    public const int ProfundidadCarga = ProfundidadMarco - 1;
    public const int MaxAlias = 40;
    public const int MaxMotivo = 64;
    public const int MaxDetalle = 4096;
    public const int MaxDirecciones = 16;

    public static int Bytes(JsonElement e) => JsonSerializer.SerializeToUtf8Bytes(e, SalaJson.Default.JsonElement).Length;

    public static int Profundidad(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => 1 + e.EnumerateObject().Select(p => Profundidad(p.Value)).DefaultIfEmpty(0).Max(),
        JsonValueKind.Array => 1 + e.EnumerateArray().Select(Profundidad).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    /// <summary>Un objeto JSON de hasta <paramref name="maxBytes"/> y <see cref="ProfundidadCarga"/> niveles.</summary>
    public static bool Objeto(JsonElement e, int maxBytes) =>
        e.ValueKind == JsonValueKind.Object && Profundidad(e) <= ProfundidadCarga && Bytes(e) <= maxBytes;

    /// <summary>Clave pública: 64 hexadecimales en minúscula.</summary>
    public static bool EsClave(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool EsAlias(string? s) => s is { Length: >= 1 and <= MaxAlias } && !s.Any(char.IsControl);

    public static bool EsMotivo(string? s) => s is { Length: >= 1 and <= MaxMotivo } && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public static string? Texto(JsonElement m, string campo) =>
        m.TryGetProperty(campo, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Entero(JsonElement m, string campo) =>
        m.TryGetProperty(campo, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) && n >= 0 ? n : null;

    public static Guid? Id(JsonElement m) => Guid.TryParse(Texto(m, "id"), out Guid id) ? id : null;

    /// <summary>Lista de hasta <paramref name="max"/> textos de hasta <paramref name="largo"/> caracteres, o null si no lo es.</summary>
    public static string[]? Textos(JsonElement m, string campo, int max, int largo = 128)
    {
        if (!m.TryGetProperty(campo, out JsonElement v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > max) return null;
        List<string> r = [];
        foreach (JsonElement x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String || x.GetString() is not { } t || t.Length > largo) return null;
            r.Add(t);
        }
        return [.. r];
    }
}
