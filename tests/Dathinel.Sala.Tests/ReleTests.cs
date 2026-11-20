using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>
/// Internet sin salir del equipo (B13): dos <c>dathinel-red</c> con identidades distintas hablan solo por un relé local de pruebas
/// (<c>dathinel-red --rele-prueba</c>, que existe solo en binarios construidos con <c>--features prueba</c>; la CI lo construye así).
/// </summary>
[Trait("Categoria", "Rele")]
public sealed class ReleTests(ITestOutputHelper salida)
{
    private static OpcionesSala ConRele(string alias, string url, bool soloRele = true, string version = VersionApp) =>
        Opciones(alias, version: version) with { Red = new Red(url, SoloRele: soloRele) { ReleInseguro = true } };

    private static string ReleDelBillete(string billete)
    {
        string b64 = billete["dathinel1.".Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        using JsonDocument d = JsonDocument.Parse(Convert.FromBase64String(b64));
        return d.RootElement.TryGetProperty("rele", out JsonElement r) ? r.GetString()! : "";
    }

    private static async Task<DiagnosticoPar> ParAsync(Sala s, Func<DiagnosticoPar, bool>? cond = null, int segundos = 15)
    {
        DateTime limite = DateTime.UtcNow.AddSeconds(segundos);
        while (true)
        {
            Diagnostico d = await s.DiagnosticoAsync();
            if (d.Pares.Count > 0 && (cond?.Invoke(d.Pares[0]) ?? true)) return d.Pares[0];
            if (DateTime.UtcNow > limite) Assert.Fail("El par no llegó al estado esperado: " + d.ParaTelefono());
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Dos_equipos_solo_por_rele_conectan_y_dicen_que_es_por_rele()
    {
        await using ReleLocal rele = await ReleLocal.IniciarAsync();
        await using Sala ana = await AnfitrionAsync(opciones: ConRele("Ana", rele.Url) with { AppVersion = "1.0.4" });
        Assert.Empty(ana.Direcciones);
        string billete = await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(new Uri(rele.Url).Authority, new Uri(ReleDelBillete(billete)).Authority);

        Stopwatch reloj = Stopwatch.StartNew();
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), ConRele("Luis", rele.Url) with { AppVersion = "1.0.4" }, billete, validar: TodoVale);
        long ms = reloj.ElapsedMilliseconds;
        Assert.Empty(luis.Direcciones);

        DiagnosticoPar enLuis = await ParAsync(luis, p => p.Rtt is not null);
        DiagnosticoPar enAna = await ParAsync(ana, p => p.Rtt is not null);
        Assert.Equal(Camino.Rele, enLuis.Camino);
        Assert.Equal(Camino.Rele, enAna.Camino);
        Assert.Equal(new Uri(rele.Url).Authority, new Uri(enLuis.Rele!).Authority);
        Assert.Equal("1.0.4", enLuis.AppVersion);
        Assert.Equal(1, enAna.Protocolo);
        Assert.Equal("Luis", enAna.Alias);
        Diagnostico d = await luis.DiagnosticoAsync();
        Assert.True(d.Red.ReleConectado);
        Assert.True(d.Red.SoloRele);
        Assert.InRange(d.ConexionMs ?? -1, 0, 20_000);
        salida.WriteLine($"Entrar por relé local: {ms} ms (de UnirseAsync a bienvenida); conexión según dathinel-red: {d.ConexionMs} ms; RTT {enLuis.Rtt?.TotalMilliseconds} ms");
        salida.WriteLine(d.ParaTelefono());
    }

    [Fact]
    public async Task Una_edicion_por_rele_con_latencia_de_25_ms_por_tramo_va_en_menos_de_250_ms_p95()
    {
        await using ReleLocal rele = await ReleLocal.IniciarAsync(latenciaMs: 25);
        await using Sala ana = await AnfitrionAsync(opciones: ConRele("Ana", rele.Url));
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), ConRele("Luis", rele.Url), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: TodoVale);
        ConcurrentDictionary<Guid, long> vuelta = new();
        Stopwatch reloj = Stopwatch.StartNew();
        luis.Ordenada += o => vuelta[o.Id] = reloj.ElapsedTicks;

