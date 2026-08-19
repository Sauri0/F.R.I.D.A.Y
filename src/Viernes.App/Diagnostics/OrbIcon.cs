using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Viernes.App.Controls;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Genera el ícono de la aplicación dibujando el orbe de verdad.
/// </summary>
/// <remarks>
/// El ícono no se dibuja aparte: <b>es el mismo control que ves en pantalla</b>, en reposo, sobre
/// transparencia. Un ícono hecho a mano se despega del producto en cuanto alguien toca la paleta o
/// la silueta, y este proyecto ya tuvo un ícono de bandeja que era un círculo azul con una «V»
/// adentro y no se parecía a nada de lo que la aplicación muestra.
/// <para>
/// Corre a pedido con <c>--render-logo</c> y escribe <c>Assets\Viernes.ico</c>. No corre en el
/// arranque normal: el .ico viaja versionado en el repositorio, porque el compilador lo necesita
/// antes de que exista un ejecutable que pueda dibujarlo.
/// </para>
/// <para>
/// <b>Los tamaños chicos no son el grande achicado.</b> A 16 px el degradado entero cae en unos
/// pocos píxeles y el orbe se lee como una mancha celeste sin forma; peor, el halo lo desdibuja
/// contra una barra de tareas oscura. Por eso el halo se apaga por debajo de 32 y la silueta se
/// dibuja un poco más chica dentro del cuadro, para que el borde quede limpio y la forma se
/// reconozca.
/// </para>
/// </remarks>
internal static class OrbIcon
{
    /// <summary>Los tamaños que Windows pide, del más grande al más chico.</summary>
    /// <remarks>
    /// 256 es el de la vista «iconos extra grandes» y el que usa la tienda; 48 el del escritorio;
    /// 32 el de la barra de título y el Alt-Tab; 16 el de la barra de tareas y el explorador en
    /// modo lista. Faltando uno, Windows lo interpola del más cercano y se nota.
    /// </remarks>
    private static readonly int[] Tamanos = [256, 128, 64, 48, 32, 16];

    /// <summary>Cuánto del cuadro ocupa el cuerpo, por tamaño.</summary>
    /// <remarks>
    /// A 256 se le deja algo de aire para que el halo tenga dónde desvanecerse. A 16 el halo no se
    /// ve igual, así que el cuerpo se agranda: lo que importa a ese tamaño es que la silueta llene
    /// el cuadro y se distinga de un punto cualquiera en la barra de tareas.
    /// </remarks>
    private static double Ocupacion(int lado) => lado >= 128 ? 0.86 : lado >= 48 ? 0.90 : 0.96;

    /// <summary>Debajo de esta opacidad es halo, no cuerpo. Sirve para encontrar la silueta.</summary>
    private const byte Cuerpo = 40;

    public static async Task<string> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var destino = Path.Combine(outputDirectory, "Viernes.ico");

        var capas = new List<byte[]>(Tamanos.Length);
        var informe = new System.Text.StringBuilder();
        informe.AppendLine("ÍCONO DE LA APLICACIÓN");
        informe.AppendLine("======================");
        informe.AppendLine();

