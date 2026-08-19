namespace Viernes.App.Controls;

/// <summary>Los tres signos a los que puede morfear el orbe.</summary>
/// <remarks>
/// Son tres y no seis a propósito. «¡Listo!» tiene un tilde, «urgente» tiene un signo de admiración
/// y «una pregunta» tiene uno de interrogación porque los tres son <em>lecturas</em>: se entienden
/// sin leer nada. «No salió», «hola» y «hasta luego» no tienen signo equivalente, y forzarles uno
/// habría sido pedirle al usuario que aprenda un idioma nuevo.
/// </remarks>
internal enum OrbGlyphKind
{
    /// <summary>Sin signo: el cuerpo queda como está.</summary>
    None,

    /// <summary>Signo de interrogación. Celeste.</summary>
    Ask,

    /// <summary>Signo de admiración. Amarillo.</summary>
    Bang,

    /// <summary>Tilde de verificación. Del color del logro.</summary>
    Check
}

/// <summary>Un punto del signo: dónde va y qué grosor tiene ahí.</summary>
/// <remarks>
/// Las coordenadas están normalizadas al radio del signo, así que el mismo trazo sirve a 108 px y a
/// 300. El grosor variable es lo que hace que el trazo se lea como escrito y no como un caño.
/// </remarks>
internal readonly record struct OrbGlyphPoint(double X, double Y, double Radius, bool IsDot);

/// <summary>
/// Los tres signos, punto por punto.
/// </summary>
/// <remarks>
/// Se calculan una sola vez y se guardan: son constantes y armarlos cuadro a cuadro sería tirar
/// trabajo. Salen tal cual de <c>static GLY()</c> del boceto.
/// </remarks>
internal static class OrbGlyph
{
    private static readonly OrbGlyphPoint[] AskPoints = BuildAsk();
    private static readonly OrbGlyphPoint[] BangPoints = BuildBang();
    private static readonly OrbGlyphPoint[] CheckPoints = BuildCheck();
    private static readonly OrbGlyphPoint[] Empty = [];

    /// <summary>Los puntos de un signo, o una lista vacía si no hay ninguno.</summary>
    internal static OrbGlyphPoint[] Points(OrbGlyphKind kind) => kind switch
    {
        OrbGlyphKind.Ask => AskPoints,
        OrbGlyphKind.Bang => BangPoints,
        OrbGlyphKind.Check => CheckPoints,
        _ => Empty
    };

    /// <summary>
    /// El garabato del interrogante: once vértices interpolados de a tres, y el punto abajo.
    /// </summary>
    /// <remarks>
    /// El grosor baja a lo largo del trazo —de 0,163 a 0,121— porque un interrogante escrito a mano
    /// adelgaza hacia la punta. Es el detalle que lo separa de una tipografía.
    /// </remarks>
    private static OrbGlyphPoint[] BuildAsk()
    {
        double[][] spine =
        [
            [-0.30, -0.26], [-0.29, -0.42], [-0.19, -0.55], [-0.02, -0.60], [0.16, -0.54],
            [0.27, -0.40], [0.26, -0.23], [0.15, -0.09], [0.05, 0.04], [0.01, 0.17], [0.00, 0.29]
        ];

        var points = new List<OrbGlyphPoint>((spine.Length * 3) + 2);
        for (var i = 0; i < spine.Length - 1; i++)
        {
            for (var s = 0; s < 3; s++)
            {
                var t = s / 3.0;
                var u = (i + t) / (spine.Length - 1);
                points.Add(new OrbGlyphPoint(
                    spine[i][0] + ((spine[i + 1][0] - spine[i][0]) * t),
                    spine[i][1] + ((spine[i + 1][1] - spine[i][1]) * t),
                    0.163 - (0.042 * u),
                    false));
            }
        }

        var last = spine[^1];
        points.Add(new OrbGlyphPoint(last[0], last[1], 0.121, false));
        points.Add(new OrbGlyphPoint(0, 0.58, 0.170, true));
        return [.. points];
    }

    /// <summary>La admiración: una barra que adelgaza hacia abajo, y el punto.</summary>
    private static OrbGlyphPoint[] BuildBang()
    {
        var points = new List<OrbGlyphPoint>(21);
        for (var i = 0; i <= 19; i++)
        {
            var t = i / 19.0;
            points.Add(new OrbGlyphPoint(0, -0.63 + (t * 0.86), 0.185 - (t * 0.062), false));
        }

        points.Add(new OrbGlyphPoint(0, 0.62, 0.172, true));
        return [.. points];
    }

    /// <summary>
    /// El tilde: el trazo corto engorda, el largo adelgaza.
    /// </summary>
    /// <remarks>
    /// Es al revés de lo que uno haría por simetría, y es lo correcto: al escribir un tilde la
    /// mano apoya al bajar y levanta al subir. Invertirlo lo vuelve un pájaro.
    /// </remarks>
    private static OrbGlyphPoint[] BuildCheck()
    {
        var points = new List<OrbGlyphPoint>(21);
        for (var i = 0; i <= 7; i++)
        {
            var t = i / 7.0;
            points.Add(new OrbGlyphPoint(-0.44 + (t * 0.34), -0.02 + (t * 0.36), 0.128 + (t * 0.030), false));
        }

        for (var i = 1; i <= 13; i++)
        {
            var t = i / 13.0;
            points.Add(new OrbGlyphPoint(-0.10 + (t * 0.56), 0.34 - (t * 0.78), 0.158 - (t * 0.048), false));
        }

        return [.. points];
    }
}
