using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala;

// El anfitrión: lo que llega de los invitados, el orden central y la reposición (docs/api.md §5.3).
public sealed partial class Sala
{
    private const int IdsAceptados = 8192, IdsRechazados = 1024;

    private readonly Dictionary<string, Cuota> _cuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cuota> _cuotasPresencia = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cuota> _cuotasReserva = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recuerdo> _vistos = new(StringComparer.Ordinal);
    private volatile bool _traspasando;

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
                    if (m.TryGetProperty("direcciones", out JsonElement dirs) && dirs.ValueKind == JsonValueKind.Array)
                        lock (_cerrojo) _direccionesDe[de] = [.. Textos(dirs).Take(16)];
                    await ReponerAsync(de, m.GetProperty("ultimoSeq").GetInt64()).ConfigureAwait(false);
                    // Con sus direcciones ya puede ser suplente: los demás tienen que saberlo.
                    await DifundirMiembrosAsync().ConfigureAwait(false);
                    break;
                }
            case "carga":
                {
                    if (!Guid.TryParse(m.GetProperty("id").GetString(), out Guid id)) return;
                    JsonElement datos = m.GetProperty("datos");
                    if (datos.ValueKind != JsonValueKind.Object) return;
                    string[] reservas = m.TryGetProperty("reservas", out JsonElement rs) && rs.ValueKind == JsonValueKind.Array
                        ? [.. rs.EnumerateArray().Select(x => x.GetString() ?? "")]
                        : [];
                    if (Encoding.UTF8.GetByteCount(datos.GetRawText()) > MaxCarga)
                    {
                        await ResponderRechazoAsync(de, id, "grande", "La carga pasa de 900 KiB.").ConfigureAwait(false);
                        return;
                    }
                    if (reservas.Length > MaxReservas || reservas.Any(c => !ClaveDeReservaValida(c)))
                    {
                        await ResponderRechazoAsync(de, id, "reserva", "Claves de reserva inválidas.").ConfigureAwait(false);
                        return;
                    }
                    switch (Admitir(_cuotas, de, RitmoMedio, RitmoRafaga))
                    {
                        case Paso.Expulsar:
                            await ExpulsarAsync(de).ConfigureAwait(false);
                            return;
                        case Paso.Ritmo:
                            await ResponderRechazoAsync(de, id, "ritmo", "Demasiadas cargas por segundo.").ConfigureAwait(false);
                            return;
                    }
                    await _porOrdenar.Writer.WriteAsync((autor, id, datos.Clone(), reservas)).ConfigureAwait(false);
                    break;
                }
            case "directo":
                {
                    Paso p = Admitir(_cuotas, de, RitmoMedio, RitmoRafaga);
                    if (p == Paso.Expulsar) await ExpulsarAsync(de).ConfigureAwait(false);
                    else if (p == Paso.Pasa && m.GetProperty("datos") is { ValueKind: JsonValueKind.Object } d) DirectoRecibido?.Invoke(new Directo(de, d.Clone()));
                    break;
                }
            case "reservar":
                await PedirReservaAsync(autor, m.GetProperty("clave").GetString() ?? "").ConfigureAwait(false);
                break;
            case "soltar":
                await SoltarReservaAsync(de, m.GetProperty("clave").GetString() ?? "").ConfigureAwait(false);
                break;
            case "presencia":
                if (m.GetProperty("datos") is { ValueKind: JsonValueKind.Object } pd) await PresenciaDeAsync(de, pd).ConfigureAwait(false);
                break;
        }
    }

    private async Task OrdenarAsync()
    {
        try
        {
            await foreach ((Miembro autor, Guid id, JsonElement datos, string[] reservas) in _porOrdenar.Reader.ReadAllAsync(_fin.Token).ConfigureAwait(false))
                await OrdenarUnaAsync(autor, id, datos, reservas).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OrdenarUnaAsync(Miembro autor, Guid id, JsonElement datos, string[] reservas)
    {
        // Durante un traspaso no se ordena nada: quien la mandó la tiene pendiente y la manda al anfitrión nuevo.
        if (_traspasando || !EsAnfitrion) return;
        JsonObject? repetida = null;
        lock (_cerrojo)
        {
            if (_vistos.TryGetValue(autor.Clave, out Recuerdo? r)) repetida = r.Buscar(id);
        }
        if (repetida is not null)
        {
            if (autor.Clave != Clave) await IntentarAsync(MandarAsync((JsonObject)repetida.DeepClone(), autor.Clave, _fin.Token)).ConfigureAwait(false);
            return;
        }

        string? ajena = null;
        lock (_cerrojo) ajena = reservas.FirstOrDefault(c => !_reservas.TryGetValue(c, out Reserva? x) || x.Dueno != autor.Clave);
        Veredicto v;
        if (autor.Rol == Rol.Mirar) v = Veredicto.No("rol", "Con el rol «mirar» no se puede editar.");
        else if (ajena is not null) v = Veredicto.No("reserva", $"La reserva «{ajena}» no es tuya.");
        else v = await ValidarAsync(autor, datos).ConfigureAwait(false);

        if (!v.Aceptada)
        {
            await ResponderRechazoAsync(autor.Clave, id, v.Motivo ?? "rechazada", v.Detalle).ConfigureAwait(false);
            return;
        }

        JsonObject orden;
        long seq;
        lock (_cerrojo)
        {
            // Seq y el historial cambian juntos: ReponerAsync los lee bajo el mismo candado.
            seq = ++Seq;
            orden = new() { ["type"] = "orden", ["epoca"] = Epoca, ["seq"] = seq, ["id"] = id.ToString(), ["autor"] = autor.Clave, ["datos"] = Nodo(datos) };
            Recordar(autor.Clave).Aceptada(id, orden);
            _historial.AddLast(orden);
            if (_historial.Count > HistorialMax) _historial.RemoveFirst();
            Renovar(autor.Clave);
        }
        await IntentarAsync(MandarAsync((JsonObject)orden.DeepClone(), null, _fin.Token)).ConfigureAwait(false);
        Ordenada?.Invoke(new Ordenado(Epoca, seq, id, autor.Clave, datos));
    }

    private async Task<Veredicto> ValidarAsync(Miembro autor, JsonElement datos)
    {
        using CancellationTokenSource tope = CancellationTokenSource.CreateLinkedTokenSource(_fin.Token);
        tope.CancelAfter(_opciones.TopeValidar);
        try
        {
            return await _validar(autor, datos, tope.Token).AsTask().WaitAsync(tope.Token).ConfigureAwait(false);
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
        lock (_cerrojo)
        {
            long masViejo = _historial.First?.Value["seq"]!.GetValue<long>() ?? Seq + 1;
            bool desfasado = ultimoSeq < Seq && ultimoSeq + 1 < masViejo;
            faltan = desfasado ? [] : [.. _historial.Where(o => o["seq"]!.GetValue<long>() > ultimoSeq).Select(o => (JsonObject)o.DeepClone())];
            bienvenida = new JsonObject { ["type"] = "bienvenida", ["epoca"] = Epoca, ["seq"] = Seq, ["reservas"] = ListaReservas() };
            PonerMiembros(bienvenida);
            if (desfasado) faltan.Add(new JsonObject { ["type"] = "desfasado", ["desde"] = masViejo });
        }
        await MandarAsync(bienvenida, a, _fin.Token).ConfigureAwait(false);
        foreach (JsonObject o in faltan) await MandarAsync(o, a, _fin.Token).ConfigureAwait(false);
    }

    /// <summary>Lista de miembros y suplente (el invitado conectado más antiguo con rol editar) en <paramref name="m"/>.</summary>
    private void PonerMiembros(JsonObject m)
    {
        m["miembros"] = new JsonArray([.. _miembros.Values.Select(x => (JsonNode)new JsonObject
        {
            ["clave"] = x.Clave,
            ["alias"] = x.Alias,
            ["rol"] = x.Rol == Rol.Mirar ? "mirar" : "editar",
        })]);
        string? suplente = _conectados.FirstOrDefault(c => _miembros.TryGetValue(c, out Miembro? x) && x.Rol == Rol.Editar && _direccionesDe.ContainsKey(c));
        if (suplente is not null)
            m["suplente"] = new JsonObject { ["clave"] = suplente, ["direcciones"] = new JsonArray([.. _direccionesDe[suplente].Select(d => (JsonNode)d)]) };
    }

    private async Task DifundirMiembrosAsync()
    {
        JsonObject m = new() { ["type"] = "miembros" };
        lock (_cerrojo) PonerMiembros(m);
        await IntentarAsync(MandarAsync(m, null, _fin.Token)).ConfigureAwait(false);
    }

    // ---------- ritmo y recuerdos ----------

    internal enum Paso { Pasa, Ritmo, Expulsar }

    private Paso Admitir(Dictionary<string, Cuota> cuotas, string de, double medio, double rafaga)
    {
        lock (_cerrojo)
        {
            if (!cuotas.TryGetValue(de, out Cuota? c)) cuotas[de] = c = new Cuota(medio, rafaga);
            return c.Tomar(DateTime.UtcNow);
        }
    }

    private Recuerdo Recordar(string clave)
    {
        if (!_vistos.TryGetValue(clave, out Recuerdo? r)) _vistos[clave] = r = new Recuerdo();
        return r;
    }

    /// <summary>Cubo de fichas (20/s y ráfaga de 40 para cargas); rechazos por 3 s al doble del ritmo: expulsión.</summary>
    internal sealed class Cuota(double medio = RitmoMedio, double rafaga = RitmoRafaga)
    {
        private readonly double _rafaga = rafaga;
        private double _fichas = rafaga;
        private DateTime _ultimo = DateTime.MinValue;
        private readonly Queue<DateTime> _rechazos = new();

        public Paso Tomar(DateTime ahora)
        {
            if (_ultimo != DateTime.MinValue) _fichas = Math.Min(_rafaga, _fichas + (ahora - _ultimo).TotalSeconds * medio);
            _ultimo = ahora;
            if (_fichas >= 1)
            {
                _fichas -= 1;
                return Paso.Pasa;
            }
            while (_rechazos.Count > 0 && ahora - _rechazos.Peek() > ExcesoExpulsa) _rechazos.Dequeue();
            _rechazos.Enqueue(ahora);
            return _rechazos.Count >= ExcesoExpulsa.TotalSeconds * medio ? Paso.Expulsar : Paso.Ritmo;
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
}
