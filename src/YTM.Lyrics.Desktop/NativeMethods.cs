using System.Runtime.InteropServices;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// Win32 P/Invoke 集中声明（TASK-301）：
/// - RegisterHotKey/UnregisterHotKey：全局热键（Alt+Shift+W 桌面歌词开关，UI_SPEC §5.1）
/// - GetWindowLong/SetWindowLong：WS_EX_TRANSPARENT 点击穿透切换（UI_SPEC §3.2）
/// 指针宽度分派：64 位进程走 *Ptr 变体，32 位走 W 变体（user32 只导出带后缀名）。
/// </summary>
internal static class NativeMethods
{
    // ---- 窗口扩展样式 ----
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x00000020; // 点击穿透：窗口不接收鼠标消息

    // ---- 热键 ----
    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002; // TASK-402：Ctrl+Shift+R 重置窗口位置（UI_SPEC §5.1）
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint VK_W = 0x57;
    internal const uint VK_R = 0x52;         // TASK-402

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>读窗口扩展样式（按进程位数分派到 *Ptr / W 变体）。</summary>
    internal static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtrW(hWnd, nIndex) : GetWindowLongW(hWnd, nIndex);

    /// <summary>写窗口扩展样式（按进程位数分派）。</summary>
    internal static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtrW(hWnd, nIndex, dwNewLong) : SetWindowLongW(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLongW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
