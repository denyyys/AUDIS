using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics; // Required for Process

namespace AudisService
{
    public partial class MainWindow : Window
    {
        private SipEngine _engine;
        private ObservableCollection<UiLogEntry> _logs = new();
        private ObservableCollection<CallViewModel> _activeCalls = new();
        private ObservableCollection<MappingItem> _mappings = new(); // For DataGrid
        private DispatcherTimer _timer;
        
        // System Tray Icon
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        // GRAVEYARD: Stores IDs of calls that recently ended to block "Zombie" retransmissions
        // StringComparer.OrdinalIgnoreCase ensures robust matching
        private Dictionary<string, DateTime> _hangupGraveyard = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            
            // --- SYSTEM TRAY SETUP ---
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application; // Use standard icon
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "Audis - Kybl Enterprise";
            _notifyIcon.DoubleClick += (s, args) => { this.Show(); this.WindowState = WindowState.Normal; };
            
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open Console", null, (s, e) => { this.Show(); this.WindowState = WindowState.Normal; });
            contextMenu.Items.Add("Exit Service", null, (s, e) => { ExitApplication(); });
            _notifyIcon.ContextMenuStrip = contextMenu;

            // --- ENGINE SETUP ---
            var logger = new UiLogger(LogToUi);
            _engine = new SipEngine(logger);
            _engine.CallStateChanged += Engine_CallStateChanged;
            _engine.SipTrafficReceived += (msg) => Dispatcher.Invoke(() => TxtSniffer.AppendText(msg + "\n"));

            // --- BINDING ---
            LogList.ItemsSource = _logs;
            CallsGrid.ItemsSource = _activeCalls;
            MappingGrid.ItemsSource = _mappings;

            // Load Config into UI
            LoadConfigToUi();

            // UI Timer
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            LogToUi("SYSTEM", "Audis Manager v1.1 Loaded.");
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // 1. Update Call Durations
            foreach(var c in _activeCalls.ToList()) c.UpdateDuration();

            // 2. Graveyard Cleanup (Remove IDs older than 35s so memory doesn't grow forever)
            var now = DateTime.Now;
            var expired = _hangupGraveyard.Where(x => (now - x.Value).TotalSeconds > 35).Select(x => x.Key).ToList();
            foreach (var key in expired) _hangupGraveyard.Remove(key);

            // 3. Update RAM Usage
            try
            {
                using var proc = Process.GetCurrentProcess();
                double memMb = proc.WorkingSet64 / 1024.0 / 1024.0;
                TxtRam.Text = $"{memMb:F1} MB";
            }
            catch { /* Ignore momentary errors */ }
        }

        private void LoadConfigToUi()
        {
            TxtPubIp.Text = _engine.CurrentConfig.PublicIp;
            TxtPort.Text = _engine.CurrentConfig.Port.ToString();
            TxtCity.Text = _engine.CurrentConfig.WeatherCity;
            TxtLat.Text = _engine.CurrentConfig.WeatherLat.ToString();
            TxtLon.Text = _engine.CurrentConfig.WeatherLong.ToString();

            _mappings.Clear();
            foreach(var kvp in _engine.CurrentConfig.KeyMappings)
            {
                _mappings.Add(new MappingItem { Key = kvp.Key, Value = kvp.Value });
            }
        }

