using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
        "SMC000", "Markdown source host canary", "Markdown canary: status={0}; YAML={1}; parser={2}", "Markdown",
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
        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SyntamarkMaxScanUnits", out var option)
            && int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out var configured) && configured > 0)
            scan = configured;
        var facts = MdParser.Parse(snapshot.ToString(), "roslyn-sample", context.AdditionalFile.Path,
            sourceVersion: null, new MdOptions(maxScanUnits: scan), context.CancellationToken);
        var yaml = typeof(YamlDotNet.Core.Parser).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add("parserVersion", facts.ParserVersion).Add("yamlVersion", yaml).Add("status", facts.Status.ToString())
            .Add("yamlAssembly", typeof(YamlDotNet.Core.Parser).Assembly.FullName)
            .Add("emptyTargetSpans", string.Join(";", facts.Links.Where(link => link.Target == "")
                .Select(link => link.TargetRawSpan.HasValue
                    ? link.TargetRawSpan.Value.Start.ToString(CultureInfo.InvariantCulture) + ":" + link.TargetRawSpan.Value.Length.ToString(CultureInfo.InvariantCulture)
                    : "unknown")));
        context.ReportDiagnostic(Diagnostic.Create(Identity, Location.None, properties,
            facts.Status, yaml, facts.ParserVersion));
        foreach (var heading in facts.Headings)
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
}
