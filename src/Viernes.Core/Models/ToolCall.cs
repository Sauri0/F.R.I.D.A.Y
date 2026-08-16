using System.Text.Json;

namespace Viernes.Core.Models;

/// <summary>A model-requested tool call. Arguments are preserved as JSON and validated by each tool.</summary>
public sealed record ToolCall(string Id, string Name, JsonElement Arguments);
