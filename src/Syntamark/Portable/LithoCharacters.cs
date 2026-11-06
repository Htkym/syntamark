using System.Globalization;
using System.Text;

namespace Syntamark.Compilation;

/// <summary>Character classes and profile-specific label handling shared by all parser targets.</summary>
internal static class LithoCharacters
{
    public static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    public static bool IsAsciiLetterOrDigit(char value) => IsAsciiLetter(value) || value is >= '0' and <= '9';

    internal static bool IsCommonMarkWhitespace(char value) =>
        value is '\t' or '\n' or '\f' or '\r' || char.GetUnicodeCategory(value) == UnicodeCategory.SpaceSeparator;

    internal static bool IsCommonMarkPunctuation(string text, int index) =>
        CharUnicodeInfo.GetUnicodeCategory(text, index) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

    internal static bool IsCommonMarkReferenceLabel(string label, MdParseContext? context)
    {
        if (label.Length is 0 or > 1998)
            return false;
        var content = false;
        var characters = 0;
        for (var i = 0; i < label.Length; i++)
        {
            context?.Scan(8);
            characters++;
            if (label[i] == '\\' && i + 1 < label.Length && IsAsciiPunctuation(label[i + 1]))
            {
                content = true;
                characters++;
                i++;
            }
            else if (label[i] is '[' or ']')
                return false;
            else
            {
                if (!IsReferenceWhitespace(label[i]))
                    content = true;
                if (char.IsHighSurrogate(label[i]) && i + 1 < label.Length && char.IsLowSurrogate(label[i + 1]))
                    i++;
            }
            if (characters > 999)
                return false;
        }
        return content;
    }

    internal static bool IsAsciiPunctuation(char value) =>
        value is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    private static bool IsReferenceWhitespace(char value) => value is ' ' or '\t' or '\r' or '\n';

    internal static string NormalizeReferenceLabel(string label, MdParseContext? context)
    {
        var result = new StringBuilder(label.Length);
        var pendingSpace = false;
        for (var i = 0; i < label.Length; i++)
        {
            context?.Scan(8);
            if (IsReferenceWhitespace(label[i]))
            {
                pendingSpace = result.Length != 0;
                continue;
            }
            if (pendingSpace)
                result.Append(' ');
            pendingSpace = false;
            var paired = char.IsHighSurrogate(label[i]) && i + 1 < label.Length && char.IsLowSurrogate(label[i + 1]);
            var codepoint = paired ? char.ConvertToUtf32(label[i], label[i + 1]) : label[i];
            if (CommonMarkCaseFolding.TryFold(codepoint, out var folded))
                result.Append(folded);
            else
                result.Append(label, i, paired ? 2 : 1);
            if (paired)
                i++;
        }
        return result.ToString();
    }
}
