using System.Text.RegularExpressions;

namespace Dathinel.Sala.Tests;

public sealed partial class HuellaTests
{
    [GeneratedRegex("^[a-z]{3,8}$")]
    private static partial Regex Palabra();

    [Fact]
    public void La_lista_tiene_2048_palabras_distintas_desde_la_cuarta_letra()
    {
        IReadOnlyList<string> l = Huella.Todas;
        Assert.Equal(2048, l.Count);
        Assert.All(l, p => Assert.Matches(Palabra(), p));
        Assert.Equal(2048, l.Select(p => p.Length > 4 ? p[..4] : p).Distinct().Count());
    }

    [Fact]
    public void Misma_clave_mismas_palabras_y_otra_clave_otras()
    {
        string a = new('a', 64), b = "b" + new string('a', 63);
        Assert.Equal(Huella.Palabras(a), Huella.Palabras(a));
        Assert.Equal(4, Huella.Palabras(a).Split('-').Length);
        Assert.NotEqual(Huella.Palabras(a), Huella.Palabras(b));
        Assert.Throws<ArgumentException>(() => Huella.Palabras("abcd"));
    }
}
