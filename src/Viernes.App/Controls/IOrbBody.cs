using Viernes.App.ViewModels;

namespace Viernes.App.Controls;

/// <summary>
/// Lo que sabe hacer un cuerpo del orbe, sea la nube o la gota.
/// </summary>
/// <remarks>
/// Existe para que quien publica los estados —hoy <c>AssistantRuntime</c> a través del
/// <c>MainViewModel</c>— no tenga que saber cuál de los dos cuerpos está puesto. Los dos tienen el
/// mismo repertorio: quince estados, seis ánimos, escritorio claro u oscuro y modo madrugada.
/// Cambiar de cuerpo es cambiar de dibujo, nunca de vocabulario.
/// <para>
/// Es la superficie completa que hace falta para encender todo esto. Si algo no se puede decir con
/// estos cuatro miembros, es que falta un estado o un ánimo, no un parámetro.
/// </para>
/// </remarks>
internal interface IOrbBody
{
    /// <summary>El estado de fondo. Dura hasta que lo cambien.</summary>
    AssistantVisualState State { get; set; }

    /// <summary>El escritorio de atrás es claro. Cambia qué color del estado se dibuja.</summary>
    bool IsLightDesktop { get; set; }

    /// <summary>Cuánto entra el modo madrugada, de 0 a 1. Ver <see cref="OrbNight.For"/>.</summary>
    double NightMode { get; set; }

    /// <summary>
    /// Dispara un ánimo. Dura lo que dice <see cref="OrbMoods.Duration"/> y se apaga solo.
    /// </summary>
    /// <remarks>
    /// Se puede disparar el mismo ánimo dos veces seguidas y arranca de cero las dos: es un gesto,
    /// no un estado, y repetirlo tiene sentido. Al terminar el orbe vuelve exactamente al estado de
    /// fondo que tenía, sin que nadie lo reponga.
    /// </remarks>
    void ShowMood(OrbMood mood);
}
