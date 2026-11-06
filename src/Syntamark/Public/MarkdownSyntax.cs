using System;
using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

/// <summary>Recognized syntax kinds. Callers should retain a fallback for future kinds.</summary>
public enum MarkdownSyntaxKind
{
    Unknown, Document, Paragraph, Heading, BlockQuote, OrderedList, UnorderedList, ListItem,
    ThematicBreak, FencedCodeBlock, IndentedCodeBlock, HtmlBlock, Text, Emphasis, StrongEmphasis,
    CodeSpan, Link, Image, Autolink, HtmlInline, SoftLineBreak, HardLineBreak,
    Table, Strikethrough, Admonition, Directive, MathBlock, MathInline,
}

/// <summary>An immutable syntax view over one owned document snapshot.</summary>
public sealed class MarkdownSyntaxNode
{
    private readonly string rawText;

    private MarkdownSyntaxNode(MdSyntaxNode node, string rawText, IReadOnlyList<MarkdownSyntaxNode> children)
    {
        this.rawText = rawText;
        Kind = (MarkdownSyntaxKind)node.Kind;
        RawSpan = global::Syntamark.RawSpan.From(node.RawSpan);
        Children = children;
        Text = node.Text;
        HeadingLevel = node.HeadingLevel;
        OrderedListStart = node.OrderedListStart;
        IsTightList = node.IsTightList;
        TaskChecked = node.TaskChecked;
        CodeInfo = node.CodeInfo;
        Target = node.Target;
        TargetRawSpan = global::Syntamark.RawSpan.From(node.TargetRawSpan);
        Title = node.Title;
        ReferenceLabel = node.ReferenceLabel;
    }

    public MarkdownSyntaxKind Kind { get; }
    /// <summary>The marker-inclusive raw envelope, or null if its position is unknown.</summary>
    public RawSpan? RawSpan { get; }
    public IReadOnlyList<MarkdownSyntaxNode> Children { get; }
    /// <summary>Leaf text for text, code, HTML, math, autolink and line-break nodes; null for containers.</summary>
    public string? Text { get; }
    public int? HeadingLevel { get; }
    public int? OrderedListStart { get; }
    public bool? IsTightList { get; }
    public bool? TaskChecked { get; }
    public string? CodeInfo { get; }
    public string? Target { get; }
    /// <summary>Destination content position. A reference definition may lie outside this syntax node.</summary>
    public RawSpan? TargetRawSpan { get; }
    public string? Title { get; }
    public string? ReferenceLabel { get; }

    /// <summary>Returns the exact raw envelope, or null for an unknown range. This is not an AST writer.</summary>
    public string? GetRawText() => RawSpan.HasValue ? rawText.Substring(RawSpan.Value.Start, RawSpan.Value.Length) : null;

    /// <summary>Enumerates this node and descendants in document order without recursive calls.</summary>
    public IEnumerable<MarkdownSyntaxNode> DescendantsAndSelf(CancellationToken cancellationToken = default)
    {
        var pending = new Stack<MarkdownSyntaxNode>();
        pending.Push(this);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = pending.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }
    }

    internal static MarkdownSyntaxNode From(MdSyntaxNode root, string rawText, CancellationToken cancellationToken)
    {
        var pending = new Stack<Frame>();
        pending.Push(new Frame(root));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = pending.Peek();
            if (frame.Index < frame.Source.Children.Count)
            {
                pending.Push(new Frame(frame.Source.Children[frame.Index++]));
                continue;
            }
            pending.Pop();
            var node = new MarkdownSyntaxNode(frame.Source, rawText, MarkdownCollections.FromOwned(frame.Children));
            if (pending.Count == 0)
                return node;
            var parent = pending.Peek();
            parent.Children[parent.Index - 1] = node;
        }
        throw new InvalidOperationException("Syntax root was not converted.");
    }

    private sealed class Frame(MdSyntaxNode source)
    {
        internal MdSyntaxNode Source { get; } = source;
        internal MarkdownSyntaxNode[] Children { get; } = source.Children.Count == 0
            ? Array.Empty<MarkdownSyntaxNode>() : new MarkdownSyntaxNode[source.Children.Count];
        internal int Index { get; set; }
    }
}

/// <summary>Facts and an optional syntax root produced by one parse of the same snapshot.</summary>
public sealed class MarkdownSyntaxDocument
{
    internal MarkdownSyntaxDocument(MdSyntaxDocument document, CancellationToken cancellationToken)
    {
        Facts = new MarkdownDocument(document.Facts);
        Root = document.Root is null ? null : MarkdownSyntaxNode.From(document.Root, document.Facts.RawText, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
    public MarkdownDocument Facts { get; }
    /// <summary>Null when input rejection or an early resource stop prevented obtaining a syntax tree.</summary>
    public MarkdownSyntaxNode? Root { get; }
}

public static partial class MarkdownParser
{
    /// <summary>Parses facts and retains a read-only syntax view. Inspect Facts.Status and Coverage before absence-based analysis.</summary>
    public static MarkdownSyntaxDocument ParseWithSyntax(string rawText, string scopeId, string sourceId,
        string? sourceVersion = null, MarkdownParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = MdParser.ParseWithSyntax(rawText, scopeId, sourceId, sourceVersion,
            options?.Core ?? defaultOptions, cancellationToken);
        return new MarkdownSyntaxDocument(parsed, cancellationToken);
    }
}
