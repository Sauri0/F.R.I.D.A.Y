using System.Text;
using System.Windows;
using Viernes.Platform.Windows.Speech;
using Viernes.Platform.Windows.Speech.Recognition;
using Viernes.Platform.Windows.Speech.WakeWord;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Banco de pruebas del micrófono: medir en vez de adivinar.
/// </summary>
/// <remarks>
/// Todo lo que se arregló de voz en este proyecto se arregló mirando números —el umbral que se comía
/// la voz a los 4,5 s, la confianza 0,69 de un falso positivo, los 240 ms que perdían «sí»—, y cada
/// vez hubo que armar la medición a mano y tirarla después. Esto la deja armada.
/// <para>
/// Los tres botones responden las tres preguntas que importan, en orden: cuánto ruido tiene tu
/// cuarto, cuánto se despega tu voz de ese ruido, y cuántas veces de diez te reconoce el nombre. Sin
/// esas tres, cualquier ajuste de umbral es una corazonada.
/// </para>
/// </remarks>
public partial class MicrophoneLab : Window
{
    private readonly StringBuilder _log = new();
    private readonly List<double> _muestras = [];
    private readonly List<double> _detecciones = [];

    private WhisperSpeechRecognitionProvider? _recognition;
    private IWakeWordService? _wake;

    private double _piso;
    private double _umbral;
    private double _pico;
    private bool _midiendo;

    public MicrophoneLab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var opciones = new WhisperSpeechRecognitionOptions();
        DispositivoTexto.Text =
            $"Dispositivo {opciones.InputDeviceNumber} " +
            $"({(opciones.InputDeviceNumber == -1 ? "el predeterminado de Windows" : "fijado a mano")}) · " +
            $"modelo {System.IO.Path.GetFileName(opciones.ModelPath)}";

        Escribir("Listo. Empezá por el botón 1, en silencio.");

        _recognition = new WhisperSpeechRecognitionProvider(opciones);
        _recognition.AudioLevelChanged += OnLevel;