        foreach (var lado in Tamanos)
        {
            var png = await DibujarAsync(lado).ConfigureAwait(true);
            capas.Add(png);
            informe.AppendLine($"  {lado,3}×{lado,-3}  {png.Length,7} bytes");

            // Cada tamaño también se guarda suelto: mirarlos es la única forma de saber si el de 16
            // se lee, y un .ico no se puede abrir de un vistazo.
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, $"orbe-{lado}.png"), png)
                .ConfigureAwait(true);
        }

        await File.WriteAllBytesAsync(destino, Empaquetar(Tamanos, capas)).ConfigureAwait(true);

        informe.AppendLine();
        informe.AppendLine($"  {destino}");
        informe.AppendLine($"  {new FileInfo(destino).Length} bytes en total");
        return informe.ToString();
    }

    /// <summary>Dibuja el orbe en reposo a un PNG cuadrado con fondo transparente.</summary>
    private static async Task<byte[]> DibujarAsync(int lado)
    {
        // Se dibuja grande y se baja después: WPF rasteriza el degradado y el halo mucho mejor a
        // 512 que a 16, y el remuestreo de alta calidad conserva la forma en vez de aliasarla.
        const int Trabajo = 512;

        var orbe = new LiquidOrb { Width = Trabajo, Height = Trabajo };
        var host = new Window
        {
            Width = Trabajo,
            Height = Trabajo,
            Left = -6000,
            Top = -6000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = orbe
        };

        host.Show();

        // El cuerpo dibuja sobre CompositionTarget.Rendering y arranca con una animación de
        // aparición: sin esperar, la foto sale a mitad de camino.
        orbe.State = ViewModels.AssistantVisualState.Idle;
        await EsperarCuadrosAsync(40).ConfigureAwait(true);

        var grande = new RenderTargetBitmap(Trabajo, Trabajo, 96, 96, PixelFormats.Pbgra32);
        host.UpdateLayout();
        grande.Render(orbe);
        grande.Freeze();
        host.Close();

        return Reducir(grande, lado);
    }

    /// <summary>
    /// Recorta al cuerpo medido y lo lleva al tamaño pedido, centrado y llenando el cuadro.
    /// </summary>
    /// <remarks>
    /// El recorte se MIDE, no se supone. <see cref="LiquidOrb"/> dibuja en un lienzo de 70×70 y el
    /// cuerpo ocupa bastante menos: el resto es el aire que necesita para menearse y para que el
    /// halo se desvanezca sin cortarse. Bajando el lienzo entero, el orbe salía ocupando poco más de
    /// la mitad del ícono —a 32 píxeles, un puntito perdido en un cuadro vacío—.
    /// <para>
    /// Midiendo la silueta y encuadrando sobre ella, el ícono llena el cuadro a cualquier tamaño y
    /// queda centrado aunque el cuerpo esté dibujado descentrado en su lienzo, que lo está. Y sigue
    /// andando el día que alguien cambie la silueta.
    /// </para>
    /// </remarks>
    private static byte[] Reducir(BitmapSource grande, int lado)
    {
        var silueta = Medir(grande);

        var ocupa = Ocupacion(lado);
        var interior = lado * ocupa;

        // El cuadrado que se dibuja mantiene la proporción del cuerpo: un orbe más ancho que alto no
        // se estira, se centra.
        var escala = interior / Math.Max(silueta.Width, silueta.Height);
        var ancho = silueta.Width * escala;
        var alto = silueta.Height * escala;

        var dibujo = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dibujo, BitmapScalingMode.HighQuality);
        using (var contexto = dibujo.RenderOpen())
        {
            contexto.PushClip(new RectangleGeometry(new Rect(0, 0, lado, lado)));
            contexto.DrawImage(
                new CroppedBitmap(
                    grande as BitmapSource ?? throw new InvalidOperationException(),
                    new Int32Rect(
                        (int)silueta.X,
                        (int)silueta.Y,
                        (int)silueta.Width,
                        (int)silueta.Height)),
                new Rect((lado - ancho) / 2, (lado - alto) / 2, ancho, alto));
            contexto.Pop();
        }

        var destino = new RenderTargetBitmap(lado, lado, 96, 96, PixelFormats.Pbgra32);
        destino.Render(dibujo);
        destino.Freeze();

        using var memoria = new MemoryStream();
        var codificador = new PngBitmapEncoder();
        codificador.Frames.Add(BitmapFrame.Create(destino));
        codificador.Save(memoria);
        return memoria.ToArray();
    }

    /// <summary>
    /// Dónde está el cuerpo dentro de la imagen, mirando la opacidad píxel por píxel.
    /// </summary>
    /// <remarks>
    /// Se busca el cuerpo y no todo lo que tenga alfa: el halo llega casi al borde del lienzo con
    /// opacidades de unos pocos puntos, así que recortar sobre «alfa mayor que cero» no recortaría
    /// nada. El umbral separa lo que es cuerpo de lo que es luz alrededor.
    /// <para>
    /// Al recorte medido se le devuelve un poco de aire proporcional, para que el halo no quede
    /// cortado a ras del borde —que se ve como un aro—.
    /// </para>
    /// </remarks>
    private static Rect Medir(BitmapSource imagen)
    {
        var ancho = imagen.PixelWidth;
        var alto = imagen.PixelHeight;
        var paso = ancho * 4;
        var pixeles = new byte[paso * alto];
        imagen.CopyPixels(pixeles, paso, 0);

        int izquierda = ancho, arriba = alto, derecha = -1, abajo = -1;

        for (var y = 0; y < alto; y++)
        {
            for (var x = 0; x < ancho; x++)
            {
                if (pixeles[(y * paso) + (x * 4) + 3] < Cuerpo)
                {
                    continue;
                }

                if (x < izquierda) { izquierda = x; }
                if (x > derecha) { derecha = x; }
                if (y < arriba) { arriba = y; }
                if (y > abajo) { abajo = y; }
            }
        }

        if (derecha < 0)
        {
            // Nada opaco: no hay nada que encuadrar y devolver la imagen entera es lo único honesto.
            return new Rect(0, 0, ancho, alto);
        }

        var w = derecha - izquierda + 1;
        var h = abajo - arriba + 1;
        var aire = Math.Max(w, h) * 0.06;

        var x0 = Math.Max(0, izquierda - aire);
        var y0 = Math.Max(0, arriba - aire);
        var x1 = Math.Min(ancho, derecha + 1 + aire);
        var y1 = Math.Min(alto, abajo + 1 + aire);

        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Arma el .ico con los PNG adentro.
    /// </summary>
    /// <remarks>
    /// Un .ico es una tabla de contenidos y los datos pegados atrás. Windows acepta PNG adentro
    /// desde Vista, así que no hace falta convertir a BMP con máscara —que además duplicaría el
    /// tamaño y obligaría a escribir la máscara de transparencia a mano—.
    /// <para>
    /// El 256 se escribe como 0 en el byte del tamaño, que es lo que manda el formato: un byte no
    /// llega a 256 y ese cero significa «doscientos cincuenta y seis», no «cero».
    /// </para>
    /// </remarks>
    private static byte[] Empaquetar(IReadOnlyList<int> lados, IReadOnlyList<byte[]> capas)
    {
        using var salida = new MemoryStream();
        using var escritor = new BinaryWriter(salida);

        escritor.Write((short)0);              // reservado
        escritor.Write((short)1);              // 1 = ícono
        escritor.Write((short)lados.Count);

        var offset = 6 + (16 * lados.Count);
        for (var i = 0; i < lados.Count; i++)
        {
            escritor.Write((byte)(lados[i] >= 256 ? 0 : lados[i]));
            escritor.Write((byte)(lados[i] >= 256 ? 0 : lados[i]));
            escritor.Write((byte)0);           // colores de la paleta: 0 = sin paleta
            escritor.Write((byte)0);           // reservado
            escritor.Write((short)1);          // planos
            escritor.Write((short)32);         // bits por píxel
            escritor.Write(capas[i].Length);
            escritor.Write(offset);
            offset += capas[i].Length;
        }

        foreach (var capa in capas)
        {
            escritor.Write(capa);
        }

        escritor.Flush();
        return salida.ToArray();
    }

    /// <summary>Deja pasar unos cuadros de verdad, para que el cuerpo llegue a su estado quieto.</summary>
    private static async Task EsperarCuadrosAsync(int cuantos)
    {
        for (var i = 0; i < cuantos; i++)
        {
            var listo = new TaskCompletionSource();
            void Cuadro(object? emisor, EventArgs argumentos)
            {
                CompositionTarget.Rendering -= Cuadro;
                listo.SetResult();
            }

            CompositionTarget.Rendering += Cuadro;
            await listo.Task.ConfigureAwait(true);
        }
    }
}
