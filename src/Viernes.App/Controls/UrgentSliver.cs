using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Viernes.App.Shell;
using Viernes.App.ViewModels;

// El proyecto arrastra WinForms por la bandeja, así que Brush, Color y Rect existen dos veces.
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using DropShadowEffect = System.Windows.Media.Effects.DropShadowEffect;
using Rect = System.Windows.Rect;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Viernes.App.Controls;

/// <summary>
/// El filete de 3 px que queda cuando hay algo urgente y el usuario está en pantalla completa.
/// </summary>
/// <remarks>
/// Es lo único que Viernes se permite dejar encima de un juego, un video o una presentación: tres
/// píxeles contra el borde más cercano, con el color del estado. Sin nada urgente no queda nada.
/// <para>
/// Los números salen del fuente de la referencia: <c>width:3px; height:64px; border-radius:2px;
/// opacity:0.85; box-shadow:0 0 12px</c> del color del estado, y <c>top</c> siguiendo la altura del
/// orbe recortada entre el 30 % y el 90 % de la pantalla. Lo que no salió del fuente es la curva de
/// la respiración: el marcado la invoca como <c>animation: vbreath 2.4s ease-in-out infinite</c> pero
/// <c>@keyframes vbreath</c> no está definido en ningún lado. De ahí se tomó el único número que sí
/// está —los 2,4 s— y la excursión de opacidad se hizo igual a la de <see cref="EdgeMark"/>, que es
/// la otra cosa diminuta que respira en este proyecto.
/// </para>
/// <para>
/// <b>No se puede tocar y no toma el foco.</b> Lleva <c>WS_EX_TRANSPARENT</c> además de
/// <c>WS_EX_NOACTIVATE</c>: un filete que come clics contra el borde de un juego a pantalla completa
/// sería peor que no avisar nada.
/// </para>
/// <para>
/// <b>Contradice a propósito lo que <c>MainWindow.EnterFullScreen</c> evita, y queda escrito acá para
/// que no se descubra de nuevo dentro de seis meses.</b> Esa función suelta el <c>Topmost</c> de la
/// ventana porque —lo dice su comentario— una ventana siempre-arriba encima de un juego en pantalla
/// completa exclusiva lo puede sacar de ese modo. El filete es siempre-arriba y por capas
/// (<c>AllowsTransparency</c>), y se muestra exactamente ahí. La decisión es <b>aceptar el riesgo</b>,
/// por tres razones:
/// </para>
/// <list type="number">
/// <item>Sólo existe mientras hay algo urgente sin ver. Sin eso no queda nada, que es la regla; el
/// caso normal de una partida de tres horas es que el filete no aparezca nunca.</item>
/// <item>Un aviso urgente que no se ve no es un aviso. Lo que está del otro lado es un recordatorio
/// que venció o una confirmación esperando decisión, y perderla cuesta más que un parpadeo.</item>
/// <item>Lo peor que pasa es que el juego salga de pantalla completa exclusiva: molesto, reversible,
/// y no pierde nada. La píldora ya paga ese mismo precio cuatro segundos antes, con la ventana
/// principal.</item>
/// </list>
/// <para>
/// Si algún día hay que elegir de nuevo, la alternativa <b>no</b> es dibujarlo más suave: es no
/// dibujarlo y avisar por el globo de la bandeja, que es lo único que no toca el orden de ventanas.
/// Se prefirió el filete porque el globo se lo come el modo concentración y desaparece solo.
/// </para>
/// </remarks>
internal sealed class UrgentSliver : Window
{
    private const double SliverWidth = 3;
    private const double SliverHeight = 64;

    /// <summary>Opacidad base del fuente. La respiración la mueve ±0,06.</summary>
    private const double BaseOpacity = 0.85;

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly Border _body;
    private AssistantVisualState _state = AssistantVisualState.Attention;

    public UrgentSliver()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Width = SliverWidth;
        Height = SliverHeight;

        _body = new Border
        {
            CornerRadius = new CornerRadius(0, 2, 2, 0),
            Opacity = BaseOpacity
        };

        Content = _body;
        Paint(_state);
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>
    /// Lo deja contra el borde vertical más cercano al orbe, a su altura.
    /// </summary>
    /// <param name="orbBounds">Dónde está el orbe.</param>
    /// <param name="monitor">Los límites del monitor, no el área de trabajo: en pantalla
    /// completa la barra de tareas no está.</param>
    /// <param name="state">Qué estado tiñe el filete.</param>
    public void SnapNear(Rect orbBounds, Rect monitor, AssistantVisualState state)
    {
        if (state != _state)
        {
            _state = state;
            Paint(state);
        }

        var centreX = orbBounds.Left + (orbBounds.Width / 2);
        var againstLeft = centreX - monitor.Left <= monitor.Right - centreX;

        Left = againstLeft ? monitor.Left : monitor.Right - SliverWidth;

        // El fuente recorta la altura entre el 30 % y el 90 % de la pantalla: pegado al borde de
        // arriba se confunde con un artefacto de la ventana, y pegado al de abajo con la barra de
        // tareas asomando.
        var fraction = Math.Clamp(
            (orbBounds.Top + (orbBounds.Height / 2) - monitor.Top) / Math.Max(1, monitor.Height),
            0.30,
            0.90);
        Top = monitor.Top + (monitor.Height * fraction) - (SliverHeight / 2);

        // La esquina redondeada va del lado de adentro; el lado plano contra el borde.
        _body.CornerRadius = againstLeft
            ? new CornerRadius(0, 2, 2, 0)
            : new CornerRadius(2, 0, 0, 2);
    }

    /// <summary>Empieza a respirar. Idempotente: llamarla dos veces no encima dos animaciones.</summary>
    public void StartBreathing()
    {
        _body.BeginAnimation(OpacityProperty, null);
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = BaseOpacity - 0.06,
            To = BaseOpacity + 0.06,
            Duration = TimeSpan.FromMilliseconds(2400),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void Paint(AssistantVisualState state)
    {
        var color = StatePalette.For(state);
        _body.Background = Frozen(color);
        _body.Effect = new DropShadowEffect
        {
            Color = color,
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.9
        };
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Transparente al mouse y sin foco, desde el sistema operativo y no desde WPF.
    /// </summary>
    /// <remarks>
    /// <c>IsHitTestVisible</c> de WPF sólo evita que el contenido reciba el clic; la ventana lo
    /// sigue capturando y el juego de abajo no lo ve nunca. <c>WS_EX_TRANSPARENT</c> es lo que hace
    /// que el clic la atraviese.
    /// </remarks>
    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLong(nint window, int index, nint value);
}
