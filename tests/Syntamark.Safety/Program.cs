using System.Collections;
using System.Reflection;
using System.Text.Json;
using Syntamark.Compilation;

var checks = 0;
foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
{
    foreach (var reason in new[] { "input", "output", "depth", "scan" })
        foreach (var invalid in new[] { 0, -1 })
        {
            try
            {
                Options(profile, reason, invalid);
                throw new InvalidOperationException($"{reason} accepted a nonpositive limit.");
            }
            catch (ArgumentOutOfRangeException) { }
        }
    CheckInput(profile);
    CheckBoundary(profile, "output", "# One\n\nparagraph [link](/u)\n\n## Two\n", 1024);
    CheckBoundary(profile, "scan", "# One\n\nparagraph [link](/u)\n\n## Two\n", 16777216);
    CheckBoundary(profile, "depth", "> > > nested\n", 200);
    CheckCancellation(profile);
    CheckAdversarial(profile);
}
Console.WriteLine($"SM08 direct Portable-source safety checks PASS ({checks} parsed documents, two profiles).");

MdOptions Options(string profile, string? reason = null, int limit = 0) => new(
    maxInputUtf16: reason == "input" ? limit : 1048576,
    maxOutputItems: reason == "output" ? limit : 131072,
    maxNestingDepth: reason == "depth" ? limit : 200,
    maxScanUnits: reason == "scan" ? limit : 16777216,
    profileId: profile);

MdDocumentFacts Parse(string raw, string profile, string? reason = null, int limit = 0)
{
    var facts = MdParser.Parse(raw, "safety", "fixture.md", null, Options(profile, reason, limit), CancellationToken.None);
    try
    {
        Validate(facts);
    }
    catch (InvalidOperationException exception)
    {
        throw new InvalidOperationException($"{profile}/{reason ?? "default"}/{limit}, raw={JsonSerializer.Serialize(raw)}: {exception.Message}", exception);
    }
    checks++;
    return facts;
}

