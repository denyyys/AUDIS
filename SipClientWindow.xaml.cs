using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using Brushes   = System.Windows.Media.Brushes;
using Color     = System.Windows.Media.Color;

namespace AudisService
{
    public partial class SipClientWindow : Window
    {
        private readonly SipClientEngine _engine = new();
        private readonly ObservableCollection<string> _logs = new();
        private readonly DispatcherTimer _callTimer = new();
        private DateTime _callStart;
        private bool _inCall = false;

        public AudisConfig? ServerConfig
        {
            set => _engine.ServerConfig = value;
        }

        public SipClientConfig ClientConfig
        {
            set
            {
                _engine.Stop();
                _engine.Start(value);
            }
        }

        public SipClientEngine Engine => _engine;

        public SipClientWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _logs;

            _engine.OnLog                      += OnEngineLog;
            _engine.OnCallStateChanged         += OnCallStateChanged;
            _engine.OnRegistrationStateChanged += OnRegistrationStateChanged;
            _engine.OnDtmfReceived             += d => Dispatcher.Invoke(() => AddLog($"↙ DTMF from remote: {d}"));

            _engine.Start(SipClientConfig.Load());
            AddLog($"SIP Client ready — port {SipClientConfig.Load().LocalSipPort}");
            AddLog("Enter SIP URI and press Call, e.g.:  500@192.168.1.5");

            // Populate contacts dropdown
            RefreshContactItems();

            // Restore audio mode selection from persisted config
            LoadAudioModeFromConfig();

            // Show/hide Test Registration button based on config
            UpdateTestRegButton();

            _callTimer.Interval = TimeSpan.FromSeconds(1);
            _callTimer.Tick += (_, _) =>
            {
                if (_inCall)
                    TxtCallTimer.Text = (DateTime.Now - _callStart).ToString(@"mm\:ss");
            };
        }

        // ── Contacts ─────────────────────────────────────────────────────────────

        public void RefreshContactItems()
        {
            var cfg = SipClientConfig.Load();
            var current = TxtNumber.Text;
            TxtNumber.Items.Clear();
            foreach (var c in cfg.Contacts)
                TxtNumber.Items.Add(new ContactItem { Name = c.Name, Extension = c.Extension });
            TxtNumber.Text = current;
        }

        private void UpdateTestRegButton()
        {
            var cfg = SipClientConfig.Load();
            BtnTestReg.IsEnabled = cfg.UseRegistration;
        }

        private string GetCurrentNumber()
        {
            if (TxtNumber.SelectedItem is ContactItem ci)
                return ci.Extension;
            return TxtNumber.Text?.Trim() ?? "";
        }

        // ── Dialpad ──────────────────────────────────────────────────────────────
        private void DialpadBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            string digit = btn.Tag?.ToString() ?? "";

