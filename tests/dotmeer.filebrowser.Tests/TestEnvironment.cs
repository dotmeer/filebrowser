using System.Runtime.CompilerServices;

namespace dotmeer.filebrowser.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// ConfigPath/DatabasePath могут быть заданы в окружении процесса (переменные
    /// деплоя приложения). Их провайдер в конфигурации приложения имеет приоритет
    /// над host-конфигурацией хоста теста, поэтому фоновые значения вычищаются
    /// однократно при загрузке сборки — до запуска любого теста, без гонок.
    /// Каждый тест передаёт эти значения через TestHostFactory (UseSetting).
    /// </summary>
    [ModuleInitializer]
    public static void ClearAmbientConfigurationOverrides()
    {
        Environment.SetEnvironmentVariable("ConfigPath", null);
        Environment.SetEnvironmentVariable("DatabasePath", null);
    }
}
