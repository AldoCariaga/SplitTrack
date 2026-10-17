using SplitTrack.Common.Diagnostics;

namespace SplitTrack.Capture.Audio.Sessions;

public static class ProcessResolver
{
    /// <summary>
    /// A PID is used as given. An exe name resolves to its root process; with several roots the one
    /// owning the most audio sessions wins, then the oldest. Null when nothing matches.
    /// </summary>
    /// <param name="audio">Current audio sessions; scanned only when needed if not given.</param>
    public static ProcessEntry? Resolve(string spec, ProcessTree tree, IReadOnlyList<AudioProcessInfo>? audio = null)
    {
        if (int.TryParse(spec, out int pid))
        {
            return tree.Get(pid);
        }

        List<int> roots = tree.FindByExe(spec).Select(p => tree.FindRoot(p.Pid)).Distinct().ToList();
        if (roots.Count <= 1)
        {
            return roots.Count == 0 ? null : tree.Get(roots[0]);
        }

        audio ??= AudioSessionScanner.Scan(tree);
        int chosen = roots
            .OrderByDescending(r => audio.FirstOrDefault(a => a.RootPid == r)?.SessionPids.Count ?? 0)
            .ThenBy(r => tree.GetCreationTime(r) ?? long.MaxValue)
            .First();

        Log.Debug($"'{spec}' has {roots.Count} separate process trees ({string.Join(", ", roots)}); chose pid {chosen}");
        return tree.Get(chosen);
    }
}
