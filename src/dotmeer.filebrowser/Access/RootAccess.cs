namespace dotmeer.filebrowser.Access;

/// <summary>
/// Права обычной учётной записи на настроенный корень. Отсутствие записи
/// означает <see cref="None"/>; полные права включают чтение.
/// </summary>
public enum RootAccess
{
    None = 0,
    Read = 1,
    Write = 2
}

public static class RootAccessNames
{
    public const string Read = "read";
    public const string Write = "write";

    public static string ToStorageValue(RootAccess access) => access switch
    {
        RootAccess.Read => Read,
        RootAccess.Write => Write,
        _ => throw new ArgumentOutOfRangeException(nameof(access))
    };

    public static RootAccess? TryParse(string? value) => value switch
    {
        Read => RootAccess.Read,
        Write => RootAccess.Write,
        _ => null
    };
}
