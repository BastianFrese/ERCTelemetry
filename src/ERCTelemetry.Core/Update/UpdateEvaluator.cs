namespace ERCTelemetry.Core.Update;

/// <summary>Verdict of comparing the running app version against the published manifest.</summary>
public enum UpdateDecision
{
    /// <summary>Published version is newer — show the update button.</summary>
    Available = 0,

    /// <summary>Running version already matches or beats the published one.</summary>
    SameOrOlder = 1,
}

/// <summary>Pure version-comparison logic: should the app offer an update? Kept free of
/// any I/O so it is unit-testable; the app layer feeds it the parsed manifest.</summary>
public static class UpdateEvaluator
{
    /// <summary>Compares the running version against the published manifest version.
    /// Returns <see cref="UpdateDecision.Available"/> only when the manifest version parses
    /// AND is strictly greater than the running version (missing current version or an
    /// unparseable manifest version never produces a false "update available").</summary>
    public static UpdateDecision Evaluate(Version? current, UpdateManifest? manifest)
    {
        if (current is null || manifest is null ||
            System.Version.TryParse(manifest.Version, out var available) is false)
        {
            return UpdateDecision.SameOrOlder;
        }

        return available > current ? UpdateDecision.Available : UpdateDecision.SameOrOlder;
    }
}