using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SplitTrack.Common.Audio;

/// <summary>Highest sample magnitude since the last <see cref="TakePeak"/>; written and read on different threads.</summary>
public sealed class PeakMeter(SampleType sampleType)
{
    private int _peakBits;

    public void Process(ReadOnlySpan<byte> data)
    {
        float peak = Measure(data);
        int current = Volatile.Read(ref _peakBits);
        while (peak > BitConverter.Int32BitsToSingle(current))
        {
            int seen = Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(peak), current);
            if (seen == current)
            {
                break;
            }

            current = seen;
        }
    }

    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    public static double ToDbfs(float peak) => peak <= 1e-6f ? -120 : 20 * Math.Log10(peak);

    private float Measure(ReadOnlySpan<byte> data)
    {
        float peak = 0;
        switch (sampleType)
        {
            case SampleType.Float32:
                foreach (float s in MemoryMarshal.Cast<byte, float>(data))
                {
                    peak = Math.Max(peak, Math.Abs(s));
                }

                return peak;

            case SampleType.Pcm16:
                foreach (short s in MemoryMarshal.Cast<byte, short>(data))
                {
                    peak = Math.Max(peak, Math.Abs(s / 32768f));
                }

                return peak;

            case SampleType.Pcm24:
                for (int i = 0; i + 2 < data.Length; i += 3)
                {
                    int s = (data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) << 8 >> 8;
                    peak = Math.Max(peak, Math.Abs(s / 8388608f));
                }

                return peak;

            default:
                foreach (int s in MemoryMarshal.Cast<byte, int>(data))
                {
                    peak = Math.Max(peak, Math.Abs(s / 2147483648f));
                }

                return peak;
        }
    }
}
