using System.Text.Json;
using System.Text.Json.Nodes;
using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>Límites compartidos con Rust (pruebas/conformidad/limites.json), historial acotado y gramática de reservas.</summary>
public sealed class LimitesTests
{
    private static JsonElement Vectores()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, "pruebas", "conformidad", "limites.json");
            if (File.Exists(f)) return JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone();
        }
        throw new InvalidOperationException("No está pruebas/conformidad/limites.json.");
    }

    [Fact]
    public void Los_limites_de_CSharp_son_los_de_los_vectores_compartidos_con_Rust()
    {
        JsonElement v = Vectores();
        Assert.Equal(Limites.ProfundidadMarco, v.GetProperty("profundidadMarco").GetInt32());
        Assert.Equal(Limites.ProfundidadCarga, v.GetProperty("profundidadCarga").GetInt32());
        Assert.Equal(Sala.MaxCarga, v.GetProperty("cargaMaxBytes").GetInt32());
        Assert.Equal(Sala.MaxPresencia, v.GetProperty("presenciaMaxBytes").GetInt32());
        Assert.Equal(v.GetProperty("profundidadLocal").GetInt32(), Limites.ProfundidadMarco + v.GetProperty("envoltorioLocal").GetInt32());
        foreach (JsonElement caso in v.GetProperty("casos").EnumerateArray())
        {
            string json = caso.GetProperty("json").GetString()!;
            // Los sustitutos sueltos los corta el proceso de Rust: a este lado solo llega UTF-8 válido.
            if (json.Contains("\\ud8", StringComparison.Ordinal)) continue;
            bool valido;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                valido = doc.RootElement.ValueKind == JsonValueKind.Object && Limites.Profundidad(doc.RootElement) <= Limites.ProfundidadMarco;
            }
            catch (JsonException)
            {
                valido = false;
            }
            Assert.True(caso.GetProperty("marco").GetBoolean() == valido, caso.GetProperty("nombre").GetString());
        }
    }

    [Fact]
    public async Task Una_carga_mas_honda_de_lo_que_cabe_en_la_tuberia_no_sale_del_cliente()
    {
        await using Sala anf = await AnfitrionAsync();
        string honda = "{\"a\":" + new string('[', Limites.ProfundidadCarga) + new string(']', Limites.ProfundidadCarga) + "}";
        await Assert.ThrowsAsync<ArgumentException>(() => anf.EnviarAsync(Json(honda)));
        string justa = "{\"a\":" + new string('[', Limites.ProfundidadCarga - 1) + new string(']', Limites.ProfundidadCarga - 1) + "}";
        TaskCompletionSource<Ordenado> llego = new();
        anf.Ordenada += o => llego.TrySetResult(o);
        await anf.EnviarAsync(Json(justa));
        Assert.Equal(1, (await llego.Task.WaitAsync(TimeSpan.FromSeconds(20))).Seq);
    }

    [Fact]
    public void La_gramatica_de_reservas_no_admite_un_salto_de_linea_al_final()
    {
        Assert.False(Sala.ClaveDeReservaValida("pista:a\n"));
        Assert.True(Sala.ClaveDeReservaValida("pista:a"));
    }

    private static JsonObject Orden(long seq, int bytes) => new()
    {
        ["type"] = "orden", ["epoca"] = 1, ["seq"] = seq, ["id"] = Guid.NewGuid().ToString(), ["autor"] = new string('a', 64),
        ["datos"] = new JsonObject { ["x"] = new string('a', bytes) },
    };

    [Fact]
    public void El_historial_cuenta_ordenes_y_bytes_y_lo_que_sale_de_memoria_se_lee_del_respaldo()
    {
        string carpeta = Carpeta();
        using (Historial h = new(carpeta))
        {
            for (long s = 1; s <= Historial.MaxOrdenes + 10; s++) h.Agregar(s, Orden(s, 10));
            Assert.Equal(Historial.MaxOrdenes, h.Count);
            Assert.Equal(11, h.MasViejo);
            Assert.Equal(3, h.Buscar(3)!["seq"]!.GetValue<long>());
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(carpeta, ".sala")));

        using Historial g = new(Carpeta());
        for (long s = 1; s <= 100; s++) g.Agregar(s, Orden(s, 900 * 1024));
        Assert.True(g.Bytes <= Historial.MaxBytes, $"{g.Bytes}");
        Assert.True(g.Count < 100);
        Assert.Equal(1, g.Buscar(1)!["seq"]!.GetValue<long>());
    }

    [Fact]
    public void El_recuerdo_de_aceptadas_guarda_el_seq_y_no_la_respuesta()
    {
        Sala.Recuerdo r = new();
        Guid id = Guid.NewGuid();
        r.Aceptada(id, 42);
        Assert.Equal((42L, (JsonObject?)null), r.Buscar(id));
        for (int i = 0; i < 9000; i++) r.Aceptada(Guid.NewGuid(), i);
        Assert.Null(r.Buscar(id).Seq);
    }

    [Fact]
    public async Task Hospedar_con_mas_anclados_de_los_que_caben_falla_antes_de_abrir()
    {
        Miembro[] anclados = [.. Enumerable.Range(0, 3).Select(i => new Miembro(new string((char)('a' + i), 64), "M" + i, Rol.Editar))];
        await Assert.ThrowsAsync<ArgumentException>(() => Sala.HospedarAsync(Identidad.Nueva(), Opciones("Ana") with { MaxMiembros = 3 }, anclados, TodoVale));
    }
}
