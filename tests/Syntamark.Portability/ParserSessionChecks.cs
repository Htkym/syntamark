using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ParserSessionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;

    internal static void Run(Assembly portable)
    {
        foreach (var assembly in new[] { typeof(Syntamark.MarkdownParser).Assembly, portable })
        {
            var host = new Host(assembly);
            foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
            {
                var options = host.Options(profile);
                foreach (var lineBreak in new[] { "\n", "\r\n", "\r" })
                {
                    var raw = "Alpha" + lineBreak + lineBreak + "Beta 😀" + lineBreak + lineBreak + "Gamma";
                    var original = host.Create(raw, options, "v0");
                    var saved = Snapshot(original);
                    var session = original;
                    for (var step = 0; step < 4; step++)
                    {
                        var position = raw.IndexOf(step % 2 == 0 ? "Beta" : "Zeta", StringComparison.Ordinal);
                        var text = step % 2 == 0 ? "Zeta" : "Beta";
                        var edit = host.Edits((position, 4, text));
                        raw = raw.Remove(position, 4).Insert(position, text);
                        session = host.Apply(session, edit, "v" + (step + 1));
                        host.EqualOracle(session, raw, options, "v" + (step + 1));
                        Check(Get(session, "Mode").ToString() == "Delta" && (int)Get(session, "ReusedBlocks") == 2
                            && (int)Get(session, "ParsedBlocks") == 1, "A local edit must reuse the actual prefix and suffix paragraphs.");
                        Check(SharedResolved(original, session) == 2, "Delta must retain the original resolved blocks, not only report a counter.");
                    }
                    Check(Snapshot(original) == saved, "Continuous edit and undo must not mutate the original session.");
                    var unicode = host.Apply(original, host.Edits((raw.IndexOf("😀", StringComparison.Ordinal), 2, "文書")), "unicode");
                    host.EqualOracle(unicode, raw.Replace("😀", "文書", StringComparison.Ordinal), options, "unicode");
                    var multi = host.Apply(original, host.Edits((0, 1, "O"), (raw.Length - 1, 1, "z")), "multi");
                    host.EqualOracle(multi, "O" + raw[1..^1] + "z", options, "multi");
                    Check((int)Get(multi, "ReusedBlocks") == 1, "Disjoint edits must not reuse either changed paragraph.");
                    host.Failures(original);
                    host.LengthChanges(lineBreak, options);
                }
                foreach (var raw in new[]
                {
                    "# Heading\n\nBeta\n", "text\n====\n\nBeta\n", "- item\n\nBeta\n", "> quote\n\nBeta\n",
                    "```\ncode\n```\n\nBeta\n", "[use][r]\n\n[r]: /target\n\nBeta\n",
                    "---\ntitle: x\n---\n\nBeta\n", "<div>\nx\n</div>\n\nBeta\n", "<Component />\n\nBeta\n",
                    ":::note\nx\n:::\n\nBeta\n", "a | b\n--- | ---\n\nBeta\n", "two\nlines\n\nBeta\n", "\uFEFFAlpha\n\nBeta\n",
                })
                {
                    var original = host.Create(raw, options, "old");
                    var position = raw.IndexOf("Beta", StringComparison.Ordinal);
                    var updated = host.Apply(original, host.Edits((position, 4, "Zeta")), "fallback");
                    host.EqualOracle(updated, raw.Remove(position, 4).Insert(position, "Zeta"), options, "fallback");
                    Check(Get(updated, "Mode").ToString() == "Full" && (int)Get(updated, "ReusedBlocks") == 0,
                        "Non-proven syntax must take full parsing.");
                }
                const string plain = "Alpha\n\nBeta\n\nGamma";
                var basis = host.Create(plain, options, "old");
                foreach (var edit in new[] { (5, 2, "\n"), (0, 0, "# "), (5, 1, "x"), (7, 4, "<x/>") })
                {
                    var changed = host.Apply(basis, host.Edits(edit), "boundary");
                    var raw = plain.Remove(edit.Item1, edit.Item2).Insert(edit.Item1, edit.Item3);
                    host.EqualOracle(changed, raw, options, "boundary");
                    Check(Get(changed, "Mode").ToString() == "Full", "Line, boundary and syntax changes must fall back.");
                }
                var invalid = host.Apply(basis, host.Edits((0, 1, "\uD800")), "invalid");
                host.EqualOracle(invalid, "\uD800" + plain[1..], options, "invalid");
                Check(Get(Get(invalid, "Document"), "Status").ToString() == "Failed", "Invalid Unicode must remain Failed.");
                host.ResourceBoundaries(profile);
            }
        }
        Console.WriteLine($"PASS parser sessions: {checks} checks, runtime/netstandard2.0, reuse identity, full oracle, resource boundaries and cancellation; no performance measurements.");
    }

    private static object Get(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static string Json(object? value) => value is null ? "null" : JsonSerializer.Serialize(value, value.GetType());
    private static string Snapshot(object session) => Json(Get(session, "Document")) + Json(Get(session, "Root"));
    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int SharedResolved(object left, object right)
    {
        object[] Values(object value)
        {
            var core = value.GetType().GetField("core", Instance)!.GetValue(value)!;
            var fragments = (IDictionary)core.GetType().GetField("fragments", Instance)!.GetValue(core)!;
            return fragments.Values.Cast<object>().Select(fragment => Get(fragment, "Resolved")).ToArray();
        }
        var old = Values(left);
        return Values(right).Count(value => old.Any(previous => ReferenceEquals(previous, value)));
    }

    private sealed class Host(Assembly assembly)
    {
        private readonly Type parser = assembly.GetType("Syntamark.MarkdownParser", true)!;
        private readonly Type span = assembly.GetType("Syntamark.RawSpan", true)!;
        private readonly Type editType = assembly.GetType("Syntamark.MarkdownTextEdit", true)!;

        internal object Options(string profile, int scan = 16777216, int output = 131072, int input = 1048576)
            => Activator.CreateInstance(assembly.GetType("Syntamark.MarkdownParseOptions", true)!, [input, output, 200, scan, profile, 1])!;
        internal object Create(string raw, object options, string version)
            => Invoke(parser.GetMethod("CreateSession")!, null, [raw, "session", "fixture.md", version, options, CancellationToken.None]);
        internal Array Edits(params (int Start, int Length, string Replacement)[] edits)
        {
            var values = Array.CreateInstance(editType, edits.Length);
            for (var i = 0; i < edits.Length; i++)
                values.SetValue(Activator.CreateInstance(editType, [Activator.CreateInstance(span, [edits[i].Start, edits[i].Length]), edits[i].Replacement]), i);
            return values;
        }
        internal object Apply(object session, Array edits, string version, object? options = null, CancellationToken token = default)
            => Invoke(session.GetType().GetMethod("ApplyEdits")!, session, [Get(Get(session, "Document"), "TextHash"), edits, version, options, token]);

        internal void EqualOracle(object session, string raw, object options, string version)
        {
            var oracle = Invoke(parser.GetMethod("ParseWithSyntax")!, null, [raw, "session", "fixture.md", version, options, CancellationToken.None]);
            Check(Json(Get(session, "Document")) == Json(Get(oracle, "Facts")) && Json(Get(session, "Root")) == Json(Get(oracle, "Root")),
                "Session facts, diagnostics, coverage, spans or syntax differ from full parsing: " + Json(raw));
            var fresh = Create(raw, options, version);
            Check(Equals(Get(session, "ScanUnits"), Get(fresh, "ScanUnits")) && Equals(Get(session, "OutputItems"), Get(fresh, "OutputItems")),
                "Reused paragraphs must replay the complete canonical logical resource charge.");
            for (var i = 0; i <= raw.Length; i++)
                Check(Json(Get(session, "Document").GetType().GetMethod("GetLinePosition")!.Invoke(Get(session, "Document"), [i]))
                    == Json(Get(oracle, "Facts").GetType().GetMethod("GetLinePosition")!.Invoke(Get(oracle, "Facts"), [i])),
                    "Line positions differ from the original UTF-16 source.");
            var altered = JsonNode.Parse(Json(Get(oracle, "Facts")))!;
            if (altered["BodySpan"] is JsonObject body)
                body["Start"] = body["Start"]!.GetValue<int>() + 1;
            else
                altered["TextHash"] = "intentional-difference";
            Check(!JsonNode.DeepEquals(altered, JsonNode.Parse(Json(Get(session, "Document")))), "Oracle comparison must detect a deliberate difference.");
        }

        internal void Failures(object session)
        {
            var saved = Snapshot(session);
            void Reject<T>(Action action) where T : Exception
            {
                try
                {
                    action();
                }
                catch (T)
                {
                    Check(Snapshot(session) == saved, "A rejected candidate changed the old session.");
                    return;
                }
                throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
            }
            Reject<ArgumentException>(() => Invoke(session.GetType().GetMethod("ApplyEdits")!, session,
                ["wrong-hash", Edits((0, 1, "Z")), "bad", null, CancellationToken.None]));
            Reject<ArgumentException>(() => Apply(session, Edits((2, 2, "AB"), (1, 1, "Z")), "bad"));
            Reject<ArgumentException>(() => Apply(session, Edits((int.MaxValue - 1, 1, "Z")), "bad"));
            Reject<ArgumentException>(() => Apply(session, Edits((0, 1, "Z")), "\uD800"));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Reject<OperationCanceledException>(() => Apply(session, Edits((0, 1, "Z")), "cancel", token: canceled.Token));
            // Both candidates start from the same immutable snapshot and must be independent.
            var a = Apply(session, Edits((0, 1, "Z")), "a");
            var b = Apply(session, Edits((0, 1, "O")), "b");
            Check(Snapshot(a) != Snapshot(b) && Snapshot(session) == saved, "Branching edits must retain separate snapshots.");
        }

        internal void LengthChanges(string lineBreak, object options)
        {
            var originalRaw = "Alpha" + lineBreak + lineBreak + "Beta 😀" + lineBreak + lineBreak + "Gamma";
            var original = Create(originalRaw, options, "length-old");
            var saved = Snapshot(original);
            var session = original;
            var raw = originalRaw;
            foreach (var replacement in new[] { "Longer paragraph 😀", "B", "Beta 😀" })
            {
                var start = raw.IndexOf(lineBreak + lineBreak, StringComparison.Ordinal) + 2 * lineBreak.Length;
                var end = raw.IndexOf(lineBreak + lineBreak, start, StringComparison.Ordinal);
                session = Apply(session, Edits((start, end - start, replacement)), "length-" + replacement);
                raw = raw.Remove(start, end - start).Insert(start, replacement);
                EqualOracle(session, raw, options, "length-" + replacement);
                Check(Get(session, "Mode").ToString() == "Delta" && (int)Get(session, "ReusedBlocks") == 2
                    && (int)Get(session, "RebasedBlocks") == 1 && (int)Get(session, "ParsedBlocks") == 1,
                    "Length changes must reparse one paragraph and rebase only the reused suffix payload.");
                Check(SharedResolved(original, session) == 1, "Length changes must keep the unchanged prefix node by reference.");
                Check(SharedTextPayloads(original, session) >= 2, "Rebased suffixes must retain old parsed text payloads, not parse the suffix again.");
            }
            Check(raw == originalRaw && Snapshot(original) == saved, "Length-changing edit/undo must preserve the original session.");
            foreach (var edit in new[] { (0, 0, "New "), (5, 0, " tail"), (0, 5, ""), (0, 1, "😀") })
            {
                var next = Apply(original, Edits(edit), "insert-delete");
                EqualOracle(next, originalRaw.Remove(edit.Item1, edit.Item2).Insert(edit.Item1, edit.Item3), options, "insert-delete");
                Check((int)Get(next, "ReusedBlocks") == 2 && (int)Get(next, "RebasedBlocks") == 2,
                    "Insertions and deletions within the first paragraph must preserve and rebase both suffix paragraphs.");
            }
            var secondStart = originalRaw.IndexOf("Beta", StringComparison.Ordinal);
            var thirdStart = originalRaw.IndexOf("Gamma", StringComparison.Ordinal);
            var multiple = Apply(original, Edits((0, 5, "First longer"), (thirdStart, 5, "G")), "multiple-length");
            var expected = "First longer" + originalRaw[5..thirdStart] + "G";
            EqualOracle(multiple, expected, options, "multiple-length");
            Check((int)Get(multiple, "ReusedBlocks") == 1 && (int)Get(multiple, "RebasedBlocks") == 1,
                "Disjoint length changes must use cumulative old-source offsets for the unchanged middle paragraph.");
            var shifted = Apply(original, Edits((secondStart, 0, "前")), "unicode-length");
            EqualOracle(shifted, originalRaw.Insert(secondStart, "前"), options, "unicode-length");
        }

        private static int SharedTextPayloads(object left, object right)
        {
            object[] Payloads(object session)
            {
                var core = session.GetType().GetField("core", Instance)!.GetValue(session)!;
                var fragments = (IDictionary)core.GetType().GetField("fragments", Instance)!.GetValue(core)!;
                return fragments.Values.Cast<object>().Select(fragment =>
                    ((IEnumerable)Get(Get(fragment, "Resolved"), "Inlines")).Cast<object>().Select(inline => Get(inline, "Text")).Single()).ToArray();
            }
            var previous = Payloads(left);
            return Payloads(right).Count(text => previous.Any(value => ReferenceEquals(value, text)));
        }

        internal void ResourceBoundaries(string profile)
        {
            const string raw = "Alpha     \n\nBeta\n\nGamma";
            const string changed = "Alpha beta\n\nBeta\n\nGamma";
            var generous = Create(raw, Options(profile), "old");
            var minimumScan = checked((int)(long)Get(generous, "ScanUnits"));
            var minimumOutput = (int)Get(generous, "OutputItems") + 1;
            foreach (var scan in new[] { 1, minimumScan - 1, minimumScan, minimumScan + 1, 16777216 })
            {
                var options = Options(profile, scan: scan);
                var previous = Create(raw, options, "old");
                var next = Apply(previous, Edits((0, 10, "Alpha beta")), "new");
                EqualOracle(next, changed, options, "new");
                var expanded = Apply(previous, Edits((0, 0, "More ")), "scan-expanded");
                EqualOracle(expanded, raw.Insert(0, "More "), options, "scan-expanded");
                if (scan == minimumScan)
                    Check(Get(expanded, "Fallback").ToString() == "ResourceBoundary",
                        "A shifted-suffix candidate must not bypass the canonical scan boundary.");
                if (scan == minimumScan)
                    Check(Get(next, "Fallback").ToString() == "ResourceBoundary", "A resource boundary in a reuse candidate must rerun full parsing.");
            }
            foreach (var output in new[] { 1, minimumOutput - 1, minimumOutput, minimumOutput + 1 })
            {
                var options = Options(profile, output: output);
                var previous = Create(raw, options, "old");
                EqualOracle(Apply(previous, Edits((0, 10, "Alpha beta")), "new"), changed, options, "new");
                EqualOracle(Apply(previous, Edits((0, 0, "More ")), "output-expanded"), raw.Insert(0, "More "), options, "output-expanded");
            }
            var limited = Options(profile, input: raw.Length - 1);
            EqualOracle(Apply(generous, Edits((0, 10, "Alpha beta")), "new", limited), changed, limited, "new");
            var lengthOptions = Options(profile, input: raw.Length);
            var lengthBasis = Create(raw, lengthOptions, "length-limit-old");
            var longer = raw.Insert(0, "More ");
            var rejected = Apply(lengthBasis, Edits((0, 0, "More ")), "length-limit-new");
            EqualOracle(rejected, longer, lengthOptions, "length-limit-new");
            Check(Get(Get(rejected, "Document"), "Status").ToString() == "Failed" && (int)Get(rejected, "ReusedBlocks") == 0,
                "Length changes must not bypass the complete-source input limit by reusing suffixes.");
        }

        private static object Invoke(MethodInfo method, object? target, object?[] arguments)
        {
            try
            {
                return method.Invoke(target, arguments)!;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
