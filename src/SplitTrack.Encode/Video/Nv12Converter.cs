using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SplitTrack.Encode.Video;

/// <summary>
/// BGRA → NV12 (BT.709, limited range) with optional scaling, on the GPU's fixed-function video
/// processor, then copied into one slice of FFmpeg's texture-array frame pool. The processor needs
/// a render target and D3D11 refuses NV12 render-target arrays, hence our own texture plus a copy.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext1 _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _output;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private readonly VideoProcessorStream[] _streams = [new VideoProcessorStream { Enable = true }];
    private ID3D11VideoProcessorInputView? _inputView;
    private nint _inputTexture;
    private ID3D11Resource? _pool;

    public Nv12Converter(ID3D11Device device, int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps)
    {
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;

        _context = device.ImmediateContext;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext1>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _output = device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, (uint)outputWidth, (uint)outputHeight, arraySize: 1, mipLevels: 1, BindFlags.RenderTarget));
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });

        var source = new RawRect(0, 0, inputWidth, inputHeight);
        var target = new RawRect(0, 0, outputWidth, outputHeight);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, source);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, target);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, target);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
    }

    public int InputWidth { get; }

    public int InputHeight { get; }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    /// <summary>Queues the conversion on the immediate context; later GPU work on the pool slice sees the result.</summary>
    public void Convert(ID3D11Texture2D input, nint poolTexture, uint poolSlice)
    {
        _streams[0].InputSurface = GetInputView(input);
        _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, _streams).CheckError();
        _context.CopySubresourceRegion(GetPool(poolTexture), poolSlice, 0, 0, 0, _output, 0);
    }

    public void Dispose()
    {
        _pool?.Dispose();
        _inputView?.Dispose();
        _outputView.Dispose();
        _output.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D input)
    {
        if (_inputView is not null && _inputTexture == input.NativePointer)
        {
            return _inputView;
        }

        _inputView?.Dispose();
        _inputView = _videoDevice.CreateVideoProcessorInputView(input, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        _inputTexture = input.NativePointer;
        return _inputView;
    }

    private ID3D11Resource GetPool(nint texture)
    {
        if (_pool is not null && _pool.NativePointer == texture)
        {
            return _pool;
        }

        // Borrowed from FFmpeg's pool: take our own reference for as long as we hold the wrapper.
        _pool?.Dispose();
        _pool = new ID3D11Resource(texture);
        _pool.AddRef();
        return _pool;
    }
}
