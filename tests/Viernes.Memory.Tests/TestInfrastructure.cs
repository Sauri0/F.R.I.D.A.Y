using Viernes.Memory.Persistence;

namespace Viernes.Memory.Tests;

internal sealed class TestStoreScope : IDisposable
{
    private static readonly string TestRoot = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "Viernes.Memory.Tests"));

    public TestStoreScope()
    {
        DirectoryPath = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        FilePath = Path.Combine(DirectoryPath, "memory.json");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public JsonPersonalMemoryStore CreateStore(
        PersonalMemoryStoreOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(FilePath, options, timeProvider);

    public void Dispose()
    {
        var resolved = Path.GetFullPath(DirectoryPath);
        if (Directory.Exists(resolved) &&
            resolved.StartsWith(TestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtcNow.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
}
