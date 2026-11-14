using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Syntamark;

internal static class SessionAnalysisChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Scope = "analysis-session";
    private const string Source = "guide.md";
    private const string Input = "---\r\ntitle: Guide\r\n---\r\n# Guide\r\n\r\nAlpha\r\n\r\nBeta 😀\r\n\r\nGamma\r\n\r\nTerm {#old}\r\n\r\n```md\r\n{#code}\r\n```\r\n";
    private static readonly MarkdownAnalysisParts[] Selections =
    [
        MarkdownAnalysisParts.UnsupportedSyntax,
        MarkdownAnalysisParts.FrontMatter | MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Sections
            | MarkdownAnalysisParts.Links | MarkdownAnalysisParts.Fences | MarkdownAnalysisParts.UnsupportedSyntax,
        MarkdownAnalysisParts.All | MarkdownAnalysisParts.UnsupportedSyntax,
    ];

    internal static void Run(Assembly portable)
    {
        foreach (var core in new[] { false, true })
        {
            var runtime = new Host(typeof(MarkdownParser).Assembly, core);
            var source = new Host(portable, core);
            foreach (var parts in Selections)
                Check(Exercise(runtime, parts).SequenceEqual(Exercise(source, parts)),
                    "Runtime and netstandard analysis sessions must expose equal selected results and retained blocks.");
            foreach (var host in new[] { runtime, source })
            {
                const string raw = "# Guide\n\nA paragraph.\n";
                const MarkdownAnalysisParts parts = MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Syntax;
                var options = host.Options(output: 4);
                var limited = host.Create(raw, parts, options, "late-limit");
                host.EqualOracle(limited, raw, parts, options, "late-limit");
                Check(host.Blocks(limited) is { } blocks && blocks.Cast<object>().Count() == 2
                    && Get(Get(limited, "Analysis")!, "Status")!.ToString() == "Partial"
                    && Get(Get(limited, "Analysis")!, "Root") is null,
                    "Completed rendering blocks must survive a later syntax-output stop in an analysis session.");
            }
        }
        Console.WriteLine("PASS analysis sessions: selected rendering inputs, real delta reuse, candidate updates, fallback availability and runtime/netstandard equality.");
    }

    private static IReadOnlyList<string> Exercise(Host host, MarkdownAnalysisParts parts)
    {
        var snapshots = new List<string>();
        var options = host.Options();
        var initial = host.Create(Input, parts, options, "v0");
        var original = host.EqualOracle(initial, Input, parts, options, "v0");
        snapshots.Add(original);
        Check(Get(initial, "Analysis") is not null
            && (Get(Get(initial, "Analysis")!, "Root") is not null) == ((parts & MarkdownAnalysisParts.Syntax) != 0),
            "An analysis session must publish syntax only when requested.");
        Check(CandidateTokens(Get(initial, "Analysis")!, Input).SequenceEqual(new[] { "{#old}" }),
            "The initial unsupported marker outside code must be retained; the fenced marker must be excluded.");

        var at = Input.IndexOf("Beta", StringComparison.Ordinal);
        const string replacement = "Longer beta {#new}";
        var changed = Input.Remove(at, 4).Insert(at, replacement);
        var updated = host.Apply(initial, at, 4, replacement, "v1");
        snapshots.Add(host.EqualOracle(updated, changed, parts, options, "v1"));
        Check(Get(updated, "Mode")!.ToString() == "Delta" && (int)Get(updated, "ReusedBlocks")! > 0
            && (int)Get(updated, "RebasedBlocks")! > 0, "A length-changing eligible paragraph edit must actually reuse and rebase neighbors.");
        Check(CandidateTokens(Get(updated, "Analysis")!, changed).SequenceEqual(new[] { "{#new}", "{#old}" }),
            "Delta must collect newly introduced unsupported syntax and rebase existing candidates while excluding code.");
        var oldBlocks = host.Blocks(initial)!;
        var newBlocks = host.Blocks(updated)!;
        var prefix = Paragraph(oldBlocks, "Alpha");
        var nextPrefix = Paragraph(newBlocks, "Alpha");
        var suffix = Paragraph(oldBlocks, "Gamma");
        var nextSuffix = Paragraph(newBlocks, "Gamma");
        Check(ReferenceEquals(prefix, nextPrefix), "Retained rendering blocks must include the unchanged prefix object, not only reuse counters.");
        Check(!ReferenceEquals(suffix, nextSuffix) && ReferenceEquals(ParagraphText(suffix), ParagraphText(nextSuffix)),
            "A shifted suffix must receive a new positioned block while retaining its parsed text payload.");
        var bodyStart = (int)Get(Get(Get(updated, "Analysis")!, "BodySpan")!, "Start")!;
        Check((int)Get(Get(nextSuffix, "Span")!, "Start")! == changed.IndexOf("Gamma", StringComparison.Ordinal) - bodyStart,
            "Blocks consumed by the renderer retain body-relative positions after a length change.");
        Check(host.Snapshot(initial) == original, "Editing a selected analysis session must preserve the previous snapshot.");

        var differentOptions = host.Options(profile: "commonmark/0.31.2");
        var optionsChanged = host.Apply(initial, at, 4, "Zeta", "options", differentOptions);
        snapshots.Add(host.EqualOracle(optionsChanged, Input.Remove(at, 4).Insert(at, "Zeta"), parts, differentOptions, "options"));
        Check(Get(optionsChanged, "Mode")!.ToString() == "Full" && Get(optionsChanged, "Fallback")!.ToString() == "OptionsChanged",
            "Options changes must use the canonical parser while preserving the selected rendering contract.");

        var structuralAt = Input.IndexOf("# Guide", StringComparison.Ordinal);
        var structuralText = Input.Remove(structuralAt, 7).Insert(structuralAt, "- Guide");
        var structural = host.Apply(initial, structuralAt, 7, "- Guide", "structure");
        snapshots.Add(host.EqualOracle(structural, structuralText, parts, options, "structure"));
        Check(Get(structural, "Mode")!.ToString() == "Full" && Get(structural, "Fallback")!.ToString() != "None",
            "A structural edit outside retained paragraphs must fall back without losing requested parts.");

        var limitedOptions = host.Options(scan: 1);
        var partial = host.Create(Input, parts, limitedOptions, "partial");
        snapshots.Add(host.EqualOracle(partial, Input, parts, limitedOptions, "partial"));
        var incomplete = host.Apply(partial, at, 4, "Zeta", "still-partial");
        snapshots.Add(host.EqualOracle(incomplete, Input.Remove(at, 4).Insert(at, "Zeta"), parts, limitedOptions, "still-partial"));
        Check(Get(Get(incomplete, "Analysis")!, "Status")!.ToString() == "Partial"
            && host.Availability(Get(incomplete, "Analysis")!, MarkdownAnalysisParts.UnsupportedSyntax) == "Partial"
            && Get(incomplete, "Fallback")!.ToString() == "PreviousNotComplete",
            "An incomplete previous result must preserve partial availability on fallback.");
        var recovered = host.Apply(partial, at, 4, "Zeta", "recovered", options);
        snapshots.Add(host.EqualOracle(recovered, Input.Remove(at, 4).Insert(at, "Zeta"), parts, options, "recovered"));
        Check(host.Availability(Get(recovered, "Analysis")!, MarkdownAnalysisParts.UnsupportedSyntax) == "Complete",
            "Restoring a sufficient processing budget must recover the selected output without changing its mask.");

        var rejectedOptions = host.Options(input: 1);
        var rejected = host.Create(Input, parts, rejectedOptions, "rejected");
        snapshots.Add(host.EqualOracle(rejected, Input, parts, rejectedOptions, "rejected"));
        Check(Get(Get(rejected, "Analysis")!, "Status")!.ToString() == "Failed" && host.Blocks(rejected) is null
            && host.Availability(Get(rejected, "Analysis")!, MarkdownAnalysisParts.UnsupportedSyntax) == "Unavailable",
            "An early input rejection must retain null render blocks, not an empty-success collection.");

        var legacy = host.CreateLegacy("Alpha\n\nBeta", options);
        Check(host.LegacyDoesNotPublishAnalysis(legacy),
            "The existing session API must retain its full-document contract without a new selected-analysis projection.");
        return snapshots;
    }

    private static string[] CandidateTokens(object analysis, string raw) => ((IEnumerable)Get(analysis, "UnsupportedSyntax")!).Cast<object>()
        .Select(candidate =>
        {
            var span = Get(candidate, "RawSpan")!;
            return raw.Substring((int)Get(span, "Start")!, (int)Get(span, "Length")!);
        }).ToArray();

    private static object Paragraph(IEnumerable blocks, string text) => blocks.Cast<object>().Single(block =>
        block.GetType().Name == "LithoParagraph" && Equals(ParagraphText(block), text));

    private static object? ParagraphText(object paragraph) => ((IEnumerable)Get(paragraph, "Inlines")!).Cast<object>()
        .Where(inline => inline.GetType().Name == "LithoText").Select(inline => Get(inline, "Text")).Single();

    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);

    private static object? SnapshotValue(object? value)
    {
        if (value is null || value is string || value is decimal || value.GetType().IsPrimitive)
            return value;
        if (value.GetType().IsEnum)
            return value.ToString();
        if (value is IEnumerable values)
            return values.Cast<object?>().Select(SnapshotValue).ToArray();
        return value.GetType().GetProperties(Instance).Where(property => property.CanRead
                && property.GetIndexParameters().Length == 0 && property.Name != "EqualityContract")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(property => property.Name, property => SnapshotValue(property.GetValue(value)), StringComparer.Ordinal);
    }

    private static string Json(object? value) => JsonSerializer.Serialize(SnapshotValue(value));
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Host(Assembly assembly, bool core)
    {
        private readonly string prefix = core ? "Syntamark.Compilation.Md" : "Syntamark.Markdown";
        private Type Type(string name) => assembly.GetType(prefix + name, true)!;
        private object Parts(MarkdownAnalysisParts parts) => Enum.ToObject(Type("AnalysisParts"), (int)parts);

        internal object Options(string profile = "lithosharp-markdown/1", int scan = 16777216, int input = 1048576, int output = 131072)
            => Type(core ? "Options" : "ParseOptions").GetConstructors(Instance).Single(constructor => constructor.GetParameters().Length == 6)
                .Invoke([input, output, 200, scan, profile, 1]);

        internal object Create(string raw, MarkdownAnalysisParts parts, object options, string version)
            => Invoke(core ? Type("ParseSession").GetMethod("CreateForAnalysis", Static)! : Type("Parser").GetMethod("CreateAnalysisSession", Static)!,
                null, [raw, Parts(parts), Scope, Source, version, options, CancellationToken.None]);

        internal object CreateLegacy(string raw, object options)
            => Invoke(core ? Type("ParseSession").GetMethod("Create", Static)! : Type("Parser").GetMethod("CreateSession", Static)!,
                null, [raw, Scope, Source, "legacy", options, CancellationToken.None]);

        internal bool LegacyDoesNotPublishAnalysis(object session) => core
            ? Get(session, "Analysis") is null : session.GetType().GetProperty("Analysis", Instance) is null;

        internal object Apply(object session, int start, int length, string replacement, string version, object? options = null)
        {
            var spanType = assembly.GetType(core ? "Syntamark.Compilation.MdRawRange" : "Syntamark.RawSpan", true)!;
            var span = spanType.GetConstructors(Instance).Single().Invoke([start, length]);
            var edits = Array.CreateInstance(Type("TextEdit"), 1);
            edits.SetValue(Type("TextEdit").GetConstructors(Instance).Single(constructor => constructor.GetParameters().Length == 2)
                .Invoke([span, replacement]), 0);
            return Invoke(session.GetType().GetMethod("ApplyEdits", Instance)!, session,
                [Get(Get(session, "Analysis")!, "TextHash"), edits, version, options, CancellationToken.None]);
        }

        internal IEnumerable? Blocks(object session)
        {
            var inner = core ? session : session.GetType().GetFields(Instance)
                .Single(field => field.FieldType.FullName == "Syntamark.Compilation.MdAnalysisSession").GetValue(session)!;
            return Get(Get(inner, "Analysis")!, "Blocks") as IEnumerable;
        }

        internal string Availability(object analysis, MarkdownAnalysisParts part)
            => Invoke(analysis.GetType().GetMethod("GetAvailability", Instance)!, analysis, [Parts(part)]).ToString()!;

        internal string Snapshot(object session) => Json(Get(session, "Analysis")) + Json(Blocks(session));

        internal string EqualOracle(object session, string raw, MarkdownAnalysisParts parts, object options, string version)
        {
            var analysis = Get(session, "Analysis")!;
            var oracle = Invoke(Type("Parser").GetMethod(core ? "AnalyzeWithBlocks" : "Analyze", Static)!, null,
                [raw, Parts(parts), Scope, Source, version, options, CancellationToken.None]);
            Check(Json(analysis) == Json(oracle), "Session selected analysis differs from its canonical full-parse oracle.");
            Check(Convert.ToInt32(Get(analysis, "RequestedParts"), CultureInfo.InvariantCulture) == (int)parts,
                "Every session result must preserve the original requested-parts mask.");
            foreach (var part in new[] { MarkdownAnalysisParts.FrontMatter, MarkdownAnalysisParts.Headings, MarkdownAnalysisParts.Sections,
                MarkdownAnalysisParts.Links, MarkdownAnalysisParts.Fences, MarkdownAnalysisParts.TextRegions,
                MarkdownAnalysisParts.Syntax, MarkdownAnalysisParts.UnsupportedSyntax })
                Check(Availability(analysis, part) == Availability(oracle, part), "Session output availability differs from the canonical selected result.");
            if ((parts & MarkdownAnalysisParts.Syntax) == 0)
                Check(Get(analysis, "Root") is null && Availability(analysis, MarkdownAnalysisParts.Syntax) == "NotRequested",
                    "A rendering-oriented session must not force syntax-tree publication.");
            if (!core)
            {
                var coreOptions = Get(options, "Core")!;
                var coreParser = assembly.GetType("Syntamark.Compilation.MdParser", true)!;
                var coreParts = Enum.ToObject(assembly.GetType("Syntamark.Compilation.MdAnalysisParts", true)!, (int)parts);
                var renderingOracle = Invoke(coreParser.GetMethod("AnalyzeWithBlocks", Static)!, null,
                    [raw, coreParts, Scope, Source, version, coreOptions, CancellationToken.None]);
                Check(Json(Blocks(session)) == Json(Get(renderingOracle, "Blocks")),
                    "The public analysis session must retain the same compiler/rendering blocks as AnalyzeWithBlocks.");
            }
            if (Get(analysis, "Status")!.ToString() == "Complete")
                Check(Blocks(session) is { } blocks && blocks.Cast<object>().Any(), "Complete rendering-oriented sessions retain their actual block payloads.");
            return Snapshot(session);
        }

        private static object Invoke(MethodInfo method, object? instance, object?[] arguments)
        {
            try
            {
                return method.Invoke(instance, arguments)!;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
