using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

public sealed class ConectadosTests
{
    [Fact]
    public async Task Un_miembro_anclado_que_se_fue_sigue_en_la_lista_pero_ya_no_esta_conectado_en_ninguno_de_los_dos_lados()
    {
        await using Sala anf = await AnfitrionAsync();
        await using Sala luis = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Luis"), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)));
        await using Sala eva = await Sala.UnirseAsync(Identidad.Nueva(), Opciones("Eva"), await anf.NuevoBilleteAsync(TimeSpan.FromMinutes(10)));
        await HastaQue(() => eva.Conectados.Count == 3 && luis.Conectados.Count == 3 && anf.Conectados.Count == 3, que: "los tres conectados");
        Assert.Contains(anf.Clave, eva.Conectados);

        await luis.DisposeAsync();

        await HastaQue(() => anf.Conectados.Count == 2 && eva.Conectados.Count == 2, que: "Luis ya no está conectado");
        Assert.DoesNotContain(luis.Clave, anf.Conectados);
        Assert.DoesNotContain(luis.Clave, eva.Conectados);
        Assert.Equal(3, anf.Miembros.Count);
    }
}
