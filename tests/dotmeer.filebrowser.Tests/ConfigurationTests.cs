using dotmeer.filebrowser.Configuration;

namespace dotmeer.filebrowser.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Load_AppliesDefaultWorkersAndNormalizesRootPath()
    {
        using var workspace = new TestWorkspace();

        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));

        Assert.Equal(2, configuration.Workers);
        Assert.Single(configuration.Roots);
        Assert.Equal(Path.GetFullPath(workspace.RootPath), configuration.Roots[0].PhysicalPath);
    }

    [Theory]
    [InlineData("workers: 0\nroots: []\n")]
    [InlineData("roots:\n  - name: invalid name\n    path: /data\n")]
    [InlineData("roots:\n  - name: files\n    path: relative\n")]
    [InlineData("roots:\n  - name: files\n    path: /data\nunknown: true\n")]
    public void Load_RejectsInvalidConfiguration(string yaml)
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidOperationException>(() =>
            FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(yaml)));
    }

    [Fact]
    public void Load_RejectsNestedRoots()
    {
        using var workspace = new TestWorkspace();
        var nestedPath = Path.Combine(workspace.RootPath, "nested");
        var nestedRootsYaml = $"roots:\n  - name: first\n    path: '{workspace.RootPath}'\n  - name: second\n    path: '{nestedPath}'\n";

        Assert.Throws<InvalidOperationException>(() =>
            FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(nestedRootsYaml)));
    }

    [Fact]
    public void Load_AppliesDefaultUploadLimit()
    {
        using var workspace = new TestWorkspace();

        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));

        Assert.Equal(UploadOptions.DefaultMaxFileBytes, configuration.Uploads.MaxFileBytes);
        Assert.Equal(8L * 1024 * 1024 * 1024, configuration.Uploads.MaxFileBytes);
    }

    [Fact]
    public void Load_ReadsConfiguredUploadLimit()
    {
        using var workspace = new TestWorkspace();
        var yaml = $"uploads:\n  maxFileBytes: 1024\nroots:\n  - name: files\n    path: '{workspace.RootPath}'\n";

        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(yaml));

        Assert.Equal(1024, configuration.Uploads.MaxFileBytes);
    }

    [Theory]
    [InlineData("uploads:\n  maxFileBytes: 0\nroots:\n  - name: files\n    path: '{0}'\n")]
    [InlineData("uploads:\n  maxFileBytes: -1\nroots:\n  - name: files\n    path: '{0}'\n")]
    [InlineData("uploads:\n  maxFileBytes: not-a-number\nroots:\n  - name: files\n    path: '{0}'\n")]
    [InlineData("uploads:\n  maxFileBytes: 10\n  unknown: true\nroots:\n  - name: files\n    path: '{0}'\n")]
    public void Load_RejectsInvalidUploadConfiguration(string yamlTemplate)
    {
        using var workspace = new TestWorkspace();
        var yaml = string.Format(yamlTemplate, workspace.RootPath);

        Assert.Throws<InvalidOperationException>(() =>
            FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(yaml)));
    }

    [Fact]
    public void Load_AllowsConfigurationDirectoryOverlapAndFilesystemRoot()
    {
        using var workspace = new TestWorkspace();
        var configOverlapYaml = $"roots:\n  - name: files\n    path: '{workspace.ConfigDirectory}'\n";

        var overlapConfiguration = FileBrowserConfigurationLoader.Load(
            workspace.WriteRawConfiguration(configOverlapYaml));

        Assert.Single(overlapConfiguration.Roots);
        Assert.Equal(Path.GetFullPath(workspace.ConfigDirectory), overlapConfiguration.Roots[0].PhysicalPath);

        var filesystemRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var filesystemRootYaml = $"roots:\n  - name: system\n    path: '{filesystemRoot}'\n";

        var systemConfiguration = FileBrowserConfigurationLoader.Load(
            workspace.WriteRawConfiguration(filesystemRootYaml));

        Assert.Single(systemConfiguration.Roots);
        Assert.Equal(Path.GetFullPath(filesystemRoot), systemConfiguration.Roots[0].PhysicalPath);
    }
}
