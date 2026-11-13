using System.Net.Sockets;
using System.Runtime.InteropServices;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Linux: FIFO, Unix socket и устройства не должны попадать в операции чтения,
/// поиска, архива или удаления. Тесты выполняются только на Linux; прогон на
/// Windows не является доказательством.
/// </summary>
public sealed class LinuxSpecialFileTests
{
    [Fact]
    public void SpecialFiles_AreRejectedByResolverListingSearchAndArchive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var fifoPath = Path.Combine(workspace.RootPath, "pipe");
        Assert.Equal(0, MkFifo(fifoPath, 0x1A4));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var socketPath = Path.Combine(workspace.RootPath, "socket");
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));
        var resolver = new PathResolver(configuration);
        var reader = new FileSystemReadService(configuration, resolver);

        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => resolver.ResolveExisting(new FileRef("files", "pipe")));
        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => resolver.ResolveExisting(new FileRef("files", "socket")));
        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => reader.OpenFile(new FileRef("files", "pipe")));

        var listing = reader.List(new FileRef("files", string.Empty));
        Assert.Equal("unsupported", listing.Entries.Single(entry => entry.Name == "pipe").Kind);
        Assert.Equal("unsupported", listing.Entries.Single(entry => entry.Name == "socket").Kind);

        var search = new FileSystemSearchService(configuration, resolver, new SearchMetrics());
        var result = search.Search(
            new FileRef("files", string.Empty),
            "pipe",
            ext: null,
            extGlobs: null,
            limit: FileSystemSearchService.DefaultResultLimit,
            cancellationToken: CancellationToken.None);
        Assert.DoesNotContain(result.Results, entry => entry.Name == "pipe");

        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => FileSystemTreeWalker.EnumerateValidated(fifoPath).ToList());

        var archive = new FileSystemArchiveService(
            resolver,
            TimeProvider.System,
            NullLogger<FileSystemArchiveService>.Instance);
        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => archive.Plan(new FileRef("files", string.Empty), ["pipe"], CancellationToken.None));
    }

    [Fact]
    public void Resolver_RejectsCharacterDeviceUnderDevRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var configuration = FileBrowserConfigurationLoader.Load(
            workspace.WriteRawConfiguration("roots:\n  - name: devices\n    path: /dev\n"));
        var resolver = new PathResolver(configuration);

        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => resolver.ResolveExisting(new FileRef("devices", "null")));
    }

    [Fact]
    public void UnixFileType_FailsClosedWhenTypeCannotBeDetermined()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var missing = Path.Combine(Path.GetTempPath(), $"filebrowser-missing-{Guid.NewGuid():N}", "object");
        Assert.True(UnixFileType.IsSpecialFile(missing));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mode);
}
