using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Recorre los dos finales que tiene un turno: el que llega hasta el final y el que la cortan.
/// </summary>
public sealed class LiveTurnMachineTests
{
    private static LiveServerEvent Audio(int bytes = 4) =>
        new() { Audio = [new byte[bytes]] };

    private static LiveServerEvent Flag(bool interrupted = false, bool generationComplete = false, bool turnComplete = false) =>
        new() { Interrupted = interrupted, GenerationComplete = generationComplete, TurnComplete = turnComplete };

    [Fact]
    public void ArrancaEnReposo()
    {
        Assert.Equal(LiveTurnState.Idle, new LiveTurnMachine().State);
    }

    [Fact]
    public void TurnoCompleto_ReposoRespondiendoDrenandoReposo()
    {
        var machine = new LiveTurnMachine();

        Assert.Equal(LiveTurnState.Responding, machine.Apply(Audio()).Current);
        Assert.Equal(LiveTurnState.Draining, machine.Apply(Flag(generationComplete: true)).Current);

        var fin = machine.Apply(Flag(turnComplete: true));
        Assert.Equal(LiveTurnState.Idle, fin.Current);
        Assert.True(fin.TurnEnded);
        Assert.Equal(1, machine.CompletedTurns);
        Assert.Equal(0, machine.InterruptionCount);
    }

    [Fact]
    public void Interrupcion_MandaAVaciarLaColaEnElActo()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());

        var transition = machine.Apply(Flag(interrupted: true));

        Assert.True(transition.FlushPlayback);
        Assert.Equal(LiveTurnState.Interrupted, transition.Current);
        Assert.Equal(1, machine.InterruptionCount);
    }

    [Fact]
    public void DespuesDeInterrumpir_NoLlegaGenerationCompleteYElTurnoCierraIgual()
    {
        // Es el orden real: audio → interrupted → turnComplete, sin generado en el medio. Esperar
        // el generado antes del cierre cuelga la máquina justo cuando la persona la cortó.
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());
        machine.Apply(Flag(interrupted: true));

        var fin = machine.Apply(Flag(turnComplete: true));

        Assert.True(fin.TurnEnded);
        Assert.Equal(LiveTurnState.Idle, machine.State);
        Assert.Equal(1, machine.CompletedTurns);
    }

    [Fact]
    public void SeLaPuedeCortarMientrasTerminaDeSonarLoQueYaGenero()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());
        machine.Apply(Flag(generationComplete: true));
        Assert.Equal(LiveTurnState.Draining, machine.State);

        var transition = machine.Apply(Flag(interrupted: true));

        // Lo que queda sonando es tan interrumpible como lo que se estaba generando.
        Assert.True(transition.FlushPlayback);
        Assert.Equal(LiveTurnState.Interrupted, transition.Current);
    }

    [Fact]
    public void AudioQueLlegaDespuesDeLaInterrupcion_NoReabreLaRespuesta()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());
        machine.Apply(Flag(interrupted: true));

        var transition = machine.Apply(Audio());

        // Lo que ya salió del servidor sigue viajando. Volver a Respondiendo devolvería a los
        // parlantes justo el pedazo que la persona mandó a callar.
        Assert.Equal(LiveTurnState.Interrupted, transition.Current);
        Assert.False(transition.FlushPlayback);
    }

    [Fact]
    public void TurnoQueCierraSinGenerado_TambienVuelveAReposo()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());

        Assert.Equal(LiveTurnState.Idle, machine.Apply(Flag(turnComplete: true)).Current);
    }

    [Fact]
    public void AudioEInterrupcionEnElMismoMensaje_MandanAVaciar()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());

        var transition = machine.Apply(new LiveServerEvent { Audio = [new byte[8]], Interrupted = true });

        Assert.True(transition.FlushPlayback);
        Assert.Equal(LiveTurnState.Interrupted, transition.Current);
    }

    [Fact]
    public void UnaTranscripcionDeSalidaSola_YaCuentaComoQueEstaRespondiendo()
    {
        var machine = new LiveTurnMachine();

        var transition = machine.Apply(new LiveServerEvent { OutputTranscript = "dale" });

        Assert.Equal(LiveTurnState.Responding, transition.Current);
        Assert.True(transition.Changed);
    }

    [Fact]
    public void Reset_VuelveAReposoSinBorrarLosContadores()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());
        machine.Apply(Flag(interrupted: true));
        machine.Apply(Flag(turnComplete: true));

        machine.Reset();

        // Reconectar por un goAway es un detalle del transporte: la charla que el usuario percibe
        // es la misma y sus contadores también.
        Assert.Equal(LiveTurnState.Idle, machine.State);
        Assert.Equal(1, machine.InterruptionCount);
        Assert.Equal(1, machine.CompletedTurns);
    }

    [Fact]
    public void UnMensajeVacio_NoCambiaNada()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());

        var transition = machine.Apply(LiveServerEvent.Empty);

        Assert.False(transition.Changed);
        Assert.False(transition.FlushPlayback);
        Assert.False(transition.TurnEnded);
    }
}
