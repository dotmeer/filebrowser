using System.IO.Compression;
using System.Text;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Предпроверка и запись архива (SPEC §12.2): структура относительно
/// базовой папки, пустые каталоги, исключение служебных объектов,
/// отказ при неподдерживаемых объектах, лимиты, запись без сжатия
/// в не-seekable поток.
/// </summary>
public sealed class ArchivePlanTests
{
    [Fact]
    public void Plan_BuildsBaseRelativeEntriesAndExcludesServiceObjects()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "report.txt"), "report");
        var photos = Path.Combine(workspace.RootPath, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "a.jpg"), "image");
        Directory.CreateDirectory(Path.Combine(photos, "empty"));
        var artifact = Path.Combine(photos, ".filebrowser-task-1");
        Directory.CreateDirectory(artifact);
        File.WriteAllText(Path.Combine(artifact, "hidden.txt"), "hidden");

        var plan = CreateService(workspace).Plan(new FileRef("files", string.Empty), [], CancellationToken.None);

        Assert.Equal("files.zip", plan.ArchiveName);
        Assert.Equal(
            new[] { "photos", "photos/a.jpg", "photos/empty", "report.txt" },
            plan.Entries.Select(entry => entry.Name).ToArray());
        Assert.DoesNotContain(plan.Entries, entry => entry.Name.Contains(".filebrowser-", StringComparison.Ordinal));
        Assert.Equal("report".Length + "image".Length, plan.TotalBytes);
    }

    [Fact]
    public void Plan_WithNames_ReturnsOnlySelectedObjectsAndFolderArchiveName()
    {
        using var workspace = new TestWorkspace();
        var photos = Path.Combine(workspace.RootPath, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "a.jpg"), "image");
        File.WriteAllText(Path.Combine(workspace.RootPath, "other.txt"), "other");

        var service = CreateService(workspace);
        var plan = service.Plan(new FileRef("files", "photos"), ["a.jpg"], CancellationToken.None);

        Assert.Equal("photos.zip", plan.ArchiveName);
        Assert.Equal(new[] { "a.jpg" }, plan.Entries.Select(entry => entry.Name).ToArray());
        Assert.DoesNotContain('/', plan.Entries[0].Name);
    }

    [Fact]
    public void Plan_SingleSelectedFolder_UsesFolderAsBaseAndKeepsContentsAtArchiveRoot()
    {
        using var workspace = new TestWorkspace();
        var photos = Path.Combine(workspace.RootPath, "photos");
        Directory.CreateDirectory(photos);
        Directory.CreateDirectory(Path.Combine(photos, "sub"));
        File.WriteAllText(Path.Combine(photos, "a.jpg"), "image");
        File.WriteAllText(Path.Combine(photos, "sub", "b.txt"), "text");
        File.WriteAllText(Path.Combine(workspace.RootPath, "other.txt"), "other");

        var plan = CreateService(workspace).Plan(new FileRef("files", string.Empty), ["photos"], CancellationToken.None);

        Assert.Equal("photos.zip", plan.ArchiveName);
        Assert.Equal(
            new[] { "a.jpg", "sub", "sub/b.txt" },
            plan.Entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Plan_EmptySelectionOfEmptyFolder_ReturnsEmptyPlan()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "empty"));

        var plan = CreateService(workspace).Plan(new FileRef("files", "empty"), [], CancellationToken.None);

        Assert.Empty(plan.Entries);
        Assert.Equal(0, plan.TotalBytes);
        Assert.Equal("empty.zip", plan.ArchiveName);
    }

    [Theory]
    [InlineData(".filebrowser-task")]
    [InlineData("nested/name")]
    [InlineData("..")]
    public void Plan_RejectsInvalidSelectionNames(string name)
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a");
        var service = CreateService(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(
            () => service.Plan(new FileRef("files", string.Empty), [name], CancellationToken.None));
    }

    [Fact]
    public void Plan_RejectsFileAsBaseFolder()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a");
        var service = CreateService(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(
            () => service.Plan(new FileRef("files", "a.txt"), [], CancellationToken.None));
    }

    [Fact]
    public void Plan_RejectsVirtualRootTopLevel()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a");
        var service = CreateService(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(
            () => service.Plan(new FileRef(VirtualRoot.VirtualRootName, string.Empty), [], CancellationToken.None));
        Assert.Throws<InvalidFileSystemRequestException>(
            () => service.Plan(
                new FileRef(VirtualRoot.VirtualRootName, string.Empty),
                ["files", "other"],
                CancellationToken.None));
    }

    [Fact]
    public void Plan_UnderVirtualRootTopLevel_SingleRoot_ReturnsRootArchive()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a");
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "b");

        var service = CreateService(workspace);
        var plan = service.Plan(
            new FileRef(VirtualRoot.VirtualRootName, string.Empty),
            ["files"],
            CancellationToken.None);

        Assert.Equal("files.zip", plan.ArchiveName);
        Assert.Equal(
            new[] { "a.txt", "sub", "sub/b.txt" },
            plan.Entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Plan_UnderVirtualRoot_DelegatesToRealRoot()
    {
        using var workspace = new TestWorkspace();
        var photos = Path.Combine(workspace.RootPath, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "a.jpg"), "image");

        var service = CreateService(workspace);
        var plan = service.Plan(
            new FileRef(VirtualRoot.VirtualRootName, "files/photos"),
            [],
            CancellationToken.None);

        Assert.Equal("photos.zip", plan.ArchiveName);
        Assert.Equal(new[] { "a.jpg" }, plan.Entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Plan_RejectsTreeDeeperThanLimit()
    {
        using var workspace = new TestWorkspace();
        var deepPath = Path.Combine(workspace.RootPath, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", ArchiveLimits.MaxDepth + 2)));
        try
        {
            Directory.CreateDirectory(deepPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.True(Directory.Exists(deepPath));
        var service = CreateService(workspace);

        Assert.Throws<ArchiveLimitExceededException>(
            () => service.Plan(new FileRef("files", string.Empty), [], CancellationToken.None));
    }

    [Fact]
    public void Plan_RejectsEntryNameLongerThanLimit()
    {
        using var workspace = new TestWorkspace();
        var segment = new string('ф', 60);
        var longPath = Path.Combine(workspace.RootPath, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(segment, 10)));
        try
        {
            Directory.CreateDirectory(longPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.True(Directory.Exists(longPath));
        var service = CreateService(workspace);

        Assert.Throws<ArchiveLimitExceededException>(
            () => service.Plan(new FileRef("files", string.Empty), [], CancellationToken.None));
    }

    [Fact]
    public void Plan_RejectsSymlinkInsideTree()
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "content");
        var link = Path.Combine(workspace.RootPath, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var service = CreateService(workspace);

        Assert.Throws<UnsupportedFileSystemObjectException>(
            () => service.Plan(new FileRef("files", string.Empty), [], CancellationToken.None));
    }

    [Fact]
    public void Plan_FailsWhenPreflightBudgetExceeded()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "sub"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "sub", "b.txt"), "b");

        var service = new FileSystemArchiveService(
            new PathResolver(LoadConfiguration(workspace)),
            new SteppingTimeProvider(TimeSpan.FromSeconds(ArchiveLimits.PreflightBudget.TotalSeconds)),
            NullLogger<FileSystemArchiveService>.Instance);

        Assert.Throws<ArchiveScanTimeoutException>(
            () => service.Plan(new FileRef("files", string.Empty), [], CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_ProducesStoredEntriesInNonSeekableStream()
    {
        using var workspace = new TestWorkspace();
        var photos = Path.Combine(workspace.RootPath, "photos");
        Directory.CreateDirectory(photos);
        Directory.CreateDirectory(Path.Combine(photos, "empty"));
        await File.WriteAllTextAsync(Path.Combine(photos, "a.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(photos, "файл.txt"), "привет");

        var service = CreateService(workspace);
        var plan = service.Plan(new FileRef("files", "photos"), [], CancellationToken.None);
        using var buffer = new MemoryStream();
        using (var sink = new NonSeekableStream(buffer))
        {
            await service.WriteAsync(plan, sink, CancellationToken.None);
        }

        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(
            new[] { "a.txt", "empty/", "файл.txt" },
            archive.Entries.Select(entry => entry.FullName).ToArray());

        var stored = archive.Entries.Single(entry => entry.FullName == "a.txt");
        Assert.Equal(stored.CompressedLength, stored.Length);
        using var reader = new StreamReader(stored.Open());
        Assert.Equal("content", await reader.ReadToEndAsync());

        var directory = archive.Entries.Single(entry => entry.FullName == "empty/");
        Assert.Equal(0, directory.Length);
    }

    [Fact]
    public async Task WriteAsync_RejectsCancelledRequestBeforeWriting()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a");
        var service = CreateService(workspace);
        var plan = service.Plan(new FileRef("files", string.Empty), [], CancellationToken.None);
        using var buffer = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.WriteAsync(plan, buffer, new CancellationToken(canceled: true)));
        Assert.Equal(0, buffer.Length);
    }

    private static FileSystemArchiveService CreateService(TestWorkspace workspace) =>
        new(
            new PathResolver(LoadConfiguration(workspace)),
            TimeProvider.System,
            NullLogger<FileSystemArchiveService>.Instance);

    private static FileBrowserConfiguration LoadConfiguration(TestWorkspace workspace) =>
        FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));

    private sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp()
        {
            _timestamp += step.Ticks;
            return _timestamp;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }
}
