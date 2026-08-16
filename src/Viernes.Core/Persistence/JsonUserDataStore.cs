using System.Text.Json;

namespace Viernes.Core.Persistence;

/// <summary>
/// Atomic JSON persistence for reminders and agenda. It intentionally contains no configuration
/// or credentials. By default the file lives under the current user's LocalApplicationData.
/// </summary>
public sealed class JsonUserDataStore : IUserDataStore
{
    private const long MaximumDataFileBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonUserDataStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? GetDefaultPath());
        if (string.IsNullOrWhiteSpace(Path.GetFileName(_filePath)))
        {
            throw new ArgumentException("A JSON file path is required.", nameof(filePath));
        }
    }

    public string FilePath => _filePath;

    public async Task<Reminder> AddReminderAsync(
        string title,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default)
    {
        InMemoryUserDataStore.ValidateTitle(title);
        var reminder = new Reminder(Guid.NewGuid(), title.Trim(), dueAt, DateTimeOffset.Now);

        await MutateAsync(data => data.Reminders.Add(reminder), cancellationToken).ConfigureAwait(false);
        return reminder;
    }

    public async Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        return data.Reminders.OrderBy(item => item.DueAt).ToArray();
    }

    public async Task<AgendaItem> AddAgendaItemAsync(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        InMemoryUserDataStore.ValidateAgenda(title, startsAt, endsAt, notes);
        var item = new AgendaItem(
            Guid.NewGuid(),
            title.Trim(),
            startsAt,
            endsAt,
            DateTimeOffset.Now,
            InMemoryUserDataStore.NormalizeNotes(notes));

        await MutateAsync(data => data.AgendaItems.Add(item), cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<AgendaItem>> GetAgendaItemsAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        return data.AgendaItems.OrderBy(item => item.StartsAt).ToArray();
    }

    private static string GetDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Local application data is unavailable.");
        }

        return Path.Combine(root, "Viernes", "assistant-data.json");
    }

    private async Task<UserDataDocument> ReadLockedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateAsync(Action<UserDataDocument> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            mutation(data);
            await WriteUnsafeAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UserDataDocument> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new UserDataDocument();
        }

        var info = new FileInfo(_filePath);
        if (info.Length > MaximumDataFileBytes)
        {
            throw new InvalidDataException("The Viernes data file is unexpectedly large.");
        }

        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<UserDataDocument>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new UserDataDocument();
    }

    private async Task WriteUnsafeAsync(UserDataDocument data, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The data file needs a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, data, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class UserDataDocument
    {
        public List<Reminder> Reminders { get; init; } = [];

        public List<AgendaItem> AgendaItems { get; init; } = [];
    }
}
