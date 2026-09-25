using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Siphon.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        var viewModel = new MainViewModel(settings);
        MainWindow = new MainWindow(viewModel, settings);
        MainWindow.Show();
        viewModel.Initialize();
    }
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
