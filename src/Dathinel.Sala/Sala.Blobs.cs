using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala;

// Blobs por huella en su canal aparte (docs/api.md §5.7): el proceso baja y comprueba; la sala vuelve a comprobar.
public sealed partial class Sala
{
    /// <summary>Descargas en curso por intento (lo da <c>blob-pedir</c>): un final tardío no completa otra descarga.</summary>
    private readonly Dictionary<long, TaskCompletionSource<string?>> _bajando = [];
    /// <summary>Finales que llegaron antes de que <see cref="PedirBlobAsync"/> apuntara su intento.</summary>
    private readonly Dictionary<long, string?> _finesTempranos = [];

    /// <summary>Bytes recibidos de una descarga en curso (huella, recibidos), como mucho 4 veces por segundo.</summary>
    public event Action<string, long>? BlobAvance;

    /// <summary>Ofrece archivos propios (dentro de la carpeta de sala) por su huella. No deben cambiar mientras estén ofrecidos.</summary>
    public Task OfrecerBlobsAsync(IEnumerable<(string Ruta, string Huella, long Tamano)> blobs, CancellationToken ct = default) =>
        _red.LlamarAsync("blob-ofrecer", new JsonObject
        {
            ["blobs"] = new JsonArray([.. blobs.Select(b => (JsonNode)new JsonObject { ["ruta"] = Path.GetFullPath(b.Ruta), ["huella"] = b.Huella, ["tamano"] = b.Tamano })]),
        }, ct);

    public Task RetirarBlobsAsync(IEnumerable<string> huellas, CancellationToken ct = default) =>
        _red.LlamarAsync("blob-retirar", new JsonObject { ["huellas"] = new JsonArray([.. huellas.Select(h => (JsonNode)h)]) }, ct);

    /// <summary>
    /// Baja de <paramref name="de"/> el blob <paramref name="huella"/> a <paramref name="destino"/> (dentro de la carpeta
    /// de sala). Un <paramref name="tamano"/> mayor que <paramref name="tope"/> se rechaza antes de abrir nada. Cancelar
    /// espera a que el proceso borre el temporal; cerrar la sala termina la espera con «cerrada».
    /// </summary>
    public async Task PedirBlobAsync(string de, string huella, long tamano, string destino, long tope, CancellationToken ct = default)
    {
        if (tamano < 0 || tamano > tope) throw new SalaException("demasiado-grande", $"El blob ({tamano} bytes) pasa del tope de {tope}.");
        string ruta = Path.GetFullPath(destino);
        using CancellationTokenSource vida = CancellationTokenSource.CreateLinkedTokenSource(ct, _fin.Token);
        JsonElement r = await _red.LlamarAsync("blob-pedir", new JsonObject { ["de"] = de, ["huella"] = huella, ["tamano"] = tamano, ["destino"] = ruta }, vida.Token).ConfigureAwait(false);
        long intento = r.GetProperty("intento").GetInt64();
        TaskCompletionSource<string?> fin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cerrojo)
        {
            if (_finesTempranos.Remove(intento, out string? temprano)) fin.TrySetResult(temprano);
            else if (_fin.IsCancellationRequested) fin.TrySetResult("cerrada");
            else _bajando[intento] = fin;
        }
        string? error;
        try
        {
            error = await fin.Task.WaitAsync(vida.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_fin.IsCancellationRequested)
        {
            // Cancelación cooperativa: el proceso borra el .parte y avisa con su blob-fin.
            await IntentarAsync(_red.LlamarAsync("blob-cancelar", new JsonObject { ["intento"] = intento }, CancellationToken.None)).ConfigureAwait(false);
            try
            {
                await fin.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new SalaException("cerrada", "La sala se cerró durante la descarga.");
        }
        finally
        {
            lock (_cerrojo) _bajando.Remove(intento);
        }
        if (error is not null) throw new SalaException(error, $"No se pudo bajar el blob: {error}.");
        // La segunda comprobación, en este lado: lo que se abre es lo que se pidió.
        if (new FileInfo(ruta).Length != tamano || HuellaDe(ruta) != huella)
        {
            File.Delete(ruta);
            throw new SalaException("huella", "El blob no coincide con su huella.");
        }
    }

    private void AtenderBlob(JsonElement ev)
    {
        string huella = ev.GetProperty("huella").GetString()!;
        if (ev.GetProperty("tipo").GetString() == "blob-avance")
        {
            BlobAvance?.Invoke(huella, ev.GetProperty("recibidos").GetInt64());
            return;
        }
        long intento = ev.GetProperty("intento").GetInt64();
        string? error = ev.GetProperty("ok").GetBoolean() ? null : Limites.Texto(ev, "error") ?? "red";
        TaskCompletionSource<string?>? fin;
        lock (_cerrojo)
        {
            if (!_bajando.Remove(intento, out fin))
            {
                _finesTempranos[intento] = error;
                // Solo los de una llamada que aún no volvió: unos pocos; se acota por si un intento nunca se reclama.
                if (_finesTempranos.Count > 64) _finesTempranos.Remove(_finesTempranos.Keys.Min());
            }
        }
        fin?.TrySetResult(error);
    }

    /// <summary>Al cerrar: ninguna descarga queda esperando para siempre.</summary>
    private void TerminarDescargas()
    {
        TaskCompletionSource<string?>[] todas;
        lock (_cerrojo)
        {
            todas = [.. _bajando.Values];
            _bajando.Clear();
        }
        foreach (TaskCompletionSource<string?> t in todas) t.TrySetResult("cerrada");
    }
}
