using System.Text;
using System.Windows.Automation;
using Viernes.Core.Tools;

namespace Viernes.Platform.Windows.Actions;

/// <summary>
/// Lee y opera los controles de una aplicación por su nombre, no por su posición en pantalla.
/// </summary>
/// <remarks>
/// Es el camino que hay que preferir sobre mirar una captura y estimar coordenadas. Windows ya
/// publica el árbol de controles de casi cualquier aplicación: nombres, identificadores y patrones
/// de interacción. Pedirle a un modelo que deduzca de una imagen dónde cae un botón, cuando el
/// sistema puede decir «el botón se llama Guardar», es cambiar información exacta por una
/// estimación —y esa estimación se rompe con cada cambio de resolución, de tema o de escala.
/// <para>
/// La visión queda como red para lo que no expone árbol: juegos, lienzos, aplicaciones dibujadas a
/// mano. Ahí sí no hay alternativa.
/// </para>
/// </remarks>
internal static class UiAutomationActions
{
    private const int MaximumControls = 60;

    /// <summary>Enumera los controles utilizables de una ventana, con su nombre tal como los verá el modelo.</summary>
    /// <remarks>
    /// Sin destino se leen los controles de la ventana que el usuario tiene delante, que es lo que
    /// alguien quiere decir con «¿qué hay acá?». Antes fallaba con «no encontré ninguna ventana de
    /// «»», que no es información: es la falta de una respuesta.
    /// </remarks>
    public static PcActionOutcome ReadControls(string? target)
    {
        var (window, problem) = ResolveScope(target);
        if (window is null)
        {
            return new PcActionOutcome(false, problem!);
        }

        var interesting = new StringBuilder();
        var count = 0;
        foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var name = SafeName(element);
            if (string.IsNullOrWhiteSpace(name) || !IsActionable(element))
            {
                continue;
            }

            if (++count > MaximumControls)
            {
                interesting.AppendLine("… (hay más)");
                break;
            }

            interesting.Append("- ").Append(name).Append(" [").Append(ControlLabel(element)).AppendLine("]");
        }

