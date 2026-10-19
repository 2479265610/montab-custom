using Montab.Core;
using Montab.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Montab.Thumbs;

/// <summary>
/// Владеет DWM-миниатюрами: регистрирует для видимых живых тайлов, снимает
/// с регистрации для полосок и ушедших за viewport (виртуализация — вне
/// экрана превью не стоит ничего). Один Sync после каждого пересчёта layout.
/// </summary>
internal sealed unsafe class ThumbnailManager(HWND panel) : IDisposable
{
    readonly HWND _panel = panel;
    readonly Dictionary<HWND, ThumbState> _thumbs = [];
    readonly HashSet<HWND> _wanted = [];
    readonly List<HWND> _stale = [];

    struct ThumbState
    {
        public nint Thumb;
        public bool CustomSource; // выставлен rcSource (zoom>1)
    }

    /// <summary>Затенение превью активного окна (255 = непрозрачно).</summary>
    const byte ActiveOpacity = 110;

    public void Sync(IReadOnlyList<LayoutItem> layout, RECT client, HWND activeWindow)
    {
        _wanted.Clear();

        foreach (var li in layout)
        {
            if (li.IsStrip)
                continue;
            if (li.Preview.bottom <= client.top || li.Preview.top >= client.bottom)
                continue; // вне viewport — поток не нужен

            HWND source = li.Window.Hwnd;
            _wanted.Add(source);

            bool wantCustomSource = li.Window.Zoom > 1.001;

            if (_thumbs.TryGetValue(source, out var state) && state.CustomSource && !wantCustomSource)
            {
                // Сбросить rcSource нельзя (флаги DWM только добавляют) — пересоздаём
                // миниатюру, чтобы DWM снова сам отслеживал размер окна-источника.
                PInvoke.DwmUnregisterThumbnail(state.Thumb);
                _thumbs.Remove(source);
                state = default;
            }

            if (state.Thumb == 0)
            {
                if (PInvoke.DwmRegisterThumbnail(_panel, source, out nint created).Failed)
                    continue;
                state = new ThumbState { Thumb = created };
                _thumbs[source] = state;
            }

            var dest = LayoutEngine.FitRect(li.Preview, li.Window.Aspect);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                // rcSource задаём только при zoom>1: у окна в переходной геометрии
                // (разворачивание из свёрнутого) GetClientRect даёт «иконик»-полосу,
                // и приколоченный rcSource показывал бы её до следующего события.
                dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE |
                          PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY |
                          (wantCustomSource ? PInvoke.DWM_TNP_RECTSOURCE : 0),
                rcDestination = dest,
                rcSource = wantCustomSource ? ComputeSourceRect(li.Window) : default,
                opacity = source == activeWindow ? ActiveOpacity : (byte)255,
                fVisible = true,
                fSourceClientAreaOnly = true,
            };
            PInvoke.DwmUpdateThumbnailProperties(state.Thumb, &props);

            if (state.CustomSource != wantCustomSource)
            {
                state.CustomSource = wantCustomSource;
                _thumbs[source] = state;
            }
        }

        // Всё, что больше не нужно (полоска, за экраном, окно закрыто) — снять.
        _stale.Clear();
        foreach (var (hwnd, state) in _thumbs)
        {
            if (_wanted.Contains(hwnd))
                continue;
            PInvoke.DwmUnregisterThumbnail(state.Thumb);
            _stale.Add(hwnd);
        }
        foreach (var hwnd in _stale)
            _thumbs.Remove(hwnd);
    }

    /// <summary>
    /// Видимая область источника: всё окно при zoom=1, иначе фрагмент 1/zoom
    /// вокруг нормализованного центра (Ctrl+zoom или временный hold-zoom).
    /// </summary>
    static RECT ComputeSourceRect(WindowItem item)
    {
        PInvoke.GetClientRect(item.Hwnd, out RECT rc);
        int sw = rc.right - rc.left, sh = rc.bottom - rc.top;
        double zoom = item.Zoom;
        if (sw <= 0 || sh <= 0 || zoom <= 1.001)
            return new RECT { left = 0, top = 0, right = Math.Max(sw, 1), bottom = Math.Max(sh, 1) };

        double visW = sw / zoom, visH = sh / zoom;
        double cx = Math.Clamp(item.CenterX * sw, visW / 2, sw - visW / 2);
        double cy = Math.Clamp(item.CenterY * sh, visH / 2, sh - visH / 2);

        return new RECT
        {
            left = (int)(cx - visW / 2),
            top = (int)(cy - visH / 2),
            right = (int)(cx + visW / 2),
            bottom = (int)(cy + visH / 2),
        };
    }

    public void Dispose()
    {
        foreach (var state in _thumbs.Values)
            PInvoke.DwmUnregisterThumbnail(state.Thumb);
        _thumbs.Clear();
    }
}
