namespace Viernes.Core.Usage;

/// <summary>Caller-supplied USD pricing per one million tokens for one exact model id.</summary>
public sealed record ModelTokenRate
{
    public ModelTokenRate(
        string model,
        decimal inputUsdPerMillionTokens,
        decimal outputUsdPerMillionTokens)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model id is required.", nameof(model));
        }

        if (inputUsdPerMillionTokens is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(inputUsdPerMillionTokens));
        }

        if (outputUsdPerMillionTokens is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(outputUsdPerMillionTokens));
        }

        Model = model.Trim();
        InputUsdPerMillionTokens = inputUsdPerMillionTokens;
        OutputUsdPerMillionTokens = outputUsdPerMillionTokens;
    }

    public string Model { get; }

    public decimal InputUsdPerMillionTokens { get; }

    public decimal OutputUsdPerMillionTokens { get; }
}
