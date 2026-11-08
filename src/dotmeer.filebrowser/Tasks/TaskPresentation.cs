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
        FileTaskStatus.Completed => localizer["Завершено"],
        FileTaskStatus.Failed => localizer["Ошибка"],
        FileTaskStatus.Interrupted => localizer["Прервано"],
        _ => status.ToString()
    };

    public static string StatusCssClass(FileTaskStatus status) => status switch
    {
        FileTaskStatus.Completed => "task-status-completed",
        FileTaskStatus.Failed => "task-status-failed",
        FileTaskStatus.Interrupted => "task-status-interrupted",
        _ => "task-status-active"
    };

    public string TypeLabel(FileTaskType type) => type switch
    {
        FileTaskType.Copy => localizer["Копирование"],
        FileTaskType.Move => localizer["Перемещение"],
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

    public static string ItemStatusCssClass(string status) => status switch
    {
        "completed" => "task-item-completed",
        "failed" => "task-item-failed",
        "not_processed" => "task-item-not-processed",
        _ => "task-item-pending"
    };
}
