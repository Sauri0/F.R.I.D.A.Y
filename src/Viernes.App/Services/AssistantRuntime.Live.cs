using System.Text;
using Viernes.App.Diagnostics;
using Viernes.App.ViewModels;
using Viernes.Core.Configuration;
using Viernes.Core.Live;
using Viernes.Platform.Windows.Live;

namespace Viernes.App.Services;

/// <summary>
/// El camino en vivo: una sola conexión dúplex que reemplaza reconocer, pensar y sintetizar.
/// </summary>
/// <remarks>
/// Los dos caminos conviven y la frontera entre ellos es una sola línea, en
/// <see cref="StartConversationAsync"/>: al abrir una conversación se elige, se escribe por qué en la
/// bitácora, y a partir de ahí no se mezclan. Todo lo que hay acá adentro corre <em>en lugar de</em>
/// <c>RunConversationLoopAsync</c>, nunca además.
/// <para>
/// Lo que cambia no es sólo que tarda menos. Es que el micrófono queda abierto mientras ella habla,
/// así que hablarle encima la calla — y eso el camino de siempre no lo puede hacer por más que se lo
/// apure, porque ahí, mientras habla, no hay nadie escuchando.
/// </para>
/// <para>
/// <b>Hablando también tiene manos, pero menos.</b> El setup declara las tres herramientas de
/// <see cref="LiveToolBridge"/> —manejar la computadora, anotar un recordatorio, llevar una misión—
/// y el servidor las pide por <c>toolCall</c>; el resto del taller sigue siendo del camino escrito.
/// Esa diferencia está dicha en la instrucción de sistema y tiene que seguir estándolo: si acá se
/// agrega una herramienta y allá no se dice, la asistente sigue contestando que no puede.
/// </para>
/// </remarks>
internal sealed partial class AssistantRuntime
{
    /// <summary>
    /// La traba vive en el runtime y no en la sesión para que sobreviva a cerrar y abrir la charla.
    /// </summary>
    /// <remarks>
    /// Si se armara junto con la sesión, cada conversación arrancaría con la escalera en cero y el
    /// «apagado automático» duraría exactamente una conversación, que es no tenerlo.
    /// </remarks>
    private readonly LiveFallbackLatch _liveLatch = new();

    /// <summary>Lo que la persona viene diciendo en el turno abierto, según el servidor.</summary>
    private readonly StringBuilder _liveHeard = new();

    /// <summary>Lo que ella viene diciendo en el turno abierto.</summary>
    private readonly StringBuilder _liveSaid = new();

    /// <summary>
    /// El pedido de la persona, que puede venir partido en varios turnos del servidor.
    /// </summary>
    /// <remarks>
    /// Un turno del servidor no es un pedido: el servidor cierra el turno cuando junta el silencio
    /// configurado, así que una pausa para respirar —o una interrupción— parte en dos algo que la
    /// persona dijo de corrido. <see cref="LiveUtterance"/> los vuelve a juntar, y los suelta sola
    /// si la persona se fue en el medio.
    /// </remarks>
    private readonly LiveUtterance _liveUtterance = new();

    private readonly object _liveTextGate = new();

    private LiveSpeakerSink? _liveSink;
    private LiveVoiceSession? _liveSession;
    private LiveMicrophonePump? _liveMicrophone;

    /// <summary>Distinto de cero mientras la charla abierta va por el camino nuevo.</summary>
    private int _liveConversation;

    /// <summary>Si la conversación abierta está yendo por la sesión en vivo.</summary>
    internal bool IsLiveConversation => Volatile.Read(ref _liveConversation) != 0;

    /// <summary>
    /// Qué camino tomaría una conversación abierta ahora mismo. No abre nada.
    /// </summary>
    /// <remarks>
    /// Se puede llamar desde cualquier lado: es una consulta, no un intento. Sirve para escribir en
    /// la bitácora del arranque por dónde va a ir la primera charla, antes de que alguien hable.
    /// </remarks>
    internal VoiceRouteDecision DescribeVoiceRoute() => EnsureLiveSession().Decide();

