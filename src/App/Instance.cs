using Windows.Win32;

namespace Montab.App;

/// <summary>
/// Межпроцессный сигнал «покажи панели». Запуск второго экземпляра (двойной
/// клик по exe), когда приложение уже висит в трее из автозагрузки, не должен
/// теряться: второй экземпляр лишь широковещательно просит первый показаться.
/// </summary>
internal static class Instance
{
    /// <summary>
    /// Зарегистрированное сообщение: второй экземпляр шлёт его широковещательно,
    /// адресата он не знает. Получат все окна верхнего уровня, включая окно трея.
    /// </summary>
    public static readonly uint ActivateMessage = PInvoke.RegisterWindowMessage("montab.activate");
}