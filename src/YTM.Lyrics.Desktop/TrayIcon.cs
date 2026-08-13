using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// TASK-501：系统托盘图标（原生 Shell_NotifyIcon，P/Invoke，无 System.Windows.Forms / 第三方依赖）。
///
/// 消息泵：不另起消息循环——托盘回调消息（WM_APP）由 App 在主窗口 HwndSource 的
/// WndProc（HotKeyWndProc）里转发给 <see cref="HandleMessage"/>，全部在 UI 线程执行。
///
/// 右键菜单：Win32 TrackPopupMenu（TPM_RETURNCMD 同步返回选中命令 ID，无 WPF ContextMenu
/// 的焦点/自动关闭坑；样式为系统经典菜单，可辨识即可）。
///
/// 回调事件（均在 UI 线程触发）：
/// - 双击图标 / 菜单"显示主窗口" → ShowMainWindowRequested
/// - 菜单"显示/隐藏桌面歌词" → ToggleDesktopRequested
/// - 菜单"退出" → ExitRequested
///
/// 生命周期：Register(hwnd) 在启动注册（NIM_ADD）；Remove() 在 App.OnExit 调用（NIM_DELETE，
/// 防托盘图标残留）。注册/移除各输出一条 Trace 日志供取证。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>托盘回调消息 ID（WM_APP 起，不与系统消息冲突）。App 的 WndProc 据此转发。</summary>
    internal const int CallbackMessage = NativeMethods.WM_APP;

    // 右键菜单命令 ID（仅 TPM_RETURNCMD 返回用，无需 WM_COMMAND 通知）
    private const int CmdShowMainWindow = 0x1001;
    private const int CmdToggleDesktop = 0x1002;
    private const int CmdExit = 0x1003;
    private const uint TrayIconId = 1; // 本进程唯一托盘项

    /// <summary>显示主窗口（还原 + 激活）。</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>显示/隐藏桌面歌词（toggle）。</summary>
    public event Action? ToggleDesktopRequested;

    /// <summary>退出应用。</summary>
    public event Action? ExitRequested;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _registered;
    private bool _menuUp; // TrackPopupMenu 重入保护（回调重入时跳过）

    /// <summary>注册托盘图标（NIM_ADD）。hwnd = 主窗口 HWND（消息泵宿主）。</summary>
    public void Register(IntPtr hwnd)
    {
        if (_registered || hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = CreateTrayIcon(),
            szTip = "YTM.Lyrics",
        };
        _icon = data.hIcon;

        _registered = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (_registered)
        {
            Trace.WriteLine($"[TrayIcon] Shell_NotifyIcon(NIM_ADD) ok (hwnd={hwnd.ToInt64():X})");
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            Trace.WriteLine($"[TrayIcon] Shell_NotifyIcon(NIM_ADD) FAILED (lastErr={err})");
            if (_icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }
    }

    /// <summary>移除托盘图标（NIM_DELETE）+ 释放 HICON。App.OnExit 调用，防残留。</summary>
    public void Remove()
    {
        if (!_registered) return;
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
        };
        var ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        Trace.WriteLine(ok
            ? "[TrayIcon] Shell_NotifyIcon(NIM_DELETE) ok"
            : $"[TrayIcon] Shell_NotifyIcon(NIM_DELETE) FAILED (lastErr={Marshal.GetLastWin32Error()})");
        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _registered = false;
    }

    /// <summary>
    /// 托盘回调分发（由 App 的 WndProc 转发，UI 线程）。
    /// 返回 true 表示消息已处理（handled）。lParam = 托盘区鼠标消息。
    /// </summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != CallbackMessage) return false;
        switch (lParam.ToInt32())
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                Trace.WriteLine("[TrayIcon] callback: left double-click -> show main window");
                ShowMainWindowRequested?.Invoke();
                return true;
            case NativeMethods.WM_RBUTTONUP:
                ShowContextMenu();
                return true;
            default:
                return true; // 其他托盘消息（移动/单击等）消费掉，不向系统传递
        }
    }

    /// <summary>右键弹菜单：TrackPopupMenu 同步阻塞直到菜单关闭，返回选中命令 ID 后分发。</summary>
    private void ShowContextMenu()
    {
        if (_menuUp) return; // 重入保护
        _menuUp = true;
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return;

            var hMenu = NativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, new IntPtr(CmdShowMainWindow), "显示主窗口");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, new IntPtr(CmdToggleDesktop), "显示/隐藏桌面歌词");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, new IntPtr(CmdExit), "退出");

            // TPM_RETURNCMD：菜单关闭后返回所选命令 ID（0 = 未选/取消）；TPM_NONOTIFY 抑制 WM_COMMAND 通知
            var cmd = NativeMethods.TrackPopupMenu(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero).ToInt32();
            NativeMethods.DestroyMenu(hMenu);

            switch (cmd)
            {
                case CmdShowMainWindow:
                    Trace.WriteLine("[TrayIcon] menu: show main window");
                    ShowMainWindowRequested?.Invoke();
                    break;
                case CmdToggleDesktop:
                    Trace.WriteLine("[TrayIcon] menu: toggle desktop lyrics");
                    ToggleDesktopRequested?.Invoke();
                    break;
                case CmdExit:
                    Trace.WriteLine("[TrayIcon] menu: exit");
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            _menuUp = false;
        }
    }

    /// <summary>
    /// 从内嵌 base64 的 32x32 32bpp(BGRA) ICO 字节创建 HICON：
    /// 解析 ICONDIR 取首个图像块（BITMAPINFOHEADER + XOR + AND），CreateIconFromResourceEx 内存加载。
    /// 图标为单色白色音符（符头 + 符干 + 符尾），透明背景；纯 P/Invoke，不依赖 System.Drawing。
    /// </summary>
    private static IntPtr CreateTrayIcon()
    {
        try
        {
            var bytes = Convert.FromBase64String(IconBase64);
            // ICONDIR: reserved(2) type(2) count(2)；ICONDIRENTRY 16 字节：bWidth bHeight bColorCount bReserved
            //          wPlanes(2) wBitCount(2) dwBytesInRes(4) dwImageOffset(4)
            if (bytes.Length < 22) return IntPtr.Zero;
            var count = BitConverter.ToUInt16(bytes, 4);
            if (count == 0) return IntPtr.Zero;
            var entry = 6; // 单图像标准布局：entry 紧随 ICONDIR
            var size = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            if (offset < 0 || size <= 0 || offset + size > bytes.Length) return IntPtr.Zero;

            var image = new byte[size];
            Array.Copy(bytes, offset, image, 0, size);
            return NativeMethods.CreateIconFromResourceEx(image, (uint)size, fIcon: true, 0x00030000, 0, 0, 0);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TrayIcon] icon create failed: {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    public void Dispose() => Remove();

    // 32x32 32bpp(BGRA) ICO，白色音符（符头圆 r5.5@(12,23) + 符干 x15-18,y7-23 + 符尾圆 r4.5@(21,8)）。
    // 生成方式：手写 ICONDIR + BITMAPINFOHEADER(biHeight=64) + bottom-up BGRA 像素 + 全 0 AND mask，
    // 避免 GDI+ GetHicon 的 alpha 丢失坑；如需重新生成，照此格式重建字节并替换本常量即可。
    private const string IconBase64 =
        "AAABAAEAICAAAAEAIACoEAAAFgAAACgAAAAgAAAAQAAAAAEAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA//////////////////////////8AAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAP////////////////////////////////////8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////" +
        "//////////////////////////8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////////////////////////////////////////////////////////" +
        "//8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAD//////////////////////////////////////////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP//////////////" +
        "/////////////////////////////////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////////////////////////////////////////////" +
        "////////////////////AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAD///////////////////////////////////////////////////////////////8AAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAD//////////////////////////////////////////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////" +
        "////////////////////////////////AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////////////////////////////" +
        "//8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAD/////////////////////AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP//////" +
        "//////////////8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////////////////////wAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAP///////////////////////////////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////////////////////////" +
        "/////////////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////////////////////////////////////" +
        "/////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAP//////////////////////////////////////////////////////////AAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////////////" +
        "//////////////////////////////////////////////8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////////////////////////" +
        "/////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////////////////////////////////////////////////AAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAA/////////////////////////////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////////" +
        "//////////////////8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAA=";
}
