using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.Core;

internal static unsafe class IconLoader
{
    const uint IconSmall = 0;
    const uint IconBig = 1;
    const uint IconSmall2 = 2;

    /// <summary>
    /// Иконка окна: WM_GETICON (с таймаутом — окно может висеть) → иконка класса →
    /// иконка exe процесса. owned=true только для извлечённой из exe (её надо DestroyIcon).
    /// </summary>
    public static HICON GetWindowIcon(HWND hwnd, out bool owned)
    {
        owned = false;

        foreach (uint kind in stackalloc uint[] { IconSmall2, IconSmall, IconBig })
        {
            nuint result = 0;
            PInvoke.SendMessageTimeout(
                hwnd, PInvoke.WM_GETICON, new WPARAM(kind), default,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
                200, &result);
            if (result != 0)
                return new HICON((nint)result);
        }

        nuint fromClass = PInvoke.GetClassLongPtr(hwnd, GET_CLASS_LONG_INDEX.GCLP_HICONSM);
        if (fromClass == 0)
            fromClass = PInvoke.GetClassLongPtr(hwnd, GET_CLASS_LONG_INDEX.GCLP_HICON);
        if (fromClass != 0)
            return new HICON((nint)fromClass);

        string exe = GetProcessImagePath(hwnd);
        if (exe.Length > 0)
        {
            HICON small = default;
            fixed (char* path = exe)
            {
                PInvoke.ExtractIconEx(path, 0, null, &small, 1);
            }
            if (small != default)
            {
                owned = true;
                return small;
            }
        }

        return default;
    }

    public static string GetProcessImagePath(HWND hwnd)
    {
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0)
            return "";

        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == default)
            return "";

        try
        {
            Span<char> buffer = stackalloc char[520];
            uint length = (uint)buffer.Length;
            if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length))
                return "";
            return new string(buffer[..(int)length]);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
