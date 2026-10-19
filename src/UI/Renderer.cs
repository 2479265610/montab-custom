using Montab.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.UI;

/// <summary>
/// GDI-отрисовка панели. Backbuffer, шрифт и размеры кешируются и
/// пересоздаются только при изменении размера окна или DPI —
/// в обычном кадре ничего не выделяется.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    // COLORREF = 0x00BBGGRR
    // Светлая тема
    static readonly COLORREF L_Bg = new(0x00F7F5F5);         // #F5F5F7
    static readonly COLORREF L_Strip = new(0x00FFFFFF);      // #FFFFFF
    static readonly COLORREF L_StripActive = new(0x00EDE8E8); // #E8E8ED
    static readonly COLORREF L_Text = new(0x001E1C1C);       // #1C1C1E
    static readonly COLORREF L_TextDim = new(0x00736E6E);    // #6E6E73
    static readonly COLORREF L_Frame = new(0x00DBD6D6);      // #D6D6DB
    static readonly COLORREF L_DragFill = new(0x00E5E0E0);   // #E0E0E5

    // Тёмная тема
    static readonly COLORREF D_Bg = new(0x001C1A1A);         // #1A1A1C
    static readonly COLORREF D_Strip = new(0x00262424);      // #242426
    static readonly COLORREF D_StripActive = new(0x00312E2E); // #2E2E31
    static readonly COLORREF D_Text = new(0x00F7F2F2);       // #F2F2F7
    static readonly COLORREF D_TextDim = new(0x00938E8E);    // #8E8E93
    static readonly COLORREF D_Frame = new(0x003C3A3A);      // #3A3A3C
    static readonly COLORREF D_DragFill = new(0x00463C3C);   // #3C3C46

    // Общие для обеих тем
    static readonly COLORREF Accent = new(0x00FF840A);       // #0A84FF
    static readonly COLORREF CloseHover = new(0x002311E8);   // #E81123
    static readonly COLORREF White = new(0x00FFFFFF);

    readonly HBRUSH _bgLight = PInvoke.CreateSolidBrush(L_Bg);
    readonly HBRUSH _bgDark = PInvoke.CreateSolidBrush(D_Bg);
    readonly HBRUSH _stripLight = PInvoke.CreateSolidBrush(L_Strip);
    readonly HBRUSH _stripDark = PInvoke.CreateSolidBrush(D_Strip);
    readonly HBRUSH _stripActiveLight = PInvoke.CreateSolidBrush(L_StripActive);
    readonly HBRUSH _stripActiveDark = PInvoke.CreateSolidBrush(D_StripActive);
    readonly HBRUSH _accentBrush = PInvoke.CreateSolidBrush(Accent);
    readonly HBRUSH _closeHoverBrush = PInvoke.CreateSolidBrush(CloseHover);
    readonly HBRUSH _frameLight = PInvoke.CreateSolidBrush(L_Frame);
    readonly HBRUSH _frameDark = PInvoke.CreateSolidBrush(D_Frame);
    readonly HBRUSH _dragFillLight = PInvoke.CreateSolidBrush(L_DragFill);
    readonly HBRUSH _dragFillDark = PInvoke.CreateSolidBrush(D_DragFill);
    readonly HBRUSH _dragFrameBrush = PInvoke.CreateSolidBrush(new COLORREF(0x00938E8E));

    // Текущий набор (переключается через SetDark)
    bool _dark;
    HBRUSH _bgBrush, _stripBrush, _stripActiveBrush, _frameBrush, _dragFillBrush;
    COLORREF _textColor, _textDimColor;

    // Кеш на текущий DPI
    uint _dpi;
    HFONT _font;
    int _iconSize, _pad, _gripDot, _gripGap, _header, _thinBorder, _thickBorder;

    // Кеш backbuffer'а на текущий размер клиентской области
    HDC _memDc;
    HBITMAP _memBmp;
    HGDIOBJ _memOldBmp;
    int _bufWidth, _bufHeight;

    public Renderer() => ApplyTheme(false);

    /// <summary>Переключает набор кистей и цветов текста между светлой и тёмной темой.</summary>
    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        _bgBrush = dark ? _bgDark : _bgLight;
        _stripBrush = dark ? _stripDark : _stripLight;
        _stripActiveBrush = dark ? _stripActiveDark : _stripActiveLight;
        _frameBrush = dark ? _frameDark : _frameLight;
        _dragFillBrush = dark ? _dragFillDark : _dragFillLight;
        _textColor = dark ? D_Text : L_Text;
        _textDimColor = dark ? D_TextDim : L_TextDim;
    }

    public bool IsDark => _dark;

    public void Paint(HWND hwnd, IReadOnlyList<LayoutItem> layout, HWND activeWindow, uint dpi,
        WindowItem? hoverClose = null, WindowItem? dragged = null, RECT desktopBar = default)
    {
        SetDpi(dpi);

        HDC hdc = PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
        try
        {
            PInvoke.GetClientRect(hwnd, out RECT client);
            int width = client.right - client.left;
            int height = client.bottom - client.top;
            if (width <= 0 || height <= 0)
                return;

            EnsureBackbuffer(hdc, width, height);
            PInvoke.SelectObject(_memDc, (HGDIOBJ)_font.Value);

            PInvoke.FillRect(_memDc, in client, _bgBrush);
            DrawHeaderGrip(client);
            if (desktopBar.bottom > desktopBar.top)
                DrawDesktopBar(desktopBar);

            foreach (var li in layout)
            {
                if (li.Bounds.bottom < client.top || li.Bounds.top > client.bottom)
                    continue;

                bool isActive = li.Window.Hwnd == activeWindow;
                bool isDragged = li.Window == dragged;
                if (!li.IsStrip)
                    DrawOutline(li.Bounds, isActive ? _accentBrush : _frameBrush, isActive ? _thickBorder : _thinBorder);
                if (isDragged)
                    DrawOutline(li.Bounds, _dragFrameBrush, _thickBorder);
                DrawLabel(li, isActive, li.Window == hoverClose, isDragged);
            }

            PInvoke.BitBlt(hdc, 0, 0, width, height, _memDc, 0, 0, ROP_CODE.SRCCOPY);
        }
        finally
        {
            PInvoke.EndPaint(hwnd, in ps);
        }
    }

    /// <summary>Гриппер-«ручка» сверху: за неё панель перетаскивают на другой монитор/край.</summary>
    void DrawHeaderGrip(RECT client)
    {
        int centerX = (client.left + client.right) / 2;
        int y = (_header - _gripDot) / 2;

        for (int i = -2; i <= 2; i++)
        {
            int x = centerX + i * _gripGap - _gripDot / 2;
            var r = new RECT { left = x, top = y, right = x + _gripDot, bottom = y + _gripDot };
            PInvoke.FillRect(_memDc, in r, _frameBrush);
        }
    }

    /// <summary>Кнопка «Показать рабочий стол»: сворачивает все окна.</summary>
    void DrawDesktopBar(RECT bar)
    {
        PInvoke.FillRect(_memDc, in bar, _stripBrush);

        var frame = bar;
        for (int i = 0; i < _thinBorder; i++)
        {
            PInvoke.FrameRect(_memDc, in frame, _frameBrush);
            frame.left++;
            frame.top++;
            frame.right--;
            frame.bottom--;
        }

        PInvoke.SetTextColor(_memDc, _textColor);
        var textRect = bar;
        fixed (char* t = "显示桌面")
        {
            PInvoke.DrawText(_memDc, t, 4, &textRect,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }
    }

    /// <summary>
    /// Цельная рамка вокруг блока «заголовок + превью». Рисуется наружу от
    /// bounds: внутри рисовать нельзя — DWM компонует превью поверх нашего GDI.
    /// </summary>
    void DrawOutline(RECT bounds, HBRUSH brush, int border)
    {
        var frame = new RECT
        {
            left = bounds.left - border,
            top = bounds.top - border,
            right = bounds.right + border,
            bottom = bounds.bottom + border,
        };
        for (int i = 0; i < border; i++)
        {
            PInvoke.FrameRect(_memDc, in frame, brush);
            frame.left++; frame.top++; frame.right--; frame.bottom--;
        }
    }

    void DrawLabel(LayoutItem li, bool isActive, bool closeHover, bool isDragged)
    {
        RECT r = li.Label;
        PInvoke.FillRect(_memDc, in r, isDragged ? _dragFillBrush : isActive ? _stripActiveBrush : _stripBrush);

        int iconX = r.left + _pad;
        int iconY = r.top + (r.bottom - r.top - _iconSize) / 2;
        if (li.Window.Icon != default)
            PInvoke.DrawIconEx(_memDc, iconX, iconY, li.Window.Icon, _iconSize, _iconSize, 0, default, DI_FLAGS.DI_NORMAL);

        var close = LayoutEngine.CloseRect(r);

        PInvoke.SetTextColor(_memDc, li.IsStrip ? _textDimColor : _textColor);
        var textRect = new RECT
        {
            left = iconX + _iconSize + _pad,
            top = r.top,
            right = close.left - _pad,
            bottom = r.bottom,
        };
        string title = li.Window.Title;
        fixed (char* p = title)
        {
            PInvoke.DrawText(_memDc, p, title.Length, &textRect,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_END_ELLIPSIS | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }

        // Крестик закрытия приложения
        if (closeHover)
            PInvoke.FillRect(_memDc, in close, _closeHoverBrush);
        PInvoke.SetTextColor(_memDc, closeHover ? White : _textDimColor);
        fixed (char* x = "✕")
        {
            PInvoke.DrawText(_memDc, x, 1, &close,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }
    }

    void SetDpi(uint dpi)
    {
        if (dpi == _dpi)
            return;
        _dpi = dpi;

        _iconSize = LayoutEngine.Scale(16, dpi);
        _pad = LayoutEngine.Scale(7, dpi);
        _gripDot = Math.Max(2, LayoutEngine.Scale(2, dpi));
        _gripGap = LayoutEngine.Scale(7, dpi);
        _header = LayoutEngine.Scale(LayoutEngine.HeaderLogical, dpi);
        _thinBorder = LayoutEngine.Scale(1, dpi);
        _thickBorder = LayoutEngine.Scale(2, dpi);

        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);
        _font = PInvoke.CreateFont(
            -LayoutEngine.Scale(11, dpi), 0, 0, 0,
            400 /* FW_NORMAL */, 0, 0, 0,
            FONT_CHARSET.DEFAULT_CHARSET,
            FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS,
            FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            FONT_QUALITY.CLEARTYPE_QUALITY,
            0, "Microsoft YaHei UI");
    }

    void EnsureBackbuffer(HDC hdc, int width, int height)
    {
        if (_memDc != default && width == _bufWidth && height == _bufHeight)
            return;

        DisposeBackbuffer();
        _memDc = PInvoke.CreateCompatibleDC(hdc);
        _memBmp = PInvoke.CreateCompatibleBitmap(hdc, width, height);
        _memOldBmp = PInvoke.SelectObject(_memDc, (HGDIOBJ)_memBmp.Value);
        PInvoke.SetBkMode(_memDc, BACKGROUND_MODE.TRANSPARENT);
        _bufWidth = width;
        _bufHeight = height;
    }

    void DisposeBackbuffer()
    {
        if (_memDc == default)
            return;
        PInvoke.SelectObject(_memDc, _memOldBmp);
        PInvoke.DeleteObject((HGDIOBJ)_memBmp.Value);
        PInvoke.DeleteDC(_memDc);
        _memDc = default;
    }

    public void Dispose()
    {
        DisposeBackbuffer();
        PInvoke.DeleteObject((HGDIOBJ)_bgLight.Value);
        PInvoke.DeleteObject((HGDIOBJ)_bgDark.Value);
        PInvoke.DeleteObject((HGDIOBJ)_stripLight.Value);
        PInvoke.DeleteObject((HGDIOBJ)_stripDark.Value);
        PInvoke.DeleteObject((HGDIOBJ)_stripActiveLight.Value);
        PInvoke.DeleteObject((HGDIOBJ)_stripActiveDark.Value);
        PInvoke.DeleteObject((HGDIOBJ)_accentBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_closeHoverBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_frameLight.Value);
        PInvoke.DeleteObject((HGDIOBJ)_frameDark.Value);
        PInvoke.DeleteObject((HGDIOBJ)_dragFillLight.Value);
        PInvoke.DeleteObject((HGDIOBJ)_dragFillDark.Value);
        PInvoke.DeleteObject((HGDIOBJ)_dragFrameBrush.Value);
        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);
    }
}