void CheckInput(string profile)
{
    const string raw = "# A😀\r\n";
    foreach (var limit in new[] { raw.Length - 1, raw.Length, raw.Length + 1 })
    {
        var facts = Parse(raw, profile, "input", limit);
        if (limit < raw.Length)
            Check(facts.Status == MdParseStatus.Failed && facts.Diagnostics.Single().Reason == "input"
                && facts.Headings.Count == 0 && facts.TextHash is null, "Input rejection parsed or hashed the oversized snapshot.");
        else
            Check(facts.Status == MdParseStatus.Complete && facts.Headings.Single().Text.Text == "A😀", "Input UTF-16 boundary rejected an allowed snapshot.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { profile, reason = "input", minimumComplete = raw.Length }));
}

void CheckBoundary(string profile, string reason, string raw, int upper)
{
    // Discover the observable boundary rather than copying implementation counters into a golden value.
    Check(Parse(raw, profile, reason, upper).Status == MdParseStatus.Complete, $"{profile}/{reason}: fixture cannot complete with the generous bound.");
    var low = 1;
    var high = upper;
    while (low < high)
    {
        var middle = low + (high - low) / 2;
        if (Parse(raw, profile, reason, middle).Status == MdParseStatus.Complete)
            high = middle;
        else
            low = middle + 1;
    }
    Check(low > 1, $"{reason}: fixture did not exercise the limit.");
    var stopped = Parse(raw, profile, reason, low - 1);
    Check(stopped.Status == MdParseStatus.Partial && stopped.Diagnostics.Count(d => d.Origin == "resource" && d.Reason == reason) == 1,
        $"{profile}/{reason}: budget exhaustion was not a single Partial resource diagnostic.");
    Check(stopped.Coverage.RawPositions != MdCoverageState.Complete && stopped.Coverage.Reasons.Contains(reason),
        $"{profile}/{reason}: stopped parsing claims complete raw coverage.");
    var exact = Parse(raw, profile, reason, low);
    Check(exact.Status == MdParseStatus.Complete && Parse(raw, profile, reason, low + 1).Status == MdParseStatus.Complete,
        $"{profile}/{reason}: completion boundary is inconsistent.");
    if (reason == "output")
        Check(stopped.Headings.Count > 0 && stopped.Headings.All(h => exact.Headings.Any(e => e.RawSpan == h.RawSpan && e.Text.Text == h.Text.Text)),
            "Output exhaustion discarded or changed already-published headings.");
    Console.WriteLine(JsonSerializer.Serialize(new { profile, reason, minimumComplete = low, retainedHeadings = stopped.Headings.Count }));
}

void CheckCancellation(string profile)
{
    const string raw = "prefix **strong** ![first][warmup] between ![second][interrupt] tail ![third][later]";
    using var source = new CancellationTokenSource();
    var control = new CancelingReferences(source, cancel: false);
    var nodes = LithoInlineParser.Parse(raw, LithoLineMap.Contiguous(0, raw.Length), control, source.Token, new MdParseContext(Options(profile), source.Token));
    Check(LithoInline.Walk(nodes).Count(pair => !pair.Exit && pair.Node is LithoLink) == 3
        && control.Lookups.SequenceEqual(new[] { "warmup", "interrupt", "later" }), "Cancellation fixture did not parse its control reference links.");
    var references = new CancelingReferences(source, cancel: true);
    try
    {
        LithoInlineParser.Parse(raw, LithoLineMap.Contiguous(0, raw.Length), references, source.Token,
            new MdParseContext(Options(profile), source.Token));
        throw new InvalidOperationException("During-inline cancellation returned a tree.");
    }
    catch (OperationCanceledException exception)
    {
        Check(exception.CancellationToken == source.Token && source.IsCancellationRequested
            && references.Lookups.SequenceEqual(new[] { "warmup", "interrupt" }), "Cancellation was early, lost its token, or continued reference lookup.");
        Console.WriteLine(JsonSerializer.Serialize(new { profile, cancellationScanFrame = exception.StackTrace?.Split('\n').FirstOrDefault(line => line.Contains("MdParseContext.Scan", StringComparison.Ordinal))?.Trim() }));
    }
    // Resolved images return directly to Scanner.Scan, whose next context.Scan observes the cancellation.
    Check(Parse(raw, profile).Status == MdParseStatus.Complete, "Cancellation affected an independent later parse.");
    Console.WriteLine(JsonSerializer.Serialize(new { profile, cancellation = "during-inline-reference-lookup", completedLookups = references.Lookups }));
}

void CheckAdversarial(string profile)
{
    var cases = new Dictionary<string, string>
    {
        ["reference-cr-label"] = "[a\rb]\n",
        ["reference-crlf-label"] = "[a\r\nb]\n",
        ["reference-nul-label"] = "[a\0b]\n",
        ["brackets"] = new string('[', 96) + "x" + new string(']', 96),
        ["destinations"] = string.Concat(Enumerable.Repeat("[a](", 192)),
        ["backticks"] = string.Join(" ", Enumerable.Range(1, 48).Select(n => new string('`', n) + "x")),
        ["html-attribute"] = "a <x " + string.Concat(Enumerable.Repeat("a='", 512)),
        ["html-comment"] = "a <!--" + new string('x', 4096),
        ["containers"] = string.Concat(Enumerable.Repeat("> ", 32)) + "nested\n",
    };
    foreach (var (name, raw) in cases)
    {
        var generous = Parse(raw, profile);
        Check(generous.Status != MdParseStatus.Failed && !generous.Diagnostics.Any(d => d.Origin == "resource"),
            $"{profile}/{name}: bounded adversarial fixture exhausted default resources.");
        var limited = Parse(raw, profile, "scan", 64);
        Check(limited.Status == MdParseStatus.Partial && limited.Diagnostics.Any(d => d.Origin == "resource" && d.Reason == "scan"),
            $"{profile}/{name}: scan exhaustion did not stop with the expected reason.");
    }
    // A fixed seed and a small alphabet make malformed combinations reproducible and bounded.
    uint seed = 0x534d3038;
    const string alphabet = "[]()<>`*_&;\\ \r\nab";
    for (var test = 0; test < 32; test++)
    {
        var chars = new char[128];
        for (var i = 0; i < chars.Length; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            chars[i] = alphabet[(int)(seed % (uint)alphabet.Length)];
        }
        var raw = new string(chars);
        Check(Parse(raw, profile).Status != MdParseStatus.Failed, "A bounded well-formed generated input failed validation.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { profile, adversarialFixtures = cases.Count, generatedFixtures = 32, seed = "534d3038" }));
}

void Validate(MdDocumentFacts facts)
{
    var pending = new Stack<object>();
    var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
    pending.Push(facts);
    while (pending.TryPop(out var value))
    {
        if (value is MdRawRange range)
        {
            Check(range.Start >= 0 && range.End <= facts.RawText.Length, "Published raw span escaped the snapshot.");
            continue;
        }
        if (value is MdTextProjection text)
        {
            var end = 0;
            foreach (var segment in text.SourceSegments)
            {
                Check(segment.DecodedSpan.Start == end && segment.DecodedSpan.Length > 0, "Decoded segments have a gap or overlap.");
                end = segment.DecodedSpan.End;
                Check(text.Text is not null && end <= text.Text.Length, "Decoded segment escaped its text.");
                if (segment.RawSpan is { } raw)
                {
                    Check(raw.End <= facts.RawText.Length, "Projected raw span escaped the snapshot.");
                    if (segment.Kind == MdSegmentKind.Linear)
                        Check(facts.RawText.Substring(raw.Start, raw.Length) == text.Text!.Substring(segment.DecodedSpan.Start, segment.DecodedSpan.Length),
                            $"Linear mapping changed source text at raw {raw.Start}:{raw.Length}, decoded {segment.DecodedSpan.Start}:{segment.DecodedSpan.Length}, text={JsonSerializer.Serialize(text.Text)}.");
                }
                else
                    Check(segment.Kind == MdSegmentKind.Unknown, "A known mapping omitted its raw span.");
            }
            Check(end == (text.Text?.Length ?? 0), "Decoded segments do not cover their text.");
            continue;
        }
        if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum || !seen.Add(value))
            continue;
        if (value is IEnumerable items)
        {
            foreach (var item in items)
                if (item is not null)
                    pending.Push(item);
            continue;
        }
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (property.GetIndexParameters().Length == 0 && property.GetValue(value) is { } child)
                pending.Push(child);
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class CancelingReferences(CancellationTokenSource source, bool cancel) : IReadOnlyDictionary<string, LithoReference>
{
    private readonly Dictionary<string, LithoReference> values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warmup"] = new("/first", null),
        ["interrupt"] = new("/second", null),
        ["later"] = new("/third", null),
    };
    internal List<string> Lookups { get; } = new();
    public bool TryGetValue(string key, out LithoReference value)
    {
        if (source.IsCancellationRequested)
            throw new InvalidOperationException("Reference lookup continued after cancellation.");
        Lookups.Add(key.ToLowerInvariant());
        if (cancel && key.Equals("interrupt", StringComparison.OrdinalIgnoreCase))
            source.Cancel();
        return values.TryGetValue(key, out value!);
    }
    public LithoReference this[string key] => values[key];
    public IEnumerable<string> Keys => values.Keys;
    public IEnumerable<LithoReference> Values => values.Values;
    public int Count => values.Count;
    public bool ContainsKey(string key) => values.ContainsKey(key);
    public IEnumerator<KeyValuePair<string, LithoReference>> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
