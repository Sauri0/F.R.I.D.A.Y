namespace Viernes.App.Shell;

/// <summary>
/// Familia de vidrio del desplegable. No es el color del estado: es el registro del panel.
/// </summary>
/// <remarks>
/// El estado del orbe cambia muchas veces por turno y tiñe el cuerpo del vidrio apenas, del lado por
/// donde nace. La familia, en cambio, decide el cuerpo y el contorno, y sólo hay cuatro porque sólo
/// hay cuatro registros: informar, pedir algo, cerrar un tema, y funcionar de menos.
/// </remarks>
internal enum PanelFamily
{
    /// <summary>Informar. La mayoría de los paneles.</summary>
    Neutro,

    /// <summary>Pedir una decisión: permiso, recordatorio, presupuesto.</summary>
    Ambar,

    /// <summary>Cerrar el tema: una regla local dijo que no.</summary>
    Rojo,

    /// <summary>Capacidad reducida. Sin croma, porque no falló nada.</summary>
    Gris
}
