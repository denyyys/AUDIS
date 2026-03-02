using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace AudisService
{
    public partial class ServerConfigWindow : Window
    {
        public AudisConfig Config { get; private set; }

        public ServerConfigWindow(AudisConfig existing)
        {
            InitializeComponent();
            Config = existing;
            LoadToUi();
        }

        private void LoadToUi()
        {
            TxtPublicIp.Text     = Config.PublicIp;
            TxtPort.Text         = Config.Port.ToString();
            TxtWeatherCity.Text  = Config.WeatherCity;
            TxtLat.Text          = Config.WeatherLat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtLong.Text         = Config.WeatherLong.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtOllamaModel.Text  = Config.OllamaModel;
        }

        private void BtnIpHelp_Click(object sender, RoutedEventArgs e)
            => MessageBox.Show(
                "This IP is used in SIP Contact headers.\n\n" +
                "Behind NAT: set to your PC's LAN IP (e.g. 192.168.100.64).\n" +
                "Public server: set to your WAN IP.",
                "Public IP Help", MessageBoxButton.OK, MessageBoxImage.Question);

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Invalid SIP port.", "Validation Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Config.PublicIp    = TxtPublicIp.Text.Trim();
            Config.Port        = port;
            Config.WeatherCity = TxtWeatherCity.Text.Trim();
            Config.OllamaModel = TxtOllamaModel.Text.Trim();

            if (double.TryParse(TxtLat.Text,  System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double lat))
                Config.WeatherLat = lat;
            if (double.TryParse(TxtLong.Text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double lng))
                Config.WeatherLong = lng;

            // Persist to disk so settings survive restarts
            Config.Save();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
