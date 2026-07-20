using System.Windows;
using BE_Cruncher.Pages;
using BE_Cruncher.Services;

namespace BE_Cruncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(AppServices services)
        {
            InitializeComponent();
            DarkTitleBar.Apply(this);
            MainFrame.Navigate(new VersionsPage(services));
        }
    }
}
