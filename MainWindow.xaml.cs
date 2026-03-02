using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using MessageBox = System.Windows.MessageBox;
using Brushes   = System.Windows.Media.Brushes;

namespace AudisService
{
    public partial class MainWindow : Window
    {
        private SipEngine   _engine;
        private ObservableCollection<UiLogEntry>    _logs        = new();
        private ObservableCollection<CallViewModel> _activeCalls = new();
        private ObservableCollection<MappingItem>   _mappings    = new();
        private DispatcherTimer _timer;
        private DispatcherTimer _aiStatusTimer;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private Dictionary<string, DateTime> _hangupGraveyard = new(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<CallLogEntry> _callLog = new();

        private Process? _ollamaProcess = null;
        private bool     _isAiRunning   = false;

        private SipClientWindow?   _sipClientWindow;
        private SipClientConfig    _sipClientConfig = SipClientConfig.Load();

        // ── Web API server ────────────────────────────────────────────────────────
        // Default port 8765 — change here or expose in Server Config if needed.
        private const int        WebPort = 8765;
        private WebApiServer?    _webServer;
        private bool             _webRunning = false;

        public bool IsRecordingEnabled
        {
            get => (DataContext as MainViewModel)?.IsRecordingEnabled ?? false;
            set { if (DataContext is MainViewModel vm) vm.IsRecordingEnabled = value; }
        }

        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainViewModel { Config = AudisConfig.Load() };
            DataContext = vm;
            _activeCalls = vm.ActiveCalls;

            _engine = new SipEngine();
            _engine.OnLog              += (lvl, msg) => Dispatcher.Invoke(() => AddLog(lvl, msg));
            _engine.OnCallStatusChange += OnCallStatusChange;

            LogList.ItemsSource = _logs;
            RefreshMappings(vm.Config);
            RefreshRecordingsList();

            // Logo
            try
            {
                string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                if (!File.Exists(logoPath)) logoPath = @"C:\Scripts\AudisService\icon.png";
                if (File.Exists(logoPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(logoPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    LogoImage.Source = bmp;
                }
            }
            catch { }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                foreach (var call in _activeCalls.Where(c => c.IsActive))
                    call.UpdateDuration();
            };

            _aiStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _aiStatusTimer.Tick += async (s, e) => await CheckAiStatus();
            _aiStatusTimer.Start();

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon    = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text    = "Audis - Kybl Enterprise"
            };
            _notifyIcon.DoubleClick += (s, _) => { Show(); WindowState = WindowState.Normal; };

            Closing += (s, e) =>
            {
                // Stop web server if running
                StopWebServer();

                _notifyIcon.Visible = false;
                _engine.Stop();
                if (_sipClientWindow != null)
                {
                    _sipClientWindow.ForceClose = true;
                    _sipClientWindow.Close();
                }
                StopOllama();
                System.Windows.Application.Current.Shutdown();
            };

            AddLog(LogLevel.Information, "Audis v1.4 Initialized");
            AddLog(LogLevel.Information, $"Audio Directory: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio")}");

            _ = CheckAiStatus();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // WEB CLIENT TOGGLE
        // ══════════════════════════════════════════════════════════════════════════

        private void TbWebClient_Click(object sender, RoutedEventArgs e)
        {
            if (_webRunning)
            {
                StopWebServer();
            }
            else
            {
                StartWebServer();
                // Auto-open browser to the page
                try
                {
                    Process.Start(new ProcessStartInfo($"http://localhost:{WebPort}/")
                        { UseShellExecute = true });
                }
                catch { }
            }
        }

