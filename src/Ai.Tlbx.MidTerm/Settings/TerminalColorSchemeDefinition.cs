namespace Ai.Tlbx.MidTerm.Settings;

public sealed class TerminalColorSchemeDefinition
{
    public string Name { get; set; } = "";
    public string Background { get; set; } = "#0C0C0C";
    public string Foreground { get; set; } = "#F2F2F2";
    public string Cursor { get; set; } = "#F2F2F2";
    public string CursorAccent { get; set; } = "#0C0C0C";
    public string SelectionBackground { get; set; } = "#7BA2F780";
    public string ScrollbarSliderBackground { get; set; } = "rgba(58, 62, 82, 0.5)";
    public string ScrollbarSliderHoverBackground { get; set; } = "rgba(123, 162, 247, 0.5)";
    public string ScrollbarSliderActiveBackground { get; set; } = "rgba(123, 162, 247, 0.7)";
    public string Black { get; set; } = "#0C0C0C";
    public string Red { get; set; } = "#FF4055";
    public string Green { get; set; } = "#32E03B";
    public string Yellow { get; set; } = "#FFCC00";
    public string Blue { get; set; } = "#2B65FF";
    public string Magenta { get; set; } = "#C73DFF";
    public string Cyan { get; set; } = "#35CFFF";
    public string White { get; set; } = "#5ABEFF";
    public string BrightBlack { get; set; } = "#767676";
    public string BrightRed { get; set; } = "#FF6B7D";
    public string BrightGreen { get; set; } = "#68FF68";
    public string BrightYellow { get; set; } = "#FFF59A";
    public string BrightBlue { get; set; } = "#7DA6FF";
    public string BrightMagenta { get; set; } = "#E667FF";
    public string BrightCyan { get; set; } = "#7AF7FF";
    public string BrightWhite { get; set; } = "#F2F2F2";

    internal static TerminalColorSchemeDefinition? Normalize(TerminalColorSchemeDefinition? scheme)
    {
        if (scheme is null)
        {
            return null;
        }

        var name = NormalizeRequiredString(scheme.Name, fallback: "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new TerminalColorSchemeDefinition
        {
            Name = name,
            Background = NormalizeRequiredString(scheme.Background, "#0C0C0C"),
            Foreground = NormalizeRequiredString(scheme.Foreground, "#F2F2F2"),
            Cursor = NormalizeRequiredString(scheme.Cursor, "#F2F2F2"),
            CursorAccent = NormalizeRequiredString(scheme.CursorAccent, "#0C0C0C"),
            SelectionBackground = NormalizeRequiredString(scheme.SelectionBackground, "#7BA2F780"),
            ScrollbarSliderBackground = NormalizeRequiredString(scheme.ScrollbarSliderBackground, "rgba(58, 62, 82, 0.5)"),
            ScrollbarSliderHoverBackground = NormalizeRequiredString(scheme.ScrollbarSliderHoverBackground, "rgba(123, 162, 247, 0.5)"),
            ScrollbarSliderActiveBackground = NormalizeRequiredString(scheme.ScrollbarSliderActiveBackground, "rgba(123, 162, 247, 0.7)"),
            Black = NormalizeRequiredString(scheme.Black, "#0C0C0C"),
            Red = NormalizeRequiredString(scheme.Red, "#FF4055"),
            Green = NormalizeRequiredString(scheme.Green, "#32E03B"),
            Yellow = NormalizeRequiredString(scheme.Yellow, "#FFCC00"),
            Blue = NormalizeRequiredString(scheme.Blue, "#2B65FF"),
            Magenta = NormalizeRequiredString(scheme.Magenta, "#C73DFF"),
            Cyan = NormalizeRequiredString(scheme.Cyan, "#35CFFF"),
            White = NormalizeRequiredString(scheme.White, "#5ABEFF"),
            BrightBlack = NormalizeRequiredString(scheme.BrightBlack, "#767676"),
            BrightRed = NormalizeRequiredString(scheme.BrightRed, "#FF6B7D"),
            BrightGreen = NormalizeRequiredString(scheme.BrightGreen, "#68FF68"),
            BrightYellow = NormalizeRequiredString(scheme.BrightYellow, "#FFF59A"),
            BrightBlue = NormalizeRequiredString(scheme.BrightBlue, "#7DA6FF"),
            BrightMagenta = NormalizeRequiredString(scheme.BrightMagenta, "#E667FF"),
            BrightCyan = NormalizeRequiredString(scheme.BrightCyan, "#7AF7FF"),
            BrightWhite = NormalizeRequiredString(scheme.BrightWhite, "#F2F2F2")
        };
    }

    private static string NormalizeRequiredString(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
