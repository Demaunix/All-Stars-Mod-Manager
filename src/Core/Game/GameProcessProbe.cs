using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AllStarsModManager;

public enum GameRunState { NotRunning, Running, Unknown }

public sealed record ProbeResult(GameRunState State, string Message)
{
    public bool SafeToChange => State == GameRunState.NotRunning;
}

public interface IGameProcessProbe
{
    ProbeResult Check(string gameRoot);
}

/// <summary>
/// Decides whether the game (or anything started from its folder) is running. The manager only observes;
/// it never closes or signals a process. If the answer cannot be established the result is Unknown and callers must decline to change files.
/// </summary>
public sealed class SystemGameProcessProbe : IGameProcessProbe
{
    static readonly string[] GameNames = { "Sonic & SEGA All-Stars Racing" };
    static readonly string[] CompanionNames = { "Config" };

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);

    const uint QueryLimited = 0x1000;

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(QueryLimited, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(2048);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    public ProbeResult Check(string gameRoot)
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            return new(GameRunState.Unknown, "The list of running programs could not be read, so the manager cannot tell whether the game is running.");
        }
        var root = LoaderPath.Full(gameRoot).TrimEnd('\\');
        var unknown = new List<string>();
        try
        {
            foreach (var p in all)
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }
                string? path = null;
                try { path = ImagePath(p.Id); } catch { }
                bool known = GameNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));
                bool companion = CompanionNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (path is not null)
                {
                    if (LoaderPath.IsInside(root, path))
                        return new(GameRunState.Running, $"\"{name}\" is running from the game folder. Close it, then try again. The manager never closes programs for you.");
                    continue;
                }
                if (known) unknown.Add(name);
            }
        }
        finally { foreach (var p in all) p.Dispose(); }
        if (unknown.Count > 0)
            return new(GameRunState.Unknown, "A program named like the game is running and its location could not be read, so the manager cannot tell whether it is this installation. Close it, then try again.");
        return new(GameRunState.NotRunning, "The game is not running.");
    }
}

/// <summary>Probe used by tests and fixture tooling.</summary>
public sealed class FixedProbe(GameRunState state, string message = "") : IGameProcessProbe
{
    public ProbeResult Check(string gameRoot) => new(state, string.IsNullOrEmpty(message) ? state.ToString() : message);
}