        List<double> ms = [];
        for (int i = 0; i < 60; i++)
        {
            long t0 = reloj.ElapsedTicks;
            Guid id = await luis.EnviarAsync(Json($$"""{"n":{{i}}}"""));
            await HastaQue(() => vuelta.ContainsKey(id), segundos: 10, que: "la vuelta de la carga " + i);
            ms.Add((vuelta[id] - t0) * 1000.0 / Stopwatch.Frequency);
        }
        ms.Sort();
        double p50 = ms[ms.Count / 2], p95 = ms[(int)(ms.Count * 0.95)];
        DiagnosticoPar p = await ParAsync(luis, x => x.Rtt is not null);
        salida.WriteLine($"Edición por relé con 25 ms por tramo: p50 {p50:0} ms, p95 {p95:0} ms, máx {ms[^1]:0} ms; RTT de QUIC {p.Rtt?.TotalMilliseconds} ms");
        Assert.Equal(Camino.Rele, p.Camino);
        Assert.InRange(p.Rtt!.Value.TotalMilliseconds, 60, 600);
        Assert.True(p95 < 250, $"p95 {p95:0} ms");
    }

    [Fact]
    public async Task Si_se_corta_el_proceso_de_red_del_invitado_vuelve_por_el_rele()
    {
        await using ReleLocal rele = await ReleLocal.IniciarAsync();
        await using Sala ana = await AnfitrionAsync(opciones: ConRele("Ana", rele.Url));
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), ConRele("Luis", rele.Url), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: TodoVale);
        int reconectada = 0;
        luis.Reconectada += () => Interlocked.Increment(ref reconectada);

        Stopwatch reloj = Stopwatch.StartNew();
        await luis.CortarRedParaPruebasAsync();
        await HastaQue(() => reconectada == 1, segundos: 60, que: "vuelve por el relé");
        long ms = reloj.ElapsedMilliseconds;
        Diagnostico d = await luis.DiagnosticoAsync();
        Assert.Equal(1, d.Reconexiones);
        Assert.NotNull(d.UltimaReconexion);
        Assert.Equal(Camino.Rele, (await ParAsync(luis)).Camino);

        Guid id = await luis.EnviarAsync(Json("""{"despues":true}"""));
        await HastaQue(() => ana.Seq == 1, que: "la edición posterior entra");
        salida.WriteLine($"Reconexión por relé tras cortar el proceso de red: {ms} ms; {d.Reconexiones} reconexión; id {id}");
    }

    [Fact]
    public async Task Sin_solo_rele_y_en_la_misma_maquina_acaba_directo_y_con_el_rele_de_respaldo()
    {
        await using ReleLocal rele = await ReleLocal.IniciarAsync();
        await using Sala ana = await AnfitrionAsync(opciones: ConRele("Ana", rele.Url, soloRele: false));
        Assert.NotEmpty(ana.Direcciones);
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), ConRele("Luis", rele.Url, soloRele: false), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: TodoVale);
        DiagnosticoPar p = await ParAsync(luis, x => x.Camino == Camino.Directo, segundos: 30);
        Assert.Null(p.Rele);
        salida.WriteLine("Con direcciones directas disponibles el camino pasó a directo: " + (await luis.DiagnosticoAsync()).ParaTelefono().Split('\n')[0]);
    }

    [Fact]
    public async Task Otra_version_no_entra_y_el_mensaje_dice_a_quien_le_toca_actualizar()
    {
        await using Sala ana = await AnfitrionAsync(opciones: Opciones("Ana", version: "1.0.4"));
        string billete = await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10), unUso: false);

        SalaException vieja = await Assert.ThrowsAsync<SalaException>(() => Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis", version: "1.0.3"), billete, validar: TodoVale));
        Assert.Equal("version", vieja.Codigo);
        Assert.Equal("Tu Prueba es el 1.0.3 y esta sala usa el 1.0.4: actualiza tu Prueba.", vieja.Message);

        SalaException nueva = await Assert.ThrowsAsync<SalaException>(() => Sala.UnirseAsync(Identidad.Nueva(), Opciones("Eva", version: "1.0.10"), billete, validar: TodoVale));
        Assert.Equal("version", nueva.Codigo);
        Assert.Equal("Esta sala usa el 1.0.4 y tu Prueba es el 1.0.10: pide a quien te invitó que actualice el suyo.", nueva.Message);

        SalaException rara = await Assert.ThrowsAsync<SalaException>(() => Sala.UnirseAsync(Identidad.Nueva(), Opciones("Pol", version: "1.0.4-beta"), billete, validar: TodoVale));
        Assert.Equal("version", rara.Codigo);
        Assert.Contains("las dos versiones tienen que ser la misma", rara.Message);
        Assert.DoesNotContain(ana.Miembros, m => m.Alias is "Luis" or "Eva" or "Pol");
    }

    [Fact]
    public async Task Un_rele_que_no_existe_no_cuelga_la_sala_y_avisa_que_no_esta_conectado()
    {
        // Nadie escucha en ese puerto: abrir-sala no falla; el diagnóstico dice que el relé no está conectado.
        OpcionesSala o = Opciones("Ana") with { Red = new Red("https://127.0.0.1:9", SoloRele: true) { ReleInseguro = true } };
        Stopwatch reloj = Stopwatch.StartNew();
        await using Sala ana = await AnfitrionAsync(opciones: o);
        Diagnostico d = await ana.DiagnosticoAsync();
        Assert.False(d.Red.ReleConectado);
        Assert.InRange(reloj.ElapsedMilliseconds, 0, 15_000);
        Assert.Contains("Relé: sin conectar", d.ParaTelefono());
    }
}

/// <summary>El relé local de pruebas de <c>dathinel-red --rele-prueba</c>: una línea JSON con su URL y vive hasta que se cierre su entrada.</summary>
internal sealed class ReleLocal : IAsyncDisposable
{
    private readonly Process _proceso;
    public string Url { get; }

    private ReleLocal(Process p, string url) { _proceso = p; Url = url; }

    public static async Task<ReleLocal> IniciarAsync(int latenciaMs = 0, string? captura = null)
    {
        string args = "--rele-prueba" + (latenciaMs > 0 ? $" --latencia-ms {latenciaMs}" : "") + (captura is null ? "" : $" --captura \"{captura}\"");
        Process p = Process.Start(new ProcessStartInfo(RutaRed, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        using CancellationTokenSource tope = new(TimeSpan.FromSeconds(30));
        try
        {
            string? linea = await p.StandardOutput.ReadLineAsync(tope.Token);
            using JsonDocument d = JsonDocument.Parse(linea ?? throw new InvalidOperationException("El relé de pruebas no dijo su URL: ¿está dathinel-red construido con --features prueba? " + await p.StandardError.ReadToEndAsync()));
            return new ReleLocal(p, d.RootElement.GetProperty("url").GetString()!);
        }
        catch
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            p.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _proceso.StandardInput.Close();
            await _proceso.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            try { _proceso.Kill(true); } catch (InvalidOperationException) { }
        }
        _proceso.Dispose();
    }
}
