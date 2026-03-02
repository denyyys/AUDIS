using System;
using System.Threading;
using System.Windows;
using System.IO;

namespace AudisService
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex = null;
        private const string AppName = "AudisService_KyblEnterprise_Mutex";

        protected override void OnStartup(StartupEventArgs e)
        {
            // Add global exception handler
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                File.WriteAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "audis_crash.txt"),
                    $"CRASH at {DateTime.Now}\n\n{ex.ExceptionObject}"
                );
                System.Windows.MessageBox.Show($"CRASH: {ex.ExceptionObject}", "Audis Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            this.DispatcherUnhandledException += (s, ex) =>
            {
                File.WriteAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "audis_crash.txt"),
                    $"UI CRASH at {DateTime.Now}\n\n{ex.Exception}"
                );
                System.Windows.MessageBox.Show($"UI CRASH: {ex.Exception.Message}\n\n{ex.Exception.StackTrace}", "Audis Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            bool createdNew;
            _mutex = new Mutex(true, AppName, out createdNew);

            if (!createdNew)
            {
                System.Windows.MessageBox.Show("Audis is already running!", "Kybl Enterprise", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                this.Shutdown();
                return;
            }

            try
            {
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                File.WriteAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "audis_startup_crash.txt"),
                    $"STARTUP CRASH at {DateTime.Now}\n\n{ex}"
                );
                System.Windows.MessageBox.Show($"Startup failed: {ex.Message}", "Audis Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
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