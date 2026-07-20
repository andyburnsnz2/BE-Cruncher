using System.Windows;
using System.Windows.Threading;
using BE_Cruncher.Pages;
using BE_Cruncher.Services;

namespace BE_Cruncher
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var splash = new SplashWindow();
            splash.Show();

            // AppServices() itself is fast (directory creation + config load), so hold the splash
            // visible for a minimum duration on top of that — otherwise it would just flash by.
            var minimumVisible = Task.Delay(6100);
            var services = await Task.Run(() => new AppServices());
            await minimumVisible;

            var mainWindow = new MainWindow(services);
            MainWindow = mainWindow;
            mainWindow.Show();
            splash.Close();
        }
    }
}
