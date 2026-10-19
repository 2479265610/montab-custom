using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Montab.Config;

namespace Montab.Core;

/// <summary>
/// Позиции значков рабочего стола: чтение и восстановление.
/// Значки живут в ListView оболочки (ProgMan → SHELLDLL_DefView → SysListView32),
/// поэтому координаты читаются и пишутся через LVM_*-сообщения с буфером
/// в адресном пространстве Explorer'а.
/// </summary>
internal static class DesktopIcons
{
    const uint LVM_GETITEMCOUNT = 0x1004;
    const uint LVM_SETITEMPOSITION = 0x100F;
    const uint LVM_GETITEMPOSITION = 0x1010;
    const uint LVM_GETITEMSPACING = 0x1033;
    const uint LVM_GETITEMTEXTW = 0x1073;

    const uint PROCESS_VM_OPERATION = 0x0008;
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_VM_WRITE = 0x0020;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;

    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint MEM_RELEASE = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    const int StructSize = 512;      // LVITEMW кладём в начало блока
    const int TextCapacity = 260;    // символов под имя значка
    const int BlockSize = StructSize + TextCapacity * 2;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>Один значок: имя и координаты в пикселях рабочего стола.</summary>
    public readonly record struct IconPos(string Name, int X, int Y);

    /// <summary>Текущие значки с позициями. Пустой список, если рабочий стол недоступен.</summary>
    public static List<IconPos> Snapshot()
    {
        var result = new List<IconPos>();

        WithDesktop((list, process, block) =>
        {
            int count = (int)SendMessageW(list, LVM_GETITEMCOUNT, default, default);
            for (int i = 0; i < count; i++)
            {
                string name = ReadName(list, process, block, i);
                if (name.Length == 0)
                    continue;
                if (!ReadPoint(list, process, block, i, out var point))
                    continue;
                result.Add(new IconPos(name, point.X, point.Y));
            }
        });

        return result;
    }

    /// <summary>Расставляет значки по сохранённым координатам. Возвращает число перенесённых.</summary>
    public static int Apply(IReadOnlyList<IconPos> saved)
    {
        if (saved.Count == 0)
            return 0;

        var byName = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (var item in saved)
            byName[item.Name] = (item.X, item.Y);

        int moved = 0;
        WithDesktop((list, process, block) =>
        {
            int count = (int)SendMessageW(list, LVM_GETITEMCOUNT, default, default);
            for (int i = 0; i < count; i++)
            {
                if (!byName.TryGetValue(ReadName(list, process, block, i), out var target))
                    continue;
                if (!ReadPoint(list, process, block, i, out var current))
                    continue;
                if (current.X == target.X && current.Y == target.Y)
                    continue;

                // LVM_SETITEMPOSITION: координаты упакованы в lParam (16 бит на ось).
                // Значки всё равно прилипают к сетке, поэтому сохраняем и возвращаем
                // те позиции, что уже стоят в узлах — тогда «обратный ход» точный.
                var packed = new IntPtr((target.Y << 16) | (target.X & 0xFFFF));
                SendMessageW(list, LVM_SETITEMPOSITION, new IntPtr(i), packed);
                moved++;
            }

            if (moved > 0)
                InvalidateRect(list, IntPtr.Zero, false);
        });

        return moved;
    }

    /// <summary>Сколько столбцов значков помещается в текущую рабочую область.</summary>
    public static int Columns()
    {
        int columns = 0;

        WithDesktop((list, _, _) =>
        {
            if (!GetClientRect(list, out var client))
                return;

            int width = client.Right - client.Left;
            int spacing = (int)SendMessageW(list, LVM_GETITEMSPACING, new IntPtr(1), default);
            int cell = spacing & 0xFFFF;
            if (cell <= 8)
                cell = 136;

            columns = Math.Max(0, width / cell);
        });

        return columns;
    }

    /// <summary>Открывает ListView рабочего стола и даёт доступ к памяти Explorer.</summary>
    static void WithDesktop(Action<IntPtr, IntPtr, IntPtr> action)
    {
        IntPtr list = FindDesktopList();
        if (list == IntPtr.Zero)
            return;

        GetWindowThreadProcessId(list, out uint pid);
        if (pid == 0)
            return;

        IntPtr process = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
            false, pid);
        if (process == IntPtr.Zero)
            return;

