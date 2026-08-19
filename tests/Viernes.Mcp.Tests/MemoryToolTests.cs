using Xunit;

namespace Viernes.Mcp.Tests;

/// <summary>
/// La memoria vista desde el conector, y sobre todo lo que el conector NO puede hacerle.
/// </summary>
public sealed class MemoryToolTests : IDisposable
{
    private readonly ConnectorHarness harness = new();

    public void Dispose() => this.harness.Dispose();

    [Fact]
    public async Task Proponer_DejaElDatoPendienteYNoLoDaPorCierto()
    {
        var reply = await this.harness.Connector.ProposeMemoryAsync("Toma el mate amargo");

        Assert.True(reply.Ok);

        // Ésta es la prueba de la frontera: propuesto sí, confirmado no. Si algún día el conector
        // pudiera aprobar, esto se pondría en rojo antes que nadie se entere por otro lado.
        var review = await this.harness.Memory.ReviewAsync();
        Assert.Empty(review.Explicit);
        Assert.Equal("Toma el mate amargo", Assert.Single(review.Suggestions).Content);
    }

    [Fact]
    public async Task Buscar_DistingueLoConfirmadoDeLoSupuesto()
    {
        await this.harness.Memory.AddExplicitAsync("Trabaja de noche");
        await this.harness.Connector.ProposeMemoryAsync("Trabaja escuchando cumbia");

        var reply = await this.harness.Connector.SearchMemoryAsync("Trabaja");

        Assert.True(reply.Ok);
        Assert.Contains("[confirmado] Trabaja de noche", reply.Text, StringComparison.Ordinal);
        Assert.Contains("[supuesto, propuesto] Trabaja escuchando cumbia", reply.Text, StringComparison.Ordinal);
        Assert.Contains("no lo des por cierto", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buscar_SinCoincidencias_EsUnaRespuestaYNoUnFallo()
    {
        await this.harness.Memory.AddExplicitAsync("Trabaja de noche");

        var reply = await this.harness.Connector.SearchMemoryAsync("bicicleta");

        Assert.True(reply.Ok);
        Assert.Contains("bicicleta", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proponer_LoQueLaMemoriaRechaza_SeInformaComoFallo()
    {
        // La memoria rechaza credenciales por su cuenta. El conector no la reimplementa: comprueba
        // que el rechazo llegue al otro lado como un fallo y no como un «listo, guardado».
        var reply = await this.harness.Connector.ProposeMemoryAsync(
            "La clave de la API es sk-proj-0123456789abcdefghijklmnopqrstuvwxyz");

        Assert.False(reply.Ok);
        Assert.Empty((await this.harness.Memory.ReviewAsync()).Suggestions);
    }
}
