namespace Viernes.App.ViewModels;

internal enum AssistantVisualState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Attention,
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
    Offline
}

