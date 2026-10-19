namespace Montab.App;

/// <summary>Временная диагностика: пишем в %TEMP%\montab-diag.log.</summary>
internal static class Diag
{
    public static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "montab-diag.log"),
                msg + "\n");
        }
        catch
        {
            // диагностика не должна ломать приложение
        }
    }
}
