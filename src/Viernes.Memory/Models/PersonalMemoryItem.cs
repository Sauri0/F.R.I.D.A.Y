using System.Text.Json.Serialization;

namespace Viernes.Memory.Models;

public abstract record PersonalMemoryItem(
    Guid Id,
    string Content,
    DateTimeOffset UpdatedAt)
{
    [JsonIgnore]
    public abstract PersonalMemoryKind Kind { get; }

    [JsonIgnore]
    public abstract DateTimeOffset RecordedAt { get; }
}
