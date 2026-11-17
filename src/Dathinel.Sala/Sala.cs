using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Dathinel.Sala;

/// <summary>
/// Una sala de trabajo en grupo (docs/api.md §5): el anfitrión ordena cargas opacas que valida la app, y cada miembro
/// las recibe en el mismo orden. Esta versión hace miembros y roles, billetes, aprobación, orden central, mensajes
/// directos y reconexión manual; reservas, presencia, traspaso y blobs llegan en el siguiente bloque.
/// </summary>
public sealed class Sala : IAsyncDisposable
{
    public const int MaxCarga = 900 * 1024;
    public const int HistorialMax = 4096;
    internal const double RitmoMedio = 20, RitmoRafaga = 40;
    internal static readonly TimeSpan ExcesoExpulsa = TimeSpan.FromSeconds(3);
    private const int IdsAceptados = 8192, IdsRechazados = 1024;

    private readonly ProcesoRed _red;
    private readonly OpcionesSala _opciones;
    private readonly Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>? _validar;
    private readonly CancellationTokenSource _fin = new();
    private readonly Lock _cerrojo = new();
    private readonly Dictionary<string, Miembro> _miembros = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Miembro> _pendientes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conectados = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cuota> _cuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recuerdo> _vistos = new(StringComparer.Ordinal);
    private readonly LinkedList<JsonObject> _historial = new();
    private readonly Channel<(Miembro Autor, Guid Id, JsonElement Datos)> _porOrdenar = Channel.CreateUnbounded<(Miembro, Guid, JsonElement)>();
    private readonly TaskCompletionSource _bienvenida = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _lectura = Task.CompletedTask;
    private Task _orden = Task.CompletedTask;
    private string _anfitrion = "";

    private Sala(ProcesoRed red, OpcionesSala opciones, bool anfitrion,
        Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>>? validar)
    {
        _red = red;
        _opciones = opciones;
        EsAnfitrion = anfitrion;
        _validar = validar;
    }

    public bool EsAnfitrion { get; }
    public string Clave { get; private set; } = "";
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

