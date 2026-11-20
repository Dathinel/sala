using static Dathinel.Sala.Tests.Entorno;

namespace Dathinel.Sala.Tests;

/// <summary>El diagnóstico de §2.3 y la ficha que se lee por teléfono, sin red.</summary>
public sealed class DiagnosticoTests
{
    private static readonly DateTimeOffset Ahora = DateTimeOffset.FromUnixTimeSeconds(1_763_400_200);
    private const string Clave = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static Diagnostico Leer(string json, string rele = "n0", string version = "1.0.4", Caida? local = null) =>
        Diagnostico.Leer(Json(json), version, rele, esAnfitrion: false, reconexiones: 2, conexionMs: 850, ultimaReconexion: null, caidaLocal: local);

    private static readonly string PorRele = """
        {"clave":"{{Clave}}","direcciones":[],"rol":"invitado",
         "red":{"rele":"n0","releUrl":"https://euw1-1.relay.n0.iroh.link./","releConectado":true,"soloRele":true,"mdns":false},
         "pares":[{"clave":"{{Clave}}","alias":"Luis","via":"rele","rele":"https://euw1-1.relay.n0.iroh.link./",
           "rttMs":118,"perdidosPct":0.4,"paquetesEnviados":5120,"paquetesPerdidos":21,"enviadosB":1048576,"recibidosB":524288,
           "appVersion":"1.0.4","protocolo":1,"conectadoDesde":1763400000,"ultimaActividad":1763400198,"cambiosDeCamino":1}],
         "ultimaCaida":{"clave":"{{Clave}}","motivo":"red","en":1763399900}}
        """.Replace("{{Clave}}", Clave);

    [Fact]
    public void Lee_el_estado_completo()
    {
        Diagnostico d = Leer(PorRele);
        Assert.True(d.Red.SoloRele);
        Assert.True(d.Red.ReleConectado);
        Assert.Equal("https://euw1-1.relay.n0.iroh.link./", d.Red.ReleUrl);
        DiagnosticoPar p = Assert.Single(d.Pares);
        Assert.Equal(Camino.Rele, p.Camino);
        Assert.Equal(TimeSpan.FromMilliseconds(118), p.Rtt);
        Assert.Equal(0.4, p.PerdidaPct);
        Assert.Equal("1.0.4", p.AppVersion);
        Assert.Equal(1, p.Protocolo);
        Assert.Equal(1, p.CambiosDeCamino);
        Assert.Equal(1_048_576, p.EnviadosB);
        Assert.Equal("red", d.UltimaCaida?.Motivo);
        Assert.Equal(2, d.Reconexiones);
        Assert.Equal(850, d.ConexionMs);
    }

    [Fact]
    public void Lo_que_falta_o_viene_mal_queda_en_null_y_no_tumba_nada()
    {
        Diagnostico d = Leer("""{"clave":"CLAVE","red":{"releUrl":7},"pares":[{"clave":"CLAVE","via":"otro","rttMs":"x","perdidosPct":null,"ultimaActividad":-5},{"alias":"sin clave"},5],"ultimaCaida":{"motivo":"red"}}""".Replace("CLAVE", Clave));
        DiagnosticoPar p = Assert.Single(d.Pares);
        Assert.Equal(Camino.Desconocido, p.Camino);
        Assert.Null(p.Rtt);
        Assert.Null(p.PerdidaPct);
        Assert.Null(p.UltimaActividad);
        Assert.Null(d.Red.ReleUrl);
        Assert.False(d.Red.ReleConectado);
        Assert.Null(d.UltimaCaida);
        Assert.Empty(Leer("{}").Pares);
        Assert.Empty(Leer("[]").Pares);
    }

    [Fact]
    public void La_caida_local_gana_si_es_mas_reciente()
    {
        Caida mas = new(Clave, "cerro", DateTimeOffset.FromUnixTimeSeconds(1_763_400_100));
        Assert.Equal("cerro", Leer(PorRele, local: mas).UltimaCaida?.Motivo);
        Caida menos = new(Clave, "cerro", DateTimeOffset.FromUnixTimeSeconds(1_763_000_000));
        Assert.Equal("red", Leer(PorRele, local: menos).UltimaCaida?.Motivo);
    }

