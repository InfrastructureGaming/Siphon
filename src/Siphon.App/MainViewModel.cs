using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Siphon.Core.Capture;
using Siphon.Core.Recording;

namespace Siphon.App;

public sealed record SourceOption(string Name, string Glyph);

public sealed partial class MainViewModel : ObservableObject
{
    private const double MeterReleaseSeconds = 0.3;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _tickClock = new();
    private SystemLoopbackSource? _source;
    private LevelMeter? _meter;
    private CaptureSession? _session;
    private Task _finished = Task.CompletedTask;
    private double _meterLeft;
    private double _meterRight;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        IsPinned = settings.AlwaysOnTop;
        OutputFolder = settings.OutputFolder;
        if (settings.LastFilePath is { } last && File.Exists(last))
            LastFilePath = last;

        Sources = [new SourceOption("System audio", "")];
        SelectedSource = Sources[0];

        // ~30 fps: meter ballistics and the running timer.
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += OnTick;
    }

    public IReadOnlyList<SourceOption> Sources { get; }

    [ObservableProperty]
    public partial SourceOption SelectedSource { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool StatusIsNotice { get; set; }

    [ObservableProperty]
    public partial double LevelLeft { get; set; }

    [ObservableProperty]
    public partial double LevelRight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFileName), nameof(HasLastFile))]
    public partial string? LastFilePath { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>Where new recordings go. Changes apply to the next recording.</summary>
    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    public string? LastFileName => LastFilePath is null ? null : Path.GetFileName(LastFilePath);

    public bool HasLastFile => LastFilePath is not null;

    /// <summary>False when Windows "Show animations" is off; the lamp glow is then steady.</summary>
    public bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public void Initialize()
    {
        StartMonitoring();
        _tickClock.Start();
        _tick.Start();
    }

    /// <summary>Finalizes any recording in progress. Every exit path must await this.</summary>
    public async Task ShutdownAsync()
    {
        _tick.Stop();
        if (_session is not null)
            await _session.StopAsync();
        await _finished;
        StopMonitoring();
        _settings.Save();
    }

    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (_session is not null)
            await _session.StopAsync();
        else
            StartRecording();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (LastFilePath is { } path && File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
            return;
        }
        Directory.CreateDirectory(OutputFolder);
        Process.Start("explorer.exe", $"\"{OutputFolder}\"");
    }

    public void OpenLastFile()
    {
        if (LastFilePath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ChangeOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where Siphon saves recordings",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            OutputFolder = dialog.FolderName;
    }

    partial void OnIsPinnedChanged(bool value)
    {
        _settings.AlwaysOnTop = value;
        _settings.Save();
    }

    partial void OnOutputFolderChanged(string value)
    {
        _settings.OutputFolder = value;
        _settings.Save();
    }

    private void StartMonitoring()
    {
        try
        {
            _source = new SystemLoopbackSource();
            _meter = new LevelMeter(_source);
            _source.Stopped += OnMonitorStopped;
            _source.Start();
        }
        catch (Exception ex)
        {
            StopMonitoring();
            ShowNotice($"Couldn't open the output device ({ex.Message}). Check a playback device is enabled.");
        }
    }

    private void StopMonitoring()
    {
        if (_source is not null)
            _source.Stopped -= OnMonitorStopped;
        _meter?.Dispose();
        _source?.Dispose();
        _meter = null;
        _source = null;
    }

    private void OnMonitorStopped(Exception? error)
    {
        // Device-change handling proper arrives in Phase 5. Until then, drop the dead source;
        // the next record press reopens the (new) default device.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            StopMonitoring();
            if (_session is null)
                ShowNotice("Output device unavailable. Press record to try again.");
        });
    }

    private void StartRecording()
    {
        if (_source is null)
        {
            StartMonitoring();
            if (_source is null)
                return;
        }

        CaptureSession session;
        try
        {
            string path = RecordingNames.NewRecordingPath(OutputFolder, null, DateTime.Now);
            session = new CaptureSession(_source, new WavSink(path, _source.Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice($"Couldn't create a file in {OutputFolder}. Check the folder exists and is writable.");
            return;
        }

        _session = session;
        session.Start();
        IsRecording = true;
        StatusText = "";
        StatusIsNotice = false;
        _finished = FinishWhenDoneAsync(session);
    }

    private async Task FinishWhenDoneAsync(CaptureSession session)
    {
        RecordingResult result = await session.Completion;
        _session = null;
        IsRecording = false;
        ElapsedText = "00:00:00";
        LastFilePath = result.Path;
        _settings.LastFilePath = result.Path;
        _settings.Save();

        switch (result.Reason)
        {
            case StopReason.User:
                StatusText = "Ready";
                StatusIsNotice = false;
                break;
            case StopReason.SizeLimit:
                ShowNotice("Recording stopped at the file size limit.");
                break;
            case StopReason.SourceStopped:
                ShowNotice($"Capture stopped ({result.Error?.Message}). Recording saved.");
                break;
            case StopReason.WriteFailed:
                ShowNotice($"Couldn't write to disk ({result.Error?.Message}). Everything up to that point was saved.");
                break;
        }
    }

    private void ShowNotice(string text)
    {
        StatusText = text;
        StatusIsNotice = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double dt = _tickClock.Elapsed.TotalSeconds;
        _tickClock.Restart();

        var (left, right) = _meter?.ReadAndReset() ?? (0f, 0f);
        double release = Math.Exp(-dt / MeterReleaseSeconds);
        _meterLeft = Math.Max(left, _meterLeft * release);
        _meterRight = Math.Max(right, _meterRight * release);
        LevelLeft = _meterLeft < 1e-4 ? 0 : _meterLeft;
        LevelRight = _meterRight < 1e-4 ? 0 : _meterRight;

        if (_session is { } session)
            ElapsedText = FormatElapsed(session.Elapsed);
    }

    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
