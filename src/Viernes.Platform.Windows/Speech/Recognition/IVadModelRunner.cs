namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// El modelo entrenado, reducido a lo único que se le pide: mirar una ventana y decir cuánta voz hay.
/// </summary>
/// <remarks>
/// Existe para que la decisión —umbrales, histéresis, qué se considera empezar a hablar— quede en
/// código propio y con pruebas, y no adentro de la caja negra ni mezclada con la plomería de ONNX.
/// El que corre el modelo de verdad es <see cref="OnnxVadModelRunner"/>; en las pruebas se le pasa
/// una secuencia de probabilidades a mano y se comprueba qué decide con ellas.
/// </remarks>
public interface IVadModelRunner : IDisposable
{
    /// <summary>Cuántas muestras espera el modelo por ventana.</summary>
    int WindowSamples { get; }

    /// <summary>Frecuencia de muestreo con la que fue entrenado.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Probabilidad de que la ventana contenga voz, entre 0 y 1.
    /// </summary>
    /// <param name="window">
    /// Muestras normalizadas a −1..1, exactamente <see cref="WindowSamples"/> de largo.
    /// </param>
    /// <returns>La probabilidad que devolvió el modelo.</returns>
    float Probability(ReadOnlySpan<float> window);

    /// <summary>Olvida el estado recurrente. Una frase nueva no arrastra la anterior.</summary>
    void Reset();
}
