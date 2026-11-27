using System.Runtime.InteropServices;

namespace DiskSpace;

internal static class NativeMethods
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
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
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int LVM_SETICONSPACING = 0x1000 + 53;

    public static void SetIconSpacing(IntPtr listView, int cx, int cy) =>
        SendMessage(listView, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF)));

    // ---- File size on disk -----------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    /// <summary>
    /// Bytes actually allocated on disk for a sparse (or compressed) file, or -1 on failure.
    /// </summary>
    public static long GetAllocatedFileSize(string path)
    {
        // Long-path prefix so paths over MAX_PATH also work.
        string p = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        uint low = GetCompressedFileSizeW(p, out uint high);
        if (low == 0xFFFFFFFF && Marshal.GetLastPInvokeError() != 0)
            return -1;
        return ((long)high << 32) | low;
    }

    // ---- Recycle Bin ----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] // natural alignment (24 bytes on x64)
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;

    /// <summary>Total size and item count of the Recycle Bin on all drives.</summary>
    public static (long Size, long Count) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        Marshal.ThrowExceptionForHR(hr);
        return (info.i64Size, info.i64NumItems);
    }

    /// <summary>Empties the Recycle Bin on all drives (caller has already confirmed). Shows the shell's progress UI.</summary>
    public static void EmptyRecycleBin(IntPtr owner)
    {
        int hr = SHEmptyRecycleBin(owner, null, SHERB_NOCONFIRMATION);
        // E_UNEXPECTED is returned when the bin is already empty.
        if (hr < 0 && hr != unchecked((int)0x8000FFFF))
            Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>Returns the shell's large icon and display name (e.g. "Local Disk (C:)") for a path.</summary>
    public static (Icon? Icon, string? DisplayName) GetShellInfo(string path)
    {
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_DISPLAYNAME);
        if (result == IntPtr.Zero)
            return (null, null);

        Icon? icon = null;
        if (info.hIcon != IntPtr.Zero)
        {
            using (var tmp = Icon.FromHandle(info.hIcon))
                icon = (Icon)tmp.Clone();
            DestroyIcon(info.hIcon);
        }
        return (icon, info.szDisplayName);
    }
}
