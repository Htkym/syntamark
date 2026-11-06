using System.Reflection;

internal static class ReviewFixChecks
{
    internal static void Run(Assembly runtime, Assembly portable)
    {
        var cases = new (string Id, Action Run)[]
        {
            ("reference-container-range", () => CommonMarkBlockChecks.ReviewReferenceRanges(runtime, portable)),
            ("html-container-tab", () => CommonMarkBlockChecks.ReviewHtmlTabs(runtime, portable)),
            ("shortcut-reference-fallback", () => CommonMarkInlineChecks.ReviewShortcutFallback(portable)),
            ("legacy-strikethrough-range", () => MarkdownSyntaxChecks.ReviewStrikethroughRanges(portable)),
        };
        var failures = new List<Exception>();
        foreach (var (id, run) in cases)
        {
            try
            {
                run();
                Console.WriteLine("PASS review regression: " + id);
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL review regression: " + id + ": " + error.Message);
                failures.Add(new InvalidOperationException(id, error));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("Review regressions failed.", failures);
    }
}
