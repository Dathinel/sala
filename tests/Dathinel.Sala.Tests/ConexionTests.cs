using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
        await HastaQue(() => eva.Seq == 4 && luis.Seq == 4 && ana.Seq == 4 && !enAna.IsEmpty, segundos: 30, que: "seq 4 en los tres");
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
        // macOS 15 pide permiso de «Red local» para multidifusión a cada programa; el corredor de la CI no lo da y no
        // hay forma de concederlo sin ventana. Probado en Windows y Linux; en Mac lo prueba una persona.
        if (OperatingSystem.IsMacOS()) return;
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

    [Fact]
    public async Task Si_el_suplente_se_va_el_anfitrion_anuncia_otro()
    {
        await using Sala ana = await AnfitrionAsync();
        Sala luis = await InvitadoAsync(ana, "Luis");
        await using Sala eva = await InvitadoAsync(ana, "Eva");
        await HastaQue(() => eva.Suplente == luis.Clave, que: "luis suplente");
        await luis.DisposeAsync();
        await HastaQue(() => eva.Suplente == eva.Clave, que: "eva pasa a suplente");
    }

    [Fact]
    public async Task Si_el_sucesor_se_va_antes_de_confirmar_el_anfitrion_sigue_al_mando()
    {
        string carpeta = Carpeta();
        await using Sala ana = await AnfitrionAsync(opciones: Opciones("Ana", carpeta));
        Sala luis = await InvitadoAsync(ana, "Luis");
        await using Sala eva = await InvitadoAsync(ana, "Eva");
        await HastaQue(() => ana.Miembros.Count == 3 && luis.Suplente is not null, que: "dentro");
        Task traspaso = ana.TraspasarAsync(luis.Clave);
        await luis.DisposeAsync();
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => traspaso);
        Assert.Equal("traspaso", e.Codigo);
        // Antes la desconexión contaba como aceptación y Ana dejaba la sala sin anfitrión.
        Assert.True(ana.EsAnfitrion);
        Assert.Equal(1, ana.Epoca);
        await eva.EnviarAsync(Json("""{"sigue":1}"""));
        await HastaQue(() => eva.Seq == 1 && ana.Seq == 1, que: "eva sigue con ana");
        // Y no deja el historial ofrecido en disco.
        string sala = Path.Combine(carpeta, ".sala");
        Assert.True(!Directory.Exists(sala) || !Directory.EnumerateFiles(sala, "historial-*").Any());
    }

    [Fact]
    public async Task El_sucesor_que_entro_tarde_completa_el_historial_y_repone_todo_a_quien_llega_despues()
    {
        await using Sala ana = await AnfitrionAsync();
        for (int i = 0; i < 5; i++) await ana.EnviarAsync(Json($$"""{"n":{{i}}}"""));
        await HastaQue(() => ana.Seq == 5, que: "cinco");
        // Luis entra diciendo que ya tiene hasta el 3: su historial empieza en el 4.
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)),
            ultimoSeq: 3, validar: TodoVale);
        await HastaQue(() => luis.Seq == 5 && luis.Suplente == luis.Clave, que: "luis al día");
        await ana.TraspasarAsync(luis.Clave);
        await HastaQue(() => luis.EsAnfitrion && ana.Anfitrion == luis.Clave, segundos: 40, que: "luis manda");
        luis.SolicitudRecibida += s => _ = luis.AceptarAsync(s.Clave, Rol.Editar);

        // Eva llega de cero al anfitrión nuevo: los cinco, aceptados en la época 1, le llegan aunque mande la 2.
        ConcurrentQueue<Ordenado> enEva = new();
        int desfasada = 0;
        await using Sala eva = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Eva"), await luis.NuevoBilleteAsync(TimeSpan.FromMinutes(10)),
            suscribir: s => { s.Ordenada += o => enEva.Enqueue(o); s.Desfasada += _ => Interlocked.Increment(ref desfasada); });
        await HastaQue(() => enEva.Count == 5, que: "los cinco en eva");
        Assert.Equal(0, desfasada);
        Assert.All(enEva, o => Assert.Equal(1, o.Epoca));
        Assert.Equal(2, eva.Epoca);
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Las_cargas_que_llegan_durante_el_traspaso_entran_una_vez()
    {
        await using Sala ana = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(ana, "Luis");
        await using Sala eva = await InvitadoAsync(ana, "Eva");
        await HastaQue(() => luis.Suplente == luis.Clave && eva.Suplente == luis.Clave, que: "suplente");
        ConcurrentQueue<Guid> enEva = new();
        eva.Ordenada += o => enEva.Enqueue(o.Id);
        Task traspaso = ana.TraspasarAsync(luis.Clave);
        Guid deLuis = await luis.EnviarAsync(Json("""{"durante":"luis"}"""));
        Guid deEva = await eva.EnviarAsync(Json("""{"durante":"eva"}"""));
        Guid deAna = await ana.EnviarAsync(Json("""{"durante":"ana"}"""));
        await traspaso;
        await HastaQue(() => luis.EsAnfitrion && luis.Seq == 3 && eva.Seq == 3 && ana.Seq == 3, segundos: 60, que: "las tres en todos");
        Assert.Equal(0, luis.Pendientes + eva.Pendientes + ana.Pendientes);
        Assert.Equal(3, enEva.Distinct().Count());
        Assert.Contains(deLuis, enEva);
        Assert.Contains(deEva, enEva);
        Assert.Contains(deAna, enEva);
    }

    [Fact]
    public async Task Ida_y_vuelta_del_traspaso_deja_un_solo_ordenador()
    {
        int dentro = 0, maximo = 0;
        Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>> validar = async (_, _, _) =>
        {
            int n = Interlocked.Increment(ref dentro);
            int m;
            while ((m = Volatile.Read(ref maximo)) < n && Interlocked.CompareExchange(ref maximo, n, m) != m) { }
            await Task.Delay(20, CancellationToken.None);
            Interlocked.Decrement(ref dentro);
            return Veredicto.Si;
        };
        await using Sala ana = await AnfitrionAsync(validar);
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: validar);
        await HastaQue(() => luis.Suplente == luis.Clave, que: "suplente");
        await ana.TraspasarAsync(luis.Clave);
        await HastaQue(() => luis.EsAnfitrion && ana.Anfitrion == luis.Clave && ana.Suplente == ana.Clave, segundos: 40, que: "a luis");
        await luis.TraspasarAsync(ana.Clave);
        await HastaQue(() => ana.EsAnfitrion && luis.Anfitrion == ana.Clave, segundos: 40, que: "y vuelta a ana");
        Assert.Equal(3, ana.Epoca);
        for (int i = 0; i < 20; i++) await luis.EnviarAsync(Json($$"""{"n":{{i}}}"""));
        for (int i = 0; i < 20; i++) await ana.EnviarAsync(Json($$"""{"m":{{i}}}"""));
        await HastaQue(() => ana.Seq == 40 && luis.Seq == 40, segundos: 30, que: "cuarenta");
        Assert.Equal(1, maximo);
    }

    [Fact]
    public async Task Tras_desfasado_la_app_confirma_su_instantanea_y_la_sala_sigue()
    {
        await using Sala ana = await AnfitrionAsync(seq: 10);
        TaskCompletionSource<long> desfasada = new();
        ConcurrentQueue<long> ordenadas = new();
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)),
            suscribir: s => { s.Desfasada += d => desfasada.TrySetResult(d); s.Ordenada += o => ordenadas.Enqueue(o.Seq); });
        await desfasada.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // La app pidió y aplicó la instantánea (directo y blobs) del estado 10; lo confirma sin recrear la sala.
        await luis.ConfirmarInstantaneaAsync(ana.Epoca, ana.Seq);
        await ana.EnviarAsync(Json("""{"once":11}"""));
        await HastaQue(() => luis.Seq == 11, que: "el 11 llega como siguiente");
        Assert.Equal([11L], ordenadas);
    }

    [Fact]
    public async Task Cambiar_el_rol_a_mirar_suelta_sus_reservas_y_ya_no_edita()
    {
        await using Sala ana = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(ana, "Luis");
        Assert.True(await luis.ReservarAsync("pista:a"));
        await ana.CambiarRolAsync(luis.Clave, Rol.Mirar);
        await HastaQue(() => luis.Rol == Rol.Mirar && !luis.Reservas.ContainsKey("pista:a"), que: "luis mira");
        TaskCompletionSource<Rechazo> r = new();
        luis.Rechazada += x => r.TrySetResult(x);
        await luis.EnviarAsync(Json("""{"a":1}"""));
        Assert.Equal("rol", (await r.Task.WaitAsync(TimeSpan.FromSeconds(20))).Motivo);
        await ana.CambiarRolAsync(luis.Clave, Rol.Editar);
        await HastaQue(() => luis.Rol == Rol.Editar, que: "vuelve a editar");
    }
}
