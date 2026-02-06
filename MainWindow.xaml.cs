using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics; 
using System.IO;
using System.Threading.Tasks;

namespace AudisService
{
    public partial class MainWindow : Window
    {
        private SipEngine _engine;
        private ObservableCollection<UiLogEntry> _logs = new();
        private ObservableCollection<CallViewModel> _activeCalls = new();
        private ObservableCollection<MappingItem> _mappings = new(); 
        private DispatcherTimer _timer;
        private DispatcherTimer _aiStatusTimer;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private Dictionary<string, DateTime> _hangupGraveyard = new(StringComparer.OrdinalIgnoreCase);

        // Ollama process management
        private Process? _ollamaProcess = null;
        private bool _isAiRunning = false;

        public bool IsRecordingEnabled
        {
            get => (DataContext as MainViewModel)?.IsRecordingEnabled ?? false;
            set { if (DataContext is MainViewModel vm) vm.IsRecordingEnabled = value; }
        }

        public MainWindow()
        {
            InitializeComponent();
            
            var vm = new MainViewModel();
            vm.Config = new AudisConfig();
            this.DataContext = vm;

            _activeCalls = vm.ActiveCalls;

            _engine = new SipEngine();
            _engine.OnLog += (lvl, msg) => Dispatcher.Invoke(() => AddLog(lvl, msg));
            _engine.OnCallStatusChange += OnCallStatusChange;

            LogList.ItemsSource = _logs;
            RefreshMappings(vm.Config);
            
            RefreshRecordingsList();

            // Load Logo
            try {
                string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                if (!File.Exists(logoPath)) logoPath = @"C:\Scripts\AudisService\icon.png";
                
                if(File.Exists(logoPath))
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    LogoImage.Source = bitmap;
                }
            } catch { }

            // Timer for UI updates
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => { 
                foreach(var call in _activeCalls)
                {
                    if (call.IsActive)
                        call.UpdateDuration();
                }
            };

            // AI Status Checker Timer
            _aiStatusTimer = new DispatcherTimer();
            _aiStatusTimer.Interval = TimeSpan.FromSeconds(2);
            _aiStatusTimer.Tick += async (s, e) => await CheckAiStatus();
            _aiStatusTimer.Start();

            // System Tray
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application; 
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "Audis - Kybl Enterprise";
            _notifyIcon.DoubleClick += (s, args) => { this.Show(); this.WindowState = WindowState.Normal; };
            
            this.Closing += (s, e) => {
                _notifyIcon.Visible = false;
                _engine.Stop();
                StopOllama();
            };

            AddLog(LogLevel.Information, "Audis Service Initialized");
            AddLog(LogLevel.Information, $"Audio Directory: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio")}");
            
