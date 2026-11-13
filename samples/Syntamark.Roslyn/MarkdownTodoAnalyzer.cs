using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Syntamark.Compilation;

namespace Syntamark.Samples;

/// <summary>A source-package/compiler-host canary, not a production lint ruleset.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MarkdownTodoAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Todo = new(
        "SMC001", "Review Markdown TODO", "Review heading: {0}", "Markdown",
        DiagnosticSeverity.Warning, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor Identity = new(
        "SMC000", "Markdown source host canary", "Markdown canary: status={0}; YAML={1}; profile={3}; syntax={4}; nodes={5}; HTML={6}; YAMLstate={7}; parser={2}", "Markdown",
        DiagnosticSeverity.Warning, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor Unreadable = new(
        "SMC002", "Unreadable Markdown", "AdditionalFile could not be read: {0}", "Markdown",
        DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Todo, Identity, Unreadable];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterAdditionalFileAction(Analyze);
    }

    private static void Analyze(AdditionalFileAnalysisContext context)
    {
        if (!context.AdditionalFile.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return;
        // This exact snapshot owns both parsing and Roslyn's zero-based line calculation.
        var snapshot = context.AdditionalFile.GetText(context.CancellationToken);
        if (snapshot is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Unreadable, Location.None, context.AdditionalFile.Path));
            return;
        }
        var scan = 16777216;
        var output = 131072;
        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SyntamarkMaxScanUnits", out var option)
            && int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out var configured) && configured > 0)
            scan = configured;
        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SyntamarkMaxOutputItems", out var outputOption)
            && int.TryParse(outputOption, NumberStyles.None, CultureInfo.InvariantCulture, out var configuredOutput) && configuredOutput > 0)
            output = configuredOutput;
        var rawText = snapshot.ToString();
        const string legacyProfile = "lithosharp-markdown/1";
        string selectedProfile;
        var syntaxState = "not-requested";
        var syntaxNodes = 0;
        var htmlNodes = 0;
        var syntaxRootSpan = "unknown";
        var headingSpans = new List<string>();
        const MdAnalysisParts requested = MdAnalysisParts.FrontMatter | MdAnalysisParts.Headings | MdAnalysisParts.Links | MdAnalysisParts.Syntax;
        var parsed = MdParser.Analyze(rawText, requested, "roslyn-sample", context.AdditionalFile.Path,
            sourceVersion: null, new MdOptions(maxScanUnits: scan, maxOutputItems: output, profileId: "commonmark/0.31.2"), context.CancellationToken);
        var facts = parsed;
        selectedProfile = facts.ProfileId;
        var recognizedHeadings = new HashSet<MdRawRange>();
        if (parsed.GetAvailability(MdAnalysisParts.Syntax) == MdAnalysisAvailability.Complete && parsed.Root is { } root)
        {
            syntaxState = root.Kind.ToString();
            syntaxRootSpan = FormatRange(root.RawSpan);
            foreach (var node in root.DescendantsAndSelf(context.CancellationToken))
            {
                syntaxNodes++;
                if (node.Kind is MdSyntaxKind.HtmlBlock or MdSyntaxKind.HtmlInline)
                    htmlNodes++;
                if (node.Kind == MdSyntaxKind.Heading && node.RawSpan.HasValue)
                {
                    recognizedHeadings.Add(node.RawSpan.Value);
                    headingSpans.Add(FormatRange(node.RawSpan));
                }
            }
        }
        else
            syntaxState = "unavailable";
        var headings = facts.GetAvailability(MdAnalysisParts.Headings) == MdAnalysisAvailability.Complete && facts.Headings is { } availableHeadings
            ? availableHeadings.Where(heading => recognizedHeadings.Contains(heading.RawSpan)) : Enumerable.Empty<MdHeading>();
        // A second profile is intentional: YAML belongs to the separate native YAML probe, not CommonMark.
        var yamlProbe = MdParser.Parse(rawText, "roslyn-yaml-probe", context.AdditionalFile.Path,
            sourceVersion: null, new MdOptions(maxScanUnits: scan, maxOutputItems: output, profileId: legacyProfile), context.CancellationToken);
        var standardFrontMatter = facts.FrontMatter?.State.ToString() ?? "Unknown";
        IReadOnlyList<MdLink> links = facts.GetAvailability(MdAnalysisParts.Links) == MdAnalysisAvailability.Complete && facts.Links is { } availableLinks
            ? availableLinks : Array.Empty<MdLink>();
        var yaml = MdYamlParser.Parse("title: canary", cancellationToken: context.CancellationToken).ProfileId;
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add("parserVersion", facts.ParserVersion).Add("yamlProfileId", yaml).Add("status", facts.Status.ToString())
            .Add("profileId", selectedProfile).Add("syntaxState", syntaxState)
            .Add("syntaxNodes", syntaxNodes.ToString(CultureInfo.InvariantCulture)).Add("htmlSyntaxNodes", htmlNodes.ToString(CultureInfo.InvariantCulture))
            .Add("syntaxRootSpan", syntaxRootSpan).Add("syntaxHeadingSpans", string.Join(";", headingSpans))
            .Add("yamlProbeProfileId", legacyProfile).Add("yamlProbeState", yamlProbe.FrontMatter.State.ToString())
            .Add("yamlProbeStatus", yamlProbe.Status.ToString()).Add("standardFrontMatterState", standardFrontMatter)
            .Add("linkTargets", string.Join(";", links.Where(link => link.Resolution != MdLinkResolution.UnresolvedReference).Select(link => link.Target)))
            .Add("parserReferences", string.Join(";", typeof(MdParser).Assembly.GetReferencedAssemblies().Select(reference => reference.Name)))
            .Add("emptyTargetSpans", string.Join(";", links.Where(link => link.Target == "")
                .Select(link => link.TargetRawSpan.HasValue
                    ? link.TargetRawSpan.Value.Start.ToString(CultureInfo.InvariantCulture) + ":" + link.TargetRawSpan.Value.Length.ToString(CultureInfo.InvariantCulture)
                    : "unknown")));
        properties = properties.Add("analysisParts", ((int)parsed.RequestedParts).ToString(CultureInfo.InvariantCulture))
            .Add("analysisSchemaVersion", parsed.AnalysisSchemaVersion.ToString(CultureInfo.InvariantCulture))
            .Add("analysisHash", parsed.AnalysisHash).Add("analysisOptionsHash", facts.OptionsHash)
            .Add("analysisContractVersion", facts.ContractVersion)
            .Add("analysisFrontMatterAvailability", parsed.GetAvailability(MdAnalysisParts.FrontMatter).ToString())
            .Add("analysisHeadingsAvailability", parsed.GetAvailability(MdAnalysisParts.Headings).ToString())
            .Add("analysisSectionsAvailability", parsed.GetAvailability(MdAnalysisParts.Sections).ToString())
            .Add("analysisLinksAvailability", parsed.GetAvailability(MdAnalysisParts.Links).ToString())
            .Add("analysisFencesAvailability", parsed.GetAvailability(MdAnalysisParts.Fences).ToString())
            .Add("analysisTextRegionsAvailability", parsed.GetAvailability(MdAnalysisParts.TextRegions).ToString())
            .Add("analysisSyntaxAvailability", parsed.GetAvailability(MdAnalysisParts.Syntax).ToString());
        context.ReportDiagnostic(Diagnostic.Create(Identity, Location.None, properties,
            facts.Status, yaml, facts.ParserVersion, selectedProfile, syntaxState, syntaxNodes, htmlNodes, yamlProbe.FrontMatter.State));
        foreach (var heading in headings)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (heading.Text.Text is not { } text || text.IndexOf("TODO", StringComparison.Ordinal) < 0)
                continue;
            var mapping = MdSourceMapping.Map(heading.Text, new MdRawRange(0, text.Length));
            var locations = new List<Location>();
            foreach (var raw in mapping.RawFragments)
            {
                var span = new TextSpan(raw.Start, raw.Length);
                locations.Add(Location.Create(context.AdditionalFile.Path, span, snapshot.Lines.GetLinePositionSpan(span)));
            }
            // Unknown mapping has no invented precise location; token-covering spans support a warning, not an automatic edit.
            context.ReportDiagnostic(Diagnostic.Create(Todo, locations.FirstOrDefault() ?? Location.None,
                locations.Skip(1), properties.Add("precision", mapping.Precision.ToString()), text));
        }
        // No absence-based rule runs on Partial/Failed facts. There is no process/global document cache.
    }

    private static string FormatRange(MdRawRange? span) => span.HasValue
        ? span.Value.Start.ToString(CultureInfo.InvariantCulture) + ":" + span.Value.Length.ToString(CultureInfo.InvariantCulture)
        : "unknown";
}
