using System.Text.RegularExpressions;
using dotmeer.filebrowser.FileSystem;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace dotmeer.filebrowser.Configuration;

public sealed class FileBrowserConfiguration
{
    public FileBrowserConfiguration(int workers, IReadOnlyList<ConfiguredRoot> roots)
    {
        Workers = workers;
        Roots = roots;
        RootsByName = roots.ToDictionary(root => root.Name, StringComparer.OrdinalIgnoreCase);
    }

    public int Workers { get; }

    public IReadOnlyList<ConfiguredRoot> Roots { get; }

    public IReadOnlyDictionary<string, ConfiguredRoot> RootsByName { get; }
}

public sealed record ConfiguredRoot(string Name, string PhysicalPath);

public static partial class FileBrowserConfigurationLoader
{
    private const int DefaultWorkers = 2;

    public static FileBrowserConfiguration Load(string configPath)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
        {
            throw new InvalidOperationException("Файл конфигурации не найден.");
        }

        ConfigurationDocument document;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build();
            document = deserializer.Deserialize<ConfigurationDocument>(File.ReadAllText(fullConfigPath))
                ?? throw new InvalidOperationException("Конфигурация пуста.");
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException("Конфигурация YAML невалидна.", exception);
        }

        return Validate(document);
    }

    private static FileBrowserConfiguration Validate(ConfigurationDocument document)
    {
        var workers = document.Workers ?? DefaultWorkers;
        if (workers is < 1 or > 16)
        {
            throw new InvalidOperationException("Значение workers должно быть целым числом от 1 до 16.");
        }

        if (document.Roots is null || document.Roots.Count == 0)
        {
            throw new InvalidOperationException("Должен быть задан хотя бы один корень.");
        }

        var roots = new List<ConfiguredRoot>(document.Roots.Count);
        foreach (var root in document.Roots)
        {
            if (string.IsNullOrWhiteSpace(root.Name) || !RootNamePattern().IsMatch(root.Name))
            {
                throw new InvalidOperationException("Имя корня не соответствует допустимому формату.");
            }

            if (string.IsNullOrWhiteSpace(root.Path) || !Path.IsPathFullyQualified(root.Path))
            {
                throw new InvalidOperationException("Путь корня должен быть абсолютным.");
            }

            var fullPath = Path.GetFullPath(root.Path);
            PathInspection.ThrowIfExistingPathContainsSymlink(fullPath);
            roots.Add(new ConfiguredRoot(root.Name, fullPath));
        }

        if (roots.GroupBy(root => root.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Имена корней должны быть уникальны без учёта регистра.");
        }

        for (var index = 0; index < roots.Count; index++)
        {
            for (var otherIndex = index + 1; otherIndex < roots.Count; otherIndex++)
            {
                if (PathInspection.PathsOverlap(roots[index].PhysicalPath, roots[otherIndex].PhysicalPath))
                {
                    throw new InvalidOperationException("Корни не могут быть одинаковыми или вложенными.");
                }
            }
        }

        return new FileBrowserConfiguration(workers, roots);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RootNamePattern();

    private sealed class ConfigurationDocument
    {
        public int? Workers { get; init; }

        public List<RootDocument>? Roots { get; init; }
    }

    private sealed class RootDocument
    {
        public string? Name { get; init; }

        public string? Path { get; init; }
    }
}