        var arranque = await _recognition.StartPushToTalkAsync(CancellationToken.None);
        if (!arranque.Succeeded)
        {
            Escribir($"⚠ No pude abrir el micrófono: {arranque.ErrorMessage}");
            Escribir("Sin micrófono no hay nada que medir. Revisá que ninguna otra aplicación lo tenga tomado.");
        }
    }

    private void OnLevel(object? sender, AudioLevelEventArgs e)
    {
        // BeginInvoke y no Invoke. Invoke es bloqueante: el hilo del audio queda esperando a que el
        // de la interfaz lo atienda, treinta y tres veces por segundo. Mientras un boton ocupa el
        // hilo de interfaz esperando a que termine una medicion, cada uno espera al otro y la
        // ventana se congela. Era exactamente eso: apretar el boton 1 trababa el programa.
        Dispatcher.BeginInvoke(() =>
        {
            var nivel = Math.Clamp(e.Level, 0, 1);
            BarraNivel.Width = nivel * 640;
            EstadoVoz.Text = e.IsVoice ? "SÍ" : "—";
            EstadoVoz.Foreground = e.IsVoice
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xD7, 0x9E))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x86, 0x95, 0xA2));

            if (nivel > _pico)
            {
                _pico = nivel;
                PicoTexto.Text = _pico.ToString("0.000");
            }

        });

        // La muestra se guarda en el hilo del audio, fuera del despacho a la interfaz. Antes se
        // guardaba adentro, asi que medir dependia de que la ventana llegara a atender cada buffer:
        // con la interfaz ocupada se perdian muestras, y con la interfaz trabada no llegaba ninguna
        // —que es como el boton 1 podia informar cero con el microfono entregando audio—.
        if (_midiendo)
        {
            lock (_muestras)
            {
                _muestras.Add(Math.Clamp(e.Level, 0, 1));
            }
        }
    }

    /// <summary>
    /// Mide el cuarto callado. Es la línea base contra la que se compara todo lo demás.
    /// </summary>
    private async void MedirRuido_Click(object sender, RoutedEventArgs e)
    {
        // Un handler async void que lanza cierra el proceso: la excepcion no tiene a donde
        // ir. Un banco de pruebas que se cierra al medir no sirve para medir nada, y ademas
        // esconde justo el dato que uno vino a buscar.
        try
        {
        BotonRuido.IsEnabled = false;
        Escribir("");
        Escribir("── 1 · SILENCIO ──");
        Escribir("No hables durante cinco segundos.");

        lock (_muestras)
        {
            _muestras.Clear();
        }

        _midiendo = true;

        // Se abre una captura de verdad en vez de escuchar el push-to-talk. El push-to-talk no
        // emite niveles hasta que arranca una sesión, así que el botón 1 medía cero mientras el 2
        // —que sí abre sesión— medía 259 muestras del mismo micrófono. No era el micrófono: era que
        // los dos botones medían cosas distintas.
        if (_recognition is not null)
        {
            await _recognition.RecognizeSingleUtteranceAsync(
                new SingleUtteranceRecognitionOptions
                {
                    // La duracion maxima tiene que ser MAYOR que los timeouts, no igual: la
                    // validacion del proveedor lo exige y tirarle tres cincos hacia que lanzara
                    // ArgumentException. Como el handler es async void, esa excepcion no la
                    // atrapaba nadie y cerraba la aplicacion entera.
                    InitialSilenceTimeout = TimeSpan.FromSeconds(5),
                    EndSilenceTimeout = TimeSpan.FromSeconds(2),
                    MaximumDuration = TimeSpan.FromSeconds(20)
                },
                CancellationToken.None);
        }

        _midiendo = false;

        double[] orden;
        lock (_muestras)
        {
            orden = _muestras.OrderBy(v => v).ToArray();
        }

        if (orden.Length == 0)
        {
            Escribir("⚠ No llegó una sola muestra. El micrófono no está entregando audio.");
            BotonRuido.IsEnabled = true;
            return;
        }
        _piso = orden[orden.Length / 2];
        var p90 = orden[(int)(orden.Length * 0.9)];
        // La fórmula del umbral vive en NoiseFloorTracker, que es internal en el otro ensamblado.
        // Se replica acá en vez de abrir la clase: un banco de pruebas no justifica ampliar la
        // superficie pública del proyecto, y si algún día divergen, este número deja de coincidir
        // con el real y la medición miente. Por eso queda dicho: es una copia, y hay que mirarla si
        // se toca el original.
        _umbral = Math.Max(_piso * 8, 0.012);

        PisoTexto.Text = _piso.ToString("0.000");
        UmbralTexto.Text = _umbral.ToString("0.000");
        MarcaUmbral.Margin = new Thickness(Math.Clamp(_umbral, 0, 1) * 640, 0, 0, 0);

        Escribir($"  muestras            {orden.Length}");
        Escribir($"  piso (mediana)      {_piso:0.0000}");
        Escribir($"  percentil 90        {p90:0.0000}");
        Escribir($"  máximo del silencio {orden[^1]:0.0000}");
        Escribir($"  umbral resultante   {_umbral:0.0000}");
        Escribir(_piso switch
        {
            > 0.05 => "  ⚠ Tu cuarto es MUY ruidoso. Con este piso, hablar bajo no la va a despertar.",
            > 0.02 => "  Cuarto con algo de ruido. Andable, pero el margen va a ser chico.",
            _ => "  Cuarto tranquilo. Buen punto de partida."
        });

            BotonRuido.IsEnabled = true;
        }
        catch (Exception exception)
        {
            Escribir($"⚠ Se rompio la medicion: {exception.GetType().Name} · {exception.Message}");
            BotonRuido.IsEnabled = true;
        }
    }

    /// <summary>
    /// Mide tu voz normal y la transcribe. El margen contra el piso es el número que decide todo.
    /// </summary>
    private async void Grabar_Click(object sender, RoutedEventArgs e)
    {
        // Un handler async void que lanza cierra el proceso: la excepcion no tiene a donde
        // ir. Un banco de pruebas que se cierra al medir no sirve para medir nada, y ademas
        // esconde justo el dato que uno vino a buscar.
        try
        {
        if (_recognition is null)
        {
            return;
        }

        BotonHablar.IsEnabled = false;
        Escribir("");
        Escribir("── 2 · TU VOZ ──");
        Escribir("Decí, con tu volumen normal: «Viernes, creame una carpeta en el escritorio».");

        lock (_muestras)
        {
            _muestras.Clear();
        }

        _pico = 0;
        _midiendo = true;

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await _recognition.RecognizeSingleUtteranceAsync(
            new SingleUtteranceRecognitionOptions
            {
                InitialSilenceTimeout = TimeSpan.FromSeconds(8),
                EndSilenceTimeout = TimeSpan.FromSeconds(1),
                MaximumDuration = TimeSpan.FromSeconds(15)
            },
            CancellationToken.None);
        reloj.Stop();
        _midiendo = false;

        double[] tomadas;
        lock (_muestras)
        {
            tomadas = [.. _muestras];
        }

        var conVoz = tomadas.Length == 0 ? 0 : tomadas.Count(v => v > _umbral);
        var medioHablando = tomadas.Where(v => v > _umbral).DefaultIfEmpty(0).Average();
        // Sin piso medido no hay margen que calcular, y mostrar 0,0× hacía parecer que el micrófono
        // estaba mudo cuando en realidad faltaba correr el botón 1.
        var margen = _piso <= 0 ? -1 : medioHablando / _piso;

        MargenTexto.Text = margen > 0 ? $"{margen:0.0}×" : "correr 1";

        Escribir($"  duración            {reloj.ElapsedMilliseconds} ms");
        Escribir($"  transcripción       «{resultado.Text}»");
        Escribir($"  estado              {(resultado.Succeeded ? "ok" : resultado.ErrorCode.ToString())}");
        Escribir($"  pico                {_pico:0.0000}");
        Escribir($"  nivel medio hablando {medioHablando:0.0000}");
        Escribir($"  buffers sobre umbral {conVoz} de {tomadas.Length}");
        if (margen < 0)
        {
            Escribir("  MARGEN               no calculado · corré primero el botón 1");
        }
        else
        {
            Escribir($"  MARGEN sobre el piso {margen:0.0}×");
            Escribir(margen switch
            {
                >= 6 => "  Margen holgado: el detector no debería perderte.",
                >= 3 => "  Margen justo. Palabras cortas pueden perderse.",
                _ => "  ⚠ Margen chico: el micrófono está lejos o su ganancia está baja."
            });
        }

        // La saturación es el problema opuesto y se confunde con «buena señal»: un pico clavado en
        // 1,0 significa que la onda se recortó arriba, y lo que se recorta no se puede transcribir.
        Escribir(_pico switch
        {
            >= 0.99 => "  ⚠ SATURA. El pico llega a 1,0: la onda se recorta y la transcripción empeora. " +
                       "Bajá la ganancia en Sonido → Entrada hasta que el pico quede cerca de 0,7.",
            >= 0.8 => "  Nivel alto, cerca de saturar. Bajá un poco la ganancia.",
            >= 0.25 => "  Nivel de entrada correcto.",
            _ => "  Nivel bajo. Subí la ganancia en Sonido → Entrada."
        });

            BotonHablar.IsEnabled = true;
        }
        catch (Exception exception)
        {
            Escribir($"⚠ Se rompio la medicion: {exception.GetType().Name} · {exception.Message}");
            BotonHablar.IsEnabled = true;
        }
    }

    /// <summary>
    /// Cuenta cuántas veces te reconoce el nombre y con cuánta confianza cada una.
    /// </summary>
    /// <remarks>
    /// Es la medición que faltaba. «No me escucha» puede ser que no dispare nunca o que dispare con
    /// confianza apenas por debajo del umbral, y son dos problemas distintos con arreglos distintos.
    /// </remarks>
    private async void ProbarWake_Click(object sender, RoutedEventArgs e)
    {
        // Un handler async void que lanza cierra el proceso: la excepcion no tiene a donde
        // ir. Un banco de pruebas que se cierra al medir no sirve para medir nada, y ademas
        // esconde justo el dato que uno vino a buscar.
        try
        {
        BotonWake.IsEnabled = false;
        Escribir("");
        Escribir("── 3 · LA PALABRA CLAVE ──");
        Escribir("Durante 30 segundos decí «Hola Viernes» unas diez veces, con pausas.");
        Escribir("Probá algunas normal, alguna más bajo y alguna desde lejos.");

        _detecciones.Clear();

        // El micrófono lo tiene la captura de arriba: se suelta para que SAPI pueda tomarlo.
        if (_recognition is not null)
        {
            await _recognition.CancelPushToTalkAsync(CancellationToken.None);
            await Task.Delay(500);
        }

        var settings = new Viernes.Platform.Windows.Storage.LocalSettingsStore();
        var cargadas = await settings.LoadAsync();
        var frases = cargadas.Settings.EffectiveWakePhrases;

        Escribir($"  frases activas      {string.Join(" · ", frases)}");

        _wake = new SapiWakeWordService(new WakeWordServiceOptions
        {
            Phrases = frases,
            RecognitionCulture = cargadas.Settings.RecognitionCulture,
            MinimumConfidence = 0.60f
        });

        _wake.WakeWordDetected += OnWake;
        var arranque = await _wake.StartAsync(CancellationToken.None);
        if (!arranque.Succeeded)
        {
            Escribir($"  ⚠ No arrancó: {arranque.ErrorMessage}");
            BotonWake.IsEnabled = true;
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30));
        await _wake.StopAsync(CancellationToken.None);
        _wake.WakeWordDetected -= OnWake;

        Escribir("");
        Escribir($"  detecciones         {_detecciones.Count}");
        if (_detecciones.Count > 0)
        {
            Escribir($"  confianza mínima    {_detecciones.Min():0.00}");
            Escribir($"  confianza media     {_detecciones.Average():0.00}");
            Escribir($"  confianza máxima    {_detecciones.Max():0.00}");
            Escribir($"  por debajo de 0,60  {_detecciones.Count(c => c < 0.60):0} (ésas se descartan hoy)");
        }

        Escribir(_detecciones.Count switch
        {
            0 => "  ⚠ Cero. O SAPI no tiene el idioma instalado, o el micrófono no le llega.",
            < 5 => "  ⚠ Menos de la mitad. El umbral o el reconocedor te están dejando afuera.",
            _ => "  Reconocimiento razonable."
        });

        // Se devuelve el micrófono a la captura, para poder seguir midiendo.
        if (_recognition is not null)
        {
            await _recognition.StartPushToTalkAsync(CancellationToken.None);
        }

            BotonWake.IsEnabled = true;
        }
        catch (Exception exception)
        {
            Escribir($"⚠ Se rompio la medicion: {exception.GetType().Name} · {exception.Message}");
            BotonWake.IsEnabled = true;
        }
    }

    private void OnWake(object? sender, WakeWordDetectedEventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            _detecciones.Add(e.Confidence);
            Escribir($"    detectada «{e.Phrase}» · confianza {e.Confidence:0.00}");
        });

    private void Informe_Click(object sender, RoutedEventArgs e)
    {
        var ruta = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"viernes-microfono-{DateTime.Now:yyyyMMdd-HHmm}.txt");

        System.IO.File.WriteAllText(ruta, _log.ToString());
        Escribir("");
        Escribir($"Informe guardado en {ruta}");
    }

    private void Escribir(string linea)
    {
        _log.AppendLine(linea);
        Bitacora.Text = _log.ToString();
        Scroll.ScrollToEnd();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_wake is not null)
        {
            await _wake.StopAsync(CancellationToken.None);
            await _wake.DisposeAsync();
        }

        if (_recognition is not null)
        {
            _recognition.AudioLevelChanged -= OnLevel;
            await _recognition.CancelPushToTalkAsync(CancellationToken.None);
            await _recognition.DisposeAsync();
        }
    }
}
