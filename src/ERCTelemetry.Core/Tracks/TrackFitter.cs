namespace ERCTelemetry.Core.Tracks;

/// <summary>Recovered layout→world similarity transform: with <see cref="Cos"/>/<see cref="Sin"/>
/// encoding the solved rotation angle, a layout point (lx, ly) maps to world metres via
/// worldX = Scale·(Cos·lx + Sin·ly) + Tx and worldZ = Scale·(Cos·ly − Sin·lx) + Ty.
/// <see cref="RmsMetres"/> is the trimmed fit error in metres, <see cref="Coverage"/> the
/// fraction of the layout polyline the driven trail actually covered (0–1). <see cref="Mirrored"/>
/// is set when the mirror hypothesis won — <see cref="ToWorld"/> then flips the layout
/// y-axis first, so callers always pass original (unmirrored) layout coordinates.</summary>
public sealed record TrackFitResult(
    float Scale,
    float Cos,
    float Sin,
    float Tx,
    float Ty,
    float RmsMetres,
    float Coverage,
    bool Mirrored = false)
{
    public (float X, float Z) ToWorld(float lx, float ly)
    {
        if (Mirrored)
        {
            ly = -ly;
        }

        return (
            Scale * (Cos * lx + Sin * ly) + Tx,
            Scale * (Cos * ly - Sin * lx) + Ty);
    }
}

/// <summary>Fits the circuit layout (<see cref="TrackLayout"/>) onto the game's world
/// coordinates by aligning the player's own driven positions (the only trustworthy
/// stream — other cars' slots can be zero-filled or lobby noise). Algorithm: 3 m grid
/// trail accumulation → PCA-seeded hypotheses (±180° × mirrored/plain) → trimmed
/// point-to-polyline ICP → quality gate; once locked the fit is frozen so the map never
/// re-zooms mid-session. Pit lane, formation grid and flashback teleports are rejected
/// by the trim, the player-only feed and grid quantization.
/// Not thread-safe: one instance per consumer, fed from a single thread. Create a fresh
/// instance whenever the session changes (new sessionUid) — state is session-scoped.</summary>
public sealed class TrackFitter
{
    private const float Grid = 3;              // metres per trail cell
    private const int MinCells = 120;          // trail cells needed before the first attempt
    private const int NewCellsPerAttempt = 30; // re-attempt only after meaningful new data
    private const int MaxSamples = 600;        // recent world samples used for fitting
    private const int MaxCells = 1500;         // trail cell budget (flashback/teleport guard)
    private const int IcpIterations = 60;
    private const int VertexIterations = 15;   // final point-to-vertex stage: pins rotation
    private const double TrimFraction = 0.75;  // keep the best 75% of pairs each iteration
    private const double CoverageStep = 10;    // metres between coverage probes on the layout
    private const double CoverageRadius = 15;  // metres — a probe counts when trail is this close
    private const double MinCoverage = 0.5;
    private const double MinRms = 6;           // metres — never tighter than this …
    private const double RmsFraction = 0.0015; // … or 0.15% of the lap length
    private const double MinScale = 0.7;
    private const double MaxScale = 1.4;       // the game world is 1 unit = 1 m; a wild scale
                                               // means the fit latched onto the wrong shape

    private readonly TrackLayout _layout;
    private readonly HashSet<(int X, int Y)> _cells = new();
    private readonly List<(float X, float Z)> _samples = new(MaxSamples);
    private readonly List<(float X, float Z)> _startFinishCrossings = new();

    private int _cellsSinceAttempt;
    private readonly System.Diagnostics.Stopwatch _sinceAttempt = System.Diagnostics.Stopwatch.StartNew();
    private TrackFitResult? _fit;

    public TrackFitter(TrackLayout layout)
    {
        _layout = layout;
    }

    /// <summary>True once the fit passed the quality gate and is frozen.</summary>
    public bool IsLocked => _fit is not null;

    /// <summary>The recovered transform, null until locked (and then never changes).</summary>
    public TrackFitResult? Fit => _fit;

