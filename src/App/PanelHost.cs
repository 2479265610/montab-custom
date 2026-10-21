using Montab.Config;
using Montab.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Montab.App;

/// <summary>
/// Владелец приложения: один трекер окон на всю систему, по панели на каждый
/// включённый монитор и иконка в трее. Пересобирает набор панелей, когда
/// меняется конфигурация дисплеев или настройки.
/// </summary>
internal sealed unsafe class PanelHost
{
    readonly Settings _settings;
    readonly WindowTracker _tracker = new();
    readonly SwitchController _switch = new();
    readonly List<DisplayInfo> _displays = [];
    readonly Dictionary<string, PanelWindow> _panels = [];
    readonly List<string> _stale = [];
    readonly HINSTANCE _hInstance;
    TrayIcon? _tray;
    SettingsWindow? _settingsWindow;
    IconLayoutKeeper? _icons;
    /// <summary>Быстрое «скрыть всё» из трея: панелей нет, но настройки мониторов целы.</summary>
    bool _hidden;

    public PanelHost(Settings settings)
    {
        _settings = settings;
        _hInstance = (HINSTANCE)PInvoke.GetModuleHandle(default(string)).Value;
    }

    public Settings Settings => _settings;

    public WindowTracker Tracker => _tracker;

    public SwitchController Switch => _switch;

    /// <summary>Мониторы системы слева направо — порядок пунктов в меню трея.</summary>
    public IReadOnlyList<DisplayInfo> Displays => _displays;

    /// <summary>Дисплей для страницы настроек, открытой из трея.</summary>
    public string PrimaryDevice =>
        _displays.Count > 0 ? _displays[0].Device
        : _settings.Monitors.Count > 0 ? _settings.Monitors[0].Device : "";

    /// <summary>Все панели временно скрыты (левый клик по иконке в трее).</summary>
    public bool Hidden => _hidden;

    public void Start()
    {
        _tray = new TrayIcon(this, _hInstance);
        _icons = new IconLayoutKeeper(_settings);

        _tracker.Changed += OnTrackerChanged;
        _tracker.ForegroundChanged += _switch.OnForegroundChanged;
        // Автопереходы по истории не идут в свёрнутые окна
        _switch.IsEligibleTarget = hwnd => _tracker.TryGet(hwnd, out var item) && !item.IsMinimized;

        RefreshDisplays();
        _tracker.Start();
        _switch.OnForegroundChanged(_tracker.ForegroundWindow);
    }

    void OnTrackerChanged()
    {
        foreach (var panel in _panels.Values)
            panel.Invalidate();
    }

    /// <summary>Пересобирает набор панелей под текущие мониторы и настройки.</summary>
    public void RefreshDisplays()
    {
        DisplayList.Enumerate(_displays);

        // Монитор отключили, панель на нём выключили или скрыли всё — окно больше не нужно
        _stale.Clear();
        foreach (var (device, _) in _panels)
        {
            if (_hidden || !TryFind(device, out _) || !_settings.For(device).Enabled)
                _stale.Add(device);
        }
        foreach (var device in _stale)
        {
            _panels[device].Destroy();
            _panels.Remove(device);
        }

        foreach (var display in _displays)
        {
            var mon = _settings.For(display.Device);
            if (!mon.Enabled || _hidden)
                continue;

            if (_panels.TryGetValue(display.Device, out var existing))
            {
                existing.SetDisplay(display);
            }
            else
            {
                var panel = new PanelWindow(this, mon);
                panel.Create(_hInstance, display);
                _panels[display.Device] = panel;
            }
        }

        // Окна могли переехать вместе с изменившейся геометрией дисплеев
        _tracker.RefreshMonitors();
        _tray?.SyncIcon();
    }

    /// <summary>Explorer перезапустился и забыл все appbar'ы — регистрируемся заново.</summary>
    public void ReregisterAppBars()
    {
        foreach (var panel in _panels.Values)
            panel.ReregisterAppBar();
    }

    /// <summary>
    /// Самолечение после гибернации/сброса work area: панель, под которую
    /// залезла рабочая область, регистрируется заново. true — что-то чинили.
    /// </summary>
    public bool HealAppBars()
    {
        bool healed = false;
        foreach (var panel in _panels.Values)
        {
            if (panel.IsWorkAreaBroken())
            {
                panel.ReregisterAppBar();
                healed = true;
            }
        }
        return healed;
    }

    /// <summary>
    /// Левый клик по иконке в трее: убрать/вернуть панели на всех мониторах.
    /// Настройки мониторов не трогаются — при возврате всё как было.
    /// Если панелей нет вовсе (все выключены в меню), клик включает основную:
    /// иначе значок выглядел бы «мёртвым».
    /// </summary>
    public void ToggleHidden()
    {
        // Панели есть, но их не видно только потому, что они уступили
        // полноэкранному окну: клик должен вернуть их на экран, а не уйти
        // в «скрыть всё» — иначе первое нажатие выглядело бы безрезультатным.
        if (!_hidden && AllPanelsYielded())
        {
            foreach (var panel in _panels.Values)
                panel.ForceShow();
            return;
        }

        // Нет ни одной включённой панели (пользователь всё выключил в меню,
        // либо это первый запуск): клик должен показать панели, а не крутить
        // «пусто» между нулём и нулём — иначе значок выглядит мёртвым.
        if (!AnyEnabled())
        {
            _hidden = false;
            EnableAllFromTray();
            _settings.Save();
            RefreshDisplays();
            return;
        }

        _hidden = !_hidden;
        RefreshDisplays();

        // Возврат панелей — явное намерение пользователя: держим их поверх
        // полноэкранного окна до конца этого полноэкранного режима, иначе
        // собственный опрос спрятал бы их обратно через полсекунды.
        if (!_hidden)
        {
            foreach (var panel in _panels.Values)
                panel.ForceShow();
        }
    }

