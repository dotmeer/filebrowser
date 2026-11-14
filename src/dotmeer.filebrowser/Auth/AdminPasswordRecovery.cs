using System.Text;
using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Auth;

/// <summary>
/// Локальная команда восстановления пароля admin для существующей SQLite.
/// Пароль вводится с консоли, не через аргументы командной строки; меняется
/// только встроенная учётка admin, все её сессии отзываются.
/// </summary>
public static class AdminPasswordRecovery
{
    public const string CommandName = "reset-admin-password";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length > 1)
        {
            Console.Error.WriteLine(
                "Пароль нельзя передавать аргументом командной строки. Запустите команду без параметров.");
            return 2;
        }

        var configPath = Environment.GetEnvironmentVariable("ConfigPath") ?? "/config/config.yaml";
        var databasePath = Environment.GetEnvironmentVariable("DatabasePath")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "app.db");

        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"База данных не найдена: {databasePath}");
            return 1;
        }

        var password = ReadPassword();
        if (password is null)
        {
            return 1;
        }

        try
        {
            if (!ResetPassword(databasePath, password))
            {
                Console.Error.WriteLine("В базе нет настроенной учётной записи admin.");
                return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Не удалось восстановить пароль admin: {exception.Message}");
            return 1;
        }

        Console.WriteLine("Пароль admin изменён, прежние сессии admin отозваны.");
        return 0;
    }

    /// <summary>Возвращает true, если admin существовал и пароль заменён.</summary>
    internal static bool ResetPassword(string databasePath, string password)
    {
        var database = new TaskDatabase(databasePath);
        database.Migrate();
        var store = new UserStore(database, new PasswordService(), TimeProvider.System);
        return store.ReplaceAdminPasswordAsync(password, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            var first = Console.ReadLine();
            var second = Console.ReadLine();
            if (first is null)
            {
                Console.Error.WriteLine("Пароль не введён.");
                return null;
            }

            if (!string.Equals(first, second, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Пароли не совпадают.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(first))
            {
                Console.Error.WriteLine("Пароль не может быть пустым.");
                return null;
            }

            return first;
        }

        var password = ReadMasked("Новый пароль admin: ");
        if (password is null)
        {
            return null;
        }

        var confirmation = ReadMasked("Повторите пароль: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Пароли не совпадают.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Пароль не может быть пустым.");
            return null;
        }

        return password;
    }

    private static string? ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
