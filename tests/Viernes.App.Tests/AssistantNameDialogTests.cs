using Viernes.App.Controls;
using Viernes.App.Services;
using Viernes.App.Shell;
using Viernes.App.ViewModels;
using Xunit;

namespace Viernes.App.Tests;

/// <summary>
/// La ventana del nombre, armada de verdad: que el XAML cargue y que Aceptar obedezca.
/// </summary>
/// <remarks>
/// El compilador de marcado atrapa un tipo mal escrito, pero no atrapa un <c>x:Name</c> que dejó de
/// existir ni un recurso que no está: eso se ve recién al abrirla, y esta ventana se abre desde un
/// menú que nadie mira todos los días. Construirla acá es lo más parecido a abrirla que se puede
/// hacer sin escritorio.
/// <para>
/// En un hilo STA propio porque WPF no construye una ventana en otro lado, y xunit corre las pruebas
/// en hilos del grupo, que son MTA.
/// </para>
/// </remarks>
public class AssistantNameDialogTests
{
    [Fact]
    public void SeAbreConElNombreActualEscritoYListoParaReemplazar()
    {
        var (nombre, aceptarActivo) = EnSuHilo(() =>
        {
            var dialog = new AssistantNameDialog(new MainViewModel(new RuntimeDeMentira("Ana")));
            return (dialog.Nombre.Text, dialog.BotonAceptar.IsEnabled);
        });

        Assert.Equal("Ana", nombre);
        Assert.True(aceptarActivo);
    }

    [Fact]
    public void AceptarSeApagaYExplicaCuandoElNombreNoSirve()
    {
        var (aceptarActivo, vistaPrevia) = EnSuHilo(() =>
        {
            var dialog = new AssistantNameDialog(new MainViewModel(new RuntimeDeMentira("Ana")));
            dialog.Nombre.Text = "R2D2";
            return (dialog.BotonAceptar.IsEnabled, dialog.VistaPrevia.Text);
        });

        Assert.False(aceptarActivo);
        Assert.Contains("números", vistaPrevia, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaVistaPreviaMuestraLaFraseQueVaADespertarlo()
    {
        var vistaPrevia = EnSuHilo(() =>
        {
            var dialog = new AssistantNameDialog(new MainViewModel(new RuntimeDeMentira("Viernes")));
            dialog.Nombre.Text = "ana maria";
            return dialog.VistaPrevia.Text;
        });

        Assert.Contains("Ana Maria", vistaPrevia, StringComparison.Ordinal);
        Assert.Contains("Hola Ana Maria", vistaPrevia, StringComparison.Ordinal);
    }

    private static T EnSuHilo<T>(Func<T> trabajo)
    {
        T resultado = default!;
        Exception? falla = null;

        var hilo = new Thread(() =>
        {
            try
            {
                resultado = trabajo();
            }
            catch (Exception exception)
            {
                falla = exception;
            }
        });
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        hilo.Join();

        // Sin esto una ventana que no carga se ve como un valor por defecto y la prueba pasa.
        if (falla is not null)
        {
            throw new InvalidOperationException("La ventana del nombre no se pudo armar.", falla);
        }

        return resultado;
    }
}
