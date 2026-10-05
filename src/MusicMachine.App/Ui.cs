using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;
using IconPacks.Avalonia.Material;
namespace MusicMachine.App;
internal static class Ui
{
    // Keep brush identities stable: already-mounted controls and drawing pens
    // receive color changes without rebuilding the editor or losing focus.
    public static readonly SolidColorBrush Background = new(Color.Parse("#1E1E2E"));
    public static readonly SolidColorBrush Surface = new(Color.Parse("#313244"));
    public static readonly SolidColorBrush Header = new(Color.Parse("#45475A"));
    public static readonly SolidColorBrush Workspace = new(Color.Parse("#181825"));
    public static readonly SolidColorBrush Line = new(Color.Parse("#6C7086"));
    public static readonly SolidColorBrush Text = new(Color.Parse("#CDD6F4"));
    public static readonly SolidColorBrush Muted = new(Color.Parse("#BAC2DE"));
    public static readonly SolidColorBrush Accent = new(Color.Parse("#89B4FA"));
    public static readonly SolidColorBrush Selection = new(Color.Parse("#45475A"));
    public static readonly SolidColorBrush Error = new(Color.Parse("#F38BA8"));
    public static readonly SolidColorBrush Success = new(Color.Parse("#A6E3A1"));
    // Preserve the legacy secondary-emphasis role using each editor palette's warm color.
    public static readonly SolidColorBrush Orange = new(Color.Parse("#F38BA8"));
    public static readonly SolidColorBrush AccentText = new(Color.Parse("#181825"));

    internal static IReadOnlyDictionary<string, SolidColorBrush> ThemeResources { get; } = new Dictionary<string, SolidColorBrush>
    {
        ["EditorBackground"] = Background, ["EditorSurface"] = Surface,
        ["EditorHeader"] = Header, ["EditorWorkspace"] = Workspace,
        ["EditorBorder"] = Line, ["EditorText"] = Text, ["EditorMuted"] = Muted,
        ["EditorAccent"] = Accent, ["EditorSelection"] = Selection,
        ["EditorError"] = Error, ["EditorSuccess"] = Success,
        ["EditorSecondary"] = Orange, ["EditorAccentText"] = AccentText
    };

    internal static void ApplyTheme(EditorTheme theme)
    {
        Background.Color = Color.Parse(theme.Background); Surface.Color = Color.Parse(theme.Surface);
        Header.Color = Color.Parse(theme.Header); Workspace.Color = Color.Parse(theme.Workspace);
        Line.Color = Color.Parse(theme.Border); Text.Color = Color.Parse(theme.Text);
        Muted.Color = Color.Parse(theme.Muted); Accent.Color = Color.Parse(theme.Accent);
        Selection.Color = Color.Parse(theme.Selection); Error.Color = Color.Parse(theme.Error);
        Success.Color = Color.Parse(theme.Success); Orange.Color = Color.Parse(theme.Error);
        AccentText.Color = Color.Parse(theme.Dark ? theme.Workspace : theme.Id == "solarized-light" ? theme.Text : theme.Background);
    }

    // Translate the previous fixed chrome colors into live theme roles. Track colors
    // are content, so unrecognized colors intentionally retain their original value.
    public static IBrush ThemeBrush(string hex) => hex.ToUpperInvariant() switch
    {
        "#101620" or "#141F2C" or "#162230" => Background,
        "#182231" or "#1C2A3C" or "#1B2B3C" => Surface,
        "#121B28" or "#10241D" => Workspace,
        "#202B3B" or "#263447" or "#233347" or "#26313E" or "#263648" or "#1C2B3D" => Header,
        "#29374A" or "#2B394B" or "#466170" or "#4A6177" => Line,
        "#E8EDF6" => Text,
        "#8C9BAF" or "#566270" or "#557C79" => Muted,
        "#77E5C0" or "#91EDCB" => Accent,
        "#FFBB86" or "#8C542D" => Orange,
        "#2C4351" or "#21463D" or "#2C544F" or "#1B3A36" or "#183D38" or "#235449" or "#284252" or "#344642" or "#47362A" => Selection,
        _ => Brush.Parse(hex)
    };

    public static void Detach(Control c) { if (c.Parent is Panel p) p.Children.Remove(c); else if (c.Parent is ContentControl cc) { cc.Content = null; cc.Presenter?.UpdateChild(); } else if (c.Parent is Decorator d) d.Child = null; }
    public static TextBlock Label(string text, double size = 12, IBrush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Text, VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 10, LetterSpacing = 1.5, Foreground = Muted, Margin = new(0, 0, 0, 10), FontWeight = FontWeight.SemiBold };
    public static Button Button(string text, Action action, string? tip = null, bool accent = false)
    {
        var button = new Button { Content = text }; button.Click += (_, _) => action();
        AutomationProperties.SetName(button, tip ?? text); ToolTip.SetTip(button, tip ?? text);
        if (accent) button.Classes.Add("accent"); return button;
    }
    public static Button IconButton(PackIconMaterialKind kind, Action action, string tooltip, bool accent = false)
    {
        var icon = new PackIconMaterial { Kind = kind, Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = icon, Width = 32, Height = 32, MinWidth = 32,
            MinHeight = 32, Padding = new Thickness(7), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center };
        button.Classes.Add("icon");
        if (accent) button.Classes.Add("accent");
        AutomationProperties.SetName(button, tooltip); ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
    public static StackPanel Row(params Control[] children) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center }; foreach (var c in children) p.Children.Add(c); return p; }
    public static Border Panel(Control child, Thickness? padding = null) => new() { Child = child, Background = Surface, Padding = padding ?? new Thickness(12), BorderBrush = Line, BorderThickness = new(0, 0, 1, 0) };
    public static FormattedText Fmt(string text, double size, IBrush? brush = null, bool mono = false) => new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(mono ? "avares://MusicMachine.App/Assets/Fonts#DejaVu Sans Mono" : "avares://Avalonia.Fonts.Inter/Assets#Inter"), size, brush ?? Text);
}
