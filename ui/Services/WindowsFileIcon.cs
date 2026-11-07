using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IDMLite.Services;

internal static class WindowsFileIcon
{
    private static readonly ConcurrentDictionary<string, ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string fileName, string? localFile)
    {
        try
        {
            // Use the shell's association for downloads that are not yet on disk.
            // Resolve embedded icons only for finished local executables/shortcuts.
            string extension = Path.GetExtension(fileName);
            if (extension.Length is 0 or > 32) extension = ".file";
            bool embedded = localFile is not null && Path.IsPathFullyQualified(localFile) &&
                !localFile.StartsWith(@"\\", StringComparison.Ordinal) &&
                (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(localFile);
            string key = embedded ? localFile! : extension;
            if (Icons.TryGetValue(key, out var icon)) return icon;
            icon = Read(embedded ? localFile! : "file" + extension, !embedded);
            if (icon is not null && Icons.Count < 256) Icons.TryAdd(key, icon);
            return icon;
        }
        catch { return null; }
    }

    private static ImageSource? Read(string path, bool associationOnly)
    {
        const uint icon = 0x100, smallIcon = 1, useFileAttributes = 0x10, normalFile = 0x80;
        if (SHGetFileInfo(path, normalFile, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(),
                icon | smallIcon | (associationOnly ? useFileAttributes : 0)) == IntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