    /// <summary>
    /// Arma la sesión en vivo la primera vez que hace falta y la reusa después.
    /// </summary>
    /// <remarks>
    /// Reusarla no es una optimización: la traba de caída y el contador de interrupciones son de la
    /// sesión que el usuario percibe como una sola —el asistente encendido—, no de cada charla.
    /// Rearmarla por conversación los borraría a los dos.
    /// </remarks>
    private LiveVoiceSession EnsureLiveSession()
    {
        if (_liveSession is not null)
        {
            return _liveSession;
        }

        _liveSink = new LiveSpeakerSink();

        // La instrucción se arma acá y no en Core porque lleva el nombre que eligió quien instaló, y
        // ese nombre vive en las preferencias — que a esta altura ya se leyeron.
        // La clave decide el valor por omisión del interruptor: pegarla en claves.json es la forma
        // en que el usuario pide la sesión hablada, y es el único gesto que se le pidió.
        var options = GeminiLiveOptions.FromEnvironment(
            ReadLiveSetting,
            BuildLiveInstruction(),
            hasKey: !string.IsNullOrWhiteSpace(LocalCredentials.Get("GOOGLE_API_KEY")));

        var session = new LiveVoiceSession(
            options,
            () => LocalCredentials.Get("GOOGLE_API_KEY"),
            _liveSink,
            transportFactory: null,
            _liveLatch,
            quietTimeout: null,
            // El orquestador va como función y no como valor: se rearma al conectar los servidores
            // MCP, y esta sesión se arma una sola vez y se reusa hasta que se apague el programa.
            new LiveToolBridge(() => _orchestrator));

        session.MomentChanged += LiveOnMomentChanged;
        session.TranscriptReceived += LiveOnTranscript;
        session.FellBack += LiveOnFellBack;
        session.Hiccup += LiveOnHiccup;
        session.WentQuiet += LiveOnWentQuiet;
        session.ToolActivity += LiveOnToolActivity;

        _liveSink.GapHeard += LiveOnGapHeard;

        _liveSession = session;
        return session;
    }

    /// <summary>
    /// De dónde salen los interruptores del camino nuevo.
    /// </summary>
    /// <remarks>
    /// Por el mismo lugar que la clave: primero <c>claves.json</c>, después el entorno. Pedirle al
    /// usuario que abra un archivo para pegar la clave y que además aprenda <c>setx</c> para prender
    /// el interruptor es pedirle dos cosas distintas para encender una sola.
    /// </remarks>
    private static string? ReadLiveSetting(string name) => LocalCredentials.Get(name);

    /// <summary>
    /// La instrucción de sistema de la sesión hablada.
    /// </summary>
    /// <remarks>
    /// No es el prompt del camino de siempre y no puede serlo: aquél está escrito alrededor de las
    /// treinta herramientas y acá hay tres. Copiarlo produciría un asistente que dice que leyó un
    /// archivo sin haber leído nada, que es la peor forma de fallar porque suena a que funcionó.
    /// <para>
    /// Qué manos tiene no se escribe acá: se pega desde <see cref="LiveToolBridge.Anuncio"/>, al
    /// lado de la lista que las declara. Escribirlo dos veces —una en la lista y otra en el texto—
    /// es garantizar que un día digan cosas distintas, y las dos formas de que se desincronicen
    /// terminan en una asistente que miente sobre sí misma.
    /// </para>
    /// </remarks>
    private string BuildLiveInstruction() => $"""
        Sos {_identity.Name}, el asistente personal de esta computadora, y esto es una conversación
        hablada: te escuchan, no te leen.

        Hablás en castellano rioplatense, de vos. Sereno, preciso y directo. Frases cortas: quien te
        escucha no puede volver atrás a releer.

        {LiveToolBridge.Anuncio}

        Te pueden interrumpir hablándote encima, y está bien: cuando pase, callate en el acto y no
        retomes la frase tuya que venías diciendo.

        Pero lo que te dicen al cortarte NO es un pedido nuevo: es la misma conversación siguiendo.
        Sumalo a lo que te venían diciendo antes y contestá UNA sola vez, teniendo en cuenta las dos
        partes. Lo mismo cuando una pausa te hizo creer que habían terminado y en realidad seguían
        hablando: es una sola cosa dicha en dos tramos. Nunca contestes dos veces seguidas, una por
        tramo, como si fueran dos personas distintas pidiéndote dos cosas.
        """;

