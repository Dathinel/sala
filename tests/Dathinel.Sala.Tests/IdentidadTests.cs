using System.Text;
using System.Text.RegularExpressions;

namespace Dathinel.Sala.Tests;

public sealed partial class IdentidadTests
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex ClaveHex();

    private static string Archivo() => Path.Combine(Entorno.Carpeta(), "identidad");

    [Fact]
    public void Crear_y_cargar_da_la_misma_clave()
    {
        string ruta = Archivo();
        string clave = Identidad.CargarOCrear(ruta);
        Assert.Matches(ClaveHex(), clave);
        Assert.Equal(clave, Identidad.CargarOCrear(ruta));
        Assert.NotEqual(clave, Identidad.CargarOCrear(Archivo()));
    }

    [Fact]
    public void En_windows_el_archivo_no_lleva_la_clave_en_claro()
    {
        if (!OperatingSystem.IsWindows()) return;
        string ruta = Archivo();
        string clave = Identidad.CargarOCrear(ruta);
        byte[] contenido = File.ReadAllBytes(ruta);
        Assert.Equal(-1, contenido.AsSpan().IndexOf(Convert.FromHexString(clave)));
        Assert.Equal(-1, contenido.AsSpan().IndexOf(Encoding.ASCII.GetBytes(clave)));
        Assert.Equal(clave, Identidad.CargarOCrear(ruta));
    }

    [Fact]
    public void En_unix_nace_0600_y_una_0644_se_rechaza()
    {
        if (OperatingSystem.IsWindows()) return;
        string ruta = Archivo();
        Identidad.CargarOCrear(ruta);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ruta));

        string abierta = Archivo();
        File.WriteAllText(abierta, Identidad.Nueva());
        File.SetUnixFileMode(abierta,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        SalaException e = Assert.Throws<SalaException>(() => Identidad.CargarOCrear(abierta));
        Assert.Equal("identidad", e.Codigo);
    }

    [Fact]
    public void Un_archivo_con_basura_lanza_identidad_y_no_se_pisa()
    {
        string ruta = Archivo();
        byte[] basura = "esto no es una clave"u8.ToArray();
        File.WriteAllBytes(ruta, basura);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        SalaException e = Assert.Throws<SalaException>(() => Identidad.CargarOCrear(ruta));
        Assert.Equal("identidad", e.Codigo);
        Assert.Equal(basura, File.ReadAllBytes(ruta));
    }
}
