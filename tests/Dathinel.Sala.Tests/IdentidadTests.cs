using System.Diagnostics;
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

    [Fact]
    public async Task Entre_16_hilos_a_la_vez_todos_ven_la_misma_clave()
    {
        string ruta = Archivo();
        using Barrier barrera = new(16);
        string[] claves = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            barrera.SignalAndWait();
            return Identidad.CargarOCrear(ruta);
        })));

        string guardada = Assert.Single(claves.Distinct());
        Assert.Matches(ClaveHex(), guardada);
        Assert.Equal(guardada, Identidad.CargarOCrear(ruta));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(ruta)!, "*.tmp-*"));
    }

    [Fact]
    public void Entre_4_procesos_a_la_vez_todos_ven_la_misma_clave()
    {
        string ruta = Archivo();
        Process[] procesos = [.. Enumerable.Range(0, 4).Select(_ =>
        {
            ProcessStartInfo psi = new(Consumidor()) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("identidad");
            psi.ArgumentList.Add(ruta);
            return Process.Start(psi)!;
        })];
        string[] claves = new string[4];
        for (int i = 0; i < procesos.Length; i++)
        {
            claves[i] = procesos[i].StandardOutput.ReadToEnd().Trim();
            string error = procesos[i].StandardError.ReadToEnd();
            Assert.True(procesos[i].WaitForExit(60_000), $"El consumidor {i} no salió a tiempo.");
            Assert.True(procesos[i].ExitCode == 0, $"El consumidor {i} salió {procesos[i].ExitCode}: {error}");
            procesos[i].Dispose();
        }

        string guardada = Assert.Single(claves.Distinct());
        Assert.Matches(ClaveHex(), guardada);
        Assert.Equal(guardada, Identidad.CargarOCrear(ruta));
    }

    // El exe del ejemplo (bin/Debug o bin/Release, según lo último compilado) junto a Sala.slnx.
    private static string Consumidor()
    {
        string exe = OperatingSystem.IsWindows() ? "Consumidor.exe" : "Consumidor";
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!File.Exists(Path.Combine(d.FullName, "Sala.slnx"))) continue;
            foreach (string conf in new[] { "Debug", "Release" })
            {
                string p = Path.Combine(d.FullName, "ejemplos", "Consumidor", "bin", conf, "net10.0", exe);
                if (File.Exists(p)) return p;
            }
            throw new InvalidOperationException("No está el consumidor compilado: dotnet build ejemplos/Consumidor.");
        }
        throw new InvalidOperationException("No se encontró el repositorio.");
    }
}
