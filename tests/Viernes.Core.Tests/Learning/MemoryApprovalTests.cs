using System.Text.Json;
using Viernes.Core.Learning;
using Viernes.Core.Tools;
using Viernes.Memory;
using Viernes.Memory.Models;
using Viernes.Memory.Persistence;
using Xunit;

namespace Viernes.Core.Tests.Learning;

/// <summary>
/// Lo que se prueba acá es que lo destilado tenga salida. Antes, una observación temporal sólo
/// podía vencerse: el store sabía aprobar sugerencias pero nadie podía llegar hasta ahí.
/// </summary>
public sealed class MemoryApprovalTests : IDisposable
{
    private readonly string _directory;
    private readonly JsonPersonalMemoryStore _store;
    private readonly MemoryApprovals _approvals;

    public MemoryApprovalTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "Viernes.Core.Tests.Memory",
            Guid.NewGuid().ToString("N"));
        _store = new JsonPersonalMemoryStore(Path.Combine(_directory, "memory.json"));
        _approvals = new MemoryApprovals(_store);
    }

    [Fact]
    public async Task Una_observacion_temporal_se_puede_volver_permanente()
    {
        await ObserveAsync("prefiere reuniones a la mañana");

        var outcome = await _approvals.ApproveAsync("reuniones");

        Assert.True(outcome.Succeeded);
        var review = await _store.ReviewAsync();
        Assert.Single(review.Explicit);
        Assert.Equal("prefiere reuniones a la mañana", review.Explicit[0].Content);

        // La observación de origen se consume: si quedara, seguiría apareciendo como pendiente de
        // aprobar algo que ya está aprobado.
        Assert.Empty(review.TemporaryObservations);
        Assert.Empty(review.Suggestions);
    }

    [Fact]
    public async Task Lo_aprobado_sobrevive_a_que_venza_la_observacion()
    {
        await ObserveAsync("trabaja de noche");
        await _approvals.ApproveAsync("noche");

        var explicitItems = await _store.ListAsync(PersonalMemoryKind.Explicit);

        // Lo explícito no tiene vencimiento; era justo lo que le faltaba a lo destilado.
        Assert.Single(explicitItems);
    }

    [Fact]
    public async Task Se_puede_aprobar_por_identificador_corto()
    {
        await ObserveAsync("usa dos monitores");
        var pending = await _approvals.ListPendingAsync();

        var outcome = await _approvals.ApproveAsync(pending[0].ShortId);

        Assert.True(outcome.Succeeded);
        Assert.Single(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task Con_una_sola_pendiente_alcanza_con_decir_que_si()
    {
        await ObserveAsync("toma mate a la tarde");

        var outcome = await _approvals.ApproveAsync(reference: null);

        Assert.True(outcome.Succeeded);
        Assert.Single(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task Con_varias_pendientes_pregunta_cual_en_vez_de_elegir()
    {
        await ObserveAsync("usa dos monitores");
        await ObserveAsync("trabaja de noche");

        var outcome = await _approvals.ApproveAsync(reference: null);

        Assert.False(outcome.Succeeded);
        Assert.Empty(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task Una_referencia_ambigua_no_aprueba_nada()
    {
        await ObserveAsync("trabaja de noche los martes");
        await ObserveAsync("trabaja de noche los jueves");

        var outcome = await _approvals.ApproveAsync("trabaja de noche");

        Assert.False(outcome.Succeeded);
        Assert.Empty(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task Rechazar_saca_la_observacion_de_lo_pendiente()
    {
        await ObserveAsync("odia las reuniones");

        var outcome = await _approvals.RejectAsync("reuniones");

        Assert.True(outcome.Succeeded);
        Assert.Empty(await _approvals.ListPendingAsync());
        Assert.Empty(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task Una_sugerencia_ya_propuesta_se_aprueba_igual()
    {
        var observation = await ObserveAsync("prefiere que le hablen de vos");
        await _store.SuggestAsync("prefiere que le hablen de vos", observation.Id);

        var outcome = await _approvals.ApproveAsync("hablen de vos");

        Assert.True(outcome.Succeeded);
        Assert.Single(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task Sin_nada_pendiente_lo_dice_en_vez_de_romperse()
    {
        var outcome = await _approvals.ApproveAsync("cualquier cosa");

        Assert.False(outcome.Succeeded);
        Assert.Contains("pendiente", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lo_pendiente_se_menciona_avisando_que_no_esta_confirmado()
    {
        await ObserveAsync("usa Linux en el trabajo");

        var described = await _approvals.DescribePendingAsync();

        Assert.NotNull(described);
        Assert.Contains("usa Linux en el trabajo", described, StringComparison.Ordinal);
        Assert.Contains("NO confirmó", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task El_contexto_separa_lo_sabido_de_lo_supuesto()
    {
        await _store.AddExplicitAsync("se llama Bruno");
        await ObserveAsync("usa Linux en el trabajo");

        var described = await _approvals.DescribeForPromptAsync();

        Assert.NotNull(described);
        var knownAt = described.IndexOf("se llama Bruno", StringComparison.Ordinal);
        var guessedAt = described.IndexOf("usa Linux en el trabajo", StringComparison.Ordinal);
        Assert.True(knownAt >= 0 && guessedAt > knownAt);
        Assert.Contains("porque te lo pidió él", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task La_herramienta_lista_y_aprueba()
    {
        await ObserveAsync("prefiere las reuniones cortas");
        var tool = new MemoryTool(_approvals);

        var listed = await ExecuteAsync(tool, new { accion = "pendientes" });
        Assert.Equal(ToolExecutionStatus.Succeeded, listed.Status);
        Assert.Contains("reuniones cortas", listed.Message, StringComparison.Ordinal);

        var approved = await ExecuteAsync(tool, new { accion = "aprobar", cual = "reuniones cortas" });

        Assert.Equal(ToolExecutionStatus.Succeeded, approved.Status);
        Assert.Single(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    [Fact]
    public async Task La_herramienta_no_inventa_acciones()
    {
        var tool = new MemoryTool(_approvals);

        var result = await ExecuteAsync(tool, new { accion = "aprender_todo" });

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task El_comando_tipeado_aprueba_y_el_ajeno_no_lo_toca()
    {
        await ObserveAsync("escucha rock mientras programa");
        var commands = new MemoryCommands(_approvals);

        Assert.Null(await commands.TryExecuteAsync("/agenda"));

        var listed = await commands.TryExecuteAsync("/pendientes");
        Assert.NotNull(listed);
        Assert.Contains("rock", listed, StringComparison.Ordinal);

        var approved = await commands.TryExecuteAsync("/aprobar rock");

        Assert.NotNull(approved);
        Assert.Single(await _store.ListAsync(PersonalMemoryKind.Explicit));
    }

    private async Task<TemporaryObservation> ObserveAsync(string fact)
    {
        await _store.ResumeObservationAsync();
        var captured = await _store.ObserveAsync(fact, confidence: 0.7);
        Assert.NotNull(captured.Observation);
        return captured.Observation;
    }

    private static async Task<ToolExecutionResult> ExecuteAsync<T>(MemoryTool tool, T arguments) =>
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(arguments),
            new ToolExecutionContext("call-1"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
