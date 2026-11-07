using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace IDMLite;

public static class ShellHelper
{
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[]? apidl,
        uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr pidl);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>
    /// Highlights/flashes the background behind the taskbar icon without stealing foreground focus.
    /// </summary>
    public static void FlashTaskbarIcon(Window? window, uint count = 5)
    {
        if (window == null) return;
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;

            var fInfo = new FLASHWINFO
            {
                cbSize = Convert.ToUInt32(Marshal.SizeOf<FLASHWINFO>()),
                hwnd = helper.Handle,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = count,
                dwTimeout = 0
            };
            FlashWindowEx(ref fInfo);
        }
        catch { }
    }

    /// <summary>
    /// Opens the containing folder in Windows Explorer and selects/highlights the file if it exists.
    /// If the file does not exist (in-progress, queued, or moved), opens the folder directly.
    /// </summary>
    public static void OpenFolderAndSelect(string? filePath, string? fallbackDirectory = null)
    {
        try
        {
            string? targetDir = fallbackDirectory;
            if (string.IsNullOrWhiteSpace(targetDir) && !string.IsNullOrWhiteSpace(filePath))
            {
                try { targetDir = Path.GetDirectoryName(filePath); } catch { }
            }

            if (string.IsNullOrWhiteSpace(targetDir) || !Path.IsPathRooted(targetDir))
            {
                targetDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }

            targetDir = Path.GetFullPath(targetDir);
            if (!Directory.Exists(targetDir))
            {
                try { Directory.CreateDirectory(targetDir); } catch { }
            }

            // Case 1: File exists on disk -> highlight it in Windows Explorer
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                string cleanFile = Path.GetFullPath(filePath);
                string cleanDir = targetDir;

                // 1. Native Windows Shell API: SHOpenFolderAndSelectItems
                if (TryNativeOpenAndSelect(cleanFile, cleanDir))
                {
                    return;
                }

                // 2. Direct explorer.exe /select with UseShellExecute = false
                if (TryExplorerProcess($"/select,\"{cleanFile}\""))
                {
                    return;
                }

                // 3. Fallback: Open directory directly
                OpenDirectory(cleanDir);
                return;
            }

            // Case 2: File is still in-progress, queued, or not on disk yet -> open the directory directly
            OpenDirectory(targetDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Opens the specified directory in Windows Explorer using multiple robust fallbacks.
    /// </summary>
    public static void OpenDirectory(string directoryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                directoryPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }

            string cleanDir = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(cleanDir))
            {
                try { Directory.CreateDirectory(cleanDir); } catch { }
            }

            // 1. Native Shell API
            if (TryNativeOpenFolder(cleanDir))
            {
                return;
            }

            // 2. Direct explorer.exe with UseShellExecute = false
            if (TryExplorerProcess($"\"{cleanDir}\""))
            {
                return;
            }

            // 3. cmd.exe /c start "" "cleanDir" (Bypasses UIPI / admin elevation blocks)
            try
            {
                using var cmdProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{cleanDir}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                });
                if (cmdProc != null)
                {
                    return;
                }
            }
            catch { }

            // 4. Default ShellExecute
            Process.Start(new ProcessStartInfo
            {
                FileName = cleanDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Opens the specified file using the default system handler (e.g. Media Player, default browser, etc.).
    /// If the file does not exist yet or is inaccessible, opens its containing folder.
    /// </summary>
    public static void OpenFile(string? filePath, string? fallbackDirectory = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    // If direct launch fails (e.g. extension not associated or security block), fallback to open folder and select
                    OpenFolderAndSelect(filePath, fallbackDirectory);
                    return;
                }
            }

            // Fallback if file does not exist yet
            OpenFolderAndSelect(filePath, fallbackDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Dosya açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool TryNativeOpenAndSelect(string filePath, string dirPath)
    {
        IntPtr pidlFolder = IntPtr.Zero;
        IntPtr pidlFile = IntPtr.Zero;
        try
        {
            pidlFolder = ILCreateFromPathW(dirPath);
            pidlFile = ILCreateFromPathW(filePath);

            if (pidlFolder != IntPtr.Zero && pidlFile != IntPtr.Zero)
            {
                IntPtr[] files = new[] { pidlFile };
                int hr = SHOpenFolderAndSelectItems(pidlFolder, (uint)files.Length, files, 0);
                if (hr == 0) // S_OK
                {
                    return true;
                }
            }
        }
        catch { }
        finally
        {
            if (pidlFile != IntPtr.Zero) ILFree(pidlFile);
            if (pidlFolder != IntPtr.Zero) ILFree(pidlFolder);
        }

        return false;
    }

    private static bool TryNativeOpenFolder(string dirPath)
    {
        IntPtr pidlFolder = IntPtr.Zero;
        try
        {
            pidlFolder = ILCreateFromPathW(dirPath);
            if (pidlFolder != IntPtr.Zero)
            {
                int hr = SHOpenFolderAndSelectItems(pidlFolder, 0, null, 0);
                if (hr == 0)
                {
                    return true;
                }
            }
        }
        catch { }
        finally
        {
            if (pidlFolder != IntPtr.Zero) ILFree(pidlFolder);
        }

        return false;
    }

    private static bool TryExplorerProcess(string arguments)
    {
        try
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string explorerExe = Path.Combine(winDir, "explorer.exe");
            if (!File.Exists(explorerExe))
            {
                explorerExe = "explorer.exe";
            }

            var psi = new ProcessStartInfo
            {
                FileName = explorerExe,
                Arguments = arguments,
                UseShellExecute = false
            };

            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }
}
