using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.App;

/// <summary>
/// Страница настроек: отдельное окно вместо контекстного меню в трее.
/// Все параметры видны сразу, клик по сегменту применяется немедленно.
/// Рисуется вручную через GDI, чтобы совпадать по оформлению с панелью.
/// </summary>
internal sealed unsafe class SettingsWindow
{
    const string ClassName = "MontabSettings";
    static bool s_classRegistered;
    static readonly HCURSOR ArrowCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW);

    // Логические размеры (масштабируются на DPI/96)
    const int WidthLogical = 470;
    const int HeaderLogical = 54;
    const int RowLogical = 44;
    const int SectionLogical = 34;
    const int PadLogical = 22;
    const int CtrlLogical = 240;
    const int BottomLogical = 20;

    enum RowKind { Segments, Stepper, Check }

    sealed class Row
    {
        public string Section = "";
        public string Label = "";
        public RowKind Kind;
        public string[] Options = [];
        public Func<int> Get = () => 0;
        public Action<int> Set = _ => { };
    }

    readonly PanelHost _host;
    readonly string _device;
    readonly List<Row> _rows = [];

    HWND _hwnd;
    uint _dpi;
    HFONT _font, _fontTitle;

    readonly HBRUSH _brushLightBg = PInvoke.CreateSolidBrush(new COLORREF(0x00F7F5F5));
    readonly HBRUSH _brushLightCard = PInvoke.CreateSolidBrush(new COLORREF(0x00FFFFFF));
    readonly HBRUSH _brushLightFrame = PInvoke.CreateSolidBrush(new COLORREF(0x00DBD6D6));
    readonly HBRUSH _brushLightSeg = PInvoke.CreateSolidBrush(new COLORREF(0x00EDE8E8));
    readonly HBRUSH _brushDarkBg = PInvoke.CreateSolidBrush(new COLORREF(0x001C1A1A));
    readonly HBRUSH _brushDarkCard = PInvoke.CreateSolidBrush(new COLORREF(0x00262424));
    readonly HBRUSH _brushDarkFrame = PInvoke.CreateSolidBrush(new COLORREF(0x003C3A3C));
    readonly HBRUSH _brushDarkSeg = PInvoke.CreateSolidBrush(new COLORREF(0x00312E2E));
    readonly HBRUSH _brushAccent = PInvoke.CreateSolidBrush(new COLORREF(0x00FF840A));

    bool _dark;
    HBRUSH _bg = default, _card = default, _frame = default, _seg = default;
    COLORREF _text, _dim;

    bool _dragging;
    int _dragDx, _dragDy;
    bool _closeHover;

    public SettingsWindow(PanelHost host, string device)
    {
        _host = host;
        _device = device;
        BuildRows();
    }

    public bool IsAlive => _hwnd != default;

    void BuildRows()
    {
        var s = _host.Settings;

        _rows.Add(new Row
        {
            Section = "外观",
            Label = "主题",
            Kind = RowKind.Segments,
            Options = ["浅色", "深色"],
            Get = () => s.Theme == ThemeMode.Dark ? 1 : 0,
            Set = i => _host.SetTheme(i == 1 ? ThemeMode.Dark : ThemeMode.Light),
        });

        _rows.Add(new Row
        {
            Label = "毛玻璃",
            Kind = RowKind.Segments,
            Options = ["关", "30%", "50%", "70%", "85%"],
            Get = () => s.GlassAlpha switch { 0 => 0, <= 40 => 1, <= 60 => 2, <= 78 => 3, _ => 4 },
            Set = i => _host.SetGlass(i switch { 0 => 0, 1 => 30, 2 => 50, 3 => 70, _ => 85 }),
        });

        _rows.Add(new Row
        {
            Section = "面板",
            Label = "停靠边",
            Kind = RowKind.Segments,
            Options = ["左侧", "右侧"],
            Get = () => s.For(_device).Edge == DockEdge.Left ? 0 : 1,
            Set = i => _host.SetEdge(_device, i == 0 ? DockEdge.Left : DockEdge.Right),
        });

        _rows.Add(new Row
        {
            Label = "面板宽度",
            Kind = RowKind.Stepper,
            Get = () => (int)Math.Round(s.For(_device).WidthPercent),
            Set = d => _host.SetWidth(_device, s.For(_device).WidthPercent + d),
        });

        _rows.Add(new Row
        {
            Section = "行为",
            Label = "点 × 按钮",
            Kind = RowKind.Segments,
            Options = ["关闭程序", "仅最小化"],
            Get = () => s.CloseAction == CloseAction.Minimize ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.CloseAction = i == 1 ? CloseAction.Minimize : CloseAction.Close),
        });

        _rows.Add(new Row
        {
            Label = "点当前窗口",
            Kind = RowKind.Segments,
            Options = ["压到最下面", "最小化"],
            Get = () => s.ActiveClickAction == ActiveClickAction.Minimize ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.ActiveClickAction =
                i == 1 ? ActiveClickAction.Minimize : ActiveClickAction.SendToBottom),
        });

        _rows.Add(new Row
        {
            Label = "最小化窗口显示为大卡片",
            Kind = RowKind.Check,
            Get = () => s.MinimizedAsCard ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.MinimizedAsCard = i == 1),
        });

        _rows.Add(new Row
        {
            Label = "悬停放大预览",
            Kind = RowKind.Check,
            Get = () => s.HoverZoom ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.HoverZoom = i == 1),
        });

        _rows.Add(new Row
        {
            Label = "显示滚动条",
            Kind = RowKind.Check,
            Get = () => s.ShowScrollbar ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.ShowScrollbar = i == 1),
        });

        _rows.Add(new Row
        {
            Label = "全屏播放时自动隐藏侧栏",
            Kind = RowKind.Check,
            Get = () => s.YieldToFullscreen ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.YieldToFullscreen = i == 1),
        });

        _rows.Add(new Row
        {
            Label = "记忆桌面图标布局",
            Kind = RowKind.Check,
            Get = () => s.RememberIcons ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.RememberIcons = i == 1),
        });

        _rows.Add(new Row
        {
            Label = "锁定卡片顺序",
            Kind = RowKind.Check,
            Get = () => s.LockOrder ? 1 : 0,
            Set = i => _host.UpdatePreferences(p => p.LockOrder = i == 1),
        });
    }

    public void Show(HINSTANCE hInstance)
    {
        if (_hwnd != default)
        {
            PInvoke.SetForegroundWindow(_hwnd);
            return;
        }

        EnsureClass(hInstance);

        // Сначала окно минимального размера: реальные габариты считаем после того,
        // как узнаем DPI этого окна (иначе масштаб был бы неизвестен).
        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST,
            ClassName,
            "montab 设置",
            WINDOW_STYLE.WS_POPUP,
            0, 0, 400, 400,
            default, default, hInstance, WindowRef.Pin(this));

        if (_hwnd == default)
            return;

        ApplyTheme();
        ApplyFonts(PInvoke.GetDpiForWindow(_hwnd));

        int width = Scale(WidthLogical);
        int height = Scale(TotalHeightLogical());

        PInvoke.GetCursorPos(out System.Drawing.Point cur);
        var monitor = PInvoke.MonitorFromPoint(cur, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        RECT work = PInvoke.GetMonitorInfo(monitor, ref mi)
            ? mi.rcWork
            : new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };

        int x = work.left + (work.right - work.left - width) / 2;
        int y = work.top + (work.bottom - work.top - height) / 3;

        PInvoke.SetWindowPos(_hwnd, default, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_hwnd);
    }

    public void Close()
    {
        if (_hwnd != default)
            PInvoke.DestroyWindow(_hwnd);
    }

    /// <summary>Настройки поменялись из трея — перерисовать страницу.</summary>
    public void Refresh()
    {
        if (_hwnd == default)
            return;
        ApplyTheme();
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

    int TotalHeightLogical()
    {
        int h = HeaderLogical + BottomLogical;
        foreach (var row in _rows)
        {
            if (row.Section.Length > 0)
                h += SectionLogical;
            h += RowLogical;
        }
        return h;
    }

    int Scale(int logical) => (int)Math.Round(logical * _dpi / 96.0);

    void ApplyTheme()
    {
        _dark = _host.Settings.Theme == ThemeMode.Dark;
        _bg = _dark ? _brushDarkBg : _brushLightBg;
        _card = _dark ? _brushDarkCard : _brushLightCard;
        _frame = _dark ? _brushDarkFrame : _brushLightFrame;
        _seg = _dark ? _brushDarkSeg : _brushLightSeg;
        _text = _dark ? new COLORREF(0x00F7F2F2) : new COLORREF(0x001E1C1C);
        _dim = _dark ? new COLORREF(0x00938E8E) : new COLORREF(0x00736E6E);
    }

    void ApplyFonts(uint dpi)
    {
        _dpi = dpi;
        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);
        if (_fontTitle != default)
            PInvoke.DeleteObject((HGDIOBJ)_fontTitle.Value);

        _font = PInvoke.CreateFont(
            -Scale(11), 0, 0, 0, 400, 0, 0, 0,
            FONT_CHARSET.DEFAULT_CHARSET,
            FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS,
            FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            FONT_QUALITY.CLEARTYPE_QUALITY,
            0, "Microsoft YaHei UI");

        _fontTitle = PInvoke.CreateFont(
            -Scale(13), 0, 0, 0, 700, 0, 0, 0,
            FONT_CHARSET.DEFAULT_CHARSET,
            FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS,
            FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            FONT_QUALITY.CLEARTYPE_QUALITY,
            0, "Microsoft YaHei UI");
    }

    // ---------- отрисовка ----------

    void Paint(HWND hwnd)
    {
        PInvoke.GetClientRect(hwnd, out RECT client);
        HDC hdc = PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
        try
        {
            PInvoke.SetBkMode(hdc, BACKGROUND_MODE.TRANSPARENT);
            PInvoke.FillRect(hdc, in client, _bg);

            // Заголовок: название слева, крестик справа
            var head = new RECT { left = 0, top = 0, right = client.right, bottom = Scale(HeaderLogical) };
            PInvoke.FillRect(hdc, in head, _card);

            var title = new RECT
            {
                left = Scale(PadLogical),
                top = 0,
                right = client.right - Scale(48),
                bottom = head.bottom,
            };
            Text(hdc, title, "设置", _text, DRAW_TEXT_FORMAT.DT_LEFT, true);

            var close = CloseRect(client);
            if (_closeHover)
                PInvoke.FillRect(hdc, in close, _brushAccent);
            Text(hdc, close, "✕", _closeHover ? White : _dim, DRAW_TEXT_FORMAT.DT_CENTER, false);

            var line = new RECT { left = 0, top = head.bottom - Scale(1), right = client.right, bottom = head.bottom };
            PInvoke.FillRect(hdc, in line, _frame);

            int y = head.bottom;
            foreach (var row in _rows)
            {
                if (row.Section.Length > 0)
                {
                    var sec = new RECT
                    {
                        left = Scale(PadLogical),
                        top = y,
                        right = client.right - Scale(PadLogical),
                        bottom = y + Scale(SectionLogical),
                    };
                    Text(hdc, sec, row.Section, _dim, DRAW_TEXT_FORMAT.DT_LEFT, false);
                    y += Scale(SectionLogical);
                }

                var rowRect = RowRect(client, y);
                DrawRow(hdc, row, rowRect, client);
                y = rowRect.bottom;
            }

            // Нижняя подсказка
            var hint = new RECT
            {
                left = Scale(PadLogical),
                top = client.bottom - Scale(BottomLogical),
                right = client.right - Scale(PadLogical),
                bottom = client.bottom - Scale(4),
            };
            Text(hdc, hint, "改动会立即生效", _dim, DRAW_TEXT_FORMAT.DT_CENTER, false);
        }
        finally
        {
            PInvoke.EndPaint(hwnd, in ps);
        }
    }

    RECT RowRect(RECT client, int top)
    {
        return new RECT
        {
            left = Scale(PadLogical),
            top = top,
            right = client.right - Scale(PadLogical),
            bottom = top + Scale(RowLogical),
        };
    }

    RECT CloseRect(RECT client)
    {
        int size = Scale(34);
        int right = client.right - Scale(10);
        int top = Scale(10);
        return new RECT { left = right - size, top = top, right = right, bottom = top + size };
    }

    RECT CtrlRect(RECT row)
    {
        int w = Scale(CtrlLogical);
        return new RECT { left = row.right - w, top = row.top + Scale(8), right = row.right, bottom = row.bottom - Scale(8) };
    }

    void DrawRow(HDC hdc, Row row, RECT rowRect, RECT client)
    {
        var labelRect = new RECT
        {
            left = rowRect.left,
            top = rowRect.top,
            right = CtrlRect(rowRect).left - Scale(12),
            bottom = rowRect.bottom,
        };
        Text(hdc, labelRect, row.Label, _text, DRAW_TEXT_FORMAT.DT_LEFT, false);

        var ctrl = CtrlRect(rowRect);
        switch (row.Kind)
        {
            case RowKind.Segments:
                DrawSegments(hdc, ctrl, row.Options, row.Get());
                break;

            case RowKind.Stepper:
                DrawStepper(hdc, ctrl, row.Get());
                break;

            case RowKind.Check:
                DrawCheck(hdc, rowRect, ctrl, row.Get() == 1);
                break;
        }
    }

    void DrawSegments(HDC hdc, RECT ctrl, string[] options, int active)
    {
        if (options.Length == 0)
            return;

        int gap = Scale(4);
        int cell = (ctrl.right - ctrl.left - gap * (options.Length - 1)) / options.Length;
        for (int i = 0; i < options.Length; i++)
        {
            var r = new RECT
            {
                left = ctrl.left + i * (cell + gap),
                top = ctrl.top,
                right = ctrl.left + i * (cell + gap) + cell,
                bottom = ctrl.bottom,
            };

            bool on = i == active;
            PInvoke.FillRect(hdc, in r, on ? _brushAccent : _seg);
            Frame(hdc, r, _frame);
            Text(hdc, r, options[i], on ? White : _text, DRAW_TEXT_FORMAT.DT_CENTER, false);
        }
    }

    void DrawStepper(HDC hdc, RECT ctrl, int value)
    {
        int btn = Scale(38);
        int gap = Scale(4);

        var minus = new RECT { left = ctrl.left, top = ctrl.top, right = ctrl.left + btn, bottom = ctrl.bottom };
        var plus = new RECT { left = ctrl.right - btn, top = ctrl.top, right = ctrl.right, bottom = ctrl.bottom };
        var mid = new RECT { left = minus.right + gap, top = ctrl.top, right = plus.left - gap, bottom = ctrl.bottom };

        PInvoke.FillRect(hdc, in minus, _seg);
        Frame(hdc, minus, _frame);
        Text(hdc, minus, "−", _text, DRAW_TEXT_FORMAT.DT_CENTER, false);

        PInvoke.FillRect(hdc, in plus, _seg);
        Frame(hdc, plus, _frame);
        Text(hdc, plus, "+", _text, DRAW_TEXT_FORMAT.DT_CENTER, false);

        Text(hdc, mid, value + " %", _text, DRAW_TEXT_FORMAT.DT_CENTER, false);
    }

    void DrawCheck(HDC hdc, RECT rowRect, RECT ctrl, bool on)
    {
        int size = Scale(24);
        var box = new RECT
        {
            left = ctrl.right - size,
            top = rowRect.top + (rowRect.bottom - rowRect.top - size) / 2,
            right = ctrl.right,
            bottom = rowRect.top + (rowRect.bottom - rowRect.top - size) / 2 + size,
        };

        PInvoke.FillRect(hdc, in box, on ? _brushAccent : _seg);
        Frame(hdc, box, _frame);
        if (on)
            Text(hdc, box, "✓", White, DRAW_TEXT_FORMAT.DT_CENTER, false);

        var word = new RECT { left = box.left - Scale(56), top = box.top, right = box.left - Scale(8), bottom = box.bottom };
        Text(hdc, word, on ? "开" : "关", _dim, DRAW_TEXT_FORMAT.DT_RIGHT, false);
    }

    static readonly COLORREF White = new(0x00FFFFFF);

    void Frame(HDC dc, RECT r, HBRUSH brush)
    {
        PInvoke.FrameRect(dc, in r, brush);
    }

    void Text(HDC dc, RECT rect, string s, COLORREF color, DRAW_TEXT_FORMAT align, bool title)
    {
        if (s.Length == 0)
            return;

        PInvoke.SetTextColor(dc, color);
        var hfont = title ? _fontTitle : _font;
        var prev = PInvoke.SelectObject(dc, (HGDIOBJ)hfont.Value);
        var r = rect;
        fixed (char* p = s)
        {
            PInvoke.DrawText(dc, p, s.Length, &r,
                align | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_NOPREFIX | DRAW_TEXT_FORMAT.DT_END_ELLIPSIS);
        }
        PInvoke.SelectObject(dc, prev);
    }

    // ---------- ввод ----------

    void OnLeftDown(HWND hwnd, int x, int y)
    {
        PInvoke.GetClientRect(hwnd, out RECT client);

        if (Inside(CloseRect(client), x, y))
        {
            PInvoke.DestroyWindow(hwnd);
            return;
        }

        if (y < Scale(HeaderLogical))
        {
            // Тянем окно за заголовок
            PInvoke.GetCursorPos(out System.Drawing.Point pt);
            PInvoke.GetWindowRect(hwnd, out RECT wr);
            _dragging = true;
            _dragDx = pt.X - wr.left;
            _dragDy = pt.Y - wr.top;
            PInvoke.SetCapture(hwnd);
            return;
        }

        int top = Scale(HeaderLogical);
        foreach (var row in _rows)
        {
            if (row.Section.Length > 0)
                top += Scale(SectionLogical);

            var rowRect = RowRect(client, top);
            top = rowRect.bottom;

            if (y < rowRect.top || y >= rowRect.bottom)
                continue;

            var ctrl = CtrlRect(rowRect);
            if (row.Kind == RowKind.Check)
            {
                if (x >= ctrl.left - Scale(60) && x <= ctrl.right)
                {
                    row.Set(row.Get() == 1 ? 0 : 1);
                    PInvoke.InvalidateRect(hwnd, null, false);
                }
                return;
            }

            if (row.Kind == RowKind.Stepper)
            {
                int btn = Scale(38);
                if (x >= ctrl.left && x < ctrl.left + btn)
                    row.Set(-1);
                else if (x >= ctrl.right - btn && x < ctrl.right)
                    row.Set(1);
                PInvoke.InvalidateRect(hwnd, null, false);
                return;
            }

            // Сегменты: делим управляющую область на равные ячейки
            int n = row.Options.Length;
            if (n == 0)
                return;
            int gap = Scale(4);
            int cell = (ctrl.right - ctrl.left - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                int left = ctrl.left + i * (cell + gap);
                if (x >= left && x < left + cell)
                {
                    row.Set(i);
                    break;
                }
            }
            PInvoke.InvalidateRect(hwnd, null, false);
            return;
        }
    }

    static bool Inside(RECT r, int x, int y) => x >= r.left && x < r.right && y >= r.top && y < r.bottom;

    // ---------- окно ----------

    static void EnsureClass(HINSTANCE hInstance)
    {
        if (s_classRegistered)
            return;

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                lpfnWndProc = &StaticWndProc,
                hInstance = hInstance,
                hCursor = ArrowCursor,
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

            var self = WindowRef.Get(hwnd) as SettingsWindow;
            var result = self?.HandleMessage(hwnd, msg, wParam, lParam)
                ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

            if (msg == PInvoke.WM_NCDESTROY)
                WindowRef.Release(hwnd);
            return result;
        }
        catch
        {
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_ERASEBKGND:
                return new LRESULT(1);

            case PInvoke.WM_PAINT:
                Paint(hwnd);
                return new LRESULT(0);

            case PInvoke.WM_LBUTTONDOWN:
                OnLeftDown(hwnd, GetXLParam(lParam), GetYLParam(lParam));
                return new LRESULT(0);

            case PInvoke.WM_MOUSEMOVE:
                if (_dragging)
                {
                    PInvoke.GetCursorPos(out System.Drawing.Point pt);
                    PInvoke.SetWindowPos(hwnd, default, pt.X - _dragDx, pt.Y - _dragDy, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
                    return new LRESULT(0);
                }

                bool hot = Inside(CloseRect(ClientRect(hwnd)), GetXLParam(lParam), GetYLParam(lParam));
                if (hot != _closeHover)
                {
                    _closeHover = hot;
                    PInvoke.InvalidateRect(hwnd, null, false);
                }
                return new LRESULT(0);

            case PInvoke.WM_LBUTTONUP:
                if (_dragging)
                {
                    _dragging = false;
                    PInvoke.ReleaseCapture();
                    return new LRESULT(0);
                }
                return new LRESULT(0);

            case PInvoke.WM_CAPTURECHANGED:
                _dragging = false;
                return new LRESULT(0);

            case PInvoke.WM_KEYDOWN:
                if ((int)wParam.Value == 0x1B) // VK_ESCAPE
                {
                    PInvoke.DestroyWindow(hwnd);
                    return new LRESULT(0);
                }
                break;

            case PInvoke.WM_DESTROY:
                _hwnd = default;
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static RECT ClientRect(HWND hwnd)
    {
        PInvoke.GetClientRect(hwnd, out RECT r);
        return r;
    }

    static int GetXLParam(LPARAM lParam) => (short)((nint)lParam.Value & 0xFFFF);

    static int GetYLParam(LPARAM lParam) => (short)(((nint)lParam.Value >> 16) & 0xFFFF);
}
