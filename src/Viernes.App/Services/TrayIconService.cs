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
        Action exit)
    {
        // Elegir el cuerpo es preferencia, no configuración: no cambia ninguna capacidad.
        _gotaItem = new Forms.ToolStripMenuItem("Gota", null, (_, _) => chooseShape("Gota"));
        _nubeItem = new Forms.ToolStripMenuItem("Nube", null, (_, _) => chooseShape("Nube"));
        var shapeMenu = new Forms.ToolStripMenuItem("Cómo se ve");
        shapeMenu.DropDownItems.AddRange([_gotaItem, _nubeItem]);

        _icon = CreateIcon();
        _showItem = new Forms.ToolStripMenuItem("Ocultar widget", null, (_, _) => toggleVisibility());
        _muteItem = new Forms.ToolStripMenuItem("Silenciar voz", null, (_, _) => toggleMute());
        _wakeWordItem = new Forms.ToolStripMenuItem("Activación por voz (demo)", null, (_, _) => toggleWakeWord());
        _listenWhileHiddenItem = new Forms.ToolStripMenuItem(
            "Escuchar aunque esté oculto",
            null,
            (_, _) => toggleListenWhileHidden());
        _autoStartItem = new Forms.ToolStripMenuItem("Iniciar con Windows", null, (_, _) => toggleAutoStart());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([
            _showItem,
            new Forms.ToolStripSeparator(),
            shapeMenu,
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

    public void SetAutoStart(bool enabled) => _autoStartItem.Checked = enabled;

    /// <summary>Aplica el nombre elegido a los textos que lo mencionan.</summary>
    public void SetAssistantName(string name)
    {
        _assistantName = AssistantIdentity.Normalize(name);
        _exitItem.Text = $"Salir de {_assistantName}";
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

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var outer = new SolidBrush(Color.FromArgb(255, 25, 35, 45));
        using var accent = new SolidBrush(Color.FromArgb(255, 114, 217, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(255, 18, 29, 38));
        graphics.FillEllipse(outer, 1, 1, 30, 30);
        graphics.FillEllipse(accent, 5, 5, 22, 22);

        using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = graphics.MeasureString("V", font);
        graphics.DrawString("V", font, textBrush, (32 - size.Width) / 2f, (32 - size.Height) / 2f - 1);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
