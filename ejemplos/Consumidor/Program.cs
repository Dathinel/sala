using System.Text.Json;
using Dathinel.Sala;

try
{
    if (args is ["identidad", { } archivo])
    {
        Console.WriteLine(Identidad.CargarOCrear(archivo));
        return 0;
    }
    if (args is not [{ } ejecutable])
    {
        Console.Error.WriteLine("uso: Consumidor <dathinel-red> | Consumidor identidad <archivo>");
        return 1;
    }

    OpcionesSala opciones = new("consumidor", "1.0.0", "ana", Path.GetFullPath(ejecutable),
        Path.Combine(Path.GetTempPath(), "sala-consumidor-" + Guid.NewGuid().ToString("N")[..12]))
    { Red = Red.SinRele };
    await using Sala anfitrion = await Sala.HospedarAsync(Identidad.Nueva(), opciones, [],
        (_, _, _) => ValueTask.FromResult(Veredicto.Si));
    anfitrion.SolicitudRecibida += s => _ = anfitrion.AceptarAsync(s.Clave, Rol.Editar);
    string billete = await anfitrion.NuevoBilleteAsync(TimeSpan.FromMinutes(10));

    TaskCompletionSource<Ordenado> llego = new();
    await using Sala invitado = await Sala.UnirseAsync(Identidad.Nueva(),
        opciones with { Alias = "luis", Carpeta = opciones.Carpeta + "-inv" }, billete,
        suscribir: s => s.Ordenada += o => llego.TrySetResult(o));
    using JsonDocument carga = JsonDocument.Parse("""{"de":"consumidor"}""");
    await invitado.EnviarAsync(carga.RootElement);
    Ordenado ordenado = await llego.Task.WaitAsync(TimeSpan.FromSeconds(60));
    if (ordenado.Seq != 1) throw new SalaException("orden", $"Llegó seq {ordenado.Seq}, no 1.");
    Console.WriteLine("ok seq=1");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
