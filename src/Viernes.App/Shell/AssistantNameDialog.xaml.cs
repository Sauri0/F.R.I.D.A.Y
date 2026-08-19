using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Viernes.App.ViewModels;

namespace Viernes.App.Shell;

/// <summary>
/// Dónde se elige cómo se llama el asistente, que es también cómo se lo despierta.
/// </summary>
/// <remarks>
/// El instalador pregunta el nombre una vez y ahí quedaba para siempre: cambiarlo pedía volver a
/// instalar. Esta ventana es la misma pregunta, disponible cuando el usuario quiera —«así puede
/// cualquiera cambiar su nombre»—.
/// <para>
/// Muestra cómo va a quedar antes de aceptar porque lo que el usuario está eligiendo de verdad no es
/// lo que escribe: es el nombre normalizado y la frase con la que va a tener que llamarlo. Que
/// «ana maria» se convierta en «Ana María» y que la frase sea «Hola Ana María» son dos sorpresas que
/// no tienen por qué llegar después.
/// </para>
/// </remarks>
public partial class AssistantNameDialog : Window
{
    // Los mismos dos colores que el banco de micrófono: el gris de lo que sólo informa y el ámbar de
    // lo que hay que mirar. System.Drawing también tiene un Color y acá conviven los dos.
    private static readonly SolidColorBrush Calmo = new(System.Windows.Media.Color.FromRgb(0x86, 0x95, 0xA2));
    private static readonly SolidColorBrush Reparo = new(System.Windows.Media.Color.FromRgb(0xE0, 0xAE, 0x52));

    private readonly MainViewModel _viewModel;
    private bool _aplicado;

    internal AssistantNameDialog(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        Title = $"{_viewModel.AssistantName} · cómo me llamo";
        Nombre.Text = _viewModel.AssistantName;

        // El nombre actual entra seleccionado: quien abre esto viene a cambiarlo, así que escribir
        // tiene que reemplazarlo sin tener que borrarlo primero. Va en Loaded porque seleccionar
        // sobre un campo que todavía no se mostró no deja nada seleccionado.
        Loaded += (_, _) =>
        {
            Nombre.Focus();
            Nombre.SelectAll();
        };

        RefrescarVistaPrevia();
    }

    private void Nombre_TextChanged(object sender, TextChangedEventArgs e) => RefrescarVistaPrevia();

    private void RefrescarVistaPrevia()
    {
        var (valido, mensaje) = AssistantNamePreview.Describe(Nombre.Text);
        VistaPrevia.Text = mensaje;
        VistaPrevia.Foreground = valido ? Calmo : Reparo;
        BotonAceptar.IsEnabled = valido;
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e) => Close();

    private void Aceptar_Click(object sender, RoutedEventArgs e)
    {
        // Segundo clic sobre un cambio que ya se hizo y dejó un aviso: ahí el botón sólo cierra.
        if (_aplicado)
        {
            Close();
            return;
        }

        BotonAceptar.IsEnabled = false;
        Nombre.IsEnabled = false;

        // Puede tardar: si hay un turno del modelo en curso, el prompt se cambia recién cuando ese
        // turno termina, y además hay que cerrar el oído y abrir otro. Sin esto la ventana parece
        // colgada.
        VistaPrevia.Text = "Cambiando el nombre…";
        VistaPrevia.Foreground = Calmo;

        // Nada de async void en un manejador: la tarea se lanza y se le miran las fallas adentro.
        _ = AplicarAsync(Nombre.Text);
    }

    private async Task AplicarAsync(string nombre)
    {
        try
        {
            var resultado = await _viewModel.RenameAssistantAsync(nombre, CancellationToken.None);

            if (!resultado.Applied)
            {
                MostrarAviso(resultado.Problem ?? "No se pudo cambiar el nombre.");
                Nombre.IsEnabled = true;
                RefrescarVistaPrevia();
                return;
            }

            if (resultado.Warning is null)
            {
                Close();
                return;
            }

            // Se aplicó pero algo quedó a medias. Cerrar acá sería decirle que ya lo despierta con el
            // nombre nuevo cuando puede no ser cierto.
            _aplicado = true;
            Title = $"{resultado.Name} · cómo me llamo";
            VistaPrevia.Text = $"Ahora me llamo «{resultado.Name}».";
            VistaPrevia.Foreground = Calmo;
            MostrarAviso(resultado.Warning);
            BotonAceptar.Content = "Cerrar";
            BotonAceptar.IsEnabled = true;
            BotonCancelar.Visibility = Visibility.Collapsed;
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            MostrarAviso($"No se pudo cambiar el nombre: {excepcion.Message}");
            Nombre.IsEnabled = true;
            RefrescarVistaPrevia();
        }
    }

    private void MostrarAviso(string texto)
    {
        Aviso.Text = texto;
        Aviso.Visibility = Visibility.Visible;
    }
}
