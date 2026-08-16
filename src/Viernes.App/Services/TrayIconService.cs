using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
    private readonly Icon _icon;

    public TrayIconService(
        Action toggleVisibility,
        Action toggleMute,
        Action toggleWakeWord,
        Action toggleListenWhileHidden,
        Action toggleAutoStart,
        Action exit)
    {
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
            _muteItem,
            _wakeWordItem,
            _listenWhileHiddenItem,
            _autoStartItem,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Salir de Viernes", null, (_, _) => exit())
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Viernes · disponible",
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

    public void SetAutoStart(bool enabled) => _autoStartItem.Checked = enabled;

    public void SetStatus(string status)
    {
        var text = $"Viernes · {status}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
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
