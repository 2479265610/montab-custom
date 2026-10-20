using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.App;

/// <summary>
/// Невидимое окно-«представитель» в панели задач: панель и страница настроек
/// созданы как WS_EX_TOOLWINDOW (в панель задач не попадают — иначе они крадут
/// фокус у активного окна), а пользователю нужен привычный значок слева внизу.
/// Это окно размером 1×1 с полной прозрачностью: снаружи его не видно,
/// но в панели задач оно есть. Клик по значку открывает страницу настроек.
/// </summary>
internal sealed unsafe class TaskbarHub
{
    const string ClassName = "montab.hub";
    const int WaInactive = 0;
    const uint WmActivate = 0x0006;
    static bool s_classRegistered;

    readonly PanelHost _host;
    HWND _hwnd;

    public TaskbarHub(PanelHost host) => _host = host;

    public HWND Handle => _hwnd;

    public void Create(HINSTANCE hInstance)
    {
        EnsureClass(hInstance);

        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_APPWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED,
            ClassName,
            "montab",
            WINDOW_STYLE.WS_POPUP,
            0, 0, 1, 1,
            default, default, hInstance, WindowRef.Pin(this));

        if (_hwnd == default)
        {
            Debug.WriteLine("TaskbarHub: CreateWindowEx failed");
            return;
        }

        // Полностью прозрачное: цвет ключа не нужен, альфа = 0
        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), 0,
            LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

        // Показать без активации — иначе окно немедленно заберёт фокус
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        // Свернуть: кнопка в панели задач остаётся, а фокус окно не держит
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
    }

    public void Destroy()
    {
        if (_hwnd != default)
            PInvoke.DestroyWindow(_hwnd);
    }

    static void EnsureClass(HINSTANCE hInstance)
    {
        if (s_classRegistered)
            return;

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &StaticWndProc,
                hInstance = hInstance,
                hIcon = PInvoke.LoadIcon(hInstance, (char*)1),
                lpszClassName = className,
            };

            if (PInvoke.RegisterClassEx(&wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed");
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (msg == PInvoke.WM_NCCREATE)
                WindowRef.Bind(hwnd, lParam);

            var self = WindowRef.Get(hwnd) as TaskbarHub;
            var result = self?.HandleMessage(hwnd, msg, wParam, lParam)
                ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

            if (msg == PInvoke.WM_NCDESTROY)
                WindowRef.Release(hwnd);
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TaskbarHub WndProc: {ex}");
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_NCCREATE:
                _hwnd = hwnd;
                break;

            case WmActivate:
            {
                // Клик по кнопке в панели задач разворачивает окно — сразу
                // открываем настройки и снова сворачиваем, чтобы не держать фокус.
                int action = (int)(wParam.Value & 0xFFFF);
                if (action != WaInactive)
                {
                    _host.OpenSettingsFromTaskbar();
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
                }
                return new LRESULT(0);
            }

            case PInvoke.WM_CLOSE:
                // Закрытие через Alt+F4/панель задач не должно убивать панель
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
                return new LRESULT(0);

            case PInvoke.WM_DESTROY:
                _hwnd = default;
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
