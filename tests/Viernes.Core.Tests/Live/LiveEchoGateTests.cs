using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// La compuerta que separa su propio eco de alguien hablándole encima.
/// </summary>
/// <remarks>
/// Es la prueba del bug que dejó la sesión cortándose sola quince veces en cuarenta y ocho segundos,
/// así que las tres primeras son las tres que importan: callada sube todo, hablando el eco no sube, y
/// hablando alguien encima sí. El resto cubre los bordes con los que ya se tropezó una vez —la cola
/// del parlante, el arranque sin medición, la frase que no se puede partir al medio—.
/// <para>
/// Los niveles no son inventados: salen del perfil que dejó escrito el banco de medición
/// (<c>--medir</c>) en el equipo del usuario. Ruido del cuarto 0,11; voz de la persona 0,89 con picos
/// que saturan en 1,0. El eco se pone en 0,30, que es esa misma voz unos quince decibeles más abajo
/// después de salir por el parlante y cruzar el cuarto.
/// </para>
/// </remarks>
public sealed class LiveEchoGateTests
{
    private static readonly TimeSpan Bloque = TimeSpan.FromMilliseconds(20);

    /// <summary>El cuarto callado, tal como lo mide el detector.</summary>
    private const double Ambiente = 0.11;

    /// <summary>Su propia voz volviendo por el micrófono.</summary>
    private const double Eco = 0.30;

    /// <summary>La persona hablándole al micrófono de cerca.</summary>
    private const double Persona = 0.89;

    /// <summary>
    /// Deja la compuerta con el eco ya medido, que es como está el 99 % del tiempo.
    /// </summary>
    /// <remarks>
    /// Arranca pesimista a propósito —ver <c>SeedLevel</c>—, así que sin este rodaje toda prueba
    /// estaría midiendo el primer segundo de la primera respuesta y no el caso normal. Un segundo de
    /// eco alcanza: la referencia baja a la mitad cada 700 ms.
    /// </remarks>
    private static LiveEchoGate Medida(double eco = Eco)
    {
        var gate = new LiveEchoGate();
        for (var i = 0; i < 100; i++)
        {
            gate.Decide(speakerAudible: true, isVoice: true, eco, Bloque);
        }

        return gate;
    }

