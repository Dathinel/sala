using System.Text;
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
    public const int HistorialMax = 4096;
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
    private readonly LinkedList<JsonObject> _historial = new();
    private readonly Channel<(Miembro Autor, Guid Id, JsonElement Datos, string[] Reservas)> _porOrdenar =
        Channel.CreateUnbounded<(Miembro, Guid, JsonElement, string[])>();
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
    public event Action<long, string>? Superada;
    public event Action<string>? Cerrada;

    // ---------- abrir ----------

    /// <param name="validar">La app decide si una carga entra; la sala la llama en orden y de a una.</param>
    public static async Task<Sala> HospedarAsync(string identidad, OpcionesSala opciones, IEnumerable<Miembro> anclados,
        Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>> validar, long epoca = 1, long seq = 0,
        Action<Sala>? suscribir = null, CancellationToken ct = default)
    {
        ProcesoRed red = await ProcesoRed.AbrirAsync(opciones.Ejecutable, ct).ConfigureAwait(false);
        Sala s = new(red, identidad, opciones, validar) { Epoca = epoca, Seq = seq, EsAnfitrion = true };
        suscribir?.Invoke(s);
        try
        {
            List<Miembro> lista = [.. anclados];
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

    private void Arrancar()
    {
        _lectura = Task.Run(LeerAsync);
        _vigia = Task.Run(VigilarAsync);
        if (EsAnfitrion) _orden = Task.Run(OrdenarAsync);
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

    public async Task ExpulsarAsync(string clave, CancellationToken ct = default)
    {
        lock (_cerrojo)
        {
            _miembros.Remove(clave);
            SoltarTodas(clave);
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
        if (EsAnfitrion)
        {
            Miembro yo;
            lock (_cerrojo) yo = _miembros[Clave];
            await _porOrdenar.Writer.WriteAsync((yo, id, datos.Clone(), claves), ct).ConfigureAwait(false);
            return id;
        }
        JsonObject carga = new() { ["type"] = "carga", ["id"] = id.ToString(), ["datos"] = Nodo(datos), ["reservas"] = new JsonArray([.. claves.Select(c => (JsonNode)c)]) };
        lock (_cerrojo) _sinRespuesta.Add(id, carga);
        if (_conectado) await IntentarAsync(MandarAsync((JsonObject)carga.DeepClone(), null, ct)).ConfigureAwait(false);
        return id;
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
        if (Encoding.UTF8.GetByteCount(datos.GetRawText()) > MaxCarga)
            throw new SalaException("demasiado-grande", "La carga pasa de 900 KiB: va como blob.");
    }

    private Task<JsonElement> MandarAsync(JsonObject mensaje, string? a, CancellationToken ct)
    {
        mensaje["epoca"] ??= Epoca;
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
                    lock (_cerrojo) _conectados.Remove(clave);
                    MiembroSale?.Invoke(clave, ev.GetProperty("motivo").GetString()!);
                    _salidas.Writer.TryWrite(clave);
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
        long epoca = m.TryGetProperty("epoca", out JsonElement e) ? e.GetInt64() : 0;
        if (epoca < Epoca) return; // anfitrión viejo
        switch (m.GetProperty("type").GetString())
        {
            case "bienvenida":
                {
                    Epoca = epoca;
                    lock (_cerrojo)
                    {
                        LeerMiembros(m);
                        LeerReservas(m.GetProperty("reservas"));
                    }
                    _conectado = true;
                    _sinAnfitrionDesde = null;
                    _bienvenida.TrySetResult();
                    // Lo que quedó sin respuesta se manda otra vez con su mismo id: el anfitrión no lo aplica dos veces.
                    JsonObject[] otraVez;
                    lock (_cerrojo) otraVez = [.. _sinRespuesta.Values.Select(c => (JsonObject)c.DeepClone())];
                    foreach (JsonObject c in otraVez) await IntentarAsync(MandarAsync(c, null, _fin.Token)).ConfigureAwait(false);
                    if (_reconectando) { _reconectando = false; Reconectada?.Invoke(); }
                    break;
                }
            case "miembros":
                lock (_cerrojo) LeerMiembros(m);
                break;
            case "orden":
                {
                    if (epoca > Epoca) Epoca = epoca;
                    long seq = m.GetProperty("seq").GetInt64();
                    if (seq <= Seq) { Respondida(m); return; }
                    if (seq != Seq + 1)
                    {
                        await IntentarAsync(SincronizarAsync(_fin.Token)).ConfigureAwait(false);
                        return;
                    }
                    Seq = seq;
                    Respondida(m);
                    lock (_cerrojo)
                    {
                        // También el invitado guarda el historial: lo necesita si toma el anfitrión.
                        _historial.AddLast((JsonObject)JsonNode.Parse(m.GetRawText())!);
                        if (_historial.Count > HistorialMax) _historial.RemoveFirst();
                    }
                    Ordenada?.Invoke(new Ordenado(epoca, seq, Guid.Parse(m.GetProperty("id").GetString()!), m.GetProperty("autor").GetString()!, m.GetProperty("datos").Clone()));
                    break;
                }
            case "rechazado":
                {
                    Guid id = Guid.Parse(m.GetProperty("id").GetString()!);
                    bool mia;
                    lock (_cerrojo) mia = _sinRespuesta.Remove(id);
                    if (mia) Rechazada?.Invoke(new Rechazo(id, m.GetProperty("motivo").GetString()!, m.TryGetProperty("detalle", out JsonElement d) ? d.GetString() : null));
                    break;
                }
            case "desfasado":
                Desfasada?.Invoke(m.GetProperty("desde").GetInt64());
                break;
            case "directo":
                if (m.GetProperty("datos") is { ValueKind: JsonValueKind.Object } datos) DirectoRecibido?.Invoke(new Directo(_anfitrion, datos.Clone()));
                break;
            case "reserva":
                AplicarReserva(m.GetProperty("clave").GetString()!, m.TryGetProperty("de", out JsonElement de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null);
                break;
            case "presencia":
                if (m.GetProperty("de").GetString() is { } quien && quien != Clave && m.GetProperty("datos") is { ValueKind: JsonValueKind.Object } pd)
                    PresenciaRecibida?.Invoke(quien, pd.Clone());
                break;
            case "traspaso":
                await TraspasoRecibidoAsync(m).ConfigureAwait(false);
                break;
        }
    }

    private void Respondida(JsonElement orden)
    {
        if (Guid.TryParse(orden.GetProperty("id").GetString(), out Guid id))
            lock (_cerrojo) _sinRespuesta.Remove(id);
    }

    private void LeerMiembros(JsonElement m)
    {
        _miembros.Clear();
        foreach (JsonElement x in m.GetProperty("miembros").EnumerateArray())
        {
            string clave = x.GetProperty("clave").GetString()!;
            _miembros[clave] = new Miembro(clave, x.GetProperty("alias").GetString()!, x.GetProperty("rol").GetString() == "mirar" ? Rol.Mirar : Rol.Editar);
        }
        if (_miembros.TryGetValue(Clave, out Miembro? yo)) Rol = yo.Rol;
        _suplente = m.TryGetProperty("suplente", out JsonElement s) && s.ValueKind == JsonValueKind.Object
            ? (s.GetProperty("clave").GetString()!, Textos(s.GetProperty("direcciones")))
            : null;
    }

    private static JsonNode Nodo(JsonElement e) => JsonSerializer.SerializeToNode(e, SalaJson.Default.JsonElement)!;

    public async ValueTask DisposeAsync()
    {
        if (_fin.IsCancellationRequested) return;
        await _fin.CancelAsync().ConfigureAwait(false);
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
        _fin.Dispose();
    }
}
