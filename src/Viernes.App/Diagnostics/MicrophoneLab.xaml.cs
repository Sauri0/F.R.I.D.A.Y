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
        Dispatcher.Invoke(() =>
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

            if (_midiendo)
            {
                _muestras.Add(nivel);
            }
        });
    }

    /// <summary>
    /// Mide el cuarto callado. Es la línea base contra la que se compara todo lo demás.
    /// </summary>
    private async void MedirRuido_Click(object sender, RoutedEventArgs e)
    {
        BotonRuido.IsEnabled = false;
        Escribir("");
        Escribir("── 1 · SILENCIO ──");
        Escribir("No hables durante cinco segundos.");

        _muestras.Clear();
        _midiendo = true;
        await Task.Delay(TimeSpan.FromSeconds(5));
        _midiendo = false;

        if (_muestras.Count == 0)
        {
            Escribir("⚠ No llegó una sola muestra. El micrófono no está entregando audio.");
            BotonRuido.IsEnabled = true;
            return;
        }

        var orden = _muestras.OrderBy(v => v).ToArray();
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

        Escribir($"  muestras            {_muestras.Count}");
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

    /// <summary>
    /// Mide tu voz normal y la transcribe. El margen contra el piso es el número que decide todo.
    /// </summary>
    private async void Grabar_Click(object sender, RoutedEventArgs e)
    {
        if (_recognition is null)
        {
            return;
        }

        BotonHablar.IsEnabled = false;
        Escribir("");
        Escribir("── 2 · TU VOZ ──");
        Escribir("Decí, con tu volumen normal: «Viernes, creame una carpeta en el escritorio».");

        _muestras.Clear();
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

        var conVoz = _muestras.Count == 0 ? 0 : _muestras.Count(v => v > _umbral);
        var medioHablando = _muestras.Where(v => v > _umbral).DefaultIfEmpty(0).Average();
        var margen = _piso <= 0 ? 0 : medioHablando / _piso;

        MargenTexto.Text = margen > 0 ? $"{margen:0.0}×" : "—";

        Escribir($"  duración            {reloj.ElapsedMilliseconds} ms");
        Escribir($"  transcripción       «{resultado.Text}»");
        Escribir($"  estado              {(resultado.Succeeded ? "ok" : resultado.ErrorCode.ToString())}");
        Escribir($"  pico                {_pico:0.0000}");
        Escribir($"  nivel medio hablando {medioHablando:0.0000}");
        Escribir($"  buffers sobre umbral {conVoz} de {_muestras.Count}");
        Escribir($"  MARGEN sobre el piso {margen:0.0}×");
        Escribir(margen switch
        {
            >= 6 => "  Margen holgado: el detector no debería perderte.",
            >= 3 => "  Margen justo. Palabras cortas pueden perderse.",
            _ => "  ⚠ Margen insuficiente. O el micrófono está lejos, o su ganancia está baja: subila en Sonido → Entrada."
        });

        BotonHablar.IsEnabled = true;
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
