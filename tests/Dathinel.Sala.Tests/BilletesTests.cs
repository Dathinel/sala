using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>Billetes y entrada (docs/api.md §3): caducados, demasiadas solicitudes, sala llena, invitado que se va.</summary>
public sealed class BilletesTests
{
    private static async Task<(ProcesoRed Red, string Clave)> AnfitrionAsync()
    {
        ProcesoRed red = await ProcesoRed.AbrirAsync(RutaRed, default);
        JsonElement r = await red.LlamarAsync("abrir-sala", Base(Identidad.Nueva(), Carpeta()));
        return (red, r.GetProperty("clave").GetString()!);
    }

    private static async Task<string> BilleteAsync(ProcesoRed anf, int caducaEn = 600) =>
        (await anf.LlamarAsync("billete", new JsonObject { ["caducaEn"] = caducaEn, ["unUso"] = true })).GetProperty("billete").GetString()!;

    private static JsonObject Unirse(string billete)
    {
        JsonObject p = Base(Identidad.Nueva(), Carpeta());
        p["alias"] = "Luis";
        p["billete"] = billete;
        return p;
    }

    private static string Recodificar(string billete, Action<JsonObject> cambio)
    {
        string b64 = billete["dathinel1.".Length..].Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        JsonObject o = JsonNode.Parse(Convert.FromBase64String(b64))!.AsObject();
        cambio(o);
        return "dathinel1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Lee los eventos del anfitrión en segundo plano; con <paramref name="decidir"/>, acepta o rechaza.</summary>
    private static Task Escuchar(ProcesoRed anf, ConcurrentQueue<JsonElement> eventos, CancellationToken ct, bool? decidir = null) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            JsonElement r;
            try { r = await anf.LlamarAsync("recibir", new JsonObject(), ct); }
            catch (Exception) { return; }
            foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray())
            {
                eventos.Enqueue(ev.Clone());
                if (decidir is { } si && ev.GetProperty("tipo").GetString() == "solicitud")
                    await anf.LlamarAsync(si ? "aceptar" : "rechazar", new JsonObject { ["clave"] = ev.GetProperty("clave").GetString() }, ct);
            }
        }
    });

    [Fact]
    public async Task Un_billete_caducado_no_entra_ni_cambiandole_la_fecha()
    {
        var (anf, _) = await AnfitrionAsync();
        await using var _a = anf;
        string billete = await BilleteAsync(anf, caducaEn: 5);
        await Task.Delay(TimeSpan.FromSeconds(6));
        await using (ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default))
            Assert.Equal("billete-invalido", (await Assert.ThrowsAsync<SalaException>(() => inv.LlamarAsync("unirse", Unirse(billete)))).Codigo);
        // El invitado no puede alargarlo: la fecha que cuenta es la del anfitrión.
        string alargado = Recodificar(billete, o => o["caduca"] = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        await using (ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default))
            Assert.Equal("billete-invalido", (await Assert.ThrowsAsync<SalaException>(() => inv.LlamarAsync("unirse", Unirse(alargado)))).Codigo);
    }

    [Fact]
    public async Task Con_cuatro_esperando_la_quinta_solicitud_no_llega_a_la_app()
    {
        var (anf, _) = await AnfitrionAsync();
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        ConcurrentQueue<JsonElement> eventos = new();
        Task escucha = Escuchar(anf, eventos, fin.Token);

        List<ProcesoRed> invitados = [];
        List<Task> entradas = [];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default);
                invitados.Add(inv);
                entradas.Add(inv.LlamarAsync("unirse", Unirse(await BilleteAsync(anf)), fin.Token));
            }
            await HastaQue(() => eventos.Count(e => e.GetProperty("tipo").GetString() == "solicitud") == 4, que: "cuatro solicitudes");
            ProcesoRed quinto = await ProcesoRed.AbrirAsync(RutaRed, default);
            invitados.Add(quinto);
            SalaException e = await Assert.ThrowsAsync<SalaException>(() => quinto.LlamarAsync("unirse", Unirse(BilleteAsync(anf).Result)));
            Assert.Equal("ocupado", e.Codigo);
            await Task.Delay(300);
            Assert.Equal(4, eventos.Count(ev => ev.GetProperty("tipo").GetString() == "solicitud"));
            Assert.All(entradas, t => Assert.False(t.IsCompleted));
        }
        finally
        {
            await fin.CancelAsync();
            foreach (ProcesoRed i in invitados) await i.DisposeAsync();
        }
    }

    [Fact]
    public async Task La_conexion_17_encuentra_la_sala_llena()
    {
        var (anf, _) = await AnfitrionAsync();
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        Task escucha = Escuchar(anf, new(), fin.Token, decidir: true);
        List<ProcesoRed> invitados = [];
        try
        {
            for (int i = 0; i < 16; i++)
            {
                ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default);
                invitados.Add(inv);
                await inv.LlamarAsync("unirse", Unirse(await BilleteAsync(anf)));
            }
            ProcesoRed diecisiete = await ProcesoRed.AbrirAsync(RutaRed, default);
            invitados.Add(diecisiete);
            SalaException e = await Assert.ThrowsAsync<SalaException>(async () => await diecisiete.LlamarAsync("unirse", Unirse(await BilleteAsync(anf))));
            Assert.Equal("llena", e.Codigo);
        }
        finally
        {
            await fin.CancelAsync();
            foreach (ProcesoRed i in invitados) await i.DisposeAsync();
        }
    }

    [Fact]
    public async Task Si_el_invitado_se_va_esperando_aprobacion_el_billete_vuelve_a_servir()
    {
        var (anf, _) = await AnfitrionAsync();
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        ConcurrentQueue<JsonElement> eventos = new();
        Task escucha = Escuchar(anf, eventos, fin.Token);
        string billete = await BilleteAsync(anf);

        await using (ProcesoRed primero = await ProcesoRed.AbrirAsync(RutaRed, default))
        {
            using CancellationTokenSource corta = new();
            Task entra = primero.LlamarAsync("unirse", Unirse(billete), corta.Token);
            await HastaQue(() => eventos.Any(e => e.GetProperty("tipo").GetString() == "solicitud"), que: "solicitud");
            await corta.CancelAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => entra);
        }

        // Sin esperar al plazo de 10 min: el mismo billete llega otra vez como solicitud.
        await using ProcesoRed segundo = await ProcesoRed.AbrirAsync(RutaRed, default);
        Task otra = segundo.LlamarAsync("unirse", Unirse(billete), fin.Token);
        await HastaQue(() => eventos.Count(e => e.GetProperty("tipo").GetString() == "solicitud") == 2, segundos: 15, que: "segunda solicitud");
        Assert.False(otra.IsFaulted);
        await fin.CancelAsync();
    }
}
