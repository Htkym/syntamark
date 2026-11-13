using System;
using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

public enum YamlScalarStyle { Plain, SingleQuoted, DoubleQuoted, Literal, Folded }
public enum YamlScalarKind { String, Null, Boolean, Integer, Float, Unknown }
public enum YamlDiagnosticKind { Syntax, Unsupported, ResourceLimit, Canceled, InvalidEncoding }

/// <summary>Limits for the single-document native-yaml/1 syntax profile.</summary>
public sealed class YamlParseOptions
{
    public YamlParseOptions(int maxInputBytes = 4194304, int maxInputUtf16 = 1048576,
        int maxNestingDepth = 128, int maxNodes = 131072, int maxScalarUtf16 = 1048576,
        int maxScanUnits = 16777216) => Core = new MdYamlOptions(maxInputBytes, maxInputUtf16,
            maxNestingDepth, maxNodes, maxScalarUtf16, maxScanUnits);
    internal MdYamlOptions Core { get; }
    public int MaxInputBytes => Core.MaxInputBytes;
    public int MaxInputUtf16 => Core.MaxInputUtf16;
    public int MaxNestingDepth => Core.MaxNestingDepth;
    public int MaxNodes => Core.MaxNodes;
    public int MaxScalarUtf16 => Core.MaxScalarUtf16;
    public int MaxScanUnits => Core.MaxScanUnits;
    public string OptionsHash => Core.Hash;
}

public sealed class YamlDiagnostic
{
    internal YamlDiagnostic(MdYamlDiagnostic value)
    {
        Kind = (YamlDiagnosticKind)value.Kind;
        Reason = value.Reason;
        Message = value.Message;
        RawSpan = global::Syntamark.RawSpan.From(value.RawSpan);
    }
    public YamlDiagnosticKind Kind { get; }
    public string Reason { get; }
    public string Message { get; }
    public RawSpan RawSpan { get; }
}

/// <summary>An ordered entry; duplicate keys remain separate entries with a diagnostic.</summary>
public sealed class YamlMappingEntry
{
    internal YamlMappingEntry(MdYamlEntry entry, string rawText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Key = new YamlNode(entry.Key, rawText, cancellationToken);
        Value = new YamlNode(entry.Value, rawText, cancellationToken);
    }
    public YamlNode Key { get; }
    public YamlNode Value { get; }
}

public sealed class YamlNode
{
    private readonly string rawText;
    internal YamlNode(MdYamlSyntaxNode node, string rawText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.rawText = rawText;
        Kind = (MarkdownYamlKind)node.Kind;
        RawSpan = global::Syntamark.RawSpan.From(node.RawSpan);
        ScalarStyle = (YamlScalarStyle)node.ScalarStyle;
        ScalarKind = (YamlScalarKind)node.ScalarKind;
        Value = node.Value;
        Projection = new MarkdownTextProjection(node.Projection);
        Children = MarkdownCollections.Convert(node.Children, child => new YamlNode(child, rawText, cancellationToken));
        Entries = MarkdownCollections.Convert(node.Entries, entry => new YamlMappingEntry(entry, rawText, cancellationToken));
        IsComplete = node.IsComplete;
    }
    public MarkdownYamlKind Kind { get; }
    public RawSpan RawSpan { get; }
    public YamlScalarStyle ScalarStyle { get; }
    /// <summary>The YAML 1.2 Core interpretation, without choosing a numeric width or converting strings to timestamps.</summary>
    public YamlScalarKind ScalarKind { get; }
    public string? Value { get; }
    public MarkdownTextProjection Projection { get; }
    public IReadOnlyList<YamlNode> Children { get; }
    public IReadOnlyList<YamlMappingEntry> Entries { get; }
    public bool IsComplete { get; }
    public string GetRawText() => rawText.Substring(RawSpan.Start, RawSpan.Length);
}

/// <summary>Syntax and diagnostics over one original snapshot. Complete status is required before absence-based validation.</summary>
public sealed class YamlDocument
{
    private readonly MdYamlDocument core;
    internal YamlDocument(MdYamlDocument document, CancellationToken cancellationToken)
    {
        core = document;
        RawText = document.RawText;
        ProfileId = document.ProfileId;
        ParserVersion = document.ParserVersion;
        OptionsHash = document.OptionsHash;
        Status = (MarkdownParseStatus)document.Status;
        // A canceled core parse is already a diagnostic result; cancellation during publication throws.
        Root = document.Root is null ? null : new YamlNode(document.Root, RawText, cancellationToken);
        var coreCanceled = document.Diagnostics.Count == 1 && document.Diagnostics[0].Kind == MdYamlDiagnosticKind.Canceled;
        Diagnostics = MarkdownCollections.Convert(document.Diagnostics, diagnostic =>
        {
            if (!coreCanceled)
                cancellationToken.ThrowIfCancellationRequested();
            return new YamlDiagnostic(diagnostic);
        });
    }
    public string RawText { get; }
    public string ProfileId { get; }
    public string ParserVersion { get; }
    public string OptionsHash { get; }
    public MarkdownParseStatus Status { get; }
    public YamlNode? Root { get; }
    public IReadOnlyList<YamlDiagnostic> Diagnostics { get; }
    public bool HasOriginalUtf8 => core.HasOriginalUtf8;
    /// <summary>Returns a new copy of the input bytes; null when input rejection or cancellation prevented retaining a snapshot.</summary>
    public byte[]? GetOriginalUtf8() => core.GetOriginalUtf8();
}

public static class YamlParser
{
    public static YamlDocument Parse(string rawText, YamlParseOptions? options = null,
        CancellationToken cancellationToken = default) => new(MdYamlParser.Parse(rawText, options?.Core, cancellationToken), cancellationToken);

    /// <summary>Parses strict UTF-8 and preserves the original bytes, including a BOM and newline style.</summary>
    public static YamlDocument ParseUtf8(byte[] rawUtf8, YamlParseOptions? options = null,
        CancellationToken cancellationToken = default) => new(MdYamlParser.ParseUtf8(rawUtf8, options?.Core, cancellationToken), cancellationToken);
}
