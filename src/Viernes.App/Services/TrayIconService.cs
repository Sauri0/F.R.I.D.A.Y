using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Viernes.Core.Configuration;
using Forms = System.Windows.Forms;

namespace Viernes.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _muteItem;
    private readonly Forms.ToolStripMenuItem _wakeWordItem;
    private readonly Forms.ToolStripMenuItem _listenWhileHiddenItem;
    private readonly Forms.ToolStripMenuItem _autoStartItem;
    private readonly Forms.ToolStripMenuItem _gotaItem;
    private readonly Forms.ToolStripMenuItem _nubeItem;
    private readonly IReadOnlyList<(double Scale, Forms.ToolStripMenuItem Item)> _scaleItems;
    private readonly Forms.ToolStripMenuItem _nameItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Icon _icon;

    /// <summary>
    /// El nombre elegido. Arranca en el de fábrica porque la bandeja se crea antes de leer el disco.
    /// </summary>
    private string _assistantName = AssistantIdentity.DefaultName;

    private string _status = "disponible";

    public TrayIconService(
        Action toggleVisibility,
        Action toggleMute,
        Action toggleWakeWord,
        Action toggleListenWhileHidden,
        Action toggleAutoStart,
        Action<string> chooseShape,
        Action<double> chooseScale,
        Action changeName,
        Action changeKeys,
        Action exit)
    {
        // Elegir el cuerpo es preferencia, no configuración: no cambia ninguna capacidad.
        _gotaItem = new Forms.ToolStripMenuItem("Gota", null, (_, _) => chooseShape("Gota"));
        _nubeItem = new Forms.ToolStripMenuItem("Nube", null, (_, _) => chooseShape("Nube"));
        var shapeMenu = new Forms.ToolStripMenuItem("Cómo se ve");
        shapeMenu.DropDownItems.AddRange([_gotaItem, _nubeItem]);

        // Acá van seis tamaños con nombre y no la barra que tiene el menú del orbe, y es a propósito:
        // esta puerta es la que se usa con el orbe guardado en la bandeja, o sea sin nada en pantalla
        // que se pueda ver crecer. Una barra sin nada que mirar es una barra a medias; una lista de
        // tamaños se elige con lo que uno ya sabe —«ponelo en la mitad»— y dice cuál está puesto.
        var scaleSteps = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        var scaleItems = new List<(double, Forms.ToolStripMenuItem)>(scaleSteps.Length);
        var scaleMenu = new Forms.ToolStripMenuItem("Qué tamaño tengo");
        foreach (var step in scaleSteps)
        {
            var salto = step;
            var item = new Forms.ToolStripMenuItem($"{salto * 100:0} %", null, (_, _) => chooseScale(salto));
            scaleItems.Add((salto, item));
            scaleMenu.DropDownItems.Add(item);
        }

        _scaleItems = scaleItems;

        _icon = CreateIcon();
        _showItem = new Forms.ToolStripMenuItem("Ocultar widget", null, (_, _) => toggleVisibility());
        _muteItem = new Forms.ToolStripMenuItem("Silenciar voz", null, (_, _) => toggleMute());
        _wakeWordItem = new Forms.ToolStripMenuItem("Activación por voz (demo)", null, (_, _) => toggleWakeWord());
        _listenWhileHiddenItem = new Forms.ToolStripMenuItem(
            "Escuchar aunque esté oculto",
            null,
            (_, _) => toggleListenWhileHidden());
        _autoStartItem = new Forms.ToolStripMenuItem("Iniciar con Windows", null, (_, _) => toggleAutoStart());

        // También acá y no sólo en el menú del orbe: con el widget guardado en la bandeja, ésta es la
        // única puerta que queda abierta.
        _nameItem = new Forms.ToolStripMenuItem($"Cómo me llamo: {_assistantName}…", null, (_, _) => changeName());
        var keysItem = new Forms.ToolStripMenuItem("Mis claves…", null, (_, _) => changeKeys());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([
            _showItem,
            new Forms.ToolStripSeparator(),
            shapeMenu,
            scaleMenu,
            _nameItem,
            keysItem,
            new Forms.ToolStripSeparator(),
            _muteItem,
            _wakeWordItem,
            _listenWhileHiddenItem,
            _autoStartItem,
            new Forms.ToolStripSeparator(),
            _exitItem = new Forms.ToolStripMenuItem($"Salir de {_assistantName}", null, (_, _) => exit())
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = $"{_assistantName} · disponible",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => toggleVisibility();
    }

    public void SetWindowVisible(bool visible) =>
        _showItem.Text = visible ? "Ocultar widget" : "Mostrar widget";

    public void SetMuted(bool muted)
    {
        _muteItem.Checked = muted;
        _muteItem.Text = muted ? "Activar voz" : "Silenciar voz";
    }

    public void SetWakeWordEnabled(bool enabled)
    {
        _wakeWordItem.Checked = enabled;
        _wakeWordItem.Text = enabled
            ? "Activación por voz (demo) · activa"
            : "Activación por voz (demo) · apagada";
    }

    public void SetListenWhileHidden(bool enabled)
    {
        _listenWhileHiddenItem.Checked = enabled;
        _listenWhileHiddenItem.Text = enabled
            ? "Escuchar aunque esté oculto · sí"
            : "Escuchar aunque esté oculto · no";
    }

    public void SetOrbShape(string shape)
    {
        var isNube = string.Equals(shape, "Nube", StringComparison.OrdinalIgnoreCase);
        _gotaItem.Checked = !isNube;
        _nubeItem.Checked = isNube;
    }

    /// <summary>
    /// Marca el tamaño puesto, o ninguno si viene de la barra y cayó entre dos.
    /// </summary>
    /// <remarks>
    /// Media unidad porcentual de tolerancia: la barra del orbe va de a 5 %, así que un 125 elegido
    /// allá tiene que encontrarse marcado acá, y un 135 no tiene que marcar nada. Marcar el más
    /// cercano sería mentir sobre lo que está puesto.
    /// </remarks>
    public void SetOrbScale(double scale)
    {
        foreach (var (step, item) in _scaleItems)
        {
            item.Checked = Math.Abs(step - scale) < 0.005;
        }
    }

    public void SetAutoStart(bool enabled) => _autoStartItem.Checked = enabled;

    /// <summary>Aplica el nombre elegido a los textos que lo mencionan.</summary>
    public void SetAssistantName(string name)
    {
        _assistantName = AssistantIdentity.Normalize(name);
        _exitItem.Text = $"Salir de {_assistantName}";
        _nameItem.Text = $"Cómo me llamo: {_assistantName}…";
        SetStatus(_status);
    }

    public void SetStatus(string status)
    {
        _status = status;

        // Windows corta el tooltip de la bandeja en 63 caracteres, y con un nombre largo el recorte
        // se comía el estado —que es la única parte que cambia—. Se recorta el nombre, no el estado.
        var suffix = $" · {status}";
        var room = Math.Max(0, 63 - suffix.Length);
        var name = _assistantName.Length <= room ? _assistantName : _assistantName[..room];
        _notifyIcon.Text = $"{name}{suffix}";
    }

    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2400);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    /// <summary>
    /// El icono de la bandeja: el mismo del producto, cargado del ensamblado.
    /// </summary>
    /// <remarks>
    /// Acá había un dibujo hecho a mano con GDI+: un círculo azul oscuro, otro celeste adentro y una
    /// «V» en el medio. No se parecía a nada de lo que la aplicación muestra —el orbe no es un
    /// círculo ni tiene letras— así que en la bandeja el producto se presentaba con una identidad
    /// que no era la suya.
    /// <para>
    /// Se pide el tamaño que Windows quiera para la bandeja en vez de fijar 32: en pantallas con
    /// escalado pide 20 o 24, y un 32 achatado a 20 se ve peor que el de 16 puesto en su lugar. El
    /// .ico trae los seis tamaños justamente para que esto pueda elegir.
    /// </para>
    /// </remarks>
    private static Icon CreateIcon()
    {
        var lado = Forms.SystemInformation.SmallIconSize.Width;

        try
        {
            var recurso = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/Viernes.ico", UriKind.Absolute));

            if (recurso is not null)
            {
                using var flujo = recurso.Stream;
                return new Icon(flujo, new System.Drawing.Size(lado, lado));
            }
        }
        catch (Exception excepcion) when (excepcion is System.IO.IOException
            or ArgumentException
            or System.Windows.Markup.XamlParseException)
        {
            // Sin icono no se puede: la bandeja necesita uno para poder mostrarse.
        }

        return SystemIcons.Application;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