        return count == 0
            ? new PcActionOutcome(false, $"«{window.Current.Name}» no expone controles legibles.")
            : new PcActionOutcome(
                true,
                $"Controles de «{window.Current.Name}»:{Environment.NewLine}{interesting}");
    }

    /// <summary>
    /// Activa un control por nombre. El destino es «ventana|control», o sólo el nombre del control.
    /// </summary>
    public static PcActionOutcome ClickControl(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new PcActionOutcome(false, "Necesito saber qué control tocar.");
        }

        var parts = target.Split('|', 2, StringSplitOptions.TrimEntries);
        var windowName = parts.Length == 2 ? parts[0] : null;
        var controlName = parts.Length == 2 ? parts[1] : parts[0];

        var (scope, problem) = ResolveScope(windowName);
        if (scope is null)
        {
            return new PcActionOutcome(false, problem!);
        }

        var control = FindByName(scope, controlName);
        if (control is null)
        {
            return new PcActionOutcome(false, $"No encontré ningún control llamado «{controlName}».");
        }

        // Invoke es el camino correcto cuando existe: le pide a la aplicación que haga la acción, en
        // vez de simular un clic que puede caer en otro lado si la ventana se movió entremedio.
        if (control.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return new PcActionOutcome(true, $"Activé «{SafeName(control)}».");
        }

        if (control.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return new PcActionOutcome(true, $"Cambié «{SafeName(control)}».");
        }

        if (control.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return new PcActionOutcome(true, $"Seleccioné «{SafeName(control)}».");
        }

        return new PcActionOutcome(
            false,
            $"«{SafeName(control)}» no admite activarse por accesibilidad; probá con un clic.");
    }

    /// <summary>
    /// Escribe dentro de un campo identificado por nombre, sin depender del foco.
    /// </summary>
    /// <remarks>
    /// Acepta «ventana|campo|texto» además de «campo|texto». La forma larga existe porque la corta no
    /// alcanza para decidir: con dos aplicaciones abiertas que tengan un campo «Correo», la búsqueda
    /// desde la raíz del escritorio se quedaba con la primera del recorrido, que es un orden que
    /// nadie controla. Escribir una dirección personal en la ventana equivocada no es un error
    /// menor, así que cuando no se nombra la ventana se usa la que el usuario tiene delante y no
    /// todo el escritorio.
    /// </remarks>
    public static PcActionOutcome SetText(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new PcActionOutcome(false, "Usá «campo|texto» o «ventana|campo|texto».");
        }

        var parts = target.Split('|', 3, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return new PcActionOutcome(false, "Usá «campo|texto» o «ventana|campo|texto».");
        }

        var windowName = parts.Length == 3 ? parts[0] : null;
        var fieldName = parts.Length == 3 ? parts[1] : parts[0];
        var text = parts.Length == 3 ? parts[2] : parts[1];

        var (scope, problem) = ResolveScope(windowName);
        if (scope is null)
        {
            return new PcActionOutcome(false, problem!);
        }

        var field = FindByName(scope, fieldName);
        if (field is null)
        {
            return new PcActionOutcome(
                false,
                $"No encontré ningún campo llamado «{fieldName}» en «{SafeName(scope)}».");
        }

        if (!field.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
        {
            return new PcActionOutcome(false, $"«{fieldName}» no admite escribirse directamente.");
        }

        ((ValuePattern)value).SetValue(text);

        // Se relee el valor en vez de confiar en que SetValue hizo lo suyo: un campo de sólo lectura
        // o con máscara acepta la llamada y se queda como estaba.
        var written = field.TryGetCurrentPattern(ValuePattern.Pattern, out var reread)
            ? ((ValuePattern)reread).Current.Value
            : null;
        if (written is not null && !string.Equals(written, text, StringComparison.Ordinal))
        {
            return new PcActionOutcome(
                false,
                $"«{fieldName}» no se quedó con lo que le escribí: quedó «{written}».");
        }

        return new PcActionOutcome(true, $"Escribí «{text}» en «{fieldName}» de «{SafeName(scope)}».");
    }

    /// <summary>
    /// Decide sobre qué ventana se busca: la nombrada, o la que el usuario tiene delante.
    /// </summary>
    /// <remarks>
    /// Nunca devuelve la raíz del escritorio, y ése es el punto. Buscar un control desde
    /// <c>RootElement</c> recorre todas las ventanas de todas las aplicaciones y se queda con la
    /// primera que coincida por nombre, sin que nadie mande en ese orden.
    /// </remarks>
    private static (AutomationElement? Scope, string? Problem) ResolveScope(string? windowName)
    {
        if (!string.IsNullOrWhiteSpace(windowName))
        {
            var named = FindWindowElement(windowName);
            return named is null
                ? (null, $"No encontré ninguna ventana de «{windowName}».")
                : (named, null);
        }

        var (handle, title) = WindowsPcActionExecutor.FrontForeignWindow();
        if (handle == nint.Zero)
        {
            return (null, "No hay ninguna ventana ajena adelante; decime en cuál querés que trabaje.");
        }

        try
        {
            var element = AutomationElement.FromHandle(handle);
            return element is null
                ? (null, $"«{title}» no publica su árbol de controles.")
                : (element, null);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or ArgumentException)
        {
            // La ventana puede irse entre que se la eligió y que se la consulta.
            return (null, $"«{title}» dejó de estar disponible antes de que pudiera leerla.");
        }
    }

    private static AutomationElement? FindWindowElement(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var needle = target.Trim();
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
        {
            if (SafeName(window).Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>
    /// Busca un control por nombre dentro de <paramref name="scope"/>, y sólo ahí.
    /// </summary>
    private static AutomationElement? FindByName(AutomationElement scope, string name)
    {
        var needle = name.Trim();

        // Primero exacto, después parcial: «Guardar» no debería resolver a «Guardar como…» si el
        // botón exacto existe. Entre los exactos gana el que se pueda usar: la etiqueta «Correo» y
        // la caja que hay al lado se llaman igual, y quedarse con la etiqueta terminaba en «no
        // admite escribirse», que suena a que no se puede cuando sí se puede.
        AutomationElement? firstExact = null;
        foreach (AutomationElement candidate in scope.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, needle)))
        {
            firstExact ??= candidate;
            if (IsActionable(candidate))
            {
                return candidate;
            }
        }

        if (firstExact is not null)
        {
            return firstExact;
        }

        foreach (AutomationElement element in scope.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            if (IsActionable(element) &&
                SafeName(element).Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }
        }

        return null;
    }

    private static bool IsActionable(AutomationElement element)
    {
        try
        {
            var type = element.Current.ControlType;
            return !element.Current.IsOffscreen &&
                (type == ControlType.Button ||
                 type == ControlType.MenuItem ||
                 type == ControlType.Edit ||
                 type == ControlType.CheckBox ||
                 type == ControlType.RadioButton ||
                 type == ControlType.ComboBox ||
                 type == ControlType.ListItem ||
                 type == ControlType.TabItem ||
                 type == ControlType.Hyperlink);
        }
        catch (ElementNotAvailableException)
        {
            // La ventana puede cerrarse mientras se recorre el árbol; no es un error del recorrido.
            return false;
        }
    }

    private static string ControlLabel(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty);
        }
        catch (ElementNotAvailableException)
        {
            return "?";
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }
}
