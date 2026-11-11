namespace dotmeer.filebrowser.Configuration;

/// <summary>
/// Physical paths of the files the application owns. Delete preflight refuses
/// to remove a tree that contains them (SPEC §9.5).
/// </summary>
public sealed record ApplicationPaths(string ConfigPath, string DatabasePath);