        private void StartWebServer()
        {
            // Make sure the SIP client window (and its engine) exist
            EnsureSipClientWindow();

            try
            {
                _webServer = new WebApiServer(
                    WebPort,
                    _sipClientWindow!.Engine,
                    () => SipClientConfig.Load(),
                    (mode, wav) =>
                    {
                        // Update engine + persist; also refresh WPF UI on the dispatcher
                        _sipClientWindow!.Engine.SetAudioMode(mode, wav);
                        _sipClientWindow.Dispatcher.Invoke(() =>
                            _sipClientWindow.LoadAudioModeFromConfig());
                    });

                _webServer.Start();
                _webRunning = true;

                // Try to show the LAN IP so the user knows what to type on other devices
                string lanIp = GetLocalIp();
                string lanUrl = lanIp != "" ? $"http://{lanIp}:{WebPort}/" : "";

                WebDot.Fill          = Brushes.LimeGreen;
                TxtWebLabel.Text     = $"Web :{WebPort}";
                BtnWebClient.ToolTip = lanUrl != ""
                    ? $"Web client running — localhost: http://localhost:{WebPort}/   LAN: {lanUrl}   — click to stop"
                    : $"Web client running at http://localhost:{WebPort}/  — click to stop";

                AddLog(LogLevel.Information, $"Web client started → http://localhost:{WebPort}/");
                if (lanUrl != "")
                    AddLog(LogLevel.Information, $"Web client LAN address → {lanUrl}");
            }
            catch (Exception ex)
            {
                AddLog(LogLevel.Error, $"Web client failed to start: {ex.Message}");
                MessageBox.Show($"Could not start web client on port {WebPort}:\n\n{ex.Message}",
                                "Web Client Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopWebServer()
        {
            if (_webServer != null)
            {
                _webServer.Stop();
                _webServer.Dispose();
                _webServer = null;
            }
            _webRunning = false;

            // Reset toolbar indicator
            Dispatcher.InvokeAsync(() =>
            {
                WebDot.Fill          = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x22, 0x22));
                TxtWebLabel.Text     = "Web Client";
                BtnWebClient.ToolTip = "Start / stop web SIP client — opens the interface in your browser";
            });

            AddLog(LogLevel.Information, "Web client stopped");
        }

        /// <summary>Returns the first non-loopback IPv4 address of this machine, or empty string.</summary>
        private static string GetLocalIp()
        {
            try
            {
                foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        // ══════════════════════════════════════════════════════════════════════════
        // TOOLBAR HANDLERS
        // ══════════════════════════════════════════════════════════════════════════

        private void TbCallLog_Click(object sender, RoutedEventArgs e)
        {
            var win = new CallLogWindow(_callLog) { Owner = this };
            win.Show();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var cfg = DataContext is MainViewModel vm ? vm.Config : new AudisConfig();

            ImgSipClient.Source    = ResolveButtonIcon(cfg, "SipClient",    ".exe");
            ImgSipSettings.Source  = ResolveButtonIcon(cfg, "SipSettings",  ".cpl");
            ImgServerConfig.Source = ResolveButtonIcon(cfg, "ServerConfig", ".msc");
            ImgCallLog.Source      = ResolveButtonIcon(cfg, "CallLog",      ".log");
            ImgHelp.Source         = ResolveButtonIcon(cfg, "Help",         ".hlp", fallbackShell32: 23);
            ImgWebClient.Source    = ResolveButtonIcon(cfg, "WebClient",    "",     fallbackDll: @"C:\Windows\System32\mmcndmgr.dll", fallbackDllIndex: 28);

            BtnRefreshFiles_Click(sender, e);
        }

        private System.Windows.Media.ImageSource? ResolveButtonIcon(
            AudisConfig cfg, string key, string defaultExt, int fallbackShell32 = -1,
            string? fallbackDll = null, int fallbackDllIndex = -1)
        {
            if (cfg.ButtonIcons.TryGetValue(key, out var entry) &&
                entry.Index >= 0 &&
                !string.IsNullOrEmpty(entry.DllPath))
            {
                var custom = WindowsIconHelper.FromDll(entry.DllPath, entry.Index);
                if (custom != null) return custom;
            }

            if (!string.IsNullOrEmpty(defaultExt))
            {
                var ext = WindowsIconHelper.ForExtension(defaultExt);
                if (ext != null) return ext;
            }

            if (fallbackShell32 >= 0)
                return WindowsIconHelper.Shell32(fallbackShell32);

            if (fallbackDll != null && fallbackDllIndex >= 0)
                return WindowsIconHelper.FromDll(fallbackDll, fallbackDllIndex);

            return null;
        }

        private void ApplyButtonIcon(string key, string dllPath, int index)
        {
            if (DataContext is not MainViewModel vm) return;

            vm.Config.ButtonIcons[key] = new ButtonIconEntry { DllPath = dllPath, Index = index };
            vm.Config.Save();

            (string defaultExt, int fallback) = key switch
            {
                "SipClient"    => (".exe", -1),
                "SipSettings"  => (".cpl", -1),
                "ServerConfig" => (".msc", -1),
                "CallLog"      => (".log", -1),
                "Help"         => (".hlp", 23),
                _              => (".exe", -1)
            };

            var src = key == "WebClient"
                ? ResolveButtonIcon(vm.Config, key, "", fallbackDll: @"C:\Windows\System32\mmcndmgr.dll", fallbackDllIndex: 28)
                : ResolveButtonIcon(vm.Config, key, defaultExt, fallback);

            System.Windows.Controls.Image? img = key switch
            {
                "SipClient"    => ImgSipClient,
                "SipSettings"  => ImgSipSettings,
                "ServerConfig" => ImgServerConfig,
                "CallLog"      => ImgCallLog,
                "Help"         => ImgHelp,
                "WebClient"    => ImgWebClient,
                _              => null
            };

            if (img != null) img.Source = src;
        }

        private void TbChangeIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem mi) return;
            string key = mi.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(key)) return;

            var picker = new IconPicker { Owner = this };
            if (picker.ShowDialog() == true)
            {
                ApplyButtonIcon(key, picker.SelectedDllPath, picker.SelectedIconIndex);
                AddLog(LogLevel.Information,
                    picker.SelectedIconIndex >= 0
                        ? $"Icon changed: {key} → {Path.GetFileName(picker.SelectedDllPath)} index {picker.SelectedIconIndex}"
                        : $"Icon reset to default: {key}");
            }
        }

