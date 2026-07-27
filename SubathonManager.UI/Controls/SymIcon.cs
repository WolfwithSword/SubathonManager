using Avalonia;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace SubathonManager.UI.Controls;

// makes replacement easy for symbol icon useage from wpf
public class SymIcon : SymbolIcon
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<SymIcon, string?>(nameof(Glyph));

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    static SymIcon()
    {
        GlyphProperty.Changed.AddClassHandler<SymIcon>((s, e) => s.ApplyGlyph(e.NewValue as string));
    }

    private void ApplyGlyph(string? glyph)
    {
        if (string.IsNullOrWhiteSpace(glyph)) return;
        int i = glyph.Length;
        while (i > 0 && char.IsDigit(glyph[i - 1])) i--;

        var name = glyph[..i];
        var sizeStr = glyph[i..];

        if (Enum.TryParse<Symbol>(name, out var sym))
            Symbol = sym;
        if (double.TryParse(sizeStr, out var size) && size > 0)
            FontSize = size;
    }
}
