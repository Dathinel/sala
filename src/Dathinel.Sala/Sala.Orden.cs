using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala;

// El anfitrión: lo que llega de los invitados, el orden central y la reposición (docs/api.md §5.3).
public sealed partial class Sala
{
    private const int IdsAceptados = 8192, IdsRechazados = 1024;
    /// <summary>Cola de cargas por ordenar: por miembro y en total, para que un validador lento no deje crecer la memoria.</summary>
    internal const int ColaPorMiembro = 64;
    internal const long ColaBytesPorMiembro = 16L << 20, ColaBytes = 64L << 20;

    private readonly Dictionary<string, Cuota> _cuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cuota> _cuotasPresencia = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cuota> _cuotasReserva = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recuerdo> _vistos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int N, long Bytes)> _enCola = new(StringComparer.Ordinal);
    private long _colaBytes;
    /// <summary>Invitados que se saltaron un orden por cola llena: el vigía les anuncia el seq para que lo pidan.</summary>
    private readonly HashSet<string> _atrasados = new(StringComparer.Ordinal);

    private async Task DeInvitadoAsync(string de, JsonElement m)
    {
        Miembro? autor;
        lock (_cerrojo) _miembros.TryGetValue(de, out autor);
        if (autor is null) return;
        switch (Limites.Texto(m, "type"))
        {
            case "sincronizar":
                {
                    if (Limites.Entero(m, "epoca") is not { } suEpoca || Limites.Entero(m, "ultimoSeq") is not { } ultimo) return;
                    // Dos anfitriones (§5.6): manda la época mayor y, a igualdad, la clave menor.
                    string? suAnfitrion = Limites.Texto(m, "anfitrion");
                    if (!Limites.EsClave(suAnfitrion)) suAnfitrion = null;
                    if (suEpoca > Epoca || (suEpoca == Epoca && suAnfitrion is not null && suAnfitrion != Clave && string.CompareOrdinal(suAnfitrion, Clave) < 0))
                    {
                        Superada?.Invoke(suEpoca, suAnfitrion ?? de);
                        // Solo la bienvenida, sin reponer: con ella el invitado ve que esta sala quedó atrás.
                        _ = Task.Run(() => ReponerAsync(de, long.MaxValue));
                        return;
                    }
                    if (Limites.Textos(m, "direcciones", Limites.MaxDirecciones) is { } dirs)
                        lock (_cerrojo) _direccionesDe[de] = dirs;
                    lock (_cerrojo) _atrasados.Remove(de);
                    // Aparte del bucle de lectura: con la cola del invitado llena, reponer espera y reintenta.
                    _ = Task.Run(async () =>
                    {
                        await ReponerAsync(de, ultimo).ConfigureAwait(false);
                        await MandarPresenciasAsync(de).ConfigureAwait(false);
                    });
                    // Con sus direcciones ya puede ser suplente: los demás tienen que saberlo.
                    await DifundirMiembrosAsync().ConfigureAwait(false);
                    break;
                }
            case "carga":
                {
                    if (Limites.Id(m) is not { } id) return;
                    if (!m.TryGetProperty("datos", out JsonElement datos) || datos.ValueKind != JsonValueKind.Object) return;
                    string[] reservas = Limites.Textos(m, "reservas", MaxReservas + 1) ?? [];
                    switch (Admitir(_cuotas, de, RitmoMedio, RitmoRafaga))
                    {
                        case Paso.Expulsar:
                            await ExpulsarAsync(de).ConfigureAwait(false);
                            return;
                        case Paso.Ritmo:
                            // Sin esperar la respuesta: con un par que inunda, el bucle de lectura tiene que ir más
                            // rápido que él, o el ritmo medido sería el nuestro y no el suyo.
                            _ = ResponderRechazoAsync(de, id, "ritmo", "Demasiadas cargas por segundo.", recordar: false);
                            return;
                    }
                    if (!Limites.Objeto(datos, MaxCarga))
                    {
                        await ResponderRechazoAsync(de, id, "grande", "La carga pasa de 900 KiB o de 27 niveles.").ConfigureAwait(false);
                        return;
                    }
                    if (reservas.Length > MaxReservas || reservas.Any(c => !ClaveDeReservaValida(c)))
                    {
                        await ResponderRechazoAsync(de, id, "reserva", "Claves de reserva inválidas.").ConfigureAwait(false);
                        return;
                    }
                    // Cada carga del dueño es actividad: sus reservas no caducan mientras siga mandando (§5.4).
                    lock (_cerrojo) Renovar(de);
                    if (!Encolar(de, id, datos.Clone(), reservas))
                        _ = ResponderRechazoAsync(de, id, "ocupado", "El anfitrión tiene demasiadas cargas tuyas por ordenar; reintenta.", recordar: false);
                    break;
                }
            case "directo":
                {
                    Paso p = Admitir(_cuotas, de, RitmoMedio, RitmoRafaga);
                    if (p == Paso.Expulsar) await ExpulsarAsync(de).ConfigureAwait(false);
                    else if (p == Paso.Pasa && m.TryGetProperty("datos", out JsonElement d) && Limites.Objeto(d, MaxCarga))
                        DirectoRecibido?.Invoke(new Directo(de, d.Clone()));
                    break;
                }
            case "reservar":
                await PedirReservaAsync(autor, Limites.Texto(m, "clave") ?? "").ConfigureAwait(false);
                break;
            case "soltar":
                await SoltarReservaAsync(de, Limites.Texto(m, "clave") ?? "").ConfigureAwait(false);
                break;
            case "presencia":
                if (m.TryGetProperty("datos", out JsonElement pd) && Limites.Objeto(pd, MaxPresencia)) await PresenciaDeAsync(de, pd).ConfigureAwait(false);
                break;
            case "traspaso-listo":
                if (Limites.Entero(m, "nueva") is { } nueva && Limites.Entero(m, "seq") is { } sq) RespuestaDeSucesor(de, nueva, sq, listo: true);
                break;
            case "traspaso-fallo":
                RespuestaDeSucesor(de, 0, 0, listo: false);
                break;
        }
    }

    /// <summary>Pone la carga en la cola si caben: 64 y 16 MiB por miembro, 64 MiB en total. Si no, false.</summary>
    private bool Encolar(string autor, Guid id, JsonElement datos, string[] reservas)
    {
        long bytes = Limites.Bytes(datos);
        lock (_cerrojo)
        {
            (int n, long b) = _enCola.GetValueOrDefault(autor);
            if (n >= ColaPorMiembro || b + bytes > ColaBytesPorMiembro || _colaBytes + bytes > ColaBytes) return false;
            _enCola[autor] = (n + 1, b + bytes);
            _colaBytes += bytes;
        }
        if (_porOrdenar.Writer.TryWrite((autor, id, datos, reservas, bytes))) return true;
        Desencolar(autor, bytes);
        return false;
    }

    private void Desencolar(string autor, long bytes)
    {
        lock (_cerrojo)
        {
            _colaBytes -= bytes;
            if (_enCola.TryGetValue(autor, out var x))
            {
                if (x.N <= 1) _enCola.Remove(autor);
                else _enCola[autor] = (x.N - 1, x.Bytes - bytes);
            }
        }
    }

    private async Task OrdenarAsync()
    {
        try
        {
            await foreach ((string autor, Guid id, JsonElement datos, string[] reservas, long bytes) in _porOrdenar.Reader.ReadAllAsync(_fin.Token).ConfigureAwait(false))
            {
                Desencolar(autor, bytes);
                await OrdenarUnaAsync(autor, id, datos, reservas).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Por qué <paramref name="autor"/> no puede ordenar ahora (null si puede). Bajo el candado.</summary>
    private Veredicto? Impedimento(string autor, string[] reservas, out bool fuera)
    {
        fuera = !_miembros.TryGetValue(autor, out Miembro? m);
        if (fuera) return Veredicto.No("fuera");
        if (m!.Rol == Rol.Mirar) return Veredicto.No("rol", "Con el rol «mirar» no se puede editar.");
        string? ajena = reservas.FirstOrDefault(c => !_reservas.TryGetValue(c, out Reserva? x) || x.Dueno != autor);
        return ajena is null ? null : Veredicto.No("reserva", $"La reserva «{ajena}» no es tuya.");
    }

    private async Task OrdenarUnaAsync(string autor, Guid id, JsonElement datos, string[] reservas)
    {
        await _mando.WaitAsync(_fin.Token).ConfigureAwait(false);
        try
        {
            // Si este equipo ya no manda (traspaso), quien la mandó la tiene pendiente y la manda al anfitrión nuevo.
            if (EsAnfitrion) await OrdenarConMandoAsync(autor, id, datos, reservas).ConfigureAwait(false);
        }
        finally
        {
            _mando.Release();
        }
    }

    private async Task OrdenarConMandoAsync(string autor, Guid id, JsonElement datos, string[] reservas)
    {
        long? repetida = null;
        JsonObject? rechazo = null;
        Miembro? quien;
        Veredicto? no;
        bool fuera;
        lock (_cerrojo)
        {
            if (_vistos.TryGetValue(autor, out Recuerdo? r)) (repetida, rechazo) = r.Buscar(id);
            no = Impedimento(autor, reservas, out fuera);
            _miembros.TryGetValue(autor, out quien);
        }
        if (repetida is not null || rechazo is not null)
        {
            await ContestarRepetidaAsync(autor, id, repetida, rechazo).ConfigureAwait(false);
            return;
        }
        if (fuera) return; // expulsado mientras esperaba en la cola: no hay a quién contestar
        Veredicto v = no ?? await ValidarAsync(quien!, datos).ConfigureAwait(false);
        if (!v.Aceptada)
        {
            await ResponderRechazoAsync(autor, id, v.Motivo ?? "rechazada", v.Detalle).ConfigureAwait(false);
            return;
        }

        JsonObject orden;
        long seq;
        lock (_cerrojo)
        {
            // La validación se hizo con el rol y las reservas de entonces: si cambiaron mientras, no entra.
            no = Impedimento(autor, reservas, out fuera);
            if (no is null)
            {
                seq = ++Seq;
                // «epoca» la pone al mandar la autoridad de cada momento; «aceptada» guarda la de ahora (§5.3).
                orden = new() { ["type"] = "orden", ["epoca"] = Epoca, ["aceptada"] = Epoca, ["seq"] = seq, ["id"] = id.ToString(), ["autor"] = autor, ["datos"] = Nodo(datos) };
                Recordar(autor).Aceptada(id, seq);
                _historial.Agregar(seq, orden);
                if (autor == Clave) _sinRespuesta.Remove(id);
            }
            else
            {
                seq = 0;
                orden = null!;
            }
        }
        if (fuera) return;
        if (no is not null)
        {
            await ResponderRechazoAsync(autor, id, no.Motivo!, no.Detalle).ConfigureAwait(false);
            return;
        }
        await DifundirOrdenAsync((JsonObject)orden.DeepClone()).ConfigureAwait(false);
        Ordenada?.Invoke(new Ordenado(Epoca, seq, id, autor, datos));
    }

    /// <summary>Manda el orden a todos; quien tenía la cola llena se lo salta y queda anotado para avisarle (§2, <c>llenos</c>).</summary>
    private async Task DifundirOrdenAsync(JsonObject orden)
    {
        try
        {
            JsonElement r = await MandarAsync(orden, null, _fin.Token).ConfigureAwait(false);
            if (Limites.Textos(r, "llenos", 64) is { Length: > 0 } llenos)
                lock (_cerrojo) _atrasados.UnionWith(llenos);
        }
        catch (SalaException)
        {
        }
    }

    /// <summary>Vigía del anfitrión: a cada atrasado, el seq actual; al verlo, él pide lo que le falta.</summary>
    private async Task AvisarAtrasadosAsync()
    {
        string[] atrasados;
        long seq;
        lock (_cerrojo)
        {
            atrasados = [.. _atrasados];
            seq = Seq;
        }
        foreach (string a in atrasados)
        {
            try
            {
                await MandarAsync(new JsonObject { ["type"] = "seq", ["seq"] = seq }, a, _fin.Token).ConfigureAwait(false);
                lock (_cerrojo) _atrasados.Remove(a);
            }
            catch (SalaException ex) when (ex.Codigo != "ocupado-red")
            {
                lock (_cerrojo) _atrasados.Remove(a); // se fue: al volver sincroniza
            }
            catch (SalaException)
            {
            }
        }
    }

    /// <summary>La misma respuesta que la primera vez; si el orden ya no se guarda, <c>ya-ordenada</c> con su seq.</summary>
    private async Task ContestarRepetidaAsync(string autor, Guid id, long? seq, JsonObject? rechazo)
    {
        if (autor == Clave) return;
        JsonObject respuesta;
        if (rechazo is not null) respuesta = (JsonObject)rechazo.DeepClone();
        else
        {
            JsonObject? o;
            lock (_cerrojo) o = _historial.Buscar(seq!.Value);
            respuesta = o is not null ? (JsonObject)o.DeepClone() : new JsonObject { ["type"] = "ya-ordenada", ["id"] = id.ToString(), ["seq"] = seq };
        }
        await IntentarAsync(MandarAsync(respuesta, autor, _fin.Token)).ConfigureAwait(false);
    }

    /// <summary>
    /// Llama a la app con su tope. Va en otro hilo: un <c>validar</c> que se bloquea antes de devolver el ValueTask no
    /// para el ordenador. Si ignora la cancelación, sigue vivo hasta que termine (contrato de §5.3).
    /// </summary>
    private async Task<Veredicto> ValidarAsync(Miembro autor, JsonElement datos)
    {
        using CancellationTokenSource tope = CancellationTokenSource.CreateLinkedTokenSource(_fin.Token);
        tope.CancelAfter(_opciones.TopeValidar);
        try
        {
            return await Task.Run(() => _validar(autor, datos, tope.Token).AsTask(), tope.Token).WaitAsync(tope.Token).ConfigureAwait(false);
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

    private async Task ResponderRechazoAsync(string a, Guid id, string motivo, string? detalle, bool recordar = true)
    {
        JsonObject r = new() { ["type"] = "rechazado", ["id"] = id.ToString(), ["motivo"] = motivo };
        if (detalle is not null) r["detalle"] = detalle.Length > Limites.MaxDetalle ? detalle[..Limites.MaxDetalle] : detalle;
        if (recordar)
        {
            lock (_cerrojo) Recordar(a).Rechazada(id, r);
        }
        if (a == Clave)
        {
            lock (_cerrojo) _sinRespuesta.Remove(id);
            Rechazada?.Invoke(new Rechazo(id, motivo, detalle));
        }
        else await IntentarAsync(MandarAsync((JsonObject)r.DeepClone(), a, _fin.Token)).ConfigureAwait(false);
    }

    private async Task ReponerAsync(string a, long ultimoSeq)
    {
        List<JsonObject> faltan;
        JsonObject bienvenida;
        lock (_cerrojo)
        {
            long masViejo = _historial.MasViejo ?? Seq + 1;
            bool desfasado = ultimoSeq < Seq && ultimoSeq + 1 < masViejo;
            faltan = desfasado ? [] : [.. _historial.Desde(ultimoSeq).Select(o => (JsonObject)o.DeepClone())];
            bienvenida = new JsonObject { ["type"] = "bienvenida", ["epoca"] = Epoca, ["seq"] = Seq, ["reservas"] = ListaReservas() };
            PonerMiembros(bienvenida);
            if (desfasado) faltan.Add(new JsonObject { ["type"] = "desfasado", ["desde"] = masViejo });
        }
        try
        {
            await MandarEsperandoAsync(bienvenida, a).ConfigureAwait(false);
            foreach (JsonObject o in faltan) await MandarEsperandoAsync(o, a).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SalaException or OperationCanceledException)
        {
            // Se fue o no hubo sitio en 30 s: al volver pedirá otra vez.
        }
    }

    /// <summary>A un destino; con su cola llena espera y reintenta (hasta 30 s) en vez de cortar la reposición.</summary>
    private async Task MandarEsperandoAsync(JsonObject m, string a)
    {
        long limite = Ahora + 30_000;
        while (true)
        {
            try
            {
                await MandarAsync((JsonObject)m.DeepClone(), a, _fin.Token).ConfigureAwait(false);
                return;
            }
            catch (SalaException ex) when (ex.Codigo == "ocupado-red" && Ahora < limite)
            {
                await Task.Delay(50, _fin.Token).ConfigureAwait(false);
            }
        }
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

    /// <summary>Olvida lo que la sala guardaba de un miembro que ya no puede volver (expulsado con veto). Bajo el candado.</summary>
    private void Olvidar(string clave)
    {
        _vistos.Remove(clave);
        _cuotas.Remove(clave);
        _cuotasPresencia.Remove(clave);
        _cuotasReserva.Remove(clave);
        _direccionesDe.Remove(clave);
        _presencias.Remove(clave);
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

    /// <summary>
    /// Ids ya vistos de un miembro: de los aceptados solo el seq (el <c>orden</c> está en el historial o su respaldo);
    /// de los rechazados, la respuesta entera (pequeña).
    /// </summary>
    internal sealed class Recuerdo
    {
        private readonly Dictionary<Guid, long> _aceptadas = [];
        private readonly Dictionary<Guid, JsonObject> _rechazadas = [];
        private readonly Queue<Guid> _colaAceptadas = new(), _colaRechazadas = new();

        public (long? Seq, JsonObject? Rechazo) Buscar(Guid id) =>
            (_aceptadas.TryGetValue(id, out long s) ? s : null, _rechazadas.GetValueOrDefault(id));

        public int Count => _aceptadas.Count + _rechazadas.Count;

        public void Aceptada(Guid id, long seq)
        {
            if (!_aceptadas.TryAdd(id, seq)) return;
            _colaAceptadas.Enqueue(id);
            if (_colaAceptadas.Count > IdsAceptados) _aceptadas.Remove(_colaAceptadas.Dequeue());
        }

        public void Rechazada(Guid id, JsonObject r)
        {
            if (!_rechazadas.TryAdd(id, r)) return;
            _colaRechazadas.Enqueue(id);
            if (_colaRechazadas.Count > IdsRechazados) _rechazadas.Remove(_colaRechazadas.Dequeue());
        }
    }
}
