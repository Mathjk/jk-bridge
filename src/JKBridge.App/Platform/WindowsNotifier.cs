using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JKBridge.App.Platform;

/// <summary>
/// Native Windows balloon notification through a private message-only window.
/// Avalonia's tray icon does not expose notifications, so JK Bridge owns a
/// short-lived NOTIFYICONDATA for each alert and removes it immediately after.
/// </summary>
internal static class WindowsNotifier
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private static readonly IntPtr MessageOnlyWindow = new(-3);

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static void ShowBalloon(string title, string text)
    {
        if (!IsSupported)
        {
            return;
        }

        var hWnd = CreateWindowEx(
            0,
            "Static",
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            MessageOnlyWindow,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (hWnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var data = new NotifyIconData
            {
                cbSize = checked((uint)Marshal.SizeOf<NotifyIconData>()),
                hWnd = hWnd,
                uID = 1,
                uFlags = NifTip | NifInfo,
                szTip = Truncate(title, 127),
                szInfo = Truncate(text, 255),
                szInfoTitle = Truncate(title, 63),
                dwInfoFlags = NiifInfo,
            };

            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!ShellNotifyIcon(NimModify, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // Balloons survive the icon removal; delete without waiting.
            ShellNotifyIcon(NimDelete, ref data);
        }
        finally
        {
            _ = DestroyWindow(hWnd);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
