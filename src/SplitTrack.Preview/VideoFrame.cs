namespace SplitTrack.Preview;

/// <summary>One decoded picture as 32-bit BGRA, top row first; pooled, so give it back with <see cref="PreviewPlayer.Release"/>.</summary>
public sealed class VideoFrame
{
    internal VideoFrame(int width, int height)
    {
        Width = width;
        Height = height;

        // Pinned: swscale writes into it directly.
        Pixels = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    /// <summary>Seconds from the start of the file.</summary>
    public double Time { get; internal set; }
}
