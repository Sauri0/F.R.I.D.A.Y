using Viernes.Core;
using Viernes.Core.Autonomy;
using Viernes.Core.Configuration;
using Viernes.Core.Learning;
using Viernes.Core.Missions;
using Viernes.Core.Projects;
using Viernes.Core.Usage;
using Viernes.Memory.Models;
using Viernes.Memory.Persistence;

namespace Viernes.App.Shell;

/// <summary>
/// De dónde saca cada uno de los seis desplegables nuevos lo que muestra.
/// </summary>
/// <remarks>
/// Todo lo de acá se lee del mismo lugar donde vive de verdad: <c>misiones.json</c>,
/// <c>autonomia.json</c>, la memoria personal, el libro de consumo y el árbol de sesiones de Claude
/// Code. <b>Nada se inventa.</b> Un panel con datos plausibles se ve terminado y miente, que es peor
/// que no tener el panel: el usuario toma decisiones mirándolo.
///
/// <para>
/// <b>Por qué se construye un libro nuevo en cada lectura.</b> El runtime tiene el suyo
/// —<c>AssistantRuntime._missionBook</c>, y la fábrica arma el suyo de autonomía— y esos libros
/// cachean en memoria lo que leyeron del disco y no invalidan nunca. Guardar acá una instancia viva
/// sería tener una copia que se va quedando vieja a medida que la herramienta <c>mision</c> o
/// <c>permiso</c> escriben. Una instancia nueva por lectura no tiene caché que envejecer: siempre ve
/// el archivo, que es donde los dos libros guardan. Cuesta abrir un archivo de pocos kilobytes cuando
/// alguien abre un panel, y eso es gratis comparado con mostrar algo que ya no es cierto.
/// </para>
///
/// <para>
/// <b>Lo que esto NO puede arreglar, y hay que saberlo.</b> Escribir desde acá deja el archivo bien,
/// pero <em>no</em> invalida el caché del libro que tiene el runtime en el mismo proceso. Contestar
/// una pregunta desde el desplegable la contesta en disco y sobrevive al reinicio; hasta que el
/// proceso se reinicie, el barrido de fondo sigue viendo su copia vieja —el orbe puede quedar en «te
/// espero»— y, si el modelo toca esa misma misión con la herramienta, guarda su lista vieja encima.
/// Se arregla el día que <c>IAssistantRuntime</c> exponga el libro que ya tiene (o un
/// «volvé a leer»); no se puede arreglar desde este lado sin tener dos verdades, que es exactamente
/// lo que el comentario de <c>_missionBook</c> avisa que no se haga.
/// </para>
///
/// <para>
/// La memoria personal es la excepción feliz: <c>JsonPersonalMemoryStore</c> no cachea nada —lee el
/// archivo en cada llamada— y su semáforo es estático por ruta, así que dos instancias en el mismo
/// proceso son una sola verdad y se serializan entre sí. Aprobar y descartar desde el panel es
/// correcto de punta a punta.
/// </para>
/// </remarks>
internal static class PanelFeed
{
    /// <summary>
    /// Cuántas filas se leen como mucho. Cuántas se dibujan lo decide el panel.
    /// </summary>
    /// <remarks>
    /// Se leen más de las que entran a propósito: el pie de cada panel dice cuántas quedaron afuera,
    /// y para decirlo hay que haberlas contado. Ocho es de dónde no pasa: el vigía de proyectos
    /// recorre disco por cada una.
    /// </remarks>
    private const int MaximumRead = 8;