        IntPtr block = VirtualAllocEx(process, IntPtr.Zero, BlockSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (block == IntPtr.Zero)
        {
            CloseHandle(process);
            return;
        }

        try
        {
            action(list, process, block);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DesktopIcons: {ex.Message}");
        }
        finally
        {
            VirtualFreeEx(process, block, 0, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    static IntPtr FindDesktopList()
    {
        IntPtr progman = FindWindowW("ProgMan", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr defView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                IntPtr list = FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
                if (list != IntPtr.Zero)
                    return list;
            }
        }

        // На части конфигураций DefView переезжает в отдельное окно WorkerW
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            IntPtr defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
                return true;
            IntPtr list = FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
            if (list == IntPtr.Zero)
                return true;
            found = list;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    static string ReadName(IntPtr list, IntPtr process, IntPtr block, int index)
    {
        var item = new LVITEMW
        {
            mask = 0x0001, // LVIF_TEXT
            iItem = index,
            iSubItem = 0,
            pszText = block + StructSize,
            cchTextMax = TextCapacity,
        };

        int size = Marshal.SizeOf<LVITEMW>();
        IntPtr local = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(item, local, false);
            if (!WriteProcessMemory(process, block, local, (nuint)size, out _))
                return "";
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }

        SendMessageW(list, LVM_GETITEMTEXTW, new IntPtr(index), block);

        IntPtr text = Marshal.AllocHGlobal(TextCapacity * 2);
        try
        {
            if (!ReadProcessMemory(process, block + StructSize, text, (nuint)(TextCapacity * 2), out _))
                return "";
            return Marshal.PtrToStringUni(text) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    static bool ReadPoint(IntPtr list, IntPtr process, IntPtr block, int index, out POINT point)
    {
        SendMessageW(list, LVM_GETITEMPOSITION, new IntPtr(index), block);

        int size = Marshal.SizeOf<POINT>();
        IntPtr local = Marshal.AllocHGlobal(size);
        try
        {
            if (!ReadProcessMemory(process, block, local, (nuint)size, out _))
            {
                point = default;
                return false;
            }
            point = Marshal.PtrToStructure<POINT>(local);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }
}

/// <summary>Файл раскладок: ключ — число столбцов, значение — «имя значка → [x, y]».</summary>
internal sealed class IconLayoutFile
{
    public Dictionary<string, Dictionary<string, int[]>> Buckets { get; set; } = [];
}

/// <summary>
/// Хранилище раскладок значков по «сегментам» рабочей области.
/// Ключ — число столбцов, помещающихся на рабочем столе: пока столбцов столько же,
/// раскладка не меняется; при потере столбца Windows перекладывает значки,
/// и мы возвращаем сохранённую раскладку именно для нового числа столбцов.
/// </summary>
internal sealed class IconLayoutKeeper : IDisposable
{
    const int PollMs = 1500;

    readonly Settings _settings;
    readonly IconLayoutFile _file;
    readonly Timer _timer;
    string _bucket = "";
    int _busy;
    int _lastWriteTick;
    bool _dirty;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "montab", "icons.json");

    public IconLayoutKeeper(Settings settings)
    {
        _settings = settings;
        _file = Load();
        _timer = new Timer(_ => Tick(), null, 2500, PollMs);
    }

    /// <summary>Возврат к раскладке без панели — самый широкий сохранённый сегмент.</summary>
    public void RestoreWidest()
    {
        try
        {
            var key = _file.Buckets.Keys
                .Select(k => (Key: k, Columns: int.TryParse(k, out int c) ? c : -1))
                .Where(x => x.Columns > 0)
                .OrderByDescending(x => x.Columns)
                .Select(x => x.Key)
                .FirstOrDefault();

            if (key is not null && _file.Buckets.TryGetValue(key, out var layout))
                DesktopIcons.Apply(ToPositions(layout));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"IconLayoutKeeper.RestoreWidest: {ex.Message}");
        }
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            if (!_settings.RememberIcons)
            {
                _bucket = "";
                return;
            }

            int columns = DesktopIcons.Columns();
            if (columns <= 0)
                return;

            string key = columns.ToString();
            if (key != _bucket)
            {
                // Раскладку прежнего сегмента сохранил предыдущий тик — здесь
                // перезаписывать её нельзя: Windows уже могла переложить значки.
                _bucket = key;

                if (_file.Buckets.TryGetValue(key, out var saved) && saved.Count > 0)
                {
                    var positions = ToPositions(saved);
                    DesktopIcons.Apply(positions);
                    // Оболочка перекладывает значки асинхронно — добиваем вторым проходом
                    Thread.Sleep(320);
                    DesktopIcons.Apply(positions);
                }
                else
                {
                    Store(key, DesktopIcons.Snapshot());
                }
            }
            else
            {
                Store(key, DesktopIcons.Snapshot());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"IconLayoutKeeper.Tick: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Обновляет раскладку сегмента; файл пишется не чаще раза в 5 секунд.</summary>
    void Store(string key, List<DesktopIcons.IconPos> snapshot)
    {
        if (snapshot.Count == 0)
            return;

        var layout = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var icon in snapshot)
            layout[icon.Name] = [icon.X, icon.Y];

        if (_file.Buckets.TryGetValue(key, out var existing) && SameLayout(existing, layout))
            return;

        _file.Buckets[key] = layout;
        _dirty = true;

        int now = Environment.TickCount;
        if (_lastWriteTick != 0 && now - _lastWriteTick < 5000)
            return;

        _lastWriteTick = now;
        Save();
    }

    static bool SameLayout(Dictionary<string, int[]> left, Dictionary<string, int[]> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (name, position) in left)
        {
            if (!right.TryGetValue(name, out var other) ||
                other[0] != position[0] || other[1] != position[1])
                return false;
        }

        return true;
    }

    static List<DesktopIcons.IconPos> ToPositions(Dictionary<string, int[]> layout)
    {
        var list = new List<DesktopIcons.IconPos>(layout.Count);
        foreach (var (name, position) in layout)
        {
            if (position.Length == 2)
                list.Add(new DesktopIcons.IconPos(name, position[0], position[1]));
        }

        return list;
    }

    void Save()
    {
        if (!_dirty)
            return;

        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_file, IconLayoutContext.Default.IconLayoutFile));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // раскладки значков — приятный бонус, не повод падать
        }
    }

    static IconLayoutFile Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new IconLayoutFile();

            return JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), IconLayoutContext.Default.IconLayoutFile) ?? new IconLayoutFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new IconLayoutFile();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Save();
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(IconLayoutFile))]
internal sealed partial class IconLayoutContext : JsonSerializerContext;