            // Check initial AI status
            _ = CheckAiStatus();
        }

        // ====== OLLAMA MANAGEMENT ======

        private async void BtnStartAi_Click(object sender, RoutedEventArgs e)
        {
            if (_isAiRunning)
            {
                AddLog(LogLevel.Warning, "AI is already running");
                return;
            }

            try
            {
                if (DataContext is MainViewModel vm)
                {
                    AddLog(LogLevel.Information, $"Starting Ollama server...");
                    AddLog(LogLevel.Information, "Checking if Ollama is installed...");
                    
                    // First check if ollama command exists
                    var checkPsi = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "ollama",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    
                    var checkProc = Process.Start(checkPsi);
                    if (checkProc != null)
                    {
                        await checkProc.WaitForExitAsync();
                        if (checkProc.ExitCode != 0)
                        {
                            AddLog(LogLevel.Error, "Ollama command not found in PATH");
                            System.Windows.MessageBox.Show(
                                "Ollama not found!\n\n" +
                                "Please install Ollama from https://ollama.ai\n" +
                                "Or make sure it's in your system PATH.",
                                "Ollama Not Found",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error
                            );
                            return;
                        }
                        
                        string ollamaPath = await checkProc.StandardOutput.ReadToEndAsync();
                        AddLog(LogLevel.Information, $"Ollama found at: {ollamaPath.Trim()}");
                    }
                    
                    // Check if Ollama server is already running
                    await CheckAiStatus();
                    if (_isAiRunning)
                    {
                        AddLog(LogLevel.Information, "Ollama server is already running!");
                        AddLog(LogLevel.Information, $"Model configured: {vm.Config.OllamaModel}");
                        
                        // Preload the model
                        AddLog(LogLevel.Information, $"Preloading model {vm.Config.OllamaModel}...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var aiCore = new AiCore();
                                string response = await aiCore.AskLocalAiAsync("test");
                                Dispatcher.Invoke(() => AddLog(LogLevel.Information, $"✓ Model {vm.Config.OllamaModel} is ready!"));
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => AddLog(LogLevel.Warning, $"Model preload failed: {ex.Message}"));
                            }
                        });
                        
                        return;
                    }
                    
                    // CRITICAL FIX: Use "ollama serve" not "ollama run"
                    // "serve" starts the API server, "run" is interactive and exits immediately
                    AddLog(LogLevel.Information, "Launching: ollama serve");
                    
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ollama",
                        Arguments = "serve",  // CHANGED FROM "run {model}"
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    _ollamaProcess = new Process { StartInfo = psi };
                    
                    // Capture output for debugging
                    _ollamaProcess.OutputDataReceived += (s, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                            Dispatcher.Invoke(() => AddLog(LogLevel.Information, $"[Ollama OUT] {args.Data}"));
                    };
                    
                    _ollamaProcess.ErrorDataReceived += (s, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            // Don't log normal INFO messages as warnings
                            string data = args.Data;
                            if (data.Contains("level=INFO"))
                                Dispatcher.Invoke(() => AddLog(LogLevel.Information, $"[Ollama] {data}"));
                            else
                                Dispatcher.Invoke(() => AddLog(LogLevel.Warning, $"[Ollama ERR] {data}"));
                        }
                    };
                    
                    _ollamaProcess.Exited += (s, args) =>
                    {
                        Dispatcher.Invoke(() => {
                            AddLog(LogLevel.Warning, $"Ollama server exited with code: {_ollamaProcess?.ExitCode}");
                            _isAiRunning = false;
                            UpdateAiStatus(false);
                        });
                    };
                    _ollamaProcess.EnableRaisingEvents = true;

                    bool started = _ollamaProcess.Start();
                    if (!started)
                    {
                        AddLog(LogLevel.Error, "Failed to start Ollama process");
                        return;
                    }
                    
                    _ollamaProcess.BeginOutputReadLine();
                    _ollamaProcess.BeginErrorReadLine();
                    
                    AddLog(LogLevel.Information, $"Ollama server started (PID: {_ollamaProcess.Id})");
                    AddLog(LogLevel.Information, "Waiting for Ollama API to become ready...");

                    // Wait for Ollama to start and check status
                    for (int i = 0; i < 10; i++) // Wait up to 10 seconds
                    {
                        await Task.Delay(1000);
                        await CheckAiStatus();
                        
                        if (_isAiRunning)
                        {
                            AddLog(LogLevel.Information, "✓ Ollama server is now ONLINE");
                            AddLog(LogLevel.Information, $"Configured model: {vm.Config.OllamaModel}");
                            
                            // Preload the model
                            AddLog(LogLevel.Information, $"Preloading model {vm.Config.OllamaModel}...");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var aiCore = new AiCore();
                                    string response = await aiCore.AskLocalAiAsync("test");
                                    Dispatcher.Invoke(() => AddLog(LogLevel.Information, $"✓ Model {vm.Config.OllamaModel} is ready! You can now use AI mode (press * during calls)"));
                                }
                                catch (Exception ex)
                                {
                                    Dispatcher.Invoke(() => AddLog(LogLevel.Warning, $"Model preload failed: {ex.Message}. Model will load on first use."));
                                }
                            });
                            
                            return;
                        }
                    }
                    
                    AddLog(LogLevel.Warning, "Ollama server started but API not responding after 10s");
                    AddLog(LogLevel.Information, "Server may still be starting up. Check status in a moment.");
                }
            }
            catch (Exception ex)
            {
                AddLog(LogLevel.Error, $"Failed to start Ollama: {ex.Message}");
                AddLog(LogLevel.Error, $"Exception type: {ex.GetType().Name}");
                AddLog(LogLevel.Error, $"Stack trace: {ex.StackTrace}");
                
                System.Windows.MessageBox.Show(
                    $"Failed to start Ollama:\n\n{ex.Message}\n\n" +
                    "Make sure:\n" +
                    "1. Ollama is installed (https://ollama.ai)\n" +
                    "2. Model is downloaded: ollama pull gemma3:1b\n" +
                    "3. Ollama is in your system PATH",
                    "Ollama Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void BtnStopAi_Click(object sender, RoutedEventArgs e)
        {
            StopOllama();
        }

        private void StopOllama()
        {
            if (_ollamaProcess != null && !_ollamaProcess.HasExited)
            {
                try
                {
                    AddLog(LogLevel.Information, "Stopping Ollama...");
                    
                    // CRITICAL FIX: Kill the entire process tree, not just parent
                    int pid = _ollamaProcess.Id;
                    
                    // Use taskkill to kill process tree (includes all child runners)
                    var killProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /T /PID {pid}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    
                    killProcess.Start();
                    killProcess.WaitForExit(3000); // Wait max 3 seconds
                    
                    AddLog(LogLevel.Information, $"Killed Ollama process tree (PID: {pid})");
                    
                    _ollamaProcess.Dispose();
                    _ollamaProcess = null;
                    
                    _isAiRunning = false;
                    UpdateAiStatus(false);
                    
                    AddLog(LogLevel.Information, "Ollama stopped");
                }
                catch (Exception ex)
                {
                    AddLog(LogLevel.Error, $"Error stopping Ollama: {ex.Message}");
                }
            }
        }

        private async Task CheckAiStatus()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                
                var response = await client.GetAsync("http://localhost:11434/api/tags");
                bool isRunning = response.IsSuccessStatusCode;
                
                if (_isAiRunning != isRunning)
                {
                    _isAiRunning = isRunning;
                    UpdateAiStatus(isRunning);
                }
            }
            catch
            {
                if (_isAiRunning)
                {
                    _isAiRunning = false;
                    UpdateAiStatus(false);
                }
            }
        }

        private void UpdateAiStatus(bool isRunning)
        {
            if (isRunning)
            {
                AiStatusTxt.Text = "ONLINE";
                AiStatusTxt.Foreground = System.Windows.Media.Brushes.Green;
                BtnStartAi.IsEnabled = false;
                BtnStopAi.IsEnabled = true;
            }
            else
            {
                AiStatusTxt.Text = "OFFLINE";
                AiStatusTxt.Foreground = System.Windows.Media.Brushes.Red;
                BtnStartAi.IsEnabled = true;
                BtnStopAi.IsEnabled = false;
            }
        }

        // ====== SIP SERVICE MANAGEMENT ======

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                _engine.IsGlobalRecordingEnabled = vm.IsRecordingEnabled;
                _engine.Start(vm.Config);
                
                StatusTxt.Text = "RUNNING";
                StatusTxt.Foreground = System.Windows.Media.Brushes.Green;
                
                BtnStart.IsEnabled = false;
                BtnStop.IsEnabled = true;
                _timer.Start();
                
                vm.PropertyChanged += (s, args) => {
                    if (args.PropertyName == nameof(MainViewModel.IsRecordingEnabled))
                        _engine.IsGlobalRecordingEnabled = vm.IsRecordingEnabled;
                };
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine.Stop();
            StatusTxt.Text = "STOPPED";
            StatusTxt.Foreground = System.Windows.Media.Brushes.Red;
            
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            
            if (DataContext is MainViewModel vm)
            {
                vm.ActiveCalls.Clear();
            }
            
            _timer.Stop();
        }

        // ====== CALL STATUS ======

        private void OnCallStatusChange(object? sender, CallStatusEventArgs e)
        {
            Dispatcher.Invoke(() => 
            {
                var existing = _activeCalls.FirstOrDefault(c => c.CallId == e.CallId);
                
                if (!e.IsActive)
                {
                    // CRITICAL FIX: Stop the timer immediately
                    if (existing != null)
                    {
                        existing.IsActive = false;  // Stop duration updates
                        _activeCalls.Remove(existing);
                    }
                    
                    _hangupGraveyard[e.CallId] = DateTime.Now;
                    RefreshRecordingsList();
                    
                    AddLog(LogLevel.Information, $"Call ended and removed from UI: {e.CallId}");
                }
                else
                {
                    if (existing == null)
                    {
                        if (_hangupGraveyard.TryGetValue(e.CallId, out DateTime deathTime))
                        {
                            if ((DateTime.Now - deathTime).TotalSeconds < 5) return; 
                        }

                        var newCall = new CallViewModel 
                        { 
                            CallId = e.CallId, 
                            Status = e.Status, 
                            IsActive = e.IsActive,
                            StartTime = DateTime.Now,
                            LastInput = e.LastInput 
                        };
                        _activeCalls.Add(newCall);
                    }
                    else
                    {
                        existing.Status = e.Status;
                        if (!string.IsNullOrEmpty(e.LastInput))
                            existing.LastInput = e.LastInput;
                    }
                }
                
                var oldKeys = _hangupGraveyard.Where(x => (DateTime.Now - x.Value).TotalSeconds > 10).Select(x => x.Key).ToList();
                foreach(var k in oldKeys) _hangupGraveyard.Remove(k);
            });
        }

        // ====== LOGGING ======

        private void AddLog(LogLevel level, string message)
        {
            if (_logs.Count > 100) _logs.RemoveAt(0);
            _logs.Add(new UiLogEntry(DateTime.Now, level, message));
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }

        // ====== CONFIG ======

        private void RefreshMappings(AudisConfig cfg)
        {
            _mappings.Clear();
            foreach (var kvp in cfg.KeyMappings.OrderBy(x => x.Key))
                _mappings.Add(new MappingItem { Key = kvp.Key, Value = kvp.Value });
            
            if(DataContext is MainViewModel vm) vm.Mappings = _mappings;
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show("Configuration saved to memory (Runtime only in this version).", "Audis", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnPublicIpHelp_Click(object sender, RoutedEventArgs e)
        {
            string msg = "This IP is used for SIP Signaling Contact headers.\n\n" +
                         "If Audis is behind a router (NAT), set this to your PC's local IP (e.g. 192.168.100.64). " +
                         "This ensures the VoIP phone sends audio back to the correct internal address.\n\n" +
                         "If you are on a public server, set this to the WAN IP.";
            System.Windows.MessageBox.Show(msg, "Public IP Configuration Help", MessageBoxButton.OK, MessageBoxImage.Question);
        }

        // ====== FILE MANAGEMENT ======

        private void BtnRefreshFiles_Click(object sender, RoutedEventArgs e)
        {
            try {
                if(DataContext is MainViewModel vm)
                {
                    string audioDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio");
                    if (Directory.Exists(audioDir))
                    {
                        var files = Directory.GetFiles(audioDir, "*.wav")
                                             .Select(Path.GetFileName)
                                             .OrderBy(x => x)
                                             .ToList();
                        FileList.ItemsSource = files;
                        AddLog(LogLevel.Information, $"Found {files.Count} audio files");
                    }
                    else
                    {
                        AddLog(LogLevel.Warning, $"Audio directory not found: {audioDir}");
                    }
                }
            } catch (Exception ex) {
                AddLog(LogLevel.Error, $"Error refreshing files: {ex.Message}");
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
             string audioDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio");
             if (!Directory.Exists(audioDir))
             {
                 Directory.CreateDirectory(audioDir);
                 AddLog(LogLevel.Information, $"Created audio directory: {audioDir}");
             }
             Process.Start("explorer.exe", audioDir);
        }

        // ====== RECORDINGS ======

        private void RefreshRecordingsList()
        {
            try {
                string recDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
                if (!Directory.Exists(recDir)) return;

                var files = new DirectoryInfo(recDir).GetFiles("*.wav")
                                                     .OrderByDescending(f => f.CreationTime)
                                                     .Take(20)
                                                     .Select(x => new RecordingItem { 
                                                         Name = x.Name, 
                                                         CreationTime = x.CreationTime, 
                                                         FullPath = x.FullName 
                                                     })
                                                     .ToList();
                
                RecordingsList.ItemsSource = files;
            } catch { }
        }

        private void BtnRefreshRecordings_Click(object sender, RoutedEventArgs e)
        {
            RefreshRecordingsList();
        }

        private void BtnOpenRecordingsFolder_Click(object sender, RoutedEventArgs e)
        {
             string recDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
             if (!Directory.Exists(recDir)) Directory.CreateDirectory(recDir);
             Process.Start("explorer.exe", recDir);
        }

        // ====== LOG EXPORT ======

        private void BtnExportLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(logsDir))
                    Directory.CreateDirectory(logsDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = Path.Combine(logsDir, $"audis_log_{timestamp}.txt");

                using (var writer = new StreamWriter(filename))
                {
                    writer.WriteLine("=".PadRight(80, '='));
                    writer.WriteLine($"AUDIS SERVICE LOG - Exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine("=".PadRight(80, '='));
                    writer.WriteLine();
                    
                    writer.WriteLine($"Version: 1.2");
                    writer.WriteLine($"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
                    writer.WriteLine($"Service Status: {StatusTxt.Text}");
                    writer.WriteLine($"AI Status: {AiStatusTxt.Text}");
                    writer.WriteLine($"Recording Enabled: {IsRecordingEnabled}");
                    
                    if (DataContext is MainViewModel vm)
                    {
                        writer.WriteLine($"Public IP: {vm.Config.PublicIp}");
                        writer.WriteLine($"SIP Port: {vm.Config.Port}");
                        writer.WriteLine($"Ollama Model: {vm.Config.OllamaModel}");
                    }
                    
                    writer.WriteLine();
                    writer.WriteLine("=".PadRight(80, '='));
                    writer.WriteLine("ACTIVE CALLS");
                    writer.WriteLine("=".PadRight(80, '='));
                    
                    if (_activeCalls.Count == 0)
                    {
                        writer.WriteLine("(none)");
                    }
                    else
                    {
                        foreach (var call in _activeCalls)
                        {
                            writer.WriteLine($"Call ID: {call.CallId}");
                            writer.WriteLine($"  Status: {call.Status}");
                            writer.WriteLine($"  Duration: {call.DurationStr}");
                            writer.WriteLine($"  Last Input: {call.LastInput}");
                            writer.WriteLine($"  Active: {call.IsActive}");
                            writer.WriteLine();
                        }
                    }
                    
                    writer.WriteLine();
                    writer.WriteLine("=".PadRight(80, '='));
                    writer.WriteLine("SYSTEM LOGS");
                    writer.WriteLine("=".PadRight(80, '='));
                    writer.WriteLine();
                    
                    foreach (var log in _logs)
                    {
                        writer.WriteLine(log.FullMessage);
                    }
                    
                    writer.WriteLine();
                    writer.WriteLine("=".PadRight(80, '='));
                    writer.WriteLine("END OF LOG");
                    writer.WriteLine("=".PadRight(80, '='));
                }

                AddLog(LogLevel.Information, $"Logs exported to: {filename}");
                
                var result = System.Windows.MessageBox.Show(
                    $"Logs exported successfully!\n\n{filename}\n\nWould you like to open the logs folder?",
                    "Export Complete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                );

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start("explorer.exe", logsDir);
                }
            }
            catch (Exception ex)
            {
                AddLog(LogLevel.Error, $"Failed to export logs: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Failed to export logs:\n{ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to clear all logs?\nThis cannot be undone.",
                "Clear Logs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result == MessageBoxResult.Yes)
            {
                _logs.Clear();
                AddLog(LogLevel.Information, "Logs cleared by user");
            }
        }
    }

    // --- VIEW MODELS ---
    
    public class MainViewModel : INotifyPropertyChanged
    {
        private AudisConfig _config = new AudisConfig();
        public AudisConfig Config { get => _config; set { _config = value; OnProp("Config"); } }
        
        public ObservableCollection<CallViewModel> ActiveCalls { get; set; } = new();
        public ObservableCollection<MappingItem> Mappings { get; set; } = new();

        private string _status = "";
        public string Status { get => _status; set { _status = value; OnProp("Status"); } }
        
        private bool _isRecordingEnabled;
        public bool IsRecordingEnabled { get => _isRecordingEnabled; set { _isRecordingEnabled = value; OnProp("IsRecordingEnabled"); } }
        
        public event PropertyChangedEventHandler? PropertyChanged;
        void OnProp(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class CallViewModel : INotifyPropertyChanged
    {
        private string _status = string.Empty;
        private string _lastInput = string.Empty;
        private string _durationStr = "00:00";

        public string CallId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        
        public string Status 
        { 
            get => _status; 
            set { _status = value; OnProp("Status"); } 
        }

        public string LastInput
        {
            get => _lastInput;
            set { _lastInput = value; OnProp("LastInput"); }
        }

        public string DurationStr
        {
            get => _durationStr;
            set { _durationStr = value; OnProp("DurationStr"); }
        }
        
        public bool IsActive { get; set; }

        public void UpdateDuration()
        {
            DurationStr = (DateTime.Now - StartTime).ToString(@"mm\:ss");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnProp(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class MappingItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class RecordingItem
    {
        public string Name { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; }
        public string FullPath { get; set; } = string.Empty;
    }

    public class UiLogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }
        public string FullMessage => $"[{Timestamp:HH:mm:ss}] {Level.ToString().ToUpper().Substring(0, 3)}: {Message}";

        public UiLogEntry(DateTime ts, LogLevel lvl, string msg)
        {
            Timestamp = ts;
            Level = lvl;
            Message = msg;
        }
    }
}
