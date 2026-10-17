using SplitTrack.FFmpeg;

namespace SplitTrack.Buffer;

/// <param name="Ticks">The packet's time in the clip, in QPC ticks (negative for an audio packet that covers the start).</param>
public readonly record struct ClipPacket(int Stream, MediaPacket Packet, long Ticks);

/// <summary>Packets cut from a <see cref="ReplayBuffer"/>, ordered by time; owns them until disposed.</summary>
/// <param name="StartTicks">The first keyframe, in ticks from the session start; the clip's time 0.</param>
/// <param name="EndTicks">The requested end, in ticks from the session start.</param>
public sealed class ReplayClip(long startTicks, long endTicks, IReadOnlyList<ClipPacket> packets) : IDisposable
{
    public long StartTicks { get; } = startTicks;

    public long EndTicks { get; } = endTicks;

    public long DurationTicks => EndTicks - StartTicks;

    public IReadOnlyList<ClipPacket> Packets { get; } = packets;

    public void Dispose()
    {
        foreach (ClipPacket packet in Packets)
        {
            packet.Packet.Dispose();
        }
    }
}
