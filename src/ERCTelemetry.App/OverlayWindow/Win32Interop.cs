using System.Runtime.InteropServices;

namespace ERCTelemetry.App.OverlayWindow;

/// <summary>Win32 interop for the click-through in-game overlay window and its
/// global hotkey. Only what the overlay window actually needs — no generator framework.</summary>
internal static class Win32Interop
{
    public const int GWL_EXSTYLE = -20;

    /// <summary>Mouse events pass through the window to the game below.</summary>
    public const long WsExTransparent = 0x0000_0020L;

    /// <summary>The window never takes keyboard focus (game keeps it).</summary>
    public const long WsExNoActivate = 0x0800_0000L;

    /// <summary>Hides the window from Alt+Tab.</summary>
    public const long WsExToolWindow = 0x0000_0080L;

    public const int WmHotkey = 0x0312;

    /// <summary>Arbitrary app-unique id for RegisterHotKey (Ctrl+Shift+O — overlay toggle).</summary>
    public const int OverlayHotKeyId = 0x4F31;

    /// <summary>Arbitrary app-unique hotkey id (Ctrl+Shift+R — force-show rival panel).</summary>
    public const int RivalHotKeyId = 0x4F32;

    /// <summary>Arbitrary app-unique hotkey id (Ctrl+Shift+M — toggle the HUD minimap).</summary>
    public const int MapHotKeyId = 0x4F34;

    /// <summary>Base id for the Ctrl+1..6 nav hotkeys; the rail index is added on top
    /// (0x4F40 = Ctrl+1 → Setup-Tab → Debug).</summary>
    public const int NavHotKeyBase = 0x4F40;

    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;
    public const uint VkO = 0x4F; // Ctrl+Shift+O toggles the overlay window
    public const uint VkR = 0x52; // Ctrl+Shift+R force-shows the rival panel
    public const uint VkD = 0x44; // Ctrl+Shift+D toggles the Debug tab
    public const uint VkM = 0x4D; // Ctrl+Shift+M toggles the HUD minimap widget
    public const uint Vk1 = 0x31; // Ctrl+1..6 nav rail (Vk1 + index)

    /// <summary>Arbitrary app-unique hotkey id (Ctrl+Shift+D — toggle the Debug tab).</summary>
    public const int DebugHotKeyId = 0x4F33;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>Applies <paramref name="style"/> bits to the window's extended style,
    /// preserving everything already set.</summary>
    public static void AddExtendedStyle(IntPtr hWnd, long style)
    {
        var current = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(current | style));
    }

    /// <summary>Clears <paramref name="style"/> bits from the window's extended style,
    /// preserving everything else (e.g. drop WS_EX_TRANSPARENT for position mode).</summary>
    public static void RemoveExtendedStyle(IntPtr hWnd, long style)
    {
        var current = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(current & ~style));
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndNoTopmost = new(-2);

    public const uint SwpNosize = 0x0001;
    public const uint SwpNomove = 0x0002;
    public const uint SwpNoactivate = 0x0010;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaBorderColor = 34;
    public const int DwmwcpRound = 2;

    /// <summary>Best-effort DWM polish for the main window: dark title bar (moot while
    /// custom chrome is drawn, but harmless), Win11 rounded corners and an F1-red DWM
    /// border. Every attribute is fire-and-forget — older Windows returns E_INVALIDARG
    /// and the default look is kept.</summary>
    public static void ApplyWindowChrome(IntPtr hwnd, bool dark, uint borderColorRef)
    {
        try
        {
            var darkOn = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkOn, sizeof(int));
            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            var color = unchecked((int)borderColorRef);
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
        }
        catch
        {
            // dwmapi.dll unavailable (pre-Vista) — keep the default look.
        }
    }
}