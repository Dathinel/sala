using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Dathinel.Sala;

/// <summary>
/// Una sala de trabajo en grupo (docs/api.md §5): el anfitrión ordena cargas opacas que valida la app, y cada miembro
/// las recibe en el mismo orden. Partes: este archivo (abrir, mandar, leer), <c>Sala.Orden.cs</c> (anfitrión),
/// <c>Sala.Reservas.cs</c> (reservas y presencia), <c>Sala.Conexion.cs</c> (reconexión, traspaso y suplente) y
/// <c>Sala.Blobs.cs</c>.
/// </summary>
public sealed partial class Sala : IAsyncDisposable
{
    public const int MaxCarga = 900 * 1024;
    public const int HistorialMax = Historial.MaxOrdenes;
    internal const double RitmoMedio = 20, RitmoRafaga = 40;
    internal static readonly TimeSpan ExcesoExpulsa = TimeSpan.FromSeconds(3);

    private ProcesoRed _red;
    private readonly string _identidad;
    private readonly OpcionesSala _opciones;
    private readonly Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>> _validar;
    private readonly CancellationTokenSource _fin = new();
    private readonly Lock _cerrojo = new();
    private readonly Dictionary<string, Miembro> _miembros = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Miembro> _pendientes = new(StringComparer.Ordinal);
    private readonly List<string> _conectados = [];
    private readonly Dictionary<string, string[]> _direccionesDe = new(StringComparer.Ordinal);
    private readonly Historial _historial;
    private readonly Channel<(string Autor, Guid Id, JsonElement Datos, string[] Reservas, long Bytes)> _porOrdenar =
        Channel.CreateUnbounded<(string, Guid, JsonElement, string[], long)>();
    private readonly TaskCompletionSource _bienvenida = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _lectura = Task.CompletedTask;
    private Task _orden = Task.CompletedTask;
    private Task _vigia = Task.CompletedTask;
    private string _anfitrion = "";

    private Sala(ProcesoRed red, string identidad, OpcionesSala opciones,
        Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>? validar)
    {
        _red = red;
        _identidad = identidad;
        _opciones = opciones;
        _validar = validar ?? ((_, _, _) => ValueTask.FromResult(Veredicto.No("sin-validar", "Este equipo no valida cargas.")));
        _historial = new Historial(opciones.Carpeta);
    }

    public bool EsAnfitrion { get; private set; }
    public string Clave { get; private set; } = "";
    public string Anfitrion => _anfitrion;
    public long Epoca { get; private set; }
    public long Seq { get; private set; }
    public Rol Rol { get; private set; } = Rol.Editar;
    public IReadOnlyList<string> Direcciones { get; private set; } = [];

    public IReadOnlyList<Miembro> Miembros
    {
        get { lock (_cerrojo) return [.. _miembros.Values]; }
    }

    public event Action<Solicitud>? SolicitudRecibida;
    public event Action<Miembro>? MiembroEntra;
    public event Action<string, string>? MiembroSale;
    public event Action<Ordenado>? Ordenada;
    public event Action<Rechazo>? Rechazada;
    public event Action<Directo>? DirectoRecibido;
    public event Action<long>? Desfasada;
    /// <summary>Hay otro anfitrión con más autoridad (época mayor o, a igualdad, clave menor): época y su clave (§5.6).</summary>
    public event Action<long, string>? Superada;
    /// <summary>Quien pedía entrar se fue, se le pasó el tiempo o se revocó su billete antes de decidir.</summary>
    public event Action<string>? SolicitudRetirada;
    public event Action<string>? Cerrada;

    // ---------- abrir ----------

