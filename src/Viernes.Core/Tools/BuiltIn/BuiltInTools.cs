using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

public static class BuiltInTools
{
    public static IReadOnlyList<IAssistantTool> Create(IUserDataStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        return Array.AsReadOnly<IAssistantTool>(
        [
            new ReminderCreateTool(dataStore),
            new ReminderListTool(dataStore),
            new AgendaCreateTool(dataStore),
            new AgendaListTool(dataStore),
            new WebSearchTool(),
            new PcActionTool()
        ]);
    }
}
