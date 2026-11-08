using System.Text;

namespace Ai.Tlbx.MidTerm.Common.Text;

public static class Utf16Text
{
    /// <summary>Shortens text without leaving a lone high surrogate, which JSON writers reject.</summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= maxLength ? value : value[..SafeLength(value.AsSpan(), maxLength)];
    }

    public static void Truncate(StringBuilder builder, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Length <= maxLength) return;
        builder.Length = maxLength > 0 && char.IsHighSurrogate(builder[maxLength - 1]) ? maxLength - 1 : Math.Max(0, maxLength);
    }

    public static int SafeLength(ReadOnlySpan<char> text, int maxLength)
    {
        if (maxLength >= text.Length) return text.Length;
        if (maxLength <= 0) return 0;
        return char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
    }
}
