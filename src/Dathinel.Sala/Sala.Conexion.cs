using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Dathinel.Sala;

// Conexión del invitado, reconexión automática, traspaso de anfitrión y suplente (docs/api.md §5.6).
public sealed partial class Sala
{
    private static readonly TimeSpan EsperaSalidaTraspaso = TimeSpan.FromSeconds(30);

    private readonly OrderedDictionary<Guid, JsonObject> _sinRespuesta = [];
    private readonly Channel<string> _salidas = Channel.CreateUnbounded<string>();
    private string[] _anfitrionDirecciones = [];
    private (string Clave, string[] Direcciones)? _suplente;
    private (string Clave, string[] Direcciones)? _destinoTraspaso;
    private DateTime? _sinAnfitrionDesde;
    private volatile bool _conectado;
    private volatile bool _reconectando;
    private volatile bool _cambiandoProceso;

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

    private async Task EntrarAsync(string? billete, string? anfitrion, IEnumerable<string>? direcciones, CancellationToken ct)
    {
        JsonObject p = Base(_identidad, _opciones);
        p["alias"] = _opciones.Alias;
        if (billete is not null) p["billete"] = billete;
        else
        {
            p["anfitrion"] = anfitrion;
            p["direcciones"] = new JsonArray([.. (direcciones ?? []).Select(d => (JsonNode)d)]);
        }
        JsonElement r = await _red.LlamarAsync("unirse", p, ct).ConfigureAwait(false);
        _anfitrion = r.GetProperty("anfitrion").GetString()!;
        _anfitrionDirecciones = Textos(r.GetProperty("anfitrionDirecciones"));
        Direcciones = Textos(r.GetProperty("direcciones"));
        JsonElement e = await _red.LlamarAsync("estado", null, ct).ConfigureAwait(false);
        Clave = e.GetProperty("clave").GetString()!;
    }

    private Task<JsonElement> SincronizarAsync(CancellationToken ct) => MandarAsync(new JsonObject
    {
        ["type"] = "sincronizar",
        ["epoca"] = Epoca,
        ["ultimoSeq"] = Seq,
        ["direcciones"] = new JsonArray([.. Direcciones.Select(d => (JsonNode)d)]),
    }, null, ct);

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