            if (_inCall)
            {
                if (digit.Length == 1)
                {
                    _engine.SendDtmf(digit[0]);
                    AddLog($"↗ DTMF sent: {digit}");
                }
            }
            else
            {
                TxtNumber.Text += digit;
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!_inCall)
            {
                TxtNumber.SelectedItem = null;
                TxtNumber.Text = "";
            }
        }

        private void TxtNumber_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Return) BtnCall_Click(sender, e);
        }

        // ── Test Registration ─────────────────────────────────────────────────────
        private void BtnTestReg_Click(object sender, RoutedEventArgs e)
        {
            AddLog("Testing registration...");
            _ = _engine.TestRegistrationAsync();
        }

        // ── Call control ─────────────────────────────────────────────────────────
        private void BtnCall_Click(object sender, RoutedEventArgs e)
        {
            if (_inCall) return;

            string target = GetCurrentNumber();
            if (string.IsNullOrEmpty(target))
            {
                MessageBox.Show("Enter a SIP address to call.\n\nExamples:\n  192.168.1.5\n  500@192.168.1.5\n  sip:200@pbx.example.com",
                                "Audis SIP Client", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AddLog($"Dialling: {target}");
            _ = _engine.CallAsync(target);
        }

        private void BtnHangup_Click(object sender, RoutedEventArgs e)
        {
            _engine.HangUp();
        }

        // ── Engine callbacks ─────────────────────────────────────────────────────
        private void OnEngineLog(LogLevel lvl, string msg)
            => Dispatcher.Invoke(() => AddLog($"[{lvl.ToString()[..3].ToUpper()}] {msg}"));

        private void OnCallStateChanged(ClientCallState state)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = state.ToString().ToUpper();

                TxtStatus.Foreground = state switch
                {
                    ClientCallState.Connected => Brushes.LightGreen,
                    ClientCallState.Calling   => Brushes.Yellow,
                    ClientCallState.Ending    => Brushes.Orange,
                    ClientCallState.Ended     => Brushes.OrangeRed,
                    _                         => new SolidColorBrush(Color.FromRgb(0x69, 0xF0, 0xAE))
                };

                _inCall = state == ClientCallState.Connected || state == ClientCallState.Calling;

                BtnCall.IsEnabled   = !_inCall;
                BtnHangup.IsEnabled =  _inCall;

                if (state == ClientCallState.Connected)
                {
                    _callStart = DateTime.Now;
                    _callTimer.Start();
                }
                else if (state == ClientCallState.Idle || state == ClientCallState.Ended)
                {
                    _callTimer.Stop();
                    TxtCallTimer.Text  = "";
                    TxtStatus.Text     = state == ClientCallState.Ended ? "ENDED" : "IDLE";
                }
            });
        }

        private void OnRegistrationStateChanged(RegistrationState state, int code)
        {
            Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case RegistrationState.Registered:
                        RegDot.Fill           = Brushes.LimeGreen;
                        TxtRegStatus.Text     = "Online";
                        TxtRegStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                        TxtRegCode.Text       = "(200 OK)";
                        break;

                    case RegistrationState.Registering:
                        RegDot.Fill           = Brushes.Orange;
                        TxtRegStatus.Text     = "Registering…";
                        TxtRegStatus.Foreground = Brushes.DarkOrange;
                        TxtRegCode.Text       = "";
                        break;

                    case RegistrationState.Failed:
                        RegDot.Fill           = Brushes.Red;
                        TxtRegStatus.Text     = "Failed";
                        TxtRegStatus.Foreground = Brushes.DarkRed;
                        TxtRegCode.Text       = code > 0 ? $"({code})" : "(no response)";
                        break;

                    case RegistrationState.Unregistered:
                        RegDot.Fill           = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17));
                        TxtRegStatus.Text     = "Unregistered";
                        TxtRegStatus.Foreground = Brushes.DarkOrange;
                        TxtRegCode.Text       = "";
                        break;

                    default:
                        RegDot.Fill           = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                        TxtRegStatus.Text     = "Disabled";
                        TxtRegStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
                        TxtRegCode.Text       = "";
                        break;
                }
            });
        }

        // ── Log ──────────────────────────────────────────────────────────────────
        private void AddLog(string msg)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (_logs.Count > 300) _logs.RemoveAt(0);
            _logs.Add(entry);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
            => _logs.Clear();

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                string file = Path.Combine(logsDir, $"audis_client_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllLines(file, _logs);
                AddLog($"Exported → {file}");
                MessageBox.Show($"SIP Client log exported:\n{file}", "Export",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Process.Start("explorer.exe", logsDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool ForceClose { get; set; }

        public void ExportCallLog()
        {
            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                string file = Path.Combine(logsDir, $"audis_calllog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                var callLines = _logs.Where(l =>
                    l.Contains("[CALL]")    ||
                    l.Contains("Dialling")  ||
                    l.Contains("INVITE")    ||
                    l.Contains("CANCEL")    ||
                    l.Contains("HangUp")    ||
                    l.Contains("Connected") ||
                    l.Contains("Ended")     ||
                    l.Contains("Calling")   ||
                    l.Contains("Registered")||
                    l.Contains("[OPTIONS]")
                ).ToList();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(new string('=', 70));
                sb.AppendLine($"AUDIS SIP CALL LOG — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Extension: {_engine.Config.Username}@{_engine.Config.SipServer}");
                sb.AppendLine(new string('=', 70));
                sb.AppendLine();

                if (callLines.Count == 0)
                    sb.AppendLine("(No call events recorded this session)");
                else
                    foreach (var line in callLines) sb.AppendLine(line);

                File.WriteAllText(file, sb.ToString());
                AddLog($"Call log exported → {file}");
                System.Diagnostics.Process.Start("explorer.exe", logsDir);
                MessageBox.Show($"Call log exported:\n{file}", "Call Log",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Lifecycle ────────────────────────────────────────────────────────────
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ForceClose)
            {
                _engine.Stop();
                return;
            }
            e.Cancel = true;
            Hide();
        }

        public void ApplyConfig(SipClientConfig cfg)
        {
            _engine.Stop();
            _engine.Start(cfg);
            AddLog($"Configuration applied — port {cfg.LocalSipPort}");
            UpdateTestRegButton();
            RefreshContactItems();
            LoadAudioModeFromConfig();
        }

        // ── Audio Mode ───────────────────────────────────────────────────────────

        /// <summary>Reads persisted audio mode and reflects it in the UI controls.</summary>
        public void LoadAudioModeFromConfig()
        {
            var cfg = SipClientConfig.Load();
            // Populate wav dropdown before touching radio buttons
            RefreshWavList(cfg.CustomWav);

            // Prevent the Checked handlers from firing Save during init
            _suppressModeChange = true;
            try
            {
                if (cfg.AudioMode == SipAudioMode.CustomWav)
                {
                    RadioCustomWav.IsChecked = true;
                    CboCustomWav.IsEnabled   = true;
                }
                else
                {
                    RadioStandard.IsChecked = true;
                    CboCustomWav.IsEnabled  = false;
                }

                ChkRecordCalls.IsChecked = cfg.RecordCalls;
            }
            finally { _suppressModeChange = false; }
        }

        private bool _suppressModeChange = false;

        private void RefreshWavList(string? selectFile = null)
        {
            CboCustomWav.Items.Clear();
            foreach (var f in _engine.GetAudioFiles())
                CboCustomWav.Items.Add(f);

            if (selectFile != null && CboCustomWav.Items.Contains(selectFile))
                CboCustomWav.SelectedItem = selectFile;
            else if (CboCustomWav.Items.Count > 0)
                CboCustomWav.SelectedIndex = 0;
        }

        private void AudioMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressModeChange) return;
            CboCustomWav.IsEnabled = RadioCustomWav.IsChecked == true;
            PersistAudioMode();
        }

        private void CboCustomWav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModeChange) return;
            PersistAudioMode();
        }

        private void BtnRefreshWavs_Click(object sender, RoutedEventArgs e)
        {
            var cfg = SipClientConfig.Load();
            RefreshWavList(cfg.CustomWav);
        }

        private void ChkRecordCalls_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressModeChange) return;
            PersistAudioMode();
        }

        /// <summary>Persists current UI selection to disk and updates the live engine.</summary>
        private void PersistAudioMode()
        {
            var mode = RadioCustomWav.IsChecked == true
                ? SipAudioMode.CustomWav
                : SipAudioMode.Standard;
            var wav    = CboCustomWav.SelectedItem?.ToString() ?? "";
            var record = ChkRecordCalls.IsChecked == true;

            // Update live engine config and save to sip_client_config.json
            _engine.Config.RecordCalls = record;
            _engine.SetAudioMode(mode, wav);   // this calls Config.Save()

            AddLog(mode == SipAudioMode.CustomWav
                ? $"Audio mode → Custom .wav: {wav}"
                : "Audio mode → Standard (AUDIS)");
            AddLog(record ? "Recording → ON" : "Recording → OFF");
        }
    }

    public class ContactItem
    {
        public string Name      { get; set; } = "";
        public string Extension { get; set; } = "";
        public string DisplayText => string.IsNullOrEmpty(Name) ? Extension : $"{Name} — {Extension}";

        public override string ToString() => DisplayText;
    }
}
