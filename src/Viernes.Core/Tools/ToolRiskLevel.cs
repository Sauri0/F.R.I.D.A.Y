namespace Viernes.Core.Tools;

/// <summary>Risk assigned before a tool is allowed to run.</summary>
public enum ToolRiskLevel
{
    Safe,
    RequiresConfirmation,
    Sensitive,
    Destructive
}
