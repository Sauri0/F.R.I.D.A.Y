using System.Windows;
using System.Windows.Media.Animation;

// El proyecto arrastra WinForms por la bandeja, así que Color, Brush, Point y Size existen dos veces.
// Los alias evitan calificar cada uso y dejan el dibujo legible.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Geometry = System.Windows.Media.Geometry;
using GradientStop = System.Windows.Media.GradientStop;
using RadialGradientBrush = System.Windows.Media.RadialGradientBrush;
using ScaleTransform = System.Windows.Media.ScaleTransform;
using StreamGeometry = System.Windows.Media.StreamGeometry;
using SweepDirection = System.Windows.Media.SweepDirection;
using Transform = System.Windows.Media.Transform;
using Grid = System.Windows.Controls.Grid;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App.Controls;

/// <summary>
/// Media gota de 14 × 22 pegada al borde, para cuando el orbe está oculto y el micrófono sigue armado.
/// </summary>
/// <remarks>
/// Existe por una regla del diseño que no es estética: <b>si el micrófono puede tomar audio, tiene
/// que haber algo en pantalla</b>. «Escuchar aunque esté oculto» sin nada visible es una promesa que
/// el diseño no puede cumplir — y el único estado en que no queda nada a la vista es el silencio,
/// porque ahí tampoco hay micrófono.
/// <para>
/// Se construye en código y no en XAML a propósito: es una ventana de catorce píxeles de ancho, sin
/// estados ni enlaces, y un archivo de marcado para esto sería más ceremonia que dibujo.
/// </para>
/// </remarks>
internal sealed class EdgeMark : Window
{
    private const double MarkWidth = 14;
    private const double MarkHeight = 22;

    /// <summary>Opacidad base. La respiración la mueve ±0,06 y nunca llega a llamar la atención.</summary>
    private const double BaseOpacity = 0.46;

    private readonly Path _body;
    private readonly Action _onRestore;

    public EdgeMark(Action onRestore)
    {
        _onRestore = onRestore;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = MarkWidth;
        Height = MarkHeight;
        Cursor = System.Windows.Input.Cursors.Hand;

        _body = new Path
        {
            Data = BuildHalfDrop(),
            Fill = BuildFill(),
            Opacity = BaseOpacity
        };

        Content = new Grid { Children = { _body } };

        MouseLeftButtonDown += (_, _) => _onRestore();
        MouseEnter += (_, _) => Fade(0.85, 120);
        MouseLeave += (_, _) => Fade(BaseOpacity, 120);
    }

    /// <summary>
    /// Media gota con el lado plano contra el borde. El lado curvo tiene radio 11 —la mitad del
    /// alto— así que es exactamente medio óvalo y no un rectángulo redondeado.
    /// </summary>
    private static Geometry BuildHalfDrop()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(0, MarkHeight), isStroked: true, isSmoothJoin: true);
            context.ArcTo(
                new Point(0, 0),
                new Size(MarkWidth, MarkHeight / 2),
                rotationAngle: 0,
                isLargeArc: false,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Mismo gradiente del cuerpo, con el foco corrido: a este tamaño el foco original se pierde.</summary>
    private static System.Windows.Media.Brush BuildFill() => new RadialGradientBrush
    {
        GradientOrigin = new Point(0.30, 0.28),
        Center = new Point(0.30, 0.28),
        RadiusX = 0.95,
        RadiusY = 0.95,
        GradientStops =
        [
            new GradientStop(Color.FromRgb(0x9E, 0xE6, 0xFF), 0),
            new GradientStop(Color.FromRgb(0x72, 0xD9, 0xFF), 0.50),
            new GradientStop(Color.FromRgb(0x17, 0x60, 0x7F), 0.84),
            new GradientStop(Color.FromRgb(0x08, 0x31, 0x4A), 1)
        ]
    };

    /// <summary>
    /// La deja contra el borde vertical más cercano a donde estaba el orbe, a la misma altura.
    /// </summary>
    /// <remarks>
    /// Pegarse al borde <em>más cercano</em> y no siempre al derecho es lo que hace que se lea como
    /// que el orbe se fue para allá: la marca queda donde tu ojo lo dejó.
    /// </remarks>
    public void SnapNear(Rect orbBounds, Rect workArea)
    {
        var orbCenterX = orbBounds.Left + (orbBounds.Width / 2);
        var toLeft = Math.Abs(orbCenterX - workArea.Left);
        var toRight = Math.Abs(workArea.Right - orbCenterX);
        var againstLeft = toLeft <= toRight;

        Left = againstLeft ? workArea.Left : workArea.Right - MarkWidth;
        Top = Math.Clamp(
            orbBounds.Top + (orbBounds.Height / 2) - (MarkHeight / 2),
            workArea.Top,
            workArea.Bottom - MarkHeight);

        // El lado plano siempre contra el borde: contra el izquierdo hay que espejarla.
        _body.RenderTransform = againstLeft
            ? Transform.Identity
            : new ScaleTransform(-1, 1, MarkWidth / 2, MarkHeight / 2);
    }

    /// <summary>
    /// Respiración lentísima: nueve segundos y ±0,06 de opacidad. Sin excursiones de forma, que a
    /// catorce píxeles no se leerían y sólo harían ruido.
    /// </summary>
    public void StartBreathing()
    {
        var breath = new DoubleAnimation
        {
            From = BaseOpacity - 0.06,
            To = BaseOpacity + 0.06,
            Duration = TimeSpan.FromMilliseconds(9300),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        _body.BeginAnimation(OpacityProperty, breath);
    }

    private void Fade(double to, int milliseconds)
    {
        // Detiene la respiración mientras dura el hover: dos animaciones sobre la misma propiedad
        // se pelean y el resultado es un parpadeo.
        _body.BeginAnimation(OpacityProperty, null);
        _body.Opacity = to;
        _ = milliseconds;
    }
}
