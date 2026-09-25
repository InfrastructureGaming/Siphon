using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Siphon.Core.Capture;

/// <summary>Captures everything the default output device plays, via WASAPI loopback.</summary>
public sealed class SystemLoopbackSource : ICaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private int _stoppedRaised;

    public SystemLoopbackSource()
        : this(GetDefaultRenderDevice())
    {
    }

    public SystemLoopbackSource(MMDevice device)
    {
        _device = device;
        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public string DeviceId => _device.ID;

    public string DeviceName => _device.FriendlyName;

    /// <summary>The device mix format. Usually 48 kHz float stereo, but never assume it.</summary>
    public WaveFormat Format => _capture.WaveFormat;

    public event CaptureDataHandler? DataAvailable;

    public event Action<Exception?>? Stopped;

    public static MMDevice GetDefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Start() => _capture.StartRecording();

    public void Stop() => _capture.StopRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // NAudio already zeroes packets flagged AUDCLNT_BUFFERFLAGS_SILENT, so there are no flags to pass on.
        if (e.BytesRecorded > 0)
            DataAvailable?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded), CaptureBufferFlags.None);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 0)
            Stopped?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _device.Dispose();
    }
}
