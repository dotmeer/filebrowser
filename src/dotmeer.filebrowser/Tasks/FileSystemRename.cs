namespace dotmeer.filebrowser.Tasks;

internal static class FileSystemRename
{
    // Linux/macOS EXDEV = 18; Windows ERROR_NOT_SAME_DEVICE = 0x80070011.
    private const int CrossDeviceError = 18;
    private const int WindowsNotSameDevice = unchecked((int)0x80070011);

    public static bool IsCrossDevice(IOException exception) =>
        OperatingSystem.IsWindows()
            ? exception.HResult == WindowsNotSameDevice
            : exception.HResult == CrossDeviceError;
}
