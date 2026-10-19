using Montab.Core;
using Windows.Win32.Foundation;

namespace Montab.UI;

internal readonly record struct LayoutItem(WindowItem Window, RECT Bounds, RECT Preview, RECT Label, bool IsStrip);

/// <summary>
/// Раскладка ленты: живые тайлы (превью по аспекту окна-источника + подпись)
/// и полоски свёрнутых окон. Все размеры предвычисляются на текущий DPI,
/// список раскладки переиспользуется между кадрами (ноль аллокаций в кадре).
/// </summary>
internal sealed class LayoutEngine
{
    /// <summary>Высота «ручки» сверху панели — за неё панель перетаскивают на другой монитор/край.</summary>
    public const int HeaderLogical = 16;

    const int StripHeightLogical = 26;
    const int LabelHeightLogical = 22;
    const int GapLogical = 8;
    const int PaddingLogical = 10;
    const int MinPreviewHeightLogical = 48;
    /// <summary>Высота кнопки «Показать рабочий стол» под ручкой.</summary>
    const int DesktopBarLogical = 30;
    const double MaxPreviewHeightFactor = 1.6; // не выше 1.6 ширины (очень «портретные» окна)

    readonly List<LayoutItem> _result = [];

    // Предвычисленные размеры на текущий DPI
    uint _dpi;
    int _header, _strip, _label, _gap, _padding, _minPreview, _desktopBar;

    public int TotalHeight { get; private set; }

    /// <summary>Область кнопки «Показать рабочий стол» в клиентских координатах.</summary>
    public RECT DesktopBar { get; private set; }

    /// <summary>Область кнопки-кнопки «закрепить порядок» справа от неё.</summary>
    public RECT PinButton { get; private set; }

    public IReadOnlyList<LayoutItem> Compute(IReadOnlyList<WindowItem> items, RECT client, uint dpi,
        int scrollOffset, bool minimizedAsCard = true)
    {
        SetDpi(dpi);
        _result.Clear();

        int width = client.right - client.left - 2 * _padding;
        if (width <= 0)
            return _result;

        int left = client.left + _padding;
        int barTop = _header;
        int barBottom = barTop + _desktopBar;
        int barRight = client.right - _padding;

        // Слева — «Показать рабочий стол», справа — кнопка фиксации порядка.
        int span = barRight - left;
        int pinWidth = Math.Min((int)(_desktopBar * 1.3), span * 45 / 100);
        int split = Math.Max(left + _desktopBar, barRight - pinWidth - _gap);

        DesktopBar = new RECT
        {
            left = left,
            top = barTop,
            right = split,
            bottom = barBottom,
        };
        PinButton = new RECT
        {
            left = split + _gap,
            top = barTop,
            right = barRight,
            bottom = barBottom,
        };
        int y = barBottom + _gap - scrollOffset;

        foreach (var item in items)
        {
            // Свёрнутые окна показываем крупной плиткой, если так задано в настройках,
            // иначе — узкой полоской, как в оригинале.
            bool isStrip = !minimizedAsCard && item.IsMinimized;
            RECT bounds, preview = default, label;

            if (isStrip)
            {
                bounds = new RECT { left = left, top = y, right = left + width, bottom = y + _strip };
                label = bounds;
            }
            else
            {
                double aspect = item.Aspect > 0.05 ? item.Aspect : 16.0 / 10.0;
                int previewHeight = Math.Clamp(
                    (int)Math.Round(width / aspect), _minPreview, (int)(width * MaxPreviewHeightFactor));

                // Заголовок сверху, превью под ним
                bounds = new RECT { left = left, top = y, right = left + width, bottom = y + _label + previewHeight };
                label = new RECT { left = left, top = y, right = left + width, bottom = y + _label };
                preview = new RECT { left = left, top = y + _label, right = left + width, bottom = bounds.bottom };
            }

            _result.Add(new LayoutItem(item, bounds, preview, label, isStrip));
            y = bounds.bottom + _gap;
        }

        TotalHeight = y + scrollOffset + _padding - (items.Count > 0 ? _gap : 0);
        return _result;
    }

    void SetDpi(uint dpi)
    {
        if (dpi == _dpi)
            return;
        _dpi = dpi;
        _header = Scale(HeaderLogical, dpi);
        _strip = Scale(StripHeightLogical, dpi);
        _label = Scale(LabelHeightLogical, dpi);
        _gap = Scale(GapLogical, dpi);
        _padding = Scale(PaddingLogical, dpi);
        _minPreview = Scale(MinPreviewHeightLogical, dpi);
        _desktopBar = Scale(DesktopBarLogical, dpi);
    }

    /// <summary>
    /// Вписывает прямоугольник с данным аспектом внутрь ячейки по центру.
    /// DWM сам сохраняет аспект, но прижимает к левому верхнему углу —
    /// поэтому точный rect считаем сами.
    /// </summary>
    public static RECT FitRect(RECT cell, double aspect)
    {
        int cellW = cell.right - cell.left;
        int cellH = cell.bottom - cell.top;
        if (cellW <= 0 || cellH <= 0 || aspect <= 0)
            return cell;

        int w = cellW, h = (int)Math.Round(cellW / aspect);
        if (h > cellH)
        {
            h = cellH;
            w = (int)Math.Round(cellH * aspect);
        }

        int x = cell.left + (cellW - w) / 2;
        int y = cell.top + (cellH - h) / 2;
        return new RECT { left = x, top = y, right = x + w, bottom = y + h };
    }

    /// <summary>Квадратная зона крестика закрытия у правого края подписи.</summary>
    public static RECT CloseRect(RECT label)
    {
        int size = label.bottom - label.top;
        return new RECT { left = label.right - size, top = label.top, right = label.right, bottom = label.bottom };
    }

    /// <summary>
    /// «Ручка» перетаскивания карточки — квадрат у левого края подписи
    /// (то есть в левом нижнем углу карточки). Сортировка запускается только
    /// отсюда: нажатие на само превью больше не двигает порядок.
    /// </summary>
    public static RECT GripRect(RECT label)
    {
        int size = label.bottom - label.top;
        return new RECT { left = label.left, top = label.top, right = label.left + size, bottom = label.bottom };
    }

    public static int Scale(int logical, uint dpi) => (int)(logical * dpi / 96.0);
}
