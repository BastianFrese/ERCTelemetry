// Offline generator for src/ERCTelemetry.Core/Tracks/track-layouts.json.
//
// Downloads (or reuses the cached copy of) the combined f1-circuits.geojson from
// bacinger/f1-circuits (MIT), converts every circuit needed by the F1 game's Track
// enum into a flat metre-space centerline (equirectangular projection around the
// circuit centroid), simplifies it with Douglas-Peucker and writes one JSON file
// keyed by lowercase enum names with the "F1_" prefix stripped. Alias enum names
// (Bahrain/Sakhir, Catalunya/Barcelona, …) are written as duplicate keys so the
// runtime catalog can look up either spelling, mirroring TrackImage.Embedded.
//
// Run:  dotnet run --project tools/TrackLayoutGen

using System.Text.Json;

var root = FindRepoRoot();
var cachePath = Path.Combine(root, "tools", "TrackLayoutGen", "cache", "f1-circuits.geojson");
var outPath = Path.Combine(root, "src", "ERCTelemetry.Core", "Tracks", "track-layouts.json");

Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
if (!File.Exists(cachePath))
{
    Console.WriteLine("cache/f1-circuits.geojson missing — downloading…");
    using var http = new HttpClient();
    var geojson = await http.GetStringAsync(
        "https://raw.githubusercontent.com/bacinger/f1-circuits/master/f1-circuits.geojson");
    await File.WriteAllTextAsync(cachePath, geojson);
}

// bacinger circuit id → F1 Track enum names (lowercase, "F1_" prefix stripped).
// Multiple names = aliases for the same circuit across game versions; every name
// also found in TrackImage.Embedded is covered except Buddh/Korea/Valencia, which
// bacinger does not carry (they fall back to the trail-based map at runtime).
var mapping = new Dictionary<string, string[]>
{
    ["au-1953"] = ["melbourne"],
    ["bh-2002"] = ["bahrain", "sakhir"],
    ["cn-2004"] = ["shanghai"],
    ["es-1991"] = ["barcelona", "catalunya"],
    ["mc-1929"] = ["monaco", "monte_carlo"],
    ["ca-1978"] = ["montreal"],
    ["fr-1969"] = ["ricard", "paulricard"],
    ["at-1969"] = ["spielberg", "redbullring", "austria", "austriareverse"],
    ["gb-1948"] = ["silverstone", "silverstonereverse"],
    ["de-1932"] = ["hockenheim"],
    ["hu-1986"] = ["hungaroring"],
    ["be-1925"] = ["spa"],
    ["it-1922"] = ["monza"],
    ["sg-2008"] = ["singapore"],
    ["ru-2014"] = ["sochi"],
    ["jp-1962"] = ["suzuka"],
    ["us-2012"] = ["austin", "texas"],
    ["mx-1962"] = ["mexicocity", "mexico"],
    ["br-1940"] = ["interlagos", "saopaulo", "brazil"],
    ["ae-2009"] = ["yas_marina", "yasmarina", "abudhabi"],
    ["it-1953"] = ["imola"],
    ["de-1927"] = ["nurburgring"],
    ["pt-2008"] = ["portimao"],
    ["it-1914"] = ["mugello"],
    ["my-1999"] = ["sepang"],
    ["tr-2005"] = ["istanbul"],
    ["nl-1948"] = ["zandvoort", "zandvoortreverse"],
    ["pt-1972"] = ["estoril"],
    ["sa-2021"] = ["jeddah"],
    ["us-2022"] = ["miami"],
    ["qa-2004"] = ["losail", "qatar"],
    ["es-2026"] = ["madring", "madrid"],
    ["az-2016"] = ["baku", "azerbaijan"],
    ["us-2023"] = ["lasvegas"],
};

var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
var byId = new Dictionary<string, JsonElement>();
foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
{
    var id = feature.GetProperty("properties").GetProperty("id").GetString()!;
    byId[id] = feature;
}

var tracks = new Dictionary<string, object>();
foreach (var (id, keys) in mapping)
{
    if (!byId.TryGetValue(id, out var feature))
    {
        throw new InvalidDataException($"Circuit '{id}' is gone from f1-circuits — update the mapping.");
    }

    var coords = feature.GetProperty("geometry").GetProperty("coordinates");
    var (points, lengthMetres) = BuildLayout(coords);
    foreach (var key in keys)
    {
        tracks[key] = new LayoutEntry(Points: points, LengthM: lengthMetres);
    }

    var name = feature.GetProperty("properties").GetProperty("Name").GetString();
    Console.WriteLine($"{id,-9} → {string.Join(", ", keys),-24} {points.Count,3} pts, {lengthMetres / 1000:F3} km ({name})");
}