        private void TbSipClient_Click(object sender, RoutedEventArgs e)
        {
            EnsureSipClientWindow();
            _sipClientWindow!.Show();
            _sipClientWindow.Activate();
        }

        private void TbSipSettings_Click(object sender, RoutedEventArgs e)
        {
            EnsureSipClientWindow();
            var dlg = new SipClientSettingsWindow(_sipClientConfig)
            {
                Owner = this,
                LiveClientWindow = _sipClientWindow
            };
            if (dlg.ShowDialog() == true)
            {
                _sipClientConfig = dlg.Config;
                _sipClientWindow?.ApplyConfig(_sipClientConfig);
                AddLog(LogLevel.Information,
                    $"SIP Client config saved — port {_sipClientConfig.LocalSipPort}");
            }
        }

        private void TbServerConfig_Click(object sender, RoutedEventArgs e)
        {
            var currentConfig = DataContext is MainViewModel vm ? vm.Config : new AudisConfig();
            var dlg = new ServerConfigWindow(currentConfig) { Owner = this };
            if (dlg.ShowDialog() == true && DataContext is MainViewModel vm2)
            {
                vm2.Config = dlg.Config;
                vm2.Config.Save();
                RefreshMappings(vm2.Config);
                AddLog(LogLevel.Information, "Server configuration saved");
            }
        }

        private void TbExportLog_Click(object sender, RoutedEventArgs e)
        {
            ExportLogsInternal(openFile: true);
        }

