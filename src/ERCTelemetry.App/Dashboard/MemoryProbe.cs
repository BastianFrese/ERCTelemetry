using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ERCTelemetry.App.Dashboard;

/// <summary>RAM-Diagnose-Hook: loggt jede Sekunde Working Set, Managed Heap, committed
/// managed memory und GC-Zähler in %LOCALAPPDATA%\ERCTelemetry\memory.log. Selbst-begrenzt,
/// damit kein Endlos-Thread läuft und die Datei nicht unbegrenzt wächst: endet nach
/// <see cref="MaxLines"/> Zeilen (~2,8 h bei 1 Hz) oder sobald das übergebene
/// CancellationToken gecancelt wird.</summary>
public static class MemoryProbe
{
    /// <summary>Maximale Zeilenzahl im memory.log — danach beendet sich der Hook von selbst.</summary>
    private const int MaxLines = 10_000;

    private static int _started;

    public static void Start(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ERCTelemetry", "memory.log");
        var sw = Stopwatch.StartNew();
        var lines = 0;
        _ = Task.Run(() =>
        {
            while (lines < MaxLines && !ct.IsCancellationRequested)
            {
                try
                {
                    var proc = Process.GetCurrentProcess();
                    var info = GC.GetGCMemoryInfo();
                    var pause = info.PauseDurations.Length > 0
                        ? info.PauseDurations[^1].TotalMilliseconds
                        : 0.0;
                    var line = $"[{sw.Elapsed:hh\\:mm\\:ss}] " +
                        $"WS={proc.WorkingSet64 / 1024.0 / 1024.0:0.0}MB " +
                        $"Heap={GC.GetTotalMemory(false) / 1024.0 / 1024.0:0.0}MB " +
                        $"Committed={info.TotalCommittedBytes / 1024.0 / 1024.0:0.0}MB " +
                        $"HeapSize={info.HeapSizeBytes / 1024.0 / 1024.0:0.0}MB " +
                        $"GCs={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                        $"Pause={pause:0.0}ms";
                    File.AppendAllText(path, line + Environment.NewLine);
                    lines++;
                }
                catch
                {
                    // Diagnose darf nie crashen.
                }

                Thread.Sleep(1000);
            }
        });
    }
}