    /// <summary>
    /// La instrucción de la sesión hablada, con lo que sabe del usuario y lo que le quedó
    /// preguntando.
    /// </summary>
    /// <remarks>
    /// La sesión hablada era <b>amnésica</b>, y eso no se notaba leyendo el código: la instrucción
    /// era una constante bien escrita y parecía completa. Pero el camino de siempre le arma al
    /// modelo, en cada turno, la memoria personal, las reglas enseñadas, los objetivos abiertos, las
    /// misiones y los permisos; el camino nuevo no le armaba nada. O sea que justo el camino que el
    /// usuario eligió como principal era el único donde ella no sabía quién era él.
    /// <para>
    /// Lo peor era la pregunta pendiente. Está construida para sobrevivir al reinicio y a los días
    /// —es la promesa central de las misiones— y hablando no existía: podía haberle preguntado algo
    /// ayer y no tener con qué retomarlo.
    /// </para>
    /// <para>
    /// Van estas dos y no las cinco del otro camino: las reglas enseñadas y los permisos hablan del
    /// taller entero —treinta herramientas—, y acá hay tres. Pagar en cada conexión los tokens de
    /// instrucciones sobre cosas que hablando no puede hacer es pagar por confundirla.
    /// </para>
    /// <para>
    /// La fecha va sí o sí, y no es adorno: sin ella «recordame el martes» se resuelve contra la
    /// fecha de corte del entrenamiento y el recordatorio queda guardado con un año que ya pasó, o
    /// sea que nunca vence. El camino escrito ya la manda en cada turno; acá va una vez por
    /// conexión, que es donde se puede.
    /// </para>
    /// </remarks>
    private async Task<string> BuildLiveInstructionAsync(CancellationToken cancellationToken)
    {
        var instruccion = new StringBuilder(BuildLiveInstruction());

        instruccion.AppendLine().AppendLine().Append(
            $"Esta conversación empieza el {DateTimeOffset.Now:dddd d 'de' MMMM 'de' yyyy, HH:mm}. " +
            "Usá esta fecha para resolver «mañana», «el martes», «en dos horas», y mandá siempre la " +
            "hora completa con zona horaria cuando anotes un recordatorio.");

        var personal = await SafeContextAsync(
            () => DescribePersonalMemoryAsync(cancellationToken)).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(personal))
        {
            instruccion.AppendLine().AppendLine().Append(personal);
        }