        private void TbHelp_Click(object sender, RoutedEventArgs e)
        {
            string helpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudisHelp.htm");
            if (!File.Exists(helpPath))
            {
                MessageBox.Show(
                    "Help file (AudisHelp.htm) not found in the application directory.",
                    "Help", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(helpPath) { UseShellExecute = true });
        }

        private void EnsureSipClientWindow()
        {
            if (_sipClientWindow == null)
            {
                _sipClientWindow = new SipClientWindow();
                if (DataContext is MainViewModel vm)
                {
                    _sipClientWindow.ServerConfig = vm.Config;
                    // Inherit the current recording setting immediately
                    _sipClientWindow.Engine.IsGlobalRecordingEnabled = vm.IsRecordingEnabled;
                }
                _sipClientWindow.ClientConfig = _sipClientConfig;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // OLLAMA MANAGEMENT
        // ══════════════════════════════════════════════════════════════════════════

        private async void BtnStartAi_Click(object sender, RoutedEventArgs e)
        {
            if (_isAiRunning) { AddLog(LogLevel.Warning, "AI is already running"); return; }

            try
            {
                if (DataContext is not MainViewModel vm) return;

                AddLog(LogLevel.Information, "Starting Ollama server...");

                var checkPsi = new ProcessStartInfo
                {
                    FileName = "where", Arguments = "ollama",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };

                var checkProc = Process.Start(checkPsi);
                if (checkProc != null)
                {
                    await checkProc.WaitForExitAsync();
                    if (checkProc.ExitCode != 0)
                    {
                        AddLog(LogLevel.Error, "Ollama not found in PATH");
                        MessageBox.Show("Ollama not found!\n\nInstall from https://ollama.ai",
                                        "Ollama Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    string ollamaPath = await checkProc.StandardOutput.ReadToEndAsync();
                    AddLog(LogLevel.Information, $"Ollama: {ollamaPath.Trim()}");
                }

                await CheckAiStatus();
                if (_isAiRunning)
                {
                    AddLog(LogLevel.Information, "Ollama already running — preloading model...");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string r = await new AiCore().AskLocalAiAsync("test");
                            Dispatcher.Invoke(() => AddLog(LogLevel.Information, $"✓ Model ready: {r}"));
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => AddLog(LogLevel.Warning, $"Preload: {ex.Message}"));
                        }
                    });
                    return;
                }

                AddLog(LogLevel.Information, "Launching: ollama serve");

                _ollamaProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ollama", Arguments = "serve",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    }
                };

                _ollamaProcess.OutputDataReceived += (_, d) =>
                {
                    if (!string.IsNullOrEmpty(d.Data))
                        Dispatcher.Invoke(() => AddLog(LogLevel.Information, $"[Ollama] {d.Data}"));
                };

                _ollamaProcess.Start();
                _ollamaProcess.BeginOutputReadLine();

                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(1000);
                    await CheckAiStatus();
                    if (_isAiRunning) break;
                }

                if (_isAiRunning)
                {
                    AddLog(LogLevel.Information, "✓ Ollama server started");
                    BtnStartAi.IsEnabled = false;
                    BtnStopAi.IsEnabled  = true;
                }
                else
                {
                    AddLog(LogLevel.Warning, "Ollama did not respond after 10 seconds");
                }
            }
            catch (Exception ex)
            {
                AddLog(LogLevel.Error, $"Start AI: {ex.Message}");
            }
        }

        private void BtnStopAi_Click(object sender, RoutedEventArgs e)
        {
            StopOllama();
            AiStatusTxt.Text       = "OFFLINE";
            AiStatusTxt.Foreground = Brushes.Red;
            BtnStartAi.IsEnabled   = true;
            BtnStopAi.IsEnabled    = false;
        }

        private void StopOllama()
        {
            try
            {
                _ollamaProcess?.Kill(entireProcessTree: true);
                _ollamaProcess?.Dispose();
                _ollamaProcess = null;
                _isAiRunning   = false;
                AddLog(LogLevel.Information, "Ollama stopped");
            }
            catch { }
        }

