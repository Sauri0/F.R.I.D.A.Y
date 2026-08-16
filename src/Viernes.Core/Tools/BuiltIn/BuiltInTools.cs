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
        bool providerWebSearch = false)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        return Array.AsReadOnly<IAssistantTool>(
        [
            new ReminderCreateTool(dataStore),
            new ReminderListTool(dataStore),
            new AgendaCreateTool(dataStore),
            new AgendaListTool(dataStore),
            new WebSearchTool(providerWebSearch),
            new PcActionTool(pcActions)
        ]);
    }
}
