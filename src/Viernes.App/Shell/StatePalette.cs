using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Viernes.App.ViewModels;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Viernes.App.Shell;

/// <summary>
/// El color de cada estado, uno solo y compartido: el orbe, la píldora y el tinte del vidrio tienen
/// que decir lo mismo al mismo tiempo.
/// </summary>
internal static class StatePalette
{
    /// <summary>Color base del estado.</summary>
    public static Color For(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Listening => Color.FromRgb(0x72, 0xF0, 0xC0),
        AssistantVisualState.Thinking => Color.FromRgb(0x9B, 0xB7, 0xFF),
        AssistantVisualState.Speaking => Color.FromRgb(0xFF, 0xCE, 0x82),
        AssistantVisualState.Attention => Color.FromRgb(0xFF, 0xB3, 0x47),
        AssistantVisualState.Error => Color.FromRgb(0xFF, 0x73, 0x85),
        AssistantVisualState.Unconfigured => Color.FromRgb(0xD8, 0xD2, 0xC6),
        AssistantVisualState.Offline => Color.FromRgb(0xE9, 0xEF, 0xF2),
        _ => Color.FromRgb(0x72, 0xD9, 0xFF)
    };

    /// <summary>Pincel congelado del estado, para no crear uno por cuadro.</summary>
    public static Brush BrushFor(AssistantVisualState state)
    {
        if (Cache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(For(state));
        brush.Freeze();
        Cache[state] = brush;
        return brush;
    }

    private static readonly Dictionary<AssistantVisualState, Brush> Cache = [];
}

/// <summary>Convierte el estado en su color, para los bindings del XAML.</summary>
internal sealed class StateBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        StatePalette.BrushFor(value is AssistantVisualState state ? state : AssistantVisualState.Idle);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("El color del estado se lee, no se escribe.");
}

/// <summary>Convierte el estado en su color puro, para las propiedades que piden un color.</summary>
internal sealed class StateColorConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        StatePalette.For(value is AssistantVisualState state ? state : AssistantVisualState.Idle);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("El color del estado se lee, no se escribe.");
}

/// <summary>
/// Muestra un bloque sólo si el desplegable abierto es el que dice el parámetro.
/// </summary>
/// <remarks>
/// Trece bloques en la misma celda y un solo comparador. La alternativa —trece propiedades booleanas
/// en el modelo de vista— es la misma condición escrita trece veces, y basta olvidarse de avisar de
/// una para que queden dos paneles dibujados uno encima del otro.
/// </remarks>
internal sealed class PanelKindVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PanelKind kind &&
        parameter is string name &&
        string.Equals(kind.ToString(), name, StringComparison.Ordinal)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("El desplegable abierto lo decide el modelo de vista.");
}

/// <summary>Invierte un booleano y lo vuelve visibilidad: se ve cuando <em>no</em> hay nada.</summary>
internal sealed class EmptyToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Sólo se lee.");
}

/// <summary>Se ve cuando hay al menos una fila.</summary>
internal sealed class FilledToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Sólo se lee.");
}