var output = JsonSerializer.Serialize(
    new OutputFile(Version: 1, Tracks: tracks),
    new JsonSerializerOptions { WriteIndented = false });

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, output);
Console.WriteLine($"{outPath}: {new FileInfo(outPath).Length / 1024.0:F0} KB, {tracks.Count} keys");
return 0;

// Converts one circuit's LineString into simplified metre-space points.
static (List<double[]> Points, double LengthMetres) BuildLayout(JsonElement coordinates)
{
    const double EarthRadius = 6_371_000;
    const double SimplifyMetres = 4;

    // Equirectangular projection around the centroid: x = east, y = north, metres.
    var lons = new List<double>();
    var lats = new List<double>();
    foreach (var pt in coordinates.EnumerateArray())
    {
        lons.Add(pt[0].GetDouble());
        lats.Add(pt[1].GetDouble());
    }

    var lat0 = lats.Average() * Math.PI / 180;
    var lon0 = lons.Average() * Math.PI / 180;
    var ring = new List<(double X, double Y)>(lons.Count + 1);
    for (var i = 0; i < lons.Count; i++)
    {
        ring.Add((EarthRadius * Math.Cos(lat0) * (lons[i] * Math.PI / 180 - lon0),
                  EarthRadius * (lats[i] * Math.PI / 180 - lat0)));
    }

    // Close the loop (the game tracks are circuits).
    if (Distance(ring[0], ring[^1]) > 1)
    {
        ring.Add(ring[0]);
    }

    var simplified = DouglasPeucker(ring, SimplifyMetres);

    // Round to 0.1 m to keep the embedded JSON small, and compute arc length.
    var points = simplified
        .Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) })
        .ToList<double[]>();
    var length = 0.0;
    for (var i = 1; i < points.Count; i++)
    {
        length += Math.Sqrt(
            Math.Pow(points[i][0] - points[i - 1][0], 2) +
            Math.Pow(points[i][1] - points[i - 1][1], 2));
    }

    return (points, Math.Round(length, 1));
}

static double Distance((double X, double Y) a, (double X, double Y) b)
{
    var dx = a.X - b.X;
    var dy = a.Y - b.Y;
    return Math.Sqrt(dx * dx + dy * dy);
}

// Classic recursive Douglas-Peucker; keeps first/last so a closed ring stays closed.
static List<(double X, double Y)> DouglasPeucker(List<(double X, double Y)> points, double epsilon)
{
    if (points.Count <= 2)
    {
        return points;
    }

    var keep = new bool[points.Count];
    keep[0] = keep[^1] = true;
    SimplifyRange(points, 0, points.Count - 1, epsilon, keep);
    return Enumerable.Range(0, points.Count).Where(i => keep[i]).Select(i => points[i]).ToList();
}

static void SimplifyRange(List<(double X, double Y)> points, int first, int last, double epsilon, bool[] keep)
{
    if (last - first < 2)
    {
        return;
    }

    var a = points[first];
    var b = points[last];
    var maxDistance = 0.0;
    var maxIndex = first;
    var segment = Distance(a, b);
    for (var i = first + 1; i < last; i++)
    {
        var d = PointSegmentDistance(points[i], a, b, segment);
        if (d > maxDistance)
        {
            maxDistance = d;
            maxIndex = i;
        }
    }

    if (maxDistance > epsilon)
    {
        keep[maxIndex] = true;
        SimplifyRange(points, first, maxIndex, epsilon, keep);
        SimplifyRange(points, maxIndex, last, epsilon, keep);
    }
}

// Distance from p to the a-b segment (the degenerate case falls back to point distance).
static double PointSegmentDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b, double segmentLength)
{
    if (segmentLength < 1e-9)
    {
        return Distance(p, a);
    }

    var t = ((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / (segmentLength * segmentLength);
    t = Math.Max(0, Math.Min(1, t));
    return Distance(p, (a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ERCTelemetry.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName
        ?? throw new InvalidDataException("Run from inside the ERCTelemetry repository (ERCTelemetry.slnx not found).");
}

sealed record OutputFile(int Version, Dictionary<string, object> Tracks);
sealed record LayoutEntry(List<double[]> Points, double LengthM);