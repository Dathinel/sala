using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala;

// Conexión del invitado, reconexión automática, traspaso de anfitrión, suplente e instantánea (docs/api.md §5.6).
//
// Las transiciones de autoridad (ordenar una carga, traspasar, ascender, reconectar, cambiar un rol) pasan todas por
// _mando, de una en una: así nunca hay dos ordenadores, ni se fotografía el historial con una validación a medias.
public sealed partial class Sala
{
    private static readonly TimeSpan EsperaTraspaso = TimeSpan.FromSeconds(60);
    private const long MaxHistorialBytes = Historial.MaxBytes + (16L << 20);

    private readonly SemaphoreSlim _mando = new(1, 1);
    private readonly OrderedDictionary<Guid, JsonObject> _sinRespuesta = [];
    /// <summary>Cargas propias que no llegaron a salir (cola llena): el vigía las reintenta.</summary>
    private readonly HashSet<Guid> _noSalieron = [];
    private string[] _anfitrionDirecciones = [];
    private (string Clave, string[] Direcciones)? _suplente;
    private (string Clave, string[] Direcciones)? _destinoTraspaso;
    /// <summary>Anfitrión de la época que conoce este equipo, para desempatar entre dos (§5.6).</summary>
    private string? _anfitrionDeEpoca;
    private long? _sinAnfitrionDesde;
    private volatile bool _conectado;
    private volatile bool _reconectando;
    private volatile bool _cambiandoProceso;
    private volatile bool _desfasado;
    private Task<bool>? _reconexion;
    private int _reconexiones;
    private long? _conexionMs;
    private DateTimeOffset? _ultimaReconexion;
    private Caida? _ultimaCaida;
    /// <summary>Sucesor: el traspaso que preparó (época nueva y seq), a la espera de que el anfitrión lo confirme.</summary>
    private (long Nueva, long Seq)? _preparado;
    /// <summary>Anfitrión que traspasa: la respuesta del sucesor (true listo, false fallo o se fue).</summary>
    private (string A, long Nueva, long Seq, TaskCompletionSource<bool> Respuesta)? _traspaso;

    /// <summary>Se perdió al anfitrión; la sala reintenta sola (1, 2, 4… hasta 30 s) y las cargas quedan pendientes.</summary>
    public event Action? Desconectada;
    public event Action? Reconectada;

    /// <summary>El suplente actual (clave), si el anfitrión nombró uno.</summary>
    public string? Suplente => _suplente?.Clave;

    /// <summary>Cargas mandadas que aún no tienen respuesta.</summary>
    public int Pendientes
    {
        get { lock (_cerrojo) return _sinRespuesta.Count; }
    }

    private async Task EntrarAsync(string? billete, string? anfitrion, IEnumerable<string>? direcciones, CancellationToken ct, Action<string>? alEsperar = null)
    {
        JsonObject p = Base(_identidad, _opciones);
        p["alias"] = _opciones.Alias;
        if (billete is not null) p["billete"] = billete;
        else
        {
            p["anfitrion"] = anfitrion;
            p["direcciones"] = new JsonArray([.. (direcciones ?? []).Select(d => (JsonNode)d)]);
        }
        Task<JsonElement> unirse = _red.LlamarAsync("unirse", p, ct);
        _ = VigilarEntradaAsync(unirse, alEsperar, ct);
        JsonElement r = await unirse.ConfigureAwait(false);
        _anfitrion = r.GetProperty("anfitrion").GetString()!;
        if (r.TryGetProperty("conexionMs", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out long n)) _conexionMs = n;
        _anfitrionDirecciones = Cadenas(r.GetProperty("anfitrionDirecciones"));
        Direcciones = Cadenas(r.GetProperty("direcciones"));
        JsonElement e = await _red.LlamarAsync("estado", null, ct).ConfigureAwait(false);
        Clave = e.GetProperty("clave").GetString()!;
    }

