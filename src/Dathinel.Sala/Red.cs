using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Modules.Protocol;

namespace Dathinel.Sala;

/// <summary>El proceso dathinel-red visto desde C#: una llamada por función de docs/api.md §2.</summary>
internal sealed class ProcesoRed : IAsyncDisposable
{
    private readonly ModuleProcess _proceso;
    // Las 4 llamadas a la vez del protocolo: se espera turno en vez de recibir «busy».
    private readonly SemaphoreSlim _turno = new(4);

    private ProcesoRed(ModuleProcess proceso) => _proceso = proceso;

    public static async Task<ProcesoRed> AbrirAsync(string ejecutable, CancellationToken ct)
    {
        try
        {
            return new ProcesoRed(await ModuleProcess.StartAsync(ejecutable, cancellationToken: ct).ConfigureAwait(false));
        }
        catch (ModuleException ex)
        {
            throw new SalaException("sin-red", "No se pudo abrir dathinel-red: " + ex.Message, ex);
        }
    }

    /// <summary>Llama a una función; un error de la función sale como <see cref="SalaException"/> con su código.</summary>
    public async Task<JsonElement> LlamarAsync(string funcion, JsonObject? parametros = null, CancellationToken ct = default)
    {
        JsonElement? p = parametros is null ? null : JsonSerializer.SerializeToElement(parametros, SalaJson.Default.JsonObject);
        await _turno.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ModuleResult r = await _proceso.Client.InvokeAsync(funcion, p, cancellationToken: ct).ConfigureAwait(false);
            return r.Value ?? default;
        }
        catch (ModuleCallException ex)
        {
            throw new SalaException(ex.Code, ex.Message, ex);
        }
        catch (ModuleException ex)
        {
            throw new SalaException("sin-red", "dathinel-red dejó de responder: " + ex.Message, ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new SalaException("sin-red", "dathinel-red ya está cerrado.", ex);
        }
        finally
        {
            _turno.Release();
        }
    }

    // _turno no se libera: sin AvailableWaitHandle no tiene nada que liberar y una llamada en vuelo aún lo suelta.
    public ValueTask DisposeAsync() => _proceso.DisposeAsync();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonArray))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonNode))]
internal sealed partial class SalaJson : System.Text.Json.Serialization.JsonSerializerContext;
