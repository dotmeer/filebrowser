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
    public void Load_RejectsNestedRootsAndConfigurationDirectoryOverlap()
    {
        using var workspace = new TestWorkspace();
        var nestedPath = Path.Combine(workspace.RootPath, "nested");
        var nestedRootsYaml = $"roots:\n  - name: first\n    path: '{workspace.RootPath}'\n  - name: second\n    path: '{nestedPath}'\n";

        Assert.Throws<InvalidOperationException>(() =>
            FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(nestedRootsYaml)));

        var configOverlapYaml = $"roots:\n  - name: files\n    path: '{workspace.ConfigDirectory}'\n";
        Assert.Throws<InvalidOperationException>(() =>
            FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(configOverlapYaml)));
    }
}