    /// <summary>Start/finish position in world metres — the median of the lap-line
    /// crossings reported via <see cref="MarkStartFinishCrossing"/> once at least three
    /// were seen; null before that. Independent of the fit lock.</summary>
    public (float X, float Z)? StartFinishWorld { get; private set; }

    /// <summary>Feeds one player-car world position (per snapshot). Unreported (0,0)
    /// slots must be skipped by the caller, as everywhere else in the pipeline.</summary>
    public void Feed(float x, float z)
    {
        if (_fit is not null)
        {
            return; // frozen — late-session garbage must never destabilize a good fit
        }

        var cell = ((int)MathF.Round(x / Grid), (int)MathF.Round(z / Grid));
        if (_cells.Count < MaxCells && _cells.Add(cell))
        {
            _cellsSinceAttempt++;
        }

        if (_samples.Count == MaxSamples)
        {
            _samples.RemoveAt(0);
        }
        _samples.Add((x, z));

        if (_cells.Count >= MinCells &&
            _cellsSinceAttempt >= NewCellsPerAttempt &&
            _sinceAttempt.ElapsedMilliseconds >= 250)
        {
            _cellsSinceAttempt = 0;
            _sinceAttempt.Restart();
            TryFit();
        }
    }

    /// <summary>Reports one crossing of the start/finish line (world metres) — typically
    /// on the player's lap-completion event. Three crossings determine the median point.</summary>
    public void MarkStartFinishCrossing(float x, float z)
    {
        if (StartFinishWorld is not null)
        {
            return;
        }

        _startFinishCrossings.Add((x, z));
        if (_startFinishCrossings.Count < 3)
        {
            return;
        }

        var xs = _startFinishCrossings.Select(p => p.X).OrderBy(v => v).ToArray();
        var zs = _startFinishCrossings.Select(p => p.Z).OrderBy(v => v).ToArray();
        StartFinishWorld = (Median(xs), Median(zs));
    }

