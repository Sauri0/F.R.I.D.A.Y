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
    public static PcActionOutcome ReadControls(string? target)
    {
        var window = FindWindowElement(target);
        if (window is null)
        {
            return new PcActionOutcome(false, $"No encontré ninguna ventana de «{target}».");
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

        var scope = windowName is null
            ? AutomationElement.RootElement
            : FindWindowElement(windowName);
        if (scope is null)
        {
            return new PcActionOutcome(false, $"No encontré ninguna ventana de «{windowName}».");
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

    /// <summary>Escribe dentro de un campo identificado por nombre, sin depender del foco.</summary>
    public static PcActionOutcome SetText(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new PcActionOutcome(false, "Usá «campo|texto».");
        }

        var parts = target.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return new PcActionOutcome(false, "Usá «campo|texto».");
        }

        var field = FindByName(AutomationElement.RootElement, parts[0]);
        if (field is null)
        {
            return new PcActionOutcome(false, $"No encontré ningún campo llamado «{parts[0]}».");
        }

        if (!field.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
        {
            return new PcActionOutcome(false, $"«{parts[0]}» no admite escribirse directamente.");
        }

        ((ValuePattern)value).SetValue(parts[1]);
        return new PcActionOutcome(true, $"Escribí «{parts[1]}» en «{parts[0]}».");
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

    private static AutomationElement? FindByName(AutomationElement scope, string name)
    {
        var needle = name.Trim();

        // Primero exacto, después parcial: «Guardar» no debería resolver a «Guardar como…» si el
        // botón exacto existe.
        var exact = scope.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, needle));
        if (exact is not null)
        {
            return exact;
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
