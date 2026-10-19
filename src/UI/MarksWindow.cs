using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.App;
using Montab.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.UI;

/// <summary>
/// Оверлей с «ручками» перетаскивания: две косые в левом нижнем углу карточки,
/// которые вместе с углом читаются как треугольник.
/// Отдельное owned-layered-окно поверх панели, потому что DWM компонует
/// миниатюры поверх GDI-отрисовки панели — нарисованное прямо в карточке
/// было бы закрыто превью. Окно «дырявое» (HTTRANSPARENT): мышь идёт в панель.
/// </summary>
internal sealed unsafe class MarksWindow
{
    const string ClassName = "montab.marks";

    static bool s_classRegistered;

    readonly HWND _owner;
    HWND _hwnd;

    int _width, _height;

    HDC _dibDc;
    HBITMAP _dib;
    HGDIOBJ _dibOld;
    uint* _bits;
    int _surfaceWidth, _surfaceHeight;

    public MarksWindow(HWND owner, HINSTANCE hInstance)
    {
        _owner = owner;

        if (!s_classRegistered)
        {
            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &StaticWndProc,
                    hInstance = hInstance,
                    lpszClassName = className,
                };
                PInvoke.RegisterClassEx(&wc);
            }
            s_classRegistered = true;
        }

        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
            ClassName, null, WINDOW_STYLE.WS_POPUP,
            0, 0, 0, 0, owner, default, hInstance, WindowRef.Pin(this));
    }

    public void Destroy()
    {
        if (_hwnd != default)
            PInvoke.DestroyWindow(_hwnd);
    }

    /// <summary>Оверлей накрывает клиентскую область панели один в один.</summary>
    public void Layout(RECT panelScreen)
    {
        int w = panelScreen.right - panelScreen.left;
        int h = panelScreen.bottom - panelScreen.top;
        if (w <= 0 || h <= 0)
            return;

        PInvoke.SetWindowPos(_hwnd, default, panelScreen.left, panelScreen.top, w, h,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        _width = w;
        _height = h;
    }

    /// <summary>
    /// Перерисовать косые: по две на каждую видимую карточку.
    /// active — карточка под курсором или которую сейчас тащат (синий акцент).
    /// </summary>
    public void Render(IReadOnlyList<LayoutItem> layout, RECT client, uint dpi, bool dark,
        bool locked, WindowItem? active)
    {
        if (_hwnd == default || _width <= 0 || _height <= 0 || layout.Count == 0)
            return;

        EnsureSurface();

        for (int i = 0; i < _width * _height; i++)
            _bits[i] = 0;

        // Порядок зафиксирован — ручек не рисуем вовсе
        if (!locked)
        {
            int length = LayoutEngine.Scale(30, dpi);
            int gap = LayoutEngine.Scale(7, dpi);
            int inset = LayoutEngine.Scale(2, dpi);
            int thick = Math.Max(1, LayoutEngine.Scale(2, dpi));
            uint basePixel = dark ? 0xFF737378 : 0xFFA8A8AE;
            uint hotPixel = 0xFFFF840A; // #0A84FF в BGRA

            foreach (var li in layout)
            {
                if (li.IsStrip)
                    continue;
                if (li.Bounds.bottom <= client.top + 2 || li.Bounds.top >= client.bottom - 2)
                    continue;

                int x = li.Bounds.left + inset;
                int y = li.Bounds.bottom - inset;
                int len = Math.Min(length, Math.Max(8, (li.Bounds.right - li.Bounds.left) / 3));
                uint pixel = li.Window == active ? hotPixel : basePixel;

                // Верхняя косая: от левого края вниз к нижнему — вместе с углом треугольник
                Line(x, y - len, x + len, y, thick, pixel);
                // Параллельная ей, со сдвигом вправо
                Line(x + gap, y - len, x + len + gap, y, thick, pixel);
            }
        }

        Flush();
    }

    /// <summary>Толстая диагональ: на каждый столбец — вертикальный отрезок толщиной thick.</summary>
    void Line(int x0, int y0, int x1, int y1, int thick, uint pixel)
    {
        int dx = x1 - x0;
        int dy = y1 - y0;
        if (dx == 0)
        {
            PlotVertical(x0, y0, y1, thick, pixel);
            return;
        }

        for (int i = 0; i <= dx; i++)
        {
            int x = x0 + i;
            int y = y0 + dy * i / dx;
            int half = thick / 2;
            for (int t = -half; t <= thick - 1 - half; t++)
                Plot(x, y + t, pixel);
        }
    }

    void PlotVertical(int x, int y0, int y1, int thick, uint pixel)
    {
        int from = Math.Min(y0, y1);
        int to = Math.Max(y0, y1);
        for (int y = from; y <= to; y++)
        {
            int half = thick / 2;
            for (int t = -half; t <= thick - 1 - half; t++)
                Plot(x + t, y, pixel);
        }
    }

    void Plot(int x, int y, uint pixel)
    {
        if (x < 0 || y < 0 || x >= _width || y >= _height)
            return;
        _bits[(long)y * _width + x] = pixel;
    }

    void Flush()
    {
        var size = new SIZE { cx = _width, cy = _height };
        var srcPoint = new System.Drawing.Point(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)PInvoke.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
        };
        PInvoke.UpdateLayeredWindow(_hwnd, default, null, &size, _dibDc, &srcPoint,
            default, &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
    }

    void EnsureSurface()
    {
        if (_dibDc != default && _surfaceWidth == _width && _surfaceHeight == _height)
            return;

        DestroySurface();

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = _width,
                biHeight = -_height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        void* bits;
        _dib = PInvoke.CreateDIBSection(default, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        _bits = (uint*)bits;
        _dibDc = PInvoke.CreateCompatibleDC(default);
        _dibOld = PInvoke.SelectObject(_dibDc, (HGDIOBJ)_dib.Value);
        _surfaceWidth = _width;
        _surfaceHeight = _height;
    }

    void DestroySurface()
    {
        if (_dibDc != default)
        {
            PInvoke.SelectObject(_dibDc, _dibOld);
            PInvoke.DeleteDC(_dibDc);
            _dibDc = default;
        }
        if (_dib != default)
        {
            PInvoke.DeleteObject((HGDIOBJ)_dib.Value);
            _dib = default;
        }
        _bits = null;
        _surfaceWidth = 0;
        _surfaceHeight = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (msg == PInvoke.WM_NCCREATE)
                WindowRef.Bind(hwnd, lParam);

            var self = WindowRef.Get(hwnd) as MarksWindow;
            var result = self?.HandleMessage(hwnd, msg, wParam, lParam)
                ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

            if (msg == PInvoke.WM_NCDESTROY)
                WindowRef.Release(hwnd);
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Marks WndProc exception: {ex}");
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

            case PInvoke.WM_NCHITTEST:
                // Полностью прозрачен для мыши: клики уходят в панель
                return new LRESULT(unchecked((nint)(int)PInvoke.HTTRANSPARENT));

            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);

            case PInvoke.WM_DESTROY:
                DestroySurface();
                _hwnd = default;
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
