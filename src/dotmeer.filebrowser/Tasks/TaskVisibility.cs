using dotmeer.filebrowser.Access;

namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// Область видимости задач для чтения, отмены и очистки истории. Полный
/// доступ (ANONYMOUS, admin, системные операции) видит все задачи, включая
/// старые и задачи удалённых учётных записей. Обычная учётная запись видит
/// только задачи со своим неизменяемым ID автора; строки с NULL-автором
/// (анонимные и старые) ей не принадлежат.
/// </summary>
public readonly record struct TaskVisibility(Guid? AuthorUserId, bool AllAuthors)
{
    /// <summary>Без ограничения по автору: admin, ANONYMOUS и системные операции.</summary>
    public static TaskVisibility Everything => new(null, true);

    /// <summary>Задачи конкретной учётной записи.</summary>
    public static TaskVisibility ForUser(Guid userId) => new(userId, false);

    /// <summary>Ничего не видно: вызывающий не имеет действующей учётки.</summary>
    public static TaskVisibility None => new(Guid.Empty, false);

    public static TaskVisibility From(FileActor actor) =>
        actor.IsFullAccess
            ? Everything
            : actor.UserId is { } userId ? ForUser(userId) : None;

    /// <summary>ID для фильтра SQL; null означает «без ограничения».</summary>
    public Guid? RequiredAuthorUserId => AllAuthors ? null : AuthorUserId;
}
