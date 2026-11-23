using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>Reservas (§5.4), presencia (§5.5) y blobs por huella (§5.7), con procesos reales.</summary>
public sealed class ReservasBlobsTests : IDisposable
{
    private readonly List<string> _carpetas = [];

    private string NuevaCarpeta()
    {
        string carpeta = Carpeta();
        _carpetas.Add(carpeta);
        return carpeta;
    }

    public void Dispose()
    {
        foreach (string carpeta in _carpetas)
            Directory.Delete(carpeta, recursive: true);
    }

    private static async Task<Sala> InvitadoAsync(Sala anf, string alias, OpcionesSala? o = null) =>
        await Sala.UnirseAsync(Identidad.Nueva(), o ?? Opciones(alias), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)));

    [Fact]
    public async Task Una_reserva_es_de_uno_y_las_cargas_sobre_ella_solo_entran_de_su_dueno()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(anf, "Luis");
        await using Sala eva = await InvitadoAsync(anf, "Eva");
        ConcurrentQueue<(string, string?)> enEva = new();
        eva.ReservaCambia += (c, d) => enEva.Enqueue((c, d));

        Assert.True(await luis.ReservarAsync("pista:a2"));
        Assert.True(await luis.ReservarAsync("pista:a2")); // ya era suya
        Assert.False(await eva.ReservarAsync("pista:a2"));
        await HastaQue(() => eva.Reservas.TryGetValue("pista:a2", out string? d) && d == luis.Clave, que: "eva la ve");

        ConcurrentQueue<string> fin = new();
        eva.Rechazada += r => fin.Enqueue("eva:" + r.Motivo);
        luis.Ordenada += o => fin.Enqueue("orden");
        await eva.EnviarAsync(Json("""{"a":1}"""), ["pista:a2"]);
        await luis.EnviarAsync(Json("""{"a":2}"""), ["pista:a2"]);
        await HastaQue(() => fin.Count == 2, que: "dos respuestas");
        Assert.Contains("eva:reserva", fin);
        Assert.Contains("orden", fin);

        await luis.SoltarAsync("pista:a2");
        await HastaQue(() => enEva.Contains(("pista:a2", null)), que: "suelta");
        Assert.True(await eva.ReservarAsync("pista:a2"));
    }

    [Fact]
    public async Task Una_reserva_sin_actividad_se_suelta_sola_y_mirar_no_reserva()
    {
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana") with { ReservaInactiva = TimeSpan.FromSeconds(4) });
        await using Sala luis = await InvitadoAsync(anf, "Luis");
        Assert.True(await luis.ReservarAsync("seccion:cad/tapa"));
        await HastaQue(() => !luis.Reservas.ContainsKey("seccion:cad/tapa"), segundos: 10, que: "caduca");
        await Assert.ThrowsAsync<ArgumentException>(() => luis.ReservarAsync("../fuera"));
        Assert.False(Sala.ClaveDeReservaValida("C:\\x"));
        Assert.False(Sala.ClaveDeReservaValida("/abs"));
        Assert.True(Sala.ClaveDeReservaValida("codigo:3f2a/src/main.cpp"));
    }

    [Fact]
    public async Task Mirar_no_puede_reservar()
    {
        await using Sala anf = await AnfitrionAsync(rol: Rol.Mirar);
        await using Sala profe = await InvitadoAsync(anf, "Profe");
        Assert.False(await profe.ReservarAsync("pista:a1"));
        Assert.Empty(anf.Reservas);
    }

    [Fact]
    public async Task La_presencia_llega_a_los_demas_y_no_a_quien_la_manda()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(anf, "Luis");
        await using Sala eva = await InvitadoAsync(anf, "Eva");
        ConcurrentQueue<string> enAnf = new(), enEva = new(), enLuis = new();
        anf.PresenciaRecibida += (de, _) => enAnf.Enqueue(de);
        eva.PresenciaRecibida += (de, _) => enEva.Enqueue(de);
        luis.PresenciaRecibida += (de, _) => enLuis.Enqueue(de);
        await luis.PresenciaAsync(Json("""{"cabezal":120}"""));
        await anf.PresenciaAsync(Json("""{"cabezal":7}"""));
        await HastaQue(() => enAnf.Contains(luis.Clave) && enEva.Contains(luis.Clave) && enEva.Contains(anf.Clave) && enLuis.Contains(anf.Clave), que: "presencias");
        Assert.DoesNotContain(luis.Clave, enLuis);
        await Assert.ThrowsAsync<ArgumentException>(() => luis.PresenciaAsync(Json($$"""{"x":"{{new string('a', 5000)}}"}""")));
        Assert.Equal(0, anf.Seq);
    }

    private static (string Ruta, string Huella, long Tamano) Archivo(string carpeta, int bytes)
    {
        string ruta = Path.Combine(carpeta, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(ruta, RandomNumberGenerator.GetBytes(bytes));
        return (ruta, Sala.HuellaDe(ruta), bytes);
    }

    [Fact]
    public async Task Un_blob_ofrecido_llega_entero_y_comprobado()
    {
        string cAna = NuevaCarpeta(), cLuis = NuevaCarpeta();
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana", cAna));
        await using Sala luis = await InvitadoAsync(anf, "Luis", Opciones("Luis", cLuis));
        var b = Archivo(cAna, 3 * 1024 * 1024 + 17);
        await anf.OfrecerBlobsAsync([b]);
        string destino = Path.Combine(cLuis, "copia.bin");
        await luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano, destino, tope: 10 << 20);
        Assert.Equal(File.ReadAllBytes(b.Ruta), File.ReadAllBytes(destino));

        // Al revés también: el anfitrión baja lo que ofrece un invitado.
        var c = Archivo(cLuis, 1000);
        await luis.OfrecerBlobsAsync([c]);
        await anf.PedirBlobAsync(luis.Clave, c.Huella, c.Tamano, Path.Combine(cAna, "de-luis.bin"), tope: 1 << 20);
    }

    [Fact]
    public async Task Blobs_hostiles_no_escriben_nada()
    {
        string cAna = NuevaCarpeta(), cLuis = NuevaCarpeta();
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana", cAna));
        await using Sala luis = await InvitadoAsync(anf, "Luis", Opciones("Luis", cLuis));
        var b = Archivo(cAna, 200_000);
        await anf.OfrecerBlobsAsync([b]);
        int avances = 0;
        luis.BlobAvance += (_, _) => Interlocked.Increment(ref avances);

        async Task<string> Codigo(Func<Task> f) => (await Assert.ThrowsAsync<SalaException>(f)).Codigo;

        // Mayor que el tope de quien pide: se rechaza antes de abrir el stream.
        Assert.Equal("demasiado-grande", await Codigo(() => luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano, Path.Combine(cLuis, "x.bin"), tope: 1000)));
        // Más de 4 GiB: lo rechaza el proceso aunque la app lo permita.
        Assert.Equal("demasiado-grande", await Codigo(() => luis.PedirBlobAsync(anf.Clave, b.Huella, 5L << 30, Path.Combine(cLuis, "x.bin"), tope: long.MaxValue)));
        // Destino fuera de la carpeta de sala.
        Assert.Equal("invalid-parameters", await Codigo(() => luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano, Path.Combine(cLuis, "..", "fuera.bin"), tope: 1 << 20)));
        // Huella no ofrecida, o tamaño distinto del ofrecido.
        Assert.Equal("no-ofrecido", await Codigo(() => luis.PedirBlobAsync(anf.Clave, new string('0', 64), 10, Path.Combine(cLuis, "y.bin"), tope: 1 << 20)));
        Assert.Equal("huella", await Codigo(() => luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano - 1, Path.Combine(cLuis, "z.bin"), tope: 1 << 20)));
        // El archivo ofrecido cambió (mismo tamaño): no coincide la huella.
        File.WriteAllBytes(b.Ruta, RandomNumberGenerator.GetBytes(200_000));
        Assert.Equal("huella", await Codigo(() => luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano, Path.Combine(cLuis, "w.bin"), tope: 1 << 20)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(cLuis));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(cLuis)!, "fuera.bin")));
        // Ofrecer algo fuera de la carpeta tampoco vale.
        string fuera = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(fuera, [1, 2, 3]);
        Assert.Equal("invalid-parameters", await Codigo(() => anf.OfrecerBlobsAsync([(fuera, Sala.HuellaDe(fuera), 3)])));
        File.Delete(fuera);
    }

    [Fact]
    public async Task Cada_carga_del_dueno_renueva_su_reserva_aunque_no_entre()
    {
        await using Sala anf = await AnfitrionAsync((_, _, _) => ValueTask.FromResult(Veredicto.No("no")),
            opciones: Opciones("Ana") with { ReservaInactiva = TimeSpan.FromSeconds(4) });
        await using Sala luis = await InvitadoAsync(anf, "Luis");
        Assert.True(await luis.ReservarAsync("pista:a"));
        // 8 s de cargas rechazadas, una cada medio segundo: antes solo renovaba una aceptada y caducaba a los 4 s.
        // Margen de 4 s entre cargas porque el corredor de Windows de la CI llega a tardar más de 1 s en una.
        for (int i = 0; i < 16; i++)
        {
            await luis.EnviarAsync(Json($$"""{"n":{{i}}}"""));
            await Task.Delay(500);
        }
        Assert.Equal(luis.Clave, anf.Reservas["pista:a"]);
    }

    [Fact]
    public async Task Quien_entra_recibe_la_ultima_presencia_de_los_demas()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await InvitadoAsync(anf, "Luis");
        ConcurrentQueue<string> enAnf = new();
        anf.PresenciaRecibida += (de, _) => enAnf.Enqueue(de);
        await luis.PresenciaAsync(Json("""{"cabezal":120}"""));
        await HastaQue(() => enAnf.Contains(luis.Clave), que: "presencia de luis");
        ConcurrentQueue<(string, JsonElement)> enEva = new();
        await using Sala eva = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Eva"), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)),
            suscribir: s => s.PresenciaRecibida += (de, d) => enEva.Enqueue((de, d)));
        await HastaQue(() => enEva.Any(p => p.Item1 == luis.Clave), que: "eva la ve al entrar");
        Assert.Equal(120, enEva.First(p => p.Item1 == luis.Clave).Item2.GetProperty("cabezal").GetInt32());
    }

    private static (string Ruta, string Huella, long Tamano) Grande(string carpeta, long bytes)
    {
        string ruta = Path.Combine(carpeta, Guid.NewGuid().ToString("N") + ".bin");
        using (FileStream f = File.Create(ruta)) f.SetLength(bytes);
        return (ruta, Sala.HuellaDe(ruta), bytes);
    }

    [Fact]
    public async Task Cancelar_una_descarga_borra_el_temporal_y_no_deja_nada()
    {
        string cAna = NuevaCarpeta(), cLuis = NuevaCarpeta();
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana", cAna));
        await using Sala luis = await InvitadoAsync(anf, "Luis", Opciones("Luis", cLuis));
        var b = Grande(cAna, 256L << 20);
        await anf.OfrecerBlobsAsync([b]);
        using CancellationTokenSource corta = new();
        luis.BlobAvance += (_, _) => corta.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano, Path.Combine(cLuis, "x.bin"), tope: 1L << 30, corta.Token));
        // Antes el proceso abortaba la tarea y el .parte se quedaba.
        Assert.Empty(Directory.EnumerateFileSystemEntries(cLuis));
        // Y la misma huella se puede volver a pedir: el intento viejo no completa el nuevo.
        var c = Archivo(cAna, 1000);
        await anf.OfrecerBlobsAsync([c]);
        await luis.PedirBlobAsync(anf.Clave, c.Huella, c.Tamano, Path.Combine(cLuis, "y.bin"), tope: 1 << 20);
    }

    [Fact]
    public async Task Cerrar_la_sala_durante_una_descarga_no_deja_la_espera_colgada()
    {
        string cAna = NuevaCarpeta(), cLuis = NuevaCarpeta();
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana", cAna));
        await using Sala luis = await InvitadoAsync(anf, "Luis", Opciones("Luis", cLuis));
        var b = Grande(cAna, 256L << 20);
        await anf.OfrecerBlobsAsync([b]);
        TaskCompletionSource avanza = new(TaskCreationOptions.RunContinuationsAsynchronously);
        luis.BlobAvance += (_, _) => avanza.TrySetResult();
        Task bajando = luis.PedirBlobAsync(anf.Clave, b.Huella, b.Tamano, Path.Combine(cLuis, "x.bin"), tope: 1L << 30);
        await avanza.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await luis.DisposeAsync();
        SalaException e = await Assert.ThrowsAsync<SalaException>(() => bajando.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("cerrada", e.Codigo);
        await HastaQue(() => !Directory.EnumerateFiles(cLuis, "*.parte").Any(), que: "sin temporales");
    }
}
