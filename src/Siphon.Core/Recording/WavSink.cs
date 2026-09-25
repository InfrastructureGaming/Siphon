using NAudio.Wave;

namespace Siphon.Core.Recording;

/// <summary>
/// Writes captured audio to a WAV file. Frames in, frames out: the sink accepts buffers in the
/// source format and is responsible for any conversion to the file format.
/// </summary>
public sealed class WavSink : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly byte[] Zeros = new byte[64 * 1024];

    private readonly WaveFileWriter _writer;
    private readonly int _blockAlign;
    private bool _disposed;

    public WavSink(string path, WaveFormat sourceFormat)
    {
        Path = path;
        SourceFormat = sourceFormat;
        FileFormat = ChooseFileFormat(sourceFormat);
        _blockAlign = FileFormat.BlockAlign;
        _writer = new WaveFileWriter(path, FileFormat);
    }

    public string Path { get; }

    public WaveFormat SourceFormat { get; }

    public WaveFormat FileFormat { get; }

    public long FramesWritten { get; private set; }

    /// <summary>Size of the data chunk in bytes.</summary>
    public long BytesWritten => _writer.Length;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FramesWritten / FileFormat.SampleRate);

    /// <summary>Writes whole frames from <paramref name="buffer"/>, which is in the source format.</summary>
    public void Write(byte[] buffer, int frames)
    {
        _writer.Write(buffer, 0, frames * _blockAlign);
        FramesWritten += frames;
    }

    public void WriteSilence(long frames)
    {
        long remaining = frames * _blockAlign;
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, Zeros.Length);
            _writer.Write(Zeros, 0, n);
            remaining -= n;
        }
        FramesWritten += frames;
    }

    /// <summary>Rewrites the RIFF/data sizes in the header so the file is valid up to this point.</summary>
    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writer.Dispose();
    }

    public static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);

    private static WaveFormat ChooseFileFormat(WaveFormat source)
    {
        // Mono/stereo get a plain header, which every tool reads. Multichannel keeps the
        // extensible header so the speaker mask survives.
        if (source.Channels > 2)
            return source;
        return IsFloat(source)
            ? WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, source.Channels)
            : new WaveFormat(source.SampleRate, source.BitsPerSample, source.Channels);
    }
}
