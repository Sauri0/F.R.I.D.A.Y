using System.Collections.ObjectModel;
using System.Windows.Input;
using Viernes.App.Infrastructure;
using Viernes.App.Shell;
using Viernes.Core.Autonomy;
using Viernes.Core.Learning;

namespace Viernes.App.ViewModels;

/// <summary>
/// Lo que muestran los seis desplegables que la referencia dejó abiertos.
/// </summary>
/// <remarks>
/// Vive aparte de <see cref="MainViewModel"/> y no adentro porque no comparte nada con él: el modelo
/// de vista principal habla con el runtime y refleja el turno en curso; esto lee archivos cuando el
/// usuario abre un panel. Meterlo ahí sumaba treinta propiedades a una clase que ya tiene cuarenta y
/// que se lee entera cada vez que alguien toca el orbe.
/// <para>
/// Todo se lee al abrir el panel, no en un reloj de fondo: son seis pantallas que se miran unos
/// segundos, y un temporizador releyendo el disco para nadie es exactamente el tipo de costo que
/// este proyecto viene sacando.
/// </para>
/// </remarks>
internal sealed class PanelBoard : ObservableObject
{
    private readonly Action _requestClose;
    private CancellationTokenSource? _loading;
    private PendingQuestion? _question;
    private string _answer = string.Empty;
    private SpendSummary? _spend;
    private string _missionsNote = string.Empty;
    private string _projectsNote = string.Empty;
    private string _permissionsNote = string.Empty;
    private string _memoryNote = string.Empty;
    private string _questionNote = string.Empty;
    private string _spendNote = string.Empty;
    private string _outcome = string.Empty;
    private string _more = string.Empty;

    public PanelBoard(Action requestClose)
    {
        _requestClose = requestClose;
        AnswerCommand = new AsyncRelayCommand(AnswerAsync, CanAnswer, ReportFailure);
    }

    /// <summary>Las misiones vivas.</summary>
    public ObservableCollection<PanelRow> Missions { get; } = [];

    /// <summary>Las sesiones de Claude Code que el vigía encontró.</summary>
    public ObservableCollection<PanelRow> Projects { get; } = [];

    /// <summary>Los permisos aprendidos, cada uno con su control para cambiarlo.</summary>
    public ObservableCollection<AutonomyRow> Permissions { get; } = [];

    /// <summary>La memoria: primero lo que espera decisión, después lo que ya es un hecho.</summary>
    public ObservableCollection<MemoryRow> Memory { get; } = [];

