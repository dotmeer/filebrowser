using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace dotmeer.filebrowser.FileSystem;

internal enum FileSystemEntryKind
{
    RegularFile,
    Directory,
    Symlink,
    Unsupported
}

internal static class PathInspection
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static void ThrowIfExistingPathContainsSymlink(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("Путь корня невалиден.");
        }

        var relativePath = Path.GetRelativePath(root, path);
        if (relativePath == ".")
        {
            return;
        }

        var currentPath = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!TryGetAttributes(currentPath, out var attributes))
            {
                return;
            }

            if (IsSymlink(currentPath, attributes))
            {
                throw new InvalidOperationException("Путь корня не может содержать символические ссылки.");
            }
        }
    }

    public static void ThrowIfUnsupported(string path)
    {
        if (!TryClassify(path, out var kind))
        {
            throw new FileSystemResolutionException("Запрошенный объект не существует.");
        }

        switch (kind)
        {
            case FileSystemEntryKind.Symlink:
                throw new UnsupportedFileSystemObjectException("Символические ссылки не поддерживаются.", path);
            case FileSystemEntryKind.Unsupported:
                throw new UnsupportedFileSystemObjectException("Тип объекта не поддерживается.");
        }
    }

    public static bool TryClassify(string path, out FileSystemEntryKind kind)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            kind = default;
            return false;
        }

        kind = Classify(path, attributes);
        return true;
    }

    public static FileSystemEntryKind Classify(string path, FileAttributes attributes)
    {
        if (IsConfirmedSymlink(path, attributes))
        {
            return FileSystemEntryKind.Symlink;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return FileSystemEntryKind.Unsupported;
        }

        if (UnixFileType.IsSpecialFile(path) || (attributes & FileAttributes.Device) != 0)
        {
            return FileSystemEntryKind.Unsupported;
        }

        return (attributes & FileAttributes.Directory) != 0
            ? FileSystemEntryKind.Directory
            : FileSystemEntryKind.RegularFile;
    }

    public static bool PathsOverlap(string firstPath, string secondPath) =>
        IsWithin(firstPath, secondPath) || IsWithin(secondPath, firstPath);

    public static bool IsWithin(string parentPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(parentPath, candidatePath);
        return relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && !relativePath.Equals("..", PathComparison)
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison));
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsSymlink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    private static bool IsConfirmedSymlink(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows() && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            using var handle = FindFirstFile(path, out var data);
            return !handle.IsInvalid
                && (data.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0
                && data.Reserved0 == 0xA000000C;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true, EntryPoint = "FindFirstFileW")]
    private static extern SafeFindHandle FindFirstFile(string fileName, out Win32FindData findData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
