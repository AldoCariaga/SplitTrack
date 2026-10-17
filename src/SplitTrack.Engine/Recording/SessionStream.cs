using SplitTrack.FFmpeg;

namespace SplitTrack.Engine.Recording;

/// <summary>One stream of the session, as every file made from it declares it.</summary>
internal sealed record SessionStream(MediaStreamInfo Info, bool IsDefault);
