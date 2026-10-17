using System.Buffers.Binary;
using SplitTrack.Common.Audio;

namespace SplitTrack.Mux.Wav;

/// <summary>
/// RIFF/WAVE writer with a WAVE_FORMAT_EXTENSIBLE header. Sizes start at their maximum and are
/// patched on close, so a file cut short by a crash still plays to its last byte.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderSize = 68;
    private const int RiffSizeOffset = 4;
    private const int DataSizeOffset = 64;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private static readonly byte[] Zeros = new byte[64 * 1024];

    private readonly FileStream _stream;
    private bool _disposed;

    public WavWriter(string path, AudioFormat format)
    {
        Path = path;
        Format = format;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 256 * 1024);
        _stream.Write(BuildHeader(format));
    }

    public string Path { get; }

    public AudioFormat Format { get; }

    public long DataBytes { get; private set; }

    public long Frames => DataBytes / Format.BlockAlign;

    public void Write(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        DataBytes += data.Length;
    }

    public void WriteSilence(long frames)
    {
        long remaining = frames * Format.BlockAlign;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, Zeros.Length);
            _stream.Write(Zeros, 0, chunk);
            remaining -= chunk;
        }

        DataBytes += frames * Format.BlockAlign;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Over 4 GB the max values stay; players then read to end of file.
        Span<byte> size = stackalloc byte[4];
        if (DataBytes + HeaderSize - 8 <= uint.MaxValue)
        {
            _stream.Position = RiffSizeOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(DataBytes + HeaderSize - 8));
            _stream.Write(size);

            _stream.Position = DataSizeOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)DataBytes);
            _stream.Write(size);
        }

        _stream.Dispose();
    }

    private static byte[] BuildHeader(AudioFormat format)
    {
        var header = new byte[HeaderSize];
        Span<byte> h = header;

        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], uint.MaxValue);
        "WAVE"u8.CopyTo(h[8..]);

        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 40);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], WaveFormatExtensible);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)format.BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)format.BitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 22);
        BinaryPrimitives.WriteUInt16LittleEndian(h[38..], (ushort)format.BitsPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], format.EffectiveChannelMask);
        format.SubType.TryWriteBytes(h[44..]);

        "data"u8.CopyTo(h[60..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[64..], uint.MaxValue - HeaderSize);

        return header;
    }
}
