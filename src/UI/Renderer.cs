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
    static readonly COLORREF L_GripLocked = new(0x00C6C6CB); // #CBC6C6

    // Тёмная тема
    static readonly COLORREF D_Bg = new(0x001C1A1A);         // #1A1A1C
    static readonly COLORREF D_Strip = new(0x00262424);      // #242426
    static readonly COLORREF D_StripActive = new(0x00312E2E); // #2E2E31
    static readonly COLORREF D_Text = new(0x00F7F2F2);       // #F2F2F7
    static readonly COLORREF D_TextDim = new(0x00938E8E);    // #8E8E93
    static readonly COLORREF D_Frame = new(0x003C3A3A);      // #3A3A3C
    static readonly COLORREF D_DragFill = new(0x00463C3C);   // #3C3C46
    static readonly COLORREF D_GripLocked = new(0x004E4C4C); // #4C4C4E

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
    COLORREF _textColor, _textDimColor, _gripLockedColor;

    // Кеш на текущий DPI
    uint _dpi;
    HFONT _font;
    int _iconSize, _pad, _gripDot, _gripGap, _header, _thinBorder, _thickBorder;
    /// <summary>Скругление кнопок под заголовком, уже с учётом DPI.</summary>
    int _btnRadius;

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
        _gripLockedColor = dark ? D_GripLocked : L_GripLocked;
    }

    public bool IsDark => _dark;

    /// <summary>
    /// Скругление кнопок под заголовком в логических пикселях. Значение из
    /// настроек; физический размер пересчитывается под текущий DPI.
    /// </summary>
    public int ButtonRadius
    {
        get => _btnRadiusLogical;
        set
        {
            if (_btnRadiusLogical == value)
                return;
            _btnRadiusLogical = value;
            RescaleButtonRadius();
        }
    }

    int _btnRadiusLogical = 4;

    public void Paint(HWND hwnd, IReadOnlyList<LayoutItem> layout, HWND activeWindow, uint dpi,
        WindowItem? hoverClose = null, WindowItem? dragged = null, RECT desktopBar = default,
        RECT pinButton = default, bool locked = false, WindowItem? hoverGrip = null,
        RECT gearButton = default)
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

            foreach (var li in layout)
            {
                if (li.Bounds.bottom < client.top || li.Bounds.top > client.bottom)
                    continue;
                // Полностью уехавшую под «замороженный» заголовок карточку не рисуем
                if (desktopBar.bottom > desktopBar.top && li.Bounds.bottom <= desktopBar.bottom)
                    continue;

                bool isActive = li.Window.Hwnd == activeWindow;
                bool isDragged = li.Window == dragged;
                if (!li.IsStrip)
                    DrawOutline(li.Bounds, isActive ? _accentBrush : _frameBrush, isActive ? _thickBorder : _thinBorder);
                if (isDragged)
                    DrawOutline(li.Bounds, _dragFrameBrush, _thickBorder);
                DrawLabel(li, isActive, li.Window == hoverClose, isDragged, li.Window == hoverGrip, locked);
            }

            // «Замораживаем» заголовок вместе с точками-ручкой: карточки, уехавшие
            // под него при прокрутке, закрашиваются фоном панели, иначе превью
            // просвечивало в пустых местах между точками.
            if (desktopBar.bottom > desktopBar.top && desktopBar.top > client.top)
            {
                var head = new RECT
                {
                    left = client.left,
                    top = client.top,
                    right = client.right,
                    // Кроем и полосу с кнопками целиком: между ними остаётся зазор,
                    // в который иначе просвечивала бы уехавшая карточка.
                    bottom = desktopBar.bottom,
                };
                PInvoke.FillRect(_memDc, in head, _bgBrush);
                DrawHeaderGrip(client);
            }

            // Кнопки рисуем последними: они всегда поверх прокручиваемой ленты,
            // то есть заголовок остаётся «замороженным».
            if (desktopBar.bottom > desktopBar.top)
                DrawButton(desktopBar, "桌面", false);
            if (pinButton.bottom > pinButton.top)
                DrawPinButton(pinButton, locked);
            if (gearButton.bottom > gearButton.top)
                DrawGearButton(gearButton);

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
            // Точки-ручка должны читаться и на светлом, и на тёмном фоне:
            // рамка-«серый» подходит для обеих тем.
            PInvoke.FillRect(_memDc, in r, _dragFrameBrush);
        }
    }

    /// <summary>Кнопка с текстом над лентой.</summary>
    void DrawButton(RECT bar, string label, bool highlight)
    {
        FillButton(bar, highlight ? _stripActiveBrush : _stripBrush,
            highlight ? _accentBrush : _frameBrush, _thinBorder);

        PInvoke.SetTextColor(_memDc, _textColor);
        var textRect = bar;
        fixed (char* t = label)
        {
            PInvoke.DrawText(_memDc, t, label.Length, &textRect,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }
    }

    /// <summary>
    /// Заливка кнопки фоном с рамкой. При нулевом радиусе — прежние
    /// прямоугольники; иначе рамка строится как «залить цветом рамки, затем
    /// залить фоном по внутреннему скруглённому контуру».
    /// </summary>
    void FillButton(RECT bar, HBRUSH fill, HBRUSH frame, int border)
    {
        if (_btnRadius <= 0)
        {
            PInvoke.FillRect(_memDc, in bar, fill);
            var f = bar;
            for (int i = 0; i < border; i++)
            {
                PInvoke.FrameRect(_memDc, in f, frame);
                f.left++;
                f.top++;
                f.right--;
                f.bottom--;
            }
            return;
        }

        // Внешний контур — цвет рамки
        FillRound(bar, _btnRadius, frame);

        // Внутренний контур — фон кнопки. Радиус уменьшаем на толщину рамки,
        // иначе по внутреннему краю оставался бы цветной «пояс».
        var body = new RECT
        {
            left = bar.left + border,
            top = bar.top + border,
            right = bar.right - border,
            bottom = bar.bottom - border,
        };
        if (body.right > body.left && body.bottom > body.top)
            FillRound(body, _btnRadius - border, fill);
    }

    /// <summary>Заливает прямоугольник со скруглёнными углами, обрезая вывод по региону.</summary>
    void FillRound(RECT r, int radius, HBRUSH brush)
    {
        // Половина высоты превратила бы кнопку в «капсулу» без прямых участков
        // сверху и снизу. 35% оставляют их читаемыми при любом DPI.
        int height = r.bottom - r.top;
        int limit = Math.Max(0, height * 35 / 100);
        if (radius > limit)
            radius = limit;

        // CreateRoundRectRgn трактует правую/нижнюю границу как исключающую
        HRGN region = PInvoke.CreateRoundRectRgn(
            r.left, r.top, r.right + 1, r.bottom + 1, radius * 2, radius * 2);
        if (region == default)
        {
            PInvoke.FillRect(_memDc, in r, brush);
            return;
        }

        PInvoke.SelectClipRgn(_memDc, region);
        PInvoke.FillRect(_memDc, in r, brush);
        PInvoke.SelectClipRgn(_memDc, default);
        PInvoke.DeleteObject((HGDIOBJ)region.Value);
    }

    /// <summary>Кнопка настроек: три полоски — привычный значок «меню/настройки».</summary>
    void DrawGearButton(RECT bar)
    {
        FillButton(bar, _stripBrush, _frameBrush, _thinBorder);

        int cx = (bar.left + bar.right) / 2;
        int cy = (bar.top + bar.bottom) / 2;
        int w = Math.Max(_iconSize, 10);
        int h = Math.Max(_thinBorder * 2, 2);
        int gap = h + Math.Max(_thinBorder, 2);

        for (int i = -1; i <= 1; i++)
        {
            var line = new RECT
            {
                left = cx - w / 2,
                top = cy + i * gap - h / 2,
                right = cx + w / 2,
                bottom = cy + i * gap - h / 2 + h,
            };
            PInvoke.FillRect(_memDc, in line, _dragFrameBrush);
        }
    }

    /// <summary>Кнопка-«канцелярская кнопка»: фиксирует порядок карточек.</summary>
    void DrawPinButton(RECT bar, bool locked)
    {
        FillButton(bar, locked ? _stripActiveBrush : _stripBrush,
            locked ? _accentBrush : _frameBrush, _thinBorder);

        // Значок кнопки: шляпка + ножка
        HBRUSH brush = locked ? _accentBrush : _frameBrush;
        int cx = (bar.left + bar.right) / 2;
        int cy = (bar.top + bar.bottom) / 2;
        int capW = Math.Max(_thinBorder * 6, _iconSize / 2);
        int capH = Math.Max(_thinBorder * 2, 2);
        int bodyW = Math.Max(capH, _thinBorder * 2);
        int bodyH = capW * 3 / 4;

        var cap = new RECT
        {
            left = cx - capW / 2,
            top = cy - (capH + bodyH) / 2,
            right = cx + capW / 2,
            bottom = cy - (capH + bodyH) / 2 + capH,
        };
        PInvoke.FillRect(_memDc, in cap, brush);

        var body = new RECT
        {
            left = cx - bodyW / 2,
            top = cap.bottom,
            right = cx + bodyW / 2,
            bottom = cap.bottom + bodyH,
        };
        PInvoke.FillRect(_memDc, in body, brush);
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

    void DrawLabel(LayoutItem li, bool isActive, bool closeHover, bool isDragged, bool gripHover, bool locked)
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

        RescaleButtonRadius();
    }

    /// <summary>Логический радиус — в физический под текущий DPI.</summary>
    void RescaleButtonRadius()
        => _btnRadius = _btnRadiusLogical <= 0
            ? 0
            : Math.Max(1, LayoutEngine.Scale(_btnRadiusLogical, _dpi == 0 ? 96u : _dpi));

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
