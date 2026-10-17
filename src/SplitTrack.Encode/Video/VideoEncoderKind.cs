namespace SplitTrack.Encode.Video;

public enum VideoEncoderKind
{
    Amf,
    Nvenc,
    Qsv,
    X264,
}

public static class VideoEncoderKinds
{
    public const uint VendorAmd = 0x1002;
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorIntel = 0x8086;

    public static IReadOnlyList<VideoEncoderKind> All { get; } = Enum.GetValues<VideoEncoderKind>();

    /// <summary>FFmpeg encoder name.</summary>
    public static string CodecName(this VideoEncoderKind kind) => kind switch
    {
        VideoEncoderKind.Amf => "h264_amf",
        VideoEncoderKind.Nvenc => "h264_nvenc",
        VideoEncoderKind.Qsv => "h264_qsv",
        VideoEncoderKind.X264 => "libx264",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The name used on the command line and in settings.</summary>
    public static string ShortName(this VideoEncoderKind kind) => kind switch
    {
        VideoEncoderKind.Amf => "amf",
        VideoEncoderKind.Nvenc => "nvenc",
        VideoEncoderKind.Qsv => "qsv",
        VideoEncoderKind.X264 => "x264",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static VideoEncoderKind? Parse(string name) =>
        All.Cast<VideoEncoderKind?>().FirstOrDefault(k => k!.Value.ShortName().Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What "auto" tries, in order: the GPU vendor's own encoder, then x264 on the CPU. Media
    /// Foundation (h264_mf) is left out: its extradata makes the Matroska muxer fail.
    /// </summary>
    public static IReadOnlyList<VideoEncoderKind> AutoOrder(uint gpuVendorId)
    {
        var order = new List<VideoEncoderKind>();
        switch (gpuVendorId)
        {
            case VendorAmd:
                order.Add(VideoEncoderKind.Amf);
                break;
            case VendorNvidia:
                order.Add(VideoEncoderKind.Nvenc);
                break;
            case VendorIntel:
                order.Add(VideoEncoderKind.Qsv);
                break;
        }

        order.Add(VideoEncoderKind.X264);
        return order;
    }
}

public sealed record VideoEncoderSettings(int Width, int Height, int Fps, int BitrateKbps, int GopSeconds = 2)
{
    /// <summary>Output size for a capture scaled down to <paramref name="maxHeight"/>. Never upscales; both sides even, as NV12 requires.</summary>
    public static (int Width, int Height) ScaledSize(int width, int height, int? maxHeight)
    {
        if (maxHeight is not { } h || h >= height)
        {
            return (width, height);
        }

        int scaledWidth = (int)Math.Round((double)width * h / height);
        return (Math.Max(2, scaledWidth & ~1), Math.Max(2, h & ~1));
    }
}
