using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>
/// Dos instancias de <c>dathinel-red</c> en este equipo, con identidades distintas, solo por el relé público de n0 (B13, paso 5).
/// Necesita internet: no corre en la CI (<c>Categoria=Internet</c>). Poco tráfico a propósito: la política de n0 es «desarrollo y
/// uso personal», así que son unos cientos de mensajes pequeños y una sola sala por prueba, nunca bucles de carga.
/// </summary>
[Trait("Categoria", "Internet")]
public sealed class InternetTests(ITestOutputHelper salida)
{
    private static OpcionesSala N0(string alias) => Opciones(alias) with { Red = new Red("n0", SoloRele: true) };

    [Fact]
    public async Task Por_el_rele_publico_de_n0_conectan_se_miden_y_vuelven_tras_un_corte()
    {
        List<long> entrada = [];
        for (int vez = 0; vez < 3; vez++)
        {
            await using Sala a = await AnfitrionAsync(opciones: N0("Ana"));
            Diagnostico da = await a.DiagnosticoAsync();
            Assert.True(da.Red.ReleConectado, "El anfitrión no contactó con el relé de n0: ¿hay internet? " + da.ParaTelefono());
            string billete = await a.NuevoBilleteAsync(TimeSpan.FromMinutes(10));
            Stopwatch reloj = Stopwatch.StartNew();
            await using Sala l = await Sala.UnirseAsync(Identidad.Nueva(), N0("Luis"), billete, validar: TodoVale);
            entrada.Add(reloj.ElapsedMilliseconds);
            if (vez < 2) continue;

            // Tercera vuelta: medir la edición y el corte con la sala ya montada.
            Diagnostico dl = await l.DiagnosticoAsync();
            Assert.Equal(Camino.Rele, dl.Pares[0].Camino);
            salida.WriteLine($"Relé de casa del anfitrión: {new Uri(da.Red.ReleUrl!).Host}; el camino del invitado: {new Uri(dl.Pares[0].Rele!).Host}");

            ConcurrentDictionary<Guid, long> vuelta = new();
            Stopwatch t = Stopwatch.StartNew();
            l.Ordenada += o => vuelta[o.Id] = t.ElapsedTicks;
            List<double> ms = [];
            for (int i = 0; i < 40; i++)
            {
                long t0 = t.ElapsedTicks;
                Guid id = await l.EnviarAsync(Json($$"""{"n":{{i}}}"""));
                await HastaQue(() => vuelta.ContainsKey(id), segundos: 15, que: "la vuelta de la carga " + i);
                ms.Add((vuelta[id] - t0) * 1000.0 / Stopwatch.Frequency);
                await Task.Delay(50);
            }
            ms.Sort();
            DiagnosticoPar p = (await l.DiagnosticoAsync()).Pares[0];
            salida.WriteLine($"Edición ida y vuelta por n0 (40 cargas): p50 {ms[20]:0} ms, p95 {ms[(int)(ms.Count * 0.95)]:0} ms, máx {ms[^1]:0} ms; RTT de QUIC {p.Rtt?.TotalMilliseconds} ms; pérdida {p.PerdidaPct}");

            int reconectada = 0;
            l.Reconectada += () => Interlocked.Increment(ref reconectada);
            Stopwatch corte = Stopwatch.StartNew();
            await l.CortarRedParaPruebasAsync();
            await HastaQue(() => reconectada == 1, segundos: 90, que: "vuelve por n0");
            salida.WriteLine($"Reconexión tras cortar el proceso de red: {corte.ElapsedMilliseconds} ms");
        }
        salida.WriteLine("Entrar (de UnirseAsync a bienvenida) en 3 salas: " + string.Join(", ", entrada.Select(e => e + " ms")));
    }
}
