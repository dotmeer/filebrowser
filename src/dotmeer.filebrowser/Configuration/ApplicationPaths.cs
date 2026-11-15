namespace dotmeer.filebrowser.Configuration;

/// <summary>
/// Physical paths of the files the application owns. Delete preflight refuses
/// to remove a tree that contains them.
/// </summary>
public sealed record ApplicationPaths(string ConfigPath, string DatabasePath);
