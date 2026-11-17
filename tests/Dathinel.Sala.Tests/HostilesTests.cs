using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>
/// Pruebas hostiles de la sala (docs/api.md §7): un par que no usa Dathinel.Sala sino dathinel-red a mano y manda lo
/// que quiere. Criterio: el anfitrión sigue, el estado no cambia y nada se aplica dos veces.
/// </summary>
public sealed class HostilesTests
{
    /// <summary>Un invitado crudo dentro de la sala de <paramref name="anf"/>, con los eventos que le llegan.</summary>
    private sealed class Crudo : IAsyncDisposable
    {
        public required ProcesoRed Red { get; init; }
        public ConcurrentQueue<JsonElement> Llegan { get; } = new();
        private readonly CancellationTokenSource _fin = new();
        private Task _lee = Task.CompletedTask;

        public static async Task<Crudo> EntrarAsync(Sala anf)
        {
            Crudo c = new() { Red = await ProcesoRed.AbrirAsync(RutaRed, default) };
            JsonObject p = Base(Identidad.Nueva(), Carpeta());
            p["alias"] = "Malo";
            p["billete"] = await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10));
            await c.Red.LlamarAsync("unirse", p);
            c._lee = Task.Run(async () =>
            {
                while (!c._fin.IsCancellationRequested)
                {
                    try
                    {
                        JsonElement r = await c.Red.LlamarAsync("recibir", new JsonObject(), c._fin.Token);
                        foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray()) c.Llegan.Enqueue(ev.Clone());
                    }
                    catch (Exception) { return; }
                }
            });
            return c;
        }

        public Task<JsonElement> MandarAsync(JsonObject m) => Red.LlamarAsync("enviar", new JsonObject { ["datos"] = m });

        public IEnumerable<JsonElement> Mensajes(string type) => Llegan
            .Where(e => e.GetProperty("tipo").GetString() == "mensaje" && e.GetProperty("datos").GetProperty("type").GetString() == type)
            .Select(e => e.GetProperty("datos"));

        public bool Cerrada => Llegan.Any(e => e.GetProperty("tipo").GetString() == "cerrada");

        public async ValueTask DisposeAsync()
        {
            await _fin.CancelAsync();
            await Red.DisposeAsync();
            _fin.Dispose();
        }
    }

    private static JsonObject Carga(Guid id, int n) => new() { ["type"] = "carga", ["epoca"] = 1, ["id"] = id.ToString(), ["datos"] = new JsonObject { ["n"] = n } };

    [Fact]
    public async Task Una_carga_repetida_se_ordena_una_vez_y_se_contesta_igual()
    {
        int validadas = 0;
        await using Sala anf = await AnfitrionAsync((_, _, _) => { Interlocked.Increment(ref validadas); return ValueTask.FromResult(Veredicto.Si); });
        await using Crudo malo = await Crudo.EntrarAsync(anf);
        Guid id = Guid.NewGuid();
        for (int i = 0; i < 5; i++) await malo.MandarAsync(Carga(id, 1));
        await HastaQue(() => malo.Mensajes("orden").Count() == 5, que: "cinco respuestas");
        Assert.Equal(1, validadas);
        Assert.Equal(1, anf.Seq);
        Assert.All(malo.Mensajes("orden"), o => Assert.Equal(1, o.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task Cargas_por_encima_del_ritmo_se_rechazan_y_seguir_asi_expulsa()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Crudo malo = await Crudo.EntrarAsync(anf);
        // 80/s en tandas de 8 cada 100 ms, mandadas a la vez: cuatro veces el ritmo de la sala (20/s) y menos que el
        // del transporte (100/s). En serie, en la CI de Windows cada llamada tardaba tanto que no se pasaba del doble.
        using PeriodicTimer reloj = new(TimeSpan.FromMilliseconds(100));
        DateTime fin = DateTime.UtcNow.AddSeconds(10);
        int n = 0;
        while (DateTime.UtcNow < fin && !malo.Cerrada && await reloj.WaitForNextTickAsync())
        {
            try
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => malo.MandarAsync(Carga(Guid.NewGuid(), Interlocked.Increment(ref n)))));
            }
            catch (SalaException) { break; }
        }
        await HastaQue(() => malo.Cerrada, que: "expulsado");
        Assert.Contains(malo.Mensajes("rechazado"), r => r.GetProperty("motivo").GetString() == "ritmo");
        Assert.True(anf.Seq is >= 40 and < 200, $"seq {anf.Seq}");
        Assert.DoesNotContain(anf.Miembros, m => m.Alias == "Malo");

        // El anfitrión sigue sirviendo a otros.
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)));
        TaskCompletionSource ok = new();
        luis.Ordenada += _ => ok.TrySetResult();
        await luis.EnviarAsync(Json("""{"sigue":true}"""));
        await ok.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Una_carga_de_mas_de_900_KiB_de_un_par_se_rechaza()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Crudo malo = await Crudo.EntrarAsync(anf);
        JsonObject c = Carga(Guid.NewGuid(), 0);
        c["datos"] = new JsonObject { ["x"] = new string('a', 920 * 1024) };
        await malo.MandarAsync(c);
        await HastaQue(() => malo.Mensajes("rechazado").Any(), que: "rechazo");
        Assert.Equal("grande", malo.Mensajes("rechazado").Single().GetProperty("motivo").GetString());
        Assert.Equal(0, anf.Seq);
    }

    [Fact]
    public async Task Mensajes_mal_formados_se_descartan_sin_tumbar_al_anfitrion()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Crudo malo = await Crudo.EntrarAsync(anf);
        JsonObject[] basura =
        [
            new() { ["type"] = "carga" },
            new() { ["type"] = "carga", ["id"] = "no-es-guid", ["datos"] = new JsonObject() },
            new() { ["type"] = "carga", ["id"] = Guid.NewGuid().ToString(), ["datos"] = "texto" },
            new() { ["type"] = "sincronizar", ["epoca"] = "uno" },
            new() { ["type"] = "orden", ["seq"] = 99, ["datos"] = new JsonObject() },
            new() { ["sin"] = "tipo" },
            new() { ["type"] = "directo", ["datos"] = 5 },
            new() { ["type"] = "carga", ["id"] = null, ["datos"] = new JsonObject() },
            new() { ["type"] = "carga", ["id"] = 123, ["datos"] = new JsonObject() },
            new() { ["type"] = null },
        ];
        foreach (JsonObject b in basura) await malo.MandarAsync(b);
        await malo.MandarAsync(Carga(Guid.NewGuid(), 7));
        await HastaQue(() => anf.Seq == 1, que: "la carga buena entra");
        Assert.Equal(1, anf.Seq);
    }

    [Fact]
    public async Task Quien_vuelve_de_muy_atras_recibe_desfasado()
    {
        await using Sala anf = await AnfitrionAsync(seq: 10);
        await using Crudo c = await Crudo.EntrarAsync(anf);
        await c.MandarAsync(new JsonObject { ["type"] = "sincronizar", ["epoca"] = 1, ["ultimoSeq"] = 0 });
        await HastaQue(() => c.Mensajes("desfasado").Any(), que: "desfasado");
        Assert.Equal(10, c.Mensajes("bienvenida").Single().GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Un_anfitrion_hostil_con_epoca_vieja_no_cambia_nada()
    {
        // Anfitrión crudo que ancla al invitado y le contesta a mano.
        string idInv = Identidad.Nueva();
        string clavePublica;
        await using (ProcesoRed sonda = await ProcesoRed.AbrirAsync(RutaRed, default))
            clavePublica = (await sonda.LlamarAsync("abrir-sala", Base(idInv, Carpeta()))).GetProperty("clave").GetString()!;

        await using ProcesoRed hostil = await ProcesoRed.AbrirAsync(RutaRed, default);
        JsonObject p = Base(Identidad.Nueva(), Carpeta());
        p["anclados"] = new JsonArray(clavePublica);
        JsonElement abierto = await hostil.LlamarAsync("abrir-sala", p);
        string claveHostil = abierto.GetProperty("clave").GetString()!;
        string[] dirs = [.. abierto.GetProperty("direcciones").EnumerateArray().Select(d => d.GetString()!)];

        using CancellationTokenSource fin = new();
        Task responde = Task.Run(async () =>
        {
            while (!fin.IsCancellationRequested)
            {
                JsonElement r;
                try { r = await hostil.LlamarAsync("recibir", new JsonObject(), fin.Token); }
                catch (Exception) { return; }
                foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray())
                {
                    if (ev.GetProperty("tipo").GetString() != "mensaje") continue;
                    if (ev.GetProperty("datos").GetProperty("type").GetString() != "sincronizar") continue;
                    async Task M(JsonObject m) => await hostil.LlamarAsync("enviar", new JsonObject { ["datos"] = m });
                    await M(new JsonObject { ["type"] = "bienvenida", ["epoca"] = 5, ["seq"] = 0, ["miembros"] = new JsonArray(), ["reservas"] = new JsonArray() });
                    await M(new JsonObject { ["type"] = "orden", ["epoca"] = 4, ["seq"] = 1, ["id"] = Guid.NewGuid().ToString(), ["autor"] = claveHostil, ["datos"] = new JsonObject { ["viejo"] = true } });
                    await M(new JsonObject { ["type"] = "orden", ["epoca"] = 5, ["seq"] = 1, ["id"] = Guid.NewGuid().ToString(), ["autor"] = claveHostil, ["datos"] = new JsonObject { ["nuevo"] = true } });
                    await M(new JsonObject { ["type"] = "orden", ["epoca"] = 5, ["seq"] = 1, ["id"] = Guid.NewGuid().ToString(), ["autor"] = claveHostil, ["datos"] = new JsonObject { ["repetido"] = true } });
                }
            }
        });

        ConcurrentQueue<Ordenado> ordenadas = new();
        await using Sala inv = await Sala.UnirseAsync(idInv, Opciones("Luis"), anfitrion: claveHostil, direcciones: dirs,
            suscribir: s => s.Ordenada += o => ordenadas.Enqueue(o));
        await HastaQue(() => inv.Seq == 1, que: "la de época 5");
        await Task.Delay(300);
        Assert.Equal(5, inv.Epoca);
        Ordenado o = Assert.Single(ordenadas);
        Assert.True(o.Datos.TryGetProperty("nuevo", out _));
        await fin.CancelAsync();
    }

    [Fact]
    public void La_cuota_deja_pasar_el_ritmo_rechaza_el_exceso_y_expulsa_si_sigue()
    {
        Sala.Cuota c = new();
        DateTime t = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        // La ráfaga de 40 pasa entera, y después 20/s sostenidos también.
        for (int i = 0; i < 40; i++) Assert.Equal(Sala.Paso.Pasa, c.Tomar(t));
        for (int i = 0; i < 100; i++) Assert.Equal(Sala.Paso.Pasa, c.Tomar(t = t.AddMilliseconds(50)));
        // Al doble del ritmo: la mitad sale por ritmo y a los 3 s, expulsión.
        Sala.Paso ultimo = Sala.Paso.Pasa;
        int n = 0;
        while (ultimo != Sala.Paso.Expulsar && n++ < 1000) ultimo = c.Tomar(t = t.AddMilliseconds(25));
        Assert.Equal(Sala.Paso.Expulsar, ultimo);
        Assert.InRange(n * 25, 2900, 3200);
    }
}
