using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Corre el modelo Silero sobre ONNX Runtime sin que el proyecto dependa de ONNX Runtime.
/// </summary>
/// <remarks>
/// Todo pasa por reflexión a propósito. ONNX Runtime son unos 120 MB de binarios nativos para todas
/// las plataformas; meterlo como paquete obligatorio le suma eso al instalador de un asistente que
/// arranca con Windows, y se lo cobra también a quien nunca active el detector entrenado. Con
/// reflexión, el proyecto compila y corre sin nada instalado, y quien quiera el modelo deja tres
/// archivos en una carpeta.
/// <para>
/// Es la misma decisión que ya se había tomado con los modelos de Whisper: no se descargan solos, no
/// se empaquetan, los pone el usuario. Acá se agrega que tampoco se referencia el runtime.
/// </para>
/// <para>
/// De todas las formas de hablarle a ONNX Runtime se eligió la única que no usa <c>Span</c>:
/// <c>OrtValue.CreateTensorValueFromMemory</c> envuelve arreglos propios y la variante de
/// <c>Run</c> que recibe las salidas ya reservadas las escribe adentro de esos mismos arreglos. La
/// reflexión no puede pasar ni devolver un <c>ref struct</c>, así que el camino habitual —armar un
/// <c>DenseTensor</c> con un <c>ReadOnlySpan&lt;int&gt;</c> de dimensiones— es directamente
/// imposible desde acá. Comprobado a los golpes: la primera versión usaba un constructor que ni
/// siquiera existe en el paquete.
/// </para>
/// <para>
/// Como los tensores envuelven arreglos que viven mientras viva esta instancia, se arman una sola
/// vez en el constructor y cada llamada no reserva nada: se copia la ventana adentro del arreglo de
/// entrada, se corre y se lee el resultado del arreglo de salida.
/// </para>
/// </remarks>
public sealed class OnnxVadModelRunner : IVadModelRunner
{
    private const string RuntimeAssemblyName = "Microsoft.ML.OnnxRuntime";
    private const string RuntimeAssemblyFile = "Microsoft.ML.OnnxRuntime.dll";
    private const string NativeRuntimeFile = "onnxruntime.dll";
    private static int _nativeResolverInstalled;

    private readonly IDisposable _session;
    private readonly IDisposable? _sessionOptions;
    private readonly IDisposable _runOptions;
    private readonly List<IDisposable> _values = [];
    private readonly MethodInfo _run;
    private readonly object _inputNames;
    private readonly object _inputValues;
    private readonly object _outputNames;
    private readonly object _outputValues;
    private readonly float[] _audio;
    private readonly float[] _probability = new float[1];
    private readonly float[][] _stateIn;
    private readonly float[][] _stateOut;
    private bool _disposed;

    /// <summary>Ventana que espera Silero a 16 kHz: 512 muestras, unos 32 ms.</summary>
    public int WindowSamples => 512;

    /// <summary>
    /// Muestras de la ventana anterior que hay que volver a mandar adelante de la nueva.
    /// </summary>
    /// <remarks>
    /// Esto no está en la documentación del modelo: está en el envoltorio de Python del proyecto, que
    /// pega el contexto <em>antes</em> de llamar al grafo. O sea que el <c>.onnx</c> no espera 512
    /// muestras sino 576, y las primeras 64 son el final de la ventana anterior.
    /// <para>
    /// Costó encontrarlo porque el modelo no se queja: acepta 512 muestras sin error y devuelve
    /// probabilidades bajas para cualquier cosa. Medido con voz sintetizada por Windows —que Whisper
    /// transcribe perfecta, así que de audio no era— el pico daba 0,125 donde tenía que dar más de
    /// 0,9. Un error que no rompe nada y sólo se ve midiendo.
    /// </para>
    /// </remarks>
    private const int ContextSamples = 64;

    public int SampleRate => 16_000;

