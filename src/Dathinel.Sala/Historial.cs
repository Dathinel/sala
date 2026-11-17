using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dathinel.Sala;

/// <summary>
/// Los últimos <c>orden</c> en memoria (4096 o 64 MiB, docs/api.md §5.3) para reponer a quien vuelve. Los que salen de
/// la memoria pasan a un respaldo en disco (<see cref="Respaldo"/>) para contestar igual a una carga repetida.
/// </summary>
internal sealed class Historial(string carpeta) : IDisposable
{
    public const int MaxOrdenes = 4096;
    public const long MaxBytes = 64L << 20;

    private readonly LinkedList<(long Seq, JsonObject Orden, long Bytes)> _lista = new();
    private readonly Dictionary<long, JsonObject> _porSeq = [];
    private readonly Respaldo _respaldo = new(carpeta);
    private long _bytes;

    public int Count => _lista.Count;
    public long Bytes => _bytes;
    public long? MasViejo => _lista.First?.Value.Seq;
    public long? MasNuevo => _lista.Last?.Value.Seq;

    public static long Medir(JsonNode orden) => JsonSerializer.SerializeToUtf8Bytes(orden, SalaJson.Default.JsonNode).Length;

    /// <summary>Añade al final (seq creciente) y saca a disco lo que pase de los topes.</summary>
    public void Agregar(long seq, JsonObject orden)
    {
        long b = Medir(orden);
        _lista.AddLast((seq, orden, b));
        _porSeq[seq] = orden;
        _bytes += b;
        while (_lista.Count > MaxOrdenes || (_bytes > MaxBytes && _lista.Count > 1))
        {
            (long s, JsonObject o, long ob) = _lista.First!.Value;
            _lista.RemoveFirst();
            _porSeq.Remove(s);
            _bytes -= ob;
            _respaldo.Guardar(s, o);
        }
    }

    /// <summary>El <c>orden</c> de <paramref name="seq"/>, de memoria o del respaldo; null si ya no se guarda.</summary>
    public JsonObject? Buscar(long seq) => _porSeq.TryGetValue(seq, out JsonObject? o) ? o : _respaldo.Leer(seq);

    public bool Tiene(long seq) => _porSeq.ContainsKey(seq);

    public IEnumerable<JsonObject> Desde(long seq) => _lista.Where(x => x.Seq > seq).Select(x => x.Orden);

    public IEnumerable<JsonObject> Todo => _lista.Select(x => x.Orden);

    public void Vaciar()
    {
        _lista.Clear();
        _porSeq.Clear();
        _bytes = 0;
    }

    public void Dispose() => _respaldo.Dispose();
}

/// <summary>
/// Respaldo en disco de los <c>orden</c> que salieron de la memoria, para la deduplicación. Retención explícita: dos
/// archivos que rotan, como mucho 1 GiB y 131 072 órdenes en total (16 miembros × 8192 ids); al cerrar la sala se borran.
/// </summary>
internal sealed class Respaldo(string carpeta) : IDisposable
{
    public const long MaxBytes = 1L << 30;
    public const int MaxOrdenes = 16 * 8192;

    private sealed class Archivo(string ruta)
    {
        public string Ruta { get; } = ruta;
        public FileStream? Flujo { get; set; }
        public List<long> Seqs { get; } = [];
        public long Bytes { get; set; }
    }

    private readonly Dictionary<long, (Archivo Archivo, long Offset, int Largo)> _indice = [];
    private Archivo? _actual, _anterior;
    private bool _roto;

    public void Guardar(long seq, JsonObject orden)
    {
        if (_roto) return;
        try
        {
            if (_actual is null || _actual.Bytes > MaxBytes / 2 || _actual.Seqs.Count >= MaxOrdenes / 2) Rotar();
            byte[] b = Encoding.UTF8.GetBytes(orden.ToJsonString() + "\n");
            long offset = _actual!.Flujo!.Position;
            _actual.Flujo.Write(b);
            _actual.Flujo.Flush();
            _actual.Bytes += b.Length;
            _actual.Seqs.Add(seq);
            _indice[seq] = (_actual, offset, b.Length - 1);
        }
        catch (IOException)
        {
            // Sin disco la sala sigue: un repetido muy viejo recibe «ya-ordenada» en vez de su orden.
            _roto = true;
        }
        catch (UnauthorizedAccessException)
        {
            _roto = true;
        }
    }

    public JsonObject? Leer(long seq)
    {
        if (!_indice.TryGetValue(seq, out var e)) return null;
        try
        {
            using FileStream f = new(e.Archivo.Ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Position = e.Offset;
            byte[] b = new byte[e.Largo];
            f.ReadExactly(b);
            return JsonNode.Parse(b) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Rotar()
    {
        if (_anterior is not null) Borrar(_anterior);
        _anterior = _actual;
        _anterior?.Flujo?.Dispose();
        if (_anterior is not null) _anterior.Flujo = null;
        string dir = Path.Combine(Path.GetFullPath(carpeta), ".sala");
        Directory.CreateDirectory(dir);
        _actual = new Archivo(Path.Combine(dir, $"respaldo-{Guid.NewGuid():N}.jsonl"));
        _actual.Flujo = new FileStream(_actual.Ruta, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    private void Borrar(Archivo a)
    {
        foreach (long s in a.Seqs) _indice.Remove(s);
        a.Flujo?.Dispose();
        try
        {
            File.Delete(a.Ruta);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (_anterior is not null) Borrar(_anterior);
        if (_actual is not null) Borrar(_actual);
        _anterior = _actual = null;
    }
}
