using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Settings;

/// <summary>
/// Server-side application settings stored in SQLite (app_settings table).
/// The language cache is process-local and invalidated on every write;
/// the database remains the source of truth across restarts.
/// </summary>
public sealed class AppSettingsService
{
    public const string LanguageKey = "ui.language";

    private static readonly string[] SupportedLanguages = ["en", "ru"];

    private readonly TaskDatabase _database;
    private readonly object _sync = new();
    private string? _cachedLanguage;

    public AppSettingsService(TaskDatabase database)
    {
        _database = database;
    }

    public static bool IsSupportedLanguage(string language) =>
        SupportedLanguages.Contains(language, StringComparer.Ordinal);

    /// <summary>
    /// Returns the configured UI language. Falls back to "en" when no value
    /// is stored. Thread-safe; reads SQLite at most once until invalidated.
    /// </summary>
    public string GetLanguage()
    {
        lock (_sync)
        {
            if (_cachedLanguage is not null)
            {
                return _cachedLanguage;
            }
        }

        string language;
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", LanguageKey);
            var stored = command.ExecuteScalar()?.ToString();
            language = stored is not null && IsSupportedLanguage(stored) ? stored : "en";
        }

        lock (_sync)
        {
            _cachedLanguage = language;
        }

        return language;
    }

    /// <summary>
    /// Persists the UI language and refreshes the cache.
    /// </summary>
    public async Task SetLanguageAsync(string language, CancellationToken cancellationToken)
    {
        if (!IsSupportedLanguage(language))
        {
            throw new ArgumentException("Неподдерживаемый язык интерфейса.", nameof(language));
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_settings(key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", LanguageKey);
            command.Parameters.AddWithValue("$value", language);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();

        lock (_sync)
        {
            _cachedLanguage = language;
        }
    }
}
