using System.Reflection;
using System.Security.Cryptography;

namespace Dathinel.Sala;

/// <summary>
/// Huella de cuatro palabras de una clave (docs/api.md §5.1): SHA-256 de los 32 bytes, los 44 primeros bits en cuatro
/// grupos de 11, cada uno un índice en <c>palabras.txt</c> (2048 palabras con las cuatro primeras letras distintas).
/// </summary>
public static class Huella
{
    private static readonly Lazy<string[]> Lista = new(Cargar);

    internal static IReadOnlyList<string> Todas => Lista.Value;

    public static string Palabras(string clave)
    {
        byte[] bytes = Convert.FromHexString(clave);
        if (bytes.Length != 32) throw new ArgumentException("Una clave son 64 hexadecimales.", nameof(clave));
        byte[] h = SHA256.HashData(bytes);
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits = (bits << 8) | h[i];
        bits >>= 4; // 48 → 44 bits
        string[] l = Lista.Value;
        return string.Join('-', Enumerable.Range(0, 4).Select(i => l[(int)((bits >> (33 - 11 * i)) & 0x7FF)]));
    }

    private static string[] Cargar()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dathinel.Sala.palabras.txt")
            ?? throw new InvalidOperationException("Falta palabras.txt.");
        using StreamReader r = new(s);
        string[] l = r.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return l.Length == 2048 ? l : throw new InvalidOperationException("palabras.txt tiene que traer 2048 palabras.");
    }
}
