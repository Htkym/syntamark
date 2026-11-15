using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class LinkEditChecks
{
    internal static void Run(Assembly portable)
    {
        const string destination = "https://example.test/dir a(b)<c>\\d[😀]'\"`?a=1&copy;=2|3%2f#節";
        var inputs = new[] {
            "[guide](old.md \"keep\")\r\n\r\n`[code](old.md)`\r\n\r\n```md\r\n[code](old.md)\r\n```\r\n",
            "![alt](<old.md> 'keep')",
            "\uFEFF---\r\ntitle: hidden\r\n---\r\n[empty]()\r\n",
            "[a][r] ![b][r]\r\n\r\n[r]: old.md \"keep\"\r\n",
            "> [a][r]\r\n>\r\n> [r]: <old.md> 'keep'\r\n",
            "| A | B |\r\n| --- | --- |\r\n| [guide](old.md \"keep\") | other |\r\n",
        };
        foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2", "syntamark-docs/2" })
        {
            var options = new MarkdownParseOptions(profileId: profile);
            foreach (var input in inputs)
            {
                var source = new SourceSnapshot("guide.md", "r1", input);
                var before = MarkdownParser.Analyze(input, MarkdownAnalysisParts.Links, "edits", source.SourceId, source.Version, options);
                Check(before.Status == MarkdownParseStatus.Complete && before.Links!.Count > 0, "Fixture did not yield complete link facts.");
                var index = before.Links!.ToList().FindIndex(link => link.Kind != MarkdownLinkKind.ReferenceDefinition);
                Check(MarkdownLinkEdits.TryCreateDestinationEdit(source, before, index, destination, out var edit, out var reason)
                    && reason == MarkdownLinkEditFailure.None && edit!.IsApplicable(source), "Expected a guarded destination edit.");
                var range = edit!.Target.Span;
                var changed = input.Remove(range.Start, range.Length).Insert(range.Start, edit.Replacement);
                var after = MarkdownParser.Analyze(changed, MarkdownAnalysisParts.Links, "edits", source.SourceId, "r2", options);
                Check(after.Status == MarkdownParseStatus.Complete && after.Links!.Count == before.Links!.Count,
                    "Editing a destination changed link recognition.");
                for (var i = 0; i < before.Links!.Count; i++)
                {
                    var old = before.Links[i];
                    var current = after.Links![i];
                    Check(current.Kind == old.Kind && current.Resolution == old.Resolution && current.Image == old.Image
                        && current.Label.Text == old.Label.Text && current.Title == old.Title && current.ReferenceLabel == old.ReferenceLabel,
                        "Editing a destination changed label, title, image or reference semantics.");
                    Check(current.Target == (old.TargetRawSpan == range ? destination : old.Target),
                        "Reparsed destination differs from the unescaped request.");
                    if (old.TargetRawSpan == range)
                    {
                        Check(MarkdownLinkEdits.TryCreateDestinationEdit(source, before, i, destination, out var shared, out _)
                            && shared!.Target.Span == range && shared.Replacement == edit.Replacement,
                            "Shared reference uses and the definition must propose the same edit.");
                    }
                }
                var counterpart = PortableEdit(portable, input, profile, index, destination);
                Check(counterpart.Success && counterpart.Reason == reason.ToString()
                    && counterpart.Edit == JsonSerializer.Serialize(edit), "Runtime and portable destination edits differ.");
            }
        }

        const string raw = "[guide](old.md)";
        var snapshot = new SourceSnapshot("guide.md", "r1", raw);
        var analysis = MarkdownParser.Analyze(raw, MarkdownAnalysisParts.Links, "edits", snapshot.SourceId, snapshot.Version);
        Reject(new SourceSnapshot("other.md", "r1", raw), analysis, 0, "next", MarkdownLinkEditFailure.SnapshotMismatch);
        Reject(new SourceSnapshot("guide.md", "r2", raw), analysis, 0, "next", MarkdownLinkEditFailure.SnapshotMismatch);
        Reject(new SourceSnapshot("guide.md", "r1", raw + "x"), analysis, 0, "next", MarkdownLinkEditFailure.SnapshotMismatch);
        Reject(snapshot, MarkdownParser.Analyze(raw, MarkdownAnalysisParts.Syntax, "edits", "guide.md", "r1"), 0, "next",
            MarkdownLinkEditFailure.LinksUnavailable);
        Reject(snapshot, MarkdownParser.Analyze(raw, MarkdownAnalysisParts.Links, "edits", "guide.md", "r1",
            new MarkdownParseOptions(maxScanUnits: 1)), 0, "next", MarkdownLinkEditFailure.AnalysisIncomplete);
        Reject(snapshot, analysis, -1, "next", MarkdownLinkEditFailure.LinkNotFound);
        Reject(snapshot, analysis, 1, "next", MarkdownLinkEditFailure.LinkNotFound);
        Reject(snapshot, analysis, 0, "", MarkdownLinkEditFailure.EmptyDestination);
        foreach (var invalid in new[] { "a\nb", "a\tb", "a\0b", "\uD800", "a\u007fb" })
            Reject(snapshot, analysis, 0, invalid, MarkdownLinkEditFailure.InvalidDestination);
        foreach (var item in new[] { ("[missing][ref]", MarkdownLinkEditFailure.UnresolvedReference),
            ("<https://example.test/>", MarkdownLinkEditFailure.UnsupportedLinkKind),
            ("<a@example.test>", MarkdownLinkEditFailure.UnsupportedLinkKind) })
        {
            var source = new SourceSnapshot("guide.md", "r1", item.Item1);
            var parsed = MarkdownParser.Analyze(source.Text, MarkdownAnalysisParts.Links, "edits", source.SourceId, source.Version);
            Reject(source, parsed, 0, "next", item.Item2);
            var counterpart = PortableEdit(portable, source.Text, "lithosharp-markdown/1", 0, "next");
            Check(!counterpart.Success && counterpart.Edit == "null" && counterpart.Reason == item.Item2.ToString(),
                "Runtime and portable rejection differs.");
        }

        const string body = "---\r\ntitle: literal\r\n---\r\n[guide](old.md)\r\n\r\nNote[^a].\r\n\r\n[^a]: Kept.\r\n";
        var bodySource = new SourceSnapshot("expanded.md", "r1", body);
        var bodyOptions = new MarkdownParseOptions(MarkdownInputKind.Body, profileId: "syntamark-docs/2");
        var bodyAnalysis = MarkdownParser.Analyze(body, MarkdownAnalysisParts.All | MarkdownAnalysisParts.UnsupportedSyntax,
            "edits", bodySource.SourceId, bodySource.Version, bodyOptions);
        Check(bodyAnalysis.FrontMatter!.State == MarkdownFrontMatterState.Absent && bodyAnalysis.BodySpan == new RawSpan(0, body.Length),
            "Body input must not consume transformed text as front matter.");
        Check(MarkdownLinkEdits.TryCreateDestinationEdit(bodySource, bodyAnalysis, 0, destination, out var bodyEdit, out _),
            "Body/profile identity must remain usable for edits.");
        var bodyChanged = body.Remove(bodyEdit!.Target.Span.Start, bodyEdit.Target.Span.Length).Insert(bodyEdit.Target.Span.Start, bodyEdit.Replacement);
        var bodyReparsed = MarkdownParser.Analyze(bodyChanged, bodyAnalysis.RequestedParts, "edits", bodySource.SourceId, "r2", bodyOptions);
        Check(bodyReparsed.FrontMatter!.State == MarkdownFrontMatterState.Absent && bodyReparsed.ProfileId == "syntamark-docs/2"
            && bodyReparsed.Root!.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.FootnoteDefinition) == 1,
            "Destination edits must preserve the body profile and footnotes.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            MarkdownLinkEdits.TryCreateDestinationEdit(snapshot, analysis, 0, "next", out _, out _, cancelled.Token);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS guarded destination edits, same-profile reparse, shared references and portable parity");
    }

    private static void Reject(SourceSnapshot source, MarkdownAnalysisResult analysis, int index, string destination, MarkdownLinkEditFailure expected)
        => Check(!MarkdownLinkEdits.TryCreateDestinationEdit(source, analysis, index, destination, out var edit, out var failure)
            && edit is null && failure == expected, "Expected edit rejection: " + expected);

    private static (bool Success, string Reason, string Edit) PortableEdit(Assembly portable, string raw, string profile, int index, string destination)
    {
        var options = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
            [1048576, 131072, 200, 16777216, profile, 1]);
        var parts = Enum.ToObject(portable.GetType("Syntamark.MarkdownAnalysisParts", true)!, 8);
        var parsed = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Analyze")!
            .Invoke(null, [raw, parts, "edits", "guide.md", "r1", options, CancellationToken.None]);
        var source = Activator.CreateInstance(portable.GetType("Syntamark.SourceSnapshot", true)!, ["guide.md", "r1", raw]);
        object?[] arguments = [source, parsed, index, destination, null, null, CancellationToken.None];
        var success = (bool)portable.GetType("Syntamark.MarkdownLinkEdits", true)!.GetMethod("TryCreateDestinationEdit")!.Invoke(null, arguments)!;
        return (success, arguments[5]!.ToString()!, arguments[4] is { } edit ? JsonSerializer.Serialize(edit, edit.GetType()) : "null");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
