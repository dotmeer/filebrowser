using Microsoft.AspNetCore.Identity;

namespace dotmeer.filebrowser.Auth;

/// <summary>
/// Хеширование паролей штатным алгоритмом ASP.NET Core Identity: PBKDF2
/// со случайной солью и версией формата внутри строки хеша.
/// </summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<object> _hasher = new();
    private readonly object _user = new();

    public string Hash(string password) => _hasher.HashPassword(_user, password);

    public bool Verify(string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(_user, passwordHash, password) != PasswordVerificationResult.Failed;

    /// <summary>
    /// Проверка против фиктивного хеша для неизвестного логина: время ответа
    /// не должно заметно отличать отсутствующую учётку от неверного пароля.
    /// </summary>
    public string DummyHash { get; } = new PasswordHasher<object>().HashPassword(new object(), "filebrowser-dummy-password");
}
