using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala.Tests;

/// <summary>Lo común de las pruebas: dónde está dathinel-red, una carpeta de sala temporal y unas opciones.</summary>
internal static class Entorno
{
    public const string App = "prueba";
    public const string VersionApp = "1.0.0";

    /// <summary>SALA_RED o red/target/debug/dathinel-red del repositorio.</summary>
    public static string RutaRed { get; } = BuscarRed();

    private static string BuscarRed()
    {
        if (Environment.GetEnvironmentVariable("SALA_RED") is { Length: > 0 } r) return r;
        string exe = OperatingSystem.IsWindows() ? "dathinel-red.exe" : "dathinel-red";
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Sala.slnx")))
                return Path.Combine(d.FullName, "red", "target", "debug", exe);
        }
        throw new InvalidOperationException("No se encontró el repositorio: define SALA_RED.");
    }

    public static OpcionesSala Opciones(string alias, string? carpeta = null, string app = App, string version = VersionApp) =>
        new(app, version, alias, RutaRed, carpeta ?? Carpeta()) { Red = Dathinel.Sala.Red.SinRele };

    public static string Carpeta()
    {
        string c = Path.Combine(Path.GetTempPath(), "sala-pruebas", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(c);
        return c;
    }

    public static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    public static JsonObject Base(string identidad, string carpeta, string app = App, string version = VersionApp) => new()
    {
        ["identidad"] = identidad,
        ["app"] = app,
        ["appVersion"] = version,
        ["carpeta"] = carpeta,
        ["red"] = new JsonObject { ["rele"] = "ninguno" },
    };

    public static readonly Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>> TodoVale = (_, _, _) => ValueTask.FromResult(Veredicto.Si);

    /// <summary>Espera a que <paramref name="cond"/> se cumpla o falla a los <paramref name="segundos"/>.</summary>
    public static async Task HastaQue(Func<bool> cond, int segundos = 20, string? que = null)
    {
        DateTime limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!cond())
        {
            if (DateTime.UtcNow > limite) Assert.Fail("No pasó a tiempo: " + que);
            await Task.Delay(20);
        }
    }

    /// <summary>Un anfitrión que acepta a todos con <paramref name="rol"/>.</summary>
    public static async Task<Sala> AnfitrionAsync(Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>? validar = null,
        Rol rol = Rol.Editar, long epoca = 1, long seq = 0, OpcionesSala? opciones = null)
    {
        Sala s = await Sala.HospedarAsync(Identidad.Nueva(), opciones ?? Opciones("Ana"), [], validar ?? TodoVale, epoca, seq);
        s.SolicitudRecibida += sol => _ = s.AceptarAsync(sol.Clave, rol);
        return s;
    }
}
