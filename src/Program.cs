using Montab.App;
using Montab.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

using var singleInstance = new Mutex(initiallyOwned: true, "montab.single-instance", out bool isFirst);

if (!isFirst)
{
    // Приложение уже работает (обычно — из автозагрузки, в режиме трея).
    // Ручной запуск должен показать полосу, а не молча ничего не сделать:
    // просим первый экземпляр развернуть панели.
    // 0xFFFF — HWND_BROADCAST: адресат неизвестен, слушают все окна верхнего уровня.
    PInvoke.PostMessage(new HWND(0xFFFF), Instance.ActivateMessage, default, default);
    return;
}

var settings = Settings.Load();

// Ключ --tray ставит задача автозапуска: приложение ждёт в трее, полосу
// не показывает. Ручной запуск двойным кликом по exe это не затрагивает.
bool fromAutostart = args.Any(a => a is "--tray" or "-t");
bool startInTray = fromAutostart && settings.StartInTray;

var host = new PanelHost(settings);
host.Start(startInTray);
host.ListenForActivation();

// Панель администраторская: старый автозапуск через Run переводим на задачу
// планировщика (иначе при входе будет лишний запрос UAC), а задачу прошлых
// версий — на запуск в режиме трея. Ждём ответа планировщика секундами, поэтому
// уводим в отдельный поток: иначе первый экземпляр не успевал создать окно
// трея, и запуск вторым кликом некому было развернуть панели.
var migration = new Thread(Autostart.MigrateAutostart) { IsBackground = true };
migration.Start();

// Запуск сразу со страницей настроек: `montab.exe --settings`
if (args.Any(a => a is "--settings" or "-s"))
    host.OpenSettings(host.PrimaryDevice);

while (PInvoke.GetMessage(out MSG msg, default, 0, 0))
{
    PInvoke.TranslateMessage(in msg);
    PInvoke.DispatchMessage(in msg);
}
