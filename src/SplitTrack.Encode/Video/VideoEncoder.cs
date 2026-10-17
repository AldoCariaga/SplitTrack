using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SplitTrack.Common.Diagnostics;
using SplitTrack.FFmpeg;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;

namespace SplitTrack.Encode.Video;

/// <summary>
/// H.264 encoder fed from FFmpeg's pool of D3D11 NV12 textures. The caller draws into the texture
/// that <see cref="BeginFrame"/> returns and then calls <see cref="EncodeFrame"/>. AMF and NVENC
/// read the texture directly, QSV maps it (zero copy), x264 downloads it.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    // Hardware encoders hold several input frames in flight; the pool cannot grow.
    private const int PoolFrames = 20;

    // D3D11_BIND_DECODER: the bind flag every encoder accepts and that NV12 texture arrays allow.
    private const uint BindDecoder = 0x200;

    // Peak bitrate allowed above the target, for busy scenes.
    private const int PeakPercent = 150;

    private AVBufferRef* _d3dDevice;
    private AVBufferRef* _d3dFrames;
    private AVBufferRef* _qsvDevice;
    private AVBufferRef* _qsvFrames;
    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private AVFrame* _converted;
    private AVPacket* _packet;
    private bool _frameReady;

    private VideoEncoder(VideoEncoderKind kind, VideoEncoderSettings settings)
    {
        Kind = kind;
        Settings = settings;
        TimeBase = new AVRational { num = 1, den = settings.Fps };
    }

    public VideoEncoderKind Kind { get; }

    public VideoEncoderSettings Settings { get; }

    /// <summary>One tick per frame: pts is the frame index on the recording's timeline.</summary>
    public AVRational TimeBase { get; }

    public static VideoEncoder Open(ID3D11Device device, VideoEncoderKind kind, VideoEncoderSettings settings)
    {
        var encoder = new VideoEncoder(kind, settings);
        try
        {
            encoder.Initialize(device);
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the encoder and pushes a few frames through it. Null when that works; otherwise why
    /// not. Opening alone is not enough: some drivers fail only on the first frame.
    /// </summary>
    public static string? Test(ID3D11Device device, VideoEncoderKind kind, VideoEncoderSettings settings)
    {
        try
        {
            using VideoEncoder encoder = Open(device, kind, settings);
            int packets = 0;
            for (int i = 0; i < 3; i++)
            {
                encoder.BeginFrame();
                encoder.EncodeFrame(i, p =>
                {
                    packets++;
                    p.Dispose();
                });
            }

            encoder.Flush(p =>
            {
                packets++;
                p.Dispose();
            });
            return packets > 0 ? null : "no output";
        }
        catch (Exception ex)
        {
            Log.Debug($"{kind.CodecName()} test failed: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>The first kind in <see cref="VideoEncoderKinds.AutoOrder"/> that passes <see cref="Test"/> at these settings.</summary>
    public static VideoEncoderKind SelectAuto(ID3D11Device device, uint gpuVendorId, VideoEncoderSettings settings)
    {
        var failures = new List<string>();
        foreach (VideoEncoderKind kind in VideoEncoderKinds.AutoOrder(gpuVendorId))
        {
            string? error = Test(device, kind, settings);
            if (error is null)
            {
                Log.Info($"Auto encoder: {kind.ShortName()}");
                return kind;
            }

            failures.Add($"{kind.ShortName()}: {error}");
        }

        throw new InvalidOperationException($"No H.264 encoder works here ({string.Join("; ", failures)}).");
    }

    /// <summary>Takes a free NV12 texture slice from the pool for the next frame.</summary>
    public (nint Texture, uint Slice) BeginFrame()
    {
        ffmpeg.av_frame_unref(_frame);
        FFmpegException.Check(ffmpeg.av_hwframe_get_buffer(_d3dFrames, _frame, 0), "av_hwframe_get_buffer");
        _frameReady = true;
        return ((nint)_frame->data[0], (uint)(nint)_frame->data[1]);
    }

    /// <summary>Encodes the frame from <see cref="BeginFrame"/> and hands out every packet that is ready.</summary>
    public void EncodeFrame(long pts, Action<MediaPacket> output)
    {
        if (!_frameReady)
        {
            throw new InvalidOperationException("BeginFrame must be called first.");
        }

        _frameReady = false;
        AVFrame* input = _frame;
        try
        {
            switch (Kind)
            {
                case VideoEncoderKind.Qsv:
                    ffmpeg.av_frame_unref(_converted);
                    _converted->format = (int)AVPixelFormat.AV_PIX_FMT_QSV;
                    _converted->hw_frames_ctx = ffmpeg.av_buffer_ref(_qsvFrames);
                    FFmpegException.Check(ffmpeg.av_hwframe_map(_converted, _frame, (int)AvHwframeMap.AV_HWFRAME_MAP_READ), "av_hwframe_map");
                    input = _converted;
                    break;
                case VideoEncoderKind.X264:
                    ffmpeg.av_frame_unref(_converted);
                    _converted->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                    FFmpegException.Check(ffmpeg.av_hwframe_transfer_data(_converted, _frame, 0), "av_hwframe_transfer_data");
                    input = _converted;
                    break;
            }

            input->pts = pts;
            input->duration = 1;
            Send(input, output);
        }
        finally
        {
            // The encoder took its own reference; the texture returns to the pool when it is done.
            ffmpeg.av_frame_unref(_frame);
            ffmpeg.av_frame_unref(_converted);
        }
    }

    /// <summary>Drains the encoder at the end of a recording.</summary>
    public void Flush(Action<MediaPacket> output) => Send(null, output);

    public MediaStreamInfo CreateStreamInfo(string title) => new(_codec, title);

    public void Dispose()
    {
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        AVFrame* converted = _converted;
        ffmpeg.av_frame_free(&converted);
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        AVBufferRef* buffer = _qsvFrames;
        ffmpeg.av_buffer_unref(&buffer);
        buffer = _qsvDevice;
        ffmpeg.av_buffer_unref(&buffer);
        buffer = _d3dFrames;
        ffmpeg.av_buffer_unref(&buffer);
        buffer = _d3dDevice;
        ffmpeg.av_buffer_unref(&buffer);
        _packet = null;
        _frame = null;
        _converted = null;
        _codec = null;
        _qsvFrames = null;
        _qsvDevice = null;
        _d3dFrames = null;
        _d3dDevice = null;
    }

    private void Initialize(ID3D11Device device)
    {
        string name = Kind.CodecName();
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null)
        {
            throw new FFmpegException($"This FFmpeg build has no {name} encoder.", ffmpeg.AVERROR_ENCODER_NOT_FOUND);
        }

        CreateD3DFrames(device);

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        _codec->width = Settings.Width;
        _codec->height = Settings.Height;
        _codec->time_base = TimeBase;
        _codec->framerate = new AVRational { num = Settings.Fps, den = 1 };
        _codec->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        _codec->gop_size = Settings.Fps * Settings.GopSeconds;
        _codec->max_b_frames = 0;
        _codec->bit_rate = Settings.BitrateKbps * 1000L;
        _codec->rc_max_rate = Settings.BitrateKbps * 1000L * PeakPercent / 100;
        _codec->rc_buffer_size = (int)Math.Min(int.MaxValue, Settings.BitrateKbps * 1000L * PeakPercent / 100);
        _codec->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _codec->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        _codec->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _codec->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        switch (Kind)
        {
            case VideoEncoderKind.Qsv:
                AVBufferRef* qsvDevice;
                FFmpegException.Check(ffmpeg.av_hwdevice_ctx_create_derived(&qsvDevice, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, _d3dDevice, 0), "Deriving a QSV device");
                _qsvDevice = qsvDevice;
                AVBufferRef* qsvFrames;
                FFmpegException.Check(
                    ffmpeg.av_hwframe_ctx_create_derived(
                        &qsvFrames, AVPixelFormat.AV_PIX_FMT_QSV, _qsvDevice, _d3dFrames,
                        (int)(AvHwframeMap.AV_HWFRAME_MAP_READ | AvHwframeMap.AV_HWFRAME_MAP_WRITE)),
                    "Deriving QSV frames");
                _qsvFrames = qsvFrames;
                _codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_QSV;
                _codec->hw_frames_ctx = ffmpeg.av_buffer_ref(_qsvFrames);
                break;
            case VideoEncoderKind.X264:
                _codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                break;
            default:
                _codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _codec->hw_frames_ctx = ffmpeg.av_buffer_ref(_d3dFrames);
                break;
        }

        AVDictionary* options = null;
        try
        {
            foreach ((string key, string value) in EncoderOptions(Kind))
            {
                ffmpeg.av_dict_set(&options, key, value, 0);
            }

            FFmpegException.Check(ffmpeg.avcodec_open2(_codec, codec, &options), $"Opening {name}");

            AVDictionaryEntry* unused = null;
            while ((unused = ffmpeg.av_dict_get(options, string.Empty, unused, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                Log.Debug($"{name} ignored option {Marshal.PtrToStringUTF8((nint)unused->key)}");
            }
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        _frame = ffmpeg.av_frame_alloc();
        _converted = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        Log.Info($"Opened {name}: {Settings.Width}x{Settings.Height} @ {Settings.Fps} fps, {Settings.BitrateKbps} kbps, GOP {_codec->gop_size}");
    }

    private void CreateD3DFrames(ID3D11Device device)
    {
        _d3dDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        var deviceContext = (AVHWDeviceContext*)_d3dDevice->data;
        var d3d = (AVD3D11VADeviceContext*)deviceContext->hwctx;

        // FFmpeg releases this reference when the device context is freed.
        device.AddRef();
        d3d->device = (global::FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
        FFmpegException.Check(ffmpeg.av_hwdevice_ctx_init(_d3dDevice), "av_hwdevice_ctx_init (D3D11)");

        _d3dFrames = ffmpeg.av_hwframe_ctx_alloc(_d3dDevice);
        var frames = (AVHWFramesContext*)_d3dFrames->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        frames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        frames->width = Settings.Width;
        frames->height = Settings.Height;
        frames->initial_pool_size = PoolFrames;
        ((AVD3D11VAFramesContext*)frames->hwctx)->BindFlags = BindDecoder;
        FFmpegException.Check(ffmpeg.av_hwframe_ctx_init(_d3dFrames), "av_hwframe_ctx_init (D3D11 NV12)");
    }

    // Peak-constrained VBR everywhere. Strict CBR pads frames to size, which the MKV muxer strips
    // again: wasted encoder bandwidth and replay-buffer memory.
    private static IEnumerable<(string Key, string Value)> EncoderOptions(VideoEncoderKind kind) => kind switch
    {
        VideoEncoderKind.Amf => [("usage", "transcoding"), ("quality", "balanced"), ("rc", "vbr_peak"), ("profile", "high")],
        VideoEncoderKind.Nvenc => [("preset", "p4"), ("tune", "hq"), ("rc", "vbr"), ("profile", "high")],
        VideoEncoderKind.Qsv => [("preset", "faster"), ("profile", "high")],
        VideoEncoderKind.X264 => [("preset", "veryfast"), ("profile", "high")],
        _ => [],
    };

    private void Send(AVFrame* frame, Action<MediaPacket> output)
    {
        FFmpegException.Check(ffmpeg.avcodec_send_frame(_codec, frame), "avcodec_send_frame");
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            FFmpegException.Check(result, "avcodec_receive_packet");
            output(MediaPacket.MoveFrom(_packet, TimeBase));
        }
    }
}