    private static float Median(float[] sorted)
    {
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private void TryFit()
    {
        if (_samples.Count < MinCells)
        {
            return;
        }

        var trail = TrailStatistics();
        var layoutStats = LayoutStatistics();

        TrackFitResult? best = null;
        double bestRms = double.MaxValue;
        var bestMirrored = false;
        foreach (var mirror in new[] { false, true })
        {
            var points = MirrorLayout(mirror);
            foreach (var flip in new[] { 0.0, Math.PI })
            {
                var candidate = RunIcp(
                    points, layoutStats.CentroidX, layoutStats.CentroidY,
                    trail.Angle - layoutStats.Angle + flip,
                    trail.MeanRadius / Math.Max(layoutStats.MeanRadius, 1e-6));
                if (candidate is not null && candidate.Value.Rms < bestRms)
                {
                    (best, bestRms) = (candidate.Value.Fit, candidate.Value.Rms);
                    bestMirrored = mirror;
                }
            }
        }

        if (best is null)
        {
            return;
        }

        var coverage = ComputeCoverage(best);
        var rmsLimit = Math.Max(MinRms, RmsFraction * _layout.LengthMetres);
        if (best.RmsMetres <= rmsLimit &&
            best.Scale is >= (float)(1 / MaxScale) and <= (float)(1 / MinScale) &&
            coverage >= MinCoverage)
        {
            _fit = best with { Coverage = (float)coverage, Mirrored = bestMirrored };
        }
    }

    private (double Angle, double MeanRadius, double CentroidX, double CentroidY) TrailStatistics()
    {
        double cx = 0, cz = 0;
        foreach (var (x, z) in _samples)
        {
            cx += x;
            cz += z;
        }
        cx /= _samples.Count;
        cz /= _samples.Count;
        double sxx = 0, szz = 0, sxz = 0, radius = 0;
        foreach (var (x, z) in _samples)
        {
            var dx = x - cx;
            var dz = z - cz;
            sxx += dx * dx;
            szz += dz * dz;
            sxz += dx * dz;
            radius += Math.Sqrt(dx * dx + dz * dz);
        }
        // Orientation of the principal axis (the PCA eigenvector angle).
        return (0.5 * Math.Atan2(2 * sxz, sxx - szz), radius / _samples.Count, cx, cz);
    }

    private (double Angle, double MeanRadius, double CentroidX, double CentroidY) LayoutStatistics()
    {
        double cx = 0, cz = 0;
        for (var i = 0; i < _layout.PointCount; i++)
        {
            cx += _layout.X[i];
            cz += _layout.Y[i];
        }
        cx /= _layout.PointCount;
        cz /= _layout.PointCount;
        double sxx = 0, szz = 0, sxz = 0, radius = 0;
        for (var i = 0; i < _layout.PointCount; i++)
        {
            var dx = _layout.X[i] - cx;
            var dy = _layout.Y[i] - cz;
            sxx += dx * dx;
            szz += dy * dy;
            sxz += dx * dy;
            radius += Math.Sqrt(dx * dx + dy * dy);
        }
        return (0.5 * Math.Atan2(2 * sxz, sxx - szz), radius / _layout.PointCount, cx, cz);
    }

    /// <summary>The layout polyline with the y-axis negated (mirror hypothesis).</summary>
    private (float X, float Y)[] MirrorLayout(bool mirror)
    {
        var points = new (float, float)[_layout.PointCount];
        for (var i = 0; i < _layout.PointCount; i++)
        {
            points[i] = (_layout.X[i], mirror ? -_layout.Y[i] : _layout.Y[i]);
        }
        return points;
    }

    /// <summary>Runs trimmed ICP for one hypothesis. Each iteration maps the world
    /// samples through the CURRENT forward estimate (world→layout) before matching —
    /// raw matching cannot recover a large world-origin offset, because correspondences
    /// would collapse onto one layout arc. Stage 1 matches to the closest point on the
    /// polyline (shape, scale, translation), stage 2 to the closest vertex — radial
    /// projection is invariant to rotation on smooth rings, so only vertex matches
    /// pin the remaining tangential offset. Returns the inverted (layout→world)
    /// transform plus the trimmed RMS, or null if ICP diverged.</summary>
    private (TrackFitResult Fit, double Rms)? RunIcp(
        (float X, float Y)[] layout,
        double layoutCentroidX, double layoutCentroidY,
        double rotation, double initScale)
    {
        // Seed: hypothesis world = initScale·R(rotation)·layout + T with centroid-aligned T,
        // inverted into the forward (world→layout) form the ICP iterates on.
        double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
        var worldMeanX = _samples.Average(s => s.X);
        var worldMeanZ = _samples.Average(s => s.Z);
        var seedTx = worldMeanX - initScale * (cos * layoutCentroidX - sin * layoutCentroidY);
        var seedTz = worldMeanZ - initScale * (sin * layoutCentroidX + cos * layoutCentroidY);
        var scale = 1 / initScale; // world→layout
        double fcos = cos, fsin = -sin; // forward rotation is −rotation
        var ftx = -scale * (fcos * seedTx - fsin * seedTz);
        var ftz = -scale * (fsin * seedTx + fcos * seedTz);

        var pairs = new List<((float X, float Z) U, (float X, float Y) Q)>(_samples.Count);

        // One refinement step: trim the worst pairs, solve the closed-form 2D similarity
        // (Procrustes) on the kept correspondences and compose it onto the forward map.
        bool Refine(List<((float X, float Z) U, (float X, float Y) Q)> current)
        {
            // Trim: drop the worst 25% of pairs (pit lane, off-track excursions).
            var distances = current.Select(p => Dist(p.U, p.Q)).OrderBy(d => d).ToArray();
            var threshold = distances[(int)(distances.Length * TrimFraction) - 1];
            var keptCount = 0;
            double ucx = 0, ucz = 0, qcx = 0, qcz = 0;
            foreach (var p in current)
            {
                if (Dist(p.U, p.Q) > threshold)
                {
                    continue;
                }

                keptCount++;
                ucx += p.U.X;
                ucz += p.U.Z;
                qcx += p.Q.X;
                qcz += p.Q.Y;
            }

            if (keptCount < 10)
            {
                return false;
            }

            ucx /= keptCount;
            ucz /= keptCount;
            qcx /= keptCount;
            qcz /= keptCount;

            double dot = 0, cross = 0, uNorm = 0;
            foreach (var p in current)
            {
                if (Dist(p.U, p.Q) > threshold)
                {
                    continue;
                }

                var px = p.U.X - ucx;
                var pz = p.U.Z - ucz;
                var qx = p.Q.X - qcx;
                var qy = p.Q.Y - qcz;
                dot += px * qx + pz * qy;
                cross += pz * qx - px * qy;
                uNorm += px * px + pz * pz;
            }

            if (uNorm < 1e-6)
            {
                return false;
            }

            var angle = Math.Atan2(-cross, dot);
            var dscale = Math.Sqrt(dot * dot + cross * cross) / uNorm;
            var dcos = Math.Cos(angle);
            var dsin = Math.Sin(angle);
            var dtx = qcx - dscale * (dcos * ucx - dsin * ucz);
            var dtz = qcz - dscale * (dsin * ucx + dcos * ucz);

            // Compose the refinement onto the forward map: Δ after F. Both translation
            // updates must use the OLD (ftx, ftz) — the second line reads dsin·ftx, so an
            // already-updated ftx would inject a cross term that accumulates over the
            // iterated refinements and skews the fit/quality gate.
            var oldFtx = ftx;
            var oldFtz = ftz;
            var ncos = dcos * fcos - dsin * fsin;
            fsin = dsin * fcos + dcos * fsin;
            fcos = ncos;
            ftx = dtx + dscale * (dcos * oldFtx - dsin * oldFtz);
            ftz = dtz + dscale * (dsin * oldFtx + dcos * oldFtz);
            scale *= dscale;
            return true;
        }

        // Stage 1 — point-to-polyline: shape, scale and translation.
        for (var iteration = 0; iteration < IcpIterations; iteration++)
        {
            pairs.Clear();
            foreach (var (x, z) in _samples)
            {
                var ux = scale * (fcos * x - fsin * z) + ftx;
                var uy = scale * (fsin * x + fcos * z) + ftz;
                pairs.Add((((float)ux, (float)uy), ClosestOnPolyline(ux, uy, layout)));
            }

            if (!Refine(pairs))
            {
                return null;
            }
        }

        // Stage 2 — point-to-vertex: pins the tangential offset the radial
        // projection is blind to (nearest-vertex matches shift along the ring).
        for (var iteration = 0; iteration < VertexIterations; iteration++)
        {
            pairs.Clear();
            foreach (var (x, z) in _samples)
            {
                var ux = scale * (fcos * x - fsin * z) + ftx;
                var uy = scale * (fsin * x + fcos * z) + ftz;
                pairs.Add((((float)ux, (float)uy), ClosestVertex(ux, uy, layout)));
            }

            if (!Refine(pairs))
            {
                return null;
            }
        }

        // Final trimmed RMS in the layout frame on the converged alignment.
        pairs.Clear();
        foreach (var (x, z) in _samples)
        {
            var ux = scale * (fcos * x - fsin * z) + ftx;
            var uy = scale * (fsin * x + fcos * z) + ftz;
            pairs.Add((((float)ux, (float)uy), ClosestOnPolyline(ux, uy, layout)));
        }

        var rms = TrimmedRms(pairs);
        if (rms is null)
        {
            return null;
        }

        // Invert the forward map (world→layout) into the layout→world form of
        // TrackFitResult: w = (1/s)·R(−θf)·(u − t).
        var invScale = 1 / scale;
        var fit = new TrackFitResult(
            Scale: (float)invScale,
            Cos: (float)fcos,
            Sin: (float)fsin,
            Tx: (float)(-invScale * (fcos * ftx + fsin * ftz)),
            Ty: (float)(invScale * (fsin * ftx - fcos * ftz)),
            RmsMetres: (float)rms.Value,
            Coverage: 0);
        return (fit, rms.Value);
    }

    /// <summary>Trimmed RMS of point-to-polyline pairs (distance-to-polyline — measures
    /// shape agreement), or null when too few pairs survive the trim.</summary>
    private static double? TrimmedRms(List<((float X, float Z) U, (float X, float Y) Q)> pairs)
    {
        var distances = pairs.Select(p => Dist(p.U, p.Q)).OrderBy(d => d).ToArray();
        var threshold = distances[(int)(distances.Length * TrimFraction) - 1];
        double error = 0;
        var keptCount = 0;
        foreach (var p in pairs)
        {
            if (Dist(p.U, p.Q) > threshold)
            {
                continue;
            }

            keptCount++;
            var dx = p.U.X - p.Q.X;
            var dy = p.U.Z - p.Q.Y;
            error += dx * dx + dy * dy;
        }

        return keptCount < 10 ? null : Math.Sqrt(error / keptCount);
    }

    private static double Dist((float X, float Z) w, (float X, float Y) l)
    {
        var dx = w.X - l.X;
        var dz = w.Z - l.Y;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Closest point on the closed layout ring for one world point.</summary>
    private static (float X, float Y) ClosestOnPolyline(double x, double z, (float X, float Y)[] layout)
    {
        double best = double.MaxValue;
        (float, float) result = layout[0];
        for (var i = 0; i < layout.Length; i++)
        {
            var a = layout[i];
            var b = layout[(i + 1) % layout.Length];
            var abx = b.X - a.X;
            var aby = b.Y - a.Y;
            var lengthSquared = abx * abx + aby * aby;
            if (lengthSquared < 1e-9)
            {
                continue; // duplicated ring endpoint
            }

            var t = ((x - a.X) * abx + (z - a.Y) * aby) / lengthSquared;
            t = Math.Max(0, Math.Min(1, t));
            var qx = a.X + t * abx;
            var qy = a.Y + t * aby;
            var dx = x - qx;
            var dz = z - qy;
            var d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                result = ((float)qx, (float)qy);
            }
        }
        return result;
    }

    /// <summary>Nearest layout vertex for one point — the tangential anchor the
    /// point-to-polyline stage cannot provide on smooth ring sections.</summary>
    private static (float X, float Y) ClosestVertex(double x, double z, (float X, float Y)[] layout)
    {
        double best = double.MaxValue;
        (float, float) result = layout[0];
        for (var i = 0; i < layout.Length; i++)
        {
            var dx = x - layout[i].X;
            var dz = z - layout[i].Y;
            var d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                result = layout[i];
            }
        }
        return result;
    }

