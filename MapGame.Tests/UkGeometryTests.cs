using MapGame;

namespace MapGame.Tests;

public sealed class UkGeometryTests
{
    private static readonly string[] UkIds = ["GB-ENG", "GB-SCT", "GB-WLS", "GB-NIR"];
    private readonly Dictionary<string, Polygon[]> _shapes;

    public UkGeometryTests()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.maps.json")!;
        _shapes = GameCatalog.Load(stream).GetRegion("europe").Features
            .Where(feature => UkIds.Contains(feature.Id) || feature.Id == "IRL")
            .ToDictionary(feature => feature.Id, feature => Polygons(feature.Geometry));
    }

    [Theory]
    [InlineData("GB-ENG", -0.1276, 51.5072)] // London
    [InlineData("GB-ENG", -2.2446, 53.4839)] // Manchester
    [InlineData("GB-SCT", -3.1883, 55.9533)] // Edinburgh
    [InlineData("GB-SCT", -4.2518, 55.8642)] // Glasgow
    [InlineData("GB-SCT", -1.241, 60.231)] // Tingwall, Shetland
    [InlineData("GB-WLS", -3.1791, 51.4816)] // Cardiff
    [InlineData("GB-WLS", -4.076, 53.068)] // Snowdon / Yr Wyddfa
    [InlineData("GB-NIR", -5.9301, 54.5973)] // Belfast
    [InlineData("GB-NIR", -7.309, 54.997)] // Derry/Londonderry
    [InlineData("IRL", -6.2603, 53.3498)] // Dublin
    [InlineData("IRL", -8.4756, 51.8985)] // Cork
    public void RealLocationsBelongToExactlyTheirOwnCountry(string expected, double x, double y)
    {
        Assert.Equal(new[] { expected }, _shapes.Where(shape => Contains(shape.Value, new(x, y)))
            .Select(shape => shape.Key));
    }

    [Fact]
    public void CountriesUseDetailedClosedRealOutlinesAndDoNotOverlap()
    {
        Assert.Equal(5, _shapes.Count);
        foreach (var polygons in _shapes.Values)
        {
            Assert.True(polygons.Sum(polygon => polygon.Rings.Sum(ring => ring.Length)) > 50);
            foreach (var ring in polygons.SelectMany(polygon => polygon.Rings))
            {
                Assert.True(ring.Length >= 4);
                Assert.Equal(ring[0], ring[^1]);
                Assert.All(ring, point =>
                {
                    Assert.InRange(point.X, -11, 2);
                    Assert.InRange(point.Y, 49, 61);
                });
            }
        }

        var ids = _shapes.Keys.ToArray();
        for (var i = 0; i < ids.Length; i++)
        for (var j = i + 1; j < ids.Length; j++)
        {
            var first = _shapes[ids[i]];
            var second = _shapes[ids[j]];
            Assert.DoesNotContain(Vertices(first), point => Contains(second, point));
            Assert.DoesNotContain(Vertices(second), point => Contains(first, point));
            foreach (var a in Edges(first))
            foreach (var b in Edges(second))
                Assert.False(Crosses(a, b), $"Crossed boundary between {ids[i]} and {ids[j]}.");
        }
    }

    [Theory]
    [InlineData("GB-ENG", "GB-SCT")]
    [InlineData("GB-ENG", "GB-WLS")]
    [InlineData("GB-NIR", "IRL")]
    public void AdjacentCountriesHaveExactlySharedBorderSegments(string first, string second)
    {
        var edges = Edges(_shapes[first]).ToHashSet();
        var common = Edges(_shapes[second]).Count(edge =>
            edges.Contains(edge) || edges.Contains((edge.B, edge.A)));
        Assert.True(common >= 5, $"Only {common} shared edges for {first}/{second}.");
    }

    private static Polygon[] Polygons(MapGeometry geometry)
    {
        var coordinates = geometry.Coordinates;
        return (geometry.Type == "Polygon" ? new[] { coordinates } : coordinates.EnumerateArray().ToArray())
            .Select(polygon => new Polygon(polygon.EnumerateArray().Select(ring =>
                ring.EnumerateArray().Select(point => new Point(point[0].GetDouble(), point[1].GetDouble()))
                    .ToArray()).ToArray())).ToArray();
    }

    private static IEnumerable<Point> Vertices(Polygon[] polygons) =>
        polygons.SelectMany(polygon => polygon.Rings).SelectMany(ring => ring);

    private static IEnumerable<(Point A, Point B)> Edges(Polygon[] polygons) =>
        polygons.SelectMany(polygon => polygon.Rings)
            .SelectMany(ring => ring.Zip(ring.Skip(1), (a, b) => (a, b)));

    private static double Side(Point a, Point b, Point p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static bool Crosses((Point A, Point B) a, (Point A, Point B) b) =>
        Side(a.A, a.B, b.A) * Side(a.A, a.B, b.B) < -1e-16
        && Side(b.A, b.B, a.A) * Side(b.A, b.B, a.B) < -1e-16;

    private static bool OnBoundary(Point[] ring, Point p) =>
        ring.Zip(ring.Skip(1)).Any(edge =>
            Math.Abs(Side(edge.First, edge.Second, p)) < 1e-10
            && p.X >= Math.Min(edge.First.X, edge.Second.X) - 1e-10
            && p.X <= Math.Max(edge.First.X, edge.Second.X) + 1e-10
            && p.Y >= Math.Min(edge.First.Y, edge.Second.Y) - 1e-10
            && p.Y <= Math.Max(edge.First.Y, edge.Second.Y) + 1e-10);

    private static bool InsideRing(Point[] ring, Point p)
    {
        var inside = false;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var a = ring[i];
            var b = ring[i + 1];
            if ((a.Y > p.Y) != (b.Y > p.Y)
                && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool Contains(Polygon[] polygons, Point p) =>
        polygons.Any(polygon => !polygon.Rings.Any(ring => OnBoundary(ring, p))
            && InsideRing(polygon.Rings[0], p)
            && !polygon.Rings.Skip(1).Any(ring => InsideRing(ring, p)));

    private sealed record Polygon(Point[][] Rings);
    private readonly record struct Point(double X, double Y);
}
