using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudisService
{
    /// <summary>
    /// Loads Windows shell icons.
    ///
    /// USAGE in Window_Loaded — call FromDll with the indices you picked
    /// using the IconPicker window (add IconPicker to your project, run it
    /// once, browse shell32/imageres, click an icon → index is copied to clipboard).
    ///
    /// Alternatively use SHGetFileInfo to get the icon Windows associates
    /// with a real file extension — 100% semantic and version-proof.
    /// </summary>
    public static class WindowsIconHelper
    {
        // ── ExtractIconEx ─────────────────────────────────────────────────────
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(
            string lpszFile, int nIconIndex,
            IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, int nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // ── SHGetFileInfo — most reliable semantic approach ───────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }
        private const uint SHGFI_ICON              = 0x100;
        private const uint SHGFI_LARGEICON         = 0x000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x010;
        private const uint FILE_ATTRIBUTE_NORMAL   = 0x080;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwAttr,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        /// <summary>
        /// Get the icon Windows shows for a given file extension.
        /// Does NOT need the file to exist.  E.g. ".exe", ".wav", ".txt", ".log"
        /// This is 100% version-proof — Windows picks the right icon itself.
        /// </summary>
        public static ImageSource? ForExtension(string ext)
        {
            var info = new SHFILEINFO();
            var ret = SHGetFileInfo("dummy" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf(info),
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            if (ret == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try   { return ToWpf(info.hIcon); }
            catch { return null; }
            finally { DestroyIcon(info.hIcon); }
        }

        /// <summary>Extract by DLL + index.  Use IconPicker to find the right index.</summary>
        public static ImageSource? FromDll(string dllPath, int index)
        {
            var large = new IntPtr[1];
            try
            {
                if (ExtractIconEx(dllPath, index, large, null, 1) < 1 || large[0] == IntPtr.Zero)
                    return null;
                return ToWpf(large[0]);
            }
            catch { return null; }
            finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
        }

        public static ImageSource? Shell32(int index)
            => FromDll(@"C:\Windows\System32\shell32.dll", index);

        public static ImageSource? Imageres(int index)
            => FromDll(@"C:\Windows\System32\imageres.dll", index);

        private static ImageSource ToWpf(IntPtr hIcon)
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
    }
}
