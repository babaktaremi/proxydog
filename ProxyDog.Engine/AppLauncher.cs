using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ProxyDog.Core;

namespace ProxyDog.Engine;

/// <summary>Turns a rule's target (an .app bundle, a .desktop entry or an executable) into a hooked process start.</summary>
public static partial class AppLauncher
{
    public static string HookFileName =>
        OperatingSystem.IsMacOS() ? "libproxydog_hook.dylib" : "libproxydog_hook.so";

    public static string? FindHook()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "native", HookFileName);
        return File.Exists(path) ? path : null;
    }

    public static ProcessStartInfo BuildStartInfo(ProxyRule rule, int enginePort, string hookPath)
    {
        var (exe, args) = ResolveTarget(rule.AppPath);
        args.AddRange(CommandLine.Split(rule.Arguments));

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        psi.Environment["PROXYDOG_PORT"] = enginePort.ToString();
        var preload = OperatingSystem.IsMacOS() ? "DYLD_INSERT_LIBRARIES" : "LD_PRELOAD";
        var existing = Environment.GetEnvironmentVariable(preload);
        psi.Environment[preload] = string.IsNullOrEmpty(existing) ? hookPath : $"{hookPath}:{existing}";
        return psi;
    }

    /// <summary>How to start the application: the process to run, and the executable to look for afterwards.</summary>
    public sealed record LaunchPlan(ProcessStartInfo StartInfo, string Executable, bool ViaLaunchServices, bool ChromiumProxyFlag, bool TelegramProxyLink = false);

    /// <summary>
    /// On macOS an .app is opened through LaunchServices (`open`), so the app is its own responsible process and keeps
    /// access to its own data. Starting it as our child makes macOS deny writes to its profile (Chrome's SingletonLock).
    /// Chromium-based apps cannot load the hook (hardened runtime), so they also get --proxy-server pointing at the engine.
    /// </summary>
    public static LaunchPlan Plan(ProxyRule rule, int enginePort, string hookPath)
    {
        var direct = BuildStartInfo(rule, enginePort, hookPath);
        var exe = direct.FileName;
        var bundle = rule.AppPath;
        var isBundle = OperatingSystem.IsMacOS() && Directory.Exists(bundle)
            && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

        var chromium = isBundle && IsChromiumBased(bundle)
            && !(rule.Arguments ?? "").Contains("--proxy-server", StringComparison.Ordinal);
        if (chromium)
            direct.ArgumentList.Add($"--proxy-server=socks5://127.0.0.1:{enginePort}");

        var telegram = isBundle && IsTelegram(bundle);
        if (!isBundle)
            return new LaunchPlan(direct, exe, false, chromium);

        var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-n", "-a", bundle })
            open.ArgumentList.Add(a);
        foreach (var key in new[] { "PROXYDOG_PORT", "DYLD_INSERT_LIBRARIES" })
        {
            open.ArgumentList.Add("--env");
            open.ArgumentList.Add($"{key}={direct.Environment[key]}");
        }
        if (direct.ArgumentList.Count > 0)
        {
            open.ArgumentList.Add("--args");
            foreach (var a in direct.ArgumentList)
                open.ArgumentList.Add(a);
        }
        return new LaunchPlan(open, exe, true, chromium, telegram);
    }

    static readonly string[] TelegramBundleIds = ["ru.keepcoder.Telegram", "com.tdesktop.Telegram", "org.telegram.desktop"];

    public static string? BundleId(string bundle)
    {
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (!File.Exists(plist)) return null;
        var m = BundleIdentifier().Match(File.ReadAllText(plist));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Telegram is sandboxed with the hardened runtime, so macOS drops the hook. It has its own SOCKS5 proxy setting,
    /// which accepts a tg://socks link, so the engine (which speaks SOCKS5) is handed over that way.
    /// </summary>
    public static bool IsTelegram(string bundle) => BundleId(bundle) is { } id && TelegramBundleIds.Contains(id);

    public static ProcessStartInfo TelegramLinkStartInfo(string bundle, int enginePort)
    {
        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, RedirectStandardError = true };
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(bundle);
        psi.ArgumentList.Add($"tg://socks?server=127.0.0.1&port={enginePort}");
        return psi;
    }

    /// <summary>Chromium browsers and Electron apps ship a framework with icudtl.dat; they honour --proxy-server.</summary>
    public static bool IsChromiumBased(string bundle)
    {
        var frameworks = Path.Combine(bundle, "Contents", "Frameworks");
        if (!Directory.Exists(frameworks)) return false;
        foreach (var fw in Directory.EnumerateDirectories(frameworks, "*.framework"))
        {
            var name = Path.GetFileName(fw);
            if (name.StartsWith("Electron Framework") || name.StartsWith("Chromium Embedded Framework"))
                return true;
            // Chrome, Edge, Brave and friends: the ICU data file only Chromium frameworks carry.
            if (name.EndsWith(" Framework.framework") && File.Exists(Path.Combine(fw, "Resources", "icudtl.dat")))
                return true;
        }
        return false;
    }

    public static (string Exe, List<string> Args) ResolveTarget(string path)
    {
        if (Directory.Exists(path) && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return (ResolveBundleExecutable(path), []);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Application not found: {path}");

        if (path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
        {
            var exec = ParseDesktopExec(File.ReadAllText(path))
                ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no Exec line.");
            var parts = CommandLine.Split(exec)
                .Where(p => !(p.StartsWith('%') && p.Length == 2))
                .ToList();
            if (parts.Count == 0)
                throw new InvalidDataException($"{Path.GetFileName(path)} has an empty Exec line.");
            return (parts[0], parts.Skip(1).ToList());
        }

        return (path, []);
    }

    static string ResolveBundleExecutable(string bundle)
    {
        var macOs = Path.Combine(bundle, "Contents", "MacOS");
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (File.Exists(plist))
        {
            var m = BundleExecutable().Match(File.ReadAllText(plist));
            if (m.Success)
            {
                var named = Path.Combine(macOs, m.Groups[1].Value);
                if (File.Exists(named)) return named;
            }
        }
        // Binary plists cannot be read as text; fall back to the only executable in the bundle.
        var files = Directory.Exists(macOs) ? Directory.GetFiles(macOs) : [];
        if (files.Length == 1) return files[0];
        throw new FileNotFoundException($"Cannot find the executable inside {Path.GetFileName(bundle)}.");
    }

    public static string? ParseDesktopExec(string text)
    {
        var inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
                inEntry = line == "[Desktop Entry]";
            else if (inEntry && line.StartsWith("Exec=", StringComparison.Ordinal))
                return line["Exec=".Length..];
        }
        return null;
    }

    /// <summary>Processes already running the given executable (other users' processes are skipped).</summary>
    public static List<Process> FindRunning(string exe)
    {
        var found = new List<Process>();
        var wanted = Normalize(exe);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != Environment.ProcessId && Normalize(p.MainModule?.FileName) == wanted)
                {
                    found.Add(p);
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not ours to inspect.
            }
            p.Dispose();
        }
        return found;
    }

    // macOS reports /private/var/... for what callers know as /var/... (same for /tmp and /etc).
    static string? Normalize(string? path)
    {
        if (path is null) return null;
        path = Path.GetFullPath(path);
        return path.StartsWith("/private/", StringComparison.Ordinal) && File.Exists(path["/private".Length..])
            ? path["/private".Length..]
            : path;
    }

    /// <summary>Asks the processes to quit (SIGTERM) and waits; returns true when all are gone.</summary>
    public static bool Quit(List<Process> processes, TimeSpan timeout)
    {
        foreach (var p in processes)
        {
            try
            {
                using var kill = Process.Start(new ProcessStartInfo("kill", ["-TERM", p.Id.ToString()]) { UseShellExecute = false });
                kill?.WaitForExit(2000);
            }
            catch (System.ComponentModel.Win32Exception) { /* kill unavailable */ }
        }
        var deadline = DateTime.UtcNow + timeout;
        foreach (var p in processes)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !p.WaitForExit(left))
                return false;
        }
        return true;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int signal);

    static bool IsAlive(int pid) => kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == 1 /* EPERM: exists, not ours */;

    /// <summary>Every descendant of the given processes, found from the process table.</summary>
    public static HashSet<int> WithDescendants(IEnumerable<int> roots)
    {
        var all = new HashSet<int>(roots);
        var children = new Dictionary<int, List<int>>();
        try
        {
            var psi = new ProcessStartInfo("ps", ["-e", "-o", "pid=,ppid="]) { RedirectStandardOutput = true, UseShellExecute = false };
            using var ps = Process.Start(psi);
            if (ps is not null)
            {
                foreach (var line in ps.StandardOutput.ReadToEnd().Split('\n'))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid))
                    {
                        if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = [];
                        list.Add(pid);
                    }
                }
                ps.WaitForExit(2000);
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* no ps: roots only */ }

        var queue = new Queue<int>(all);
        while (queue.Count > 0)
            if (children.TryGetValue(queue.Dequeue(), out var kids))
                foreach (var k in kids)
                    if (all.Add(k)) queue.Enqueue(k);
        return all;
    }

    /// <summary>Terminates the processes and everything they started: SIGTERM, then SIGKILL after the grace period.</summary>
    public static int Terminate(IEnumerable<int> roots, TimeSpan grace)
    {
        var targets = WithDescendants(roots).Where(pid => pid != Environment.ProcessId).ToList();
        foreach (var pid in targets)
            kill(pid, 15);

        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline && targets.Any(IsAlive))
            Thread.Sleep(100);

        foreach (var pid in targets.Where(IsAlive))
            kill(pid, 9);
        return targets.Count;
    }

    /// <summary>Returns a human-readable warning when the OS is likely to strip the injected hook.</summary>
    public static string? HookWarning(string exe)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        if (exe.StartsWith("/System/") || exe.StartsWith("/usr/bin/") || exe.StartsWith("/bin/") || exe.StartsWith("/sbin/"))
            return "This is a system-protected program. macOS ignores the proxy hook for it, so its traffic will not be proxied.";

        try
        {
            var psi = new ProcessStartInfo("codesign") { RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-dv");
            psi.ArgumentList.Add(exe);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000)) return null;
            if (Regex.IsMatch(output, @"flags=0x[0-9a-f]+\([^)]*runtime"))
                return "This app uses the hardened runtime, which usually makes macOS ignore the proxy hook. Its traffic may bypass the proxy.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // codesign unavailable: no warning.
        }
        return null;
    }

    [GeneratedRegex(@"<key>CFBundleIdentifier</key>\s*<string>([^<]+)</string>")]
    private static partial Regex BundleIdentifier();

    [GeneratedRegex(@"<key>CFBundleExecutable</key>\s*<string>([^<]+)</string>")]
    private static partial Regex BundleExecutable();
}
