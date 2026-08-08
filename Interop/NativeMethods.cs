using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Stacks.Interop;

/// <summary>
/// Win32 P/Invoke declarations for desktop window manipulation.
/// </summary>
public static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const int HWND_BOTTOM = 1;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOACTIVATE = 0x0010;
    public const int SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    public const uint SMTO_NORMAL = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPIF_UPDATEINIFILE = 0x0001;
    public const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegSetValueExW(IntPtr hKey, string? lpValueName, uint Reserved, uint dwType, byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(IntPtr hKey);

    public static readonly IntPtr HKEY_CURRENT_USER = new(-2147483647);
    public const uint KEY_SET_VALUE = 0x0002;
    public const uint REG_DWORD = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public static void SetWindowBottom(IntPtr hwnd)
    {
        SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void ApplyNoActivate(IntPtr hwnd)
    {
        var style = GetWindowLongW(hwnd, GWL_EXSTYLE);
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongW(hwnd, GWL_EXSTYLE, style);
    }

    /// <summary>
    /// Find the WorkerW/Progman window that contains the desktop icon layer.
    /// </summary>
    public static IntPtr FindDesktopWindow()
    {
        // Trigger WorkerW creation via undocumented message to Progman
        var progman = FindWindowW("Progman", null);
        if (progman != IntPtr.Zero)
        {
            SendMessageTimeoutW(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        }

        IntPtr defWorker = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            var defView = FindWindowExW(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                defWorker = hWnd;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);

        return defWorker != IntPtr.Zero ? defWorker : progman;
    }

    /// <summary>
    /// Hide or show desktop icons. Uses direct WorkerW window visibility
    /// (ShowWindow) rather than registry — far more reliable on Windows 11
    /// where the registry/refresh approach often fails or takes seconds.
    /// </summary>
    public static void SetDesktopIconsVisible(bool visible)
    {
        try
        {
            var worker = FindDesktopWindow();
            if (worker != IntPtr.Zero)
                ShowWindow(worker, visible ? SW_SHOW : SW_HIDE);
        }
        catch { }
    }

    // ======== High-Resolution Icon Extraction ========

    public const int SHGFI_ICON = 0x100;
    public const int SHGFI_LARGEICON = 0x0;
    public const int SHGFI_SMALLICON = 0x1;
    public const int SHGFI_SYSICONINDEX = 0x4000;
    public const int SHGFI_USEFILEATTRIBUTES = 0x10;
    public const int SHIL_JUMBO = 0x4;      // 256x256
    public const int SHIL_EXTRALARGE = 0x2; // 48x48
    public const int SHIL_LARGE = 0x0;      // 32x32
    public const int FILE_ATTRIBUTE_NORMAL = 0x80;
    public const int ILD_TRANSPARENT = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll")]
    public static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ======== Windows Acrylic / Mica backdrop ========

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Win11: tell DWM to paint the system acrylic/mica backdrop behind the window.</summary>
    public const int DWMWA_SYSTEMBACKDROP_PREFERRED = 38;

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENTPOLICY
    {
        public int nAccentState;
        public int nFlags;
        public int nColor;
        public int nAnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINCOMPATTRDATA
    {
        public int nAttribute;
        public IntPtr pData;
        public int ulSize;
    }

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA pAttrData);

    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;

    /// <summary>
    /// Enable a native Windows acrylic (frosted-glass) backdrop on the overlay window.
    /// Tries the Win11 DWM system backdrop first; falls back to the Win10 blur-behind
    /// accent if that API is unavailable. Best-effort — failures are silently ignored.
    /// </summary>
    public static void EnableAcrylic(IntPtr hwnd)
    {
        // Win11: native acrylic/mica backdrop
        try
        {
            int prefer = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_PREFERRED, ref prefer, sizeof(int)) == 0)
                return;
        }
        catch { }

        // Win10 fallback: blur whatever is behind the window
        try
        {
            var policy = new ACCENTPOLICY
            {
                nAccentState = ACCENT_ENABLE_BLURBEHIND,
                nFlags = 0,
                nColor = 0,
                nAnimationId = 0
            };
            var data = new WINCOMPATTRDATA
            {
                nAttribute = WCA_ACCENT_POLICY,
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(policy)),
                ulSize = Marshal.SizeOf(policy)
            };
            Marshal.StructureToPtr(policy, data.pData, false);
            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(data.pData);
        }
        catch { }
    }

    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>
    /// Extract a high-resolution icon (up to 256x256) for any file or extension.
    /// Uses SHGetFileInfo + SHGetImageList(SHIL_JUMBO) for maximum quality.
    /// </summary>
    public static System.Drawing.Icon? ExtractHighResIcon(string path, int size = 256)
    {
        try
        {
            // Try to get jumbo (256x256) image list
            IntPtr imageList = IntPtr.Zero;
            var iid = IID_IImageList; // local copy so it can be passed as ref
            int hr = SHGetImageList(SHIL_JUMBO, ref iid, out imageList);

            // If jumbo not available (pre-Win8), fall back to extra large (48)
            if (hr != 0 || imageList == IntPtr.Zero)
            {
                iid = IID_IImageList;
                hr = SHGetImageList(SHIL_EXTRALARGE, ref iid, out imageList);
            }

            // If still not available, fall back to classic icon extraction
            if (hr != 0 || imageList == IntPtr.Zero)
            {
                var shinfo = new SHFILEINFOW();
                SHGetFileInfoW(path, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFOW>(),
                    SHGFI_ICON | SHGFI_LARGEICON);
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    var icon = (Icon)Icon.FromHandle(shinfo.hIcon).Clone();
                    DestroyIcon(shinfo.hIcon);
                    if (icon.Size.Width >= size) return icon;
                    return new Icon(icon, size, size);
                }
                return null;
            }

            // Get icon index from shell.
            // CRITICAL: pass FILE_ATTRIBUTE_DIRECTORY for directories so Windows
            // returns the folder icon index, not a generic file icon. Without this,
            // folders render as blank/default file icons in the JUMBO image list.
            uint fileAttrs = File.Exists(path) ? FILE_ATTRIBUTE_NORMAL : 0x10u; // FILE_ATTRIBUTE_DIRECTORY
            var shinfo2 = new SHFILEINFOW();
            SHGetFileInfoW(path, fileAttrs, ref shinfo2,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);

            if (shinfo2.iIcon >= 0)
            {
                IntPtr hIcon = ImageList_GetIcon(imageList, shinfo2.iIcon, ILD_TRANSPARENT);
                if (hIcon != IntPtr.Zero)
                {
                    var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                    DestroyIcon(hIcon);
                    return icon;
                }
            }

            // Final fallback: use direct file icon
            var shinfo3 = new SHFILEINFOW();
            SHGetFileInfoW(path, 0, ref shinfo3, (uint)Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_ICON | SHGFI_LARGEICON);
            if (shinfo3.hIcon != IntPtr.Zero)
            {
                var icon = (Icon)Icon.FromHandle(shinfo3.hIcon).Clone();
                DestroyIcon(shinfo3.hIcon);
                return icon;
            }
        }
        catch { }

        return null;
    }
}
