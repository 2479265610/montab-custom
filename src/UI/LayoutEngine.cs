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
    // Сторона зоны захвата «ручки» в левом нижнем углу карточки.
    public const int CornerLogical = 28;
    /// <summary>Высота кнопки «桌面» под ручкой.</summary>
    const int DesktopBarLogical = 30;
    /// <summary>
    /// Запасной коэффициент высоты превью (высота/ширина) на случай, когда
    /// пропорции монитора неизвестны. 16:9 — самый частый случай.
    /// </summary>
    const double FallbackPreviewAspect = 9.0 / 16.0;
    /// <summary>
    /// Пределы высоты превью в карточке. Верхняя граница нужна для мониторов
    /// в портретной ориентации: там пропорции экрана дали бы превью выше
    /// самой карточки, и одна карточка занимала бы весь экран.
    /// </summary>
    const double MinPreviewAspect = 0.30;
    const double MaxPreviewAspect = 0.85;
    /// <summary>
    /// Пределы высоты превью для режима «карточка по пропорциям своего окна»
    /// (когда единый размер выключен). Те же, что были до 1.9.8: слишком
    /// низкая карточка нечитаема, слишком высокая занимает весь экран.
    /// </summary>
    const int MinPreviewHeightLogical = 48;
    const double MaxPreviewHeightFactor = 1.6;

    readonly List<LayoutItem> _result = [];

    // Предвычисленные размеры на текущий DPI
    uint _dpi;
    int _header, _strip, _label, _gap, _padding, _minPreview, _desktopBar, _corner;

    public int TotalHeight { get; private set; }

    /// <summary>
    /// Сторона квадратной «ручки» в правом нижнем углу карточки.
    /// Зависит от высоты последней карточки: при низких карточках (единый
    /// размер по пропорциям экрана) угол в 28 логических пикселей занимал бы
    /// треть превью, поэтому зона захвата ужимается вместе с карточкой.
    /// </summary>
    public int GripSize { get; private set; }

    /// <summary>Область кнопки «桌面» в клиентских координатах.</summary>
    public RECT DesktopBar { get; private set; }

    /// <summary>Область кнопки-кнопки «закрепить порядок» справа от неё.</summary>
    public RECT PinButton { get; private set; }

    /// <summary>Кнопка настроек (три полоски) в правом верхнем углу.</summary>
    public RECT GearButton { get; private set; }

    public IReadOnlyList<LayoutItem> Compute(IReadOnlyList<WindowItem> items, RECT client, uint dpi,
        int scrollOffset, bool minimizedAsCard = true, double screenAspect = 0, bool uniformCards = true)
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

        // Слева — «桌面», справа — фиксация порядка и кнопка настроек.
        int span = barRight - left;
        int btn = Math.Max(Scale(22, dpi), Math.Min((int)(_desktopBar * 1.15), (span - 2 * _gap) / 4));
        int split = Math.Max(left + _desktopBar, barRight - 2 * btn - 2 * _gap);

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
            right = split + _gap + btn,
            bottom = barBottom,
        };
        GearButton = new RECT
        {
            left = barRight - btn,
            top = barTop,
            right = barRight,
            bottom = barBottom,
        };
        int y = barBottom + _gap - scrollOffset;

        // Высота «эталонной» карточки. Считаем её по пропорциям развёрнутого
        // окна на этом мониторе: карточка шириной w изображает окно, а окно
        // во весь экран имеет пропорции самого экрана. Отсюда высота w/aspect —
        // столько же, сколько занимала карточка, когда окно было развёрнуто.
        // Развёрнутое окно в 16:9 или 16:10 — обычное состояние, поэтому
        // лента выглядит «как раньше», но длинные и вертикальные окна больше
        // не растягивают её.
        double refAspect = screenAspect > 0.1 ? screenAspect : 1.0 / FallbackPreviewAspect;
        double refRatio = Math.Clamp(1.0 / refAspect, MinPreviewAspect, MaxPreviewAspect);
        int uniformPreview = Math.Max(Scale(24, dpi), (int)Math.Round(width * refRatio));
        int lastGrip = _corner;

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
                // Единый размер: все карточки одинаковые, а превью вписывается
                // внутрь по центру (FitRect) — длинное окно просто мельче.
                // Иначе — прежнее поведение: высота по пропорциям своего окна.
                int previewHeight = uniformCards
                    ? uniformPreview
                    : Math.Clamp(
                        (int)Math.Round(width / (item.Aspect > 0.01 ? item.Aspect : 1.6)),
                        _minPreview,
                        Math.Max(_minPreview, (int)(width * MaxPreviewHeightFactor)));

                bounds = new RECT
                {
                    left = left,
                    top = y,
                    right = left + width,
                    bottom = y + _label + previewHeight,
                };
                label = new RECT { left = left, top = y, right = left + width, bottom = y + _label };
                preview = new RECT
                {
                    left = left,
                    top = y + _label,
                    right = left + width,
                    bottom = bounds.bottom,
                };
            }

            _result.Add(new LayoutItem(item, bounds, preview, label, isStrip));
            y = bounds.bottom + _gap;

            if (!isStrip)
            {
                // Зона захвата ручки — не больше трети высоты карточки:
                // на низких карточках угол иначе съедал бы половину превью.
                int cardH = bounds.bottom - bounds.top;
                lastGrip = Math.Clamp(cardH / 3, Scale(10, dpi), _corner);
            }
        }

        GripSize = lastGrip > 0 ? lastGrip : _corner;

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
        _corner = Scale(CornerLogical, dpi);
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
    /// Зона захвата «ручки» перетаскивания — квадрат в левом нижнем углу карточки
    /// (сами косые рисует отдельный оверлей поверх превью). Нажатие только здесь
    /// запускает сортировку: клик по превью больше не двигает порядок.
    /// </summary>
    public static RECT GripRect(RECT bounds, int size)
    {
        return new RECT
        {
            left = bounds.left,
            top = bounds.bottom - size,
            right = bounds.left + size,
            bottom = bounds.bottom,
        };
    }

    public static int Scale(int logical, uint dpi) => (int)(logical * dpi / 96.0);
}
