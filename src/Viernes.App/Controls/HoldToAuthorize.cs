using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

using Border = System.Windows.Controls.Border;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Viernes.App.Controls;

/// <summary>
/// Hay que mantenerlo apretado 1200 ms para autorizar. Revocar es un clic, sin confirmación.
/// </summary>
/// <remarks>
/// La asimetría <em>es</em> el diseño: dar permiso para gastar cuesta una duración que sentís;
/// quitarlo no cuesta nada. Un botón común diría «esto es fácil» justo donde el proyecto entero dice
/// lo contrario, y un clic para gastar plata es exactamente lo que se quiere evitar.
/// <para>
/// El relleno avanza <b>lineal</b> a propósito. Un easing haría que la barra parezca un adorno; acá
/// es una duración que estás pagando, y tiene que sentirse pareja de principio a fin.
/// </para>
/// <para>
/// Soltar antes no es un error: retrae y no pasa nada, sin mensaje. No fallaste, no terminaste.
/// </para>
/// </remarks>
internal sealed class HoldToAuthorize : Border
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(1200);

    private readonly Border _fill;
    private readonly TextBlock _label;
    private readonly DispatcherTimerBridge _timer;
    private bool _completed;

    public HoldToAuthorize()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xB3, 0x47));
        CornerRadius = new CornerRadius(6);
        Height = 34;
        Cursor = Cursors.Hand;

        _fill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x57, 0xFF, 0xB3, 0x47)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            CornerRadius = new CornerRadius(6)
        };

        _label = new TextBlock
        {
            Text = "Mantené para autorizar",
            Foreground = Brushes.White,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Child = new Grid { Children = { _fill, _label } };
        _timer = new DispatcherTimerBridge(OnTick);

        MouseLeftButtonDown += (_, _) => Begin();
        MouseLeftButtonUp += (_, _) => Cancel();
        MouseLeave += (_, _) => Cancel();
    }

    /// <summary>Se dispara sólo cuando el relleno llegó al final. Nunca al soltar antes.</summary>
    public event EventHandler? Authorized;

    private DateTime _startedAt;

    private void Begin()
    {
        if (_completed)
        {
            return;
        }

        _startedAt = DateTime.UtcNow;
        _timer.Start();
    }

    private void OnTick()
    {
        var progress = Math.Min(1, (DateTime.UtcNow - _startedAt) / HoldDuration);
        _fill.Width = ActualWidth * progress;

        if (progress < 1)
        {
            return;
        }

        _timer.Stop();
        _completed = true;

        // Se queda lleno un momento y recién ahí se convierte en recibo. Sin destello y sin tilde
        // animado: autorizar gasto no se festeja.
        var settle = new DispatcherTimerBridge(null);
        settle.Once(TimeSpan.FromMilliseconds(200), () =>
        {
            _label.Text = $"Autorizado hasta 23:59 · se pierde si reiniciás";
            Authorized?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Cancel()
    {
        if (_completed || !_timer.IsRunning)
        {
            return;
        }

        _timer.Stop();

        // Retrae y listo. Sin mensaje de error: no fallaste, no terminaste.
        var retract = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        _fill.BeginAnimation(WidthProperty, retract);
    }
}

/// <summary>
/// Envoltorio mínimo sobre el temporizador de la interfaz, para que el control no repita plomería.
/// </summary>
internal sealed class DispatcherTimerBridge
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public DispatcherTimerBridge(Action? onTick)
    {
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            // Cada 16 ms: el relleno tiene que verse continuo, porque su continuidad es el mensaje.
            Interval = TimeSpan.FromMilliseconds(16)
        };

        if (onTick is not null)
        {
            _timer.Tick += (_, _) => onTick();
        }
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Once(TimeSpan delay, Action action)
    {
        var once = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        once.Tick += (sender, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
            action();
        };

        once.Start();
    }
}
