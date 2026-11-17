using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala;

// Blobs por huella en su canal aparte (docs/api.md §5.7): el proceso baja y comprueba; la sala vuelve a comprobar.
public sealed partial class Sala
{
    private readonly Dictionary<string, TaskCompletionSource<string?>> _bajando = new(StringComparer.Ordinal);

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
    /// de sala). Un <paramref name="tamano"/> mayor que <paramref name="tope"/> se rechaza antes de abrir nada.
    /// </summary>
    public async Task PedirBlobAsync(string de, string huella, long tamano, string destino, long tope, CancellationToken ct = default)
    {
        if (tamano < 0 || tamano > tope) throw new SalaException("demasiado-grande", $"El blob ({tamano} bytes) pasa del tope de {tope}.");
        TaskCompletionSource<string?> fin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cerrojo)
        {
            if (!_bajando.TryAdd(huella, fin)) throw new SalaException("ocupado", "Ese blob ya se está bajando.");
        }
        try
        {
            string ruta = Path.GetFullPath(destino);
            await _red.LlamarAsync("blob-pedir", new JsonObject { ["de"] = de, ["huella"] = huella, ["tamano"] = tamano, ["destino"] = ruta }, ct).ConfigureAwait(false);
            string? error;
            try
            {
                error = await fin.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await IntentarAsync(_red.LlamarAsync("blob-cancelar", new JsonObject { ["huella"] = huella }, CancellationToken.None)).ConfigureAwait(false);
                throw;
            }
            if (error is not null) throw new SalaException(error, $"No se pudo bajar el blob: {error}.");
            // La segunda comprobación, en este lado: lo que se abre es lo que se pidió.
            if (new FileInfo(ruta).Length != tamano || HuellaDe(ruta) != huella)
            {
                File.Delete(ruta);
                throw new SalaException("huella", "El blob no coincide con su huella.");
            }
        }
        finally
        {
            lock (_cerrojo) _bajando.Remove(huella);
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
        TaskCompletionSource<string?>? fin;
        lock (_cerrojo) _bajando.TryGetValue(huella, out fin);
        fin?.TrySetResult(ev.GetProperty("ok").GetBoolean() ? null : ev.GetProperty("error").GetString() ?? "red");
    }
}
