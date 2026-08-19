using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Viernes.Core.Voice;

namespace Viernes.App.Shell;

/// <summary>
/// Cómo se dibuja cada palabra de la transcripción según qué tan firme es.
/// </summary>
/// <remarks>
/// Los tres valores salen del fuente de la referencia y no de una escala de grises elegida a ojo:
/// <c>rgba(244,244,247,0.40)</c> para lo recuperado, <c>#F4F4F7</c> pleno para lo confirmado y
/// <c>rgba(244,244,247,0.60)</c> en itálica para lo provisorio. El color es el mismo en los tres; lo
/// que cambia es la opacidad, así que alcanza con teñir el texto una vez y variar el alfa.
/// <para>
/// Vive acá y no en el marcado porque un <c>DataTrigger</c> por calidad son nueve líneas de XAML por
/// cada propiedad, y porque los números tienen que quedar donde se pueden comparar contra el fuente.
/// </para>
/// </remarks>
public sealed class DictationOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DictationQuality quality
            ? quality switch
            {
                DictationQuality.Recuperado => 0.40,
                DictationQuality.Provisorio => 0.60,
                _ => 1.0
            }
            : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sólo lo provisorio va en itálica: es la palabra que todavía se está formando.</summary>
public sealed class DictationStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DictationQuality.Provisorio ? FontStyles.Italic : FontStyles.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
