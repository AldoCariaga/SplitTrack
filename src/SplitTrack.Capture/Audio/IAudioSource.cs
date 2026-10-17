using SplitTrack.Common.Audio;

namespace SplitTrack.Capture.Audio;

/// <summary>An initialised capture stream; <see cref="Format"/> is final before <see cref="Start"/>.</summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }

    AudioFormat Format { get; }

    /// <summary>Set when the stream died mid-capture (device removed, etc.); the source then only idles.</summary>
    Exception? Fault { get; }

    void Start(IAudioSink sink);

    void Stop();
}
