using System.Text.Json;
using System.Text.Json.Serialization;

namespace Montab.Config;

internal enum DockEdge
{
    Left,
    Right,
}

/// <summary>Оформление панели.</summary>
internal enum ThemeMode
{
    Light,
    Dark,
}

/// <summary>Что делает крестик на карточке.</summary>
internal enum CloseAction
{
    Close,
    Minimize,
}

/// <summary>Что делает клик по карточке уже активного окна.</summary>
internal enum ActiveClickAction
{
    SendToBottom,
    Minimize,
}

/// <summary>Панель одного монитора: сторона, ширина, показывать ли её вообще.</summary>
internal sealed class MonitorSettings
{
    /// <summary>Имя устройства из MONITORINFOEXW (\\.\DISPLAY1) — ключ настроек.</summary>
    public string Device { get; set; } = "";

    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double WidthPercent { get; set; } = Settings.DefaultWidthPercent;
    public bool Enabled { get; set; } = true;
}

internal sealed class Settings
{
    public const double DefaultWidthPercent = 10;
    public const double MinWidthPercent = 3;
    // До половины экрана: на вертикальных мониторах широкую панель реально используют
    public const double MaxWidthPercent = 50;

    /// <summary>Панели по мониторам; запись для монитора заводится при первом его появлении.</summary>
    public List<MonitorSettings> Monitors { get; set; } = [];

    // Поля панели-одиночки (версии до мультимонитора): читаются из старого
    // файла настроек и служат значениями по умолчанию для новых мониторов.
    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double WidthPercent { get; set; } = DefaultWidthPercent;

    /// <summary>
    /// Прозрачность акрилового (матового стекла) фона панели: 0 — выключено,
    /// 1–100 — сила затемнения подложки. Размытие задаётся системой и не регулируется.
    /// </summary>
    public int GlassAlpha { get; set; } = 0;

    /// <summary>Светлое или тёмное оформление панели.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Light;

    /// <summary>Крестик на карточке: закрыть программу или только свернуть.</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Close;

    /// <summary>Клик по карточке активного окна: убрать назад или свернуть.</summary>
    public ActiveClickAction ActiveClickAction { get; set; } = ActiveClickAction.SendToBottom;

    /// <summary>Свёрнутые окна показывать крупной карточкой (иначе — узкой полоской).</summary>
    public bool MinimizedAsCard { get; set; } = true;

    /// <summary>Увеличение превью при наведении.</summary>
    public bool HoverZoom { get; set; } = true;

    /// <summary>
    /// Запоминать раскладку значков рабочего стола для каждого числа столбцов:
    /// панель сужает рабочую область, и значки переезжают — возвращаем их назад.
    /// </summary>
    public bool RememberIcons { get; set; } = true;

    /// <summary>Показывать полосу прокрутки.</summary>
    public bool ShowScrollbar { get; set; } = true;

    /// <summary>
    /// Уступать полноэкранным окнам: AppBar сужает только рабочую область,
    /// а видео и игры во весь экран её не читают и уходят под панель.
    /// </summary>
    public bool YieldToFullscreen { get; set; } = true;

    /// <summary>
    /// Запуск из автозагрузки не показывает полосы — приложение ждёт в трее.
    /// Полосу включают кликом по значку, когда она действительно нужна.
    /// Ручной запуск (двойной клик по exe) это не затрагивает.
    /// </summary>
    public bool StartInTray { get; set; } = true;

    /// <summary>
    /// Скругление кнопок «桌面 / закрепить / настройки» под заголовком, в логических
    /// пикселях. 0 — обычные прямоугольники, как было до появления настройки.
    /// Значение ограничивается при записи: файл настроек правят и руками.
    /// </summary>
    public int ButtonRadius
    {
        get => _buttonRadius;
        set => _buttonRadius = Math.Clamp(value, 0, MaxButtonRadius);
    }

    int _buttonRadius = 4;

    /// <summary>
    /// Предел скругления в логических пикселях. Кнопка — 30 логических в высоту,
    /// полувысота 15 дала бы «капсулу», поэтому останавливаемся на 8.
    /// </summary>
    public const int MaxButtonRadius = 8;

    /// <summary>Зафиксировать порядок карточек: перетаскивание его больше не меняет.</summary>
    public bool LockOrder { get; set; }

    /// <summary>Настройки монитора; отсутствующие заводятся на лету по образцу первой панели.</summary>
    public MonitorSettings For(string device)
    {
        foreach (var mon in Monitors)
        {
            if (mon.Device == device)
                return mon;
        }

        // Новый монитор наследует геометрию уже настроенной панели, а не голый дефолт
        var template = Monitors.Count > 0 ? Monitors[0] : null;
        var created = new MonitorSettings
        {
            Device = device,
            Edge = template?.Edge ?? Edge,
            WidthPercent = template?.WidthPercent ?? WidthPercent,
        };
        Monitors.Add(created);
        return created;
    }

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "montab");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(FilePath), SettingsContext.Default.Settings);
                if (loaded is not null)
                {
                    loaded.WidthPercent = Math.Clamp(loaded.WidthPercent, MinWidthPercent, MaxWidthPercent);
                    foreach (var mon in loaded.Monitors)
                        mon.WidthPercent = Math.Clamp(mon.WidthPercent, MinWidthPercent, MaxWidthPercent);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsContext.Default.Settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsContext : JsonSerializerContext;
