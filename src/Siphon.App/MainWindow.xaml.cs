using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Siphon.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private Point? _dragStart;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        RestorePosition();

        SourceInitialized += (_, _) => ApplyWindows11Frame();
        Loaded += (_, _) => RecordButton.Focus();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownComplete)
            return;

        // Never lose a file: finalize any recording before the window goes away.
        e.Cancel = true;
        IsEnabled = false;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        await _viewModel.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnSettingsOpened(object? sender, EventArgs e) => ChangeFolderButton.Focus();

    private void OnSettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        SettingsPopup.IsOpen = false;
        SettingsButton.Focus();
        e.Handled = true;
    }

    private void OnLastFileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _dragStart = null;
            _viewModel.OpenLastFile();
            return;
        }
        _dragStart = e.GetPosition(this);
    }

    private void OnLastFileMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStart = null;
            return;
        }

        Vector moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;
        if (_viewModel.LastFilePath is { } path && File.Exists(path))
            DragDrop.DoDragDrop(LastFileRow, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    private void RestorePosition()
    {
        if (_settings.WindowLeft is not { } left || _settings.WindowTop is not { } top)
            return;

        // Only restore if the window would still be reachable (monitors may have changed).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (screen.Contains(new Point(left + 40, top + 18)) && screen.Contains(new Point(left + Width - 40, top + 18)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    // On Windows 11, let DWM draw rounded corners and the border instead of our square frame.
    private void ApplyWindows11Frame()
    {
        if (Environment.OSVersion.Version.Build < 22000)
            return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int round = DwmwcpRound;
        int border = 0x004D4745; // COLORREF (BGR) for #45474D
        if (DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int)) == 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
            Frame.BorderThickness = new Thickness(0);
        }
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
