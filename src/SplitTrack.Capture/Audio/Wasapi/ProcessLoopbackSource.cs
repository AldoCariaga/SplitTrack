using System.Runtime.InteropServices;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>
/// WASAPI process loopback: the audio rendered by one process tree (include) or by everything
/// except it (exclude), independent of which output device it plays on.
/// </summary>
public sealed class ProcessLoopbackSource : IAudioSource
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly IAudioClient _client;
    private readonly WasapiCaptureLoop _loop;
    private bool _disposed;

    private ProcessLoopbackSource(string name, int processId, bool includeTree, IAudioClient client, AudioFormat format)
    {
        Name = name;
        ProcessId = processId;
        IncludeTree = includeTree;
        Format = format;
        _client = client;
        _loop = new WasapiCaptureLoop(name, client, format);
    }

    public string Name { get; }

    public int ProcessId { get; }

    public bool IncludeTree { get; }

    public AudioFormat Format { get; }

    public Exception? Fault => _loop.Fault;

    /// <summary>
    /// Opens the stream in <paramref name="preferred"/> format, falling back to 16-bit 44.1 kHz
    /// (the format of Microsoft's sample) if Windows rejects it.
    /// </summary>
    public static ProcessLoopbackSource Open(string name, int processId, bool includeTree, AudioFormat preferred) =>
        ComApartment.RunInMta(() =>
        {
            AudioFormat[] candidates = preferred == AudioFormat.Pcm16Stereo44
                ? [preferred]
                : [preferred, AudioFormat.Pcm16Stereo44];

            COMException? lastError = null;
            foreach (AudioFormat format in candidates)
            {
                IAudioClient client = Activate(processId, includeTree);
                try
                {
                    Initialize(client, format);
                    Log.Info($"{name}: process loopback pid {processId} ({(includeTree ? "include" : "exclude")} tree), {format}");
                    return new ProcessLoopbackSource(name, processId, includeTree, client, format);
                }
                catch (COMException ex)
                {
                    Marshal.ReleaseComObject(client);
                    Log.Warn($"{name}: Initialize rejected {format} (0x{ex.HResult:X8})");
                    lastError = ex;
                }
            }

            throw new NotSupportedException($"{name}: process loopback refused every format.", lastError);
        });

    public void Start(IAudioSink sink) => _loop.Start(sink);

    public void Stop() => _loop.Stop();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loop.Dispose();
        Marshal.ReleaseComObject(_client);
    }

    private static unsafe IAudioClient Activate(int processId, bool includeTree)
    {
        var parameters = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        };
        parameters.ProcessLoopbackParams.TargetProcessId = (uint)processId;
        parameters.ProcessLoopbackParams.ProcessLoopbackMode = includeTree
            ? PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            : PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;

        var activationParams = default(PROPVARIANT);
        activationParams.vt = VARENUM.VT_BLOB;
        activationParams.blob.cbSize = (uint)sizeof(AUDIOCLIENT_ACTIVATION_PARAMS);
        activationParams.blob.pBlobData = (byte*)&parameters;

        // Referenced until WaitFor returns, as the activation contract requires.
        var handler = new ActivationHandler();
        PInvoke.ActivateAudioInterfaceAsync(
                PInvoke.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                typeof(IAudioClient).GUID,
                activationParams,
                handler,
                out IActivateAudioInterfaceAsyncOperation operation)
            .ThrowOnFailure();

        try
        {
            return handler.WaitFor<IAudioClient>(ActivationTimeout);
        }
        finally
        {
            Marshal.ReleaseComObject(operation);
            GC.KeepAlive(handler);
        }
    }

    private static unsafe void Initialize(IAudioClient client, AudioFormat format)
    {
        WAVEFORMATEXTENSIBLE native = WaveFormatInterop.ToNative(format);
        client.Initialize(
            AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
            PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK | PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            WasapiCaptureLoop.BufferDurationTicks,
            0,
            (WAVEFORMATEX*)&native,
            null);
    }
}
