using System.Diagnostics;
using ProxyDog.Core;

namespace ProxyDog.Engine;

/// <summary>What a V2Ray rule needs to start: the parsed profile and the core executable to run it.</summary>
public sealed record V2RayStart(V2RayEndpoint Endpoint, string CorePath);

/// <summary>One rule being switched on: its forwarding engine plus the hooked application process.</summary>
public sealed class RuleSession(ProxyRule rule)
{
    V2RayCore? core;
    ProxyRule? upstream;          // what the engine dials: set for a V2Ray rule (the local core); otherwise the rule itself
    string? watchHost;
    int watchPort;

    ProxyEngine? engine;
    Process? process;
    UpstreamWatchdog? watchdog;
    string? executable;
    int lastPort;
    int failureStreak;
    int tripped;

    /// <summary>Required when the rule's kind is V2Ray.</summary>
    public V2RayStart? V2Ray { get; init; }

    public bool IsRunning => engine is not null;

    /// <summary>How often the kill switch checks that the proxy still answers.</summary>
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(3);

    public bool IsAppRunning
    {
        get
        {
            try { return process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public event Action<ConnectionEvent>? ConnectionEvent;

    /// <summary>Plain status lines for the log (text, isProblem).</summary>
    public event Action<string, bool>? Notice;

    public event Action? StateChanged;

    /// <summary>The kill switch fired on its own (not by the user): the app was closed and the rule stopped.</summary>
    public event Action<string>? KillSwitchTripped;

    public void Start()
    {
        if (engine is not null) return;
        var hook = AppLauncher.FindHook()
            ?? throw new FileNotFoundException(
                $"The proxy hook ({AppLauncher.HookFileName}) is missing next to the app. Rebuild ProxyDog to create it.");

        tripped = 0;
        failureStreak = 0;
        if (rule.Kind == ProxyKind.V2Ray)
            StartV2RayCore();

        var e = new ProxyEngine(upstream ?? rule);
        e.ConnectionEvent += ev =>
        {
            if (ev.Kind == ConnectionEventKind.Connected) Interlocked.Exchange(ref failureStreak, 0);
            ConnectionEvent?.Invoke(ev);
        };
        // Reuse the rule's own port so anything that remembered it (Telegram) keeps working across runs.
        try
        {
            e.Start(rule.LocalPort != 0 ? rule.LocalPort : lastPort);
        }
        catch
        {
            // The core is already running; do not leave it behind.
            if (core is { } orphan) { core = null; orphan.StopAsync().GetAwaiter().GetResult(); }
            throw;
        }
        lastPort = e.Port;
        rule.LocalPort = e.Port;
        engine = e;
        Notice?.Invoke(rule.Kind == ProxyKind.V2Ray
            ? $"Forwarding through V2Ray: {V2Ray!.Endpoint.Name} ({V2Ray.Endpoint.Summary})"
            : $"Forwarding through {rule.Kind.ToString().ToUpperInvariant()} {rule.Host}:{rule.Port}", false);

        // Always listening, so the kill switch can be armed or disarmed while the rule is on.
        e.UpstreamFailure += msg =>
        {
            if (rule.KillSwitch && Interlocked.Increment(ref failureStreak) >= 3)
                _ = TripAsync($"the proxy is not answering ({msg})");
        };
        e.Faulted += msg =>
        {
            if (rule.KillSwitch) _ = TripAsync(msg);
        };
        if (rule.KillSwitch)
            ArmWatchdog();

        try
        {
            LaunchApp();
        }
        catch
        {
            _ = StopAsync();
            throw;
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Runs the Xray core and makes the engine dial it as a plain SOCKS5 proxy on loopback. The kill switch watches the
    /// V2Ray server itself (when it speaks TCP), so a dead server is noticed, not only a dead local core.
    /// </summary>
    void StartV2RayCore()
    {
        var v = V2Ray ?? throw new InvalidOperationException("This rule uses V2Ray, but no V2Ray profile was supplied.");
        var started = V2RayCore.StartAsync(v.CorePath, v.Endpoint).GetAwaiter().GetResult();
        core = started;

        upstream = rule.Clone();
        upstream.Kind = ProxyKind.Socks5;
        upstream.Host = "127.0.0.1";
        upstream.Port = started.Port;
        upstream.Username = null;
        upstream.Password = null;

        var tcpBased = v.Endpoint.Network is not ("kcp" or "quic");
        watchHost = tcpBased ? v.Endpoint.Address : "127.0.0.1";
        watchPort = tcpBased ? v.Endpoint.Port : started.Port;

        started.Exited += reason =>
        {
            if (engine is null) return;
            Notice?.Invoke(reason, true);
            if (rule.KillSwitch) _ = TripAsync(reason.TrimEnd('.'));
        };
    }

    void ArmWatchdog()
    {
        if (watchdog is not null) return;
        var dog = new UpstreamWatchdog(watchHost ?? rule.Host, watchPort != 0 ? watchPort : rule.Port, WatchInterval);
        dog.Tripped += msg => _ = TripAsync(msg);
        dog.Start();
        watchdog = dog;
        Notice?.Invoke("Kill switch armed: the application closes if the proxy fails or the rule is turned off.", false);
    }

    /// <summary>Arms or disarms the kill switch, also while the rule is running.</summary>
    public void SetKillSwitch(bool on)
    {
        if (rule.KillSwitch == on) return;
        rule.KillSwitch = on;
        if (engine is null) return;
        if (on)
        {
            Interlocked.Exchange(ref failureStreak, 0);
            ArmWatchdog();
        }
        else
        {
            watchdog?.Stop();
            watchdog = null;
            Notice?.Invoke("Kill switch disarmed.", false);
        }
    }

    /// <summary>Starts the application again while the rule is on (for example after the user quit it).</summary>
    public void LaunchApp()
    {
        if (engine is null) throw new InvalidOperationException("Turn the rule on first.");
        if (IsAppRunning)
        {
            // Telegram keeps running between rule toggles; "Launch" resends the proxy link in case it was dismissed.
            if (OperatingSystem.IsMacOS() && rule.AppPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                && AppLauncher.IsTelegram(rule.AppPath))
                SendTelegramProxy(force: true);
            return;
        }

        var hook = AppLauncher.FindHook()!;
        var plan = AppLauncher.Plan(rule, engine.Port, hook);
        executable = plan.Executable;
        var psi = plan.StartInfo;
        var running = AppLauncher.FindRunning(plan.Executable);
        if (running.Count > 0)
        {
            var name = Path.GetFileName(plan.Executable);
            if (rule.IfRunning == RunningAppPolicy.Restart)
            {
                Notice?.Invoke($"{name} is already running. Closing it so it can restart through the proxy.", false);
                var closed = AppLauncher.Quit(running, TimeSpan.FromSeconds(8));
                foreach (var r in running) r.Dispose();
                if (!closed)
                    throw new InvalidOperationException(
                        $"{name} did not quit. Close it yourself, then press Launch Application.");
            }
            else
            {
                foreach (var r in running) r.Dispose();
                Notice?.Invoke(
                    $"{name} is already running, and that copy is not proxied. Opening it again may only bring the old window forward. Set \"If already running\" to Restart in the rule to route it.",
                    true);
            }
        }

        if (plan.TelegramProxyLink)
            Notice?.Invoke("Telegram cannot be hooked (sandboxed, hardened runtime). Routing it through its own proxy setting instead.", false);
        else if (plan.ChromiumProxyFlag)
            Notice?.Invoke("Chromium-based app: routing it with --proxy-server, since macOS will not load the hook into it.", false);
        else if (AppLauncher.HookWarning(plan.Executable) is { } warning)
            Notice?.Invoke(warning, true);

        Process p;
        if (plan.ViaLaunchServices)
        {
            using (var open = Process.Start(psi) ?? throw new InvalidOperationException("Could not run `open`."))
            {
                var err = open.StandardError.ReadToEnd();
                open.WaitForExit();
                if (open.ExitCode != 0)
                    throw new InvalidOperationException($"macOS could not open the application: {err.Trim()}");
            }
            p = WaitForProcess(plan.Executable)
                ?? throw new InvalidOperationException(
                    "The application was opened, but ProxyDog could not find its process. Press Launch Application to try again.");
        }
        else
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("The application did not start.");
        }

        p.EnableRaisingEvents = true;
        p.Exited += (_, _) =>
        {
            Notice?.Invoke($"Application exited (code {SafeExitCode(p)}).", false);
            StateChanged?.Invoke();
        };
        process = p;
        Notice?.Invoke($"Launched {Path.GetFileName(plan.Executable)} (pid {p.Id}).", false);
        if (plan.TelegramProxyLink)
            SendTelegramProxy(force: false);
        StateChanged?.Invoke();
    }

    /// <summary>Hands the engine to Telegram as its SOCKS5 proxy. Telegram asks the user to confirm once per port.</summary>
    void SendTelegramProxy(bool force)
    {
        if (engine is null) return;
        var port = engine.Port;
        if (!force && rule.ProxyLinkPort == port)
        {
            Notice?.Invoke("Telegram already has this proxy. If its connections are not shown here, open Settings > Data and Storage > Proxy and enable it, or press Launch Application to send it again.", false);
            return;
        }
        Thread.Sleep(2000); // give a freshly started app time to be ready for the link
        try
        {
            using var open = Process.Start(AppLauncher.TelegramLinkStartInfo(rule.AppPath, port));
            open?.WaitForExit(5000);
            rule.ProxyLinkPort = port;
            Notice?.Invoke($"Sent the proxy to Telegram (127.0.0.1:{port}). Click Connect Proxy in Telegram to turn it on.", false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Notice?.Invoke($"Could not send the proxy to Telegram: {ex.Message}", true);
        }
    }

    // `open` returns before the app's process exists, so look for it for a few seconds.
    static Process? WaitForProcess(string exe)
    {
        for (var i = 0; i < 40; i++)
        {
            var found = AppLauncher.FindRunning(exe);
            if (found.Count > 0)
            {
                var newest = found.OrderByDescending(x => x.StartTime).First();
                foreach (var other in found.Where(x => x != newest)) other.Dispose();
                return newest;
            }
            Thread.Sleep(250);
        }
        return null;
    }

    /// <summary>Turns the rule off. With the kill switch armed, the application is closed too.</summary>
    public Task StopAsync() => StopCoreAsync(rule.KillSwitch);

    async Task TripAsync(string reason)
    {
        if (Interlocked.Exchange(ref tripped, 1) == 1) return;
        Notice?.Invoke($"Kill switch: {reason}.", true);
        await StopCoreAsync(killApp: true);
        KillSwitchTripped?.Invoke(reason);
    }

    async Task StopCoreAsync(bool killApp)
    {
        var e = engine;
        if (e is null) return;
        engine = null;
        watchdog?.Stop();
        watchdog = null;

        // Close the app before the engine so nothing can slip out while we shut down.
        var closed = killApp ? await Task.Run(TerminateApp) : 0;
        await e.StopAsync();
        if (core is { } c)
        {
            core = null;
            await c.StopAsync();
        }

        var name = executable is null ? "the application" : Path.GetFileName(executable);
        Notice?.Invoke(
            closed > 0 ? $"Rule off. Kill switch closed {name}."
            : IsAppRunning ? "Rule off. The application is still running; its new connections are blocked until you turn the rule on again."
            : "Rule off.",
            false);
        StateChanged?.Invoke();
    }

    int TerminateApp()
    {
        var pids = new HashSet<int>();
        try { if (process is { HasExited: false }) pids.Add(process.Id); }
        catch (InvalidOperationException) { /* already gone */ }
        if (executable is not null)
        {
            var found = AppLauncher.FindRunning(executable);
            foreach (var p in found) { pids.Add(p.Id); p.Dispose(); }
        }
        return pids.Count == 0 ? 0 : AppLauncher.Terminate(pids, TimeSpan.FromSeconds(3));
    }

    static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