    /// <param name="validar">La app decide si una carga entra; la sala la llama en orden y de a una.</param>
    public static async Task<Sala> HospedarAsync(string identidad, OpcionesSala opciones, IEnumerable<Miembro> anclados,
        Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>> validar, long epoca = 1, long seq = 0,
        Action<Sala>? suscribir = null, CancellationToken ct = default)
    {
        Comprobar(opciones);
        List<Miembro> lista = [.. anclados];
        if (lista.Count >= opciones.MaxMiembros)
            throw new ArgumentException($"Más anclados ({lista.Count}) de los que caben con el anfitrión ({opciones.MaxMiembros}).", nameof(anclados));
        if (lista.Any(m => !Limites.EsClave(m.Clave) || !Limites.EsAlias(m.Alias)))
            throw new ArgumentException("Un anclado tiene una clave o un alias inválido.", nameof(anclados));
        ProcesoRed red = await ProcesoRed.AbrirAsync(opciones.Ejecutable, ct).ConfigureAwait(false);
        Sala s = new(red, identidad, opciones, validar) { Epoca = epoca, Seq = seq, EsAnfitrion = true };
        suscribir?.Invoke(s);
        try
        {
            JsonObject p = Base(identidad, opciones);
            p["anclados"] = new JsonArray([.. lista.Select(m => (JsonNode)m.Clave)]);
            JsonElement r = await red.LlamarAsync("abrir-sala", p, ct).ConfigureAwait(false);
            s.Clave = r.GetProperty("clave").GetString()!;
            s.Direcciones = Textos(r.GetProperty("direcciones"));
            s._anfitrion = s.Clave;
            foreach (Miembro m in lista) s._miembros[m.Clave] = m;
            s._miembros[s.Clave] = new Miembro(s.Clave, opciones.Alias, Rol.Editar);
            s.Arrancar();
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Entra con un billete, o vuelve a un anfitrión que ya te ancló (<paramref name="anfitrion"/> y sus direcciones).
    /// <paramref name="suscribir"/> se llama antes de conectar: los <c>orden</c> que reponen llegan justo tras la bienvenida.
    /// <paramref name="validar"/> solo se usa si este equipo llega a anfitrión (traspaso o suplente).
    /// </summary>
    public static async Task<Sala> UnirseAsync(string identidad, OpcionesSala opciones, string? billete = null,
        string? anfitrion = null, IEnumerable<string>? direcciones = null, long epoca = 0, long ultimoSeq = 0,
        Action<Sala>? suscribir = null, Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>? validar = null,
        CancellationToken ct = default)
    {
        Comprobar(opciones);
        ProcesoRed red = await ProcesoRed.AbrirAsync(opciones.Ejecutable, ct).ConfigureAwait(false);
        Sala s = new(red, identidad, opciones, validar) { Epoca = epoca, Seq = ultimoSeq };
        suscribir?.Invoke(s);
        try
        {
            if (billete is null && anfitrion is null) throw new ArgumentException("Hace falta un billete o la clave del anfitrión.");
            await s.EntrarAsync(billete, anfitrion, direcciones, ct).ConfigureAwait(false);
            s.Arrancar();
            await s.SincronizarAsync(ct).ConfigureAwait(false);
            using CancellationTokenSource tope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tope.CancelAfter(TimeSpan.FromSeconds(30));
            await s._bienvenida.Task.WaitAsync(tope.Token).ConfigureAwait(false);
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static JsonObject Base(string identidad, OpcionesSala o) => new()
    {
        ["identidad"] = identidad,
        ["app"] = o.App,
        ["appVersion"] = o.AppVersion,
        ["carpeta"] = Path.GetFullPath(o.Carpeta),
        ["red"] = new JsonObject { ["rele"] = o.Red.Rele, ["soloRele"] = o.Red.SoloRele, ["mdns"] = o.Red.Mdns },
    };

    private static string[] Textos(JsonElement lista) => [.. lista.EnumerateArray().Select(d => d.GetString()!)];

    private static void Comprobar(OpcionesSala o)
    {
        if (o.MaxMiembros is < 1 or > 16) throw new ArgumentException("MaxMiembros va de 1 a 16.", nameof(o));
        if (!Limites.EsAlias(o.Alias)) throw new ArgumentException("El alias tiene de 1 a 40 caracteres, sin caracteres de control.", nameof(o));
        if (o.TopeValidar <= TimeSpan.Zero || o.ReservaInactiva <= TimeSpan.Zero || o.EsperaSuplente < TimeSpan.Zero)
            throw new ArgumentException("Los tiempos de OpcionesSala tienen que ser positivos.", nameof(o));
    }

    private void Arrancar()
    {
        _lectura = Task.Run(LeerAsync);
        _vigia = Task.Run(VigilarAsync);
        // Un solo ordenador en toda la vida de la sala: solo ordena mientras este equipo sea el anfitrión.
        _orden = Task.Run(OrdenarAsync);
    }

    // ---------- anfitrión ----------

    public async Task<string> NuevoBilleteAsync(TimeSpan caducaEn, bool unUso = true, CancellationToken ct = default)
    {
        JsonElement r = await _red.LlamarAsync("billete", new JsonObject { ["caducaEn"] = (long)caducaEn.TotalSeconds, ["unUso"] = unUso }, ct).ConfigureAwait(false);
        return r.GetProperty("billete").GetString()!;
    }

    /// <summary>Sin <paramref name="id"/>, invalida todos los billetes; con él, solo ese.</summary>
    public Task RevocarAsync(string? id = null, CancellationToken ct = default) =>
        _red.LlamarAsync("revocar", id is null ? new JsonObject() : new JsonObject { ["id"] = id }, ct);

    public async Task AceptarAsync(string clave, Rol rol, CancellationToken ct = default)
    {
        lock (_cerrojo)
        {
            if (!_pendientes.TryGetValue(clave, out Miembro? p)) throw new SalaException("desconocido", "Esa clave no está esperando.");
            if (_miembros.Count >= _opciones.MaxMiembros && !_miembros.ContainsKey(clave)) throw new SalaException("llena", "La sala está llena.");
            // Antes de que el proceso lo admita: su «entra» puede llegar antes de que vuelva la llamada.
            _miembros[clave] = p with { Rol = rol };
        }
        try
        {
            await _red.LlamarAsync("aceptar", new JsonObject { ["clave"] = clave }, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_cerrojo) _miembros.Remove(clave);
            throw;
        }
        lock (_cerrojo) _pendientes.Remove(clave);
    }

    public async Task RechazarAsync(string clave, CancellationToken ct = default)
    {
        lock (_cerrojo) _pendientes.Remove(clave);
        await _red.LlamarAsync("rechazar", new JsonObject { ["clave"] = clave }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cambia el rol de un miembro. Pasa por el mismo turno que el orden: la carga que se esté validando se confirma o
    /// no con el rol nuevo. A «mirar» suelta sus reservas; los demás reciben la lista y el suplente que toque.
    /// </summary>
    public async Task CambiarRolAsync(string clave, Rol rol, CancellationToken ct = default)
    {
        if (!EsAnfitrion) throw new SalaException("invalid-parameters", "Solo el anfitrión cambia roles.");
        if (clave == Clave) throw new SalaException("invalid-parameters", "El anfitrión siempre edita.");
        await _mando.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_cerrojo)
            {
                if (!_miembros.TryGetValue(clave, out Miembro? m)) throw new SalaException("desconocido", "Esa clave no es de la sala.");
                if (m.Rol == rol) return;
                _miembros[clave] = m with { Rol = rol };
                if (rol == Rol.Mirar) SoltarTodas(clave);
            }
        }
        finally
        {
            _mando.Release();
        }
        await DifundirMiembrosAsync().ConfigureAwait(false);
        await DifundirReservasDeAsync().ConfigureAwait(false);
    }

    public async Task ExpulsarAsync(string clave, CancellationToken ct = default)
    {
        lock (_cerrojo)
        {
            _miembros.Remove(clave);
            _pendientes.Remove(clave);
            SoltarTodas(clave);
            Olvidar(clave);
        }
        await _red.LlamarAsync("expulsar", new JsonObject { ["clave"] = clave, ["vetar"] = true }, ct).ConfigureAwait(false);
        await DifundirMiembrosAsync().ConfigureAwait(false);
        await DifundirReservasDeAsync().ConfigureAwait(false);
    }

    // ---------- los dos ----------

    /// <summary>
    /// Propone una carga que necesita <paramref name="reservas"/> (claves que el autor tiene que tener). Vuelve con su
    /// id; lo que pase llega por <see cref="Ordenada"/> o <see cref="Rechazada"/>. Sin conexión con el anfitrión, queda
    /// pendiente y se manda al volver.
    /// </summary>
    public async Task<Guid> EnviarAsync(JsonElement datos, IEnumerable<string>? reservas = null, CancellationToken ct = default)
    {
        ComprobarCarga(datos);
        string[] claves = [.. reservas ?? []];
        if (claves.Length > MaxReservas || claves.Any(c => !ClaveDeReservaValida(c))) throw new ArgumentException("Claves de reserva inválidas.", nameof(reservas));
        Guid id = Guid.NewGuid();
        JsonObject carga = new() { ["type"] = "carga", ["id"] = id.ToString(), ["datos"] = Nodo(datos), ["reservas"] = new JsonArray([.. claves.Select(c => (JsonNode)c)]) };
        // También el anfitrión apunta las suyas: si traspasa antes de ordenarlas, las manda al nuevo como invitado.
        lock (_cerrojo) _sinRespuesta.Add(id, carga);
        if (EsAnfitrion)
        {
            lock (_cerrojo) Renovar(Clave);
            if (Encolar(Clave, id, datos.Clone(), claves)) return id;
            lock (_cerrojo) _sinRespuesta.Remove(id);
            throw new SalaException("ocupado", "Hay demasiadas cargas por ordenar; reintenta.");
        }
        if (_conectado) await MandarCargaAsync(id, carga, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Vigía del invitado: las cargas que no salieron por cola llena, otra vez.</summary>
    private async Task ReintentarCargasAsync()
    {
        if (!_conectado) return;
        (Guid, JsonObject)[] otraVez;
        lock (_cerrojo) otraVez = [.. _noSalieron.Where(_sinRespuesta.ContainsKey).Select(id => (id, _sinRespuesta[id]))];
        foreach ((Guid id, JsonObject c) in otraVez) await MandarCargaAsync(id, c, _fin.Token).ConfigureAwait(false);
    }

    /// <summary>Al anfitrión, dirigida: si su cola está llena queda apuntada y el vigía la reintenta.</summary>
    private async Task MandarCargaAsync(Guid id, JsonObject carga, CancellationToken ct)
    {
        try
        {
            await MandarAsync((JsonObject)carga.DeepClone(), _anfitrion, ct).ConfigureAwait(false);
            lock (_cerrojo) _noSalieron.Remove(id);
        }
        catch (SalaException)
        {
            lock (_cerrojo) _noSalieron.Add(id);
        }
    }

    /// <summary>Mensaje fuera del orden (manifiestos, pedir huellas, anunciar blobs). El invitado solo lo manda al anfitrión.</summary>
    public Task EnviarDirectoAsync(JsonElement datos, string? a = null, CancellationToken ct = default)
    {
        ComprobarCarga(datos);
        if (!EsAnfitrion) a = _anfitrion;
        return MandarAsync(new JsonObject { ["type"] = "directo", ["datos"] = Nodo(datos) }, a, ct);
    }

    private static void ComprobarCarga(JsonElement datos)
    {
        if (datos.ValueKind != JsonValueKind.Object) throw new ArgumentException("La carga tiene que ser un objeto JSON.", nameof(datos));
        if (Limites.Profundidad(datos) > Limites.ProfundidadCarga)
            throw new ArgumentException($"La carga pasa de {Limites.ProfundidadCarga} niveles de anidamiento.", nameof(datos));
        if (Limites.Bytes(datos) > MaxCarga)
            throw new SalaException("demasiado-grande", "La carga pasa de 900 KiB: va como blob.");
    }

    private Task<JsonElement> MandarAsync(JsonObject mensaje, string? a, CancellationToken ct)
    {
        // La época del que manda ahora; la de aceptación de un orden va aparte, en «aceptada».
        mensaje["epoca"] = Epoca;
        JsonObject p = new() { ["datos"] = mensaje };
        if (a is not null) p["a"] = new JsonArray(a);
        return _red.LlamarAsync("enviar", p, ct);
    }

    private static async Task IntentarAsync(Task t)
    {
        try
        {
            await t.ConfigureAwait(false);
        }
        catch (SalaException)
        {
            // Un destino que se fue o una cola llena: lo arregla la reconexión (sincronizar).
        }
    }

    // ---------- lo que llega ----------

    private async Task LeerAsync()
    {
        while (!_fin.IsCancellationRequested)
        {
            JsonElement r;
            try
            {
                r = await _red.LlamarAsync("recibir", new JsonObject { ["max"] = 256 }, _fin.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SalaException)
            {
                if (_fin.IsCancellationRequested) return;
                // El proceso murió o se cambió por otro (traspaso): un invitado vuelve a conectar; un anfitrión, no.
                if (_cambiandoProceso) { await Task.Delay(50).ConfigureAwait(false); continue; }
                if (EsAnfitrion || !await ReconectarAsync(procesoNuevo: true).ConfigureAwait(false))
                {
                    Cerrada?.Invoke("red");
                    return;
                }
                continue;
            }
            foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray())
            {
                try
                {
                    await AtenderAsync(ev).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SalaException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
                {
                    // Un mensaje de un par que no tiene la forma esperada se descarta; el proceso ya validó el marco.
                }
            }
        }
    }

    private async Task AtenderAsync(JsonElement ev)
    {
        switch (ev.GetProperty("tipo").GetString())
        {
            case "solicitud":
                {
                    Solicitud s = new(ev.GetProperty("clave").GetString()!, ev.GetProperty("alias").GetString()!);
                    lock (_cerrojo) _pendientes[s.Clave] = new Miembro(s.Clave, s.Alias, Rol.Editar);
                    SolicitudRecibida?.Invoke(s);
                    break;
                }
            case "solicitud-retirada":
                {
                    string clave = ev.GetProperty("clave").GetString()!;
                    bool estaba;
                    lock (_cerrojo) estaba = _pendientes.Remove(clave);
                    if (estaba) SolicitudRetirada?.Invoke(clave);
                    break;
                }
            case "entra":
                {
                    string clave = ev.GetProperty("clave").GetString()!;
                    Miembro? m;
                    lock (_cerrojo)
                    {
                        _conectados.Remove(clave);
                        _conectados.Add(clave);
                        _miembros.TryGetValue(clave, out m);
                    }
                    if (m is not null) MiembroEntra?.Invoke(m);
                    if (EsAnfitrion) await DifundirMiembrosAsync().ConfigureAwait(false);
                    break;
                }
            case "sale":
                {
                    string clave = ev.GetProperty("clave").GetString()!;
                    lock (_cerrojo)
                    {
                        _conectados.Remove(clave);
                        _presencias.Remove(clave);
                    }
                    MiembroSale?.Invoke(clave, ev.GetProperty("motivo").GetString()!);
                    // Un sucesor que se va no aceptó el traspaso.
                    RespuestaDeSucesor(clave, 0, 0, listo: false);
                    // Si era el suplente, los demás tienen que saber quién lo es ahora (§5.6).
                    if (EsAnfitrion) await DifundirMiembrosAsync().ConfigureAwait(false);
                    break;
                }
            case "cerrada":
                await PerdidoAsync(ev.GetProperty("motivo").GetString()!).ConfigureAwait(false);
                break;
            case "blob-avance":
            case "blob-fin":
                AtenderBlob(ev);
                break;
            case "mensaje":
                {
                    string de = ev.GetProperty("de").GetString()!;
                    JsonElement m = ev.GetProperty("datos");
                    if (EsAnfitrion) await DeInvitadoAsync(de, m).ConfigureAwait(false);
                    else if (de == _anfitrion) await DeAnfitrionAsync(m).ConfigureAwait(false);
                    break;
                }
        }
    }

    private async Task DeAnfitrionAsync(JsonElement m)
    {
        // Todo se lee y se comprueba antes de tocar el estado: un mensaje con un campo mal no cambia nada (§5.2).
        if (Limites.Entero(m, "epoca") is not { } epoca) return;
        string? tipo = Limites.Texto(m, "type");
        if (epoca < Epoca)
        {
            // Un anfitrión con menos autoridad que la que ya conocemos (§5.6).
            if (tipo == "bienvenida") Superado(Epoca, _anfitrionDeEpoca);
            return;
        }
        switch (tipo)
        {
            case "bienvenida":
                {
                    if (Limites.Entero(m, "seq") is null || LeerMiembros(m) is not { } lista || LeerReservas(m, lista.Miembros) is not { } reservas) return;
                    if (epoca == Epoca && _anfitrionDeEpoca is { } otro && otro != _anfitrion && string.CompareOrdinal(otro, _anfitrion) < 0)
                    {
                        Superado(Epoca, otro);
                        return;
                    }
                    lock (_cerrojo)
                    {
                        Epoca = epoca;
                        _anfitrionDeEpoca = _anfitrion;
                        AplicarMiembros(lista);
                        _reservas.Clear();
                        foreach ((string c, string d) in reservas) _reservas[c] = new Reserva(d, Ahora);
                    }
                    _conectado = true;
                    _sinAnfitrionDesde = null;
                    _bienvenida.TrySetResult();
                    // Lo que quedó sin respuesta se manda otra vez con su mismo id: el anfitrión no lo aplica dos veces.
                    JsonObject[] otraVez;
                    lock (_cerrojo) otraVez = [.. _sinRespuesta.Values.Select(c => (JsonObject)c.DeepClone())];
                    foreach (JsonObject c in otraVez) await MandarCargaAsync(Guid.Parse(c["id"]!.GetValue<string>()), c, _fin.Token).ConfigureAwait(false);
                    if (_reconectando) { _reconectando = false; Reconectada?.Invoke(); }
                    break;
                }
            case "miembros":
                if (LeerMiembros(m) is { } l) lock (_cerrojo) AplicarMiembros(l);
                break;
            case "orden":
                {
                    if (Limites.Entero(m, "seq") is not { } seq || Limites.Id(m) is not { } id || Limites.Texto(m, "autor") is not { } autor
                        || !Limites.EsClave(autor) || !m.TryGetProperty("datos", out JsonElement datos) || !Limites.Objeto(datos, MaxCarga)) return;
                    bool hueco;
                    lock (_cerrojo)
                    {
                        if (seq <= Seq)
                        {
                            _sinRespuesta.Remove(id);
                            return;
                        }
                        hueco = seq != Seq + 1;
                        if (!hueco)
                        {
                            if (epoca > Epoca) Epoca = epoca;
                            Seq = seq;
                            _sinRespuesta.Remove(id);
                            // También el invitado guarda el historial: lo necesita si toma el anfitrión.
                            _historial.Agregar(seq, (JsonObject)JsonNode.Parse(m.GetRawText())!);
                        }
                    }
                    // Desfasado, el hueco no se pide: hasta que la app confirme su instantánea (§5.6).
                    if (hueco && !_desfasado) await IntentarAsync(SincronizarAsync(_fin.Token)).ConfigureAwait(false);
                    else if (!hueco) Ordenada?.Invoke(new Ordenado(Limites.Entero(m, "aceptada") ?? epoca, seq, id, autor, datos.Clone()));
                    break;
                }
            case "ya-ordenada":
                if (Limites.Id(m) is { } ya) lock (_cerrojo) _sinRespuesta.Remove(ya);
                break;
            case "rechazado":
                {
                    if (Limites.Id(m) is not { } id || !Limites.EsMotivo(Limites.Texto(m, "motivo"))) return;
                    string? detalle = Limites.Texto(m, "detalle");
                    if (detalle is { Length: > Limites.MaxDetalle }) return;
                    bool mia;
                    lock (_cerrojo) mia = _sinRespuesta.Remove(id);
                    if (mia) Rechazada?.Invoke(new Rechazo(id, Limites.Texto(m, "motivo")!, detalle));
                    break;
                }
            case "desfasado":
                if (Limites.Entero(m, "desde") is { } desde)
                {
                    _desfasado = true;
                    Desfasada?.Invoke(desde);
                }
                break;
            case "seq":
                // El anfitrión avisa de que nos saltamos órdenes con la cola llena.
                if (Limites.Entero(m, "seq") is { } ultimo && ultimo > Seq && !_desfasado) await IntentarAsync(SincronizarAsync(_fin.Token)).ConfigureAwait(false);
                break;
            case "directo":
                if (m.TryGetProperty("datos", out JsonElement dd) && Limites.Objeto(dd, MaxCarga)) DirectoRecibido?.Invoke(new Directo(_anfitrion, dd.Clone()));
                break;
            case "reserva":
                {
                    string? clave = Limites.Texto(m, "clave"), de = Limites.Texto(m, "de");
                    if (clave is null || !ClaveDeReservaValida(clave) || (de is not null && !Limites.EsClave(de))) return;
                    AplicarReserva(clave, de);
                    break;
                }
            case "presencia":
                if (Limites.Texto(m, "de") is { } quien && Limites.EsClave(quien) && quien != Clave
                    && m.TryGetProperty("datos", out JsonElement pd) && Limites.Objeto(pd, MaxPresencia))
                    PresenciaRecibida?.Invoke(quien, pd.Clone());
                break;
            case "traspaso" or "traspaso-oferta" or "traspaso-cancelado":
                await TraspasoRecibidoAsync(tipo, m).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Hay otro anfitrión con más autoridad: si aún entrábamos, la entrada falla.</summary>
    private void Superado(long epoca, string? anfitrion)
    {
        _bienvenida.TrySetException(new SalaException("superada", $"Esta sala quedó atrás: manda la época {epoca}."));
        Superada?.Invoke(epoca, anfitrion ?? "");
    }

    private sealed record ListaMiembros(Dictionary<string, Miembro> Miembros, (string Clave, string[] Direcciones)? Suplente);

    /// <summary>Lee <c>miembros</c> y <c>suplente</c> sin tocar nada; null si algo no vale.</summary>
    private static ListaMiembros? LeerMiembros(JsonElement m)
    {
        if (!m.TryGetProperty("miembros", out JsonElement lista) || lista.ValueKind != JsonValueKind.Array || lista.GetArrayLength() > 16) return null;
        Dictionary<string, Miembro> r = new(StringComparer.Ordinal);
        foreach (JsonElement x in lista.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) return null;
            string? clave = Limites.Texto(x, "clave"), alias = Limites.Texto(x, "alias"), rol = Limites.Texto(x, "rol");
            if (!Limites.EsClave(clave) || !Limites.EsAlias(alias) || rol is not ("mirar" or "editar") || r.ContainsKey(clave!)) return null;
            r[clave!] = new Miembro(clave!, alias!, rol == "mirar" ? Rol.Mirar : Rol.Editar);
        }
        (string, string[])? suplente = null;
        if (m.TryGetProperty("suplente", out JsonElement s) && s.ValueKind != JsonValueKind.Null)
        {
            if (s.ValueKind != JsonValueKind.Object || Limites.Texto(s, "clave") is not { } sc || !r.ContainsKey(sc)
                || Limites.Textos(s, "direcciones", Limites.MaxDirecciones) is not { } sd) return null;
            suplente = (sc, sd);
        }
        return new ListaMiembros(r, suplente);
    }

    /// <summary>Bajo el candado.</summary>
    private void AplicarMiembros(ListaMiembros l)
    {
        _miembros.Clear();
        foreach (KeyValuePair<string, Miembro> x in l.Miembros) _miembros[x.Key] = x.Value;
        if (_miembros.TryGetValue(Clave, out Miembro? yo)) Rol = yo.Rol;
        _suplente = l.Suplente;
    }

    private static JsonNode Nodo(JsonElement e) => JsonSerializer.SerializeToNode(e, SalaJson.Default.JsonElement)!;

    public async ValueTask DisposeAsync()
    {
        if (_fin.IsCancellationRequested) return;
        await _fin.CancelAsync().ConfigureAwait(false);
        TerminarDescargas();
        _porOrdenar.Writer.TryComplete();
        try
        {
            await _red.LlamarAsync("cerrar").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SalaException or TimeoutException)
        {
        }
        await _red.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_lectura, _orden, _vigia).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
        lock (_cerrojo) _historial.Dispose();
        _fin.Dispose();
    }
}
