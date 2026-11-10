namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// Stable machine codes for task errors persisted in SQLite (tasks.error,
/// task_items.error). Executors run outside an HTTP request, so free-text
/// localized messages cannot be produced reliably at execution time;
/// presentation translates known codes and passes legacy free text through.
/// </summary>
public static class FileTaskErrorCodes
{
    public const string DestinationNotDirectory = "destination_not_directory";
    public const string DestinationUnavailable = "destination_unavailable";
    public const string RootAsSource = "root_as_source";
    public const string UnsupportedOrSymlink = "unsupported_or_symlink";
    public const string SourceUnavailable = "source_unavailable";
    public const string SourceUnreadable = "source_unreadable";
    public const string SourceAccessDenied = "source_access_denied";
    public const string SourcesNotSameParent = "sources_not_same_parent";
    public const string DestinationEqualsSourceParent = "destination_equals_source_parent";
    public const string DestinationInsideSource = "destination_inside_source";
    public const string NameConflict = "name_conflict";
    public const string PreflightFailed = "preflight_failed";
    public const string TemporaryArtifactFailed = "temporary_artifact_failed";
    public const string SkippedAfterError = "skipped_after_error";
    public const string OperationFailed = "operation_failed";
    public const string OperationAccessDenied = "operation_access_denied";
    public const string IoError = "io_error";
    public const string MoveSourceNotDeleted = "move_source_not_deleted";
    public const string ExecutorFailed = "executor_failed";
    public const string TaskInterruptedRestart = "task_interrupted_restart";
    public const string CancelledByUser = "cancelled_by_user";
    public const string SkippedAfterCancel = "skipped_after_cancel";
    public const string InsufficientSpace = "insufficient_space";
}
