namespace Viernes.Core.Intelligence;

public sealed record EmbeddingResult(
    ReadOnlyMemory<float> Vector,
    string ModelOrEngine,
    bool IsLocal);
