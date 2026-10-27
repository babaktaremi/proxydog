using System.Diagnostics;
using System.Reflection;
using ProxyDog.Core;

namespace ProxyDog.UI;

/// <summary>What this build is, and small helpers for talking to the desktop.</summary>
static class AppInfo
{
    /// <summary>The version stamped at build time (from the release tag in CI), without "+commit" metadata.</summary>
    public static string VersionText
    {
        get
        {
            var assembly = typeof(AppInfo).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? assembly.GetName().Version?.ToString(3)
                       ?? "1.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    public static SemVer Version => SemVer.TryParse(VersionText, out var v) ? v : new SemVer(0, 0, 0);

    public static void OpenUrl(string url)
    {
        try
        {
            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            using var _ = Process.Start(new ProcessStartInfo(opener, [url]) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser opener installed: nothing sensible to do.
        }
    }

    /// <summary>Best-effort desktop notification, for when the window is hidden in the tray.</summary>
    public static void Notify(string title, string message)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsMacOS())
            {
                static string Quote(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
                psi = new ProcessStartInfo("osascript", ["-e", $"display notification \"{Quote(message)}\" with title \"{Quote(title)}\""]);
            }
            else
            {
                psi = new ProcessStartInfo("notify-send", ["--app-name=ProxyDog", title, message]);
            }
            psi.UseShellExecute = false;
            using var _ = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // notify-send is optional on Linux.
        }
    }
}
