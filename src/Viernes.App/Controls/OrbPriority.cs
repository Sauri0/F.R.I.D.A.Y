using Viernes.App.ViewModels;

namespace Viernes.App.Controls;

/// <summary>
/// Quién le gana a quién cuando dos cosas quieren el cuerpo al mismo tiempo, y con cuánta fuerza se
/// mueve cada estado.
/// </summary>
/// <remarks>
/// Son las cuatro tablas de decisión del boceto —<c>PRI</c>, <c>MPRI</c>, <c>MBLOQ</c> y
/// <c>MEN</c>— juntas en un solo lugar porque juntas son una sola idea: el orbe tiene un cuerpo
/// solo y todo el mundo lo quiere.
/// </remarks>
internal static class OrbPriority
{
    /// <summary>
    /// Prioridad de un estado, de 0 a 6.
    /// </summary>
    /// <remarks>
    /// Un estado de más prioridad corta una transición en vuelo; uno de menos espera a que termine.
    /// El orden no es de importancia sino de <em>urgencia de ser visto</em>: «algo falló» tiene que
    /// entrar en el cuadro siguiente, y «volví a reposo» puede esperar medio segundo sin que nadie
    /// se entere.
    /// </remarks>
    internal static int Of(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Error => 6,
        AssistantVisualState.Deaf => 5,
        AssistantVisualState.Interrupted => 5,
        AssistantVisualState.AskingPermission => 4,
        AssistantVisualState.Attention => 4,
        AssistantVisualState.ProjectWaiting => 3,
        AssistantVisualState.WaitingForYou => 3,
        AssistantVisualState.Offline => 3,
        AssistantVisualState.Unconfigured => 3,
        AssistantVisualState.Listening => 2,
        AssistantVisualState.Thinking => 2,
        AssistantVisualState.Speaking => 2,
        AssistantVisualState.Background => 1,
        AssistantVisualState.Watching => 1,
        _ => 0
    };

    /// <summary>Prioridad de un ánimo, de 1 a 5.</summary>
    internal static int Of(OrbMood mood) => mood switch
    {
        OrbMood.Urgente => 5,
        OrbMood.Tropiezo => 4,
        OrbMood.Pregunta => 3,
        OrbMood.Logro => 2,
        _ => 1
    };

    /// <summary>
    /// Cuánta energía tiene el estado de fondo para prestarle al ánimo, de 0,32 a 1,10.
    /// </summary>
    /// <remarks>
    /// Es lo que hace que «no salió» sea un golpe sobre <em>pensando</em> y un suspiro sobre
    /// <em>esperándote</em>: el mismo ánimo, la misma coreografía, la amplitud escalada por lo que
    /// el cuerpo tenía disponible. Sin esto habría que escribir seis ánimos por estado, o resignarse
    /// a que un orbe apagado dé un salto que no le corresponde.
    /// </remarks>
    internal static double Energy(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Thinking => 1.00,
        AssistantVisualState.Speaking => 1.00,
        AssistantVisualState.Error => 1.10,
        AssistantVisualState.Listening => 0.95,
        AssistantVisualState.Background => 0.85,
        AssistantVisualState.Idle => 0.80,
        AssistantVisualState.ProjectWaiting => 0.80,
        AssistantVisualState.Watching => 0.70,
        AssistantVisualState.Attention => 0.70,
        AssistantVisualState.AskingPermission => 0.70,
        AssistantVisualState.Interrupted => 0.55,
        AssistantVisualState.Deaf => 0.50,
        AssistantVisualState.WaitingForYou => 0.45,
        AssistantVisualState.Offline => 0.38,
        _ => 0.32
    };

    /// <summary>
    /// Combinaciones prohibidas: este ánimo no va sobre este estado.
    /// </summary>
    /// <remarks>
    /// No es una cuestión de estética. «Hola» sobre <em>error</em> o «¡listo!» sobre <em>sin clave</em>
    /// son mentiras: dicen que algo salió bien cuando el cuerpo está diciendo que no puede. Y
    /// «hasta luego» mientras escucha o habla es peor todavía, porque se despide en la mitad de una
    /// frase que ella misma está diciendo.
    /// </remarks>
    internal static bool Blocks(OrbMood mood, AssistantVisualState state) => mood switch
    {
        OrbMood.Saludo => state is AssistantVisualState.Deaf or AssistantVisualState.Offline
            or AssistantVisualState.Unconfigured or AssistantVisualState.Error
            or AssistantVisualState.Interrupted or AssistantVisualState.WaitingForYou,

        OrbMood.Logro => state is AssistantVisualState.Error or AssistantVisualState.Deaf
            or AssistantVisualState.Offline or AssistantVisualState.Unconfigured,

        OrbMood.Despedida => state is AssistantVisualState.Listening or AssistantVisualState.Thinking
            or AssistantVisualState.Background or AssistantVisualState.Speaking,

        OrbMood.Pregunta => state is AssistantVisualState.Deaf or AssistantVisualState.Offline
            or AssistantVisualState.Unconfigured,

        OrbMood.Urgente => state is AssistantVisualState.Unconfigured,

        // «No salió» no está prohibido en ningún lado, y ese hueco es deliberado: tropezar puede
        // pasar en cualquier estado, incluso encima de un error. Es el único que nunca miente.
        _ => false
    };
}
