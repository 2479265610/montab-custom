using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Montab.App;

/// <summary>
/// Приложение запускается от администратора (иначе нельзя управлять окнами
/// программ с повышением прав). Побочный эффект — UIPI: сообщения от процессов
/// с меньшими правами до наших окон не доходят, SendMessage возвращает
/// ERROR_ACCESS_DENIED. А почти весь нужный нам ввод приходит именно оттуда:
/// Explorer шлёт клик по значку в трее, перерегистрацию appbar, смену
/// дисплеев и рабочей области. Здесь эти сообщения разрешаются точечно.
/// </summary>
internal static unsafe class MessageFilter
{
    const uint FilterAllow = 1;
    /// <summary>WM_COPYGLOBALDATA: нужен, чтобы Explorer мог слать нам DropFiles.</summary>
    const uint WmCopyGlobalData = 0x0049;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ChangeWindowMessageFilterEx(void* hwnd, uint msg, uint action, void* changeInfo);

    /// <summary>Разрешить окну перечисленные сообщения от процессов с меньшими правами.</summary>
    public static void Allow(HWND hwnd, params uint[] messages)
    {
        void* handle = hwnd.Value;
        if (handle == null)
            return;

        Apply(handle, WmCopyGlobalData);
        foreach (uint msg in messages)
            Apply(handle, msg);
    }

    static void Apply(void* hwnd, uint msg)
        => ChangeWindowMessageFilterEx(hwnd, msg, FilterAllow, null);
}
