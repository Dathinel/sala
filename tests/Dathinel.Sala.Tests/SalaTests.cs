using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>La sala de punta a punta con dos o tres procesos (docs/api.md §5) y sus pruebas hostiles (§7).</summary>
public sealed class SalaTests
{
    private static async Task<Sala> InvitadoAsync(Sala anfitrion, string alias = "Luis", OpcionesSala? o = null) =>
        await Sala.UnirseAsync(Identidad.Nueva(), o ?? Opciones(alias), await anfitrion.NuevoBilleteAsync(TimeSpan.FromMinutes(10)));

    [Fact]
    public async Task Las_cargas_de_todos_llegan_a_todos_en_el_mismo_orden()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(anf);
        await using Sala eva = await InvitadoAsync(anf, "Eva");
        ConcurrentQueue<long> enAnf = new(), enLuis = new(), enEva = new();
        ConcurrentQueue<Guid> idsAnf = new(), idsLuis = new(), idsEva = new();
        anf.Ordenada += o => { enAnf.Enqueue(o.Seq); idsAnf.Enqueue(o.Id); };
        luis.Ordenada += o => { enLuis.Enqueue(o.Seq); idsLuis.Enqueue(o.Id); };
        eva.Ordenada += o => { enEva.Enqueue(o.Seq); idsEva.Enqueue(o.Id); };

