using System.Runtime.InteropServices;

namespace dotmeer.filebrowser.FileSystem;

/// <summary>
/// Создание ровно одного каталога без recursive-create. В отличие от
/// Directory.CreateDirectory, существующее имя возвращается как конфликт,
/// а не как успех: проверка и создание выполняются одной операцией ОС.
/// </summary>
internal static class AtomicDirectory
{
    private const int ErrorAlreadyExists = 183;
    private const int ErrorAccessDenied = 5;
    private const int ErrnoPermission = 1;
    private const int ErrnoAccess = 13;
    private const int ErrnoAlreadyExists = 17;
    private const uint UnixDefaultMode = 0x1FF;

    public static void Create(string physicalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectoryWindows(physicalPath, IntPtr.Zero))
            {
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                ErrorAlreadyExists => new FileSystemNameConflictException("Объект с таким именем уже существует."),
                ErrorAccessDenied => new FileSystemAccessDeniedException(
                    "Недостаточно прав для создания каталога.",
                    new IOException($"CreateDirectory завершился с кодом {error}.")),
                _ => new FileSystemOperationException(
                    "Не удалось создать каталог.",
                    new IOException($"CreateDirectory завершился с кодом {error}."))
            };
        }

        if (MkDir(physicalPath, UnixDefaultMode) == 0)
        {
            return;
        }

        var errno = Marshal.GetLastPInvokeError();
        throw errno switch
        {
            ErrnoAlreadyExists => new FileSystemNameConflictException("Объект с таким именем уже существует."),
            ErrnoAccess or ErrnoPermission => new FileSystemAccessDeniedException(
                "Недостаточно прав для создания каталога.",
                new IOException($"mkdir завершился с errno {errno}.")),
            _ => new FileSystemOperationException(
                "Не удалось создать каталог.",
                new IOException($"mkdir завершился с errno {errno}."))
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string pathName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MkDir([MarshalAs(UnmanagedType.LPUTF8Str)] string pathName, uint mode);
}