    /// <summary>
    /// Mientras <c>unirse</c> espera (conexión, aprobación), lee <c>estado</c> cada 100 ms: la fase para
    /// <see cref="FaseCambia"/> y, la primera vez, la clave propia para <paramref name="alEsperar"/>.
    /// </summary>
    private async Task VigilarEntradaAsync(Task<JsonElement> unirse, Action<string>? alEsperar, CancellationToken ct)
    {
        bool clavePuesta = alEsperar is null;
        while (!unirse.IsCompleted && !ct.IsCancellationRequested)
        {
            try
            {
                JsonElement e = await _red.LlamarAsync("estado", null, ct).ConfigureAwait(false);
                if (!clavePuesta && e.TryGetProperty("clave", out JsonElement c) && c.GetString() is { Length: > 0 } clave)
                {
                    Clave = clave;
                    clavePuesta = true;
                    try { alEsperar!(clave); }
                    catch (Exception) { /* un fallo del aviso de la app no debe tumbar la entrada */ }
                }
                if (e.TryGetProperty("conexion", out JsonElement con) && con.TryGetProperty("fase", out JsonElement f))
                {
                    if (con.TryGetProperty("intento", out JsonElement it) && it.TryGetInt32(out int n)) Intento = n;
                    if (f.GetString() switch
                    {
                        "buscando" => FaseConexion.Buscando,
                        "conectando" => FaseConexion.Conectando,
                        "esperando-aprobacion" => FaseConexion.EsperandoAprobacion,
                        "sincronizando" => FaseConexion.Sincronizando,
                        _ => (FaseConexion?)null,
                    } is { } fase) PonerFase(fase);
                }
            }
            catch (SalaException)
            {
                // Aún no hay sala en el proceso (no-abierta) o terminó: se reintenta mientras dure la llamada larga.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!unirse.IsCompleted) await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Fase de la conexión con el anfitrión; <see cref="FaseCambia"/> avisa de cada cambio.</summary>
    public FaseConexion Fase { get; private set; } = FaseConexion.Buscando;

    /// <summary>Intento de conexión en curso (1 a 3) mientras <see cref="Fase"/> es <see cref="FaseConexion.Conectando"/>.</summary>
    public int Intento { get; private set; }

    public event Action<FaseConexion>? FaseCambia;

    private void PonerFase(FaseConexion fase)
    {
        if (Fase == fase) return;
        Fase = fase;
        FaseCambia?.Invoke(fase);
    }

    /// <summary>Cómo va la conexión (docs/api.md §2.3 y §5.9). Una llamada local barata: se puede pedir cada segundo.</summary>
    public async Task<Diagnostico> DiagnosticoAsync(CancellationToken ct = default)
    {
        JsonElement e = await _red.LlamarAsync("estado", null, ct).ConfigureAwait(false);
        return Diagnostico.Leer(e, _opciones.AppVersion, _opciones.Red.Rele, EsAnfitrion, Volatile.Read(ref _reconexiones), _conexionMs, _ultimaReconexion, _ultimaCaida, Fase, Intento);
    }

    private Task<JsonElement> SincronizarAsync(CancellationToken ct)
    {
        JsonObject m = new()
        {
            ["type"] = "sincronizar",
            ["ultimoSeq"] = Seq,
            ["direcciones"] = new JsonArray([.. Direcciones.Select(d => (JsonNode)d)]),
        };
        if (_anfitrionDeEpoca is not null) m["anfitrion"] = _anfitrionDeEpoca;
        return MandarAsync(m, _anfitrion, ct);
    }

    /// <summary>
    /// Tras <see cref="Desfasada"/>, la app pide y aplica la instantánea (directo y blobs) y confirma aquí la época y el
    /// seq que representa. La sala sigue desde ahí sin recrearse: pide lo que vino después.
    /// </summary>
    public async Task ConfirmarInstantaneaAsync(long epoca, long seq, CancellationToken ct = default)
    {
        if (EsAnfitrion) throw new SalaException("invalid-parameters", "El anfitrión no confirma instantáneas.");
        ArgumentOutOfRangeException.ThrowIfNegative(seq);
        lock (_cerrojo)
        {
            if (epoca < Epoca) throw new SalaException("invalid-parameters", $"La instantánea es de la época {epoca} y la sala ya va por la {Epoca}.");
            Epoca = epoca;
            Seq = seq;
            _historial.Vaciar();
            _desfasado = false;
        }
        if (_conectado) await SincronizarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>El proceso avisó que la conexión con el anfitrión terminó.</summary>
    private async Task PerdidoAsync(string motivo)
    {
        if (EsAnfitrion) return;
        if (motivo is "expulsado" or "revocado")
        {
            _conectado = false;
            Cerrada?.Invoke(motivo);
            return;
        }
        if (!await ReconectarAsync(procesoNuevo: false).ConfigureAwait(false)) Cerrada?.Invoke(motivo);
    }

    /// <summary>Un solo bucle de reconexión a la vez: quien llega mientras otro reintenta espera ese mismo.</summary>
    private Task<bool> ReconectarAsync(bool procesoNuevo)
    {
        lock (_cerrojo)
        {
            if (_reconexion is { IsCompleted: false } r) return r;
            return _reconexion = Task.Run(() => BucleReconexionAsync(procesoNuevo));
        }
    }

    /// <summary>Reintenta hasta volver (true) o hasta un no definitivo o el cierre (false).</summary>
    private async Task<bool> BucleReconexionAsync(bool procesoNuevo)
    {
        _conectado = false;
        _sinAnfitrionDesde ??= Ahora;
        if (!_reconectando)
        {
            _reconectando = true;
            PonerFase(FaseConexion.Desconectado);
            Desconectada?.Invoke();
        }
        TimeSpan espera = TimeSpan.FromSeconds(1);
        while (!_fin.IsCancellationRequested)
        {
            if (EsAnfitrion) return true; // tomó la sala como suplente mientras tanto
            List<(string Clave, string[] Direcciones)> candidatos = [];
            if (_destinoTraspaso is { } t) candidatos.Add(t);
            else
            {
                // Solo quien también perdió al anfitrión busca al suplente, y solo tras la espera (§5.6). Va primero:
                // intentar con un anfitrión apagado tarda lo que tarda en vencer la conexión.
                if (_suplente is { } s && s.Clave != Clave && Ahora - _sinAnfitrionDesde >= (long)_opciones.EsperaSuplente.TotalMilliseconds)
                    candidatos.Add(s);
                candidatos.Add((_anfitrion, _anfitrionDirecciones));
            }
            foreach ((string clave, string[] dirs) in candidatos)
            {
                try
                {
                    await _mando.WaitAsync(_fin.Token).ConfigureAwait(false);
                    try
                    {
                        if (EsAnfitrion) return true;
                        if (procesoNuevo)
                        {
                            await CambiarProcesoAsync().ConfigureAwait(false);
                            procesoNuevo = false;
                        }
                        await EntrarAsync(null, clave, dirs, _fin.Token).ConfigureAwait(false);
                        _destinoTraspaso = null;
                        await SincronizarAsync(_fin.Token).ConfigureAwait(false);
                        return true;
                    }
                    finally
                    {
                        _mando.Release();
                    }
                }
                catch (SalaException ex) when (ex.Codigo is "rechazado" or "version")
                {
                    return false;
                }
                catch (SalaException ex) when (ex.Codigo == "sin-red")
                {
                    procesoNuevo = true;
                }
                catch (SalaException)
                {
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            try
            {
                await Task.Delay(espera, _fin.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            espera = TimeSpan.FromSeconds(Math.Min(30, espera.TotalSeconds * 2));
        }
        return false;
    }

    /// <summary>Cambia el proceso de red por uno nuevo; LeerAsync espera mientras.</summary>
    private async Task CambiarProcesoAsync()
    {
        _cambiandoProceso = true;
        try
        {
            ProcesoRed viejo = _red;
            _red = await ProcesoRed.AbrirAsync(_opciones.Ejecutable, _fin.Token).ConfigureAwait(false);
            await viejo.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _cambiandoProceso = false;
        }
    }

    // ---------- traspaso ----------

    /// <summary>
    /// Pasa la sala a <paramref name="clave"/> (conectado y con rol editar). El sucesor baja el historial, lo
    /// comprueba y confirma con la época y el seq preparados; hasta entonces este equipo sigue al mando. Si el sucesor
    /// falla, se va o no confirma a tiempo, sale <see cref="SalaException"/> «traspaso» y nada cambia.
    /// </summary>
    public async Task TraspasarAsync(string clave, CancellationToken ct = default)
    {
        if (!EsAnfitrion) throw new SalaException("invalid-parameters", "Solo el anfitrión traspasa la sala.");
        await _mando.WaitAsync(ct).ConfigureAwait(false);
        string? ruta = null;
        string? huella = null;
        string[] dirs;
        long nueva, seq;
        bool hecho = false;
        try
        {
            if (!EsAnfitrion) throw new SalaException("invalid-parameters", "Solo el anfitrión traspasa la sala.");
            JsonArray historial;
            lock (_cerrojo)
            {
                if (!_conectados.Contains(clave) || !_miembros.TryGetValue(clave, out Miembro? m) || m.Rol != Rol.Editar || !_direccionesDe.TryGetValue(clave, out string[]? d))
                    throw new SalaException("desconocido", "Ese miembro no está conectado o no puede editar.");
                dirs = d;
                // Con _mando tomado no hay ninguna validación a medias: lo fotografiado es todo lo confirmado.
                historial = [.. _historial.Todo.Select(o => o.DeepClone())];
                seq = Seq;
                nueva = Epoca + 1;
            }
            string carpeta = Path.Combine(Path.GetFullPath(_opciones.Carpeta), ".sala");
            Directory.CreateDirectory(carpeta);
            ruta = Path.Combine(carpeta, $"historial-{Guid.NewGuid():N}.json");
            await File.WriteAllBytesAsync(ruta, JsonSerializer.SerializeToUtf8Bytes(historial, SalaJson.Default.JsonArray), ct).ConfigureAwait(false);
            huella = HuellaDe(ruta);
            long tamano = new FileInfo(ruta).Length;
            await OfrecerBlobsAsync([(ruta, huella, tamano)], ct).ConfigureAwait(false);

            TaskCompletionSource<bool> respuesta = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_cerrojo) _traspaso = (clave, nueva, seq, respuesta);
            await MandarAsync(new JsonObject
            {
                ["type"] = "traspaso-oferta",
                ["nueva"] = nueva,
                ["seq"] = seq,
                ["historial"] = new JsonObject { ["huella"] = huella, ["tamano"] = tamano },
            }, clave, ct).ConfigureAwait(false);

            bool listo;
            try
            {
                listo = await respuesta.Task.WaitAsync(EsperaTraspaso, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                listo = false;
            }
            if (!listo)
            {
                await IntentarAsync(MandarAsync(new JsonObject { ["type"] = "traspaso-cancelado", ["nueva"] = nueva }, clave, CancellationToken.None)).ConfigureAwait(false);
                throw new SalaException("traspaso", "El sucesor no confirmó el traspaso: la sala sigue aquí.");
            }

            // Confirmado: desde aquí manda el sucesor. Los demás se reconectan a él con la época nueva.
            lock (_cerrojo)
            {
                EsAnfitrion = false;
                _anfitrion = clave;
                _anfitrionDirecciones = dirs;
                _destinoTraspaso = (clave, dirs);
                _anfitrionDeEpoca = null;
            }
            await IntentarAsync(MandarAsync(new JsonObject
            {
                ["type"] = "traspaso",
                ["a"] = clave,
                ["nueva"] = nueva,
                ["seq"] = seq,
                ["direcciones"] = new JsonArray([.. dirs.Select(x => (JsonNode)x)]),
            }, null, CancellationToken.None)).ConfigureAwait(false);
            hecho = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !hecho)
        {
            throw new SalaException("traspaso", "Traspaso cancelado: la sala sigue aquí.");
        }
        catch (Exception ex) when (!hecho && ruta is not null && ex is SalaException { Codigo: not "traspaso" } or IOException or UnauthorizedAccessException)
        {
            // Cualquier fallo antes de la confirmación (el sucesor se fue, disco, red): nada cambia.
            throw new SalaException("traspaso", "El traspaso no se completó: la sala sigue aquí. " + ex.Message, ex);
        }
        finally
        {
            lock (_cerrojo) _traspaso = null;
            if (huella is not null) await IntentarAsync(RetirarBlobsAsync([huella], CancellationToken.None)).ConfigureAwait(false);
            if (ruta is not null) BorrarSinFallar(ruta);
            _mando.Release();
        }

        // El sucesor corta con nosotros al ascender; se le da un momento para que el «traspaso» llegue a todos.
        await HastaQueSalgaAsync(clave, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _cambiandoProceso = true;
        try
        {
            ProcesoRed viejo = _red;
            _red = await ProcesoRed.AbrirAsync(_opciones.Ejecutable, ct).ConfigureAwait(false);
            await viejo.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _cambiandoProceso = false;
        }
        // La reconexión sigue sola (Reconectada avisa): el traspaso ya está hecho.
        _ = ReconectarAsync(procesoNuevo: false);
    }

    private async Task HastaQueSalgaAsync(string clave, TimeSpan tope)
    {
        long limite = Ahora + (long)tope.TotalMilliseconds;
        while (Ahora < limite)
        {
            lock (_cerrojo)
            {
                if (!_conectados.Contains(clave)) return;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static void BorrarSinFallar(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Anfitrión que traspasa: respuesta del sucesor, o su salida (que nunca vale como aceptación).</summary>
    private void RespuestaDeSucesor(string de, long nueva, long seq, bool listo)
    {
        TaskCompletionSource<bool>? r = null;
        lock (_cerrojo)
        {
            if (_traspaso is { } t && t.A == de && (!listo || (t.Nueva == nueva && t.Seq == seq))) r = t.Respuesta;
        }
        r?.TrySetResult(listo);
    }

    /// <summary>Invitado: lo que llega del anfitrión sobre un traspaso.</summary>
    private async Task TraspasoRecibidoAsync(string tipo, JsonElement m)
    {
        if (Limites.Entero(m, "nueva") is not { } nueva || nueva <= Epoca) return;
        switch (tipo)
        {
            case "traspaso-oferta":
                {
                    if (Limites.Entero(m, "seq") is not { } seq || !m.TryGetProperty("historial", out JsonElement h)
                        || Limites.Texto(h, "huella") is not { } huella || !Limites.EsClave(huella) || Limites.Entero(h, "tamano") is not { } tamano) return;
                    string de = _anfitrion;
                    // Fuera del bucle de lectura: la descarga avisa por eventos que ese mismo bucle tiene que leer.
                    _ = Task.Run(async () =>
                    {
                        JsonObject r;
                        try
                        {
                            await PrepararTraspasoAsync(de, huella, tamano, nueva, seq).ConfigureAwait(false);
                            r = new JsonObject { ["type"] = "traspaso-listo", ["nueva"] = nueva, ["seq"] = seq };
                        }
                        catch (Exception ex) when (ex is SalaException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
                        {
                            r = new JsonObject { ["type"] = "traspaso-fallo", ["nueva"] = nueva, ["motivo"] = ex is SalaException s ? s.Codigo : "disco" };
                        }
                        await IntentarAsync(MandarAsync(r, de, _fin.Token)).ConfigureAwait(false);
                    });
                    break;
                }
            case "traspaso-cancelado":
                lock (_cerrojo)
                {
                    if (_preparado?.Nueva == nueva) _preparado = null;
                }
                break;
            case "traspaso":
                {
                    if (Limites.Texto(m, "a") is not { } a || !Limites.EsClave(a) || Limites.Entero(m, "seq") is not { } seq
                        || Limites.Textos(m, "direcciones", Limites.MaxDirecciones) is not { } dirs) return;
                    if (a == Clave)
                    {
                        bool preparado;
                        lock (_cerrojo) preparado = _preparado == (nueva, seq);
                        if (preparado) _ = Task.Run(() => AscenderAsync(nueva));
                        return;
                    }
                    _destinoTraspaso = (a, dirs);
                    if (!await ReconectarAsync(procesoNuevo: false).ConfigureAwait(false)) Cerrada?.Invoke("traspaso");
                    break;
                }
        }
    }

    /// <summary>
    /// Sucesor: baja el historial del anfitrión, lo junta con el propio y exige continuidad hasta el seq anunciado. Lo
    /// que ya tenía aplicado solo se guarda (para reponer y deduplicar); lo nuevo se aplica en orden.
    /// </summary>
    private async Task PrepararTraspasoAsync(string de, string huella, long tamano, long nueva, long seq)
    {
        string carpeta = Path.Combine(Path.GetFullPath(_opciones.Carpeta), ".sala");
        Directory.CreateDirectory(carpeta);
        string ruta = Path.Combine(carpeta, $"historial-{Guid.NewGuid():N}.json");
        JsonArray recibido;
        try
        {
            await PedirBlobAsync(de, huella, tamano, ruta, MaxHistorialBytes, _fin.Token).ConfigureAwait(false);
            recibido = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(ruta, _fin.Token).ConfigureAwait(false), SalaJson.Default.JsonArray)
                ?? throw new SalaException("historial", "El historial está vacío.");
        }
        finally
        {
            BorrarSinFallar(ruta);
        }

        SortedDictionary<long, JsonObject> todos = [];
        foreach (JsonNode? n in recibido)
        {
            if (n is not JsonObject o) throw new SalaException("historial", "El historial trae algo que no es un orden.");
            JsonElement e = JsonSerializer.SerializeToElement(o, SalaJson.Default.JsonObject);
            if (Limites.Entero(e, "seq") is not { } s || s > seq || Limites.Id(e) is null || !Limites.EsClave(Limites.Texto(e, "autor"))
                || !e.TryGetProperty("datos", out JsonElement d) || !Limites.Objeto(d, MaxCarga))
                throw new SalaException("historial", "El historial trae un orden mal formado.");
            todos[s] = o;
        }
        List<Ordenado> nuevos = [];
        lock (_cerrojo)
        {
            foreach (JsonObject o in _historial.Todo) todos.TryAdd(o["seq"]!.GetValue<long>(), o);
            // Continuidad: desde lo que ya tengo hasta el seq anunciado, sin huecos.
            for (long s = Seq + 1; s <= seq; s++)
                if (!todos.ContainsKey(s)) throw new SalaException("historial", $"Al historial le falta el orden {s}.");
            _historial.Vaciar();
            foreach (KeyValuePair<long, JsonObject> x in todos) _historial.Agregar(x.Key, x.Value);
            for (long s = Seq + 1; s <= seq; s++)
            {
                JsonObject o = todos[s];
                Guid id = Guid.Parse(o["id"]!.GetValue<string>());
                _sinRespuesta.Remove(id);
                nuevos.Add(new Ordenado(EpocaDe(o), s, id, o["autor"]!.GetValue<string>(), JsonSerializer.SerializeToElement(o["datos"]!, SalaJson.Default.JsonNode)));
            }
            Seq = seq;
            _preparado = (nueva, seq);
        }
        foreach (Ordenado o in nuevos) Ordenada?.Invoke(o);
    }

    /// <summary>Época en que se aceptó un orden (la de la autoridad que lo repone puede ser mayor).</summary>
    private static long EpocaDe(JsonObject o) => (o["aceptada"] ?? o["epoca"])!.GetValue<long>();

    /// <summary>
    /// El suplente toma la sala cuando el anfitrión lleva <see cref="OpcionesSala.EsperaSuplente"/> sin responder.
    /// Abre con época + 1 y el historial que recibió; los que también lo perdieron lo buscan solos.
    /// </summary>
    public async Task TomarAnfitrionAsync(CancellationToken ct = default)
    {
        if (EsAnfitrion) throw new SalaException("invalid-parameters", "Ya eres el anfitrión.");
        if (_suplente?.Clave != Clave) throw new SalaException("invalid-parameters", "No eres el suplente de esta sala.");
        if (_sinAnfitrionDesde is not { } desde || Ahora - desde < (long)_opciones.EsperaSuplente.TotalMilliseconds)
            throw new SalaException("invalid-parameters", "El anfitrión no lleva bastante tiempo sin responder.");
        await AscenderAsync(Epoca + 1, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pasa a anfitrión en el mismo proceso. Rehace la tabla de ids con el historial y vuelve a poner en el orden las
    /// cargas propias que no tenían respuesta, con sus ids, salvo las que el historial ya trae.
    /// </summary>
    private async Task AscenderAsync(long epoca, CancellationToken ct = default)
    {
        await _mando.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (EsAnfitrion) return;
            string[] otros;
            lock (_cerrojo) otros = [.. _miembros.Keys.Where(k => k != Clave)];
            JsonElement r = await _red.LlamarAsync("ascender", new JsonObject { ["anclados"] = new JsonArray([.. otros.Select(k => (JsonNode)k)]) }, ct).ConfigureAwait(false);
            Direcciones = Cadenas(r.GetProperty("direcciones"));
            List<(Guid Id, JsonElement Datos, string[] Reservas)> mias = [];
            lock (_cerrojo)
            {
                _vistos.Clear();
                foreach (JsonObject o in _historial.Todo)
                    if (Guid.TryParse(o["id"]?.GetValue<string>(), out Guid id)) Recordar(o["autor"]!.GetValue<string>()).Aceptada(id, o["seq"]!.GetValue<long>());
                foreach ((Guid id, JsonObject c) in _sinRespuesta.ToList())
                {
                    if (_vistos.TryGetValue(Clave, out Recuerdo? yo) && yo.Buscar(id).Seq is not null)
                    {
                        _sinRespuesta.Remove(id);
                        continue;
                    }
                    JsonElement e = JsonSerializer.SerializeToElement(c, SalaJson.Default.JsonObject);
                    mias.Add((id, e.GetProperty("datos").Clone(), Limites.Textos(e, "reservas", MaxReservas) ?? []));
                }
                _noSalieron.Clear();
                _conectados.Clear();
                if (_miembros.TryGetValue(Clave, out Miembro? yoMiembro)) _miembros[Clave] = yoMiembro with { Rol = Rol.Editar };
                Epoca = epoca;
                _anfitrion = Clave;
                _anfitrionDeEpoca = Clave;
                _destinoTraspaso = null;
                _sinAnfitrionDesde = null;
                _preparado = null;
                _desfasado = false;
                _reconectando = false;
                _conectado = true;
                EsAnfitrion = true;
            }
            // Las propias, otra vez por el orden (mismo id): si no caben se quedan pendientes y se avisa con rechazo.
            foreach ((Guid id, JsonElement datos, string[] reservas) in mias)
                if (!Encolar(Clave, id, datos, reservas)) await ResponderRechazoAsync(Clave, id, "ocupado", "No cupo en la cola al tomar la sala.", recordar: false).ConfigureAwait(false);
        }
        finally
        {
            _mando.Release();
        }
    }

    /// <summary>Para las pruebas: mata el proceso de red como si se hubiera caído.</summary>
    internal ValueTask CortarRedParaPruebasAsync() => _red.DisposeAsync();

    /// <summary>SHA-256 de un archivo en 64 hexadecimales en minúscula.</summary>
    public static string HuellaDe(string ruta)
    {
        using FileStream f = File.OpenRead(ruta);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }
}
