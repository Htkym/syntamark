using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syntamark;

internal static class QueryChecks
{
    private const string BaselineHash = "d03842829c409e51f2bc068a7188fd949b0044ac4575029c04b11ee0d06309a9";

    internal static void Run()
    {
        var hash = CaptureHash();
        Check(hash == BaselineHash, "SM05 query results differ from the fixed de0a642 baseline: " + hash);
        var crlf = MarkdownParser.Parse("a\r\nb", "query", "crlf");
        var expected = new[] { (1, 1), (1, 2), (2, 1), (2, 1), (2, 2) };
        foreach (var offset in Enumerable.Range(0, expected.Length))
            Check(crlf.GetLinePosition(offset) == new MarkdownLinePosition(expected[offset].Item1, expected[offset].Item2),
                "CRLF boundary " + offset);
        foreach (var raw in Inputs())
        {
            var fresh = MarkdownParser.Parse(raw, "query", "fixture");
            var expectedSnapshot = Snapshot(MarkdownParser.Parse(raw, "query", "fixture"), raw.Length);
            Parallel.For(0, 16, _ => Check(Snapshot(fresh, raw.Length) == expectedSnapshot, "Concurrent cold query initialization"));
        }
        foreach (var offset in new[] { -1, 5 })
        {
            try
            {
                crlf.GetLinePosition(offset);
                throw new InvalidOperationException("Out-of-range query accepted");
            }
            catch (ArgumentOutOfRangeException exception)
            {
                Check(exception.ParamName == "rawOffset", "Line query parameter contract");
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            task = "SM05",
            status = "PASS",
            baseline = "de0a642",
            snapshotSha256 = hash,
            checks = "all offsets/all projection subranges/zero-width and unknown maps/concurrent initialization"
        }));
    }

    internal static string CaptureHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Inputs().Select(raw => Snapshot(MarkdownParser.Parse(raw, "query", "fixture"), raw.Length)).ToArray())))).ToLowerInvariant();

    private static string Snapshot(MarkdownDocument document, int rawLength)
    {
        var projections = document.Headings.Select(heading => heading.Text)
            .Concat(document.TextRegions.Select(region => region.Content))
            .Concat(document.Links.Select(link => link.Label))
            .Concat(document.Fences.Select(fence => fence.Content));
        var projected = projections.Select(projection => new
        {
            projection.Text,
            queries = projection.Text is null ? new[] { new { start = 100, length = 1, mapped = projection.Map(new RawSpan(100, 1)) } }
                : Enumerable.Range(0, projection.Text.Length + 1)
                    .SelectMany(start => Enumerable.Range(0, projection.Text.Length - start + 1)
                        .Select(length => new { start, length, mapped = projection.Map(new RawSpan(start, length)) })).ToArray()
        }).ToArray();
        var positions = Enumerable.Range(0, rawLength + 1).Select(document.GetLinePosition).ToArray();
        Check(positions.SequenceEqual(Enumerable.Range(0, rawLength + 1).Select(document.GetLinePosition)), "Repeated indexed positions");
        return JsonSerializer.Serialize(new { document.Status, projected, positions });
    }

    private static string[] Inputs() => new[]
    {
        "", "a\r\nb\nc\rd", "\r\n\r\r\n\n",
        "\uFEFF# A&amp;\\*😀\r\n\r\n> a\r\n> b\r\n\r\n    code\r\n",
        "| H |\n| - |\n| a\\|b [x]() |\n", "<Widget> body\n", "` one\r\ntwo `\n",
    };

    private static void Check(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
