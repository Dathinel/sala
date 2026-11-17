using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>Reconexión, traspaso, suplente (§5.6) y mDNS, con procesos reales.</summary>
public sealed class ConexionTests
{
    private static async Task<Sala> InvitadoAsync(Sala anf, string alias, OpcionesSala? o = null) =>
        await Sala.UnirseAsync(Identidad.Nueva(), o ?? Opciones(alias), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: TodoVale);

    [Fact]
    public async Task Si_se_cae_la_red_vuelve_sola_y_manda_lo_pendiente()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(anf, "Luis");
        ConcurrentQueue<long> ordenadas = new();
        luis.Ordenada += o => ordenadas.Enqueue(o.Seq);
        int reconectada = 0;
        luis.Reconectada += () => Interlocked.Increment(ref reconectada);

        await luis.EnviarAsync(Json("""{"antes":1}"""));
        await HastaQue(() => ordenadas.Count == 1, que: "la primera");
        await luis.CortarRedParaPruebasAsync();
        await luis.EnviarAsync(Json("""{"durante":2}"""));
        await HastaQue(() => reconectada == 1, segundos: 30, que: "reconectada");
        await HastaQue(() => ordenadas.Count == 2, que: "la pendiente entra al volver");
        Assert.Equal([1L, 2L], ordenadas);
        Assert.Equal(0, luis.Pendientes);
        Assert.Equal(2, anf.Seq);
    }

    [Fact]
    public async Task El_traspaso_pasa_la_sala_con_su_historial_y_todos_siguen()
    {
        await using Sala ana = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(ana, "Luis");
        await using Sala eva = await InvitadoAsync(ana, "Eva");
        for (int i = 0; i < 3; i++) await eva.EnviarAsync(Json($$"""{"n":{{i}}}"""));
        await HastaQue(() => luis.Seq == 3 && eva.Seq == 3, que: "tres antes");

        await ana.TraspasarAsync(luis.Clave);
        await HastaQue(() => luis.EsAnfitrion && eva.Anfitrion == luis.Clave && ana.Anfitrion == luis.Clave && !ana.EsAnfitrion, segundos: 40, que: "todos con luis");
        Assert.Equal(2, luis.Epoca);

        ConcurrentQueue<long> enAna = new();
        ana.Ordenada += o => enAna.Enqueue(o.Seq);
        await eva.EnviarAsync(Json("""{"despues":true}"""));
        await HastaQue(() => eva.Seq == 4 && luis.Seq == 4 && ana.Seq == 4, segundos: 30, que: "seq 4 en los tres");
        Assert.Equal([4L], enAna);
        Assert.Equal(2, eva.Epoca);
    }

    [Fact]
    public async Task Sin_anfitrion_el_suplente_toma_la_sala_y_el_otro_lo_encuentra()
    {
        OpcionesSala O(string a) => Opciones(a) with { EsperaSuplente = TimeSpan.FromSeconds(2) };
        Sala ana = await AnfitrionAsync(opciones: O("Ana"));
        await using Sala luis = await InvitadoAsync(ana, "Luis", O("Luis"));
        await using Sala eva = await InvitadoAsync(ana, "Eva", O("Eva"));
        await luis.EnviarAsync(Json("""{"n":1}"""));
        await HastaQue(() => eva.Seq == 1 && luis.Suplente is not null && eva.Suplente == luis.Suplente, que: "suplente conocido");
        Sala suplente = luis.Suplente == luis.Clave ? luis : eva, otro = suplente == luis ? eva : luis;

        await ana.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        await suplente.TomarAnfitrionAsync();
        Assert.True(suplente.EsAnfitrion);
        await HastaQue(() => otro.Anfitrion == suplente.Clave && otro.Epoca == 2, segundos: 60, que: "el otro con el suplente");
        await otro.EnviarAsync(Json("""{"n":2}"""));
        await HastaQue(() => suplente.Seq == 2 && otro.Seq == 2, segundos: 20, que: "sigue el orden");
    }

    [Fact]
    public async Task El_suplente_no_toma_la_sala_con_el_anfitrion_vivo()
    {
        await using Sala ana = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(ana, "Luis");
        await HastaQue(() => luis.Suplente == luis.Clave, que: "luis suplente");
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => luis.TomarAnfitrionAsync());
        Assert.Equal("invalid-parameters", e.Codigo);
    }

    [Fact]
    public async Task Con_mdns_se_entra_sin_direcciones()
    {
        OpcionesSala O(string a) => Opciones(a) with { Red = new Red("ninguno", Mdns: true) };
        await using Sala anf = await AnfitrionAsync(opciones: O("Ana"));
        string billete = await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10));
        // El mismo billete sin direcciones: solo se encuentra por mDNS en la red local.
        string b64 = billete["dathinel1.".Length..].Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        JsonObject o = JsonNode.Parse(Convert.FromBase64String(b64))!.AsObject();
        o["direcciones"] = new JsonArray();
        string sinDirecciones = "dathinel1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), O("Luis"), sinDirecciones);
        Assert.Equal(anf.Clave, luis.Anfitrion);
    }
}
