using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Siphon.Core.Recording;

namespace Siphon.Core.Capture;

/// <summary>
/// Tracks the peak of the first two channels of a source, whether or not it is being recorded.
/// Peaks accumulate on the capture thread (one allocation-free pass per packet) and are
/// collected and reset by the UI via <see cref="ReadAndReset"/>. Mono sources report the
/// same peak on both sides.
/// </summary>
public sealed class LevelMeter : IDisposable
{
    private readonly ICaptureSource _source;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;

    // Non-negative floats order the same as their bit patterns, so these can be max-ed as ints.
    private int _peakLeftBits;
    private int _peakRightBits;

    public LevelMeter(ICaptureSource source)
    {
        _source = source;
        WaveFormat format = source.Format;
        _channels = format.Channels;
        _bytesPerSample = format.BitsPerSample / 8;
        _isFloat = WavSink.IsFloat(format);
        _source.DataAvailable += OnData;
    }

    /// <summary>Linear peaks (0..1+, full scale = 1) since the last call.</summary>
    public (float Left, float Right) ReadAndReset() =>
        (BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakLeftBits, 0)),
         BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakRightBits, 0)));

    private void OnData(ReadOnlySpan<byte> data, CaptureBufferFlags flags)
    {
        if ((flags & CaptureBufferFlags.Silent) != 0)
            return;

        float left, right;
        if (_isFloat && _bytesPerSample == 4)
            (left, right) = PeakFloat(MemoryMarshal.Cast<byte, float>(data));
        else if (!_isFloat && _bytesPerSample is 2 or 3 or 4)
            (left, right) = PeakPcm(data);
        else
            return;

        if (_channels == 1)
            right = left;
        StoreMax(ref _peakLeftBits, left);
        StoreMax(ref _peakRightBits, right);
    }

    private (float, float) PeakFloat(ReadOnlySpan<float> samples)
    {
        float l = 0, r = 0;
        int stride = _channels;
        for (int i = 0; i + stride <= samples.Length; i += stride)
        {
            l = MathF.Max(l, MathF.Abs(samples[i]));
            if (stride > 1)
                r = MathF.Max(r, MathF.Abs(samples[i + 1]));
        }
        return (l, r);
    }

    private (float, float) PeakPcm(ReadOnlySpan<byte> data)
    {
        int l = 0, r = 0;
        int frameBytes = _bytesPerSample * _channels;
        for (int i = 0; i + frameBytes <= data.Length; i += frameBytes)
        {
            l = Math.Max(l, Math.Abs(ReadSample(data[i..])));
            if (_channels > 1)
                r = Math.Max(r, Math.Abs(ReadSample(data[(i + _bytesPerSample)..])));
        }
        // Samples are normalized to 32-bit range, so full scale is 2^31.
        const float scale = 1f / 2147483648f;
        return (l * scale, r * scale);
    }

    // Clamped so Math.Abs can't overflow on a full-scale negative sample.
    private int ReadSample(ReadOnlySpan<byte> s) => Math.Max(-int.MaxValue, _bytesPerSample switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(s) << 16,
        3 => (s[0] << 8) | (s[1] << 16) | (s[2] << 24),
        _ => BinaryPrimitives.ReadInt32LittleEndian(s),
    });

    private static void StoreMax(ref int target, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        int current = Volatile.Read(ref target);
        while (bits > current)
        {
            int seen = Interlocked.CompareExchange(ref target, bits, current);
            if (seen == current)
                return;
            current = seen;
        }
    }

    public void Dispose() => _source.DataAvailable -= OnData;
}
