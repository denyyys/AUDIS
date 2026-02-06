using System;
using System.Threading;
using System.Windows;

namespace AudisService
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex = null;
        private const string AppName = "AudisService_KyblEnterprise_Mutex";

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _mutex = new Mutex(true, AppName, out createdNew);

            if (!createdNew)
            {
                System.Windows.MessageBox.Show("Audis is already running!", "Kybl Enterprise", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                this.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
