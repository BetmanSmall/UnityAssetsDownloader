using System.Runtime.InteropServices;

/// <summary>
/// Раскладка окон: консоль слева, браузер справа.
/// Так видно и логи, и что происходит в браузере, без переключения окон.
///
/// Работает на Windows. На других системах размеры экрана не определяются,
/// и программа просто оставляет окна как есть.
/// </summary>
internal static class ScreenLayout
{
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>Половина экрана справа — туда встаёт браузер.</summary>
    public static Rect? RightHalf()
    {
        var area = GetWorkArea();
        if (area is null)
        {
            return null;
        }

        var a = area.Value;
        var half = a.Width / 2;
        return new Rect(a.X + half, a.Y, a.Width - half, a.Height);
    }

    /// <summary>
    /// Ставит окно консоли в левую половину экрана.
    /// Возвращает false, если это не Windows или окна консоли нет.
    /// </summary>
    public static bool MoveConsoleToLeftHalf()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var area = GetWorkArea();
            if (area is null)
            {
                return false;
            }

            var a = area.Value;
            var half = a.Width / 2;

            // Развёрнутое окно не двигается, поэтому сначала возвращаем обычный размер.
            ShowWindow(hwnd, SwRestore);
            return SetWindowPos(hwnd, IntPtr.Zero, a.X, a.Y, half, a.Height, SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Рабочая область экрана — без панели задач.</summary>
    private static Rect? GetWorkArea()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (SystemParametersInfo(SpiGetWorkArea, 0, out var r, 0) && r.Right > r.Left)
            {
                return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }

            var w = GetSystemMetrics(SmCxScreen);
            var h = GetSystemMetrics(SmCyScreen);
            return w > 0 && h > 0 ? new Rect(0, 0, w, h) : null;
        }
        catch
        {
            return null;
        }
    }

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SpiGetWorkArea = 0x0030;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out NativeRect rect, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
}
