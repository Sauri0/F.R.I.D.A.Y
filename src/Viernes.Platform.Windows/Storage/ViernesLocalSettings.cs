using System.Text.Json.Serialization;
using Viernes.Core.Configuration;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Storage;

/// <summary>
/// Preferencias locales no sensibles. Deliberadamente no contiene claves, tokens ni credenciales.
/// </summary>
public sealed record ViernesLocalSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool MicrophoneMuted { get; init; }

    public bool StartWithWindows { get; init; }

    /// <summary>Wake-word local es el modo normal; PTT sigue disponible como control privado.</summary>
    public VoiceActivationMode VoiceActivation { get; init; } = VoiceActivationMode.LocalWakeWord;

    /// <summary>
    /// Cómo se llama el asistente. Lo elige quien instala.
    /// </summary>
    public string AssistantName { get; init; } = AssistantIdentity.DefaultName;

    /// <summary>
    /// Frases de activación. <c>null</c> significa «las que salen del nombre».
    /// </summary>
    /// <remarks>
    /// Guardar las frases sólo cuando alguien las escribió a mano es lo que hace que renombrar el
    /// asistente funcione: si estuvieran siempre en disco, cambiar el nombre de «Viernes» a «Ana»
    /// dejaría «Hola Viernes» escrito en el archivo y seguiría despertando con el nombre viejo
    /// —o peor, no despertaría con ninguno de los dos.
    /// <para>
    /// La palabra suelta queda afuera a propósito; el porqué está en
    /// <see cref="AssistantIdentity.WakePhrases"/>. Quien prefiera la palabra suelta puede reponerla
    /// con <c>VIERNES_WAKE_PHRASES</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? WakeWordPhrases { get; init; }

    /// <summary>Las frases que efectivamente despiertan: las elegidas, o las derivadas del nombre.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveWakePhrases =>
        this.WakeWordPhrases is { Count: > 0 }
            ? this.WakeWordPhrases
            : new AssistantIdentity(this.AssistantName).WakePhrases;

    /// <summary>
    /// Mantiene la activación por voz mientras el orbe está oculto, para que Viernes pueda aparecer
    /// solo al ser llamado. Silenciar sigue siendo el corte duro que libera el micrófono.
    /// </summary>
    public bool ListenWhileHidden { get; init; } = true;

    /// <summary>
    /// Cuerpo del orbe elegido por el usuario: <c>Gota</c> o <c>Nube</c>. Es preferencia, no
    /// configuración del sistema: cambiarla no altera ninguna capacidad ni ningún permiso.
    /// </summary>
    public string OrbShape { get; init; } = "Gota";

    /// <summary>
    /// Qué tan grande es el orbe, en fracción de su tamaño de fábrica. Sólo lo afecta a él.
    /// </summary>
    /// <remarks>
    /// Se guarda la fracción y no los píxeles a propósito: el tamaño de fábrica es una decisión de
    /// diseño que puede cambiar con una versión, y un archivo con «108» escrito la congelaría para
    /// quien ya lo tenía. El rango legal lo pone <see cref="OrbScaleRange"/>, y
    /// <c>LocalSettingsStore.Normalize</c> lo recorta: un archivo editado a mano con 50 no puede
    /// dejar un orbe de cinco mil píxeles.
    /// </remarks>
    public double OrbScale { get; init; } = OrbScaleRange.Default;

    /// <summary>
    /// Si el orbe se muda solo al monitor donde estás trabajando, mirando dónde está el cursor.
    /// </summary>
    /// <remarks>
    /// <b>Arranca apagada</b>, y eso es la decisión, no el valor por descuido. Encendida, el orbe
    /// mira cada 700 ms en qué pantalla está el mouse y a los tres tics seguidos en otra se muda,
    /// sin que el usuario haya pedido nada: «si se va de la pantalla ella también, incluso si no se
    /// presiona». Un objeto que se muda solo mientras mirás otra cosa es exactamente la queja que
    /// originó esta preferencia, así que lo que no sorprende es el valor de fábrica.
    /// <para>
    /// Apagada, el orbe igual se muda cuando el usuario <em>actúa</em>: cuando lo llama —ahí el
    /// cursor sí dice dónde está, porque acaba de hablarle— y cuando lo arrastra con la mano.
    /// </para>
    /// </remarks>
    public bool FollowActiveMonitor { get; init; }

    public string RecognitionCulture { get; init; } = "es-AR";

    public string? PreferredVoiceName { get; init; }

    public SpeechRecognitionProviderKind PreferredRecognitionProvider { get; init; } =
        SpeechRecognitionProviderKind.WhisperLocal;

    /// <summary>Ruta local del modelo GGML; nunca contiene credenciales ni dispara descargas.</summary>
    public string? WhisperModelPath { get; init; }

    /// <summary>Identificador público de modelo; la clave de OpenRouter nunca se guarda aquí.</summary>
    public string? PreferredOpenRouterModel { get; init; }

    public double? WidgetLeft { get; init; }

    public double? WidgetTop { get; init; }
}
