using System.Collections.ObjectModel;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace AudisService
{
    public partial class SipClientSettingsWindow : Window
    {
        public SipClientConfig Config { get; private set; }

        // Still kept for backward compat - not used for Test Registration anymore
        public SipClientWindow? LiveClientWindow { get; set; }

        private ObservableCollection<SipContact> _contacts = new();

        public SipClientSettingsWindow(SipClientConfig existing)
        {
            InitializeComponent();
            Config = existing;
            LoadToUi();
        }

        private void LoadToUi()
        {
            TxtSipPort.Text     = Config.LocalSipPort.ToString();
            TxtRtpStart.Text    = Config.RtpPortStart.ToString();
            TxtRtpEnd.Text      = Config.RtpPortEnd.ToString();
            TxtPublicIp.Text    = Config.PublicIp;

            ChkUseReg.IsChecked  = Config.UseRegistration;
            TxtSipServer.Text    = Config.SipServer;
            TxtSipProxy.Text     = Config.SipProxy;
            TxtUsername.Text     = Config.Username;
            TxtDomain.Text       = Config.Domain;
            PbPassword.Password  = Config.Password;
            TxtDisplayName.Text  = Config.DisplayName;

            CmbTransport.SelectedIndex = Config.Transport switch
            {
                SipTransportProtocol.TCP => 1,
                SipTransportProtocol.TLS => 2,
                _                        => 0
            };

            RbRfc2833.IsChecked = Config.DtmfMethod == DtmfMethod.Rfc2833;
            RbSipInfo.IsChecked = Config.DtmfMethod == DtmfMethod.SipInfo;
            RbPcmu.IsChecked    = Config.PreferredCodec == "PCMU";
            RbPcma.IsChecked    = Config.PreferredCodec == "PCMA";

            // Load contacts
            _contacts = new ObservableCollection<SipContact>(Config.Contacts);
            ContactsGrid.ItemsSource = _contacts;

            UpdateAccountEnabled();
        }

        private void ChkUseReg_Changed(object sender, RoutedEventArgs e)
            => UpdateAccountEnabled();

        private void UpdateAccountEnabled()
        {
            bool enabled = ChkUseReg.IsChecked == true;
            GrpAccount.IsEnabled  = enabled;
        }

        private void BtnAddContact_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewName.Text.Trim();
            string ext  = TxtNewExt.Text.Trim();

            if (string.IsNullOrEmpty(ext))
            {
                MessageBox.Show("Extension/number is required.", "Add Contact",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _contacts.Add(new SipContact { Name = name, Extension = ext });
            TxtNewName.Text = "";
            TxtNewExt.Text  = "";
        }

        private void BtnRemoveContact_Click(object sender, RoutedEventArgs e)
        {
            if (ContactsGrid.SelectedItem is SipContact selected)
                _contacts.Remove(selected);
            else
                MessageBox.Show("Select a contact row to remove.", "Remove Contact",
                                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var cfg = BuildConfigFromUi();
            if (cfg == null) return;

            Config = cfg;
            Config.Save();
            DialogResult = true;
            Close();
        }

        private SipClientConfig? BuildConfigFromUi()
        {
            if (!int.TryParse(TxtSipPort.Text,  out int sipPort)  || sipPort  < 1024 || sipPort  > 65535 ||
                !int.TryParse(TxtRtpStart.Text, out int rtpStart) || rtpStart < 1024 || rtpStart > 65535 ||
                !int.TryParse(TxtRtpEnd.Text,   out int rtpEnd)   || rtpEnd   < 1024 || rtpEnd   > 65535)
            {
                MessageBox.Show("Invalid port values. Ports must be between 1024 and 65535.",
                                "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            // Collect contacts from grid (including in-edit rows)
            var contacts = new System.Collections.Generic.List<SipContact>(_contacts);

            return new SipClientConfig
            {
                LocalSipPort   = sipPort,
                RtpPortStart   = rtpStart,
                RtpPortEnd     = rtpEnd,
                PublicIp       = TxtPublicIp.Text.Trim(),

                UseRegistration = ChkUseReg.IsChecked == true,
                SipServer      = TxtSipServer.Text.Trim(),
                SipProxy       = TxtSipProxy.Text.Trim(),
                Username       = TxtUsername.Text.Trim(),
                Domain         = TxtDomain.Text.Trim(),
                Password       = PbPassword.Password,
                DisplayName    = TxtDisplayName.Text.Trim(),

                Transport = CmbTransport.SelectedIndex switch
                {
                    1 => SipTransportProtocol.TCP,
                    2 => SipTransportProtocol.TLS,
                    _ => SipTransportProtocol.UDP
                },

                DtmfMethod     = RbSipInfo.IsChecked == true ? DtmfMethod.SipInfo : DtmfMethod.Rfc2833,
                PreferredCodec = RbPcma.IsChecked == true ? "PCMA" : "PCMU",
                Contacts       = contacts
            };
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
