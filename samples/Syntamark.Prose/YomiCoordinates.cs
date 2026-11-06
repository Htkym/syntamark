using System.Buffers;
using System.Text;
using Syntamark;

namespace Syntamark.Prose.Sample;

internal readonly record struct CodePointRange(int Start, int Length);

// Optional coordinate bridge only. It does not run or replace Yomiyasu's scanner.
internal sealed class YomiCoordinates
{
    private readonly int[] rawBoundaries;
    internal YomiCoordinates(string raw)
    {
        var position = raw.StartsWith('\uFEFF') ? 1 : 0;
        var boundaries = new List<int> { position };
        var normalized = new StringBuilder();
        while (position < raw.Length)
        {
            if (raw[position] == '\r')
            {
                position += position + 1 < raw.Length && raw[position + 1] == '\n' ? 2 : 1;
                normalized.Append('\n');
            }
            else
            {
                if (Rune.DecodeFromUtf16(raw.AsSpan(position), out var rune, out var width) != OperationStatus.Done)
                    throw new ArgumentException("Well-formed Unicode is required.", nameof(raw));
                normalized.Append(rune.ToString());
                position += width;
            }
            boundaries.Add(position);
        }
        Text = normalized.ToString();
        rawBoundaries = boundaries.ToArray();
    }
    internal string Text { get; }
    internal RawSpan ToRaw(CodePointRange range)
    {
        if (range.Start < 0 || range.Length < 0 || (long)range.Start + range.Length >= rawBoundaries.Length)
            throw new ArgumentOutOfRangeException(nameof(range));
        var start = rawBoundaries[range.Start];
        return new(start, rawBoundaries[range.Start + range.Length] - start);
    }
    internal CodePointRange ToPython(RawSpan range)
    {
        var start = Array.BinarySearch(rawBoundaries, range.Start);
        var end = Array.BinarySearch(rawBoundaries, range.End);
        if (start < 0 || end < 0)
            throw new ArgumentException("The range must use retained Unicode scalar and line-ending boundaries.", nameof(range));
        return new(start, end - start);
    }
}
