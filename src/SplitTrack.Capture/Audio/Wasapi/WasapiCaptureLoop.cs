using System.Runtime.InteropServices;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>
/// Event-driven capture thread shared by every WASAPI source. Wakes on the buffer event or every
/// 100 ms, drains all packets, and reports idleness so the aligner can pad silent sources.
/// </summary>
internal sealed unsafe class WasapiCaptureLoop : IDisposable
{
    public const long BufferDurationTicks = 2_000_000; // 200 ms: survives a GC pause

    private const int WaitTimeoutMs = 100;
    private static readonly long MaxTimestampSkewTicks = QpcClock.MillisecondsToTicks(2_000);

    private readonly string _name;
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AudioFormat _format;
    private readonly AutoResetEvent _bufferEvent = new(false);

    private Thread? _thread;
    private IAudioSink? _sink;
    private volatile bool _stopping;

    public WasapiCaptureLoop(string name, IAudioClient client, AudioFormat format)
    {
        _name = name;
        _client = client;
        _format = format;

        _client.SetEventHandle(new HANDLE((void*)_bufferEvent.SafeWaitHandle.DangerousGetHandle()));

        Guid iid = typeof(IAudioCaptureClient).GUID;
        _client.GetService(&iid, out void* ppv);
        try
        {
            _capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown((nint)ppv);
        }
        finally
        {
            Marshal.Release((nint)ppv);
        }
    }

    public Exception? Fault { get; private set; }

    public void Start(IAudioSink sink)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException($"{_name} is already running.");
        }

        _sink = sink;
        _stopping = false;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Audio capture: {_name}" };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopping = true;
        _bufferEvent.Set();
        _thread.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        Marshal.ReleaseComObject(_capture);
        _bufferEvent.Dispose();
    }

    private void Run()
    {
        uint taskIndex = 0;
        using var mmcss = PInvoke.AvSetMmThreadCharacteristics("Audio", ref taskIndex);
        if (mmcss.IsInvalid)
        {
            Log.Warn($"{_name}: MMCSS 'Audio' registration failed (error {Marshal.GetLastWin32Error()})");
        }

        bool started = false;
        try
        {
            _client.Start();
            started = true;
        }
        catch (Exception ex)
        {
            SetFault(ex);
        }

        while (!_stopping)
        {
            bool signaled = _bufferEvent.WaitOne(WaitTimeoutMs);
            int packets = Fault is null ? Drain() : 0;
            if (packets == 0 && !signaled && !_stopping)
            {
                _sink!.OnIdle(QpcClock.Now);
            }
        }

        if (Fault is null)
        {
            Drain();
        }

        if (started)
        {
            try
            {
                _client.Stop();
            }
            catch (COMException ex)
            {
                Log.Debug($"{_name}: IAudioClient.Stop failed: 0x{ex.HResult:X8}");
            }
        }
    }

    private int Drain()
    {
        int count = 0;
        try
        {
            while (true)
            {
                _capture.GetNextPacketSize(out uint next);
                if (next == 0)
                {
                    return count;
                }

                byte* data;
                ulong qpcPosition;
                _capture.GetBuffer(&data, out uint frames, out uint flags, null, &qpcPosition);
                try
                {
                    if (frames > 0)
                    {
                        Deliver(data, (int)frames, flags, qpcPosition);
                        count++;
                    }
                }
                finally
                {
                    _capture.ReleaseBuffer(frames);
                }
            }
        }
        catch (Exception ex)
        {
            SetFault(ex);
            return count;
        }
    }

    private void Deliver(byte* data, int frames, uint flags, ulong qpcPosition)
    {
        bool silent = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
        bool discontinuity = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0;
        bool timestampError = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0;

        long now = QpcClock.Now;
        long ticks = (long)qpcPosition;

        // A missing or absurd timestamp is replaced by "the packet just finished".
        if (timestampError || ticks == 0 || Math.Abs(now - ticks) > MaxTimestampSkewTicks)
        {
            timestampError = true;
            ticks = now - QpcClock.FramesToTicks(frames, _format.SampleRate);
        }

        var span = silent ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(data, frames * _format.BlockAlign);
        _sink!.OnPacket(new AudioPacket(span, frames, ticks, silent, discontinuity, timestampError));
    }

    private void SetFault(Exception ex)
    {
        if (Fault is not null)
        {
            return;
        }

        Fault = ex;
        Log.Error($"{_name}: capture stopped, track continues as silence", ex);
    }
}
