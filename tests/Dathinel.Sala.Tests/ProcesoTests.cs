using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>Dos dathinel-red en el mismo equipo, hablando el protocolo de módulos tal cual (docs/api.md §2–§4, §7).</summary>
public sealed class ProcesoTests
{
    private static async Task<(ProcesoRed Red, string Clave)> AnfitrionCrudoAsync(params string[] anclados)
    {
        ProcesoRed red = await ProcesoRed.AbrirAsync(RutaRed, default);
        JsonObject p = Base(Identidad.Nueva(), Carpeta());
        p["anclados"] = new JsonArray([.. anclados.Select(a => (JsonNode)a)]);
        JsonElement r = await red.LlamarAsync("abrir-sala", p);
        return (red, r.GetProperty("clave").GetString()!);
    }

    private static async Task<string> BilleteAsync(ProcesoRed anfitrion, bool unUso = true) =>
        (await anfitrion.LlamarAsync("billete", new JsonObject { ["caducaEn"] = 600, ["unUso"] = unUso })).GetProperty("billete").GetString()!;

    private static JsonObject Unirse(string identidad, string billete, string app = App, string version = VersionApp)
    {
        JsonObject p = Base(identidad, Carpeta(), app, version);
        p["alias"] = "Luis";
        p["billete"] = billete;
        return p;
    }

