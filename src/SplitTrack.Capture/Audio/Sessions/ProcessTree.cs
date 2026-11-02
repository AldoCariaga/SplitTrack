using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace SplitTrack.Capture.Audio.Sessions;

public sealed record ProcessEntry(int Pid, int ParentPid, string ExeName);

/// <summary>Toolhelp snapshot of all processes with parent links. No admin rights needed.</summary>
public sealed class ProcessTree
{
    private readonly Dictionary<int, ProcessEntry> _byPid;
    private readonly Dictionary<int, long?> _creationTimes = [];

    private ProcessTree(Dictionary<int, ProcessEntry> byPid) => _byPid = byPid;

    public IEnumerable<ProcessEntry> Processes => _byPid.Values;

    public static unsafe ProcessTree Snapshot()
    {
        var byPid = new Dictionary<int, ProcessEntry>();
        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
        if (PInvoke.Process32FirstW(snapshot, ref entry))
        {
            do
            {
                int pid = (int)entry.th32ProcessID;
                byPid[pid] = new ProcessEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile.ToString());
            }
            while (PInvoke.Process32NextW(snapshot, ref entry));
        }

        return new ProcessTree(byPid);
    }

    public ProcessEntry? Get(int pid) => _byPid.GetValueOrDefault(pid);

    /// <summary>Matches with or without the ".exe" suffix, ignoring case.</summary>
    public IReadOnlyList<ProcessEntry> FindByExe(string exeName)
    {
        string wanted = NormalizeExeName(exeName);
        return _byPid.Values.Where(p => string.Equals(p.ExeName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Walks up while the parent has the same exe name, e.g. a Discord.exe utility process to the
    /// main Discord.exe. A parent created after its child is a reused PID and stops the walk.
    /// </summary>
    public int FindRoot(int pid)
    {
        var visited = new HashSet<int>();
        int current = pid;
        while (visited.Add(current) &&
               TryGetLiveParent(current, out ProcessEntry? entry, out ProcessEntry? parent) &&
               string.Equals(parent.ExeName, entry.ExeName, StringComparison.OrdinalIgnoreCase))
        {
            current = parent.Pid;
        }

        return current;
    }

    /// <summary>
    /// The PIDs from <paramref name="pid"/> up through its parents, itself first, by parent links:
    /// every root whose process loopback tree capture includes it. A parent created after its child
    /// is a reused PID and ends the chain.
    /// </summary>
    public IReadOnlyList<int> GetAncestry(int pid)
    {
        var chain = new List<int>();
        var visited = new HashSet<int>();
        int current = pid;
        while (_byPid.ContainsKey(current) && visited.Add(current))
        {
            chain.Add(current);
            if (!TryGetLiveParent(current, out _, out ProcessEntry? parent))
            {
                break;
            }

            current = parent.Pid;
        }

        return chain;
    }

    /// <summary>
    /// <paramref name="pid"/>'s entry and its parent's, when the parent is still the process that
    /// started it: a parent that is the process itself, or was created after it (a reused PID), is none.
    /// </summary>
    private bool TryGetLiveParent(
        int pid,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProcessEntry? entry,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProcessEntry? parent)
    {
        parent = null;
        if (!_byPid.TryGetValue(pid, out entry) ||
            entry.ParentPid == pid ||
            !_byPid.TryGetValue(entry.ParentPid, out parent))
        {
            parent = null;
            return false;
        }

        long? parentCreated = GetCreationTime(parent.Pid);
        long? childCreated = GetCreationTime(pid);
        if (parentCreated is { } p && childCreated is { } c && p > c)
        {
            parent = null;
            return false;
        }

        return true;
    }

    /// <summary>Creation time as a FILETIME value, or null when the process cannot be opened.</summary>
    public long? GetCreationTime(int pid)
    {
        if (_creationTimes.TryGetValue(pid, out long? cached))
        {
            return cached;
        }

        long? result = null;
        using var handle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (!handle.IsInvalid && PInvoke.GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            result = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        }

        _creationTimes[pid] = result;
        return result;
    }

    public static string NormalizeExeName(string exeName) =>
        exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
}
