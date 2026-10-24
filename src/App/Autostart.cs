using System.Diagnostics;

namespace Montab.App;

/// <summary>
/// Автозапуск. Панель запрашивает права администратора (нужны, чтобы управлять
/// окнами программ, запущенных от админа), а обычный ключ Run такие программы
/// при входе в систему поднимает с запросом UAC. Поэтому основной путь —
/// задача планировщика с «наивысшими правами», она стартует молча;
/// ключ Run остаётся запасным вариантом, если schtasks недоступен.
/// </summary>
internal static class Autostart
{
    const string TaskName = "montab";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "montab";
    /// <summary>Ключ автозапуска: полосы не показывать, ждать в трее.</summary>
    const string TrayArg = "--tray";

    public static bool IsEnabled() => TaskExists() || LegacyEnabled();

    public static void Toggle()
    {
        if (IsEnabled())
        {
            Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f");
            RemoveLegacy();
            return;
        }

        string exe = Environment.ProcessPath ?? "";
        int created = Run("schtasks.exe",
            $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" {TrayArg}\" /sc onlogon /rl highest /f");

        if (created != 0)
        {
            // Планировщик недоступен — падаем на классический Run
            TrySetLegacy(exe);
            return;
        }

        RemoveLegacy();
    }

    /// <summary>
    /// Приводит существующую задачу к текущему виду. Задача, созданная прошлой
    /// версией, запускает exe без ключа — тогда при входе в систему полоса
    /// выезжала сама. Пересоздаём её с <see cref="TrayArg"/>, не меняя
    /// остальных параметров (тот же уровень прав и триггер «при входе»).
    /// </summary>
    public static void MigrateAutostart()
    {
        MigrateLegacyRun();

        // Ключ Run — запасной путь: он тоже должен ждать в трее
        string exe = Environment.ProcessPath ?? "";
        if (LegacyEnabled())
            TrySetLegacy(exe);

        if (!TaskExists())
            return;
        if (TaskRunsInTray())
            return;

        // Пересоздание: /f перезаписывает задачу с тем же именем
        Run("schtasks.exe",
            $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" {TrayArg}\" /sc onlogon /rl highest /f");
    }

    /// <summary>Действующая задача уже запускает приложение в режиме трея?</summary>
    static bool TaskRunsInTray()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /xml")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            string xml = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000))
                return false;

            return xml.Contains(TrayArg, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Панель стала администраторской: старую запись Run убираем, иначе при
    /// входе в систему будет лишний запрос UAC и второй экземпляр.
    /// </summary>
    public static void MigrateLegacyRun()
    {
        if (LegacyEnabled() && TaskExists())
            RemoveLegacy();
    }

    static bool TaskExists() => Run("schtasks.exe", $"/query /tn \"{TaskName}\"") == 0;

    static bool LegacyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch
        {
            return false;
        }
    }

    static void TrySetLegacy(string exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, $"\"{exe}\" {TrayArg}");
        }
        catch
        {
            // прав нет — просто не включаем автозапуск
        }
    }

    static void RemoveLegacy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch
        {
            // нечего удалять / нет прав
        }
    }

    static int Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return -1;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(8000) ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
