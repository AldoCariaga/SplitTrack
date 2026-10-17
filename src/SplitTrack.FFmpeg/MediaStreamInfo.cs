using FFmpeg.AutoGen;

namespace SplitTrack.FFmpeg;

/// <summary>What a muxer needs to declare one stream: a copy of the encoder's parameters and its time base.</summary>
public sealed unsafe class MediaStreamInfo : IDisposable
{
    private AVCodecParameters* _parameters;

    public MediaStreamInfo(AVCodecContext* encoder, string title)
    {
        _parameters = ffmpeg.avcodec_parameters_alloc();
        FFmpegException.Check(ffmpeg.avcodec_parameters_from_context(_parameters, encoder), "avcodec_parameters_from_context");
        TimeBase = encoder->time_base;
        FrameRate = encoder->framerate;
        Title = title;
    }

    ~MediaStreamInfo() => Free();

    public AVCodecParameters* Parameters => _parameters != null ? _parameters : throw new ObjectDisposedException(nameof(MediaStreamInfo));

    public AVRational TimeBase { get; }

    public AVRational FrameRate { get; }

    public string Title { get; }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        AVCodecParameters* parameters = _parameters;
        _parameters = null;
        if (parameters != null)
        {
            ffmpeg.avcodec_parameters_free(&parameters);
        }
    }
}
