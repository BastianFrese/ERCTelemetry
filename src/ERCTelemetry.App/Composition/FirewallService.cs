using System.ComponentModel;
using System.Diagnostics;

namespace ERCTelemetry.App.Composition;

/// <summary>Manages the Windows Firewall allow rule for inbound UDP telemetry. The rule is
/// created by the app (one-time UAC prompt) instead of the installer, because the installer
/// now runs without elevation. The rule name matches the one the old installer used, so
/// existing installs are recognized; a rule pointing at a pre-migration Program-Files path
/// is treated as missing so the user can re-create it for the new location.</summary>
public static class FirewallService
{
    /// <summary>Rule name — identical to the one the old installer's netsh [Run] entry used,
    /// so the app's check recognizes an existing rule and never re-prompts.</summary>
    public const string RuleName = "ERCTelemetry Telemetrie (UDP)";

    /// <summary>True when a rule with <see cref="RuleName"/> exists AND points at the current
    /// executable. The check is a plain substring match on the netsh output, so it is
    /// independent of the OS language.</summary>
    public static bool RuleExists()
    {
        var output = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"");
        if (output is null)
        {
            return false;
        }

        var exePath = Environment.ProcessPath;
        return exePath is not null && output.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates (or replaces) the allow rule for the current executable. One elevated
    /// cmd.exe run chains delete + add, so the user sees a single UAC prompt; the delete
    /// makes the rule idempotent and drops a stale rule from a pre-migration install. Returns
    /// true when the rule now exists and points at the current exe.</summary>
    public static bool AddRule()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            return false;
        }

        var args = $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" & " +
                   $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                   $"program=\"{exePath}\" protocol=udp profile=private,public enable=yes";
        return RunElevated("cmd.exe", args) && RuleExists();
    }

    /// <summary>Runs netsh without elevation (rule queries work for normal users) and returns
    /// its stdout; null when netsh is unavailable or the call failed.</summary>
    private static string? RunNetsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Runs a command elevated (UAC prompt). Returns false when the user cancels the
    /// prompt or the command cannot be started.</summary>
    private static bool RunElevated(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            process?.WaitForExit();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false; // UAC cancelled or the command could not be started
        }
    }
}
