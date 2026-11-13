using System.Runtime.InteropServices;

namespace dotmeer.filebrowser.FileSystem;

/// <summary>
/// Определяет Unix-тип объекта через statx(2). File.GetAttributes на Unix
/// не сообщает о FIFO/socket/device, поэтому одной проверки FileAttributes.Device
/// недостаточно. statx используется без маршалинга native struct: буфер фиксированного
/// размера и stx_mode по смещению из стабильного uapi-контракта Linux.
/// </summary>
internal static class UnixFileType
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxBufferSize = 256;
    private const int ModeOffset = 28;
    private const int TypeMask = 0xF000;
    private const int TypeRegularFile = 0x8000;
    private const int TypeDirectory = 0x4000;
    private const int TypeSymlink = 0xA000;

    public static bool IsSpecialFile(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var mode = TryGetMode(path);
        if (mode is null)
        {
            // Без подтверждённого типа объекта отказ безопаснее: FIFO/socket/
            // device нельзя отличить от обычного файла managed-атрибутами.
            return true;
        }

        var type = mode.Value & TypeMask;
        return type is not (TypeRegularFile or TypeDirectory or TypeSymlink);
    }

    private static ushort? TryGetMode(string path)
    {
        var buffer = new byte[StatxBufferSize];
        try
        {
            if (Statx(AtFdcwd, path, AtSymlinkNoFollow, StatxBasicStats, buffer) != 0)
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        return MemoryMarshal.Read<ushort>(buffer.AsSpan(ModeOffset));
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
        int flags,
        uint mask,
        byte[] statxbuf);
}
