namespace dotmeer.filebrowser.Auth;

/// <summary>
/// Режим доступа, выбранный при старте. <see cref="Anonymous"/> сохраняет
/// прежнее поведение приложения; <see cref="Local"/> включает локальные
/// учётные записи и cookie-сессии.
/// </summary>
public enum AuthMode
{
    Anonymous,
    Local
}

/// <summary>
/// Настройки авторизации, прочитанные из стандартной конфигурации ASP.NET Core.
/// </summary>
public sealed record AuthOptions(AuthMode Mode)
{
    public bool IsAnonymous => Mode == AuthMode.Anonymous;

    public bool IsLocal => Mode == AuthMode.Local;
}

public static class AuthModeReader
{
    public const string SettingName = "AUTH_MODE";

    /// <summary>
    /// Читает <c>AUTH_MODE</c>. Отсутствие переменной означает
    /// <see cref="AuthMode.Anonymous"/>. Пустое или неизвестное значение —
    /// ошибка запуска: молчаливое понижение режима недопустимо.
    /// </summary>
    public static AuthOptions Read(IConfiguration configuration)
    {
        var raw = configuration[SettingName];
        if (raw is null)
        {
            return new AuthOptions(AuthMode.Anonymous);
        }

        var value = raw.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException(
                "Переменная AUTH_MODE задана пустой. Допустимые значения: ANONYMOUS, LOCAL.");
        }

        return value.ToUpperInvariant() switch
        {
            "ANONYMOUS" => new AuthOptions(AuthMode.Anonymous),
            "LOCAL" => new AuthOptions(AuthMode.Local),
            _ => throw new InvalidOperationException(
                $"Неизвестное значение AUTH_MODE «{value}». Допустимые значения: ANONYMOUS, LOCAL.")
        };
    }
}
