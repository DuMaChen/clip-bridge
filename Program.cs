using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClipBridge
{
    static class Program
    {
        public const string APP_NAME = "clipbridge";
        public const string PROCESSED_TYPE = "com.antigravity.clipbridge.processed";
        public const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string REG_VALUE_NAME = "ClipBridge";
        private const string MUTEX_NAME = @"Local\ClipBridge_Daemon_Mutex_2026";

        private static Mutex s_singleInstanceMutex = null;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private const int SW_HIDE = 0;

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                try
                {
                    string errPath = Path.Combine(GetLogDirectory(), "clipbridge.err");
                    File.AppendAllText(errPath, string.Format("[{0}] Unhandled Exception: {1}\r\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), e.ExceptionObject), Encoding.UTF8);
                }
                catch { }
            };

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";

            switch (command)
            {
                case "start":
                case "install":
                    return HandleStart();

                case "stop":
                case "uninstall":
                    return HandleStop();

                case "status":
                    return HandleStatus();

                case "clean":
                case "cleanup":
                    return HandleClean();

                case "run":
                    bool hidden = false;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (args[i].Equals("--hidden", StringComparison.OrdinalIgnoreCase) ||
                            args[i].Equals("-h", StringComparison.OrdinalIgnoreCase))
                        {
                            hidden = true;
                            break;
                        }
                    }
                    return RunWatcher(hidden);

                case "--help":
                case "-h":
                case "help":
                case "/?":
                    PrintHelp();
                    return 0;

                default:
                    Console.WriteLine("Unknown command '{0}'. Run with --help for usage.", command);
                    return 1;
            }
        }

        #region Path & Storage Helpers

        public static string GetStorageDirectory()
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(userHome, ".agy_screenshots");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static string GetLogDirectory()
        {
            string dir = Path.Combine(GetStorageDirectory(), "logs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static string GetLogFilePath()
        {
            return Path.Combine(GetLogDirectory(), "clipbridge.log");
        }

        public static string GetPidFilePath()
        {
            return Path.Combine(GetStorageDirectory(), "clipbridge.pid");
        }

        public static string GetExecutablePath()
        {
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        #endregion

        #region Logging

        public static bool IsConsoleAvailable = true;

        public static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string formatted = string.Format("[{0}] [{1}] {2}", timestamp, APP_NAME, message);

            if (IsConsoleAvailable)
            {
                try
                {
                    Console.WriteLine(formatted);
                }
                catch { }
            }

            try
            {
                string logPath = GetLogFilePath();
                File.AppendAllText(logPath, formatted + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        #endregion

        #region Memory Optimization

        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch { }
        }

        #endregion

        #region Cleanup

        public static int CleanupOldScreenshots(string directory, double maxAgeDays = 7.0, int maxCount = 200)
        {
            int deletedCount = 0;
            try
            {
                if (!Directory.Exists(directory)) return 0;

                DirectoryInfo di = new DirectoryInfo(directory);
                FileInfo[] files = di.GetFiles("screenshot_*.png");
                DateTime now = DateTime.Now;
                List<FileInfo> validFiles = new List<FileInfo>();

                for (int i = 0; i < files.Length; i++)
                {
                    FileInfo fi = files[i];
                    double ageInDays = (now - fi.CreationTime).TotalDays;
                    if (ageInDays > maxAgeDays)
                    {
                        try
                        {
                            fi.Delete();
                            deletedCount++;
                        }
                        catch { }
                    }
                    else
                    {
                        validFiles.Add(fi);
                    }
                }

                if (validFiles.Count > maxCount)
                {
                    validFiles.Sort(delegate(FileInfo a, FileInfo b)
                    {
                        return a.CreationTime.CompareTo(b.CreationTime);
                    });

                    int excess = validFiles.Count - maxCount;
                    for (int i = 0; i < excess; i++)
                    {
                        try
                        {
                            validFiles[i].Delete();
                            deletedCount++;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return deletedCount;
        }

        #endregion

        #region Watcher Implementation

        private sealed class ClipboardListenerWindow : NativeWindow, IDisposable
        {
            private const int WM_CLIPBOARDUPDATE = 0x031D;
            public event EventHandler ClipboardUpdated;

            public ClipboardListenerWindow()
            {
                CreateParams cp = new CreateParams();
                CreateHandle(cp);
                AddClipboardFormatListener(this.Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_CLIPBOARDUPDATE)
                {
                    if (ClipboardUpdated != null)
                    {
                        try
                        {
                            ClipboardUpdated(this, EventArgs.Empty);
                        }
                        catch (Exception ex)
                        {
                            Log("Error processing clipboard event: " + ex.Message);
                        }
                    }
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                RemoveClipboardFormatListener(this.Handle);
                DestroyHandle();
            }
        }

        private static int RunWatcher(bool hidden)
        {
            if (hidden)
            {
                IsConsoleAvailable = false;
                IntPtr consoleWnd = GetConsoleWindow();
                if (consoleWnd != IntPtr.Zero)
                {
                    ShowWindow(consoleWnd, SW_HIDE);
                }
            }

            bool createdNew;
            s_singleInstanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                Log("⚠️ Another instance of ClipBridge is already running. Exiting.");
                return 0;
            }

            // Write PID file
            try
            {
                File.WriteAllText(GetPidFilePath(), Process.GetCurrentProcess().Id.ToString());
            }
            catch { }

            string storageDir = GetStorageDirectory();
            Log(string.Format("🚀 ClipBridge started. Listening for screenshots & clipboard images (Event-Driven)..."));
            Log(string.Format("📁 Screenshots storage: {0}", storageDir));

            // Initial cleanup & trim
            CleanupOldScreenshots(storageDir);
            TrimMemory();

            try
            {
                using (ClipboardListenerWindow listener = new ClipboardListenerWindow())
                {
                    listener.ClipboardUpdated += delegate
                    {
                        OnClipboardUpdated(storageDir);
                    };

                    Application.Run();
                }
            }
            catch (Exception ex)
            {
                Log("FATAL exception in watcher: " + ex.ToString());
            }
            finally
            {
                // Cleanup PID file on exit
                try
                {
                    if (File.Exists(GetPidFilePath())) File.Delete(GetPidFilePath());
                }
                catch { }
            }

            return 0;
        }

        private static void OnClipboardUpdated(string storageDir)
        {
            // Give writing application a moment to finish populating clipboard
            ExecuteWithClipboardRetry(delegate
            {
                // 1. If clipboard contains our processed marker, skip to avoid feedback loop
                if (Clipboard.ContainsData(PROCESSED_TYPE))
                {
                    return;
                }

                // 2. Check if clipboard contains file drop (e.g. copied an image from Explorer)
                if (Clipboard.ContainsFileDropList())
                {
                    StringCollection files = Clipboard.GetFileDropList();
                    if (files != null && files.Count > 0)
                    {
                        string firstFile = files[0];
                        if (File.Exists(firstFile))
                        {
                            string ext = Path.GetExtension(firstFile).ToLowerInvariant();
                            // Only formats GDI+ can actually decode; webp/heic would throw in Image.FromFile
                            string[] imageExts = new string[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".ico" };
                            bool isImage = false;
                            for (int i = 0; i < imageExts.Length; i++)
                            {
                                if (imageExts[i] == ext)
                                {
                                    isImage = true;
                                    break;
                                }
                            }

                            if (isImage)
                            {
                                string currentText = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                                if (currentText != firstFile)
                                {
                                    EnrichPasteboardWithExistingFile(firstFile);
                                }
                                return;
                            }
                        }
                    }
                }

                // 3. Check if clipboard contains an image (Screenshot or copied image)
                bool hasImage = Clipboard.ContainsImage() || Clipboard.ContainsData("PNG") || Clipboard.ContainsData(DataFormats.Bitmap);
                if (!hasImage)
                {
                    return;
                }

                byte[] pngBytes = null;
                Image image = null;

                // Check for high-fidelity PNG stream (e.g. modern browser, Snipaste, etc.)
                if (Clipboard.ContainsData("PNG"))
                {
                    object rawPng = Clipboard.GetData("PNG");
                    byte[] rawBytes = rawPng is MemoryStream ? ((MemoryStream)rawPng).ToArray() : rawPng as byte[];
                    if (rawBytes != null)
                    {
                        pngBytes = rawBytes;
                        try
                        {
                            // GDI+ requires the stream to stay open for the lifetime of the Image,
                            // so hand over ownership instead of disposing it here.
                            image = Image.FromStream(new MemoryStream(rawBytes));
                        }
                        catch { image = null; }
                    }
                }

                // If no raw PNG stream, extract via GDI Bitmap
                if (image == null && Clipboard.ContainsImage())
                {
                    image = Clipboard.GetImage();
                    if (image != null)
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            image.Save(ms, ImageFormat.Png);
                            pngBytes = ms.ToArray();
                        }
                    }
                }

                if (image == null || pngBytes == null)
                {
                    return;
                }

                // 4. Save to .agy_screenshots
                string filename = string.Format("screenshot_{0}.png", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                string filePath = Path.Combine(storageDir, filename);

                File.WriteAllBytes(filePath, pngBytes);
                Log(string.Format("📸 Image captured: {0}", filePath));

                // 5. Re-populate multi-type clipboard
                using (image)
                using (MemoryStream ms = new MemoryStream(pngBytes))
                {
                    DataObject data = new DataObject();
                    // Rich image formats for WeChat, Feishu, Slack, Discord, Browsers
                    data.SetData("PNG", false, ms);
                    data.SetData(DataFormats.Bitmap, image);

                    // Embedded RTF image so Office apps (Word/Excel/PowerPoint/WPS) paste the
                    // actual image instead of the file path text (they rank plain text above bitmaps)
                    string rtf = BuildRtfWithEmbeddedPng(pngBytes, image);
                    if (rtf != null)
                    {
                        data.SetData(DataFormats.Rtf, false, new MemoryStream(Encoding.ASCII.GetBytes(rtf)));
                    }

                    // Plain text file path for Terminal, PowerShell, CMD, VS Code, Anti Gravity CLI
                    data.SetData(DataFormats.UnicodeText, filePath);
                    data.SetData(DataFormats.Text, filePath);

                    // File drop format for Windows File Explorer
                    data.SetData(DataFormats.FileDrop, new string[] { filePath });

                    // Marker to prevent self-loop
                    data.SetData(PROCESSED_TYPE, "1");

                    SetClipboardWithRetry(data);
                }

                // 6. Housekeeping & Memory optimization
                CleanupOldScreenshots(storageDir);
                TrimMemory();
            });
        }

        private static void EnrichPasteboardWithExistingFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                byte[] pngBytes = null;
                Image image = null;
                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                if (ext == ".png")
                {
                    pngBytes = File.ReadAllBytes(filePath);
                    // Stream ownership is handed to the Image (GDI+ needs it alive)
                    image = Image.FromStream(new MemoryStream(pngBytes));
                }
                else
                {
                    using (Image orig = Image.FromFile(filePath))
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            orig.Save(ms, ImageFormat.Png);
                            pngBytes = ms.ToArray();
                        }
                        image = new Bitmap(orig);
                    }
                }

                using (image)
                using (MemoryStream ms = new MemoryStream(pngBytes))
                {
                    DataObject data = new DataObject();
                    data.SetData("PNG", false, ms);
                    data.SetData(DataFormats.Bitmap, image);

                    string rtf = BuildRtfWithEmbeddedPng(pngBytes, image);
                    if (rtf != null)
                    {
                        data.SetData(DataFormats.Rtf, false, new MemoryStream(Encoding.ASCII.GetBytes(rtf)));
                    }

                    data.SetData(DataFormats.UnicodeText, filePath);
                    data.SetData(DataFormats.Text, filePath);
                    data.SetData(DataFormats.FileDrop, new string[] { filePath });
                    data.SetData(PROCESSED_TYPE, "1");

                    SetClipboardWithRetry(data);
                    Log(string.Format("📎 File path enriched: {0}", filePath));
                }

                TrimMemory();
            }
            catch (Exception ex)
            {
                Log(string.Format("❌ Error enriching file path: {0}", ex.Message));
            }
        }

        /// <summary>
        /// Builds an RTF document with the given PNG embedded as a \pngblip picture.
        /// Office apps (Word/Excel/PowerPoint/WPS) prefer RTF over plain text, so they will
        /// paste the embedded image instead of the file path string carried in CF_TEXT.
        /// The image data is embedded (not linked), so later cleanup of the screenshot
        /// file does not affect documents already pasted into.
        /// </summary>
        private static string BuildRtfWithEmbeddedPng(byte[] pngBytes, Image image)
        {
            if (pngBytes == null || pngBytes.Length == 0 || image == null)
            {
                return null;
            }

            try
            {
                double dpiX = image.HorizontalResolution > 0 ? image.HorizontalResolution : 96.0;
                double dpiY = image.VerticalResolution > 0 ? image.VerticalResolution : 96.0;
                // RTF display size is in twips (1440 twips = 1 inch)
                int wGoal = Math.Max(1, (int)Math.Round(image.Width * 1440.0 / dpiX));
                int hGoal = Math.Max(1, (int)Math.Round(image.Height * 1440.0 / dpiY));

                StringBuilder sb = new StringBuilder(pngBytes.Length * 2 + 256);
                sb.Append("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 Segoe UI;}}\r\n");
                sb.Append("{\\pict\\pngblip");
                sb.AppendFormat("\\picw{0}\\pich{1}\\picwgoal{2}\\pichgoal{3}\r\n",
                    image.Width, image.Height, wGoal, hGoal);
                // Hex payload, wrapped every 32 bytes (some RTF parsers choke on very long lines)
                for (int i = 0; i < pngBytes.Length; i++)
                {
                    sb.Append(pngBytes[i].ToString("X2"));
                    if ((i & 31) == 31)
                    {
                        sb.Append("\r\n");
                    }
                }
                sb.Append("}}\r\n");
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void ExecuteWithClipboardRetry(Action action, int maxRetries = 8, int delayMs = 25)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    action();
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    Log("Clipboard action error: " + ex.Message);
                    return;
                }
            }
        }

        private static void SetClipboardWithRetry(DataObject data, int maxRetries = 8, int delayMs = 25)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(data, true);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    Log("Error setting clipboard: " + ex.Message);
                    return;
                }
            }
        }

        #endregion

        #region CLI Service Management

        private static int HandleStart()
        {
            string exePath = GetExecutablePath();

            // 1. Configure auto-start via Registry HKCU Run
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
                {
                    if (key != null)
                    {
                        string runCmd = string.Format("\"{0}\" run --hidden", exePath);
                        key.SetValue(REG_VALUE_NAME, runCmd);
                        Console.WriteLine("✅ Configured auto-start in Windows Registry (HKCU Run).");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("⚠️ Warning: Could not register auto-start: {0}", ex.Message);
            }

            // 2. Check if already running
            Process runningProc = FindRunningDaemon();
            if (runningProc != null)
            {
                Console.WriteLine("ℹ️ ClipBridge is already running (PID: {0}).", runningProc.Id);
                return 0;
            }

            // 3. Launch background process detached
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = "run --hidden";
                psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    Thread.Sleep(300);
                    proc.Refresh();
                    if (!proc.HasExited)
                    {
                        Console.WriteLine("✅ Background service started successfully! (PID: {0})", proc.Id);
                        Console.WriteLine("   It will automatically start on login and run silently in the background.");
                        return 0;
                    }
                    else
                    {
                        Console.WriteLine("❌ Process started but exited immediately with code {0}.", proc.ExitCode);
                        return 1;
                    }
                }
                else
                {
                    Console.WriteLine("❌ Failed to start background process.");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Error launching background service: {0}", ex.Message);
                return 1;
            }
        }

        private static int HandleStop()
        {
            // 1. Remove Registry Run key
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
                {
                    if (key != null && key.GetValue(REG_VALUE_NAME) != null)
                    {
                        key.DeleteValue(REG_VALUE_NAME, false);
                        Console.WriteLine("✅ Removed auto-start entry from Windows Registry.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("⚠️ Error removing auto-start key: {0}", ex.Message);
            }

            // 2. Terminate running daemon processes
            int currentPid = Process.GetCurrentProcess().Id;
            int stoppedCount = 0;

            // Check PID file first
            string pidFile = GetPidFilePath();
            if (File.Exists(pidFile))
            {
                try
                {
                    string pidStr = File.ReadAllText(pidFile).Trim();
                    int pid;
                    if (int.TryParse(pidStr, out pid) && pid != currentPid)
                    {
                        try
                        {
                            Process p = Process.GetProcessById(pid);
                            if (p != null && !p.HasExited)
                            {
                                p.Kill();
                                stoppedCount++;
                            }
                        }
                        catch { }
                    }
                    File.Delete(pidFile);
                }
                catch { }
            }

            // Also check all processes by name
            Process[] procs = Process.GetProcessesByName(APP_NAME);
            for (int i = 0; i < procs.Length; i++)
            {
                Process p = procs[i];
                if (p.Id != currentPid)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill();
                            stoppedCount++;
                        }
                    }
                    catch { }
                }
            }

            if (stoppedCount > 0)
            {
                Console.WriteLine("🛑 ClipBridge background service stopped ({0} process terminated).", stoppedCount);
            }
            else
            {
                Console.WriteLine("ℹ️ ClipBridge background service is not currently running.");
            }

            return 0;
        }

        private static int HandleStatus()
        {
            // 1. Check Auto-start
            bool isAutoStart = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, false))
                {
                    if (key != null && key.GetValue(REG_VALUE_NAME) != null)
                    {
                        isAutoStart = true;
                    }
                }
            }
            catch { }

            // 2. Check running daemon
            Process runningProc = FindRunningDaemon();
            bool isRunning = runningProc != null;
            long memoryMb = 0;
            if (isRunning)
            {
                try
                {
                    runningProc.Refresh();
                    memoryMb = runningProc.WorkingSet64 / (1024 * 1024);
                }
                catch { }
            }

            // 3. Screenshots statistics
            string storageDir = GetStorageDirectory();
            int fileCount = 0;
            long totalBytes = 0;
            if (Directory.Exists(storageDir))
            {
                DirectoryInfo di = new DirectoryInfo(storageDir);
                FileInfo[] files = di.GetFiles("screenshot_*.png");
                fileCount = files.Length;
                for (int i = 0; i < files.Length; i++)
                {
                    totalBytes += files[i].Length;
                }
            }
            double totalMb = totalBytes / (1024.0 * 1024.0);

            Console.WriteLine("=== ClipBridge Status (Windows) ===");
            Console.WriteLine("• Auto-Start:        {0}", isAutoStart ? "🟢 Enabled (HKCU Run)" : "🔴 Disabled");
            Console.WriteLine("• Service Running:   {0}", isRunning ? string.Format("🟢 Running (PID: {0}, Memory: ~{1} MB)", runningProc.Id, memoryMb) : "🔴 Stopped");
            Console.WriteLine("• Screenshots Dir:   {0} ({1} files, {2:F2} MB)", storageDir, fileCount, totalMb);
            Console.WriteLine("• Binary Location:   {0}", GetExecutablePath());
            Console.WriteLine("• Log Location:      {0}", GetLogFilePath());

            return 0;
        }

        private static int HandleClean()
        {
            string storageDir = GetStorageDirectory();
            Console.WriteLine("🧹 Cleaning up screenshots in {0}...", storageDir);
            int deleted = CleanupOldScreenshots(storageDir, maxAgeDays: 7.0, maxCount: 200);
            Console.WriteLine("✅ Cleaned up {0} old or excess screenshot(s).", deleted);
            return 0;
        }

        private static Process FindRunningDaemon()
        {
            int currentPid = Process.GetCurrentProcess().Id;

            // Check PID file first
            string pidFile = GetPidFilePath();
            if (File.Exists(pidFile))
            {
                try
                {
                    string pidStr = File.ReadAllText(pidFile).Trim();
                    int pid;
                    if (int.TryParse(pidStr, out pid) && pid != currentPid)
                    {
                        Process p = Process.GetProcessById(pid);
                        if (p != null && !p.HasExited)
                        {
                            return p;
                        }
                    }
                }
                catch { }
            }

            // Check by process name
            Process[] procs = Process.GetProcessesByName(APP_NAME);
            for (int i = 0; i < procs.Length; i++)
            {
                if (procs[i].Id != currentPid)
                {
                    try
                    {
                        if (!procs[i].HasExited)
                        {
                            return procs[i];
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"ClipBridge (Windows) - Screenshot & Clipboard Image to File Path Bridge

Bridge Windows screenshot clipboard directly to file paths in Terminal/CLI
without losing image pasting in chat apps.

Usage:
  clipbridge start    # Register auto-start and launch background daemon
  clipbridge stop     # Stop background daemon and remove auto-start
  clipbridge status   # Check current running status and statistics
  clipbridge run      # Run in foreground console (with live logs)
  clipbridge clean    # Clean up screenshots older than 7 days manually
  clipbridge --help   # Show this help message

Features:
  - Multi-type Clipboard: Terminal pastes file path; WeChat/Feishu/Slack paste image!
  - 0% Idle CPU: Uses Win32 AddClipboardFormatListener (event-driven, no polling).
  - Ultra Lightweight: Native C# (.NET Framework), resident memory ~2-4 MB.
  - Auto Cleanup: Retains last 7 days (max 200 images), prevents disk bloat.
");
        }

        #endregion
    }
}
