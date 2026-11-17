using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dathinel.Sala;

// Reservas por clave de texto (docs/api.md §5.4) y presencia (§5.5). El anfitrión decide; los invitados reflejan.
public sealed partial class Sala
{
    public const int MaxReservas = 64;
    public const int MaxPresencia = 4096;
    private static readonly TimeSpan EsperaReserva = TimeSpan.FromSeconds(10);

    private sealed record Reserva(string Dueno, DateTime Ultima);

    private readonly Dictionary<string, Reserva> _reservas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<string?>> _esperandoReserva = new(StringComparer.Ordinal);

    /// <summary>Cambió quién tiene una reserva (<c>null</c>: quedó libre).</summary>
    public event Action<string, string?>? ReservaCambia;
    public event Action<string, JsonElement>? PresenciaRecibida;

    /// <summary>Clave → dueño de las reservas que este equipo conoce.</summary>
    public IReadOnlyDictionary<string, string> Reservas
    {
        get { lock (_cerrojo) return _reservas.ToDictionary(r => r.Key, r => r.Value.Dueno, StringComparer.Ordinal); }
    }

    [GeneratedRegex("^[a-z0-9:/._-]{1,128}$")]
    private static partial Regex ClaveReserva();

    /// <summary>Una etiqueta, nunca una ruta: <c>[a-z0-9:/._-]</c>, sin <c>..</c> ni <c>/</c> al principio o al final.</summary>
    public static bool ClaveDeReservaValida(string clave) =>
        ClaveReserva().IsMatch(clave) && !clave.Contains("..", StringComparison.Ordinal) && clave[0] != '/' && clave[^1] != '/';

