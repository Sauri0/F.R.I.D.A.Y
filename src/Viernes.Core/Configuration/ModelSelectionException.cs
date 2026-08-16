namespace Viernes.Core.Configuration;

/// <summary>Raised before any network call when an explicit selection is not ready.</summary>
public sealed class ModelSelectionException(ModelSelection selection)
    : InvalidOperationException(selection?.Message)
{
    public ModelSelection Selection { get; } =
        selection ?? throw new ArgumentNullException(nameof(selection));
}
