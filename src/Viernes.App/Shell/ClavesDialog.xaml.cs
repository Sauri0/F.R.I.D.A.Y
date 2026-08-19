using System.Windows;
using System.Windows.Media;
using Viernes.App.ViewModels;

namespace Viernes.App.Shell;

/// <summary>
/// Dónde se ponen y se cambian las dos claves, sin tener que abrir un archivo ni aprender <c>setx</c>.
/// </summary>
/// <remarks>
/// Hasta acá las claves se ponían sólo en el instalador, y cambiarlas pedía volver a correrlo o
/// editar <c>claves.json</c> a mano. El usuario lo pidió así: «que el menú del clic derecho tenga una
/// opción para keys así podés pegar o cambiar las dos que tiene Viernes».
/// <para>
/// <b>Esto invierte una regla que este proyecto tenía escrita</b> —la clave de OpenRouter nunca en un
/// campo de la interfaz— y hay que decir qué se conserva de lo que esa regla protegía, porque el
/// motivo sigue siendo válido:
/// </para>
/// <list type="bullet">
///   <item>Los dos campos son <c>PasswordBox</c>. No hay forma de que el valor se dibuje en pantalla,
///   ni siquiera un instante, ni siquiera con el foco puesto.</item>
///   <item><b>Lo que ya está guardado no se muestra nunca</b>, ni entero ni enmascarado ni con los
///   últimos cuatro caracteres. Sólo se dice si está puesta o no. Una clave a medias en pantalla es
///   una clave en pantalla.</item>
///   <item>La de OpenRouter sigue yendo a las variables de entorno de la cuenta de Windows y
///   <b>no a ningún archivo</b>. Lo único que cambió es por dónde entra.</item>
///   <item>Ningún valor entra en la bitácora, ni en un mensaje de error, ni en una excepción. Lo que
///   se anota es cuál de las dos cambió y nada más.</item>
///   <item>Los campos se vacían al cerrar la ventana.</item>
/// </list>
/// <para>
/// Y una que no es de seguridad sino de honestidad: <b>los campos arrancan vacíos aunque haya claves
/// puestas</b>. Un campo con puntitos adentro que no son la clave real invita a borrarlos y
/// reescribir, o peor, hace creer que se guardó algo que no se tocó. Vacío significa «no estoy
/// cambiando ésta».
/// </para>
/// </remarks>
public partial class ClavesDialog : Window
{
    private static readonly SolidColorBrush Puesta = new(System.Windows.Media.Color.FromRgb(0x7F, 0xC8, 0xA9));
    private static readonly SolidColorBrush Falta = new(System.Windows.Media.Color.FromRgb(0xE0, 0xAE, 0x52));

    private readonly MainViewModel _viewModel;

    internal ClavesDialog(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        Title = $"{_viewModel.AssistantName} · mis claves";
        Refrescar();

        Loaded += (_, _) => ClaveRouter.Focus();

        // Los campos no sobreviven a la ventana. Un PasswordBox guarda el texto en memoria
        // administrada y no hay forma de garantizar que se borre, pero dejarlo cargado sí es una
        // decisión, y es la equivocada.
        Closed += (_, _) =>
        {
            ClaveRouter.Clear();
            ClaveGoogle.Clear();
        };
    }

    /// <summary>Dice qué hay puesto. Nunca qué es.</summary>
    private void Refrescar()
    {
        var estado = _viewModel.DescribeCredentials();

        EstadoRouter.Text = estado.HasOpenRouter
            ? "Está puesta. Pegá otra para reemplazarla."
            : "No tenés ninguna puesta. Sin ella arranco pero no puedo pensar.";
        EstadoRouter.Foreground = estado.HasOpenRouter ? Puesta : Falta;

        EstadoGoogle.Text = estado.HasGoogle
            ? "Está puesta. Pegá otra para reemplazarla."
            : "No tenés ninguna puesta. Es opcional: sin ella ando por el camino de siempre.";
        EstadoGoogle.Foreground = estado.HasGoogle ? Puesta : Falta;

        BotonBorrarRouter.IsEnabled = estado.HasOpenRouter;
        BotonBorrarGoogle.IsEnabled = estado.HasGoogle;

        if (estado.OpenRouterShadowed)
        {
            // El archivo le gana al entorno, así que una clave vieja en claves.json haría que la
            // que se acaba de guardar no se use nunca. Callarlo sería dejar al usuario cambiando
            // una clave que no es la que corre.
            MostrarAviso(
                "Tenés una clave de OpenRouter también en el archivo de claves, y ésa es la que se " +
                "usa. Lo que guardes acá va al entorno y no va a tener efecto hasta que la saques " +
                "del archivo.");
        }
    }

    private void Campo_Cambio(object sender, RoutedEventArgs e) =>
        BotonGuardar.IsEnabled =
            ClaveRouter.SecurePassword.Length > 0 || ClaveGoogle.SecurePassword.Length > 0;

    private void Cancelar_Click(object sender, RoutedEventArgs e) => Close();

    private void BorrarRouter_Click(object sender, RoutedEventArgs e) => _ = AplicarAsync(borrarRouter: true);

    private void BorrarGoogle_Click(object sender, RoutedEventArgs e) => _ = AplicarAsync(borrarGoogle: true);

    private void Guardar_Click(object sender, RoutedEventArgs e) => _ = AplicarAsync();

    private async Task AplicarAsync(bool borrarRouter = false, bool borrarGoogle = false)
    {
        Habilitar(false);

        try
        {
            // Un campo vacío significa «no toques ésta», no «borrala». Borrar tiene su propio botón,
            // porque es una acción distinta y merece un gesto distinto.
            var router = borrarRouter ? string.Empty : Vacio(ClaveRouter.Password);
            var google = borrarGoogle ? string.Empty : Vacio(ClaveGoogle.Password);

            var resultado = await _viewModel.SetCredentialsAsync(router, google, CancellationToken.None);

            ClaveRouter.Clear();
            ClaveGoogle.Clear();
            Refrescar();

            if (resultado.Problem is not null)
            {
                MostrarAviso(resultado.Problem);
                Habilitar(true);
                return;
            }

            if (resultado.Warning is not null)
            {
                MostrarAviso(resultado.Warning);
                Habilitar(true);
                BotonGuardar.IsEnabled = false;
                return;
            }

            Close();
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            // El tipo de la excepción y nada más: un mensaje de excepción puede arrastrar lo que se
            // estaba escribiendo, y lo que se estaba escribiendo es una clave.
            MostrarAviso($"No se pudieron guardar las claves ({excepcion.GetType().Name}).");
            Habilitar(true);
        }
    }

    private static string? Vacio(string valor) => string.IsNullOrWhiteSpace(valor) ? null : valor;

    private void Habilitar(bool puede)
    {
        BotonGuardar.IsEnabled = puede && (ClaveRouter.SecurePassword.Length > 0 || ClaveGoogle.SecurePassword.Length > 0);
        ClaveRouter.IsEnabled = puede;
        ClaveGoogle.IsEnabled = puede;
        BotonBorrarRouter.IsEnabled = puede && BotonBorrarRouter.IsEnabled;
        BotonBorrarGoogle.IsEnabled = puede && BotonBorrarGoogle.IsEnabled;
    }

    private void MostrarAviso(string texto)
    {
        Aviso.Text = texto;
        Aviso.Visibility = Visibility.Visible;
    }
}