    /// <summary>La pregunta pendiente, o <c>null</c> si no hay ninguna.</summary>
    public PendingQuestion? Question
    {
        get => _question;
        private set
        {
            if (SetProperty(ref _question, value))
            {
                OnPropertyChanged(nameof(HasQuestion));
                OnPropertyChanged(nameof(HasNoQuestion));
                ((AsyncRelayCommand)AnswerCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Si hay algo que contestar.</summary>
    public bool HasQuestion => Question is not null;

    /// <summary>Lo contrario. Existe para el XAML, que no sabe negar.</summary>
    public bool HasNoQuestion => Question is null;

    /// <summary>Lo que el usuario está escribiendo como respuesta.</summary>
    public string Answer
    {
        get => _answer;
        set
        {
            if (SetProperty(ref _answer, value))
            {
                ((AsyncRelayCommand)AnswerCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Contesta la pregunta y destraba la misión.</summary>
    public ICommand AnswerCommand { get; }

    /// <summary>Lo gastado hoy y en el mes.</summary>
    public SpendSummary? Spend
    {
        get => _spend;
        private set => SetProperty(ref _spend, value);
    }

    /// <summary>Qué decir cuando no hay misiones que mostrar.</summary>
    public string MissionsNote
    {
        get => _missionsNote;
        private set => SetProperty(ref _missionsNote, value);
    }

    /// <summary>Qué decir cuando no hay sesiones de Claude Code que mostrar.</summary>
    public string ProjectsNote
    {
        get => _projectsNote;
        private set => SetProperty(ref _projectsNote, value);
    }

    /// <summary>Qué decir cuando no hay permisos guardados.</summary>
    public string PermissionsNote
    {
        get => _permissionsNote;
        private set => SetProperty(ref _permissionsNote, value);
    }

    /// <summary>Qué decir cuando la memoria no tiene nada.</summary>
    public string MemoryNote
    {
        get => _memoryNote;
        private set => SetProperty(ref _memoryNote, value);
    }

    /// <summary>
    /// Qué pasó la última vez que el usuario tocó algo en estos paneles.
    /// </summary>
    /// <remarks>
    /// Va aparte del texto de lista vacía y no mezclado con él: son dos cosas distintas y las dos
    /// tienen que poder verse a la vez. Un panel con tres filas que además acaba de guardar algo no
    /// tiene dónde decirlo si la única línea disponible es la que aparece cuando no hay filas.
    /// </remarks>
    public string Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    /// <summary>Cuántas filas quedaron sin dibujar en el panel abierto. Vacío si entraron todas.</summary>
    /// <remarks>
    /// Una sola para los cuatro paneles de lista porque nunca hay dos abiertos: el vidrio es uno solo
    /// y cambia de forma. Cuatro propiedades diciendo lo mismo serían tres que alguien va a olvidarse
    /// de limpiar.
    /// </remarks>
    public string More
    {
        get => _more;
        private set => SetProperty(ref _more, value);
    }

    /// <summary>Qué decir cuando no hay ninguna pregunta esperando.</summary>
    public string QuestionNote
    {
        get => _questionNote;
        private set => SetProperty(ref _questionNote, value);
    }

    /// <summary>El pie del panel de gasto: contra qué presupuesto se está midiendo.</summary>
    public string SpendNote
    {
        get => _spendNote;
        private set => SetProperty(ref _spendNote, value);
    }

    /// <summary>
    /// Lee de nuevo lo que muestra el desplegable que se está abriendo.
    /// </summary>
    /// <remarks>
    /// Devuelve enseguida: la lectura sigue por su cuenta y llena las listas cuando termina. Los seis
    /// paneles leen archivos, y hacerlo antes de dejar abrir el panel se ve como el orbe trabándose.
    /// <para>
    /// Cancela la lectura anterior. Abrir dos paneles seguidos rápido dejaba dos lecturas en vuelo y
    /// la que terminaba última pisaba a la otra, así que el panel se llenaba con las filas del
    /// anterior.
    /// </para>
    /// </remarks>
    public void Refresh(PanelKind kind)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        Outcome = string.Empty;
        More = string.Empty;

        if (kind is not (PanelKind.Misiones or PanelKind.Pregunta or PanelKind.Proyectos
            or PanelKind.Autonomia or PanelKind.Aprendido or PanelKind.Consumo))
        {
            return;
        }

        _loading = new CancellationTokenSource();
        _ = RefreshAsync(kind, _loading.Token);
    }

    /// <summary>
    /// La lectura.
    /// </summary>
    /// <remarks>
    /// Sin <c>ConfigureAwait(false)</c> a propósito: arranca en el hilo de la interfaz y las
    /// continuaciones tocan <see cref="ObservableCollection{T}"/>, que sólo se puede tocar desde ahí.
    /// </remarks>
    private async Task RefreshAsync(PanelKind kind, CancellationToken cancellationToken)
    {
        try
        {
            switch (kind)
            {
                case PanelKind.Misiones:
                    MissionsNote = "Leyendo…";
                    Fill(Missions, await PanelFeed.MissionsAsync(cancellationToken));
                    MissionsNote = "No hay ninguna misión abierta.";
                    break;

                case PanelKind.Pregunta:
                    QuestionNote = "Leyendo…";
                    Question = await PanelFeed.QuestionAsync(cancellationToken);
                    QuestionNote = "No te preguntó nada que esté esperando respuesta.";
                    break;

                case PanelKind.Proyectos:
                    ProjectsNote = "Leyendo…";
                    Fill(Projects, await PanelFeed.ProjectsAsync(cancellationToken));
                    ProjectsNote = "No hay ninguna sesión de Claude Code para mirar.";
                    break;

                case PanelKind.Autonomia:
                    PermissionsNote = "Leyendo…";
                    FillPermissions(await PanelFeed.AutonomyAsync(cancellationToken));
                    PermissionsNote = "Todavía no le diste ningún permiso especial. " +
                        "Leer, buscar y redactar los hace sin preguntar; mandar, publicar, borrar y " +
                        "pagar los pregunta siempre.";
                    break;

                case PanelKind.Aprendido:
                    MemoryNote = "Leyendo…";
                    FillMemory(await PanelFeed.MemoryAsync(cancellationToken));
                    break;

                case PanelKind.Consumo:
                    SpendNote = "Leyendo…";
                    Spend = await PanelFeed.SpendAsync(cancellationToken);
                    SpendNote = DescribeBudget(Spend);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Se abrió otro panel encima. La lectura que valga es la del que quedó.
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    /// <summary>
    /// Un panel que no pudo leer lo dice.
    /// </summary>
    /// <remarks>
    /// Deliberadamente ruidoso en la pantalla y silencioso en el proceso: dejar la lista vacía sin
    /// decir nada se lee como «no hay nada», que es una afirmación distinta y puede ser falsa.
    /// </remarks>
    private void ReportFailure(Exception exception)
    {
        var text = $"No pude leerlo: {exception.Message}";
        MissionsNote = text;
        ProjectsNote = text;
        PermissionsNote = text;
        MemoryNote = text;
        QuestionNote = text;
        SpendNote = text;
    }

    /// <summary>
    /// Cuántas filas se dibujan.
    /// </summary>
    /// <remarks>
    /// Tres, y no las que haya. Los cuatro paneles de lista miden entre 196 y 206 de alto: con
    /// encabezado, pie y filas de dos líneas —lo que dice y por qué—, entran tres. Cortar en tres y
    /// contar el resto en el pie es honesto; dibujar seis y que WPF recorte la última por la mitad,
    /// no.
    /// </remarks>
    private const int VisibleRows = 3;

    private void Fill(ObservableCollection<PanelRow> target, IReadOnlyList<PanelRow> rows)
    {
        target.Clear();
        foreach (var row in rows.Take(VisibleRows))
        {
            target.Add(row);
        }

        More = Rest(rows.Count);
    }

    private void FillPermissions(IReadOnlyList<AutonomyRule> rules)
    {
        Permissions.Clear();
        foreach (var rule in rules.Take(VisibleRows))
        {
            Permissions.Add(new AutonomyRow(rule, ChangeLevelAsync));
        }

        More = Rest(rules.Count);
    }

    /// <summary>Cuántas quedaron sin dibujar, dicho en el pie. Vacío si entraron todas.</summary>
    private static string Rest(int total) => total > VisibleRows
        ? $"y {total - VisibleRows} más"
        : string.Empty;

    private void FillMemory(MemoryDesk desk)
    {
        Memory.Clear();

        // Lo pendiente va primero: es lo único de este panel sobre lo que hay algo que decidir, y si
        // hay que recortar, lo que se recorta es lo que ya está decidido.
        foreach (var item in desk.Pending.Take(VisibleRows))
        {
            Memory.Add(MemoryRow.Pending(
                item.ShortId,
                item.Content,
                PanelFeed.Within(item.ExpiresAt - DateTimeOffset.Now),
                ApproveAsync,
                RejectAsync));
        }

        foreach (var item in desk.Known.Take(VisibleRows - Memory.Count))
        {
            Memory.Add(MemoryRow.Known(
                item.Id.ToString("N")[..8],
                item.Content,
                ForgetAsync));
        }

        More = Rest(desk.Pending.Count + desk.Known.Count);
        MemoryNote = desk.IsObservationPaused
            ? "La memoria está vacía. La captura automática está en pausa: sólo guarda lo que le pidas."
            : "La memoria está vacía.";
    }

    /// <summary>
    /// Guarda el nivel nuevo y recién entonces lo refleja en la fila.
    /// </summary>
    /// <remarks>
    /// En ese orden, no al revés. Si el botón pintara el nivel nuevo y después fallara la escritura,
    /// la pantalla diría «nunca» sobre un permiso que sigue guardado como «lo hace sola», y esa
    /// mentira es de las que cuestan plata.
    /// </remarks>
    private async Task ChangeLevelAsync(AutonomyRow row, AutonomyLevel level)
    {
        try
        {
            await PanelFeed.LearnAsync(row.Action, row.Subject, level, CancellationToken.None);
            row.Adopt(level);
            Outcome = level switch
            {
                AutonomyLevel.Automatico => $"Listo: {row.Text} {row.With} lo hace sola.",
                AutonomyLevel.Nunca => $"Anotado: {row.Text} {row.With} no lo hace nunca.",
                _ => $"Anotado: {row.Text} {row.With} te pregunta siempre."
            };
        }
        catch (Exception exception)
        {
            Outcome = $"No pude guardar el permiso: {exception.Message}";
        }
    }

    private Task ApproveAsync(MemoryRow row) =>
        DecideAsync(() => PanelFeed.ApproveMemoryAsync(row.ShortId, CancellationToken.None));

    private Task RejectAsync(MemoryRow row) =>
        DecideAsync(() => PanelFeed.RejectMemoryAsync(row.ShortId, CancellationToken.None));

    private Task ForgetAsync(MemoryRow row) =>
        DecideAsync(() => PanelFeed.ForgetMemoryAsync(row.ShortId, CancellationToken.None));

    /// <summary>
    /// Las tres decisiones sobre la memoria pasan por acá: se decide, se cuenta qué pasó y se relee.
    /// </summary>
    /// <remarks>
    /// Se relee en vez de sacar la fila de la lista a mano. Aprobar una observación temporal la
    /// convierte en explícita —cambia de grupo, no desaparece—, así que tocar la lista desde acá
    /// sería reimplementar del lado de la vista lo que el mostrador ya decidió.
    /// <para>
    /// El <c>catch</c> no es decorativo: estas tres las dispara un <c>ICommand</c>, que las lanza y
    /// no espera. Sin esto, una memoria llena o un archivo bloqueado no dejaban ningún rastro.
    /// </para>
    /// </remarks>
    private async Task DecideAsync(Func<Task<MemoryApprovalOutcome>> decide)
    {
        string message;
        try
        {
            message = (await decide()).Message;
        }
        catch (Exception exception)
        {
            message = $"No pude guardarlo: {exception.Message}";
        }

        // Primero la relectura y después el mensaje: Refresh limpia lo que haya, así que al revés se
        // borraría solo lo que se acaba de decir.
        Refresh(PanelKind.Aprendido);
        Outcome = message;
    }

    private bool CanAnswer() => Question is not null && !string.IsNullOrWhiteSpace(Answer);

    private async Task AnswerAsync(CancellationToken cancellationToken)
    {
        if (Question is not { } question)
        {
            return;
        }

        var text = Answer.Trim();
        var saved = await PanelFeed.AnswerAsync(question.MissionId, text, cancellationToken);
        if (!saved)
        {
            QuestionNote = "No encontré esa misión: puede haberse cerrado mientras escribías.";
            return;
        }

        Answer = string.Empty;
        Question = null;
        _requestClose();
    }

    private static string DescribeBudget(SpendSummary spend)
    {
        if (!spend.IsPersistent)
        {
            return "El libro de consumo no está en disco: lo de hoy se pierde al cerrar.";
        }

        return spend.HasBudget
            ? "Presupuesto configurado. Al alcanzarlo hay que autorizar cada gasto a mano."
            : "Sin presupuesto configurado: nada frena el gasto por monto.";
    }
}

/// <summary>
/// Un permiso aprendido, con el control para cambiarlo.
/// </summary>
/// <remarks>
/// Es la única pantalla donde el usuario ve qué le concedió, así que también es la única donde puede
/// sacárselo. Los tres niveles son botones y no un ciclo: un control que va rotando obliga a pasar
/// por «lo hace sola» para llegar a «nunca», y ése es exactamente el estado por el que no se quiere
/// pasar ni un instante.
/// </remarks>
internal sealed class AutonomyRow : ObservableObject
{
    private readonly Func<AutonomyRow, AutonomyLevel, Task> _change;
    private AutonomyLevel _level;

    public AutonomyRow(AutonomyRule rule, Func<AutonomyRow, AutonomyLevel, Task> change)
    {
        _change = change;
        Action = rule.Action;
        Subject = rule.Subject;
        _level = rule.Level;
        Because = rule.Because ?? string.Empty;
        Learned = PanelFeed.Since(DateTimeOffset.Now - rule.LearnedAt);

        SetAutomaticCommand = new AsyncRelayCommand(_ => Change(AutonomyLevel.Automatico));
        SetAskCommand = new AsyncRelayCommand(_ => Change(AutonomyLevel.Preguntar));
        SetNeverCommand = new AsyncRelayCommand(_ => Change(AutonomyLevel.Nunca));
    }

    /// <summary>La acción, tal como quedó guardada. <c>*</c> significa cualquiera.</summary>
    public string Action { get; }

    /// <summary>A quién o sobre qué. <c>*</c> significa cualquiera.</summary>
    public string Subject { get; }

    /// <summary>Cómo se lee la regla en una línea.</summary>
    public string Text => Action == "*" ? "cualquier acción" : Action;

    /// <summary>Con quién, escrito para leerse debajo.</summary>
    public string With => Subject == "*" ? "con cualquiera" : $"con {Subject}";

    /// <summary>Con qué palabras lo pidió el usuario, si quedó anotado.</summary>
    public string Because { get; }

    /// <summary>Desde cuándo está guardado.</summary>
    public string Learned { get; }

    /// <summary>
    /// De dónde salió el permiso y desde cuándo, para el <em>tooltip</em> de la fila.
    /// </summary>
    /// <remarks>
    /// Las dos cosas juntas y no una sola: «lo hace sola» sin saber cuándo ni por qué se lo
    /// concediste es exactamente la clase de permiso que uno no recuerda haber dado.
    /// </remarks>
    public string Why => Because.Length == 0 ? Learned : $"{Because} · {Learned}";

    public bool IsAutomatic => _level == AutonomyLevel.Automatico;

    public bool IsAsk => _level == AutonomyLevel.Preguntar;

    public bool IsNever => _level == AutonomyLevel.Nunca;

    public ICommand SetAutomaticCommand { get; }

    public ICommand SetAskCommand { get; }

    public ICommand SetNeverCommand { get; }

    /// <summary>Refleja el nivel que quedó guardado. Lo llama quien escribió, no el botón.</summary>
    public void Adopt(AutonomyLevel level)
    {
        _level = level;
        OnPropertyChanged(nameof(IsAutomatic));
        OnPropertyChanged(nameof(IsAsk));
        OnPropertyChanged(nameof(IsNever));
    }

    private Task Change(AutonomyLevel level) => _level == level ? Task.CompletedTask : _change(this, level);
}

/// <summary>
/// Una línea de la memoria: pendiente de aprobar, o ya aprobada.
/// </summary>
/// <remarks>
/// Las dos clases de fila viven en el mismo tipo porque van en la misma lista y el usuario las lee
/// como lo mismo —cosas que Viernes sabe o cree saber de él—. Lo que cambia es qué se puede hacer con
/// cada una, y eso son los comandos: los pendientes se aprueban o se descartan, los aprobados se
/// olvidan.
/// </remarks>
internal sealed class MemoryRow
{
    private MemoryRow(string shortId, string content, string trail, bool isPending)
    {
        ShortId = shortId;
        Content = content;
        Trail = trail;
        IsPending = isPending;
    }

    /// <summary>Lo que espera una decisión: se puede aprobar o descartar.</summary>
    public static MemoryRow Pending(
        string shortId,
        string content,
        string expires,
        Func<MemoryRow, Task> approve,
        Func<MemoryRow, Task> reject)
    {
        var row = new MemoryRow(shortId, content, $"vence {expires}", isPending: true);
        row.ApproveCommand = new AsyncRelayCommand(_ => approve(row));
        row.RejectCommand = new AsyncRelayCommand(_ => reject(row));
        return row;
    }

    /// <summary>Lo que el usuario pidió recordar: sólo se puede olvidar.</summary>
    public static MemoryRow Known(string shortId, string content, Func<MemoryRow, Task> forget)
    {
        var row = new MemoryRow(shortId, content, string.Empty, isPending: false);
        row.ForgetCommand = new AsyncRelayCommand(_ => forget(row));
        return row;
    }

    /// <summary>Los ocho primeros dígitos. Alcanzan para nombrarlo hablando y para <c>/olvidar</c>.</summary>
    public string ShortId { get; }

    public string Content { get; }

    /// <summary>Cuándo vence, para lo pendiente. Vacío para lo aprobado, que no vence.</summary>
    public string Trail { get; }

    /// <summary>Si está esperando una decisión.</summary>
    public bool IsPending { get; }

    /// <summary>Lo contrario. Existe para el XAML, que no sabe negar.</summary>
    public bool IsKnown => !IsPending;

    public ICommand? ApproveCommand { get; private set; }

    public ICommand? RejectCommand { get; private set; }

    public ICommand? ForgetCommand { get; private set; }
}
