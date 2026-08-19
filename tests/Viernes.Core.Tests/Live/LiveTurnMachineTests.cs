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

    [Fact]
    public void CortarlaDeEsteLado_DescartaElAudioQueYaVenia()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());

        Assert.True(machine.InterruptLocally());
        Assert.Equal(LiveTurnState.Interrupted, machine.State);

        // Acá está el punto: el servidor sigue mandando lo que ya despachó. Si esto volviera a
        // Responding, el parlante arrancaría de nuevo y la voz se cortaría un instante y seguiría,
        // que es exactamente lo que pasaba cuando cortar era sólo vaciar la cola.
        machine.Apply(Audio());
        Assert.Equal(LiveTurnState.Interrupted, machine.State);
    }

    [Fact]
    public void CortarlaMientrasVacia_TambienCuenta()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());
        machine.Apply(Flag(generationComplete: true));
        Assert.Equal(LiveTurnState.Draining, machine.State);

        // El servidor terminó de generar pero los parlantes siguen sonando: es el tramo donde más
        // se la corta, porque es cuando ya se entendió lo que iba a decir.
        Assert.True(machine.InterruptLocally());
        Assert.Equal(LiveTurnState.Interrupted, machine.State);
    }

    [Fact]
    public void CortarlaSinNadaSonando_NoHaceNada()
    {
        var machine = new LiveTurnMachine();

        Assert.False(machine.InterruptLocally());
        Assert.Equal(LiveTurnState.Idle, machine.State);
        Assert.Equal(0, machine.InterruptionCount);

        // Y el turno siguiente arranca sano. Si interrumpir en reposo dejara la máquina en
        // Interrupted, el próximo turno —el que la persona acaba de pedir— nacería descartando su
        // propio audio y se quedaría muda sin que nada fallara.
        machine.Apply(Audio());
        Assert.Equal(LiveTurnState.Responding, machine.State);
    }

    [Fact]
    public void ElServidorCierraElTurnoIgual_DespuesDeCortarlaDeEsteLado()
    {
        var machine = new LiveTurnMachine();
        machine.Apply(Audio());
        machine.InterruptLocally();

        // Cortar de este lado no le avisa al servidor: el turno lo sigue cerrando él, y ahí la
        // máquina vuelve a reposo sola. Sin esto quedaría descartando audio para siempre.
        machine.Apply(Flag(turnComplete: true));

        Assert.Equal(LiveTurnState.Idle, machine.State);
        Assert.Equal(1, machine.CompletedTurns);
        Assert.Equal(1, machine.InterruptionCount);
    }
}
