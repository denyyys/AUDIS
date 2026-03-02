using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace AudisService
{
    public partial class CallLogWindow : Window
    {
        private readonly ObservableCollection<CallLogEntry> _source;
        private ObservableCollection<CallLogRowVm> _rows = new();

        public CallLogWindow(ObservableCollection<CallLogEntry> source)
        {
            InitializeComponent();
            _source = source;
            Refresh();

            // Auto-update when new calls come in
            _source.CollectionChanged += (_, _) => Dispatcher.Invoke(Refresh);
        }

        private void Refresh()
        {
            _rows.Clear();
            int idx = 1;
            foreach (var e in _source.OrderByDescending(x => x.StartTime))
            {
                _rows.Add(new CallLogRowVm(e) { RowIndex = idx++ });
            }
            CallGrid.ItemsSource = _rows;
            TxtCount.Text = $"{_source.Count} call{(_source.Count == 1 ? "" : "s")}";
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                string file = Path.Combine(logsDir, $"audis_calllog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                var sb = new StringBuilder();
                sb.AppendLine(new string('=', 70));
                sb.AppendLine($"AUDIS SIP CALL LOG — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine(new string('=', 70));
                sb.AppendLine();
                sb.AppendLine($"{"#",-4} {"Extension",-20} {"Call ID",-36} {"Start",-10} {"End",-10} {"Duration",-10} {"Status",-10}");
                sb.AppendLine(new string('-', 100));

                int i = 1;
                foreach (var entry in _source.OrderBy(x => x.StartTime))
                {
                    sb.AppendLine($"{i,-4} {entry.Extension,-20} {entry.CallId,-36} {entry.StartTime:HH:mm:ss}  {entry.EndTime?.ToString("HH:mm:ss") ?? "—",-10} {entry.DurationStr,-10} {entry.Status}");
                    i++;
                }

                File.WriteAllText(file, sb.ToString());
                MessageBox.Show($"Call log exported:\n{file}", "Export",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                Process.Start("explorer.exe", logsDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Clear call log?", "Confirm", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _source.Clear();
                Refresh();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }

    public class CallLogRowVm
    {
        private readonly CallLogEntry _e;
        public int    RowIndex  { get; set; }
        public string CallId    => _e.CallId;
        public string Extension => string.IsNullOrEmpty(_e.Extension) ? "(unknown)" : _e.Extension;
        public string StartTimeStr => _e.StartTimeStr;
        public string EndTimeStr   => _e.EndTimeStr;
        public string DurationStr  => _e.DurationStr;
        public string Status       => _e.Status;

        public CallLogRowVm(CallLogEntry e) { _e = e; }
    }
}
