using Syntamark;

internal static class SourceMapChecks
{
    internal static void Run()
    {
        var original = new SourceSnapshot("code.cs", "1", "😀 &amp; tail");
        var included = new SourceSnapshot("include", "1", "😀 &amp; tail\n😀 &amp; tail");
        var first = original.Text.Length;
        var includes = new SourceMap(included, new[] {
            Linear(0, first, original, 0),
            new SourceMapSegment(new RawSpan(first, 1), null, MarkdownSegmentKind.Unknown),
            Linear(first + 1, first, original, 0),
        });
        var rendered = new SourceSnapshot("html", "1", "😀 & tail\n😀 & tail");
        var rendering = new SourceMap(rendered, new[] {
            Linear(0, 3, included, 0),
            new SourceMapSegment(new RawSpan(3, 1), new SourceRange(included, new RawSpan(3, 5)), MarkdownSegmentKind.Atomic),
            Linear(4, 5, included, 8),
            new SourceMapSegment(new RawSpan(9, 1), null, MarkdownSegmentKind.Unknown),
            Linear(10, 3, included, first + 1),
            new SourceMapSegment(new RawSpan(13, 1), new SourceRange(included, new RawSpan(first + 4, 5)), MarkdownSegmentKind.Atomic),
            Linear(14, 5, included, first + 9),
        }).Compose(new[] { includes });
        var decoded = rendering.Map(new RawSpan(3, 1));
        Check(decoded.Precision == MarkdownMappingPrecision.CoveringTokens && !decoded.CanEdit
            && decoded.Origins.Single().Span == new RawSpan(3, 5), "Decoded entity must cover its original token.");
        var second = rendering.Map(new RawSpan(10, 2));
        Check(second.CanEdit && second.Origins.Single().Source.SourceId == "code.cs"
            && second.Origins.Single().Span == new RawSpan(0, 2), "Repeated include must return the correct original range.");
        Check(!rendering.Map(new RawSpan(10, 1)).CanEdit, "An edit must not split a surrogate pair.");
        var all = rendering.Map(new RawSpan(0, rendered.Text.Length));
        Check(all.Precision == MarkdownMappingPrecision.Partial && !all.CanEdit
            && all.Origins.Count == 2, "Generated text and repeated includes must not become one exact editable envelope.");
        var edit = new SourceEdit(second.Origins.Single(), "😀", "ok");
        Check(edit.IsApplicable(original) && !edit.IsApplicable(new SourceSnapshot("code.cs", "2", original.Text))
            && !edit.IsApplicable(new SourceSnapshot("code.cs", "1", "😀 changed")), "Stale versions and reused versions with changed text must reject edits.");
        var other = new SourceSnapshot("other", "1", "tail");
        var joined = new SourceSnapshot("joined", "1", "tailtail");
        var mixed = new SourceMap(joined, new[] { Linear(0, 4, original, 9), Linear(4, 4, other, 0) });
        var fragments = mixed.Map(new RawSpan(0, 8));
        Check(fragments.Precision == MarkdownMappingPrecision.Exact && fragments.Origins.Count == 2 && !fragments.CanEdit,
            "Multiple input files must stay distinct even when their text matches.");
        Check(!mixed.Map(new RawSpan(4, 0)).CanEdit, "An ambiguous insertion boundary cannot be edited.");
        var a = new SourceSnapshot("a", "1", "x");
        var b = new SourceSnapshot("b", "1", "x");
        var c = new SourceSnapshot("c", "1", "x");
        var r = new SourceSnapshot("r", "1", "x");
        var aMap = new SourceMap(a, new[] { Linear(0, 1, r, 0) });
        var bMap = new SourceMap(b, new[] { Linear(0, 1, a, 0) });
        var three = new SourceMap(c, new[] { Linear(0, 1, b, 0) }).Compose(new[] { bMap, aMap }).Map(new RawSpan(0, 1));
        Check(three.CanEdit && three.Origins.Single().Source.SourceId == "r", "A flat composition list must resolve every intermediate snapshot.");
        var cycle = new SourceMap(a, new[] { Linear(0, 1, b, 0) }).Compose(new[] { bMap }).Map(new RawSpan(0, 1));
        Check(cycle.Precision == MarkdownMappingPrecision.Unknown && !cycle.CanEdit, "A snapshot cycle must terminate without claiming an editable source.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            rendering.Map(new RawSpan(0, 1), cancelled.Token);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS composable source maps and versioned edits");
    }

    private static SourceMapSegment Linear(int start, int length, SourceSnapshot source, int origin)
        => new(new RawSpan(start, length), new SourceRange(source, new RawSpan(origin, length)), MarkdownSegmentKind.Linear);
    private static void Check(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
