namespace dotmeer.filebrowser.Tests;

[CollectionDefinition("Web application factory", DisableParallelization = true)]
public sealed class WebApplicationFactoryCollection
{
}

internal sealed class TestWorkspace : IDisposable
{
    private readonly string _path;

    public TestWorkspace()
    {
        _path = Path.Combine(Path.GetTempPath(), $"filebrowser-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_path);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(RootPath);
    }

    public string ConfigDirectory => Path.Combine(_path, "config");

    public string RootPath => Path.Combine(_path, "root");

    public string WriteConfiguration(string rootPath, int? workers = null)
    {
        var workersLine = workers is null ? string.Empty : $"workers: {workers}\n";
        var configPath = Path.Combine(ConfigDirectory, "config.yaml");
        File.WriteAllText(configPath, $"{workersLine}roots:\n  - name: files\n    path: '{rootPath}'\n");
        return configPath;
    }

    public string WriteRawConfiguration(string yaml)
    {
        var configPath = Path.Combine(ConfigDirectory, "config.yaml");
        File.WriteAllText(configPath, yaml);
        return configPath;
    }

    public void Dispose()
    {
        Directory.Delete(_path, recursive: true);
    }
}
