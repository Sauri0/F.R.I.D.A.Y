using System.Text;
using Viernes.App.Shell;
using Xunit;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point y Rect existen dos veces.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// El rebote tiene que verse igual a 30, a 60, a 144 y a 180 cuadros por segundo.
/// </summary>
/// <remarks>
/// El comentario de <see cref="OrbMotion"/> prometía «pasos fijos de 1/120 s y no el <c>dt</c> del
/// cuadro». Eso es cierto sólo por debajo de 120 Hz: el bucle de subpasos toma
/// <c>h = min(SubStep, restante)</c>, así que a 180 Hz —donde el cuadro dura 5,56 ms, menos que los
/// 8,33 del subpaso— corre <b>un</b> paso y ese paso es el del cuadro. El subpaso es un techo, no un
/// piso.
/// <para>
/// Que sea un techo y no un piso es lo correcto: hacerlo piso obligaría a la física a avanzar a 120
/// mientras la ventana se dibuja a 180, y ahí el orbe se movería en dos de cada tres cuadros. Es
/// exactamente el desfase que hacía ver a la nube a los tirones. Pero entonces hay que <em>medir</em>
/// que un paso más chico no cambie el rebote, en vez de suponerlo.
/// </para>
/// </remarks>
public class OrbBounceRateTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1040);

    /// <summary>Altura del pique medida a una frecuencia dada, en píxeles sobre el piso.</summary>
    private static double BounceHeight(double hz)
    {
        var bounds = ShellLayout.OrbBounds(Screen);
        var motion = new OrbMotion();
        var start = new Point(bounds.Left + 400, bounds.Bottom - 300);
        motion.Teleport(start);

        // Hacia abajo, fuerte: 2000 px/s es del orden de lo que da soltarlo con ganas.
        motion.Launch(start, kick: 0, lift: 2000);

        // Se anota el recorrido entero y después se busca el pique, en vez de detectar el choque
        // por cuadro. A 30 Hz el cuadro se parte en cuatro subpasos y el rebote entero puede pasar
        // adentro de uno: al terminar el cuadro el orbe ya viene subiendo y un vigía por cuadro no
        // ve nunca el instante del contacto. Buscar el punto más bajo del recorrido no depende de
        // dónde caiga el corte de los cuadros, que es justo lo que se está midiendo.
        var dt = 1.0 / hz;
        var track = new List<double>();
        for (var t = 0.0; t < 2.0; t += dt)
        {
            motion.Step(dt, bounds);
            track.Add(motion.Position.Y);
        }

        var floor = track.Max();
        var apex = double.MaxValue;
        for (var i = track.IndexOf(floor); i < track.Count; i++)
        {
            apex = Math.Min(apex, track[i]);
        }

        return floor - apex;
    }

    [Fact]
    public void ElPiqueMideLoMismoATodaFrecuencia()
    {
        double[] rates = [30, 60, 90, 120, 144, 180, 240];
        var heights = new double[rates.Length];
        var detalle = new StringBuilder();

        for (var i = 0; i < rates.Length; i++)
        {
            heights[i] = BounceHeight(rates[i]);
            detalle.Append($"{rates[i]:0} Hz → {heights[i]:0.0} px; ");
        }

        var spread = heights.Max() - heights.Min();

        // Medido acá, corriendo esta misma prueba:
        //   30 Hz → 197,6 px   60 Hz → 197,6 px   90 Hz → 203,5 px   120 Hz → 202,3 px
        //   144 Hz → 200,6 px  180 Hz → 203,3 px  240 Hz → 201,3 px
        // O sea 5,9 px de dispersión sobre un pique de 200: un 3 %. Y no es ruido de muestreo —cerca
        // del pique el orbe se mueve a 44 px/s y un cuadro de 30 Hz lo muestrea cada 1,5 px—: 30 y
        // 60 dan EXACTAMENTE lo mismo porque sus cuadros son múltiplos enteros del subpaso de
        // 1/120, y las frecuencias que dejan resto se separan un poco. Es el precio de que el
        // subpaso sea un techo y no un piso, y es el precio correcto: hacerlo piso pondría la física
        // a 120 con la ventana dibujando a 180, y ahí el orbe se movería en dos de cada tres
        // cuadros. Un 3 % en la altura de un pique no lo ve nadie; una ventana que se mueve en dos
        // de cada tres cuadros la vio el usuario y por eso existe este trabajo.
        //
        // Los 8 px de tolerancia son el doble de lo medido, para que no se rompa por un bit.
        Assert.True(spread < 8, $"el pique cambia con la frecuencia: {detalle} · dispersión {spread:0.0} px");
    }

    /// <summary>
    /// A 180 Hz el bucle de subpasos corre <b>un</b> paso por cuadro, y ese paso es el del cuadro.
    /// </summary>
    /// <remarks>
    /// No se puede comprobar contando subpasos desde afuera, así que se comprueba por su
    /// consecuencia: con un <c>dt</c> menor que el subpaso, el desplazamiento del cuadro es
    /// exactamente <c>v · dt</c>, que es lo que da un solo paso. Con dos o más pasos, el rozamiento
    /// se aplicaría dos veces y el número saldría más chico.
    /// </remarks>
    [Fact]
    public void ADoscientosHzCorreUnSoloSubpasoPorCuadro()
    {
        var bounds = ShellLayout.OrbBounds(Screen);
        var motion = new OrbMotion();
        var start = new Point(bounds.Left + 400, bounds.Top + 300);
        motion.Teleport(start);
        motion.Launch(start, kick: 0, lift: 1000);

        var dt = 1.0 / 180;
        motion.Step(dt, bounds);

        // StepFlight mueve primero y frena después, así que el primer cuadro se corre v · dt exacto.
        Assert.Equal(start.Y + (1000 * dt), motion.Position.Y, 6);
    }
}