    /// <summary>Все существующие панели уступили полноэкранному окну.</summary>
    bool AllPanelsYielded()
    {
        if (_panels.Count == 0)
            return false;
        foreach (var panel in _panels.Values)
        {
            if (!panel.Yielded)
                return false;
        }
        return true;
    }

    /// <summary>Хоть один монитор помечен включённым.</summary>
    bool AnyEnabled()
    {
        foreach (var mon in _settings.Monitors)
        {
            if (mon.Enabled)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Включает все мониторы, которые когда-то были настроены, но затем
    /// выключены. Так «показать панель» из трея возвращает привычную картину,
    /// а не одну полосу на главном экране.
    /// </summary>
    void EnableAllFromTray()
    {
        // Сначала заводим записи для реально подключённых мониторов: у только что
        // установленного приложения списка настроек может не быть вовсе
        foreach (var display in _displays)
            _settings.For(display.Device).Enabled = true;

        foreach (var mon in _settings.Monitors)
            mon.Enabled = true;
    }

    public void SetEnabled(string device, bool enabled)
    {
        var mon = _settings.For(device);
        // Включение панели из меню — явная просьба её показать, снимает «скрыть всё»
        bool unhide = enabled && _hidden;
        if (mon.Enabled == enabled && !unhide)
            return;

        mon.Enabled = enabled;
        _hidden &= !unhide;
        _settings.Save();
        RefreshDisplays();
    }

    public void SetEdge(string device, DockEdge edge)
    {
        var mon = _settings.For(device);
        if (mon.Edge == edge)
            return;
        mon.Edge = edge;
        _settings.Save();
        if (_panels.TryGetValue(device, out var panel))
            panel.Relayout();
        SyncSettingsWindow();
    }

    /// <summary>Светлое/тёмное оформление — общее для всех панелей.</summary>
    public void SetTheme(ThemeMode theme)
    {
        if (_settings.Theme == theme)
            return;
        _settings.Theme = theme;
        _settings.Save();
        foreach (var panel in _panels.Values)
            panel.ApplyTheme();
        SyncSettingsWindow();
    }

    /// <summary>Ширина полосы в процентах от рабочей области монитора.</summary>
    public void SetWidth(string device, double percent)
    {
        var mon = _settings.For(device);
        percent = Math.Clamp(percent, 5, 50);
        if (Math.Abs(mon.WidthPercent - percent) < 0.05)
            return;
        mon.WidthPercent = percent;
        _settings.Save();
        if (_panels.TryGetValue(device, out var panel))
            panel.Relayout();
        SyncSettingsWindow();
    }

    /// <summary>Прозрачность матового стекла: 0 — выключено, 1–100 — плотность.</summary>
    public void SetGlass(int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 100);
        if (_settings.GlassAlpha == alpha)
            return;
        _settings.GlassAlpha = alpha;
        _settings.Save();
        foreach (var panel in _panels.Values)
            panel.ApplyGlass();
        SyncSettingsWindow();
    }

    /// <summary>Меняет настройки поведения и сразу применяет их ко всем панелям.</summary>
    public void UpdatePreferences(Action<Settings> mutate)
    {
        mutate(_settings);
        _settings.Save();
        foreach (var panel in _panels.Values)
            panel.Refresh();
        SyncSettingsWindow();
    }

    /// <summary>
    /// Панель бросили на другой монитор: если он свободен — переезжаем туда,
    /// у занятого своя панель уже есть, и трогать её незачем.
    /// </summary>
    public void MovePanel(PanelWindow panel, HMONITOR target, int cursorX)
    {
        if (!TryFind(target, out var display))
            return;

        var mon = _settings.For(display.Device);
        if (mon.Enabled)
            return;

        mon.Enabled = true;
        mon.Edge = cursorX < (display.Rect.left + display.Rect.right) / 2 ? DockEdge.Left : DockEdge.Right;
        _settings.For(panel.Device).Enabled = false;
        _settings.Save();
        RefreshDisplays();
    }

    /// <summary>Страница настроек (одна на приложение, открывается с панели или из трея).</summary>
    public void OpenSettings(string device)
    {
        _settingsWindow ??= new SettingsWindow(this, device);
        _settingsWindow.Show(_hInstance);
    }

    /// <summary>Обновить открытую страницу настроек, если она есть.</summary>
    void SyncSettingsWindow() => _settingsWindow?.Refresh();

    public void Save() => _settings.Save();

    public void Exit()
    {
        _settings.Save();

        foreach (var panel in _panels.Values)
            panel.Destroy();
        _panels.Clear();

        // Рабочая область вернулась на место — возвращаем раскладку значков
        // той ширины, на которой панели нет.
        Thread.Sleep(250);
        _icons?.RestoreWidest();
        _icons?.Dispose();
        _icons = null;

        _tray?.Dispose();
        _tray = null;
        _tracker.Dispose();
        PInvoke.PostQuitMessage(0);
    }

    bool TryFind(string device, out DisplayInfo display)
    {
        foreach (var candidate in _displays)
        {
            if (candidate.Device == device)
            {
                display = candidate;
                return true;
            }
        }
        display = default;
        return false;
    }

    bool TryFind(HMONITOR handle, out DisplayInfo display)
    {
        foreach (var candidate in _displays)
        {
            if (candidate.Handle == handle)
            {
                display = candidate;
                return true;
            }
        }
        display = default;
        return false;
    }
}
