namespace Viernes.App.ViewModels;

/// <summary>
/// Los quince estados del orbe. Cada uno tiene su color, su dispersión, su giro y su respiración
/// en <see cref="Controls.OrbPalette"/>; acá sólo está el nombre.
/// </summary>
/// <remarks>
/// Los ocho primeros son los que ya existían y conservan su valor numérico: quien los publica
/// —<c>AssistantRuntime</c>— no se entera de que la lista creció. Los siete nuevos van al final por
/// esa razón y no por importancia; de hecho <see cref="Watching"/> es el estado en el que Viernes
/// pasa la mayor parte del día.
/// <para>
/// La distinción que sostiene toda la lista: <em>capacidad reducida no es error</em>, y
/// <em>esperar no es trabajar</em>. Un asistente que dibuja igual «no tengo clave», «no te oigo» y
/// «algo falló» obliga a abrir un menú para saber cuál de las tres pasó.
/// </para>
/// </remarks>
internal enum AssistantVisualState
{
    /// <summary>Reposo: celeste, respira despacio. Nada pendiente, nadie esperando.</summary>
    Idle,

    /// <summary>Te escucho: verde, se estira hacia el sonido. El micrófono está tomando.</summary>
    Listening,

    /// <summary>Pensando: azul, giro rápido y polvo revuelto. Hay un turno en curso.</summary>
    Thinking,

    /// <summary>Hablando: ámbar, con la onda de la sílaba encima.</summary>
    Speaking,

    /// <summary>
    /// Revisar: naranja, tensa y casi lisa. Hay algo que necesita tu ok antes de seguir.
    /// </summary>
    Attention,

    /// <summary>Error: rojo, anillos rotos y temblor. Algo falló de verdad.</summary>
    Error,

    /// <summary>
    /// Falta la clave: nada falló, falta terminar de configurar. Gris, y viva.
    /// </summary>
    /// <remarks>
    /// Capacidad reducida no es error, y hasta ahora compartían dibujo. Gris dice <em>menos</em>;
    /// rojo dice <em>falló</em>. Son cosas distintas y confundirlas hace que el usuario busque un
    /// problema donde sólo hay una instalación a medias.
    /// </remarks>
    Unconfigured,

    /// <summary>Sin red: el mismo cuerpo gris, pero un fluido sin a dónde ir.</summary>
    Offline,

    /// <summary>
    /// Guardia: verde apagado, atenta al nombre. El micrófono está armado pero no está tomando.
    /// </summary>
    /// <remarks>
    /// Es el estado de fondo real de un asistente siempre encendido, y es el que más importa que se
    /// distinga de <see cref="Listening"/>: uno dice «puedo oírte si me llamás» y el otro dice
    /// «te estoy grabando ahora». Confundirlos es un problema de privacidad, no de estética.
    /// </remarks>
    Watching,

    /// <summary>
    /// Trabajando sin vos: azul lavado, giro largo. Hay una misión corriendo que nadie está mirando.
    /// </summary>
    /// <remarks>
    /// Va más apagado que <see cref="Thinking"/> a propósito: el trabajo de fondo no debe reclamar
    /// la vista. Lo que lo delata es el pulso lento —cada 1,7 s—, no el brillo.
    /// </remarks>
    Background,

    /// <summary>Interrumpida: se le cortó la frase. Entra de golpe y se apaga sola.</summary>
    /// <remarks>
    /// Es el único estado que arranca con la transición ya empezada (<c>snap</c>): cuando le pedís
    /// que pare, el corte tiene que verse en el mismo cuadro o no se lee como obediencia.
    /// </remarks>
    Interrupted,

    /// <summary>Esperándote: ámbar tibio, muy quieta. Te hizo una pregunta y no contestaste.</summary>
    WaitingForYou,

    /// <summary>
    /// Un proyecto te espera: violeta, con dos golpecitos cada 2,6 s. Algo quedó a medias.
    /// </summary>
    ProjectWaiting,

    /// <summary>
    /// Pidiendo permiso: naranja, inclinada hacia vos. Quiere hacer algo que necesita autorización.
    /// </summary>
    /// <remarks>
    /// La inclinación (<c>lean</c>) es literal: el cuerpo entero se corre 22 px hacia el costado.
    /// Pedir es acercarse, y eso se dibuja moviéndose, no cambiando de color.
    /// </remarks>
    AskingPermission,

    /// <summary>Sorda: gris azulado, estirándose hacia el sonido que no llega.</summary>
    /// <remarks>
    /// No es <see cref="Unconfigured"/>: el micrófono existe y está tomando, pero no entra señal.
    /// El gesto de <c>reach</c> —un estirón cada 1,9 s— es lo que dice «te estoy buscando».
    /// </remarks>
    Deaf
}
