using System.Globalization;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public static class TaskPresentation
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

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        if (bytes < 1024)
        {
            return $"{bytes.Value.ToString(CultureInfo.InvariantCulture)} Б";
        }

        var value = (double)bytes.Value;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < ByteUnits.Length - 1);

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {ByteUnits[unit]}");
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0 Б/с";
        }

        return $"{FormatBytes((long)Math.Round(bytesPerSecond))}/с";
    }

    public static string FormatReference(FileRef file) =>
        string.IsNullOrEmpty(file.Path) ? file.Root : $"{file.Root}/{file.Path}";

    public static string StatusLabel(FileTaskStatus status) => status switch
    {
        FileTaskStatus.Queued => "В очереди",
        FileTaskStatus.Scanning => "Проверка и подсчёт",
        FileTaskStatus.Running => "Выполняется",
        FileTaskStatus.Completed => "Завершено",
        FileTaskStatus.Failed => "Ошибка",
        FileTaskStatus.Interrupted => "Прервано",
        _ => status.ToString()
    };

    public static string StatusCssClass(FileTaskStatus status) => status switch
    {
        FileTaskStatus.Completed => "task-status-completed",
        FileTaskStatus.Failed => "task-status-failed",
        FileTaskStatus.Interrupted => "task-status-interrupted",
        _ => "task-status-active"
    };

    public static string TypeLabel(FileTaskType type) => type switch
    {
        FileTaskType.Copy => "Копирование",
        FileTaskType.Move => "Перемещение",
        _ => type.ToString()
    };

    public static string ItemStatusLabel(string status) => status switch
    {
        "pending" => "Ожидает",
        "completed" => "Обработан",
        "failed" => "Ошибка",
        "not_processed" => "Не обработан",
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
