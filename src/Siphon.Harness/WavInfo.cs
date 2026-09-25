using System.Buffers.Binary;

namespace Siphon.Harness;

/// <summary>
/// A deliberately independent RIFF/WAVE reader (not NAudio), so the harness checks what is
/// actually on disk: header sizes versus real file length, and sample data.
/// </summary>
internal sealed class WavInfo
{
    public required string Path { get; init; }
    public long FileLength { get; init; }
    public uint RiffSize { get; init; }
    public ushort FormatTag { get; init; }
    public ushort Channels { get; init; }
    public int SampleRate { get; init; }
    public ushort BitsPerSample { get; init; }
    public ushort BlockAlign { get; init; }
    public long DataOffset { get; init; }
    public uint DataSizeInHeader { get; init; }

    public long DataBytesOnDisk => FileLength - DataOffset;
    public long HeaderFrames => DataSizeInHeader / BlockAlign;
    public double HeaderSeconds => (double)HeaderFrames / SampleRate;
    public double DiskSeconds => (double)(DataBytesOnDisk / BlockAlign) / SampleRate;
    public bool IsFloat => FormatTag == 3 || (FormatTag == 0xFFFE && _subFormatTag == 3);

    private ushort _subFormatTag;

    public static WavInfo Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[12];
        fs.ReadExactly(head);
        if (head[0..4] is not [(byte)'R', (byte)'I', (byte)'F', (byte)'F'] || head[8..12] is not [(byte)'W', (byte)'A', (byte)'V', (byte)'E'])
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));

        byte[]? fmt = null;
        var chunkHeader = new byte[8];
        while (fs.Position + 8 <= fs.Length)
        {
            fs.ReadExactly(chunkHeader);
            string id = System.Text.Encoding.ASCII.GetString(chunkHeader, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            if (id == "fmt ")
            {
                fmt = new byte[size];
                fs.ReadExactly(fmt);
            }
            else if (id == "data")
            {
                if (fmt is null)
                    throw new InvalidDataException("data chunk before fmt chunk.");
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                return new WavInfo
                {
                    Path = path,
                    FileLength = fs.Length,
                    RiffSize = riffSize,
                    FormatTag = tag,
                    Channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2)),
                    SampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4)),
                    BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12)),
                    BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14)),
                    DataOffset = fs.Position,
                    DataSizeInHeader = size,
                    _subFormatTag = tag == 0xFFFE && fmt.Length >= 26 ? BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24)) : (ushort)0,
                };
            }
            else
            {
                fs.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        throw new InvalidDataException("No data chunk found.");
    }

    /// <summary>Per-frame peak (max |sample| over all channels), for the frames the header declares.</summary>
    public float[] ReadFramePeaks()
    {
        if (!IsFloat || BitsPerSample != 32)
            throw new NotSupportedException("Harness analysis expects 32-bit float files.");

        var peaks = new float[HeaderFrames];
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = DataOffset;
        var buffer = new byte[BlockAlign * 4096];
        long frame = 0;
        while (frame < peaks.Length)
        {
            int want = (int)Math.Min(4096, peaks.Length - frame) * BlockAlign;
            fs.ReadExactly(buffer, 0, want);
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, want));
            for (int i = 0; i < samples.Length; i += Channels, frame++)
            {
                float p = 0;
                for (int c = 0; c < Channels; c++)
                    p = Math.Max(p, Math.Abs(samples[i + c]));
                peaks[frame] = p;
            }
        }
        return peaks;
    }

    public void Print()
    {
        string kind = IsFloat ? "float" : "PCM";
        Console.WriteLine($"  file:        {Path}");
        Console.WriteLine($"  format:      {SampleRate} Hz, {BitsPerSample}-bit {kind}, {Channels} ch (tag 0x{FormatTag:X4})");
        Console.WriteLine($"  header says: {HeaderSeconds:F3} s ({DataSizeInHeader:N0} data bytes, RIFF size {RiffSize:N0})");
        Console.WriteLine($"  on disk:     {DiskSeconds:F3} s ({DataBytesOnDisk:N0} data bytes, file {FileLength:N0})");
        bool consistent = DataSizeInHeader == DataBytesOnDisk && RiffSize == FileLength - 8;
        Console.WriteLine(consistent
            ? "  header:      consistent with file length"
            : $"  header:      lags file by {(DataBytesOnDisk - DataSizeInHeader) / (double)BlockAlign / SampleRate:F3} s (expected after a kill)");
    }
}
