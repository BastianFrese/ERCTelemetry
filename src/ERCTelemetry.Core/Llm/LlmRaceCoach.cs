using System.Globalization;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 AI coach backed by an <see cref="OllamaClient"/>: rewrites the
/// structured Layer-1 findings into 2–4 natural German sentences. Null on failure — the
/// Layer-1 findings stay visible.</summary>
public sealed class LlmRaceCoach : IRaceCoach
{
    private readonly OllamaClient _client;

    public LlmRaceCoach(OllamaClient client) => _client = client;

    public async Task<string?> CoachAsync(CoachReport report, CancellationToken ct)
    {
        var system =
            "Du bist ein deutscher F1-Fahrcoach. Fasse die folgenden Analyse-Findings in " +
            "2–4 natürlichen deutschen Sätzen zusammen. Sei konkret und freundlich, wiederhole " +
            "die Findings nicht wörtlich, keine Anführungszeichen.";
        var findings = string.Join("\n", report.Findings.Select(f =>
            f.CornerNumber > 0
                ? $"Kurve {f.CornerNumber}: −{f.LossSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s — {f.Advice}"
                : f.Advice));
        var user =
            $"Gegner: {report.RivalName}. Gesamtverlust: " +
            $"{report.TotalLossSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s.\n{findings}";
        return await _client.CompleteAsync(system, user, ct).ConfigureAwait(false);
    }
}
