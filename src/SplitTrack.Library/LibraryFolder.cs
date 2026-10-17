using System.Text.Json;

namespace SplitTrack.Library;

/// <summary>The files of one library entry, all sharing one base name.</summary>
public sealed record EntryPaths(string Video, string Sidecar, string Thumbnail)
{
    /// <summary>Where the video is written until it is complete.</summary>
    public string PartialVideo => Video + ".partial";

    public static EntryPaths ForVideo(string videoPath) =>
        new(videoPath, Path.ChangeExtension(videoPath, ".json"), Path.ChangeExtension(videoPath, ".jpg"));
}

/// <summary>The folder recordings and clips are saved to, one MKV + JSON sidecar + JPEG each.</summary>
public sealed class LibraryFolder(string root)
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SplitTrack");

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Paths for a new entry named after <paramref name="time"/>, e.g. 20261018_140322_clip.mkv; never an existing name.</summary>
    public EntryPaths NewEntry(DateTime time, string suffix)
    {
        Directory.CreateDirectory(Root);
        string baseName = $"{time:yyyyMMdd_HHmmss}_{suffix}";
        for (int n = 1; ; n++)
        {
            string name = n == 1 ? baseName : $"{baseName}_{n}";
            EntryPaths paths = EntryPaths.ForVideo(Path.Combine(Root, name + ".mkv"));
            if (!File.Exists(paths.Video) && !File.Exists(paths.PartialVideo) && !File.Exists(paths.Sidecar))
            {
                return paths;
            }
        }
    }

    /// <summary>Writes through a temporary file, so a reader never sees half a sidecar.</summary>
    public static void WriteSidecar(string path, Sidecar sidecar)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sidecar, Sidecar.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static Sidecar? ReadSidecar(string path) =>
        JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), Sidecar.JsonOptions);
}