        var misiones = await SafeContextAsync(
            () => _missionBook.DescribeOpenAsync(cancellationToken)).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(misiones))
        {
            instruccion.AppendLine().AppendLine().Append(misiones);
            instruccion.AppendLine().AppendLine().Append(
                "Si hay una pregunta tuya sin contestar, retomala vos apenas venga al caso. No " +
                "esperes que se acuerde él. Cuando te conteste, anotalo en el acto con «mision» y " +
                "accion=«responder»: si no lo anotás, lo que te dijo se pierde al cerrar la charla " +
                "y la misión queda frenada esperando algo que ya te contestaron.");
        }

        return instruccion.ToString();
    }

    /// <summary>
    /// Trae un pedazo de contexto sin dejar que su falla impida abrir la charla.
    /// </summary>
    /// <remarks>
    /// Un archivo ilegible no puede ser la razón por la que el asistente no atiende. Se pierde el
    /// contexto —que se nota— y no la conversación —que se nota mucho más—.
    /// </remarks>
    private static async Task<string?> SafeContextAsync(Func<Task<string?>> leer)
    {
        try
        {
            return await leer().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RuntimeTrace.Write("vivo.contexto.excepcion", exception.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Intenta abrir la conversación por el camino nuevo. Devuelve si se hizo cargo.
    /// </summary>
    /// <remarks>
    /// Cuando devuelve <c>false</c> no dejó nada abierto: el llamador puede seguir con
    /// <c>RunConversationLoopAsync</c> sin limpiar nada. Es lo que hace que «se cae al camino de
    /// siempre» sea una línea y no una coreografía.
    /// </remarks>
    private async Task<bool> TryStartLiveConversationAsync(CancellationToken cancellationToken)
    {
        var session = EnsureLiveSession();

        var decision = session.Decide();
        RuntimeTrace.Write("voz.camino", decision.ToString());
        if (!decision.IsLive)
        {
            return false;
        }

        // El micrófono es de uno solo. El oído continuo lo suelta acá igual que para el bucle de
        // siempre, y la voz de siempre no puede estar sonando encima de la nueva.
        await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);
        CancelSpeechSafely();
        _neuralPlayer.Stop();
        await SilenceVoiceAsync(cancellationToken).ConfigureAwait(false);

        // El micrófono se arma ANTES de abrir el socket, y no es un detalle de orden.
        //
        // Acá adentro se carga el detector de voz, y eso tarda. Armándolo después, la sesión quedaba
        // abierta y aceptada mientras el modelo cargaba: la persona ya estaba hablando y ese tramo no
        // subía a ningún lado. Se perdían las primeras palabras de la primera frase de cada charla.
        // Ahora además recibe el detector que ya armó el oído continuo, así que no carga nada: era
        // una copia nueva por conversación, al lado de la que el oído ya tenía cargada.
        var microphone = new LiveMicrophonePump(session, _voiceDetector);
        RuntimeTrace.Write("vivo.microfono.detector", microphone.DetectorInfo.Name);

        // La instrucción se rearma en cada conversación, con lo que sabe del usuario y lo que le
        // quedó preguntando. Sin esto la sesión hablada era amnésica: ver BuildLiveInstructionAsync.
        session.UseSystemInstruction(
            await BuildLiveInstructionAsync(cancellationToken).ConfigureAwait(false));

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var opened = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        reloj.Stop();
        RuntimeTrace.Write("vivo.socket", $"{reloj.ElapsedMilliseconds} ms · {opened.Route}");
        if (!opened.IsLive)
        {
            RuntimeTrace.Write("vivo.no.abrio", opened.Reason);
            await microphone.DisposeAsync().ConfigureAwait(false);
            await session.StopAsync().ConfigureAwait(false);
            _liveSink?.Close();
            return false;
        }

        microphone.LevelChanged += LiveOnAudioLevel;
        microphone.PreRollReleased += LiveOnPreRollReleased;
        if (!microphone.Start())
        {
            microphone.LevelChanged -= LiveOnAudioLevel;
            microphone.PreRollReleased -= LiveOnPreRollReleased;
            RuntimeTrace.Write("vivo.microfono", microphone.LastFailure ?? "no abrió");
            await microphone.DisposeAsync().ConfigureAwait(false);
            await session.StopAsync().ConfigureAwait(false);
            _liveSink?.Close();
            return false;
        }

        _liveMicrophone = microphone;
        ClearLiveText();
        ClearDictation();
        Interlocked.Exchange(ref _liveConversation, 1);
        RuntimeTrace.Write("vivo.abierta");

        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Listening,
            "En vivo · hablame encima para cortarme",
            "Te escucho.",
            MicrophoneActive: true));

        return true;
    }

    /// <summary>
    /// Cierra el camino nuevo y deja los dispositivos libres.
    /// </summary>
    /// <remarks>
    /// Se puede llamar de más: si la charla no iba por acá, no hace nada. Lo llaman el cierre de
    /// conversación, el mute, el freno de emergencia y el apagado, y ninguno de ellos sabe —ni
    /// tiene por qué saber— por qué camino iba la charla.
    /// </remarks>
    private async Task StopLiveAsync(string reason)
    {
        if (Interlocked.Exchange(ref _liveConversation, 0) == 0)
        {
            return;
        }

        var microphone = _liveMicrophone;
        _liveMicrophone = null;

        if (microphone is not null)
        {
            microphone.LevelChanged -= LiveOnAudioLevel;
            microphone.PreRollReleased -= LiveOnPreRollReleased;

            // Queda escrito porque es lo único que dice, después, si la compuerta de eco hizo su
            // trabajo. Si «eco» es cero mientras ella habló, no está frenando nada y el bucle de
            // cortarse sola va a volver; si «encima» es cero después de haberle hablado encima, el
            // listón quedó alto y hay que mirar el perfil.
            var (eco, liston) = microphone.EchoProfile;
            RuntimeTrace.Write(
                "vivo.microfono.eco",
                $"eco={microphone.EchoBlocks} bloques · encima={microphone.Breakthroughs} · " +
                $"nivel={eco:0.000} listón={liston:0.000} · tarde={microphone.DroppedBlocks}" +
                (microphone.EchoGaveUp
                    ? " · SE RINDIÓ: el parlante nunca se calló, así que soltó el micrófono para no dejarte mudo"
                    : string.Empty));

            await microphone.DisposeAsync().ConfigureAwait(false);
        }

        if (_liveSession is not null)
        {
            await _liveSession.StopAsync().ConfigureAwait(false);
        }

        // El parlante se cierra y no se desecha: la sesión se reusa y la próxima charla lo vuelve a
        // abrir sola. Tener el dispositivo tomado entre charla y charla es tenerlo tomado todo el
        // día, y hay una sola tarjeta de sonido.
        _liveSink?.Close();
        ClearLiveText();
        ClearDictation();

        RuntimeTrace.Write("vivo.cerrada", reason);
    }

    /// <summary>
    /// Apaga el camino nuevo del todo: cierra la sesión, se desuscribe y suelta el parlante.
    /// </summary>
    /// <remarks>
    /// Desuscribirse no es prolijidad. La sesión dispara sus eventos desde el hilo que lee del
    /// servidor, y un manejador que sobrevive al cierre se encuentra con un runtime desechado
    /// publicando sobre un dispatcher apagado. Es la misma clase de fuga que este repositorio ya
    /// arregló en el reconocedor y en el oído continuo.
    /// </remarks>
    private async Task DisposeLiveAsync()
    {
        await StopLiveAsync("apagado").ConfigureAwait(false);

        var session = _liveSession;
        _liveSession = null;

        if (session is not null)
        {
            session.MomentChanged -= LiveOnMomentChanged;
            session.TranscriptReceived -= LiveOnTranscript;
            session.FellBack -= LiveOnFellBack;
            session.Hiccup -= LiveOnHiccup;
            session.WentQuiet -= LiveOnWentQuiet;
            session.ToolActivity -= LiveOnToolActivity;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        // El resumen del parlante se escribe ANTES de cerrarlo, y es lo primero que hay que mirar
        // cuando el usuario dice que se le cortó la voz: cero huecos con la charla entera hablada
        // significa que el corte no estuvo en la salida de audio y hay que buscarlo en otro lado.
        // El relleno va aparte de los huecos justamente porque no acusa a nadie: ahí adentro está
        // el final de cada frase y cada herramienta que tardó, que no son defectos.
        if (_liveSink is not null)
        {
            _liveSink.GapHeard -= LiveOnGapHeard;
            RuntimeTrace.Write(
                "vivo.parlante",
                $"huecos={_liveSink.Gaps} · silencio={_liveSink.GapTotal.TotalMilliseconds:0} ms · " +
                $"relleno={_liveSink.Filler.TotalMilliseconds:0} ms · callada={_liveSink.FlushCount} veces");
        }

        _liveSink?.Dispose();
        _liveSink = null;
    }

    /// <summary>Calla los parlantes de la sesión en vivo. Lo llama el freno del usuario.</summary>
    private void SilenceLive()
    {
        if (Volatile.Read(ref _liveConversation) == 0)
        {
            return;
        }

        _liveSession?.SilenceNow();
    }

    /// <summary>
    /// Cambió el momento de la charla en vivo.
    /// </summary>
    /// <remarks>
    /// Llega desde el hilo que lee del servidor o desde el del micrófono, así que acá no se espera
    /// nada: lo único que se hace es publicar —y publicar despacha con <c>InvokeAsync</c> del otro
    /// lado— y, si hay que cerrar la charla, se sale por una tarea. Esperar acá traba el bucle que
    /// trae el aviso de interrupción, que es justo el que no puede trabarse.
    /// </remarks>
    private void LiveOnMomentChanged(object? sender, LiveMomentChangedEventArgs eventArgs)
    {
        if (_isDisposed || Volatile.Read(ref _liveConversation) == 0)
        {
            return;
        }

        RuntimeTrace.Write("vivo.momento", $"{eventArgs.Previous} → {eventArgs.Current}");

        // Contestó entera y sin que la cortaran: es lo único que da por cerrado el pedido de la
        // persona. Volver a «te escucho» desde «pensando» o desde «te corté» no cierra nada, porque
        // en los dos casos lo que sigue es la misma frase. Ver LiveUtterance.ClosesUtterance.
        var cierra = LiveUtterance.ClosesUtterance(eventArgs.Previous, eventArgs.Current);
        if (cierra)
        {
            _liveUtterance.Close();
        }

        // Al salir de «te escucho» el tramo de la persona quedó cerrado; al volver, la respuesta.
        // Es el único momento en que una y otra se pueden leer enteras: llegan de a fragmentos.
        var heard = eventArgs.Current == LiveOrbMoment.Listening ? null : TakeLiveText(_liveHeard);
        var said = eventArgs.Current == LiveOrbMoment.Listening ? TakeLiveText(_liveSaid) : null;

        if (heard is not null && IsClosingPhrase(heard))
        {
            _ = Task.Run(() => CloseLiveConversationAsync(heard));
            return;
        }

        // El tramo se suma al pedido abierto en vez de nacer suelto.
        //
        // Un turno del servidor no es un pedido. El detector de voz del servidor cierra el turno
        // apenas junta el silencio configurado, así que una pausa para respirar parte en dos algo
        // que se dijo de corrido — y una interrupción hace lo mismo. Anotando cada tramo aparte, una
        // sola oración quedaba como dos turnos de la charla y la burbuja borraba la primera mitad
        // para escribir la segunda: la persona veía cortarse lo que estaba diciendo.
        string? whole = null;
        var vencio = false;
        if (heard is not null)
        {
            var sumado = _liveUtterance.Add(heard);
            whole = sumado.Text;
            vencio = sumado.Expired is not null;

            if (vencio)
            {
                // Sin lo que decía: sólo que se soltó y por qué. La bitácora se pega en un reporte.
                RuntimeTrace.Write("vivo.frase.vencida", $"tras={LiveUtterance.DefaultMaxGap}");
            }

            if (sumado.Continued)
            {
                // Sin la frase: la bitácora es un archivo de texto que se pega en un reporte. El
                // número de tramos alcanza para saber cuánto está cortando de más el servidor.
                RuntimeTrace.Write("vivo.frase.unida", $"tramos={_liveUtterance.Parts}");
                AmendLastConversationTurn(whole);
            }
            else
            {
                AddConversationTurn(whole);
            }
        }

        // Lo vencido se borra igual que lo contestado: en los dos casos lo que quedó escrito es de
        // otra frase, y dejarlo puesto hace que la nueva se lea pegada a la vieja.
        if (cierra || vencio)
        {
            // Vuelve el turno a la persona con lo anterior ya contestado: lo que quedaba escrito era
            // el pedido viejo. Dejarlo puesto hace que la frase nueva se escriba a continuación de
            // la vieja, y las dos juntas se leen como una sola. Mientras el pedido sigue abierto no
            // se borra, justamente porque ahí las dos mitades SÍ son una sola.
            ClearDictation();
        }

        Publish(new AssistantRuntimeUpdate(
            ToVisualState(eventArgs.Current),
            LiveStatusLabel(eventArgs.Current),
            whole ?? said,
            MicrophoneActive: true,
            // El tramo quedó cerrado: lo que estaba en itálica pasa a firme y se queda quieto
            // mientras contesta. Va la frase entera y no el tramo, que es lo que hace que la
            // continuación se vea agregarse en vez de reemplazar lo anterior.
            Dictation: whole is null ? null : _dictation.Settle(whole),
            DictationRecovered: _dictation.RecoveredSpan));
    }

    /// <summary>
    /// Reenvía el nivel del micrófono a la interfaz sin tocar el estado.
    /// </summary>
    /// <remarks>
    /// Llega cincuenta veces por segundo y viaja por el mismo canal que la captura de siempre, que
    /// del otro lado se atiende antes que nada y sale. Sin esto el orbe dibuja «te escucho» quieto
    /// durante toda la charla en vivo, y quieto no se distingue de colgado.
    /// </remarks>
    private void LiveOnAudioLevel(object? sender, Viernes.Platform.Windows.Speech.AudioLevelEventArgs eventArgs)
    {
        if (_isDisposed || Volatile.Read(ref _liveConversation) == 0)
        {
            return;
        }

        if (eventArgs.IsVoice)
        {
            // Entra audio: sorda deja de valer en este mismo cuadro, igual que en el otro camino.
            Volatile.Write(ref _deaf, 0);
        }

        // Va por Updated y no por Publish, igual que en el otro camino: Publish reescribe el último
        // estado publicado, y esto no es un cambio de estado sino el mismo estado moviéndose.
        Updated?.Invoke(this, new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            AudioLevel: eventArgs.Level));
    }

    /// <summary>
    /// Llegó un pedazo de transcripción.
    /// </summary>
    /// <remarks>
    /// Llega de a fragmentos y no de a frases, y ni siquiera de a palabras: un fragmento puede
    /// cortar una palabra al medio. Lo que dice la persona sale a la burbuja apenas llega —para eso
    /// está la palabra provisoria, que es exactamente la que se está formando— y lo que dice ella se
    /// acumula callado, porque su respuesta no es dictado: la arma entera
    /// <see cref="LiveOnMomentChanged"/> en el borde del turno.
    /// <para>
    /// Publicar cada fragmento no reescribe la burbuja entera: va por el canal del dictado, que no
    /// toca el estado del orbe ni el mensaje.
    /// </para>
    /// </remarks>
    private void LiveOnTranscript(object? sender, LiveTranscriptEventArgs eventArgs)
    {
        if (_isDisposed || Volatile.Read(ref _liveConversation) == 0)
        {
            return;
        }

        string heard;
        lock (_liveTextGate)
        {
            var target = eventArgs.Speaker == LiveSpeaker.User ? _liveHeard : _liveSaid;
            target.Append(eventArgs.Text);

            if (eventArgs.Speaker != LiveSpeaker.User)
            {
                return;
            }

            heard = _liveHeard.ToString();
        }

        PublishDictation(_dictation.Hear(heard));
    }

    /// <summary>
    /// Una herramienta de la sesión hablada arrancó o terminó.
    /// </summary>
    /// <remarks>
    /// Sólo escribe en la bitácora, y eso es todo lo que tiene que hacer. El orbe ya lo muestra por
    /// otro lado: la herramienta corre por dentro del orquestador, que pasa a «pensando» al empezar,
    /// y el usuario ve ese mismo gesto que ve cuando le escribe.
    /// <para>
    /// Lo que se escribe es el nombre y cómo salió, nunca los argumentos. En «abrí tal cosa» los
    /// argumentos son inocentes; en «anotá que el turno del médico es el jueves» no, y la bitácora
    /// es un archivo de texto que se pega en un reporte cuando algo falla.
    /// </para>
    /// </remarks>
    private void LiveOnToolActivity(object? sender, LiveToolEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        RuntimeTrace.Write(
            "vivo.herramienta",
            eventArgs.Finished
                ? $"{eventArgs.Name} · {(eventArgs.Succeeded ? "hecho" : "no pudo")}"
                : $"{eventArgs.Name} · arranca");
    }

    /// <summary>
    /// La sesión en vivo se murió: se sigue por el camino de siempre sin que el usuario haga nada.
    /// </summary>
    /// <remarks>
    /// No es <c>async void</c> aunque todo lo que hay adentro sea asincrónico. Un <c>async void</c>
    /// en un manejador de eventos ya tumbó el proceso una vez en este repositorio: la excepción no
    /// tiene a dónde ir. Acá el trabajo sale por una tarea con su propio <c>try</c>.
    /// </remarks>
    private void LiveOnFellBack(object? sender, LiveFailureEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        RuntimeTrace.Write("vivo.caida", eventArgs.Message);

        if (Volatile.Read(ref _liveConversation) == 0)
        {
            return;
        }

        var token = ConversationToken();

        _ = Task.Run(async () =>
        {
            try
            {
                await StopLiveAsync(eventArgs.Message).ConfigureAwait(false);

                if (_isDisposed || !_conversationActive)
                {
                    return;
                }

                // La charla no se corta: se muda. El usuario no tiene por qué enterarse de que un
                // servicio se cayó más allá de la línea de estado.
                Publish(new AssistantRuntimeUpdate(
                    AssistantVisualState.Listening,
                    "Sigo por el camino de siempre",
                    eventArgs.Message,
                    MicrophoneActive: true));

                await RunConversationLoopAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RuntimeTrace.Write("vivo.caida.excepcion", exception.GetType().Name);
            }
        });
    }

    /// <summary>
    /// El parlante se quedó sin voz que sonar en el medio de una respuesta.
    /// </summary>
    /// <remarks>
    /// <b>Este renglón es todo el punto.</b> El usuario reportó que «a veces se le corta la voz
    /// mientras habla» y no había en toda la bitácora una sola marca de eso: la salida rellena con
    /// silencio y sigue como si nada. Ahora queda escrito cuándo pasó, cuánto duró y de qué lado se
    /// quedó corto — la cola, que es la red llegando tarde, o el driver, que es esta máquina no
    /// dándole el hilo a tiempo. Se arreglan en lugares distintos, y sin esto no había forma de
    /// saber cuál de los dos arreglar.
    /// <para>
    /// Llega desde el hilo del driver de audio, así que lo único que se hace es encolar un renglón
    /// —que es lo que <see cref="RuntimeTrace"/> promete que cuesta nanosegundos—. Cualquier cosa
    /// que tarde acá se oye.
    /// </para>
    /// </remarks>
    private void LiveOnGapHeard(object? sender, LiveAudioGapEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        var gap = eventArgs.Gap;
        var lado = gap.Kind == LiveAudioGapKind.Queue
            ? "la cola quedó vacía (llegó tarde el audio del servidor)"
            : "el driver volvió tarde a buscar audio (es la máquina, no la red)";

        RuntimeTrace.Write(
            "vivo.parlante.hueco",
            $"{gap.Duration.TotalMilliseconds:0} ms a los {gap.SinceStart.TotalSeconds:0.00} s · {lado}");
    }

    /// <summary>
    /// Algo se rompió y la sesión se recompuso sola.
    /// </summary>
    /// <remarks>
    /// No cierra nada ni cambia el camino: sólo deja el renglón. <b>Un corte de transporte vacía la
    /// cola del parlante</b>, así que desde el cuarto se oye exactamente como que la voz se cortó a
    /// mitad de frase — y hasta ahora no dejaba rastro ninguno, con lo cual era indistinguible de
    /// que no hubiera pasado nada. Es el candidato más directo a lo que reportó el usuario.
    /// </remarks>
    private void LiveOnHiccup(object? sender, LiveFailureEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        RuntimeTrace.Write("vivo.hipo", eventArgs.Message);
    }

    /// <summary>
    /// Se soltó la antesala del micrófono de golpe.
    /// </summary>
    /// <remarks>
    /// Se anota para poder cruzarlo con los cortes de voz. La sospecha es que esta ráfaga —hasta
    /// trescientos milisegundos entrando de una— es lo que el servidor lee como una interrupción y
    /// por eso se calla sola; con la marca en la bitácora, esa sospecha se confirma o se tira abajo
    /// mirando si el corte cae al lado. Sin la marca no se puede hacer ninguna de las dos cosas, que
    /// es donde estábamos.
    /// </remarks>
    private void LiveOnPreRollReleased(object? sender, LivePreRollReleasedEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        RuntimeTrace.Write(
            "vivo.microfono.rafaga",
            $"{eventArgs.Blocks} bloques · {eventArgs.Duration.TotalMilliseconds:0} ms de una");
    }

    /// <summary>
    /// Pasó un minuto sin que nadie hable: se cierra la charla.
    /// </summary>
    /// <remarks>
    /// No es sólo prolijidad. Una sesión en vivo abierta manda audio del micrófono a la nube sin
    /// parar y se cobra por minuto: dejarla escuchando un cuarto vacío es dejar el micrófono
    /// transmitiendo hasta que alguien apague la máquina. El camino de siempre ya se cerraba solo
    /// cuando nadie contestaba; éste no tenía cómo.
    /// </remarks>
    private void LiveOnWentQuiet(object? sender, EventArgs eventArgs)
    {
        if (_isDisposed || Volatile.Read(ref _liveConversation) == 0)
        {
            return;
        }

        RuntimeTrace.Write("vivo.abandonada", "un minuto sin voz");
        _ = Task.Run(async () =>
        {
            try
            {
                var turns = TakeConversationTurns();
                await EndConversationAsync("Cerré por silencio", quiet: true, CancellationToken.None)
                    .ConfigureAwait(false);
                await LearnFromConversationAsync(turns).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RuntimeTrace.Write("vivo.abandonada.excepcion", exception.GetType().Name);
            }
        });
    }

    private async Task CloseLiveConversationAsync(string transcript)
    {
        try
        {
            // La frase NO se escribe. La traza es un archivo de texto plano que queda en el disco y
            // que se pega en un reporte cuando algo falla; lo que se dijo en voz alta adentro de la
            // casa no tiene por qué terminar ahí. Para depurar el cierre hablado alcanza con saber
            // que se reconoció uno y cuánto medía.
            RuntimeTrace.Write("vivo.cierre.hablado", $"palabras={transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length}");

            // Los turnos se retiran antes de cerrar, porque el cierre los descarta.
            var turns = TakeConversationTurns();
            await EndConversationAsync("Conversación cerrada", quiet: true, CancellationToken.None)
                .ConfigureAwait(false);
            await LearnFromConversationAsync(turns).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RuntimeTrace.Write("vivo.cierre.excepcion", exception.GetType().Name);
        }
    }

    /// <summary>El token de la charla abierta, sin explotar si el cierre ganó la carrera.</summary>
    private CancellationToken ConversationToken()
    {
        try
        {
            return _conversationCancellation?.Token ?? CancellationToken.None;
        }
        catch (ObjectDisposedException)
        {
            return CancellationToken.None;
        }
    }

    private string? TakeLiveText(StringBuilder buffer)
    {
        lock (_liveTextGate)
        {
            if (buffer.Length == 0)
            {
                return null;
            }

            var text = buffer.ToString().Trim();
            buffer.Clear();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    private void ClearLiveText()
    {
        lock (_liveTextGate)
        {
            _liveHeard.Clear();
            _liveSaid.Clear();
        }

        // El pedido abierto también. Lo llaman el arranque y el cierre de la conversación en vivo, y
        // un pedido a medio decir que sobreviva a un cierre se le pegaría adelante a la primera
        // frase de la charla siguiente.
        _liveUtterance.Reset();
    }

    /// <summary>
    /// Traduce el momento de la sesión al estado del orbe.
    /// </summary>
    /// <remarks>
    /// Es la única traducción que hace falta y por eso los momentos son cuatro y no quince: el resto
    /// de lo que dibuja el orbe —guardia, sin clave, sorda, un proyecto esperando— no es un momento
    /// de esta conversación sino una condición del asistente, y la sigue decidiendo
    /// <c>Resting()</c>.
    /// </remarks>
    private static AssistantVisualState ToVisualState(LiveOrbMoment moment) => moment switch
    {
        LiveOrbMoment.Thinking => AssistantVisualState.Thinking,
        LiveOrbMoment.Speaking => AssistantVisualState.Speaking,
        LiveOrbMoment.Interrupted => AssistantVisualState.Interrupted,
        _ => AssistantVisualState.Listening
    };

    private static string LiveStatusLabel(LiveOrbMoment moment) => moment switch
    {
        LiveOrbMoment.Thinking => "Pensando…",
        LiveOrbMoment.Speaking => "Hablando · hablame encima para cortarme",
        LiveOrbMoment.Interrupted => "Te escucho",
        _ => "En vivo · decime «listo» para cortar"
    };
}