        // --- BUTTONS ---

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            ApplyConfig(); // Ensure latest settings are used
            _engine.Start();
            
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            StatusTxt.Text = "RUNNING";
            StatusTxt.Foreground = System.Windows.Media.Brushes.DarkGreen;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine.Stop();
            _activeCalls.Clear();
            _hangupGraveyard.Clear(); // Clear graveyard on stop
            
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            StatusTxt.Text = "STOPPED";
            StatusTxt.Foreground = System.Windows.Media.Brushes.Red;
        }

        private void BtnApplyConfig_Click(object sender, RoutedEventArgs e)
        {
            ApplyConfig();
            LogToUi("CONFIG", "Configuration saved. Restart service to apply Network changes.");
            System.Windows.MessageBox.Show("Configuration Updated.", "Audis Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ApplyConfig()
        {
            _engine.CurrentConfig.PublicIp = TxtPubIp.Text;
            if(int.TryParse(TxtPort.Text, out int p)) _engine.CurrentConfig.Port = p;
            
            _engine.CurrentConfig.WeatherCity = TxtCity.Text;
            if(double.TryParse(TxtLat.Text, out double lat)) _engine.CurrentConfig.WeatherLat = lat;
            if(double.TryParse(TxtLon.Text, out double lon)) _engine.CurrentConfig.WeatherLong = lon;

            // Update Mappings from Grid
            _engine.CurrentConfig.KeyMappings.Clear();
            foreach(var item in _mappings)
            {
                _engine.CurrentConfig.KeyMappings[item.Key] = item.Value;
            }
        }

        private void BtnClearSniff_Click(object sender, RoutedEventArgs e) => TxtSniffer.Clear();

        // --- EVENTS & LOGGING ---

        private void LogToUi(string category, string message)
        {
            Dispatcher.Invoke(() => 
            {
                var entry = new UiLogEntry { Timestamp = DateTime.Now.ToString("HH:mm:ss"), Message = message, Category = category };
                _logs.Add(entry);
                if (_logs.Count > 200) _logs.RemoveAt(0);
                
                if (VisualTreeHelper.GetChildrenCount(LogList) > 0)
                {
                    Border border = (Border)VisualTreeHelper.GetChild(LogList, 0);
                    ScrollViewer scrollViewer = (ScrollViewer)VisualTreeHelper.GetChild(border, 0);
                    scrollViewer.ScrollToBottom();
                }
            });
        }

        private void Engine_CallStateChanged(object? sender, CallStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                string cleanId = e.CallId.Trim();

                // 1. ZOMBIE FILTER: Check Graveyard
                if (e.IsActive && _hangupGraveyard.ContainsKey(cleanId))
                {
                    return; 
                }

                // Find existing call
                var existing = _activeCalls.FirstOrDefault(x => x.CallId.Equals(cleanId, StringComparison.OrdinalIgnoreCase));

                // If call is disconnected
                if (!e.IsActive) 
                {
                    if (existing != null) 
                    {
                        // Add to Graveyard
                        _hangupGraveyard[cleanId] = DateTime.Now;
                        
                        LogToUi("CALL", $"Call Ended: {cleanId}");
                        existing.IsLive = false;
                        _activeCalls.Remove(existing);
                    }
                    else
                    {
                        // Fallback cleanup
                         if (_activeCalls.Count == 1 && !_hangupGraveyard.ContainsKey(cleanId))
                         {
                            var stuck = _activeCalls[0];
                            _hangupGraveyard[stuck.CallId] = DateTime.Now;
                            stuck.IsLive = false;
                            _activeCalls.Remove(stuck);
                         }
                    }
                    return;
                }

                // If call is new
                if (existing == null) 
                {
                    existing = new CallViewModel { CallId = cleanId, StartTime = DateTime.Now };
                    _activeCalls.Add(existing);
                }

                // Update status
                existing.Status = e.Status;
                existing.LastInput = e.LastInput;
            });
        }

        // --- WINDOW & TRAY LOGIC ---

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
                this.Hide();
            base.OnStateChanged(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Intercept close button to minimize to tray
            e.Cancel = true;
            this.Hide();
            
            // SHOW NOTIFICATION
            _notifyIcon.ShowBalloonTip(3000, "Audis - Kybl Enterprise", "AUDIS is still running", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void ExitApplication()
        {
            _notifyIcon.Dispose();
            _engine?.Stop();
            System.Windows.Application.Current.Shutdown();
        }
    }

    // --- VIEW MODELS ---

    public class MappingItem
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class UiLogEntry
    {
        public string Timestamp { get; set; } = "";
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
        public string FullMessage => $"[{Timestamp}] [{Category}] {Message}";
    }

    public class CallViewModel : INotifyPropertyChanged
    {
        public string CallId { get; set; } = "";
        public bool IsLive { get; set; } = true; // Control flag for duration timer

        private string _status = "";
        public string Status { get => _status; set { _status = value; OnProp("Status"); } }
        
        private string _lastInput = "";
        public string LastInput { get => _lastInput; set { _lastInput = value; OnProp("LastInput"); } }
        
        public DateTime StartTime { get; set; }
        private string _durStr = "00:00";
        public string DurationStr { get => _durStr; set { _durStr = value; OnProp("DurationStr"); } }

        public void UpdateDuration() 
        {
            if(IsLive) DurationStr = (DateTime.Now - StartTime).ToString(@"mm\:ss");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnProp(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class UiLogger : ILogger
    {
        private readonly Action<string, string> _logAction;
        public UiLogger(Action<string, string> action) => _logAction = action;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _logAction(logLevel.ToString(), formatter(state, exception));
        }
    }
}