    /// <summary>Fraction of the layout polyline (probed every ~10 m of arc length) that
    /// lies within <see cref="CoverageRadius"/> of any driven trail cell.</summary>
    private double ComputeCoverage(TrackFitResult fit)
    {
        // Bucket the trail cells into 15 m buckets for O(1) neighborhood lookups.
        var buckets = new HashSet<(int X, int Y)>();
        foreach (var (ix, iz) in _cells)
        {
            var bx = (int)Math.Floor(ix * Grid / CoverageRadius);
            var by = (int)Math.Floor(iz * Grid / CoverageRadius);
            buckets.Add((bx, by));
        }

        var probes = 0;
        var covered = 0;
        for (var i = 0; i < _layout.PointCount; i++)
        {
            var a = (X: _layout.X[i], Y: _layout.Y[i]);
            var bI = (i + 1) % _layout.PointCount;
            var b = (X: _layout.X[bI], Y: _layout.Y[bI]);
            var segment = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            var steps = Math.Max(1, (int)(segment / CoverageStep));
            for (var s = 0; s < steps; s++)
            {
                var t = (double)s / steps;
                var (wx, wz) = fit.ToWorld(
                    (float)(a.X + t * (b.X - a.X)),
                    (float)(a.Y + t * (b.Y - a.Y)));
                probes++;
                if (NearTrail(wx, wz, buckets))
                {
                    covered++;
                }
            }
        }

        return probes == 0 ? 0 : (double)covered / probes;
    }

    private bool NearTrail(double wx, double wz, HashSet<(int X, int Y)> buckets)
    {
        var bx = (int)Math.Floor(wx / CoverageRadius);
        var by = (int)Math.Floor(wz / CoverageRadius);
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (buckets.Contains((bx + dx, by + dy)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}