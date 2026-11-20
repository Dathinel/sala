using System.Collections.Concurrent;
using System.Security.Cryptography;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>Datos para las barras de progreso de las apps: fases de la entrada y avance de las descargas (docs/api.md §5.9).</summary>
public sealed class ProgresoTests
{
    [Fact]
    public async Task La_entrada_dice_su_clave_antes_de_la_aprobacion_y_pasa_por_las_fases()
    {
        await using Sala ana = await Sala.HospedarAsync(Identidad.Nueva(), Opciones("Ana"), [], TodoVale);
        TaskCompletionSource<Solicitud> pide = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ana.SolicitudRecibida += s => pide.TrySetResult(s);

        ConcurrentQueue<FaseConexion> fases = new();
        string? claveAlEsperar = null;
        Task<Sala> entra = Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), await ana.NuevoBilleteAsync(TimeSpan.FromMinutes(10)),
            suscribir: s => s.FaseCambia += f => fases.Enqueue(f), validar: TodoVale, alEsperar: c => claveAlEsperar = c);

        Solicitud sol = await pide.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await HastaQue(() => claveAlEsperar is not null && fases.Contains(FaseConexion.EsperandoAprobacion), que: "clave y fase de espera antes de aprobar");
        Assert.Equal(sol.Clave, claveAlEsperar);
        Assert.Equal(4, Huella.Palabras(claveAlEsperar!).Split('-', ' ').Length);
        Assert.False(entra.IsCompleted);

        await ana.AceptarAsync(sol.Clave, Rol.Editar);
        await using Sala luis = await entra.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(FaseConexion.Conectado, luis.Fase);
        Assert.Equal(FaseConexion.Conectado, fases.Last());
        // Las fases salen en orden: nunca se vuelve atrás durante la entrada.
        int[] orden = [.. fases.Select(f => (int)f)];
        Assert.Equal(orden.OrderBy(x => x), orden);
        Diagnostico d = await luis.DiagnosticoAsync();
        Assert.Equal(FaseConexion.Conectado, d.Fase);
        Assert.Equal(1, luis.Intento);
    }

    [Fact]
    public async Task Una_descarga_da_su_progreso_con_total_y_termina_en_el_cien_por_cien()
    {
        string cAna = Carpeta(), cLuis = Carpeta();
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana", cAna));
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis", cLuis), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: TodoVale);
        string ruta = Path.Combine(cAna, "grande.bin");
        File.WriteAllBytes(ruta, RandomNumberGenerator.GetBytes(6 * 1024 * 1024));
        string huella = Sala.HuellaDe(ruta);
        await anf.OfrecerBlobsAsync([(ruta, huella, 6L * 1024 * 1024)]);

        ConcurrentQueue<ProgresoBlob> visto = new();
        luis.BlobProgreso += visto.Enqueue;
        ConcurrentBag<ProgresoBlob> informado = [];
        await luis.PedirBlobAsync(anf.Clave, huella, 6L * 1024 * 1024, Path.Combine(cLuis, "copia.bin"), 64 << 20, new Progress<ProgresoBlob>(p => informado.Add(p)));

        ProgresoBlob[] todos = [.. visto];
        Assert.True(todos.Length >= 2);
        Assert.Equal(0, todos[0].Hecho);
        Assert.Equal(0.0, todos[0].Fraccion);
        Assert.Equal(6L * 1024 * 1024, todos[^1].Total);
        Assert.Equal(todos[^1].Total, todos[^1].Hecho);
        Assert.Equal(1.0, todos[^1].Fraccion);
        Assert.Null(todos[^1].Restante);
        Assert.Equal(anf.Clave, todos[0].De);
        Assert.Equal(todos.Select(p => p.Hecho).OrderBy(x => x), todos.Select(p => p.Hecho));
        Assert.All(todos, p => Assert.True(p.UltimoAvance >= p.Inicio));
        await HastaQue(() => informado.Count >= todos.Length, que: "el IProgress recibe lo mismo");
    }

    [Fact]
    public async Task Cancelar_una_descarga_corta_el_progreso()
    {
        string cAna = Carpeta(), cLuis = Carpeta();
        await using Sala anf = await AnfitrionAsync(opciones: Opciones("Ana", cAna));
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis", cLuis), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)), validar: TodoVale);
        string ruta = Path.Combine(cAna, "grande.bin");
        File.WriteAllBytes(ruta, RandomNumberGenerator.GetBytes(1024 * 1024));
        string huella = Sala.HuellaDe(ruta);
        await anf.OfrecerBlobsAsync([(ruta, huella, 1024L * 1024)]);
        using CancellationTokenSource corta = new();
        luis.BlobProgreso += _ => corta.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => luis.PedirBlobAsync(anf.Clave, huella, 1024L * 1024, Path.Combine(cLuis, "x.bin"), 64 << 20, null, corta.Token));
        Assert.False(File.Exists(Path.Combine(cLuis, "x.bin")));
    }
}
