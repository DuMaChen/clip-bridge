using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        public const string APP_VERSION = "1.1.0";
        public const string PROCESSED_TYPE = "com.antigravity.clipbridge.processed";
        public const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string REG_VALUE_NAME = "ClipBridge";
        private const string MUTEX_NAME = @"Local\ClipBridge_Daemon_Mutex_2026";
        private const string GUI_MUTEX_NAME = @"Local\ClipBridge_Gui_Mutex_2026";

        // Window message used by external processes (Settings UI) to tell the daemon
        // to hot-reload config.ini without restarting.
        public const int MSG_RELOAD = 0x0400 + 0x5CB; // WM_USER + 1483
        public const string MSG_WND_TITLE = "ClipBridgeMessageWnd_3F2A";

        private static Mutex s_singleInstanceMutex = null;
        private static NotifyIcon s_trayIcon = null;

        /// <summary>Live configuration for the daemon; replaced on hot-reload.</summary>
        public static AppConfig Config = null;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern int GetFileType(IntPtr hFile);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        private const int SW_HIDE = 0;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [STAThread]
        static int Main(string[] args)
        {
            // WinExe has no console of its own; when launched from cmd/PowerShell,
            // borrow the parent console so CLI output stays visible.
            TryAttachParentConsole();

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                try
                {
                    File.AppendAllText(GetLogFilePath(),
                        string.Format("[{0}] Unhandled Exception: {1}\r\n",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), e.ExceptionObject),
                        Encoding.UTF8);
                }
                catch { }
            };

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            switch (command)
            {
                case "":
                case "gui":
                case "settings":
                    return LaunchInteractive();

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

                case "--version":
                case "-v":
                case "version":
                    Console.WriteLine("ClipBridge {0} (Windows)", APP_VERSION);
                    return 0;

                case "--help":
                case "help":
                case "/?":
                    PrintHelp();
                    return 0;

                default:
                    Console.WriteLine("Unknown command '{0}'. Run with --help for usage.", command);
                    return 1;
            }
        }

        private static void TryAttachParentConsole()
        {
            try
            {
                // Never hijack output when stdout is already redirected (pipe/file):
                // attaching would rebind Console.Out to the parent console and lose it.
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    IntPtr hOut = GetStdHandle(-11 /*STD_OUTPUT_HANDLE*/);
                    if (hOut != IntPtr.Zero && hOut != (IntPtr)(-1))
                    {
                        int fileType = GetFileType(hOut);
                        if (fileType == 1 /*DISK*/ || fileType == 3 /*PIPE*/)
                        {
                            return;
                        }
                    }
                }

                if (AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    StreamWriter outWriter = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8);
                    outWriter.AutoFlush = true;
                    Console.SetOut(outWriter);
                    StreamWriter errWriter = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8);
                    errWriter.AutoFlush = true;
                    Console.SetError(errWriter);
                }
            }
            catch { }
        }

        #region Path & Storage Helpers

        // System directory: config / logs / pid always live here so they stay findable
        // even when the user relocates the screenshot storage dir in settings.
        public static string GetSystemDirectory()
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(userHome, ".agy_screenshots");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static string GetDefaultStorageDirectory()
        {
            return GetSystemDirectory();
        }

        public static string GetLogDirectory()
        {
            string dir = Path.Combine(GetSystemDirectory(), "logs");
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
            return Path.Combine(GetSystemDirectory(), "clipbridge.pid");
        }

        public static string GetConfigPath()
        {
            return Path.Combine(GetSystemDirectory(), "config.ini");
        }

        public static string GetInstallDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
        }

        public static string GetInstalledExePath()
        {
            return Path.Combine(GetInstallDirectory(), APP_NAME + ".exe");
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
                File.AppendAllText(GetLogFilePath(), formatted + Environment.NewLine, Encoding.UTF8);
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

        public static int CleanupOldScreenshots(string directory, double maxAgeDays, int maxCount)
        {
            int deletedCount = 0;
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

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
            public event EventHandler ConfigReloadRequested;

            public ClipboardListenerWindow()
            {
                CreateParams cp = new CreateParams();
                // Stable caption so external processes can FindWindow() us for hot-reload
                cp.Caption = MSG_WND_TITLE;
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
                else if (m.Msg == MSG_RELOAD)
                {
                    if (ConfigReloadRequested != null)
                    {
                        try
                        {
                            ConfigReloadRequested(this, EventArgs.Empty);
                        }
                        catch { }
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
                Log("Another instance of ClipBridge is already running. Exiting.");
                return 0;
            }

            // Write PID file
            try
            {
                File.WriteAllText(GetPidFilePath(), Process.GetCurrentProcess().Id.ToString());
            }
            catch { }

            Config = AppConfig.Load();
            string storageDir = Config.GetEffectiveStorageDir();
            Log(string.Format("ClipBridge v{0} started. Listening for screenshots & clipboard images (Event-Driven)...", APP_VERSION));
            Log(string.Format("Screenshots storage: {0}", storageDir));

            // Initial cleanup & trim
            CleanupOldScreenshots(storageDir, Config.MaxAgeDays, Config.MaxCount);
            TrimMemory();

            try
            {
                using (ClipboardListenerWindow listener = new ClipboardListenerWindow())
                {
                    listener.ClipboardUpdated += delegate
                    {
                        OnClipboardUpdated();
                    };
                    listener.ConfigReloadRequested += delegate
                    {
                        OnExternalConfigReload();
                    };

                    using (s_trayIcon = CreateTrayIcon())
                    {
                        Application.Run();
                        s_trayIcon.Visible = false;
                    }
                    s_trayIcon = null;
                }
            }
            catch (Exception ex)
            {
                Log("FATAL exception in watcher: " + ex.ToString());
            }
            finally
            {
                try
                {
                    if (File.Exists(GetPidFilePath())) File.Delete(GetPidFilePath());
                }
                catch { }
                if (s_singleInstanceMutex != null)
                {
                    try { s_singleInstanceMutex.ReleaseMutex(); } catch { }
                }
            }

            return 0;
        }

        private static void OnExternalConfigReload()
        {
            Config = AppConfig.Load();
            Log(string.Format("Config reloaded from settings UI: enabled={0}, dir={1}", Config.Enabled, Config.GetEffectiveStorageDir()));
            if (s_trayIcon != null)
            {
                try { s_trayIcon.ShowBalloonTip(1500, "ClipBridge", "Settings applied.", ToolTipIcon.Info); } catch { }
            }
        }

        private static void OnClipboardUpdated()
        {
            // Give writing application a moment to finish populating clipboard
            ExecuteWithClipboardRetry(delegate
            {
                AppConfig cfg = Config;
                if (cfg == null || !cfg.Enabled)
                {
                    return;
                }
                string storageDir = cfg.GetEffectiveStorageDir();

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

                // 4. Save to storage dir
                string filename = string.Format("screenshot_{0}.png", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                string filePath = Path.Combine(storageDir, filename);

                File.WriteAllBytes(filePath, pngBytes);
                Log(string.Format("Image captured: {0}", filePath));

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
                    if (cfg.AddRtf)
                    {
                        string rtf = BuildRtfWithEmbeddedPng(pngBytes, image);
                        if (rtf != null)
                        {
                            data.SetData(DataFormats.Rtf, false, new MemoryStream(Encoding.ASCII.GetBytes(rtf)));
                        }
                    }

                    // Plain text file path for Terminal, PowerShell, CMD, VS Code, Anti Gravity CLI
                    data.SetData(DataFormats.UnicodeText, filePath);
                    data.SetData(DataFormats.Text, filePath);

                    // File drop format for Windows File Explorer
                    if (cfg.AddFileDrop)
                    {
                        data.SetData(DataFormats.FileDrop, new string[] { filePath });
                    }

                    // Marker to prevent self-loop
                    data.SetData(PROCESSED_TYPE, "1");

                    SetClipboardWithRetry(data);
                }

                // 6. Housekeeping & Memory optimization
                CleanupOldScreenshots(storageDir, cfg.MaxAgeDays, cfg.MaxCount);
                TrimMemory();
            });
        }

        private static void EnrichPasteboardWithExistingFile(string filePath)
        {
            try
            {
                AppConfig cfg = Config;
                if (cfg == null || !cfg.Enabled || !File.Exists(filePath)) return;

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

                    if (cfg.AddRtf)
                    {
                        string rtf = BuildRtfWithEmbeddedPng(pngBytes, image);
                        if (rtf != null)
                        {
                            data.SetData(DataFormats.Rtf, false, new MemoryStream(Encoding.ASCII.GetBytes(rtf)));
                        }
                    }

                    data.SetData(DataFormats.UnicodeText, filePath);
                    data.SetData(DataFormats.Text, filePath);
                    if (cfg.AddFileDrop)
                    {
                        data.SetData(DataFormats.FileDrop, new string[] { filePath });
                    }
                    data.SetData(PROCESSED_TYPE, "1");

                    SetClipboardWithRetry(data);
                    Log(string.Format("File path enriched: {0}", filePath));
                }

                TrimMemory();
            }
            catch (Exception ex)
            {
                Log(string.Format("Error enriching file path: {0}", ex.Message));
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

        #region Tray Icon & Settings Window

        private static Icon CreateAppIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(47, 92, 216)))
                    {
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    }
                    using (Font f = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush w = new SolidBrush(Color.White))
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("CB", f, w, new RectangleF(0, 0, 32, 32), sf);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static NotifyIcon CreateTrayIcon()
        {
            NotifyIcon tray = new NotifyIcon();
            tray.Icon = CreateAppIcon();
            tray.Text = "ClipBridge v" + APP_VERSION;
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem miSettings = new ToolStripMenuItem("Settings...");
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
            miSettings.Click += delegate { ShowSettingsForm(); };
            menu.Items.Add(miSettings);

            ToolStripMenuItem miPause = new ToolStripMenuItem("Pause listening");
            miPause.Click += delegate
            {
                AppConfig cfg = Config != null ? Config : AppConfig.Load();
                cfg.Enabled = !cfg.Enabled;
                cfg.Save();
                Config = cfg;
                Log("Listening " + (cfg.Enabled ? "resumed" : "paused") + " from tray menu.");
                tray.ShowBalloonTip(1200, "ClipBridge",
                    cfg.Enabled ? "Listening resumed." : "Listening paused.",
                    ToolTipIcon.Info);
            };
            menu.Items.Add(miPause);

            ToolStripMenuItem miClean = new ToolStripMenuItem("Clean up now");
            miClean.Click += delegate
            {
                AppConfig cfg = Config != null ? Config : AppConfig.Load();
                int deleted = CleanupOldScreenshots(cfg.GetEffectiveStorageDir(), cfg.MaxAgeDays, cfg.MaxCount);
                tray.ShowBalloonTip(1500, "ClipBridge",
                    deleted > 0 ? string.Format("Cleaned {0} screenshot(s).", deleted) : "Nothing to clean.",
                    ToolTipIcon.Info);
            };
            menu.Items.Add(miClean);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate
            {
                Application.ExitThread();
            };
            menu.Items.Add(miExit);

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowSettingsForm(); };
            return tray;
        }

        private static SettingsForm s_openSettings = null;

        /// <summary>Opens the settings window from the daemon process (hot-apply, no restart).</summary>
        private static void ShowSettingsForm()
        {
            if (s_openSettings != null && !s_openSettings.IsDisposed)
            {
                s_openSettings.Activate();
                return;
            }
            s_openSettings = new SettingsForm(true);
            s_openSettings.Show();
        }

        #endregion

        #region Interactive GUI (installer & settings)

        private static int LaunchInteractive()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            Mutex guiMutex = new Mutex(true, GUI_MUTEX_NAME, out createdNew);

            bool installed = IsInstalled();
            if (!installed)
            {
                using (InstallForm form = new InstallForm())
                {
                    Application.Run(form);
                }
            }
            else
            {
                using (SettingsForm form = new SettingsForm(false))
                {
                    Application.Run(form);
                }
            }

            GC.KeepAlive(guiMutex);
            return 0;
        }

        public static bool IsInstalled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, false))
                {
                    if (key != null && key.GetValue(REG_VALUE_NAME) != null) return true;
                }
            }
            catch { }
            return File.Exists(GetInstalledExePath());
        }

        /// <summary>Installs: copy binary, PATH, autostart registry, launch daemon. Returns error text or null.</summary>
        public static string PerformInstall(bool configureAutoStart, bool startDaemon)
        {
            try
            {
                string targetExe = GetInstalledExePath();
                string currentExe = GetExecutablePath();
                bool selfCopy = !string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase);

                KillAllDaemons();

                if (selfCopy)
                {
                    string dir = Path.GetDirectoryName(targetExe);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.Copy(currentExe, targetExe, true);
                }

                EnsureInstallDirOnPath();
                SetAutoStartRegistry(configureAutoStart);

                if (startDaemon)
                {
                    // UseShellExecute=true: ShellExecute does NOT inherit our stdio handles,
                    // so callers piping our output (scripts / CI) are not kept alive by the daemon.
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = targetExe;
                    psi.Arguments = "run --hidden";
                    psi.WorkingDirectory = Path.GetDirectoryName(targetExe);
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    Process.Start(psi);
                    Thread.Sleep(400);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static void SetAutoStartRegistry(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(REG_RUN_KEY, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            key.SetValue(REG_VALUE_NAME, string.Format("\"{0}\" run --hidden", GetInstalledExePath()));
                        }
                        else if (key.GetValue(REG_VALUE_NAME) != null)
                        {
                            key.DeleteValue(REG_VALUE_NAME, false);
                        }
                    }
                }
            }
            catch { }
        }

        public static void EnsureInstallDirOnPath()
        {
            try
            {
                string installDir = GetInstallDirectory();
                using (RegistryKey env = Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (env == null) return;
                    string path = env.GetValue("Path", "") as string;
                    if (string.IsNullOrEmpty(path))
                    {
                        env.SetValue("Path", installDir, RegistryValueKind.ExpandString);
                    }
                    else
                    {
                        string[] parts = path.Split(';');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (string.Equals(parts[i].Trim().TrimEnd('\\'), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            {
                                return; // already on PATH
                            }
                        }
                        env.SetValue("Path", path.TrimEnd(';') + ";" + installDir, RegistryValueKind.ExpandString);
                    }
                }
                UIntPtr result;
                SendMessageTimeout((IntPtr)0xFFFF /*HWND_BROADCAST*/, WM_SETTINGCHANGE, UIntPtr.Zero,
                    "Environment", SMTO_ABORTIFHUNG, 1000, out result);
            }
            catch { }
        }

        /// <summary>Tells the running daemon (if any) to hot-reload config.ini.</summary>
        public static void NotifyDaemonReload()
        {
            try
            {
                IntPtr h = FindWindow(null, MSG_WND_TITLE);
                if (h != IntPtr.Zero)
                {
                    SendMessage(h, MSG_RELOAD, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        }

        public static void KillAllDaemons()
        {
            int currentPid = Process.GetCurrentProcess().Id;

            string pidFile = GetPidFilePath();
            if (File.Exists(pidFile))
            {
                try
                {
                    int pid;
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out pid) && pid != currentPid)
                    {
                        try { Process.GetProcessById(pid).Kill(); } catch { }
                    }
                    File.Delete(pidFile);
                }
                catch { }
            }

            Process[] procs = Process.GetProcessesByName(APP_NAME);
            for (int i = 0; i < procs.Length; i++)
            {
                if (procs[i].Id != currentPid)
                {
                    try { procs[i].Kill(); } catch { }
                }
            }
        }

        #endregion

        #region CLI Service Management

        private static int HandleStart()
        {
            if (!IsInstalled())
            {
                Console.WriteLine("Not installed yet. Run 'clipbridge' (no arguments) for the one-click installer, or re-install to {0}.", GetInstalledExePath());
            }

            SetAutoStartRegistry(true);
            Console.WriteLine("Configured auto-start in Windows Registry (HKCU Run).");

            Process runningProc = FindRunningDaemon();
            if (runningProc != null)
            {
                Console.WriteLine("ClipBridge is already running (PID: {0}).", runningProc.Id);
                return 0;
            }

            try
            {
                // UseShellExecute=true avoids stdio handle inheritance into the daemon
                // (keeps piped callers from waiting on the daemon forever).
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = GetExecutablePath();
                psi.Arguments = "run --hidden";
                psi.WorkingDirectory = Path.GetDirectoryName(GetExecutablePath());
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    Thread.Sleep(300);
                    proc.Refresh();
                    if (!proc.HasExited)
                    {
                        Console.WriteLine("Background service started successfully! (PID: {0})", proc.Id);
                        Console.WriteLine("It will automatically start on login and run silently in the background.");
                        return 0;
                    }
                    else
                    {
                        Console.WriteLine("Process started but exited immediately with code {0}.", proc.ExitCode);
                        return 1;
                    }
                }
                else
                {
                    Console.WriteLine("Failed to start background process.");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error launching background service: {0}", ex.Message);
                return 1;
            }
        }

        private static int HandleStop()
        {
            SetAutoStartRegistry(false);
            Console.WriteLine("Removed auto-start entry from Windows Registry.");

            int currentPid = Process.GetCurrentProcess().Id;
            int stoppedCount = 0;

            string pidFile = GetPidFilePath();
            if (File.Exists(pidFile))
            {
                try
                {
                    int pid;
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out pid) && pid != currentPid)
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
                Console.WriteLine("ClipBridge background service stopped ({0} process terminated).", stoppedCount);
            }
            else
            {
                Console.WriteLine("ClipBridge background service is not currently running.");
            }

            return 0;
        }

        private static int HandleStatus()
        {
            AppConfig cfg = Config != null ? Config : AppConfig.Load();

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

            string storageDir = cfg.GetEffectiveStorageDir();
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

            Console.WriteLine("=== ClipBridge v{0} Status (Windows) ===", APP_VERSION);
            Console.WriteLine("• Service Running:   {0}", isRunning ? string.Format("Running (PID: {0}, Memory: ~{1} MB)", runningProc.Id, memoryMb) : "Stopped");
            Console.WriteLine("• Listening:         {0}", cfg.Enabled ? "Enabled" : "Paused");
            Console.WriteLine("• Auto-Start:        {0}", isAutoStart ? "Enabled (HKCU Run)" : "Disabled");
            Console.WriteLine("• Screenshots Dir:   {0} ({1} files, {2:F2} MB)", storageDir, fileCount, totalMb);
            Console.WriteLine("• Retention:         {0} days / max {1} images (Office RTF: {2})", cfg.MaxAgeDays, cfg.MaxCount, cfg.AddRtf ? "on" : "off");
            Console.WriteLine("• Binary Location:   {0}", GetExecutablePath());
            Console.WriteLine("• Config File:       {0}", GetConfigPath());
            Console.WriteLine("• Log Location:      {0}", GetLogFilePath());

            return 0;
        }

        private static int HandleClean()
        {
            AppConfig cfg = Config != null ? Config : AppConfig.Load();
            string storageDir = cfg.GetEffectiveStorageDir();
            Console.WriteLine("Cleaning up screenshots in {0}...", storageDir);
            int deleted = CleanupOldScreenshots(storageDir, cfg.MaxAgeDays, cfg.MaxCount);
            Console.WriteLine("Cleaned up {0} old or excess screenshot(s).", deleted);
            return 0;
        }

        public static Process FindRunningDaemon()
        {
            int currentPid = Process.GetCurrentProcess().Id;

            string pidFile = GetPidFilePath();
            if (File.Exists(pidFile))
            {
                try
                {
                    int pid;
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out pid) && pid != currentPid)
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
            Console.WriteLine(@"ClipBridge (Windows) v" + APP_VERSION + @" - Screenshot & Clipboard Image to File Path Bridge

Bridge Windows screenshot clipboard directly to file paths in Terminal/CLI
without losing image pasting in chat apps.

Usage:
  clipbridge            # Open graphical installer / settings (double-click friendly)
  clipbridge start      # Register auto-start and launch background daemon
  clipbridge stop       # Stop background daemon and remove auto-start
  clipbridge status     # Check current running status and statistics
  clipbridge run        # Run in foreground with tray icon (for debugging)
  clipbridge clean      # Clean up old screenshots manually
  clipbridge --version  # Show version
  clipbridge --help     # Show this help message

Features:
  - Multi-type Clipboard: Terminal pastes file path; WeChat/Feishu/Slack paste image;
    Word/Excel/PowerPoint paste embedded image via RTF.
  - 0% Idle CPU: Uses Win32 AddClipboardFormatListener (event-driven, no polling).
  - Ultra Lightweight: Native C# (.NET Framework), resident memory ~2-8 MB.
  - Tray icon with settings UI; config at ~/.agy_screenshots/config.ini.
  - Auto Cleanup: Retains last N days (max M images), configurable in settings.
");
        }

        #endregion
    }

    #region AppConfig

    /// <summary>
    /// User-editable settings persisted as a simple INI file at ~/.agy_screenshots/config.ini.
    /// Kept dependency-free (hand-rolled parser) so the binary stays a single zero-dependency exe.
    /// </summary>
    public class AppConfig
    {
        public string StorageDir = "";      // empty = default (~/.agy_screenshots)
        public int MaxAgeDays = 7;
        public int MaxCount = 200;
        public bool AutoStart = true;
        public bool Enabled = true;
        public bool AddRtf = true;
        public bool AddFileDrop = true;

        public string GetEffectiveStorageDir()
        {
            string dir = string.IsNullOrEmpty(StorageDir) ? Program.GetDefaultStorageDirectory() : StorageDir;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            return dir;
        }

        public static AppConfig Load()
        {
            AppConfig c = new AppConfig();
            try
            {
                string path = Program.GetConfigPath();
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        int n;
                        switch (k)
                        {
                            case "storage_dir": c.StorageDir = v; break;
                            case "max_age_days":
                                if (int.TryParse(v, out n)) c.MaxAgeDays = n;
                                break;
                            case "max_count":
                                if (int.TryParse(v, out n)) c.MaxCount = n;
                                break;
                            case "auto_start": c.AutoStart = v != "0"; break;
                            case "enabled": c.Enabled = v != "0"; break;
                            case "add_rtf": c.AddRtf = v != "0"; break;
                            case "add_filedrop": c.AddFileDrop = v != "0"; break;
                        }
                    }
                }
            }
            catch { }

            // clamp to sane ranges
            if (c.MaxAgeDays < 1) c.MaxAgeDays = 1;
            if (c.MaxAgeDays > 3650) c.MaxAgeDays = 3650;
            if (c.MaxCount < 10) c.MaxCount = 10;
            if (c.MaxCount > 100000) c.MaxCount = 100000;
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; ClipBridge configuration - edited by Settings UI or by hand");
                sb.AppendLine("; Changes are hot-reloaded by the running daemon.");
                sb.AppendLine("[settings]");
                sb.AppendLine("storage_dir=" + StorageDir);
                sb.AppendLine("max_age_days=" + MaxAgeDays.ToString());
                sb.AppendLine("max_count=" + MaxCount.ToString());
                sb.AppendLine("auto_start=" + (AutoStart ? "1" : "0"));
                sb.AppendLine("enabled=" + (Enabled ? "1" : "0"));
                sb.AppendLine("add_rtf=" + (AddRtf ? "1" : "0"));
                sb.AppendLine("add_filedrop=" + (AddFileDrop ? "1" : "0"));
                File.WriteAllText(Program.GetConfigPath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    #endregion

    #region Settings Form

    public class SettingsForm : Form
    {
        private readonly bool _insideDaemon;

        private Label _lblRun;
        private Label _lblStats;
        private Label _lblDir;
        private CheckBox _chkEnabled;
        private CheckBox _chkAutoStart;
        private CheckBox _chkRtf;
        private CheckBox _chkFileDrop;
        private TextBox _txtDir;
        private Button _btnBrowse;
        private NumericUpDown _numAge;
        private NumericUpDown _numCount;
        private Button _btnSave;
        private Button _btnClean;
        private Button _btnUninstall;

        public SettingsForm(bool insideDaemon)
        {
            _insideDaemon = insideDaemon;
            BuildUi();
            LoadSettings();
            RefreshStatus();
        }

        private void BuildUi()
        {
            Text = "ClipBridge 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(424, 452);
            Font = new Font("Segoe UI", 9F);

            GroupBox grpStatus = new GroupBox();
            grpStatus.Text = "运行状态";
            grpStatus.SetBounds(12, 12, 400, 92);

            _lblRun = new Label();
            _lblRun.SetBounds(14, 22, 370, 20);
            _lblRun.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            grpStatus.Controls.Add(_lblRun);

            _lblStats = new Label();
            _lblStats.SetBounds(14, 44, 370, 18);
            grpStatus.Controls.Add(_lblStats);

            _lblDir = new Label();
            _lblDir.SetBounds(14, 64, 370, 18);
            _lblDir.AutoEllipsis = true;
            grpStatus.Controls.Add(_lblDir);

            GroupBox grpSet = new GroupBox();
            grpSet.Text = "设置";
            grpSet.SetBounds(12, 112, 400, 264);

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "启用剪贴板监听（取消后暂停截图捕获）";
            _chkEnabled.SetBounds(14, 26, 370, 22);
            grpSet.Controls.Add(_chkEnabled);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动（当前用户注册表）";
            _chkAutoStart.SetBounds(14, 52, 370, 22);
            grpSet.Controls.Add(_chkAutoStart);

            _chkRtf = new CheckBox();
            _chkRtf.Text = "Word / Excel 粘贴为图片（RTF 内嵌）";
            _chkRtf.SetBounds(14, 78, 370, 22);
            grpSet.Controls.Add(_chkRtf);

            _chkFileDrop = new CheckBox();
            _chkFileDrop.Text = "允许在文件管理器中粘贴为文件副本";
            _chkFileDrop.SetBounds(14, 104, 370, 22);
            grpSet.Controls.Add(_chkFileDrop);

            Label lblDir = new Label();
            lblDir.Text = "截图存储目录：";
            lblDir.SetBounds(14, 136, 120, 17);
            grpSet.Controls.Add(lblDir);

            _txtDir = new TextBox();
            _txtDir.SetBounds(14, 156, 292, 23);
            grpSet.Controls.Add(_txtDir);

            _btnBrowse = new Button();
            _btnBrowse.Text = "浏览...";
            _btnBrowse.SetBounds(312, 154, 74, 25);
            _btnBrowse.Click += delegate { OnBrowse(); };
            grpSet.Controls.Add(_btnBrowse);

            Label lblAge = new Label();
            lblAge.Text = "保留天数：";
            lblAge.SetBounds(14, 196, 70, 17);
            grpSet.Controls.Add(lblAge);

            _numAge = new NumericUpDown();
            _numAge.Minimum = 1;
            _numAge.Maximum = 3650;
            _numAge.SetBounds(86, 192, 70, 23);
            grpSet.Controls.Add(_numAge);

            Label lblCount = new Label();
            lblCount.Text = "最多保留（张）：";
            lblCount.SetBounds(180, 196, 100, 17);
            grpSet.Controls.Add(lblCount);

            _numCount = new NumericUpDown();
            _numCount.Minimum = 10;
            _numCount.Maximum = 100000;
            _numCount.SetBounds(284, 192, 80, 23);
            grpSet.Controls.Add(_numCount);

            _btnSave = new Button();
            _btnSave.Text = "保存并应用";
            _btnSave.SetBounds(12, 386, 120, 32);
            _btnSave.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _btnSave.Click += delegate { OnSave(); };

            _btnClean = new Button();
            _btnClean.Text = "立即清理";
            _btnClean.SetBounds(140, 386, 100, 32);
            _btnClean.Click += delegate { OnClean(); };

            _btnUninstall = new Button();
            _btnUninstall.Text = "卸载";
            _btnUninstall.SetBounds(248, 386, 76, 32);
            _btnUninstall.Click += delegate { OnUninstall(); };

            Label lblVer = new Label();
            lblVer.Text = "v" + Program.APP_VERSION;
            lblVer.ForeColor = Color.DimGray;
            lblVer.SetBounds(330, 394, 80, 18);
            lblVer.TextAlign = ContentAlignment.MiddleRight;

            Label lblHint = new Label();
            lblHint.Text = "保存后立即生效，无需重启。托盘图标右键可快捷暂停/清理。";
            lblHint.ForeColor = Color.DimGray;
            lblHint.SetBounds(12, 424, 400, 17);

            Controls.Add(grpStatus);
            Controls.Add(grpSet);
            Controls.Add(_btnSave);
            Controls.Add(_btnClean);
            Controls.Add(_btnUninstall);
            Controls.Add(lblVer);
            Controls.Add(lblHint);

            FormClosed += delegate
            {
                if (_insideDaemon && Program.Config != null)
                {
                    // nothing to do; daemon keeps its live config
                }
            };
        }

        private void LoadSettings()
        {
            AppConfig cfg = _insideDaemon && Program.Config != null ? Program.Config : AppConfig.Load();
            _chkEnabled.Checked = cfg.Enabled;
            _chkAutoStart.Checked = cfg.AutoStart;
            _chkRtf.Checked = cfg.AddRtf;
            _chkFileDrop.Checked = cfg.AddFileDrop;
            _txtDir.Text = cfg.StorageDir;
            _numAge.Value = Math.Min(Math.Max(cfg.MaxAgeDays, (int)_numAge.Minimum), (int)_numAge.Maximum);
            _numCount.Value = Math.Min(Math.Max(cfg.MaxCount, (int)_numCount.Minimum), (int)_numCount.Maximum);
        }

        private AppConfig CollectFromUi()
        {
            AppConfig cfg = new AppConfig();
            cfg.Enabled = _chkEnabled.Checked;
            cfg.AutoStart = _chkAutoStart.Checked;
            cfg.AddRtf = _chkRtf.Checked;
            cfg.AddFileDrop = _chkFileDrop.Checked;
            cfg.StorageDir = _txtDir.Text.Trim();
            cfg.MaxAgeDays = (int)_numAge.Value;
            cfg.MaxCount = (int)_numCount.Value;
            return cfg;
        }

        private void OnBrowse()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择截图存储目录";
                dlg.ShowNewFolderButton = true;
                string cur = _txtDir.Text.Trim();
                if (cur.Length > 0 && Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtDir.Text = dlg.SelectedPath;
                }
            }
        }

        private void OnSave()
        {
            AppConfig cfg = CollectFromUi();
            try
            {
                if (cfg.StorageDir.Length > 0 && !Directory.Exists(cfg.StorageDir))
                {
                    Directory.CreateDirectory(cfg.StorageDir);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法创建所选目录：" + ex.Message, "ClipBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            cfg.Save();

            if (_insideDaemon)
            {
                Program.Config = cfg; // hot-apply in this (daemon) process
            }
            else
            {
                Program.SetAutoStartRegistry(cfg.AutoStart);
                Program.NotifyDaemonReload(); // daemon picks up new config.ini live
            }

            Log("Settings saved: enabled={0}, rtf={1}, filedrop={2}, dir={3}, age={4}, count={5}",
                cfg.Enabled, cfg.AddRtf, cfg.AddFileDrop, cfg.GetEffectiveStorageDir(), cfg.MaxAgeDays, cfg.MaxCount);

            RefreshStatus();
            MessageBox.Show(this, "设置已保存并立即生效。", "ClipBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Log(string fmt, params object[] args)
        {
            try { Program.Log(string.Format(fmt, args)); } catch { }
        }

        private void OnClean()
        {
            AppConfig cfg = CollectFromUi();
            cfg.Save();
            if (_insideDaemon)
            {
                Program.Config = cfg;
            }
            else
            {
                Program.NotifyDaemonReload();
            }
            int deleted = Program.CleanupOldScreenshots(cfg.GetEffectiveStorageDir(), cfg.MaxAgeDays, cfg.MaxCount);
            RefreshStatus();
            MessageBox.Show(this, deleted > 0
                ? string.Format("已清理 {0} 张过期或超出数量限制的截图。", deleted)
                : "当前没有需要清理的截图。", "ClipBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnUninstall()
        {
            DialogResult r = MessageBox.Show(this,
                "确定要卸载 ClipBridge 吗？\n\n将停止后台服务并取消开机自启（已保存的截图文件不受影响）。",
                "卸载 ClipBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            Program.SetAutoStartRegistry(false);
            Program.KillAllDaemons();
            Program.NotifyDaemonReload();

            // Best-effort binary removal (fails if this very exe is the installed one and is running)
            try
            {
                string installed = Program.GetInstalledExePath();
                if (File.Exists(installed) &&
                    !string.Equals(Program.GetExecutablePath(), installed, StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Thread.Sleep(300);
                    File.Delete(installed);
                }
            }
            catch { }

            MessageBox.Show(this, "ClipBridge 已卸载。", "ClipBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }

        private void RefreshStatus()
        {
            Process p = Program.FindRunningDaemon();
            if (p != null)
            {
                _lblRun.Text = string.Format("● 后台运行中（PID {0}）", p.Id);
                _lblRun.ForeColor = Color.FromArgb(0, 150, 70);
            }
            else
            {
                _lblRun.Text = "○ 未运行";
                _lblRun.ForeColor = Color.Firebrick;
            }

            AppConfig cfg = CollectFromUi();
            string dir = cfg.GetEffectiveStorageDir();
            int count = 0;
            double mb = 0;
            try
            {
                if (Directory.Exists(dir))
                {
                    FileInfo[] files = new DirectoryInfo(dir).GetFiles("screenshot_*.png");
                    count = files.Length;
                    long bytes = 0;
                    for (int i = 0; i < files.Length; i++) bytes += files[i].Length;
                    mb = bytes / (1024.0 * 1024.0);
                }
            }
            catch { }
            _lblStats.Text = string.Format("已保存 {0} 张截图 · 占用 {1:F1} MB", count, mb);
            _lblDir.Text = "存储目录：" + dir;
        }
    }

    #endregion

    #region Install Form

    public class InstallForm : Form
    {
        private Label _lblState;
        private Button _btnInstall;
        private Button _btnSettings;
        private bool _installed;

        public InstallForm()
        {
            BuildUi();
            RefreshState();
        }

        private void BuildUi()
        {
            Text = "ClipBridge 安装";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 318);
            Font = new Font("Segoe UI", 9F);

            Label lblTitle = new Label();
            lblTitle.Text = "🌉 ClipBridge";
            lblTitle.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
            lblTitle.SetBounds(16, 14, 380, 34);
            Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.Text = "v" + Program.APP_VERSION + " · 截图剪贴板桥：终端贴路径，聊天贴图片";
            lblSub.ForeColor = Color.DimGray;
            lblSub.SetBounds(16, 50, 390, 18);
            Controls.Add(lblSub);

            Label lblDesc = new Label();
            lblDesc.Text = "自动捕获截图（Win+Shift+S / 微信 / Snipaste 等）并保存为 PNG：\n\n" +
                           "  •  终端 / PowerShell / VS Code 中粘贴  →  图片文件路径\n" +
                           "  •  微信 / 飞书 / Slack 中粘贴  →  图片本身\n" +
                           "  •  Word / Excel 中粘贴  →  图片本身（内嵌）\n\n" +
                           "截图自动保存、自动清理，零依赖，空闲 CPU 0%。";
            lblDesc.SetBounds(16, 78, 390, 110);
            Controls.Add(lblDesc);

            _lblState = new Label();
            _lblState.SetBounds(16, 196, 390, 18);
            _lblState.ForeColor = Color.DimGray;
            Controls.Add(_lblState);

            _btnInstall = new Button();
            _btnInstall.Text = "🚀 一键安装";
            _btnInstall.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _btnInstall.SetBounds(16, 222, 186, 38);
            _btnInstall.Click += delegate { OnInstall(); };
            Controls.Add(_btnInstall);

            _btnSettings = new Button();
            _btnSettings.Text = "⚙ 打开设置";
            _btnSettings.SetBounds(212, 222, 190, 38);
            _btnSettings.Click += delegate
            {
                SettingsForm f = new SettingsForm(false);
                Hide();
                f.ShowDialog(this);
                RefreshState();
                Show();
            };
            Controls.Add(_btnSettings);

            LinkLabel link = new LinkLabel();
            link.Text = "GitHub: DuMaChen/clip-bridge";
            link.SetBounds(16, 272, 200, 17);
            link.LinkClicked += delegate
            {
                try { Process.Start("https://github.com/DuMaChen/clip-bridge"); } catch { }
            };
            Controls.Add(link);

            Label lblFoot = new Label();
            lblFoot.Text = "安装到 %USERPROFILE%\\.local\\bin · 开机自启可在设置中关闭";
            lblFoot.ForeColor = Color.DimGray;
            lblFoot.SetBounds(16, 294, 390, 17);
            Controls.Add(lblFoot);
        }

        private void RefreshState()
        {
            _installed = Program.IsInstalled();
            bool running = Program.FindRunningDaemon() != null;
            _lblState.Text = _installed
                ? (running ? "状态：已安装，后台运行中 ✓" : "状态：已安装，后台未运行")
                : "状态：未安装";
            _btnInstall.Text = _installed ? "🔄 重新安装 / 更新" : "🚀 一键安装";
            _btnSettings.Enabled = _installed;
        }

        private void OnInstall()
        {
            _btnInstall.Enabled = false;
            _lblState.Text = "正在安装，请稍候...";
            Application.DoEvents();

            string err = Program.PerformInstall(true, true);
            if (err == null)
            {
                Program.Log("Installed via GUI one-click installer.");
                _lblState.Text = "状态：已安装，后台运行中 ✓";
                MessageBox.Show(this,
                    "安装完成！ClipBridge 正在后台运行。\n\n" +
                    "  •  截图后在终端按 Ctrl+V 即可粘贴图片路径\n" +
                    "  •  托盘图标双击可打开设置，右键可暂停/清理/退出\n" +
                    "  •  新开的终端窗口可直接使用 clipbridge 命令",
                    "ClipBridge 安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "安装失败：" + err, "ClipBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            _btnInstall.Enabled = true;
            RefreshState();
        }
    }

    #endregion
}
