namespace SplitTrack.App.ViewModels;

/// <summary>
/// Which colour of <see cref="Views.Palette.Tracks"/> a track gets. It follows the track's kind, not its position,
/// so the game is the same colour in the recorder, the player and settings even when the tracks are in another order.
/// App tracks take turns over three colours in their order.
/// </summary>
public static class TrackColors
{
    private const int Game = 0;
    private const int Microphone = 2;
    private const int System = 3;
    private static readonly int[] Apps = [1, 4, 5];

    /// <param name="kinds">Kind names as stored in files and settings ("game", "app", "microphone", "system"); null counts as an app.</param>
    public static IReadOnlyList<int> For(IEnumerable<string?> kinds)
    {
        int apps = 0;
        return kinds.Select(kind => kind switch
        {
            "game" => Game,
            "microphone" => Microphone,
            "system" => System,
            _ => Apps[apps++ % Apps.Length],
        }).ToList();
    }
}