    /// <summary>
    /// Las opciones se leen una vez: son variables de entorno del proceso y no cambian mientras corre.
    /// </summary>
    private static readonly Lazy<ViernesOptions> Options = new(
        () => ViernesOptions.FromEnvironment(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // ============================ misiones abiertas ============================

    /// <summary>Las misiones vivas, la que se movió último primero.</summary>
    public static async Task<IReadOnlyList<PanelRow>> MissionsAsync(CancellationToken cancellationToken)
    {
        var missions = await new MissionBook()
            .ListAsync(onlyOpen: true, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.Now;
        return
        [
            .. missions
                .Take(MaximumRead)
                .Select(mission => new PanelRow(
                    mission.Id,
                    mission.Title,

                    // Lo que le falta es el objetivo, no el último avance: el avance cuenta de dónde
                    // viene y el objetivo cuenta cuándo se puede cerrar, que es lo que se pregunta
                    // mirando una lista de misiones abiertas.
                    mission.Goal,
                    $"{StateLabel(mission.State)} · {Since(now - mission.LastProgress)}",
                    mission.State == MissionState.Esperando ? PanelRowTone.Esperando : PanelRowTone.Normal))
        ];
    }

    /// <summary>Cómo se dice cada uno de los cinco estados de una misión.</summary>
    /// <remarks>
    /// Los cinco, aunque el panel liste sólo las abiertas: el día que muestre también las cerradas,
    /// la etiqueta ya está y no hay que acordarse de agregarla.
    /// </remarks>
    public static string StateLabel(MissionState state) => state switch
    {
        MissionState.Abierta => "sin empezar",
        MissionState.EnCurso => "en curso",
        MissionState.Esperando => "te espera",
        MissionState.Terminada => "terminada",
        MissionState.Cancelada => "cancelada",
        _ => state.ToString()
    };

    // ============================ la pregunta pendiente ============================

    /// <summary>La pregunta sin contestar más vieja, o <c>null</c> si no hay ninguna.</summary>
    /// <remarks>
    /// La más vieja y no la más nueva: es la que lleva más tiempo trabando una misión, y es la única
    /// de la que se puede decir con honestidad «desde cuándo».
    /// </remarks>
    public static async Task<PendingQuestion?> QuestionAsync(CancellationToken cancellationToken)
    {
        var missions = await new MissionBook()
            .ListAsync(onlyOpen: true, cancellationToken)
            .ConfigureAwait(false);

        var waiting = missions
            .Where(mission => mission.State == MissionState.Esperando && mission.Question is not null)
            .OrderBy(mission => mission.AskedAt ?? mission.LastProgress)
            .FirstOrDefault();

        if (waiting is null)
        {
            return null;
        }

        var asked = waiting.AskedAt ?? waiting.LastProgress;
        return new PendingQuestion(
            waiting.Id,
            waiting.Title,
            waiting.Question!,
            Since(DateTimeOffset.Now - asked),
            missions.Count(mission => mission.State == MissionState.Esperando && mission.Question is not null));
    }

    /// <summary>
    /// Contesta la pregunta y destraba la misión. Devuelve si quedó guardado.
    /// </summary>
    /// <remarks>
    /// Escribe en el archivo, que es lo que sobrevive al reinicio y lo que el brief pide. Lo que no
    /// puede hacer —y está explicado arriba— es enterar al libro que el runtime ya tiene cargado en
    /// memoria: hasta reiniciar, el barrido de fondo puede seguir mostrando el orbe esperando.
    /// </remarks>
    public static async Task<bool> AnswerAsync(
        string missionId,
        string answer,
        CancellationToken cancellationToken)
    {
        var mission = await new MissionBook()
            .AnswerAsync(missionId, answer, cancellationToken)
            .ConfigureAwait(false);
        return mission is not null;
    }

    // ============================ proyectos ============================

    /// <summary>Las sesiones de Claude Code más recientes. Sólo lee archivos; nunca escribe.</summary>
    /// <remarks>
    /// El propio Viernes queda afuera por la misma razón que en el barrido del runtime: mirarse
    /// trabajando produce un lazo, y además no es lo que el usuario quiere seguir.
    /// </remarks>
    public static Task<IReadOnlyList<PanelRow>> ProjectsAsync(CancellationToken cancellationToken) =>

        // Recorre un árbol de carpetas y lee la cola de cada archivo: es disco, y en el hilo de la
        // interfaz eso se ve como el orbe trabándose al abrir el panel.
        Task.Run<IReadOnlyList<PanelRow>>(
            () =>
            {
                var now = DateTimeOffset.Now;
                var sessions = new ClaudeSessionWatcher()
                    .Recent(now, maximum: MaximumRead, excludeProjectContaining: "Viernes");

                return
                [
                    .. sessions.Select(session => new PanelRow(
                        session.Branch is { Length: > 0 } branch ? branch : "—",
                        System.IO.Path.GetFileName(session.Project.TrimEnd('\\', '/')),
                        ActivityDetail(session),
                        Since(now - session.LastActivity),
                        session.Activity == SessionActivity.Esperando
                            ? PanelRowTone.Esperando
                            : PanelRowTone.Normal))
                ];
            },
            cancellationToken);

    private static string ActivityDetail(SessionSnapshot session) => session.Activity switch
    {
        SessionActivity.Trabajando => $"trabajando · {session.ToolsSinceYouSpoke} pasos desde tu mensaje",
        SessionActivity.Esperando => session.LastSaid is { Length: > 0 } said
            ? $"te espera · «{said}»"
            : "te espera",
        _ => "quieta"
    };

    // ============================ permisos aprendidos ============================

    /// <summary>Los permisos guardados, el último aprendido primero.</summary>
    public static async Task<IReadOnlyList<AutonomyRule>> AutonomyAsync(CancellationToken cancellationToken)
    {
        var rules = await new AutonomyPolicy().ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rules.Take(MaximumRead)];
    }

    /// <summary>Cambia un permiso. Pisa el anterior sobre la misma acción y el mismo sujeto.</summary>
    public static Task LearnAsync(
        string action,
        string subject,
        AutonomyLevel level,
        CancellationToken cancellationToken) =>
        new AutonomyPolicy().LearnAsync(
            action,
            subject,

            level,

            // Queda escrito de dónde salió: en la lista, un permiso sin explicación es indistinguible
            // de uno que el usuario no recuerda haber dado.
            because: "Lo cambiaste desde el desplegable de permisos.",
            cancellationToken);

    // ============================ lo que aprendió ============================

    /// <summary>Lo que espera aprobación y lo que ya es un hecho.</summary>
    public static async Task<MemoryDesk> MemoryAsync(CancellationToken cancellationToken)
    {
        var store = new JsonPersonalMemoryStore();
        var pending = await new MemoryApprovals(store)
            .ListPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        var review = await store.ReviewAsync(cancellationToken).ConfigureAwait(false);

        return new MemoryDesk(pending, review.Explicit, review.IsObservationPaused);
    }

    /// <summary>Convierte en permanente algo pendiente.</summary>
    public static Task<MemoryApprovalOutcome> ApproveMemoryAsync(
        string reference,
        CancellationToken cancellationToken) =>
        new MemoryApprovals(new JsonPersonalMemoryStore()).ApproveAsync(reference, cancellationToken);

    /// <summary>Descarta algo pendiente para que no vuelva a aparecer.</summary>
    public static Task<MemoryApprovalOutcome> RejectMemoryAsync(
        string reference,
        CancellationToken cancellationToken) =>
        new MemoryApprovals(new JsonPersonalMemoryStore()).RejectAsync(reference, cancellationToken);

    /// <summary>Borra un hecho ya aprobado.</summary>
    public static Task<MemoryApprovalOutcome> ForgetMemoryAsync(
        string reference,
        CancellationToken cancellationToken) =>
        new MemoryApprovals(new JsonPersonalMemoryStore()).ForgetAsync(reference, cancellationToken);

    // ============================ gasto ============================

    /// <summary>Lo gastado hoy y en el mes, contra el presupuesto configurado.</summary>
    public static async Task<SpendSummary> SpendAsync(CancellationToken cancellationToken)
    {
        var options = Options.Value;
        var ledger = ViernesCoreFactory.CreateUsageLedger(options);
        var today = await ledger.GetDailyTotalsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var month = await ledger.GetMonthlyTotalsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return new SpendSummary(
            today.EffectiveCostUsd,
            options.UsageBudgets.DailyBudgetUsd,
            month.EffectiveCostUsd,
            options.UsageBudgets.MonthlyBudgetUsd,
            today.RequestCount,
            today.Tokens.TotalTokens,
            ledger.FilePath is not null);
    }

    /// <summary>
    /// Hace cuánto, en una línea y sin falsa precisión.
    /// </summary>
    /// <remarks>
    /// Los mismos cortes que usa <see cref="ClaudeSessionWatcher"/> para contar lo mismo. Decir «hace
    /// 127 minutos» es exacto y no significa nada; «hace 2 horas» es lo que uno contesta.
    /// </remarks>
    public static string Since(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "recién",
        { TotalMinutes: < 60 } => $"hace {(int)span.TotalMinutes} min",
        { TotalHours: < 24 } => $"hace {(int)span.TotalHours} h",
        { TotalDays: < 2 } => "ayer",
        _ => $"hace {(int)span.TotalDays} días"
    };

    /// <summary>
    /// Cuánto falta. Es <see cref="Since"/> mirando para el otro lado.
    /// </summary>
    /// <remarks>
    /// Existe porque un vencimiento está en el futuro y pasarlo por <see cref="Since"/> escribía
    /// «vence hace 3 días» sobre algo que todavía no venció.
    /// </remarks>
    public static string Within(TimeSpan span) => span switch
    {
        { Ticks: <= 0 } => "ya",
        { TotalMinutes: < 60 } => $"en {(int)span.TotalMinutes} min",
        { TotalHours: < 24 } => $"en {(int)span.TotalHours} h",
        { TotalDays: < 2 } => "mañana",
        _ => $"en {(int)span.TotalDays} días"
    };
}

/// <summary>Cómo se lee una fila: si está esperando algo del usuario o sólo informa.</summary>
internal enum PanelRowTone
{
    /// <summary>Informa. La mayoría.</summary>
    Normal,

    /// <summary>Está frenada esperando al usuario. Es lo único que se puede destrabar ahora.</summary>
    Esperando
}

/// <summary>
/// Una fila de los desplegables de lista.
/// </summary>
/// <param name="Lead">La columna angosta de la izquierda: un identificador, una rama, un estado.</param>
/// <param name="Text">Lo que la fila dice, en una línea.</param>
/// <param name="Detail">La segunda línea, más chica. Vacía si no hace falta.</param>
/// <param name="Trail">La columna angosta de la derecha: casi siempre desde cuándo.</param>
/// <param name="Tone">Si la fila está esperando algo.</param>
internal sealed record PanelRow(
    string Lead,
    string Text,
    string Detail,
    string Trail,
    PanelRowTone Tone)
{
    /// <summary>Para el XAML, que sabe comparar contra <c>True</c> y no contra un valor de enumeración.</summary>
    public bool IsWaiting => Tone == PanelRowTone.Esperando;
}

/// <summary>
/// La pregunta que sobrevive al reinicio.
/// </summary>
/// <param name="MissionId">Qué misión está frenada. Es lo que hace falta para contestarla.</param>
/// <param name="MissionTitle">Cómo se llama esa misión.</param>
/// <param name="Question">Lo que preguntó.</param>
/// <param name="Since">Desde cuándo espera.</param>
/// <param name="Waiting">Cuántas preguntas hay esperando en total, contando ésta.</param>
internal sealed record PendingQuestion(
    string MissionId,
    string MissionTitle,
    string Question,
    string Since,
    int Waiting)
{
    /// <summary>
    /// Si hay más preguntas atrás, dicho en la misma línea.
    /// </summary>
    /// <remarks>
    /// Contestar una y que el panel se cierre da a entender que no quedaba nada. Si quedan dos más,
    /// hay que decirlo antes de contestar la primera, no después.
    /// </remarks>
    public string Queue => Waiting > 1 ? $" · y {Waiting - 1} más esperando" : string.Empty;
}

/// <summary>El mostrador de la memoria: lo que espera decisión y lo que ya se sabe.</summary>
/// <param name="Pending">Sugerencias y observaciones temporales sin decidir.</param>
/// <param name="Known">Lo que el usuario pidió recordar.</param>
/// <param name="IsObservationPaused">Si la captura automática está en pausa.</param>
internal sealed record MemoryDesk(
    IReadOnlyList<PendingMemory> Pending,
    IReadOnlyList<ExplicitMemory> Known,
    bool IsObservationPaused);

/// <summary>
/// Cuánto lleva gastado el modelo.
/// </summary>
/// <param name="TodayUsd">Costo efectivo de hoy, en dólares.</param>
/// <param name="DailyBudgetUsd">Presupuesto diario, o <c>null</c> si nadie configuró uno.</param>
/// <param name="MonthUsd">Costo efectivo del mes.</param>
/// <param name="MonthlyBudgetUsd">Presupuesto mensual, o <c>null</c>.</param>
/// <param name="RequestsToday">Cuántas solicitudes salieron hoy.</param>
/// <param name="TokensToday">Cuántos tokens se usaron hoy.</param>
/// <param name="IsPersistent">Si el libro está en disco. Si no, lo de hoy muere con el proceso.</param>
internal sealed record SpendSummary(
    decimal TodayUsd,
    decimal? DailyBudgetUsd,
    decimal MonthUsd,
    decimal? MonthlyBudgetUsd,
    int RequestsToday,
    long TokensToday,
    bool IsPersistent)
{
    /// <summary>Cuánto del presupuesto diario está gastado, de 0 a 1. Cero si no hay presupuesto.</summary>
    public double DailyFraction => DailyBudgetUsd is > 0
        ? Math.Clamp((double)(TodayUsd / DailyBudgetUsd.Value), 0, 1)
        : 0;

    /// <summary>Si hay algún presupuesto contra el que comparar.</summary>
    public bool HasBudget => DailyBudgetUsd is not null || MonthlyBudgetUsd is not null;

    /// <summary>Si hay tope diario. Sin tope no se dibuja la barra: una barra vacía dice «no gastaste».</summary>
    public bool HasDailyBudget => DailyBudgetUsd is not null;

    /// <summary>
    /// Cuánto mide la parte llena de la barra.
    /// </summary>
    /// <remarks>
    /// El ancho del riel sale del panel: 364 de vidrio menos los 16 de margen de cada lado. Se
    /// calcula acá y no en el XAML porque una fracción no se puede convertir a píxeles en un enlace
    /// sin un convertidor, y un convertidor para una multiplicación es más ceremonia que cuenta. Si
    /// cambia el ancho del panel de gasto en <see cref="PanelCatalog"/>, cambia este número.
    /// </remarks>
    public double BarWidth => 332 * DailyFraction;

    /// <summary>Lo gastado hoy.</summary>
    public string TodayText => Money(TodayUsd);

    /// <summary>El tope diario, o que no hay.</summary>
    public string DailyBudgetText => DailyBudgetUsd is { } limit ? Money(limit) : "sin tope";

    /// <summary>Lo gastado en el mes.</summary>
    public string MonthText => Money(MonthUsd);

    /// <summary>El tope mensual, o que no hay.</summary>
    public string MonthlyBudgetText => MonthlyBudgetUsd is { } limit ? Money(limit) : "sin tope";

    /// <summary>Las solicitudes y los tokens de hoy, en una línea.</summary>
    public string TrafficText =>
        $"{RequestsToday} solicitudes · {TokensToday.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} tokens";

    /// <summary>
    /// Dólares con dos decimales y punto.
    /// </summary>
    /// <remarks>
    /// Invariante a propósito, aunque el resto de la interfaz hable en rioplatense: son dólares de
    /// una factura de API, y el libro los guarda así. Escribir «US$ 0,42» acá y «0.42» en el archivo
    /// obliga a traducir mentalmente cada vez que se comparan las dos cosas.
    /// </remarks>
    private static string Money(decimal value) =>
        "US$ " + value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