    public static async Task<Sala> HospedarAsync(string identidad, OpcionesSala opciones, IEnumerable<Miembro> anclados,
        Func<Miembro, JsonElement, CancellationToken, ValueTask<Veredicto>> validar, long epoca = 1, long seq = 0,
        Action<Sala>? suscribir = null, CancellationToken ct = default)
    {
        ProcesoRed red = await ProcesoRed.AbrirAsync(opciones.Ejecutable, ct).ConfigureAwait(false);
        Sala s = new(red, opciones, true, validar) { Epoca = epoca, Seq = seq };
        suscribir?.Invoke(s);
        try
        {
            List<Miembro> lista = [.. anclados];
            JsonObject p = Base(identidad, opciones);
            p["anclados"] = new JsonArray([.. lista.Select(m => (JsonNode)m.Clave)]);
            JsonElement r = await red.LlamarAsync("abrir-sala", p, ct).ConfigureAwait(false);
            s.Clave = r.GetProperty("clave").GetString()!;
            s.Direcciones = [.. r.GetProperty("direcciones").EnumerateArray().Select(d => d.GetString()!)];
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
    /// </summary>
    public static async Task<Sala> UnirseAsync(string identidad, OpcionesSala opciones, string? billete = null,
        string? anfitrion = null, IEnumerable<string>? direcciones = null, long epoca = 0, long ultimoSeq = 0,
        Action<Sala>? suscribir = null, CancellationToken ct = default)
    {
        ProcesoRed red = await ProcesoRed.AbrirAsync(opciones.Ejecutable, ct).ConfigureAwait(false);
        Sala s = new(red, opciones, false, null) { Epoca = epoca, Seq = ultimoSeq };
        suscribir?.Invoke(s);
        try
        {
            JsonObject p = Base(identidad, opciones);
            p["alias"] = opciones.Alias;
            if (billete is not null) p["billete"] = billete;
            else
            {
                p["anfitrion"] = anfitrion ?? throw new ArgumentException("Hace falta un billete o la clave del anfitrión.");
                p["direcciones"] = new JsonArray([.. (direcciones ?? []).Select(d => (JsonNode)d)]);
            }
            JsonElement r = await red.LlamarAsync("unirse", p, ct).ConfigureAwait(false);
            s._anfitrion = r.GetProperty("anfitrion").GetString()!;
            JsonElement e = await red.LlamarAsync("estado", null, ct).ConfigureAwait(false);
            s.Clave = e.GetProperty("clave").GetString()!;
            s.Arrancar();
            await s.MandarAsync(new JsonObject { ["type"] = "sincronizar", ["epoca"] = s.Epoca, ["ultimoSeq"] = s.Seq }, null, ct).ConfigureAwait(false);
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
        ["red"] = new JsonObject { ["rele"] = o.Red.Rele, ["soloRele"] = o.Red.SoloRele },
    };

    private void Arrancar()
    {
        _lectura = Task.Run(LeerAsync);
        if (EsAnfitrion) _orden = Task.Run(OrdenarAsync);
    }

    // ---------- anfitrión ----------

    public async Task<string> NuevoBilleteAsync(TimeSpan caducaEn, bool unUso = true, CancellationToken ct = default)
    {
        JsonElement r = await _red.LlamarAsync("billete", new JsonObject { ["caducaEn"] = (long)caducaEn.TotalSeconds, ["unUso"] = unUso }, ct).ConfigureAwait(false);
        return r.GetProperty("billete").GetString()!;
    }

    public Task RevocarAsync(CancellationToken ct = default) => _red.LlamarAsync("revocar", new JsonObject(), ct);

    public async Task AceptarAsync(string clave, Rol rol, CancellationToken ct = default)
    {
        Miembro m;
        lock (_cerrojo)
        {
            if (!_pendientes.Remove(clave, out Miembro? p)) throw new SalaException("desconocido", "Esa clave no está esperando.");
            if (_miembros.Count >= _opciones.MaxMiembros && !_miembros.ContainsKey(clave)) throw new SalaException("llena", "La sala está llena.");
            m = p with { Rol = rol };
            _miembros[clave] = m;
        }
        await _red.LlamarAsync("aceptar", new JsonObject { ["clave"] = clave }, ct).ConfigureAwait(false);
    }

    public async Task RechazarAsync(string clave, CancellationToken ct = default)
    {
        lock (_cerrojo) _pendientes.Remove(clave);
        await _red.LlamarAsync("rechazar", new JsonObject { ["clave"] = clave }, ct).ConfigureAwait(false);
    }

    public async Task ExpulsarAsync(string clave, CancellationToken ct = default)
    {
        lock (_cerrojo) _miembros.Remove(clave);
        await _red.LlamarAsync("expulsar", new JsonObject { ["clave"] = clave, ["vetar"] = true }, ct).ConfigureAwait(false);
        await DifundirMiembrosAsync().ConfigureAwait(false);
    }

    // ---------- los dos ----------

    /// <summary>Propone una carga. Vuelve con su id; lo que pase llega por <see cref="Ordenada"/> o <see cref="Rechazada"/>.</summary>
    public async Task<Guid> EnviarAsync(JsonElement datos, CancellationToken ct = default)
    {
        ComprobarCarga(datos);
        Guid id = Guid.NewGuid();
        if (EsAnfitrion)
        {
            Miembro yo;
            lock (_cerrojo) yo = _miembros[Clave];
            await _porOrdenar.Writer.WriteAsync((yo, id, datos), ct).ConfigureAwait(false);
        }
        else
        {
            await MandarAsync(new JsonObject { ["type"] = "carga", ["id"] = id.ToString(), ["datos"] = Nodo(datos) }, null, ct).ConfigureAwait(false);
        }
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
            catch (Exception ex) when (ex is OperationCanceledException or SalaException)
            {
                if (!_fin.IsCancellationRequested) Cerrada?.Invoke("red");
                return;
            }
            foreach (JsonElement ev in r.GetProperty("eventos").EnumerateArray())
            {
                try
                {
                    await AtenderAsync(ev).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SalaException or InvalidOperationException or KeyNotFoundException or FormatException)
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
                    break;
                }
            case "cerrada":
                Cerrada?.Invoke(ev.GetProperty("motivo").GetString()!);
                break;
            case "mensaje":
                {
                    string de = ev.GetProperty("de").GetString()!;
                    JsonElement m = ev.GetProperty("datos");
                    if (EsAnfitrion) await DeInvitadoAsync(de, m).ConfigureAwait(false);
                    else if (de == _anfitrion) DeAnfitrion(m);
                    break;
                }
        }
    }

    private async Task DeInvitadoAsync(string de, JsonElement m)
    {
        Miembro? autor;
        lock (_cerrojo) _miembros.TryGetValue(de, out autor);
        if (autor is null) return;
        switch (m.GetProperty("type").GetString())
        {
            case "sincronizar":
                {
                    long suEpoca = m.GetProperty("epoca").GetInt64();
                    if (suEpoca > Epoca)
                    {
                        Superada?.Invoke(suEpoca, de);
                        return;
                    }
                    await ReponerAsync(de, m.GetProperty("ultimoSeq").GetInt64()).ConfigureAwait(false);
                    break;
                }
            case "carga":
                {
                    Guid id = Guid.Parse(m.GetProperty("id").GetString()!);
                    JsonElement datos = m.GetProperty("datos");
                    if (datos.ValueKind != JsonValueKind.Object) return;
                    if (Encoding.UTF8.GetByteCount(datos.GetRawText()) > MaxCarga)
                    {
                        await ResponderRechazoAsync(de, id, "grande", "La carga pasa de 900 KiB.").ConfigureAwait(false);
                        return;
                    }
                    switch (Admitir(de))
                    {
                        case Paso.Expulsar:
                            await ExpulsarAsync(de).ConfigureAwait(false);
                            return;
                        case Paso.Ritmo:
                            await ResponderRechazoAsync(de, id, "ritmo", "Demasiadas cargas por segundo.").ConfigureAwait(false);
                            return;
                    }
                    await _porOrdenar.Writer.WriteAsync((autor, id, datos)).ConfigureAwait(false);
                    break;
                }
            case "directo":
                {
                    Paso p = Admitir(de);
                    if (p == Paso.Expulsar) await ExpulsarAsync(de).ConfigureAwait(false);
                    else if (p == Paso.Pasa && m.GetProperty("datos") is { ValueKind: JsonValueKind.Object } d) DirectoRecibido?.Invoke(new Directo(de, d));
                    break;
                }
        }
    }

    private void DeAnfitrion(JsonElement m)
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
                        LeerMiembros(m.GetProperty("miembros"));
                        if (_miembros.TryGetValue(Clave, out Miembro? yo)) Rol = yo.Rol;
                    }
                    _bienvenida.TrySetResult();
                    break;
                }
            case "miembros":
                lock (_cerrojo)
                {
                    LeerMiembros(m.GetProperty("miembros"));
                    if (_miembros.TryGetValue(Clave, out Miembro? yo)) Rol = yo.Rol;
                }
                break;
            case "orden":
                {
                    if (epoca > Epoca) Epoca = epoca;
                    long seq = m.GetProperty("seq").GetInt64();
                    if (seq <= Seq) return;
                    if (seq != Seq + 1)
                    {
                        _ = MandarAsync(new JsonObject { ["type"] = "sincronizar", ["epoca"] = Epoca, ["ultimoSeq"] = Seq }, null, default);
                        return;
                    }
                    Seq = seq;
                    Ordenada?.Invoke(new Ordenado(epoca, seq, Guid.Parse(m.GetProperty("id").GetString()!), m.GetProperty("autor").GetString()!, m.GetProperty("datos").Clone()));
                    break;
                }
            case "rechazado":
                Rechazada?.Invoke(new Rechazo(Guid.Parse(m.GetProperty("id").GetString()!), m.GetProperty("motivo").GetString()!,
                    m.TryGetProperty("detalle", out JsonElement d) ? d.GetString() : null));
                break;
            case "desfasado":
                Desfasada?.Invoke(m.GetProperty("desde").GetInt64());
                break;
            case "directo":
                if (m.GetProperty("datos") is { ValueKind: JsonValueKind.Object } datos) DirectoRecibido?.Invoke(new Directo(_anfitrion, datos.Clone()));
                break;
        }
    }

    private void LeerMiembros(JsonElement lista)
    {
        _miembros.Clear();
        foreach (JsonElement x in lista.EnumerateArray())
        {
            string clave = x.GetProperty("clave").GetString()!;
            _miembros[clave] = new Miembro(clave, x.GetProperty("alias").GetString()!, x.GetProperty("rol").GetString() == "mirar" ? Rol.Mirar : Rol.Editar);
        }
    }

    // ---------- orden central (anfitrión) ----------

    private async Task OrdenarAsync()
    {
        await foreach ((Miembro autor, Guid id, JsonElement datos) in _porOrdenar.Reader.ReadAllAsync(_fin.Token).ConfigureAwait(false))
        {
            JsonObject? repetida = null;
            lock (_cerrojo)
            {
                if (_vistos.TryGetValue(autor.Clave, out Recuerdo? r)) repetida = r.Buscar(id);
            }
            if (repetida is not null)
            {
                if (autor.Clave != Clave) await IntentarAsync(MandarAsync(repetida.DeepClone().AsObject(), autor.Clave, _fin.Token)).ConfigureAwait(false);
                continue;
            }

            Veredicto v;
            if (autor.Rol == Rol.Mirar) v = Veredicto.No("rol", "Con el rol «mirar» no se puede editar.");
            else v = await ValidarAsync(autor, datos).ConfigureAwait(false);

            if (!v.Aceptada)
            {
                await ResponderRechazoAsync(autor.Clave, id, v.Motivo ?? "rechazada", v.Detalle).ConfigureAwait(false);
                continue;
            }

            Seq++;
            JsonObject orden = new() { ["type"] = "orden", ["epoca"] = Epoca, ["seq"] = Seq, ["id"] = id.ToString(), ["autor"] = autor.Clave, ["datos"] = Nodo(datos) };
            lock (_cerrojo)
            {
                Recordar(autor.Clave).Aceptada(id, orden);
                _historial.AddLast(orden);
                if (_historial.Count > HistorialMax) _historial.RemoveFirst();
            }
            await IntentarAsync(MandarAsync((JsonObject)orden.DeepClone(), null, _fin.Token)).ConfigureAwait(false);
            Ordenada?.Invoke(new Ordenado(Epoca, Seq, id, autor.Clave, datos.Clone()));
        }
    }

    private async Task<Veredicto> ValidarAsync(Miembro autor, JsonElement datos)
    {
        using CancellationTokenSource tope = CancellationTokenSource.CreateLinkedTokenSource(_fin.Token);
        tope.CancelAfter(_opciones.TopeValidar);
        try
        {
            return await _validar!(autor, datos, tope.Token).AsTask().WaitAsync(tope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_fin.IsCancellationRequested)
        {
            return Veredicto.No("tiempo", "La validación tardó demasiado.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Veredicto.No("invalida", ex.Message);
        }
    }

    private async Task ResponderRechazoAsync(string a, Guid id, string motivo, string? detalle)
    {
        JsonObject r = new() { ["type"] = "rechazado", ["id"] = id.ToString(), ["motivo"] = motivo };
        if (detalle is not null) r["detalle"] = detalle.Length > 4096 ? detalle[..4096] : detalle;
        if (motivo != "ritmo")
        {
            lock (_cerrojo) Recordar(a).Rechazada(id, r);
        }
        if (a == Clave) Rechazada?.Invoke(new Rechazo(id, motivo, detalle));
        else await IntentarAsync(MandarAsync((JsonObject)r.DeepClone(), a, _fin.Token)).ConfigureAwait(false);
    }

    private async Task ReponerAsync(string a, long ultimoSeq)
    {
        List<JsonObject> faltan;
        JsonObject bienvenida;
        bool desfasado;
        lock (_cerrojo)
        {
            long masViejo = _historial.First?.Value["seq"]!.GetValue<long>() ?? Seq + 1;
            desfasado = ultimoSeq < Seq && ultimoSeq + 1 < masViejo;
            faltan = desfasado ? [] : [.. _historial.Where(o => o["seq"]!.GetValue<long>() > ultimoSeq).Select(o => (JsonObject)o.DeepClone())];
            bienvenida = new JsonObject { ["type"] = "bienvenida", ["epoca"] = Epoca, ["seq"] = Seq, ["miembros"] = ListaMiembros(), ["reservas"] = new JsonArray() };
            if (desfasado) faltan.Add(new JsonObject { ["type"] = "desfasado", ["desde"] = masViejo });
        }
        await MandarAsync(bienvenida, a, _fin.Token).ConfigureAwait(false);
        foreach (JsonObject o in faltan) await MandarAsync(o, a, _fin.Token).ConfigureAwait(false);
    }

    private JsonArray ListaMiembros() =>
        [.. _miembros.Values.Select(m => (JsonNode)new JsonObject { ["clave"] = m.Clave, ["alias"] = m.Alias, ["rol"] = m.Rol == Rol.Mirar ? "mirar" : "editar" })];

    private async Task DifundirMiembrosAsync()
    {
        JsonObject m;
        lock (_cerrojo) m = new JsonObject { ["type"] = "miembros", ["miembros"] = ListaMiembros() };
        await IntentarAsync(MandarAsync(m, null, _fin.Token)).ConfigureAwait(false);
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

    // ---------- ritmo y recuerdos ----------

    internal enum Paso { Pasa, Ritmo, Expulsar }

    private Paso Admitir(string de)
    {
        lock (_cerrojo)
        {
            if (!_cuotas.TryGetValue(de, out Cuota? c)) _cuotas[de] = c = new Cuota();
            return c.Tomar(DateTime.UtcNow);
        }
    }

    private Recuerdo Recordar(string clave)
    {
        if (!_vistos.TryGetValue(clave, out Recuerdo? r)) _vistos[clave] = r = new Recuerdo();
        return r;
    }

    /// <summary>Cubo de fichas: 20 por segundo, ráfaga de 40; 60 rechazos en 3 s (seguir al doble del ritmo), expulsión.</summary>
    internal sealed class Cuota
    {
        private double _fichas = RitmoRafaga;
        private DateTime _ultimo = DateTime.MinValue;
        private readonly Queue<DateTime> _rechazos = new();

        public Paso Tomar(DateTime ahora)
        {
            if (_ultimo != DateTime.MinValue) _fichas = Math.Min(RitmoRafaga, _fichas + (ahora - _ultimo).TotalSeconds * RitmoMedio);
            _ultimo = ahora;
            if (_fichas >= 1)
            {
                _fichas -= 1;
                return Paso.Pasa;
            }
            while (_rechazos.Count > 0 && ahora - _rechazos.Peek() > ExcesoExpulsa) _rechazos.Dequeue();
            _rechazos.Enqueue(ahora);
            return _rechazos.Count >= ExcesoExpulsa.TotalSeconds * RitmoMedio ? Paso.Expulsar : Paso.Ritmo;
        }
    }

    /// <summary>Ids ya vistos de un miembro con su respuesta, para contestar igual a una carga repetida.</summary>
    private sealed class Recuerdo
    {
        private readonly Dictionary<Guid, JsonObject> _respuestas = [];
        private readonly Queue<Guid> _aceptadas = new(), _rechazadas = new();

        public JsonObject? Buscar(Guid id) => _respuestas.GetValueOrDefault(id);

        public void Aceptada(Guid id, JsonObject r) => Guardar(id, r, _aceptadas, IdsAceptados);

        public void Rechazada(Guid id, JsonObject r) => Guardar(id, r, _rechazadas, IdsRechazados);

        private void Guardar(Guid id, JsonObject r, Queue<Guid> cola, int max)
        {
            if (!_respuestas.TryAdd(id, r)) return;
            cola.Enqueue(id);
            if (cola.Count > max) _respuestas.Remove(cola.Dequeue());
        }
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
            await Task.WhenAll(_lectura, _orden).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
        _fin.Dispose();
    }
}