        for (int i = 0; i < 30; i++)
        {
            await luis.EnviarAsync(Json($$"""{"de":"luis","n":{{i}}}"""));
            await eva.EnviarAsync(Json($$"""{"de":"eva","n":{{i}}}"""));
            await anf.EnviarAsync(Json($$"""{"de":"ana","n":{{i}}}"""));
        }
        await HastaQue(() => enLuis.Count == 90 && enEva.Count == 90 && enAnf.Count == 90, que: "90 en cada uno");
        Assert.Equal(Enumerable.Range(1, 90).Select(i => (long)i), enLuis);
        Assert.Equal(idsAnf, idsLuis);
        Assert.Equal(idsAnf, idsEva);
        Assert.Equal(3, luis.Miembros.Count);
    }

    [Fact]
    public async Task La_huella_es_igual_en_los_dos_lados_y_rechazar_deja_fuera()
    {
        await using Sala anf = await Sala.HospedarAsync(Identidad.Nueva(), Opciones("Ana"), [], TodoVale);
        TaskCompletionSource<Solicitud> vista = new();
        anf.SolicitudRecibida += s => { vista.TrySetResult(s); _ = anf.RechazarAsync(s.Clave); };
        string billete = await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10));
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), billete));
        Assert.Equal("rechazado", e.Codigo);
        Solicitud s = await vista.Task;
        Assert.Equal("Luis", s.Alias);
        Assert.Equal(4, s.Huella.Split('-').Length);
    }

    [Fact]
    public async Task Mirar_no_puede_editar()
    {
        await using Sala anf = await AnfitrionAsync(rol: Rol.Mirar);
        await using Sala profe = await InvitadoAsync(anf, "Profe");
        TaskCompletionSource<Rechazo> r = new();
        profe.Rechazada += x => r.TrySetResult(x);
        Guid id = await profe.EnviarAsync(Json("""{"a":1}"""));
        Rechazo x = await r.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(id, x.Id);
        Assert.Equal("rol", x.Motivo);
        Assert.Equal(Rol.Mirar, profe.Rol);
        Assert.Equal(0, anf.Seq);
    }

    [Fact]
    public async Task Una_carga_con_rutas_llega_intacta_a_validar_y_nada_toca_el_disco()
    {
        string carpeta = Carpeta();
        ConcurrentQueue<string> vistos = new();
        await using Sala anf = await AnfitrionAsync((_, d, _) => { vistos.Enqueue(d.GetRawText()); return ValueTask.FromResult(Veredicto.Si); },
            opciones: Opciones("Ana", carpeta));
        string carpetaInv = Carpeta();
        await using Sala luis = await InvitadoAsync(anf, o: Opciones("Luis", carpetaInv));
        TaskCompletionSource<Ordenado> llego = new();
        luis.Ordenada += o => llego.TrySetResult(o);

        string hostil = """{"ruta":"\\\\equipo\\recurso\\x.cube","lut":"file:///C:/Windows/win.ini","rel":"..\\..\\..\\AppData\\x","abs":"C:\\x","u":"/etc/passwd"}""";
        await luis.EnviarAsync(Json(hostil));
        Ordenado o = await llego.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(hostil), JsonNode.Parse(vistos.Single())), vistos.Single());
        Assert.Equal("""\\equipo\recurso\x.cube""", o.Datos.GetProperty("ruta").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(carpeta));
        Assert.Empty(Directory.EnumerateFileSystemEntries(carpetaInv));
    }

    [Fact]
    public async Task Un_validar_colgado_sale_por_tiempo_y_la_sala_sigue()
    {
        int llamadas = 0;
        await using Sala anf = await AnfitrionAsync(async (_, d, ct) =>
        {
            if (Interlocked.Increment(ref llamadas) == 1) await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return Veredicto.Si;
        }, opciones: Opciones("Ana") with { TopeValidar = TimeSpan.FromSeconds(1) });
        await using Sala luis = await InvitadoAsync(anf);
        ConcurrentQueue<string> fin = new();
        luis.Rechazada += r => fin.Enqueue("no:" + r.Motivo);
        luis.Ordenada += o => fin.Enqueue("si");
        await luis.EnviarAsync(Json("""{"a":1}"""));
        await luis.EnviarAsync(Json("""{"a":2}"""));
        await HastaQue(() => fin.Count == 2, que: "las dos respuestas");
        Assert.Equal(["no:tiempo", "si"], fin);
    }

    [Fact]
    public async Task Una_carga_de_mas_de_900_KiB_no_sale_del_cliente()
    {
        await using Sala anf = await AnfitrionAsync();
        string grande = $$"""{"x":"{{new string('a', 901 * 1024)}}"}""";
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => anf.EnviarAsync(Json(grande)));
        Assert.Equal("demasiado-grande", e.Codigo);
    }

    [Fact]
    public async Task Directos_van_y_vuelven_sin_numerar()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(anf);
        TaskCompletionSource<Directo> enAnf = new(), enLuis = new();
        anf.DirectoRecibido += d => enAnf.TrySetResult(d);
        luis.DirectoRecibido += d => enLuis.TrySetResult(d);
        await luis.EnviarDirectoAsync(Json("""{"faltan":["ab"]}"""));
        Directo d1 = await enAnf.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(luis.Clave, d1.De);
        await anf.EnviarDirectoAsync(Json("""{"manifiesto":1}"""), luis.Clave);
        Directo d2 = await enLuis.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(anf.Clave, d2.De);
        Assert.Equal(0, anf.Seq);
    }

    [Fact]
    public async Task Un_validar_que_se_bloquea_sin_devolver_la_tarea_tambien_sale_por_tiempo()
    {
        int llamadas = 0;
        await using Sala anf = await AnfitrionAsync((_, _, _) =>
        {
            // Trabajo síncrono antes de devolver el ValueTask: antes paraba el ordenador hasta que acababa.
            if (Interlocked.Increment(ref llamadas) == 1) Thread.Sleep(TimeSpan.FromSeconds(8));
            return ValueTask.FromResult(Veredicto.Si);
        }, opciones: Opciones("Ana") with { TopeValidar = TimeSpan.FromSeconds(1) });
        await using Sala luis = await InvitadoAsync(anf);
        ConcurrentQueue<string> fin = new();
        luis.Rechazada += r => fin.Enqueue("no:" + r.Motivo);
        luis.Ordenada += o => fin.Enqueue("si");
        DateTime t0 = DateTime.UtcNow;
        await luis.EnviarAsync(Json("""{"a":1}"""));
        await luis.EnviarAsync(Json("""{"a":2}"""));
        await HastaQue(() => fin.Count == 2, que: "las dos respuestas");
        Assert.Equal(["no:tiempo", "si"], fin);
        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(6), "la segunda no esperó al validador bloqueado");
    }

    [Fact]
    public async Task Una_carga_no_entra_si_mientras_se_valida_su_autor_pierde_la_reserva_o_lo_expulsan()
    {
        SemaphoreSlim entra = new(0), suelta = new(0);
        await using Sala anf = await AnfitrionAsync(async (_, _, _) => { entra.Release(); await suelta.WaitAsync(CancellationToken.None); return Veredicto.Si; });
        await using Sala luis = await InvitadoAsync(anf);
        await using Sala eva = await InvitadoAsync(anf, "Eva");
        TaskCompletionSource<Rechazo> rechazo = new();
        luis.Rechazada += r => rechazo.TrySetResult(r);

        Assert.True(await luis.ReservarAsync("pista:a"));
        await luis.EnviarAsync(Json("""{"a":1}"""), ["pista:a"]);
        await entra.WaitAsync(TimeSpan.FromSeconds(20));
        await luis.SoltarAsync("pista:a");
        await HastaQue(() => !anf.Reservas.ContainsKey("pista:a"), que: "suelta");
        suelta.Release();
        Assert.Equal("reserva", (await rechazo.Task.WaitAsync(TimeSpan.FromSeconds(20))).Motivo);

        await eva.EnviarAsync(Json("""{"b":1}"""));
        await entra.WaitAsync(TimeSpan.FromSeconds(20));
        await anf.ExpulsarAsync(eva.Clave);
        suelta.Release();
        await Task.Delay(500);
        Assert.Equal(0, anf.Seq);
    }
}