    [Fact]
    public void ConLaAsistenteCallada_SubeSiempre()
    {
        // El caso normal: la charla entera menos los tramos en que ella habla. Acá la compuerta no
        // puede existir, ni siquiera para el ruido del cuarto.
        var gate = new LiveEchoGate();

        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: false, isVoice: true, Persona, Bloque));

        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: false, isVoice: false, Ambiente, Bloque));
    }

    [Fact]
    public void HablandoElla_ElEcoNoSube()
    {
        // El bug entero, en una prueba: su propia voz, que el detector ve como voz porque lo es,
        // entrando mientras el parlante suena. Si esto sube, el servidor manda «interrupted» y la
        // conversación se corta sola.
        var gate = Medida();

        for (var i = 0; i < 250; i++)
        {
            Assert.Equal(
                LiveMicrophoneVerdict.Hold,
                gate.Decide(speakerAudible: true, isVoice: true, Eco, Bloque));
        }
    }

    [Fact]
    public void HablandoElla_AlguienEncimaSiSube()
    {
        // La otra mitad del trato, y la razón por la que no alcanza con cerrar el micrófono: la
        // interrupción tiene que seguir andando.
        var gate = Medida();

        var veredicto = LiveMicrophoneVerdict.Hold;
        for (var i = 0; i < 10 && veredicto == LiveMicrophoneVerdict.Hold; i++)
        {
            veredicto = gate.Decide(speakerAudible: true, isVoice: true, Persona, Bloque);
        }

        Assert.Equal(LiveMicrophoneVerdict.Release, veredicto);
        Assert.Equal(1, gate.Breakthroughs);
    }

    [Fact]
    public void LaInterrupcionSeConfirmaEnMenosDeCienMilisegundos()
    {
        // El número importa: es lo que tarda en empezar a subir la frase de quien interrumpe, y la
        // antesala de la bomba devuelve el audio de ese tramo. Más de cien milisegundos acá y el
        // arranque de la palabra ya no entra en la antesala.
        var gate = Medida();

        var bloques = 0;
        while (gate.Decide(speakerAudible: true, isVoice: true, Persona, Bloque) != LiveMicrophoneVerdict.Release)
        {
            bloques++;
            Assert.True(bloques < 10, "la compuerta no dejó pasar a nadie");
        }

        Assert.True(
            (bloques + 1) * Bloque.TotalMilliseconds <= 100,
            $"tardó {(bloques + 1) * Bloque.TotalMilliseconds} ms en confirmar la interrupción");
    }

    [Fact]
    public void UnGolpeFuerteNoAbreLaCompuerta()
    {
        // Un portazo pasa el listón de nivel de sobra, pero no es voz. Que el detector local siga
        // opinando es lo que evita que cualquier ruido fuerte la calle.
        var gate = Medida();
        var liston = gate.Bar;

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(
                LiveMicrophoneVerdict.Hold,
                gate.Decide(speakerAudible: true, isVoice: false, level: 1.0, Bloque));
        }

        // Y tampoco puede dejar el listón trepado: si el golpe subiera la referencia, durante el
        // segundo siguiente habría que gritarle para cortarla.
        Assert.True(gate.Bar <= liston, $"el golpe subió el listón de {liston:0.000} a {gate.Bar:0.000}");
    }

    [Fact]
    public void UnPicoSueltoDelEcoNoAbreLaCompuerta()
    {
        // El caso que obligó a pedir voz sostenida y no un bloque: el arranque de una sílaba suya
        // salta varios decibeles en veinte milisegundos y puede cruzar el listón antes de que la
        // referencia lo alcance. Lo que no puede es sostenerse.
        var gate = Medida();

        for (var i = 0; i < 60; i++)
        {
            var pico = i % 6 == 0 ? Persona : Eco;
            Assert.Equal(
                LiveMicrophoneVerdict.Hold,
                gate.Decide(speakerAudible: true, isVoice: true, pico, Bloque));
        }
    }

    [Fact]
    public void UnEcoQueSubeDeGolpeNoAbreLaCompuerta()
    {
        // Ella arranca a hablar fuerte después de una pausa: el nivel salta del ruido del cuarto al
        // eco pleno en un bloque. Es el mismo salto que hace una persona, y lo que los separa es
        // adónde llegan — la referencia sube de golpe con el primer bloque que no cruza.
        var gate = Medida();

        for (var vuelta = 0; vuelta < 10; vuelta++)
        {
            for (var i = 0; i < 15; i++)
            {
                gate.Decide(speakerAudible: true, isVoice: false, Ambiente, Bloque);
            }

            for (var i = 0; i < 25; i++)
            {
                Assert.Equal(
                    LiveMicrophoneVerdict.Hold,
                    gate.Decide(speakerAudible: true, isVoice: true, Eco, Bloque));
            }
        }
    }

    [Fact]
    public void DesdeElPrimerBloque_ElEcoNoSube()
    {
        // Sin rodaje: la primera respuesta de la primera charla, con la compuerta todavía sin medir
        // nada. Es el único momento en que no hay medición, y es justo el momento en que el bug se
        // manifestaba. Por eso arranca pesimista.
        var gate = new LiveEchoGate();

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(
                LiveMicrophoneVerdict.Hold,
                gate.Decide(speakerAudible: true, isVoice: true, Eco, Bloque));
        }
    }

    [Fact]
    public void DesdeElPrimerBloque_LaInterrupcionSigueAndando()
    {
        // Y el arranque pesimista no puede costar la interrupción: el listón inicial tiene que
        // quedar por debajo del nivel al que habla una persona.
        var gate = new LiveEchoGate();

        var veredicto = LiveMicrophoneVerdict.Hold;
        for (var i = 0; i < 10 && veredicto == LiveMicrophoneVerdict.Hold; i++)
        {
            veredicto = gate.Decide(speakerAudible: true, isVoice: true, Persona, Bloque);
        }

        Assert.Equal(LiveMicrophoneVerdict.Release, veredicto);
    }

    [Fact]
    public void UnaVezAbierta_NoVuelveAPedirElListon()
    {
        // Una frase no se puede partir al medio. Si cada bloque tuviera que cruzar el listón, las
        // consonantes quedarían afuera y al servidor le llegaría un picadillo.
        var gate = Medida();

        LiveMicrophoneVerdict veredicto;
        do
        {
            veredicto = gate.Decide(speakerAudible: true, isVoice: true, Persona, Bloque);
        }
        while (veredicto == LiveMicrophoneVerdict.Hold);

        // El resto de la frase: bajo, con pausas, y hasta por debajo del eco.
        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: true, isVoice: false, Ambiente, Bloque));

        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: true, isVoice: true, 0.2, Bloque));
    }

    [Fact]
    public void LaColaDelParlanteSigueFrenada()
    {
        // El eco no termina cuando el parlante se calla: falta la propagación por el cuarto, la
        // latencia del driver y el búfer de captura. Es la misma cola que la sesión ya tuvo que
        // reconocer con sus 180 ms.
        var gate = Medida();
        gate.Decide(speakerAudible: true, isVoice: true, Eco, Bloque);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(
                LiveMicrophoneVerdict.Hold,
                gate.Decide(speakerAudible: false, isVoice: true, Eco, Bloque));
        }
    }

    [Fact]
    public void PasadaLaCola_VuelveASubirTodo()
    {
        var gate = Medida();
        gate.Decide(speakerAudible: true, isVoice: true, Eco, Bloque);

        for (var i = 0; i < 11; i++)
        {
            gate.Decide(speakerAudible: false, isVoice: false, Ambiente, Bloque);
        }

        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: false, isVoice: true, Eco, Bloque));
    }

    [Fact]
    public void ConUnParlanteFuerte_ElListonNoSeVuelveInalcanzable()
    {
        // El caso feo: el parlante fuerte y el micrófono cerca, con el eco llegando casi tan alto
        // como una persona. La referencia se instala arriba con un eco que crece de a poco, que es
        // como llega ahí sin abrir la compuerta en el camino.
        var gate = new LiveEchoGate();
        gate.Decide(speakerAudible: true, isVoice: true, level: 0.50, Bloque);
        gate.Decide(speakerAudible: true, isVoice: true, level: 0.80, Bloque);

        // Sin tope, el listón habría quedado en 1,36: por encima de lo que el conversor puede
        // entregar, o sea una interrupción imposible. Y una interrupción imposible no se puede
        // notar desde afuera — se ve igual que si no te escuchara—, así que se prefiere el error
        // contrario: que se cuele algo de eco. Cuando el listón toca el tope, esta compuerta ya no
        // alcanza y hace falta cancelación de verdad.
        Assert.True(gate.EchoLevel >= 0.80);
        Assert.True(gate.Bar <= 0.85, $"el listón quedó en {gate.Bar:0.000}, fuera del alcance de una voz");

        var veredicto = LiveMicrophoneVerdict.Hold;
        for (var i = 0; i < 10 && veredicto == LiveMicrophoneVerdict.Hold; i++)
        {
            veredicto = gate.Decide(speakerAudible: true, isVoice: true, Persona, Bloque);
        }

        Assert.Equal(LiveMicrophoneVerdict.Release, veredicto);
    }

    [Fact]
    public void UnNivelRotoNoCierraElMicrofono()
    {
        // Si el detector devuelve cualquier cosa, el lado seguro es el de siempre: subir. Un
        // micrófono que se cierra por una falla del detector se ve igual que un asistente que no
        // escucha, y nadie puede saber por qué.
        var gate = Medida();

        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: true, isVoice: true, double.NaN, Bloque));

        Assert.Equal(
            LiveMicrophoneVerdict.Send,
            gate.Decide(speakerAudible: true, isVoice: true, -1, Bloque));
    }

    [Fact]
    public void ElListonSaleDeLaMedicion_YNoDeUnaConstante()
    {
        // Lo que hace que esto ande en un equipo que no es el del programador: cuánto se atenúa el
        // eco depende del volumen, del cuarto y de dónde está el micrófono. Con un eco flojo el
        // listón baja, y con eso baja cuánto hay que levantar la voz para cortarla.
        var flojo = Medida(eco: 0.15);
        var fuerte = Medida(eco: 0.45);

        Assert.True(flojo.Bar < fuerte.Bar);
        Assert.True(flojo.Bar > 0.15, "el listón tiene que quedar por encima del eco que midió");
        Assert.True(flojo.EchoLevel < fuerte.EchoLevel);
    }

    [Fact]
    public void AlReiniciar_VuelveAArrancarPesimista()
    {
        // Entre una charla y otra el usuario puede haber movido el volumen o cambiado de auriculares
        // a parlantes. Una medición vieja aplicada a un cuarto nuevo es peor que ninguna.
        var gate = Medida(eco: 0.15);
        var medido = gate.Bar;

        gate.Reset();

        Assert.True(gate.Bar > medido);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(
                LiveMicrophoneVerdict.Hold,
                gate.Decide(speakerAudible: true, isVoice: true, Eco, Bloque));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void UnBloqueQueNoDuraNadaSeRechaza(int milisegundos)
    {
        // El cero también, y no es prolijidad: todo lo que puede volver a abrir el micrófono se mide
        // sumando duraciones, así que con bloques de cero la compuerta se quedaría cerrada para
        // siempre y desde afuera se vería como que dejó de escuchar.
        var gate = new LiveEchoGate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            gate.Decide(speakerAudible: true, isVoice: true, Eco, TimeSpan.FromMilliseconds(milisegundos)));
    }
}
