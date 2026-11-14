using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Syntamark.Compilation;

internal enum MdFrontMatterRecovery { None, TreatAsBody }
internal enum MdInputKind { Document, Body }

internal sealed class MdOptions
{
    private string? hash;

    internal MdOptions(int maxInputUtf16 = 1048576, int maxOutputItems = 131072,
        int maxNestingDepth = 200, int maxScanUnits = 16777216,
        string profileId = "lithosharp-markdown/1", int optionsSchemaVersion = 1)
        : this(MdFrontMatterRecovery.None, maxInputUtf16, maxOutputItems, maxNestingDepth, maxScanUnits,
            profileId, optionsSchemaVersion)
    {
    }

    internal MdOptions(MdFrontMatterRecovery frontMatterRecovery, int maxInputUtf16 = 1048576, int maxOutputItems = 131072,
        int maxNestingDepth = 200, int maxScanUnits = 16777216,
        string profileId = "lithosharp-markdown/1", int optionsSchemaVersion = 1)
        : this(MdInputKind.Document, frontMatterRecovery, maxInputUtf16, maxOutputItems, maxNestingDepth, maxScanUnits,
            profileId, optionsSchemaVersion)
    {
    }

    internal MdOptions(MdInputKind inputKind, MdFrontMatterRecovery frontMatterRecovery = MdFrontMatterRecovery.None,
        int maxInputUtf16 = 1048576, int maxOutputItems = 131072, int maxNestingDepth = 200, int maxScanUnits = 16777216,
        string profileId = "lithosharp-markdown/1", int optionsSchemaVersion = 1)
    {
        if (inputKind is not (MdInputKind.Document or MdInputKind.Body))
            throw new ArgumentOutOfRangeException(nameof(inputKind));
        if (frontMatterRecovery is not (MdFrontMatterRecovery.None or MdFrontMatterRecovery.TreatAsBody))
            throw new ArgumentOutOfRangeException(nameof(frontMatterRecovery));
        if (maxInputUtf16 < 1 || maxOutputItems < 1 || maxNestingDepth < 1 || maxScanUnits < 1)
            throw new ArgumentOutOfRangeException(nameof(maxInputUtf16), "Resource limits must be positive integers.");
        if ((profileId != "lithosharp-markdown/1" && profileId != "commonmark/0.31.2" && profileId != "syntamark-docs/2") || optionsSchemaVersion != 1)
            throw new ArgumentException("Unknown Markdown profile or options schema.");
        MaxInputUtf16 = maxInputUtf16;
        MaxOutputItems = maxOutputItems;
        MaxNestingDepth = maxNestingDepth;
        MaxScanUnits = maxScanUnits;
        ProfileId = profileId;
        OptionsSchemaVersion = optionsSchemaVersion;
        FrontMatterRecovery = frontMatterRecovery;
        InputKind = inputKind;
    }
    internal int MaxInputUtf16 { get; }
    internal int MaxOutputItems { get; }
    internal int MaxNestingDepth { get; }
    internal int MaxScanUnits { get; }
    internal string ProfileId { get; }
    internal bool IsCommonMark => ProfileId == "commonmark/0.31.2";
    internal bool HasFootnotes => ProfileId == "syntamark-docs/2";
    internal int OptionsSchemaVersion { get; }
    internal MdFrontMatterRecovery FrontMatterRecovery { get; }
    internal MdInputKind InputKind { get; }
    internal string Hash
    {
        get
        {
            var cached = Volatile.Read(ref hash);
            if (cached is not null)
                return cached;
            var recovery = InputKind == MdInputKind.Document && FrontMatterRecovery == MdFrontMatterRecovery.TreatAsBody && !IsCommonMark
                ? "\"frontMatterRecovery\":\"treatAsBody\"," : "";
            var input = InputKind == MdInputKind.Body ? "\"inputKind\":\"body\"," : "";
            var computed = MdHash.Compute("{" + recovery + input + "\"maxInputUtf16\":" + MaxInputUtf16.ToString(CultureInfo.InvariantCulture)
                + ",\"maxNestingDepth\":" + MaxNestingDepth.ToString(CultureInfo.InvariantCulture)
                + ",\"maxOutputItems\":" + MaxOutputItems.ToString(CultureInfo.InvariantCulture)
                + ",\"maxScanUnits\":" + MaxScanUnits.ToString(CultureInfo.InvariantCulture)
                + ",\"optionsSchemaVersion\":" + OptionsSchemaVersion.ToString(CultureInfo.InvariantCulture)
                + ",\"profileId\":\"" + ProfileId + "\"}");
            return Interlocked.CompareExchange(ref hash, computed, null) ?? computed;
        }
    }
}

internal static class MdHash
{
    internal static string Compute(string text)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(text));
        var result = new StringBuilder(64);
        foreach (var value in bytes)
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return result.ToString();
    }
    internal static int InvalidUnicode(string text, CancellationToken cancellationToken)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return i;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
                return i;
        }
        return -1;
    }
}

internal sealed class MdResourceLimit(string reason) : Exception(reason)
{
    internal string Reason { get; } = reason;
}

internal sealed class MdParseContext(MdOptions options, CancellationToken cancellationToken)
{
    private long scanUnits;
    private int outputItems;
    internal MdOptions Options { get; } = options;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal bool CaptureUnsupportedSyntax { get; set; }
    internal System.Collections.Generic.List<MdLink> Definitions { get; } = new();
    internal System.Collections.Generic.List<MdRawRange> OpaqueTokens { get; } = new();
    internal MdFootnoteState Footnotes { get; } = new();
    internal long ScanUnits => scanUnits;
    internal int OutputItems => outputItems;
    internal MdParagraphReuse? ParagraphReuse { get; init; }
    internal void Scan(long units = 1)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (units < 0)
            throw new ArgumentOutOfRangeException(nameof(units));
        if (units > Options.MaxScanUnits - scanUnits)
            throw new MdResourceLimit("scan");
        scanUnits += units;
    }
    // Charging the whole operand before a library string/regex operation is conservative.
    internal string Inspect(string value)
    {
        Scan(value.Length);
        return value;
    }
    internal void Depth(int depth)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (depth > Options.MaxNestingDepth)
            throw new MdResourceLimit("depth");
    }
    internal void Emit(int count = 1)
    {
        CancellationToken.ThrowIfCancellationRequested();
        // Keep one slot for the first resource diagnostic.
        if (count > Options.MaxOutputItems - 1 - outputItems)
            throw new MdResourceLimit("output");
        outputItems += count;
    }
}
