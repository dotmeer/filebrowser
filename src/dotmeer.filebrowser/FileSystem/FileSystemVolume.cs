namespace dotmeer.filebrowser.FileSystem;

/// <summary>
/// Определяет фактический том пути и свободное место на нём. На Unix корень
/// пути («/») не равен тому: несколько mounts имеют общий строковый корень,
/// поэтому том выбирается по самому длинному mount point. Запрос свободного
/// места идёт по реальному пути, а не по корню строки.
/// </summary>
internal static class FileSystemVolume
{
    public static bool AreOnSameVolume(string firstPhysicalPath, string secondPhysicalPath) =>
        string.Equals(
            ResolveVolumeRoot(firstPhysicalPath),
            ResolveVolumeRoot(secondPhysicalPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static long GetAvailableFreeSpace(string physicalPath) =>
        new DriveInfo(ResolveVolumeRoot(physicalPath)).AvailableFreeSpace;

    private static string ResolveVolumeRoot(string physicalPath)
    {
        var fullPath = Path.GetFullPath(physicalPath);
        var stringRoot = Path.GetPathRoot(fullPath);
        if (OperatingSystem.IsWindows())
        {
            return string.IsNullOrEmpty(stringRoot) ? fullPath : stringRoot;
        }

        var best = string.IsNullOrEmpty(stringRoot) ? "/" : stringRoot;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(drive.Name));
            if (mount.Length > best.Length && PathInspection.IsWithin(mount, fullPath))
            {
                best = mount;
            }
        }

        return best;
    }
}