    /// <summary>Atiende las solicitudes del anfitrión crudo con <paramref name="si"/> mientras dure la prueba.</summary>
    private static async Task DecidirAsync(ProcesoRed anfitrion, bool si, ConcurrentQueue<JsonElement> otros, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonElement r;
            try { r = await anfitrion.LlamarAsync("recibir", new JsonObject(), ct); }
            catch (Exception) { return; }
            foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray())
            {
                if (ev.GetProperty("tipo").GetString() == "solicitud")
                    await anfitrion.LlamarAsync(si ? "aceptar" : "rechazar", new JsonObject { ["clave"] = ev.GetProperty("clave").GetString() }, ct);
                else otros.Enqueue(ev.Clone());
            }
        }
    }

    [Fact]
    public async Task Diez_mil_marcos_llegan_en_orden_y_un_billete_de_un_uso_no_sirve_dos_veces()
    {
        var (anf, clave) = await AnfitrionCrudoAsync();
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        ConcurrentQueue<JsonElement> eventos = new();
        Task decide = DecidirAsync(anf, true, eventos, fin.Token);

        string billete = await BilleteAsync(anf);
        await using ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default);
        JsonElement u = await inv.LlamarAsync("unirse", Unirse(Identidad.Nueva(), billete));
        Assert.Equal(clave, u.GetProperty("anfitrion").GetString());

        // El mismo billete, otra clave: ya está gastado.
        await using ProcesoRed otro = await ProcesoRed.AbrirAsync(RutaRed, default);
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => otro.LlamarAsync("unirse", Unirse(Identidad.Nueva(), billete)));
        Assert.Equal("billete-invalido", e.Codigo);

        const int N = 10_000;
        Task envio = Task.Run(async () =>
        {
            for (int i = 0; i < N; i++)
            {
                while (true)
                {
                    try
                    {
                        await anf.LlamarAsync("enviar", new JsonObject { ["datos"] = new JsonObject { ["type"] = "x", ["n"] = i } });
                        break;
                    }
                    catch (SalaException ex) when (ex.Codigo == "ocupado-red")
                    {
                        await Task.Delay(5);
                    }
                }
            }
        });
        int esperado = 0;
        while (esperado < N)
        {
            JsonElement r = await inv.LlamarAsync("recibir", new JsonObject { ["max"] = 1024 });
            foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray())
            {
                if (ev.GetProperty("tipo").GetString() != "mensaje") continue;
                Assert.Equal(esperado++, ev.GetProperty("datos").GetProperty("n").GetInt32());
            }
        }
        await envio;
        await fin.CancelAsync();
    }

    [Fact]
    public async Task Rechazado_otra_app_otra_version_y_sin_billete_no_entran()
    {
        var (anf, clave) = await AnfitrionCrudoAsync();
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        Task decide = DecidirAsync(anf, false, new(), fin.Token);

        async Task<string> Codigo(JsonObject p)
        {
            await using ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default);
            return (await Assert.ThrowsAsync<SalaException>(() => inv.LlamarAsync("unirse", p))).Codigo;
        }

        Assert.Equal("rechazado", await Codigo(Unirse(Identidad.Nueva(), await BilleteAsync(anf))));
        Assert.Equal("version", await Codigo(Unirse(Identidad.Nueva(), await BilleteAsync(anf), version: "9.9.9")));
        // Un billete de otra app ni siquiera se intenta.
        string otraApp = await BilleteAsync(anf);
        JsonObject p = Unirse(Identidad.Nueva(), otraApp, app: "otra");
        Assert.Equal("version", await Codigo(p));

        // Clave no anclada y sin billete.
        JsonObject sin = Base(Identidad.Nueva(), Carpeta());
        sin["alias"] = "Eva";
        sin["anfitrion"] = clave;
        sin["direcciones"] = new JsonArray(await DireccionesDe(anf));
        Assert.Equal("billete-invalido", await Codigo(sin));
        await fin.CancelAsync();
    }

    private static async Task<JsonNode[]> DireccionesDe(ProcesoRed anf)
    {
        string b = await BilleteAsync(anf);
        string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(b["dathinel1.".Length..])));
        return [.. JsonDocument.Parse(json).RootElement.GetProperty("direcciones").EnumerateArray().Select(d => (JsonNode)d.GetString()!)];
    }

    private static string Pad(string b64url)
    {
        string s = b64url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }

    [Fact]
    public async Task Billete_revocado_no_entra_y_un_anclado_vuelve_sin_billete()
    {
        string idInv = Identidad.Nueva();
        await using ProcesoRed sonda = await ProcesoRed.AbrirAsync(RutaRed, default);
        // La clave pública del invitado sale de abrir un proceso con su identidad.
        string clavePublica = (await sonda.LlamarAsync("abrir-sala", Base(idInv, Carpeta()))).GetProperty("clave").GetString()!;
        await sonda.LlamarAsync("cerrar");

        var (anf, clave) = await AnfitrionCrudoAsync(clavePublica);
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        Task decide = DecidirAsync(anf, true, new(), fin.Token);

        // Revocar uno deja vivo al otro; revocar todos, a ninguno.
        string uno = await BilleteAsync(anf, unUso: false), otro = await BilleteAsync(anf, unUso: false);
        string idUno = JsonDocument.Parse(Convert.FromBase64String(Pad(uno["dathinel1.".Length..]))).RootElement.GetProperty("id").GetString()!;
        await anf.LlamarAsync("revocar", new JsonObject { ["id"] = idUno });
        await using (ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default))
            Assert.Equal("billete-invalido", (await Assert.ThrowsAsync<SalaException>(() => inv.LlamarAsync("unirse", Unirse(Identidad.Nueva(), uno)))).Codigo);
        await using (ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default))
            await inv.LlamarAsync("unirse", Unirse(Identidad.Nueva(), otro));
        string billete = await BilleteAsync(anf, unUso: false);
        await anf.LlamarAsync("revocar", new JsonObject());
        await using (ProcesoRed inv = await ProcesoRed.AbrirAsync(RutaRed, default))
        {
            SalaException e = await Assert.ThrowsAsync<SalaException>(() => inv.LlamarAsync("unirse", Unirse(Identidad.Nueva(), billete)));
            Assert.Equal("billete-invalido", e.Codigo);
        }

        JsonObject p = Base(idInv, Carpeta());
        p["alias"] = "Luis";
        p["anfitrion"] = clave;
        p["direcciones"] = new JsonArray(await DireccionesDe(anf));
        await using ProcesoRed vuelve = await ProcesoRed.AbrirAsync(RutaRed, default);
        JsonElement r = await vuelve.LlamarAsync("unirse", p);
        Assert.Equal(clave, r.GetProperty("anfitrion").GetString());
        await fin.CancelAsync();
    }

    [Fact]
    public async Task Rafaga_por_encima_del_ritmo_corta_a_ese_par_y_el_anfitrion_sigue()
    {
        var (anf, _) = await AnfitrionCrudoAsync();
        await using var _a = anf;
        using CancellationTokenSource fin = new();
        ConcurrentQueue<JsonElement> eventos = new();
        Task decide = DecidirAsync(anf, true, eventos, fin.Token);

        await using ProcesoRed malo = await ProcesoRed.AbrirAsync(RutaRed, default);
        await malo.LlamarAsync("unirse", Unirse(Identidad.Nueva(), await BilleteAsync(anf)));
        await using ProcesoRed bueno = await ProcesoRed.AbrirAsync(RutaRed, default);
        await bueno.LlamarAsync("unirse", Unirse(Identidad.Nueva(), await BilleteAsync(anf)));

        for (int i = 0; i < 2000; i++)
        {
            try { await malo.LlamarAsync("enviar", new JsonObject { ["datos"] = new JsonObject { ["n"] = i } }); }
            catch (SalaException) { break; }
        }
        await HastaQue(() => eventos.Any(e => e.GetProperty("tipo").GetString() == "sale" && e.GetProperty("motivo").GetString() == "ritmo"), que: "sale por ritmo");

        await bueno.LlamarAsync("enviar", new JsonObject { ["datos"] = new JsonObject { ["hola"] = 1 } });
        await HastaQue(() => eventos.Any(e => e.GetProperty("tipo").GetString() == "mensaje" && e.GetProperty("datos").TryGetProperty("hola", out _)), que: "el bueno sigue");
        await fin.CancelAsync();
    }

    [Fact]
    public async Task Un_mensaje_de_mas_de_960_KiB_no_sale()
    {
        var (anf, _) = await AnfitrionCrudoAsync();
        await using var _a = anf;
        JsonObject grande = new() { ["x"] = new string('a', 961 * 1024) };
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => anf.LlamarAsync("enviar", new JsonObject { ["datos"] = grande }));
        Assert.Equal("demasiado-grande", e.Codigo);
    }

    [Fact]
    public async Task Sin_sala_abierta_las_funciones_dicen_no_abierta()
    {
        await using ProcesoRed red = await ProcesoRed.AbrirAsync(RutaRed, default);
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => red.LlamarAsync("billete", new JsonObject()));
        Assert.Equal("no-abierta", e.Codigo);
    }
}
