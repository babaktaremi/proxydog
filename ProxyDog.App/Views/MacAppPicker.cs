using System.Diagnostics;

namespace ProxyDog.UI.Views;

/// <summary>
/// The system "choose an application" dialog via AppleScript. Avalonia's file picker greys out or fails to
/// return .app bundles, while this one filters on the application-bundle type and hands back a plain POSIX path.
/// </summary>
static class MacAppPicker
{
    const string Script = """
        tell me to activate
        set chosen to choose file of type {"com.apple.application-bundle"} with prompt "Choose the application to proxy" default location (POSIX file "/Applications")
        return POSIX path of chosen
        """;

    public static async Task<string?> PickAsync()
    {
        var psi = new ProcessStartInfo("osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(Script);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not open the file chooser.");
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            return null; // cancelled (osascript reports "User canceled." with a non-zero exit)

        var path = output.Trim();
        return path.Length > 1 ? path.TrimEnd('/') : null;
    }
}
