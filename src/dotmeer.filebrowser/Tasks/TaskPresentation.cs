using System.Globalization;
using dotmeer.filebrowser.FileSystem;
using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Tasks;

public sealed class TaskPresentation(IStringLocalizer<SharedResources> localizer)
{
    private static readonly string[] ByteUnits = ["КБ", "МБ", "ГБ", "ТБ", "ПБ"];

    public static int? GetPercent(FileTask task)
    {
        if (task.Status == FileTaskStatus.Completed)
        {
            return 100;
        }

        if (task.Type == FileTaskType.Delete)
        {
            if (task.Status != FileTaskStatus.Running || task.ItemsTotal is not > 0)
            {
                return null;
            }

            return (int)Math.Clamp(task.ItemsDone * 100.0 / task.ItemsTotal.Value, 0, 100);
        }

        if (task.Status != FileTaskStatus.Running || task.BytesTotal is not > 0)
        {
            return null;
        }

        var percent = task.BytesDone * 100.0 / task.BytesTotal.Value;
        return (int)Math.Clamp(percent, 0, 100);
    }

    public string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes.Value} {localizer["Б"]}");
        }

        var value = (double)bytes.Value;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < ByteUnits.Length - 1);

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {localizer[ByteUnits[unit]]}");
    }

    public string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return FormatBytes(0) + localizer["/с"];
        }

        return FormatBytes((long)Math.Round(bytesPerSecond)) + localizer["/с"];
    }

    public string FormatReference(FileRef file) =>
        string.IsNullOrEmpty(file.Path) ? file.Root : $"{file.Root}/{file.Path}";

    public string StatusLabel(FileTaskStatus status) => status switch
    {
        FileTaskStatus.Queued => localizer["В очереди"],
        FileTaskStatus.Scanning => localizer["Проверка и подсчёт"],
        FileTaskStatus.Running => localizer["Выполняется"],
        FileTaskStatus.Cancelling => localizer["Отменяется…"],
        FileTaskStatus.Completed => localizer["Завершено"],
        FileTaskStatus.Failed => localizer["Ошибка"],
        FileTaskStatus.Cancelled => localizer["Отменено"],
        FileTaskStatus.Interrupted => localizer["Прервано"],
        _ => status.ToString()
    };

    public static string StatusCssClass(FileTaskStatus status) => status switch
    {
        FileTaskStatus.Completed => "task-status-completed",
        FileTaskStatus.Failed => "task-status-failed",
        FileTaskStatus.Cancelled => "task-status-cancelled",
        FileTaskStatus.Interrupted => "task-status-interrupted",
        _ => "task-status-active"
    };

    public string TypeLabel(FileTaskType type) => type switch
    {
        FileTaskType.Copy => localizer["Копирование"],
        FileTaskType.Move => localizer["Перемещение"],
        FileTaskType.Delete => localizer["Удаление"],
        FileTaskType.Upload => localizer["Загрузка"],
        _ => type.ToString()
    };

    public string ItemStatusLabel(string status) => status switch
    {
        "pending" => localizer["Ожидает"],
        "completed" => localizer["Обработан"],
        "failed" => localizer["Ошибка"],
        "not_processed" => localizer["Не обработан"],
        _ => status
    };

    /// <summary>
    /// Translates a persisted task error (stable code from
    /// <see cref="FileTaskErrorCodes"/>) for display. Legacy rows written
    /// before codes were introduced contain free text and pass through
    /// unchanged.
    /// </summary>
    public string ErrorText(string? error, FileTaskType taskType) => error switch
    {
        null => string.Empty,
        FileTaskErrorCodes.DestinationNotDirectory => localizer["Назначение не является каталогом."],
        FileTaskErrorCodes.DestinationUnavailable => localizer["Не удалось открыть каталог назначения."],
        FileTaskErrorCodes.RootAsSource => localizer["Корень нельзя использовать как источник."],
        FileTaskErrorCodes.UnsupportedOrSymlink => localizer["Обнаружен неподдерживаемый объект или символическая ссылка."],
        FileTaskErrorCodes.SourceUnavailable => localizer["Источник недоступен."],
        FileTaskErrorCodes.SourceUnreadable => localizer["Не удалось прочитать источник."],
        FileTaskErrorCodes.SourceAccessDenied => localizer["Нет доступа к источнику."],
        FileTaskErrorCodes.SourcesNotSameParent => localizer["Источники должны находиться в одной родительской папке."],
        FileTaskErrorCodes.DestinationEqualsSourceParent => localizer["Назначение совпадает с родительской папкой источников."],
        FileTaskErrorCodes.DestinationInsideSource => localizer["Каталог назначения находится внутри источника или совпадает с ним."],
        FileTaskErrorCodes.NameConflict => localizer["Конфликт имён: объект с таким именем уже существует."],
        FileTaskErrorCodes.PreflightFailed => localizer["Предварительная проверка не пройдена."],
        FileTaskErrorCodes.TemporaryArtifactFailed => localizer["Не удалось создать временный объект."],
        FileTaskErrorCodes.SkippedAfterError => localizer["Не обработан после ошибки предыдущего объекта."],
        FileTaskErrorCodes.OperationFailed => localizer["Файловая операция не выполнена."],
        FileTaskErrorCodes.OperationAccessDenied => localizer["Нет доступа."],
        FileTaskErrorCodes.IoError => taskType switch
        {
            FileTaskType.Move => localizer["Ошибка перемещения."],
            FileTaskType.Delete => localizer["Ошибка удаления."],
            FileTaskType.Upload => localizer["Ошибка загрузки."],
            _ => localizer["Ошибка копирования."]
        },
        FileTaskErrorCodes.UploadAborted => localizer["Загрузка прервана: соединение потеряно."],
        FileTaskErrorCodes.UploadIncomplete => localizer["Загрузка не завершена: получено меньше данных, чем заявлено."],
        FileTaskErrorCodes.FileTooLarge => localizer["Файл больше допустимого размера."],
        FileTaskErrorCodes.MoveSourceNotDeleted => localizer["Назначение создано, но источник не удалён. Источник мог сохраниться полностью или частично."],
        FileTaskErrorCodes.ExecutorFailed => localizer["Исполнитель задачи завершился ошибкой."],
        FileTaskErrorCodes.TaskInterruptedRestart => localizer["Задача прервана перезапуском приложения."],
        FileTaskErrorCodes.CancelledByUser => localizer["Задача отменена пользователем."],
        FileTaskErrorCodes.SkippedAfterCancel => localizer["Не обработан: задача отменена."],
        FileTaskErrorCodes.InsufficientSpace => localizer["Недостаточно свободного места в каталоге назначения."],
        FileTaskErrorCodes.ReadOnlyObject => localizer["Объект доступен только для чтения: удаление невозможно."],
        FileTaskErrorCodes.ServiceFileProtected => localizer["В выбранном дереве находятся служебные файлы приложения."],
        FileTaskErrorCodes.ArtifactInUse => localizer["В выбранном дереве есть временные объекты активных задач."],
        FileTaskErrorCodes.DeleteIncomplete => localizer["Объект скрыт, но не удалён полностью. Место освободится при перезапуске приложения."],
        _ => error
    };

    public static string ItemStatusCssClass(string status) => status switch
    {
        "completed" => "task-item-completed",
        "failed" => "task-item-failed",
        "not_processed" => "task-item-not-processed",
        _ => "task-item-pending"
    };
}
