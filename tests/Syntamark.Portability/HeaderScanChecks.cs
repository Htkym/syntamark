using System.Collections;
using System.Reflection;
using Syntamark;

internal static class HeaderScanChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Header = "\uFEFF---\r\ntitle: 😀\r\n---\r\n";

    internal static void Run(Assembly portable)
    {
        var options = new MarkdownParseOptions(maxInputUtf16: Header.Length, maxOutputItems: 1, maxNestingDepth: 1, maxScanUnits: 256);
        var reference = MarkdownParser.ScanFrontMatter(Header + "ordinary", options);
        var metadata = MarkdownParser.Analyze("", MarkdownAnalysisParts.None, "header", "input.md", options: options);
        Check(reference.State == MarkdownFrontMatterScanState.Closed && reference.Status == MarkdownParseStatus.Complete
            && reference.Diagnostics.Count == 0 && reference.FailureSpan is null && reference.FailureLine is null
            && reference.ContractVersion == "frontmatter-scan/1" && reference.ProfileId == "lithosharp-markdown/1"
            && reference.ParserVersion == metadata.ParserVersion && reference.OptionsHash == metadata.OptionsHash,
            "Header scans must identify their delimiter-only contract and the parser/options that produced it.");
        var bodies = new[]
        {
            "# Heading\n\nOrdinary body.\n",
            new string('>', 240) + " nested body\n",
            "<Component value={{x: 1}}>\n```opaque\n{#literal}\n",
            "[unterminated](\n> broken\n```unclosed\n",
            new string('x', 50000),
            "\uD800invalid body\uDC00",
        };
        foreach (var assembly in new[] { typeof(MarkdownParser).Assembly, portable })
        {
            foreach (var core in new[] { false, true })
            {
                var scanOptions = Options(assembly, core, maxInput: Header.Length);
                foreach (var body in bodies)
                {
                    var scan = Scan(assembly, core, Header + body, scanOptions);
                    Check(Evidence(scan) == Evidence(reference) && Span(scan, "BodySpan") == new RawSpan(Header.Length, body.Length)
                        && Span(scan, "OpeningSpan") == new RawSpan(1, 3)
                        && Span(scan, "YamlSpan") == new RawSpan(6, Header.Length - 11)
                        && Span(scan, "ClosingSpan") == new RawSpan(Header.Length - 5, 3)
                        && Span(scan, "HeaderSpan") == new RawSpan(0, Header.Length),
                        "Closed-header evidence must ignore oversized, nested, opaque, malformed and invalid-Unicode bodies.");
                }
                DelimitersAndAbsence(assembly, core);
                FailureEvidence(assembly, core);
                Reject<ArgumentException>(() => Scan(assembly, core, Header, Options(assembly, core, inputKind: MarkdownInputKind.Body)));
            }
        }

        var wholeInput = MarkdownParser.Analyze(Header + bodies[4], MarkdownAnalysisParts.FrontMatter, "header", "input.md", options: options);
        Check(wholeInput.Status == MarkdownParseStatus.Failed && wholeInput.Diagnostics.Single().Id == "LMD002",
            "Existing selected FrontMatter analysis must retain its whole-input limit.");
        var recognition = MarkdownParser.Analyze(Header + bodies[4], MarkdownAnalysisParts.FrontMatter, "header", "input.md",
            options: new MarkdownParseOptions(maxScanUnits: 256));
        Check(recognition.Status == MarkdownParseStatus.Partial && recognition.Diagnostics.Any(diagnostic => diagnostic.Id == "LMD003"),
            "Existing selected FrontMatter analysis must still recognize the Markdown body.");
        Console.WriteLine("Header scans: PASS (runtime/netstandard public/internal, prefix limits, raw ranges, uninspected bodies).");
    }

    private static void DelimitersAndAbsence(Assembly assembly, bool core)
    {
        foreach (var header in new[] { "---\n---\n", "---\n[invalid YAML\n---\n", "---\n---\r" })
        {
            var scan = Scan(assembly, core, header + "\uD800body", Options(assembly, core, maxInput: header.Length));
            var yamlStart = header.IndexOf('\n') + 1;
            var closing = header.LastIndexOf("---", StringComparison.Ordinal);
            Check(Name(scan, "State") == "Closed" && Name(scan, "Status") == "Complete"
                && !Diagnostics(scan).Any() && Span(scan, "HeaderSpan") == new RawSpan(0, header.Length)
                && Span(scan, "YamlSpan") == new RawSpan(yamlStart, closing - yamlStart)
                && Span(scan, "BodySpan") == new RawSpan(header.Length, 5),
                "Closed delimiters must not claim YAML validity or inspect the character after a closing CR/LF.");
        }

        const string crHeader = "---\r---\r";
        foreach (var body in new[] { "", "x", "\uD800" })
        {
            var scan = Scan(assembly, core, crHeader + body, Options(assembly, core, maxInput: 8, maxScan: 29));
            Check(Name(scan, "State") == "Closed" && Name(scan, "Status") == "Complete" && !Diagnostics(scan).Any()
                && Span(scan, "HeaderSpan") == new RawSpan(0, 8) && Span(scan, "BodySpan") == new RawSpan(8, body.Length),
                "A body character after closing CR must not consume the header scan budget.");
        }

        var longLine = "---x" + new string('x', 50000) + "\uD800";
        foreach (var raw in new[] { longLine, "\uFEFF" + longLine, "\uD800" + longLine })
        {
            var prefix = raw[0] == '\uFEFF' ? 1 : 0;
            var scan = Scan(assembly, core, raw, Options(assembly, core, maxInput: 1, maxScan: 16));
            Check(Name(scan, "State") == "Absent" && Name(scan, "Status") == "Complete" && !Diagnostics(scan).Any()
                && Span(scan, "HeaderSpan") == new RawSpan(0, prefix)
                && Span(scan, "BodySpan") == new RawSpan(prefix, raw.Length - prefix)
                && Span(scan, "OpeningSpan") is null && Span(scan, "YamlSpan") is null && Span(scan, "ClosingSpan") is null,
                "Non-openings must be decided with a bounded probe without scanning their long first line or Unicode body.");
        }
        var standard = Scan(assembly, core, Header + longLine, Options(assembly, core, maxInput: 1, maxScan: 1, profile: "commonmark/0.31.2"));
        Check(Name(standard, "State") == "Absent" && Name(standard, "Status") == "Complete" && !Diagnostics(standard).Any()
            && Span(standard, "HeaderSpan") == new RawSpan(0, 0)
            && Span(standard, "BodySpan") == new RawSpan(0, Header.Length + longLine.Length),
            "Profiles without front matter must retain their entire input as the body.");
    }

    private static void FailureEvidence(Assembly assembly, bool core)
    {
        const string unclosed = "---\nkey: x";
        foreach (var recovery in new[] { MarkdownFrontMatterRecovery.None, MarkdownFrontMatterRecovery.TreatAsBody })
        {
            var scan = Scan(assembly, core, unclosed, Options(assembly, core, maxInput: unclosed.Length, recovery: recovery));
            Check(Name(scan, "State") == "Unterminated" && Name(scan, "Status") == "Partial"
                && Span(scan, "OpeningSpan") == new RawSpan(0, 3) && Span(scan, "YamlSpan") == new RawSpan(4, 6)
                && Span(scan, "ClosingSpan") is null && Span(scan, "BodySpan") is null
                && Span(scan, "HeaderSpan") == new RawSpan(0, unclosed.Length)
                && Span(scan, "FailureSpan") == new RawSpan(unclosed.Length, 0) && (int?)Get(scan, "FailureLine") == 3
                && Name(Diagnostics(scan).Single(), "Id") == "LMD004",
                "Confirmed unterminated EOF must preserve legacy failure positions without applying body recovery.");
        }
        Unknown(Scan(assembly, core, Header + "body", Options(assembly, core, maxInput: Header.Length - 1)), "Failed", "LMD002");
        Unknown(Scan(assembly, core, "---\n" + new string('x', 300), Options(assembly, core, maxScan: 16)), "Partial", "LMD003");
        const string invalid = "---\nkey: \uD800\n---\n";
        var invalidScan = Scan(assembly, core, invalid, Options(assembly, core, maxInput: invalid.Length));
        Unknown(invalidScan, "Failed", "LMD001");
        Check(Span(Diagnostics(invalidScan).Single(), "RawSpan") == new RawSpan(invalid.IndexOf('\uD800'), 1),
            "Invalid header Unicode must identify its original UTF-16 unit.");
    }

    private static void Unknown(object result, string status, string diagnostic)
    {
        Check(Name(result, "State") == "Unknown" && Name(result, "Status") == status
            && Name(Diagnostics(result).Single(), "Id") == diagnostic && Get(result, "FailureLine") is null
            && new[] { "OpeningSpan", "YamlSpan", "ClosingSpan", "BodySpan", "HeaderSpan", "FailureSpan" }.All(name => Span(result, name) is null),
            "Rejected or interrupted prefix scans must not turn unconfirmed boundaries or EOF into facts.");
    }

    private static object Options(Assembly assembly, bool core, int maxInput = 1048576, int maxScan = 256,
        MarkdownFrontMatterRecovery recovery = MarkdownFrontMatterRecovery.None, MarkdownInputKind inputKind = MarkdownInputKind.Document,
        string profile = "lithosharp-markdown/1")
    {
        var prefix = core ? "Syntamark.Compilation.Md" : "Syntamark.Markdown";
        return assembly.GetType(prefix + (core ? "Options" : "ParseOptions"), true)!.GetConstructors(Instance)
            .Single(constructor => constructor.GetParameters().Length == 8).Invoke(new[]
            {
                Enum.ToObject(assembly.GetType(prefix + "InputKind", true)!, (int)inputKind),
                Enum.ToObject(assembly.GetType(prefix + "FrontMatterRecovery", true)!, (int)recovery),
                (object)maxInput, 1, 1, maxScan, profile, 1,
            });
    }

    private static object Scan(Assembly assembly, bool core, string raw, object options) =>
        assembly.GetType(core ? "Syntamark.Compilation.MdParser" : "Syntamark.MarkdownParser", true)!
            .GetMethod("ScanFrontMatter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, new[] { (object)raw, options, CancellationToken.None })!;

    private static string Evidence(object result) => string.Join("|",
        new[] { "State", "Status", "ContractVersion", "ParserVersion", "ProfileId", "OptionsHash", "FailureLine" }.Select(name => Name(result, name))
            .Concat(new[] { "OpeningSpan", "YamlSpan", "ClosingSpan", "HeaderSpan", "FailureSpan" }.Select(name => Span(result, name)?.ToString()))
            .Append(Span(result, "BodySpan")?.Start.ToString())
            .Append(string.Join(",", Diagnostics(result).Select(diagnostic => Name(diagnostic, "Id")))));

    private static RawSpan? Span(object result, string name)
    {
        var range = Get(result, name);
        return range is null ? null : new RawSpan((int)Get(range, "Start")!, (int)Get(range, "Length")!);
    }

    private static IEnumerable<object> Diagnostics(object result) => ((IEnumerable)Get(result, "Diagnostics")!).Cast<object>();
    private static string? Name(object value, string name) => Get(value, name)?.ToString();
    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);

    private static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (TargetInvocationException error) when (error.InnerException is T)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
