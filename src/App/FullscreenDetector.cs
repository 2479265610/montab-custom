using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.App;

/// <summary>
/// Ищет окно, занявшее экран целиком, — чтобы панель могла уступить ему место.
/// Полоса, зарезервированная через AppBar, сужает только рабочую область,
/// а окна полноэкранного режима её не читают: они растягиваются на весь
/// монитор и уходят под панель, потому что она topmost.
/// </summary>
internal static unsafe class FullscreenDetector
{
    /// <summary>
    /// На этом мониторе сверху лежит полноэкранное окно?
    /// Смотрим z-order, а не foreground: видео продолжает занимать экран,
    /// даже когда фокус ушёл на другой монитор, и панель не должна всплывать.
    /// </summary>
    public static bool TryFind(HMONITOR monitor, out HWND fullscreen)
    {
        fullscreen = default;

        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref mi))
            return false;

        for (HWND hwnd = PInvoke.GetTopWindow(default); hwnd != default;
             hwnd = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_HWNDNEXT))
        {
            if (!IsCandidate(hwnd, monitor))
                continue;

            if (!TryVisibleBounds(hwnd, out RECT bounds))
                return false;

            if (CoversWholeMonitor(bounds, mi.rcMonitor))
            {
                // Обычное развёрнутое окно занимает ровно рабочую область и до
                // целого монитора не достаёт (её сужает наша же полоса appbar'а).
                // Если же панель задач скрыта автоматически и рабочая область
                // совпала с монитором, отличить помогает признак «развёрнуто»:
                // у развёрнутого окна границы не выходят за рабочую область.
                if (PInvoke.IsZoomed(hwnd) && !ExceedsWorkArea(bounds, mi.rcWork))
                    return false;

                fullscreen = hwnd;
                return true;
            }

            // Мелкое окно поверх видео (настройки плеера, уведомление, всплывающая
            // панель управления) обзор не закрывает — смотрим, что под ним.
            if (IsOverlayPopup(hwnd, bounds, mi.rcMonitor))
                continue;

            // Первое же обычное окно этого монитора закрывает обзор: оно не во
            // весь экран, значит полноэкранного режима здесь нет.
            return false;
        }

        return false;
    }

    /// <summary>Мелкое всплывающее окно поверх полноэкранного — не самостоятельный экран.</summary>
    static bool IsOverlayPopup(HWND hwnd, RECT bounds, RECT screen)
    {
        bool small = (bounds.right - bounds.left) * 2 < screen.right - screen.left
                     || (bounds.bottom - bounds.top) * 2 < screen.bottom - screen.top;
        if (!small)
            return false;
        return PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != default
               || (PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
                   & (nint)(uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0;
    }

    /// <summary>Окно вообще может претендовать на роль полноэкранного.</summary>
    static bool IsCandidate(HWND hwnd, HMONITOR monitor)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
            return false;

        var exStyle = (WINDOW_EX_STYLE)unchecked((uint)PInvoke.GetWindowLongPtr(
            hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));
        // Панели ввода, подсказки и прочие всплывающие окна экран не занимают
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0
            && (exStyle & WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
            return false;

        if (IsShellSurface(hwnd))
            return false;

        // Cloaked: приостановленные UWP и окна других виртуальных рабочих столов
        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &cloaked, sizeof(int));
        if (cloaked != 0)
            return false;

        return PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST) == monitor;
    }

    static bool CoversWholeMonitor(RECT bounds, RECT screen)
    {
        const int Slack = 2; // у DWM и эксклюзивного режима расхождение в пиксель
        return bounds.left <= screen.left + Slack
               && bounds.top <= screen.top + Slack
               && bounds.right >= screen.right - Slack
               && bounds.bottom >= screen.bottom - Slack;
    }

    static bool ExceedsWorkArea(RECT bounds, RECT work)
    {
        const int Slack = 2;
        return bounds.left < work.left - Slack
               || bounds.top < work.top - Slack
               || bounds.right > work.right + Slack
               || bounds.bottom > work.bottom + Slack;
    }

    /// <summary>
    /// Видимые границы окна. DWM отдаёт их без невидимой рамки изменения
    /// размера: у развёрнутого окна они совпадают с рабочей областью,
    /// у полноэкранного — с целым монитором.
    /// </summary>
    static bool TryVisibleBounds(HWND hwnd, out RECT bounds)
    {
        if (!PInvoke.GetWindowRect(hwnd, out bounds))
            return false;

        RECT dwm = default;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &dwm, sizeof(RECT));
        // При отказе DWM структура остаётся пустой — тогда годится GetWindowRect
        if (dwm.right > dwm.left && dwm.bottom > dwm.top)
            bounds = dwm;
        return true;
    }

    /// <summary>Рабочий стол, оболочка и наши собственные окна.</summary>
    static bool IsShellSurface(HWND hwnd)
    {
        if (hwnd == PInvoke.GetDesktopWindow() || hwnd == PInvoke.GetShellWindow())
            return true;

        Span<char> name = stackalloc char[64];
        int length;
        fixed (char* p = name)
            length = PInvoke.GetClassName(hwnd, p, name.Length);
        if (length <= 0)
            return false;

        var cls = name[..length];
        return cls.SequenceEqual("montab.panel")
            || cls.SequenceEqual("montab.marks")
            || cls.SequenceEqual("montab.scrollbar")
            || cls.SequenceEqual("Progman")
            || cls.SequenceEqual("WorkerW")
            || cls.SequenceEqual("Shell_TrayWnd");
    }
}