        private async Task CheckAiStatus()
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await http.GetAsync("http://localhost:11434/api/tags");
                _isAiRunning = resp.IsSuccessStatusCode;
            }
            catch { _isAiRunning = false; }

            Dispatcher.Invoke(() =>
            {
                AiStatusTxt.Text       = _isAiRunning ? "ONLINE"  : "OFFLINE";
                AiStatusTxt.Foreground = _isAiRunning ? Brushes.Green : Brushes.Red;
                BtnStartAi.IsEnabled   = !_isAiRunning;
                BtnStopAi.IsEnabled    =  _isAiRunning;
            });
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SERVICE START / STOP
        // ══════════════════════════════════════════════════════════════════════════

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            _engine.IsGlobalRecordingEnabled = vm.IsRecordingEnabled;
            _engine.Start(vm.Config);

            StatusTxt.Text       = "RUNNING";
            StatusTxt.Foreground = Brushes.Green;
            BtnStart.IsEnabled   = false;
            BtnStop.IsEnabled    = true;
            _timer.Start();

            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsRecordingEnabled))
                {
                    _engine.IsGlobalRecordingEnabled = vm.IsRecordingEnabled;
                    // Keep SIP client engine in sync with the same toggle
                    if (_sipClientWindow != null)
                        _sipClientWindow.Engine.IsGlobalRecordingEnabled = vm.IsRecordingEnabled;
                }
            };
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine.Stop();
            StatusTxt.Text       = "STOPPED";
            StatusTxt.Foreground = Brushes.Red;
            BtnStart.IsEnabled   = true;
            BtnStop.IsEnabled    = false;

            if (DataContext is MainViewModel vm) vm.ActiveCalls.Clear();
            _timer.Stop();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // CALL STATUS
        // ══════════════════════════════════════════════════════════════════════════

        private void OnCallStatusChange(object? sender, CallStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var existing = _activeCalls.FirstOrDefault(c => c.CallId == e.CallId);

                if (!e.IsActive)
                {
                    if (existing != null)
                    {
                        var duration = DateTime.Now - existing.StartTime;
                        _callLog.Add(new CallLogEntry
                        {
                            CallId    = existing.CallId,
                            Extension = existing.Extension,
                            StartTime = existing.StartTime,
                            EndTime   = DateTime.Now,
                            Duration  = duration,
                            Status    = "Ended"
                        });

                        existing.IsActive = false;
                        _activeCalls.Remove(existing);
                    }
                    _hangupGraveyard[e.CallId] = DateTime.Now;
                    RefreshRecordingsList();
                    AddLog(LogLevel.Information, $"Call ended: {e.CallId}");
                }
                else
                {
                    if (existing == null)
                    {
                        if (_hangupGraveyard.TryGetValue(e.CallId, out DateTime dt) &&
                            (DateTime.Now - dt).TotalSeconds < 5) return;

                        var newCall = new CallViewModel
                        {
                            CallId    = e.CallId,
                            Extension = e.Extension,
                            Status    = e.Status,
                            IsActive  = e.IsActive,
                            StartTime = DateTime.Now,
                            LastInput = e.LastInput
                        };
                        _activeCalls.Add(newCall);
                    }
                    else
                    {
                        existing.Status = e.Status;
                        if (!string.IsNullOrEmpty(e.LastInput)) existing.LastInput = e.LastInput;
                        if (!string.IsNullOrEmpty(e.Extension)) existing.Extension = e.Extension;
                    }
                }

                var stale = _hangupGraveyard
                    .Where(x => (DateTime.Now - x.Value).TotalSeconds > 10)
                    .Select(x => x.Key).ToList();
                foreach (var k in stale) _hangupGraveyard.Remove(k);
            });
        }

        // ══════════════════════════════════════════════════════════════════════════
        // LOGGING
        // ══════════════════════════════════════════════════════════════════════════

        private void AddLog(LogLevel level, string message)
        {
            if (_logs.Count > 200) _logs.RemoveAt(0);
            _logs.Add(new UiLogEntry(DateTime.Now, level, message));
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }

        private void BtnExportLogs_Click(object sender, RoutedEventArgs e)
            => ExportLogsInternal(openFile: false);

        private void ExportLogsInternal(bool openFile)
        {
            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                string filename = Path.Combine(logsDir, $"audis_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                using var writer = new StreamWriter(filename);
                writer.WriteLine(new string('=', 80));
                writer.WriteLine($"AUDIS SERVICE LOG — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine(new string('=', 80));
                writer.WriteLine();

                if (DataContext is MainViewModel vm)
                {
                    writer.WriteLine($"Version:    1.4");
                    writer.WriteLine($"Public IP:  {vm.Config.PublicIp}");
                    writer.WriteLine($"SIP Port:   {vm.Config.Port}");
                }

                writer.WriteLine();
                foreach (var log in _logs) writer.WriteLine(log.FullMessage);

                AddLog(LogLevel.Information, $"Exported: {filename}");

                if (openFile)
                    Process.Start(new ProcessStartInfo(filename) { UseShellExecute = true });
                else
                {
                    var result = MessageBox.Show(
                        $"Logs exported:\n\n{filename}\n\nOpen the logs folder?",
                        "Export Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                        Process.Start("explorer.exe", logsDir);
                }
            }
            catch (Exception ex)
            {
                AddLog(LogLevel.Error, $"Export failed: {ex.Message}");
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Clear all logs?", "Clear Logs",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _logs.Clear();
                AddLog(LogLevel.Information, "Logs cleared");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // CONFIG / FILES / RECORDINGS
        // ══════════════════════════════════════════════════════════════════════════

        private void RefreshMappings(AudisConfig cfg)
        {
            _mappings.Clear();
            foreach (var kvp in cfg.KeyMappings
                .OrderBy(x => int.TryParse(x.Key, out int n) ? n : int.MaxValue)
                .ThenBy(x => x.Key))
                _mappings.Add(new MappingItem { Key = kvp.Key, Value = kvp.Value });
            if (DataContext is MainViewModel vm) vm.Mappings = _mappings;
        }

        private void BtnRefreshFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string audioDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio");
                if (Directory.Exists(audioDir))
                {
                    var files = Directory.GetFiles(audioDir, "*.wav")
                                         .Select(Path.GetFileName).OrderBy(x => x).ToList();
                    FileList.ItemsSource = files;
                    AddLog(LogLevel.Information, $"Audio files: {files.Count}");
                }
                else AddLog(LogLevel.Warning, $"Audio directory not found: {audioDir}");
            }
            catch (Exception ex) { AddLog(LogLevel.Error, $"Refresh files: {ex.Message}"); }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string audioDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio");
            Directory.CreateDirectory(audioDir);
            Process.Start("explorer.exe", audioDir);
        }

        private void RefreshRecordingsList()
        {
            try
            {
                string recDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
                if (!Directory.Exists(recDir)) return;

                var files = new DirectoryInfo(recDir).GetFiles("*.wav")
                    .OrderByDescending(f => f.CreationTime).Take(20)
                    .Select(x => new RecordingItem
                        { Name = x.Name, CreationTime = x.CreationTime, FullPath = x.FullName })
                    .ToList();

                RecordingsList.ItemsSource = files;
            }
            catch { }
        }

        private void BtnRefreshRecordings_Click(object sender, RoutedEventArgs e) => RefreshRecordingsList();

        private void BtnOpenRecordingsFolder_Click(object sender, RoutedEventArgs e)
        {
            string recDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
            Directory.CreateDirectory(recDir);
            Process.Start("explorer.exe", recDir);
        }
    }

    // ── View Models ───────────────────────────────────────────────────────────────

    public class MainViewModel : INotifyPropertyChanged
    {
        private AudisConfig _config = new();
        public AudisConfig Config { get => _config; set { _config = value; OnProp(nameof(Config)); } }

        public ObservableCollection<CallViewModel> ActiveCalls { get; set; } = new();
        public ObservableCollection<MappingItem>   Mappings    { get; set; } = new();

        private bool _isRecordingEnabled;
        public bool IsRecordingEnabled
        {
            get => _isRecordingEnabled;
            set { _isRecordingEnabled = value; OnProp(nameof(IsRecordingEnabled)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnProp(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class CallViewModel : INotifyPropertyChanged
    {
        private string _status    = "";
        private string _lastInput = "";
        private string _duration  = "00:00";
        private string _extension = "";

        public string   CallId    { get; set; } = "";
        public DateTime StartTime { get; set; }
        public bool     IsActive  { get; set; }

        public string Extension   { get => _extension; set { _extension = value; OnProp(nameof(Extension)); } }
        public string Status      { get => _status;    set { _status    = value; OnProp(nameof(Status));    } }
        public string LastInput   { get => _lastInput; set { _lastInput = value; OnProp(nameof(LastInput)); } }
        public string DurationStr { get => _duration;  set { _duration  = value; OnProp(nameof(DurationStr)); } }

        public void UpdateDuration() => DurationStr = (DateTime.Now - StartTime).ToString(@"mm\:ss");

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnProp(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class CallLogEntry
    {
        public string    CallId    { get; set; } = "";
        public string    Extension { get; set; } = "";
        public DateTime  StartTime { get; set; }
        public DateTime? EndTime   { get; set; }
        public TimeSpan? Duration  { get; set; }
        public string    Status    { get; set; } = "";

        public string StartTimeStr => StartTime.ToString("HH:mm:ss");
        public string EndTimeStr   => EndTime?.ToString("HH:mm:ss") ?? "—";
        public string DurationStr  => Duration.HasValue
            ? Duration.Value.ToString(@"mm\:ss")
            : (DateTime.Now - StartTime).ToString(@"mm\:ss");
    }

    public class MappingItem
    {
        public string Key   { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class RecordingItem
    {
        public string   Name         { get; set; } = "";
        public DateTime CreationTime { get; set; }
        public string   FullPath     { get; set; } = "";
    }

    public class UiLogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level     { get; }
        public string   Message   { get; }
        public string   FullMessage =>
            $"[{Timestamp:HH:mm:ss}] {Level.ToString().ToUpper()[..3]}: {Message}";

        public UiLogEntry(DateTime ts, LogLevel lvl, string msg)
        { Timestamp = ts; Level = lvl; Message = msg; }
    }
}
