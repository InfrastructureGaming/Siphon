using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Siphon.Core.Capture;

namespace Siphon.Core.Recording;

public enum StopReason
{
    User,
    SizeLimit,
    SourceStopped,
    WriteFailed,
}

public sealed record RecordingResult(string Path, TimeSpan Duration, StopReason Reason, Exception? Error);

/// <summary>
/// Records one running <see cref="ICaptureSource"/> into one <see cref="WavSink"/>.
/// The capture callback only copies into a channel; a writer task drains it, fills gaps with
/// silence so the file tracks wall-clock time, flushes the header every second, and finalizes.
/// Every way a recording can end goes through <see cref="RequestStop"/> and the writer's finalize step.
/// </summary>
public sealed class CaptureSession
{
    public const long DefaultSizeLimitBytes = 3_800_000_000;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    // Gaps smaller than this are delivery jitter, not silence.
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMilliseconds(20);

    // No packet for this long means the source has gone quiet. NAudio's loopback capture
    // polls every ~50 ms, so this must comfortably exceed that. It is also how far behind
    // real time the silence padding stays during a gap, so that audio resuming mid-gap
    // is never overlapped by padding already written.
    private static readonly TimeSpan GapDetect = TimeSpan.FromMilliseconds(100);

    private readonly record struct Chunk(byte[] Buffer, int Length, bool Silent, long Timestamp);

    private readonly ICaptureSource _source;
    private readonly WavSink _sink;
    private readonly long _sizeLimitBytes;
    private readonly int _sampleRate;
    private readonly int _blockAlign;
    private readonly long _gapThresholdTicks = SecondsToTicks(GapThreshold);
    private readonly long _gapDetectTicks = SecondsToTicks(GapDetect);
    private readonly Channel<Chunk> _channel = Channel.CreateUnbounded<Chunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource<RecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _stopLock = new();

    private StopReason? _stopReason;
    private Exception? _error;
    private long _stopFrames = -1;
    private int _started;

    public CaptureSession(ICaptureSource source, WavSink sink, long sizeLimitBytes = DefaultSizeLimitBytes)
    {
        _source = source;
        _sink = sink;
        _sizeLimitBytes = sizeLimitBytes;
        _sampleRate = source.Format.SampleRate;
        _blockAlign = source.Format.BlockAlign;
    }

    public string Path => _sink.Path;

    /// <summary>Wall-clock time since the recording started.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Completes once the file is finalized, however the recording ended.</summary>
    public Task<RecordingResult> Completion => _completion.Task;

    /// <summary>Attaches to the (already running) source and starts writing.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A session can only be started once.");

        _clock.Start();
        _source.Stopped += OnSourceStopped;
        _source.DataAvailable += OnData;
        _ = Task.Run(WriterLoopAsync);
    }

    public Task<RecordingResult> StopAsync()
    {
        RequestStop(StopReason.User, null);
        return Completion;
    }

    private void RequestStop(StopReason reason, Exception? error)
    {
        lock (_stopLock)
        {
            if (_stopReason is not null)
                return;
            _stopReason = reason;
            _error = error;
            Volatile.Write(ref _stopFrames, TicksToFrames(_clock.ElapsedTicks));
        }

        _source.DataAvailable -= OnData;
        _source.Stopped -= OnSourceStopped;
        _channel.Writer.TryComplete();
    }

    // Capture thread: copy and go.
    private void OnData(ReadOnlySpan<byte> data, CaptureBufferFlags flags)
    {
        long timestamp = _clock.ElapsedTicks;
        bool silent = (flags & CaptureBufferFlags.Silent) != 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        if (!silent)
            data.CopyTo(buffer);
        if (!_channel.Writer.TryWrite(new Chunk(buffer, data.Length, silent, timestamp)))
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private void OnSourceStopped(Exception? error) =>
        RequestStop(StopReason.SourceStopped, error ?? new InvalidOperationException("Audio capture stopped unexpectedly."));

    private async Task WriterLoopAsync()
    {
        ChannelReader<Chunk> reader = _channel.Reader;
        long lastArrival = 0;
        long lastFlush = 0;
        Task<bool>? waitForData = null;

        try
        {
            while (true)
            {
                while (reader.TryRead(out Chunk chunk))
                {
                    try
                    {
                        WriteChunk(chunk, lastArrival);
                        lastArrival = chunk.Timestamp;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk.Buffer);
                    }
                }

                // Source is quiet: keep the file moving with silence, staying GapDetect behind
                // real time so a packet that arrives now can't end up overlapping the padding.
                long now = _clock.ElapsedTicks;
                if (now - lastArrival > _gapDetectTicks)
                    PadTo(TicksToFrames(now - _gapDetectTicks));

                if (now - lastFlush >= SecondsToTicks(FlushInterval))
                {
                    _sink.Flush();
                    lastFlush = now;
                }

                if (_sink.BytesWritten >= _sizeLimitBytes)
                    RequestStop(StopReason.SizeLimit, null);

                waitForData ??= reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(waitForData, Task.Delay(TickInterval)) == waitForData)
                {
                    bool more = await waitForData;
                    waitForData = null;
                    if (!more)
                        break;
                }
            }

            // A recording that ends in silence still runs to the moment Stop was pressed.
            PadTo(Volatile.Read(ref _stopFrames));
        }
        catch (Exception ex)
        {
            RequestStop(StopReason.WriteFailed, ex);
            while (reader.TryRead(out Chunk chunk))
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
        }
        finally
        {
            FinalizeRecording();
        }
    }

    private void WriteChunk(Chunk chunk, long lastArrival)
    {
        int frames = chunk.Length / _blockAlign;

        // After a genuine gap in delivery, pad up to where this packet begins. During steady
        // streaming we never pad: jitter and device-vs-system clock drift would otherwise
        // insert small dropouts into continuous audio.
        if (chunk.Timestamp - lastArrival > _gapDetectTicks)
        {
            long packetStart = TicksToFrames(chunk.Timestamp) - frames;
            if (packetStart - _sink.FramesWritten > TicksToFrames(_gapThresholdTicks))
                PadTo(packetStart);
        }

        long stopFrames = Volatile.Read(ref _stopFrames);
        if (stopFrames >= 0)
            frames = (int)Math.Clamp(stopFrames - _sink.FramesWritten, 0, frames);
        if (frames == 0)
            return;

        if (chunk.Silent)
            _sink.WriteSilence(frames);
        else
            _sink.Write(chunk.Buffer, frames);
    }

    private void PadTo(long targetFrames)
    {
        long stopFrames = Volatile.Read(ref _stopFrames);
        if (stopFrames >= 0)
            targetFrames = Math.Min(targetFrames, stopFrames);
        long missing = targetFrames - _sink.FramesWritten;
        if (missing > 0)
            _sink.WriteSilence(missing);
    }

    private void FinalizeRecording()
    {
        _clock.Stop();
        try
        {
            _sink.Dispose();
        }
        catch (Exception ex)
        {
            _error ??= ex;
            _stopReason ??= StopReason.WriteFailed;
        }

        _completion.TrySetResult(new RecordingResult(_sink.Path, _sink.Duration, _stopReason ?? StopReason.User, _error));
    }

    private long TicksToFrames(long stopwatchTicks) => stopwatchTicks * _sampleRate / Stopwatch.Frequency;

    private static long SecondsToTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}
