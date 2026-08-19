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

    /// <summary>Lo mínimo para que el modelo de vista exista: sólo el nombre importa acá.</summary>
    private sealed class RuntimeDeMentira(string name) : IAssistantRuntime
    {
        // Con cuerpo vacío y no como campo: el modelo de vista se engancha en su constructor, acá
        // nadie los dispara, y un evento declarado y nunca disparado es una advertencia.
        public event EventHandler<AssistantRuntimeUpdate>? Updated
        {
            add { }
            remove { }
        }

        public event EventHandler<ShellActivationRequest>? ActivationRequested
        {
            add { }
            remove { }
        }

        public string AssistantName { get; private set; } = name;

        public bool IsMuted { get; set; }

        public bool IsCloudConfigured => false;

        public bool IsWakeWordEnabled => false;

        public bool IsListeningWhileHidden => false;

        public OrbShape OrbShape => OrbShape.Gota;

        public bool FollowsActiveMonitor => false;

        public bool IsWakeWordDemo => true;

        public string RecognitionProviderName => "de mentira";

        public bool IsConversationActive => false;

        public bool HasSpendAuthorization => false;

        public Task<AssistantRenameResult> SetAssistantNameAsync(
            string? nombre,
            CancellationToken cancellationToken)
        {
            this.AssistantName = nombre ?? this.AssistantName;
            return Task.FromResult(new AssistantRenameResult(true, this.AssistantName));
        }

        public Task SetOrbShapeAsync(OrbShape shape, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetFollowActiveMonitorAsync(bool follow, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> SendAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        public Task StartPushToTalkAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopPushToTalkAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CancelSpeechAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetWakeWordEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetListenWhileHiddenAsync(bool enabled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StartConversationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndConversationAsync(string reason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EndConversationAsync(string reason, bool quiet, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Panic()
        {
        }

        public Task ConfirmPendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void DismissPending()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