    /// <summary>Reintenta hasta volver (true) o hasta un no definitivo o el cierre (false).</summary>
    private async Task<bool> ReconectarAsync(bool procesoNuevo)
    {
        _conectado = false;
        _sinAnfitrionDesde ??= DateTime.UtcNow;
        if (!_reconectando)
        {
            _reconectando = true;
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
                if (_suplente is { } s && s.Clave != Clave && DateTime.UtcNow - _sinAnfitrionDesde >= _opciones.EsperaSuplente)
                    candidatos.Add(s);
                candidatos.Add((_anfitrion, _anfitrionDirecciones));
            }
            foreach ((string clave, string[] dirs) in candidatos)
            {
                try
                {
                    if (procesoNuevo)
                    {
                        await CambiarProcesoAsync().ConfigureAwait(false);
                        procesoNuevo = false;
                    }
                    await EntrarAsync(null, clave, dirs, _fin.Token).ConfigureAwait(false);
                    if (EsAnfitrion) return true;
                    _destinoTraspaso = null;
                    await SincronizarAsync(_fin.Token).ConfigureAwait(false);
                    return true;
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
    /// Pasa la sala a <paramref name="clave"/> (conectado y con rol editar): le da el historial como blob, todos se
    /// reconectan a él con época + 1 y este equipo vuelve como invitado.
    /// </summary>
    public async Task TraspasarAsync(string clave, CancellationToken ct = default)
    {
        if (!EsAnfitrion) throw new SalaException("invalid-parameters", "Solo el anfitrión traspasa la sala.");
        string[]? dirs;
        JsonArray historial;
        long seq;
        lock (_cerrojo)
        {
            if (!_conectados.Contains(clave) || !_miembros.TryGetValue(clave, out Miembro? m) || m.Rol != Rol.Editar || !_direccionesDe.TryGetValue(clave, out dirs))
                throw new SalaException("desconocido", "Ese miembro no está conectado o no puede editar.");
            _traspasando = true;
            historial = [.. _historial.Select(o => o.DeepClone())];
            seq = Seq;
        }
        string carpeta = Path.Combine(Path.GetFullPath(_opciones.Carpeta), ".sala");
        Directory.CreateDirectory(carpeta);
        string ruta = Path.Combine(carpeta, $"historial-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(ruta, JsonSerializer.SerializeToUtf8Bytes(historial, SalaJson.Default.JsonArray), ct).ConfigureAwait(false);
        string huella = HuellaDe(ruta);
        long tamano = new FileInfo(ruta).Length;
        await OfrecerBlobsAsync([(ruta, huella, tamano)], ct).ConfigureAwait(false);
        while (_salidas.Reader.TryRead(out _)) { }
        await MandarAsync(new JsonObject
        {
            ["type"] = "traspaso",
            ["a"] = clave,
            ["epoca"] = Epoca + 1,
            ["seq"] = seq,
            ["direcciones"] = new JsonArray([.. dirs.Select(d => (JsonNode)d)]),
            ["historial"] = new JsonObject { ["huella"] = huella, ["tamano"] = tamano },
        }, null, ct).ConfigureAwait(false);

        // El nuevo corta con nosotros al tomar la sala (o se da por hecho a los 30 s).
        using (CancellationTokenSource tope = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            tope.CancelAfter(EsperaSalidaTraspaso);
            try
            {
                while (await _salidas.Reader.ReadAsync(tope.Token).ConfigureAwait(false) != clave) { }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        _cambiandoProceso = true;
        try
        {
            EsAnfitrion = false;
            _anfitrion = clave;
            _anfitrionDirecciones = dirs;
            _destinoTraspaso = (clave, dirs);
            ProcesoRed viejo = _red;
            _red = await ProcesoRed.AbrirAsync(_opciones.Ejecutable, ct).ConfigureAwait(false);
            await viejo.DisposeAsync().ConfigureAwait(false);
            _traspasando = false;
            if (!await ReconectarAsync(procesoNuevo: false).ConfigureAwait(false))
                throw new SalaException("sin-conexion", "No se pudo entrar en la sala del nuevo anfitrión.");
        }
        finally
        {
            _cambiandoProceso = false;
            File.Delete(ruta);
        }
    }

    private async Task TraspasoRecibidoAsync(JsonElement m)
    {
        string a = m.GetProperty("a").GetString()!;
        string[] dirs = Textos(m.GetProperty("direcciones"));
        long epoca = m.GetProperty("epoca").GetInt64();
        if (epoca <= Epoca) return;
        if (a != Clave)
        {
            _destinoTraspaso = (a, dirs);
            if (!await ReconectarAsync(procesoNuevo: false).ConfigureAwait(false)) Cerrada?.Invoke("traspaso");
            return;
        }
        string huella = m.GetProperty("historial").GetProperty("huella").GetString()!;
        long tamano = m.GetProperty("historial").GetProperty("tamano").GetInt64();
        // Fuera del bucle de lectura: la descarga avisa por eventos que ese mismo bucle tiene que leer.
        _ = Task.Run(async () =>
        {
            try
            {
                await AscenderPorTraspasoAsync(huella, tamano, epoca).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SalaException or IOException or JsonException or OperationCanceledException)
            {
                Cerrada?.Invoke("traspaso");
            }
        });
    }

    private async Task AscenderPorTraspasoAsync(string huella, long tamano, long epoca)
    {
        string carpeta = Path.Combine(Path.GetFullPath(_opciones.Carpeta), ".sala");
        Directory.CreateDirectory(carpeta);
        string ruta = Path.Combine(carpeta, $"historial-{huella[..16]}.json");
        await PedirBlobAsync(_anfitrion, huella, tamano, ruta, MaxHistorialBytes, _fin.Token).ConfigureAwait(false);
        JsonArray recibido = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(ruta, _fin.Token).ConfigureAwait(false), SalaJson.Default.JsonArray)!;
        File.Delete(ruta);
        foreach (JsonObject o in recibido.OfType<JsonObject>().OrderBy(o => o["seq"]!.GetValue<long>()))
        {
            long seq = o["seq"]!.GetValue<long>();
            if (seq != Seq + 1) continue;
            Seq = seq;
            lock (_cerrojo) _historial.AddLast(o);
            Ordenada?.Invoke(new Ordenado(o["epoca"]!.GetValue<long>(), seq, Guid.Parse(o["id"]!.GetValue<string>()), o["autor"]!.GetValue<string>(), JsonSerializer.SerializeToElement(o["datos"]!, SalaJson.Default.JsonNode)));
        }
        await AscenderAsync(epoca).ConfigureAwait(false);
    }

    private const long MaxHistorialBytes = 64L << 20;

    /// <summary>
    /// El suplente toma la sala cuando el anfitrión lleva <see cref="OpcionesSala.EsperaSuplente"/> sin responder.
    /// Abre con época + 1 y el historial que recibió; los que también lo perdieron lo buscan solos.
    /// </summary>
    public async Task TomarAnfitrionAsync(CancellationToken ct = default)
    {
        if (EsAnfitrion) throw new SalaException("invalid-parameters", "Ya eres el anfitrión.");
        if (_suplente?.Clave != Clave) throw new SalaException("invalid-parameters", "No eres el suplente de esta sala.");
        if (_sinAnfitrionDesde is not { } desde || DateTime.UtcNow - desde < _opciones.EsperaSuplente)
            throw new SalaException("invalid-parameters", "El anfitrión no lleva bastante tiempo sin responder.");
        await AscenderAsync(Epoca + 1, ct).ConfigureAwait(false);
    }

    private async Task AscenderAsync(long epoca, CancellationToken ct = default)
    {
        string[] otros;
        lock (_cerrojo)
        {
            otros = [.. _miembros.Keys.Where(k => k != Clave)];
            _vistos.Clear();
            foreach (JsonObject o in _historial)
                if (Guid.TryParse(o["id"]?.GetValue<string>(), out Guid id)) Recordar(o["autor"]!.GetValue<string>()).Aceptada(id, o);
            // Mis cargas sin respuesta pasan por el orden como cualquier otra.
            _sinRespuesta.Clear();
        }
        JsonElement r = await _red.LlamarAsync("ascender", new JsonObject { ["anclados"] = new JsonArray([.. otros.Select(k => (JsonNode)k)]) }, ct).ConfigureAwait(false);
        Direcciones = Textos(r.GetProperty("direcciones"));
        lock (_cerrojo)
        {
            _conectados.Clear();
            if (_miembros.TryGetValue(Clave, out Miembro? yo)) _miembros[Clave] = yo with { Rol = Rol.Editar };
        }
        Epoca = epoca;
        _anfitrion = Clave;
        _destinoTraspaso = null;
        _sinAnfitrionDesde = null;
        _reconectando = false;
        _conectado = true;
        EsAnfitrion = true;
        _orden = Task.Run(OrdenarAsync, CancellationToken.None);
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
