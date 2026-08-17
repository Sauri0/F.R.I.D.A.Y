using Viernes.Core.Awareness;
using Xunit;

namespace Viernes.Core.Tests.Awareness;

/// <summary>Reloj manual: el espaciado entre avisos se mide en minutos y no se puede esperar de verdad.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now = _now.Add(amount);
}

public sealed class SalienceGateTests
{
    private static Observation Grave(string key = "grave") =>
        new(key, "algo importante", Importance: 0.9, Urgency: 0.9, Confidence: 0.9, Actionability: 0.9);

    private static Observation Trivial() =>
        new("trivial", "algo menor", Importance: 0.3, Urgency: 0.2, Confidence: 0.9, Actionability: 0.3);

    [Fact]
    public void Choose_TrivialObservation_StaysSilent()
    {
        var gate = new SalienceGate();

        // Callarse es una función, no una falla: al tercer aviso irrelevante el usuario deja de
        // escuchar los que sí importan.
        Assert.Null(gate.Choose([Trivial()], userIsBusy: false));
    }

    [Fact]
    public void Choose_SameFactTwice_AnnouncesOnlyOnce()
    {
        var time = new ManualClock(new DateTimeOffset(2026, 8, 17, 4, 0, 0, TimeSpan.Zero));
        var gate = new SalienceGate(TimeSpan.FromMinutes(10), time);

        Assert.NotNull(gate.Choose([Grave()], userIsBusy: false));

        // Que siga siendo cierto no lo vuelve noticia otra vez, ni pasado el espaciado mínimo.
        time.Advance(TimeSpan.FromHours(2));
        Assert.Null(gate.Choose([Grave()], userIsBusy: false));
    }

    [Fact]
    public void Choose_WithinMinimumSpacing_HoldsTheSecondAnnouncement()
    {
        var time = new ManualClock(new DateTimeOffset(2026, 8, 17, 4, 0, 0, TimeSpan.Zero));
        var gate = new SalienceGate(TimeSpan.FromMinutes(10), time);

        Assert.NotNull(gate.Choose([Grave("uno")], userIsBusy: false));

        // Dos avisos seguidos son una conversación que el usuario no pidió.
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(gate.Choose([Grave("dos")], userIsBusy: false));

        time.Advance(TimeSpan.FromMinutes(8));
        Assert.NotNull(gate.Choose([Grave("dos")], userIsBusy: false));
    }

    [Fact]
    public void Choose_WhileUserIsBusy_RaisesTheBarInsteadOfBlocking()
    {
        // Un hecho intermedio: pasa con el usuario libre y no pasa con el usuario tecleando. Es lo
        // que hace que estar concentrado suba el costo de interrumpir en vez de ser un permiso.
        //
        // El producto tiene que caer en la ventana estrecha donde el costo decide: por encima de
        // 0,40 —umbral 0,25 más el costo de 0,15 estando libre— y hasta 0,70, que es lo máximo que
        // el costo de 0,45 todavía alcanza a bloquear. Acá da 0,551.
        var middling = new Observation(
            "medio", "algo intermedio",
            Importance: 0.9, Urgency: 0.85, Confidence: 0.9, Actionability: 0.8);

        Assert.NotNull(new SalienceGate().Choose([middling], userIsBusy: false));
        Assert.Null(new SalienceGate().Choose([middling], userIsBusy: true));
    }

    [Fact]
    public void Choose_ManyCandidates_PicksTheMostValuable()
    {
        var gate = new SalienceGate();

        var chosen = gate.Choose([Trivial(), Grave(), Trivial()], userIsBusy: false);

        Assert.Equal("grave", chosen?.Key);
    }

    [Fact]
    public void Value_WithZeroActionability_IsNeverWorthInterrupting()
    {
        // Multiplicar y no promediar: si no hay nada que hacer, no importa cuán urgente parezca.
        var useless = new Observation(
            "sin-accion", "no podés hacer nada",
            Importance: 1, Urgency: 1, Confidence: 1, Actionability: 0);

        Assert.True(useless.Value(interruptionCost: 0) <= 0);
        Assert.Null(new SalienceGate().Choose([useless], userIsBusy: false));
    }

    [Fact]
    public void Forget_LetsAFactBecomeNewsAgain()
    {
        var gate = new SalienceGate(TimeSpan.Zero);

        Assert.NotNull(gate.Choose([Grave()], userIsBusy: false));
        Assert.Null(gate.Choose([Grave()], userIsBusy: false));

        // La condición dejó de cumplirse y volvió: eso sí es noticia nueva.
        gate.Forget("grave");
        Assert.NotNull(gate.Choose([Grave()], userIsBusy: false));
    }
}