    /// <summary>
    /// Carpeta donde se esperan <c>silero_vad.onnx</c> y el runtime.
    /// </summary>
    /// <remarks>
    /// Va al lado de los modelos de Whisper por la misma razón: es la carpeta que el usuario ya
    /// conoce, la que puede borrar entera para dejar el equipo como estaba. Hacen falta tres
    /// archivos: el modelo, <c>Microsoft.ML.OnnxRuntime.dll</c> —la variante <c>netstandard2.0</c>
    /// del paquete, que es la que no arrastra <c>System.Numerics.Tensors</c> aparte— y
    /// <c>onnxruntime.dll</c>.
    /// </remarks>
    public static string GetDefaultModelDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("LOCALAPPDATA no está disponible.");
        }

        return Path.Combine(localApplicationData, "Viernes", "Models", "Vad");
    }

    /// <summary>Ruta esperada del modelo Silero.</summary>
    public static string GetDefaultModelPath() =>
        Path.Combine(GetDefaultModelDirectory(), "silero_vad.onnx");

    /// <summary>
    /// Intenta armar el runner; devuelve <c>null</c> y un motivo legible si no se puede.
    /// </summary>
    /// <param name="modelPath">Ruta del <c>.onnx</c>; si es nula se usa la predeterminada.</param>
    /// <param name="unavailableReason">Por qué no se pudo, para poder decírselo al usuario.</param>
    /// <returns>El runner listo, o <c>null</c>.</returns>
    public static OnnxVadModelRunner? TryCreate(string? modelPath, out string? unavailableReason)
    {
        var path = string.IsNullOrWhiteSpace(modelPath) ? GetDefaultModelPath() : modelPath;
        if (!File.Exists(path))
        {
            unavailableReason = $"Falta el modelo de voz entrenado en {path}.";
            return null;
        }

        var directory = Path.GetDirectoryName(path);
        var (runtime, loadedFromDirectory) = TryLoadRuntime(directory);
        if (runtime is null)
        {
            unavailableReason =
                $"Falta {RuntimeAssemblyFile} (y sus binarios nativos) junto al modelo, en {directory}.";
            return null;
        }

        if (loadedFromDirectory)
        {
            // Este equipo tiene un onnxruntime.dll en system32 de otra versión, y cargarlo no da una
            // excepción: da una violación de acceso que se lleva puesto el proceso entero —o sea,
            // toda la asistente— y no hay try/catch que la agarre. Comprobado. Por eso, cuando el
            // runtime viene de la carpeta del usuario, el binario nativo tiene que venir de ahí
            // también, y se fija a mano para que Windows no salga a buscar por su cuenta.
            if (!File.Exists(Path.Combine(directory!, NativeRuntimeFile)))
            {
                unavailableReason =
                    $"Falta {NativeRuntimeFile} junto a {RuntimeAssemblyFile} en {directory}. " +
                    "Sin él, Windows cargaría el que tenga instalado el sistema y una versión que " +
                    "no corresponde tira abajo el proceso.";
                return null;
            }

            PinNativeRuntime(runtime, directory!);
        }

        try
        {
            var runner = new OnnxVadModelRunner(runtime, path);
            unavailableReason = null;
            return runner;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Un runtime incompleto, un modelo de otra versión o un .onnx corrupto no pueden dejar
            // sin voz al asistente: se informa y sigue la heurística.
            unavailableReason = $"El detector entrenado no pudo cargarse: {exception.Message}";
            return null;
        }
    }

    /// <summary>
    /// Busca el runtime primero como dependencia de la aplicación y después en la carpeta del modelo.
    /// </summary>
    /// <remarks>
    /// El orden importa: si alguien decidió agregar el paquete de ONNX Runtime a la aplicación, ese
    /// es el que hay que usar, con sus nativos ya resueltos por el SDK. La carpeta del usuario es el
    /// camino alternativo para no obligar a nadie a empaquetar 120 MB.
    /// </remarks>
    private static (Assembly? Runtime, bool FromDirectory) TryLoadRuntime(string? probeDirectory)
    {
        try
        {
            return (Assembly.Load(new AssemblyName(RuntimeAssemblyName)), false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
        }

        if (string.IsNullOrWhiteSpace(probeDirectory))
        {
            return (null, false);
        }

        var candidate = Path.Combine(probeDirectory, RuntimeAssemblyFile);
        if (!File.Exists(candidate))
        {
            return (null, false);
        }

        try
        {
            return (Assembly.LoadFrom(candidate), true);
        }
        catch (Exception exception) when (exception is FileLoadException or BadImageFormatException or IOException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Ata el <c>onnxruntime.dll</c> del runtime al que está en la carpeta del modelo.
    /// </summary>
    /// <remarks>
    /// Sin esto Windows resuelve la biblioteca nativa por su orden habitual y en este equipo eso
    /// significa el <c>onnxruntime.dll</c> de system32, de otra versión, que al inicializarse
    /// provoca una violación de acceso y mata el proceso.
    /// </remarks>
    private static void PinNativeRuntime(Assembly runtime, string directory)
    {
        if (Interlocked.Exchange(ref _nativeResolverInstalled, 1) != 0)
        {
            return;
        }

        var native = Path.Combine(directory, NativeRuntimeFile);
        NativeLibrary.SetDllImportResolver(runtime, (libraryName, _, _) =>
            libraryName.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(native)
                : IntPtr.Zero);
    }

    private OnnxVadModelRunner(Assembly runtime, string modelPath)
    {
        var sessionType = Require(runtime, "Microsoft.ML.OnnxRuntime.InferenceSession");
        var ortValueType = Require(runtime, "Microsoft.ML.OnnxRuntime.OrtValue");
        var runOptionsType = Require(runtime, "Microsoft.ML.OnnxRuntime.RunOptions");

        _sessionOptions = BuildSessionOptions(runtime);
        _session = _sessionOptions is null
            ? (IDisposable)Activator.CreateInstance(sessionType, modelPath)!
            : (IDisposable)Activator.CreateInstance(sessionType, modelPath, _sessionOptions)!;
        _runOptions = (IDisposable)Activator.CreateInstance(runOptionsType)!;

        // La variante de Run que devuelve void es la que recibe las salidas ya reservadas. Es la que
        // permite leer el resultado de arreglos propios sin tocar un Span.
        _run = sessionType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(method => method.Name == "Run" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 5 &&
                method.GetParameters()[2].ParameterType.GetGenericArguments()[0] == ortValueType);

        var createFromMemory = ortValueType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(method => method.Name == "CreateTensorValueFromMemory" &&
                method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType.IsArray);
        var createFloat = createFromMemory.MakeGenericMethod(typeof(float));
        var createLong = createFromMemory.MakeGenericMethod(typeof(long));

        var inputNames = ReadNames(sessionType, "InputNames", _session);
        var outputNames = ReadNames(sessionType, "OutputNames", _session);

        // Silero cambió de forma entre versiones: la 4 lleva dos estados sueltos («h» y «c») de
        // 2×1×64 y la 5 los unificó en «state», de 2×1×128. Se descubre por los nombres en vez de
        // fijar una versión, así que sirve el archivo que el usuario haya bajado.
        var audioName = inputNames.FirstOrDefault(name => name is "input") ?? inputNames[0];
        var sampleRateName = inputNames.FirstOrDefault(name => name is "sr");
        var stateInputNames = inputNames.Where(name => name is "state" or "h" or "c").ToArray();
        var probabilityName = outputNames.FirstOrDefault(name => name is "output") ?? outputNames[0];
        var stateOutputNames = outputNames.Where(name => name is "stateN" or "hn" or "cn").ToArray();

        var stateShapes = stateInputNames
            .Select(name => name is "state" ? new long[] { 2, 1, 128 } : new long[] { 2, 1, 64 })
            .ToArray();
        _stateIn = [.. stateShapes.Select(shape => new float[shape.Aggregate(1L, (a, b) => a * b)])];
        _stateOut = [.. stateShapes.Select(shape => new float[shape.Aggregate(1L, (a, b) => a * b)])];
        _audio = new float[ContextSamples + WindowSamples];

        var names = new List<string> { audioName };
        var values = new List<object?>
        {
            Track(createFloat.Invoke(null, [_audio, new long[] { 1, _audio.Length }]))
        };
        if (sampleRateName is not null)
        {
            var rate = new long[] { SampleRate };
            names.Add(sampleRateName);
            values.Add(Track(createLong.Invoke(null, [rate, new long[] { 1 }])));
        }

        for (var index = 0; index < stateInputNames.Length; index++)
        {
            names.Add(stateInputNames[index]);
            values.Add(Track(createFloat.Invoke(null, [_stateIn[index], stateShapes[index]])));
        }

        var resultNames = new List<string> { probabilityName };
        var resultValues = new List<object?>
        {
            Track(createFloat.Invoke(null, [_probability, new long[] { 1, 1 }]))
        };
        for (var index = 0; index < stateOutputNames.Length; index++)
        {
            resultNames.Add(stateOutputNames[index]);
            resultValues.Add(Track(createFloat.Invoke(null, [_stateOut[index], stateShapes[index]])));
        }

        _inputNames = names;
        _outputNames = resultNames;
        _inputValues = ToTypedList(ortValueType, values);
        _outputValues = ToTypedList(ortValueType, resultValues);
    }

    /// <summary>
    /// Le pone freno de mano al planificador de ONNX Runtime.
    /// </summary>
    /// <remarks>
    /// <b>Medido: sin esto, el oído continuo consumía 3,2 núcleos enteros todo el tiempo</b> —39,5 s
    /// de CPU cada 12 s de reloj—, contra 4 % de un núcleo con la heurística. No es que la inferencia
    /// sea cara: es una ventana de 512 muestras treinta y una veces por segundo. Lo caro es lo que
    /// ONNX Runtime hace <em>entre</em> inferencias: reparte cada operación entre todos los núcleos y
    /// los deja girando en espera activa para que la próxima llamada arranque un microsegundo antes.
    /// Ese cambio es excelente para un servidor que infiere sin parar y desastroso para un proceso
    /// que arranca con Windows, escucha todo el día y tiene que desaparecer detrás de lo que el
    /// usuario está haciendo.
    /// <para>
    /// Un hilo, sin paralelismo entre operaciones y sin espera activa. Va por reflexión y con todo
    /// opcional porque el runtime lo pone el usuario en una carpeta y no se sabe qué versión es: si
    /// alguna de estas perillas no existe, se arma la sesión sin ellas y el modelo igual corre.
    /// </para>
    /// </remarks>
    private static IDisposable? BuildSessionOptions(Assembly runtime)
    {
        try
        {
            var type = runtime.GetType("Microsoft.ML.OnnxRuntime.SessionOptions");
            if (type is null || Activator.CreateInstance(type) is not IDisposable options)
            {
                return null;
            }

            type.GetProperty("IntraOpNumThreads")?.SetValue(options, 1);
            type.GetProperty("InterOpNumThreads")?.SetValue(options, 1);

            // ORT_SEQUENTIAL. Se arma desde el cero para no depender del nombre del enum.
            var executionMode = type.GetProperty("ExecutionMode");
            if (executionMode is not null && executionMode.PropertyType.IsEnum)
            {
                executionMode.SetValue(options, Enum.ToObject(executionMode.PropertyType, 0));
            }

            var addEntry = type.GetMethod("AddSessionConfigEntry", [typeof(string), typeof(string)]);
            if (addEntry is not null)
            {
                // Acá está la mayor parte de los tres núcleos: sin esto los hilos giran esperando la
                // próxima ventana en vez de dormirse.
                addEntry.Invoke(options, ["session.intra_op.allow_spinning", "0"]);
                addEntry.Invoke(options, ["session.inter_op.allow_spinning", "0"]);
            }

            return options;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Un runtime con otra forma no puede dejar sin detector al asistente: se arma la sesión
            // como antes y, como mucho, gasta de más.
            return null;
        }
    }

    public float Probability(ReadOnlySpan<float> window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (window.Length != WindowSamples)
        {
            throw new ArgumentException(
                $"El modelo espera exactamente {WindowSamples} muestras.",
                nameof(window));
        }

        // Las primeras 64 posiciones son el final de la ventana anterior y ya están puestas; la
        // ventana nueva va detrás.
        window.CopyTo(_audio.AsSpan(ContextSamples));
        _run.Invoke(_session, [_runOptions, _inputNames, _inputValues, _outputNames, _outputValues]);
        _audio.AsSpan(_audio.Length - ContextSamples, ContextSamples).CopyTo(_audio);

        // El estado recurrente que salió es el que entra la próxima vez: es lo que le permite al
        // modelo mirar la evolución y no un instante suelto, que es justo lo que separa un golpe de
        // una sílaba.
        for (var index = 0; index < _stateIn.Length; index++)
        {
            _stateOut[index].CopyTo(_stateIn[index], 0);
        }

        return _probability[0];
    }

    public void Reset()
    {
        Array.Clear(_audio);
        foreach (var state in _stateIn)
        {
            Array.Clear(state);
        }

        foreach (var state in _stateOut)
        {
            Array.Clear(state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var value in _values)
        {
            value.Dispose();
        }

        _runOptions.Dispose();
        _session.Dispose();

        // Después de la sesión: las opciones son de ella mientras viva.
        _sessionOptions?.Dispose();
    }

    private object Track(object? value)
    {
        var disposable = (IDisposable)value!;
        _values.Add(disposable);
        return disposable;
    }

    private static object ToTypedList(Type elementType, List<object?> items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static string[] ReadNames(Type sessionType, string propertyName, object session) =>
        [.. (IReadOnlyList<string>)sessionType.GetProperty(propertyName)!.GetValue(session)!];

    private static Type Require(Assembly runtime, string name) =>
        runtime.GetType(name) ??
        throw new InvalidOperationException($"El runtime instalado no expone {name}.");
}