    /// <summary>Pide la reserva. Vuelve <c>true</c> si es tuya (estaba libre o ya lo era).</summary>
    public async Task<bool> ReservarAsync(string clave, CancellationToken ct = default)
    {
        if (!ClaveDeReservaValida(clave)) throw new ArgumentException("Clave de reserva inválida.", nameof(clave));
        if (EsAnfitrion)
        {
            Miembro yo;
            lock (_cerrojo) yo = _miembros[Clave];
            return await PedirReservaAsync(yo, clave).ConfigureAwait(false) == Clave;
        }
        TaskCompletionSource<string?> tcs;
        lock (_cerrojo)
        {
            if (_esperandoReserva.TryGetValue(clave, out TaskCompletionSource<string?>? existente)) tcs = existente;
            else _esperandoReserva[clave] = tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await MandarAsync(new JsonObject { ["type"] = "reservar", ["clave"] = clave }, null, ct).ConfigureAwait(false);
        try
        {
            return await tcs.Task.WaitAsync(EsperaReserva, ct).ConfigureAwait(false) == Clave;
        }
        catch (TimeoutException)
        {
            lock (_cerrojo) _esperandoReserva.Remove(clave);
            return false;
        }
    }

    public async Task SoltarAsync(string clave, CancellationToken ct = default)
    {
        if (EsAnfitrion) await SoltarReservaAsync(Clave, clave).ConfigureAwait(false);
        else await MandarAsync(new JsonObject { ["type"] = "soltar", ["clave"] = clave }, null, ct).ConfigureAwait(false);
    }

    /// <summary>Cabezal, cursor o selección: objeto de hasta 4 KiB; fuera del orden y sin guardar.</summary>
    public async Task PresenciaAsync(JsonElement datos, CancellationToken ct = default)
    {
        if (datos.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(datos.GetRawText()) > MaxPresencia)
            throw new ArgumentException("La presencia es un objeto de hasta 4 KiB.", nameof(datos));
        if (EsAnfitrion) await PresenciaDeAsync(Clave, datos).ConfigureAwait(false);
        else if (_conectado) await IntentarAsync(MandarAsync(new JsonObject { ["type"] = "presencia", ["datos"] = Nodo(datos) }, null, ct)).ConfigureAwait(false);
    }

    // ---------- anfitrión ----------

    /// <returns>El dueño después de pedirla.</returns>
    private async Task<string?> PedirReservaAsync(Miembro quien, string clave)
    {
        string? dueno;
        bool cambio = false;
        lock (_cerrojo)
        {
            if (!ClaveDeReservaValida(clave)) return null;
            _reservas.TryGetValue(clave, out Reserva? r);
            bool puede = quien.Rol == Rol.Editar
                && (r is null || r.Dueno == quien.Clave)
                && (r is not null || _reservas.Values.Count(x => x.Dueno == quien.Clave) < MaxReservas)
                && (quien.Clave == Clave || Admitir(_cuotasReserva, quien.Clave, 5, 10) == Paso.Pasa);
            if (puede)
            {
                cambio = r is null;
                _reservas[clave] = new Reserva(quien.Clave, DateTime.UtcNow);
            }
            dueno = _reservas.TryGetValue(clave, out Reserva? d) ? d.Dueno : null;
        }
        JsonObject aviso = new() { ["type"] = "reserva", ["clave"] = clave, ["de"] = dueno };
        if (cambio)
        {
            ReservaCambia?.Invoke(clave, dueno);
            await IntentarAsync(MandarAsync(aviso, null, _fin.Token)).ConfigureAwait(false);
        }
        else if (quien.Clave != Clave)
        {
            await IntentarAsync(MandarAsync(aviso, quien.Clave, _fin.Token)).ConfigureAwait(false);
        }
        return dueno;
    }

    private async Task SoltarReservaAsync(string quien, string clave)
    {
        lock (_cerrojo)
        {
            if (!_reservas.TryGetValue(clave, out Reserva? r) || r.Dueno != quien) return;
            _reservas.Remove(clave);
        }
        ReservaCambia?.Invoke(clave, null);
        await IntentarAsync(MandarAsync(new JsonObject { ["type"] = "reserva", ["clave"] = clave, ["de"] = null }, null, _fin.Token)).ConfigureAwait(false);
    }

    private readonly List<string> _sueltas = [];

    /// <summary>Quita las reservas de <paramref name="quien"/> (bajo el candado); se avisan con <see cref="DifundirReservasDeAsync"/>.</summary>
    private void SoltarTodas(string quien)
    {
        foreach (string c in _reservas.Where(r => r.Value.Dueno == quien).Select(r => r.Key).ToList())
        {
            _reservas.Remove(c);
            _sueltas.Add(c);
        }
    }

    private async Task DifundirReservasDeAsync()
    {
        string[] sueltas;
        lock (_cerrojo)
        {
            sueltas = [.. _sueltas];
            _sueltas.Clear();
        }
        foreach (string c in sueltas)
        {
            ReservaCambia?.Invoke(c, null);
            await IntentarAsync(MandarAsync(new JsonObject { ["type"] = "reserva", ["clave"] = c, ["de"] = null }, null, _fin.Token)).ConfigureAwait(false);
        }
    }

    /// <summary>Actividad del dueño (carga o presencia): sus reservas no caducan. Bajo el candado.</summary>
    private void Renovar(string quien)
    {
        foreach (KeyValuePair<string, Reserva> r in _reservas.Where(r => r.Value.Dueno == quien).ToList())
            _reservas[r.Key] = r.Value with { Ultima = DateTime.UtcNow };
    }

    private JsonArray ListaReservas() =>
        [.. _reservas.Select(r => (JsonNode)new JsonObject { ["clave"] = r.Key, ["de"] = r.Value.Dueno })];

    private async Task PresenciaDeAsync(string de, JsonElement datos)
    {
        if (Encoding.UTF8.GetByteCount(datos.GetRawText()) > MaxPresencia) return;
        // Por encima de 20/s se descarta en silencio; no cuenta para expulsar.
        if (de != Clave && Admitir(_cuotasPresencia, de, 20, 20) != Paso.Pasa) return;
        lock (_cerrojo) Renovar(de);
        if (de != Clave) PresenciaRecibida?.Invoke(de, datos.Clone());
        string[] otros;
        lock (_cerrojo) otros = [.. _conectados.Where(c => c != de)];
        JsonObject m = new() { ["type"] = "presencia", ["de"] = de, ["datos"] = Nodo(datos) };
        foreach (string a in otros) await IntentarAsync(MandarAsync((JsonObject)m.DeepClone(), a, _fin.Token)).ConfigureAwait(false);
    }

    /// <summary>Anfitrión: suelta las reservas sin actividad. Invitado: cuenta el tiempo sin anfitrión.</summary>
    private async Task VigilarAsync()
    {
        using PeriodicTimer reloj = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await reloj.WaitForNextTickAsync(_fin.Token).ConfigureAwait(false))
            {
                if (!EsAnfitrion) continue;
                DateTime limite = DateTime.UtcNow - _opciones.ReservaInactiva;
                lock (_cerrojo)
                {
                    foreach (string c in _reservas.Where(r => r.Value.Ultima < limite).Select(r => r.Key).ToList())
                    {
                        _reservas.Remove(c);
                        _sueltas.Add(c);
                    }
                }
                await DifundirReservasDeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------- invitado ----------

    private void LeerReservas(JsonElement lista)
    {
        _reservas.Clear();
        foreach (JsonElement r in lista.EnumerateArray())
            _reservas[r.GetProperty("clave").GetString()!] = new Reserva(r.GetProperty("de").GetString()!, DateTime.UtcNow);
    }

    private void AplicarReserva(string clave, string? de)
    {
        TaskCompletionSource<string?>? tcs;
        lock (_cerrojo)
        {
            if (de is null) _reservas.Remove(clave);
            else _reservas[clave] = new Reserva(de, DateTime.UtcNow);
            _esperandoReserva.Remove(clave, out tcs);
        }
        tcs?.TrySetResult(de);
        ReservaCambia?.Invoke(clave, de);
    }
}
