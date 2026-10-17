using System.Globalization;
using System.Text.Json;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Library;

namespace SplitTrack.Export;

/// <summary>One audio track of the source, named from the sidecar when there is one, else from the file.</summary>
/// <param name="Stream">Stream index in the file.</param>
/// <param name="Number">1-based position among the audio tracks.</param>
/// <param name="Kind">game, app, microphone or system (from the sidecar); null when unknown.</param>
/// <param name="Source">The exe or device it captured last (from the sidecar).</param>
public sealed record SourceTrack(int Stream, int Number, string Title, string? Kind, string? Source);

/// <summary>A recording or clip to export: the video file, its sidecar if any, and its tracks.</summary>
public sealed class ExportSource
{
    private ExportSource(string videoPath, Sidecar? sidecar, MediaInfo media, IReadOnlyList<SourceTrack> tracks)
    {
        VideoPath = videoPath;
        Sidecar = sidecar;
        Media = media;
        Tracks = tracks;
    }

    public string VideoPath { get; }

    public Sidecar? Sidecar { get; }

    public MediaInfo Media { get; }

    public IReadOnlyList<SourceTrack> Tracks { get; }

    /// <summary>Opens a video file or its .json sidecar.</summary>
    /// <exception cref="ExportException">The file or the video a sidecar points to does not exist.</exception>
    public static ExportSource Open(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new ExportException($"{path} does not exist.");
        }

        Sidecar? sidecar;
        string videoPath;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            sidecar = TryReadSidecar(path) ?? throw new ExportException($"{path} is not a SplitTrack sidecar.");
            videoPath = Path.Combine(Path.GetDirectoryName(path)!, sidecar.File);
            if (!File.Exists(videoPath))
            {
                throw new ExportException($"The video of {Path.GetFileName(path)} is missing: {videoPath}");
            }
        }
        else
        {
            videoPath = path;
            string sidecarPath = EntryPaths.ForVideo(path).Sidecar;
            sidecar = File.Exists(sidecarPath) ? TryReadSidecar(sidecarPath) : null;
        }

        MediaInfo media = MediaInfo.Probe(videoPath);
        return new ExportSource(videoPath, sidecar, media, NameTracks(media, sidecar));
    }

    /// <summary>The track with this title (case-insensitive), or else with this 1-based number.</summary>
    public SourceTrack? FindTrack(string spec)
    {
        spec = spec.Trim();
        return Tracks.FirstOrDefault(t => t.Title.Equals(spec, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                ? Tracks.FirstOrDefault(t => t.Number == n)
                : null);
    }

    /// <summary>Next to the source with its name, e.g. 20261018_140322_clip.mp4 (.m4a without video); never an existing file.</summary>
    public string DefaultOutputPath()
    {
        string extension = Media.Video is null ? ".m4a" : ".mp4";
        string stem = Path.Combine(Path.GetDirectoryName(VideoPath)!, Path.GetFileNameWithoutExtension(VideoPath));
        for (int n = 1; ; n++)
        {
            string path = (n == 1 ? stem : $"{stem}_{n}") + extension;
            if (!File.Exists(path) && !File.Exists(path + ".partial"))
            {
                return path;
            }
        }
    }

    private static List<SourceTrack> NameTracks(MediaInfo media, Sidecar? sidecar)
    {
        var tracks = new List<SourceTrack>();
        foreach (AudioStreamInfo stream in media.Audio)
        {
            SidecarTrack? described = sidecar?.AudioTracks?.FirstOrDefault(t => t.Stream == stream.Index);
            string title = described?.Title is { Length: > 0 } t ? t : stream.Title ?? $"Track {stream.Number}";
            tracks.Add(new SourceTrack(stream.Index, stream.Number, title, described?.Kind, described?.Source));
        }

        return tracks;
    }

    // A broken sidecar only costs the track kinds; the titles are in the MKV too.
    private static Sidecar? TryReadSidecar(string path)
    {
        try
        {
            return LibraryFolder.ReadSidecar(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Ignoring sidecar {path}", ex);
            return null;
        }
    }
}

public sealed class ExportException(string message) : Exception(message);
