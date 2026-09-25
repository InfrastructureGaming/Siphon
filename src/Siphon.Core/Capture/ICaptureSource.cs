using NAudio.Wave;

namespace Siphon.Core.Capture;

[Flags]
public enum CaptureBufferFlags
{
    None = 0,

    /// <summary>The packet should be treated as digital silence regardless of its contents.</summary>
    Silent = 1,
}

/// <summary>
/// Raised on the capture thread. Handlers must copy what they need and return immediately.
/// The span is only valid for the duration of the call.
/// </summary>
public delegate void CaptureDataHandler(ReadOnlySpan<byte> data, CaptureBufferFlags flags);

/// <summary>
/// A running stream of captured audio. Sources are started once and run independently of
/// any recording, so the level meter can preview them while idle; a <see cref="Recording.CaptureSession"/>
/// attaches to a running source for the duration of a recording.
/// </summary>
public interface ICaptureSource : IDisposable
{
    WaveFormat Format { get; }

    event CaptureDataHandler? DataAvailable;

    /// <summary>Raised once when capture ends, with the exception if it ended abnormally.</summary>
    event Action<Exception?>? Stopped;

    void Start();

    void Stop();
}