    [Fact]
    public void La_ficha_de_telefono_dice_camino_latencia_perdida_versiones_actividad_y_caida()
    {
        string ficha = Leer(PorRele).ParaTelefono(Ahora);
        Assert.Equal(
            "Camino: por relé (relé de n0)\nIda y vuelta: 118 ms\nPérdida: 0,4 %\nVersión: tuya 1.0.4 · de Luis 1.0.4 (iguales)\nActividad: hace 2 s\nÚltima caída: Se cortó la conexión de red, hace 5 min",
            ficha);
        Assert.True(ficha.Split('\n').Length <= 6);
    }

    [Fact]
    public void La_ficha_no_lleva_ni_ip_ni_claves_ni_rutas()
    {
        string json = PorRele.Replace("\"direcciones\":[]", "\"direcciones\":[\"203.0.113.9:4433\"]");
        string ficha = Leer(json, rele: "https://rele.ejemplo.org").ParaTelefono(Ahora);
        Assert.DoesNotContain("203.0.113", ficha);
        Assert.DoesNotContain(Clave, ficha);
        Assert.DoesNotContain("\\", ficha);
        string propio = json.Replace("https://euw1-1.relay.n0.iroh.link./", "https://rele.ejemplo.org/").Replace("\"rele\":\"n0\"", "\"rele\":\"https://rele.ejemplo.org\"");
        Assert.Contains("relé propio rele.ejemplo.org", Leer(propio, rele: "https://rele.ejemplo.org").ParaTelefono(Ahora));
    }

    [Fact]
    public void Directo_sin_medidas_y_sin_caida()
    {
        string ficha = Leer("""{"clave":"CLAVE","red":{"rele":"n0","releConectado":true},"pares":[{"clave":"CLAVE","alias":"","via":"directo","appVersion":"1.0.3"}]}""".Replace("CLAVE", Clave)).ParaTelefono(Ahora);
        Assert.StartsWith("Camino: directo\nIda y vuelta: sin medir\nPérdida: sin datos\nVersión: tuya 1.0.4 · de ", ficha);
        Assert.Contains("1.0.3 (distintas)", ficha);
        Assert.EndsWith("Actividad: ninguna todavía\nÚltima caída: ninguna", ficha);
    }

    [Fact]
    public void Sin_pares_avisa_si_el_rele_no_esta_conectado()
    {
        string ficha = Leer("""{"red":{"rele":"n0","releConectado":false},"pares":[]}""").ParaTelefono(Ahora);
        Assert.Contains("Camino: sin conexión con nadie", ficha);
        Assert.Contains("Relé: sin conectar", ficha);
        Assert.DoesNotContain("Relé: sin conectar", Leer("""{"red":{"rele":"ninguno"},"pares":[]}""", rele: "ninguno").ParaTelefono(Ahora));
    }

    [Theory]
    [InlineData("cerro")]
    [InlineData("red")]
    [InlineData("ritmo")]
    [InlineData("marco")]
    [InlineData("expulsado")]
    [InlineData("revocado")]
    [InlineData("anfitrion-cerro")]
    [InlineData("motivo-nuevo-que-no-existe")]
    public void Todo_motivo_tiene_una_frase_en_espanol_y_no_repite_uno_desconocido(string motivo)
    {
        string t = Textos.MotivoDeSalida(motivo);
        Assert.EndsWith(".", t);
        if (motivo.Contains("nuevo")) Assert.DoesNotContain(motivo, t);
    }

    [Fact]
    public void Los_textos_dicen_lo_que_prometen()
    {
        Assert.Contains("cifrado de extremo a extremo", Textos.Rele);
        Assert.Contains("nunca el contenido", Textos.Rele);
        Assert.Contains("dathinel-red", Textos.Cortafuegos);
        Assert.Contains("UDP", Textos.RedSinUdp);
        Assert.Contains("443", Textos.RedSinUdp);
        Assert.Contains("ms", Textos.ComoLeerElDiagnostico);
    }
}
