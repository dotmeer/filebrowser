namespace dotmeer.filebrowser.Archive;

/// <summary>
/// Лимиты скачивания архивом (SPEC §12.2). Значения — константы кода,
/// а не параметры config.yaml: конфигурация приложения ограничена
/// `workers` и корнями, а лимиты являются защитой от исчерпания
/// ресурсов, а не настройкой поведения.
/// </summary>
public static class ArchiveLimits
{
    /// <summary>Максимальное число записей в архиве.</summary>
    public const int MaxEntries = 50_000;

    /// <summary>Максимальный суммарный объём (несжатый), байты.</summary>
    public const long MaxTotalBytes = 200L * 1024 * 1024 * 1024;

    /// <summary>Максимальная глубина вложенности записи.</summary>
    public const int MaxDepth = 128;

    /// <summary>Максимальная длина имени записи внутри архива, байты UTF-8.</summary>
    public const int MaxEntryPathBytes = 1024;

    /// <summary>Одновременно формируемых архивов.</summary>
    public const int MaxConcurrentArchives = 2;

    /// <summary>Бюджет предпроверки дерева.</summary>
    public static readonly TimeSpan PreflightBudget = TimeSpan.FromSeconds(60);

    /// <summary>Время жизни тикета выбора.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(120);

    /// <summary>Максимальное число живых тикетов в памяти.</summary>
    public const int MaxLiveTickets = 8;
}
