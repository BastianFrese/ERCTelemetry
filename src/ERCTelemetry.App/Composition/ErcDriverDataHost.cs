using System.Diagnostics;
using System.IO;

namespace ERCTelemetry.App.Composition;

/// <summary>
/// Keeps the ERC championship overlay data fresh: every 6 hours it runs the
/// repository's <c>tools/erc-drivers.js</c> scraper (Node.js) so the standings
/// shown by <c>championship.html</c> / <c>grid.html</c> follow erdi-erc.de
/// without a manual step. Fire-and-forget — any failure (Node.js missing,
/// script missing, network down) is logged and the loop simply retries at the
/// next tick; it must never surface to the user or take the app down.
/// </summary>
internal static class ErcDriverDataHost
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const string ScriptRelativePath = "tools/erc-drivers.js";

    /// <summary>Runs the scraper once shortly after startup, then every 6 hours.
    /// Never returns null and never throws — callers can fire-and-forget it.</summary>
    internal static async Task RunAsync()
    {
        await Task.Delay(StartDelay).ConfigureAwait(false);

        while (true)
        {
            try
            {
                await ScrapeOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Log($"erc-drivers scrape failed: {ex.Message}");
            }

            await Task.Delay(Interval).ConfigureAwait(false);
        }
    }

    /// <summary>One scrape run: locates Node.js and the script, then runs it with
    /// <c>--ziel</c> pointed at this app's served overlay data folder.</summary>
    private static async Task ScrapeOnceAsync()
    {
        string? node = FindNode();
        if (node is null)
        {
            App.Log("erc-drivers scrape skipped: node.exe not found on PATH");
            return; // no retry spam — checked again at the next interval
        }

        string? script = FindScript();
        if (script is null)
        {
            App.Log("erc-drivers scrape skipped: tools/erc-drivers.js not found above the install directory");
            return;
        }

        string ziel = Path.Combine(AppContext.BaseDirectory, "wwwroot", "overlay", "data", "erc-drivers.json");
        var start = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{script}\" --ziel=\"{ziel}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(start);
        if (process is null)
        {
            App.Log("erc-drivers scrape failed: process did not start");
            return;
        }

        // Beide Streams GLEICHZEITig lesen: liest man erst stdout komplett und
        // füllt in der Zeit der stderr-Puffer (4 KB), blockiert Node auf stderr
        // und ReadToEndAsync auf stdout erreicht nie EOF → Deadlock im 6h-Loop.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string outputText = await outputTask.ConfigureAwait(false);
        string errorText = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            App.Log($"erc-drivers scrape failed (exit {process.ExitCode}): {errorText.Trim()}");
        }
        else
        {
            // Last output line is the "OK: N Fahrer -> ..." summary
            string last = outputText.Trim().Split('\n').LastOrDefault()?.Trim() ?? "done";
            App.Log($"erc-drivers scrape ok: {last}");
        }
    }

    /// <summary>Finds node.exe: first on PATH, then the standard installer location.</summary>
    private static string? FindNode()
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(dir, "node.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    /// <summary>Walks up from the install directory to find the repository's
    /// tools/erc-drivers.js (installed layout sits several levels below the root).</summary>
    private static string? FindScript()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "erc-drivers.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}