using System.Collections.Generic;
using Syntamark.Compilation;

namespace Syntamark;

/// <summary>A real or synthesized table cell. Synthesized cells have no raw source range.</summary>
public sealed class MarkdownTableCellSyntax
{
    internal MarkdownTableCellSyntax(MdTableCellSyntax cell)
    {
        Row = cell.Row;
        Column = cell.Column;
        RawSpan = global::Syntamark.RawSpan.From(cell.RawSpan);
        Content = new MarkdownTextProjection(cell.Content);
    }
    public int Row { get; }
    public int Column { get; }
    public RawSpan? RawSpan { get; }
    public MarkdownTextProjection Content { get; }
}

/// <summary>One directive attribute, retaining duplicate and incomplete attributes in source order.</summary>
public sealed class MarkdownDirectiveAttribute
{
    internal MarkdownDirectiveAttribute(MdDirectiveAttribute attribute)
    {
        Name = attribute.Name;
        RawSpan = global::Syntamark.RawSpan.From(attribute.RawSpan);
        NameRawSpan = global::Syntamark.RawSpan.From(attribute.NameRawSpan);
        ValueRawSpan = global::Syntamark.RawSpan.From(attribute.ValueRawSpan);
        OpeningQuoteRawSpan = global::Syntamark.RawSpan.From(attribute.OpeningQuoteRawSpan);
        ClosingQuoteRawSpan = global::Syntamark.RawSpan.From(attribute.ClosingQuoteRawSpan);
        Value = attribute.Value is null ? null : new MarkdownTextProjection(attribute.Value);
    }
    public string Name { get; }
    public RawSpan RawSpan { get; }
    public RawSpan NameRawSpan { get; }
    public RawSpan? ValueRawSpan { get; }
    public RawSpan? OpeningQuoteRawSpan { get; }
    public RawSpan? ClosingQuoteRawSpan { get; }
    /// <summary>Quoted values decode escaped matching quotes and backslashes; all other text remains literal.</summary>
    public MarkdownTextProjection? Value { get; }
}

/// <summary>A colon-fenced directive header and body from the same document snapshot.</summary>
public sealed class MarkdownDirectiveSyntax
{
    internal MarkdownDirectiveSyntax(MdDirectiveSyntax directive)
    {
        Name = directive.Name;
        NameRawSpan = global::Syntamark.RawSpan.From(directive.NameRawSpan);
        OpeningMarkerRawSpan = global::Syntamark.RawSpan.From(directive.OpeningMarkerRawSpan);
        HeaderRawSpan = global::Syntamark.RawSpan.From(directive.HeaderRawSpan);
        ContentRawSpan = global::Syntamark.RawSpan.From(directive.ContentRawSpan);
        ClosingMarkerRawSpan = global::Syntamark.RawSpan.From(directive.ClosingMarkerRawSpan);
        Attributes = MarkdownCollections.Convert(directive.Attributes, static attribute => new MarkdownDirectiveAttribute(attribute));
        Diagnostics = MarkdownCollections.Convert(directive.Diagnostics, static diagnostic => new MarkdownDiagnostic(diagnostic));
        IsComplete = directive.IsComplete;
    }
    public string Name { get; }
    public RawSpan NameRawSpan { get; }
    public RawSpan OpeningMarkerRawSpan { get; }
    public RawSpan HeaderRawSpan { get; }
    /// <summary>The body envelope excludes fence lines and can include raw container prefixes.</summary>
    public RawSpan ContentRawSpan { get; }
    public RawSpan? ClosingMarkerRawSpan { get; }
    /// <summary>Attributes are interpreted only by syntamark-docs/2; older profiles retain their original header meaning.</summary>
    public IReadOnlyList<MarkdownDirectiveAttribute> Attributes { get; }
    public IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; }
    public bool IsComplete { get; }
}
