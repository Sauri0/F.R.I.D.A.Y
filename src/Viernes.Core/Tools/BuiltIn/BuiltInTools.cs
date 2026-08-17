using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

public static class BuiltInTools
{
    /// <summary>
    /// Sin <paramref name="pcActions"/>, la herramienta de PC sigue siendo una vista previa.
    /// El host decide si le entrega un ejecutor real.
    /// </summary>
    public static IReadOnlyList<IAssistantTool> Create(
        IUserDataStore dataStore,
        IPcActionExecutor? pcActions = null,
        bool providerWebSearch = false,
        bool confirmActions = false,
        Awareness.IEnvironmentObserver? environment = null,
        Learning.RuleBook? rules = null,
        Goals.GoalBook? goals = null,
        Missions.MissionBook? missions = null,
        Autonomy.AutonomyPolicy? autonomy = null)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        return Array.AsReadOnly<IAssistantTool>(
        [
            new ReminderCreateTool(dataStore),
            new ReminderListTool(dataStore),
            new AgendaCreateTool(dataStore),
            new AgendaListTool(dataStore),
            new WebSearchTool(providerWebSearch),
            new PcActionTool(pcActions, confirmActions),
            new SituationTool(environment),
            new TeachTool(rules ?? new Learning.RuleBook()),
            new GoalTool(goals ?? new Goals.GoalBook()),
            new MissionTool(missions ?? new Missions.MissionBook()),
            new PermissionTool(autonomy ?? new Autonomy.AutonomyPolicy()),
            new ProjectTool(),
            new FileSystemTool(),
            new ShellTool()
        ]);
    }
}
