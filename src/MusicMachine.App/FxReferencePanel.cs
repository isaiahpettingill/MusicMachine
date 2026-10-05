using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using IconPacks.Avalonia.Material;
using MusicMachine.Core;

namespace MusicMachine.App;

/// <summary>A model-independent reference. Only an explicit Insert calls the host's edit transaction.</summary>
public sealed class FxReferencePanel : UserControl
{
    private readonly Action<string> _insert;
    private readonly TextBox _search;
    private readonly ListBox _results;
    private readonly TextBlock _resultCount, _empty, _name, _description, _preview, _meaning, _context;
    private readonly StackPanel _details, _parameters, _examples;
    private readonly Button _insertButton;
    private readonly Dictionary<char, int[]> _values = [];
    private readonly List<NumericUpDown> _inputs = [];
    private readonly HashSet<NumericUpDown> _invalidInputs = [];
    private FxDefinition? _selected;
    private char _lastSelected = 'A';
    private bool _refreshing, _canInsert;

    public string SelectedCode { get; private set; } = "";

    public FxReferencePanel(Action<string> insert)
    {
        ArgumentNullException.ThrowIfNull(insert);
        _insert = insert;
        Name = "FxReferencePanel";
        Background = Ui.Background;
        AutomationProperties.SetName(this, "FX reference and code builder");

        _search = new TextBox { Name = "FxSearch", PlaceholderText = "Search effects or codes…", MinWidth = 0 };
        AutomationProperties.SetName(_search, "Search FX names, descriptions or codes");
        ToolTip.SetTip(_search, "Try volume, arpeggio, slide down, R04 or Gxx");
        _search.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) RefreshResults(); };
        _resultCount = Text("", 10, Ui.Muted); _resultCount.Name = "FxResultCount";
        var searchArea = new StackPanel { Spacing = 5, Margin = new(0, 0, 0, 6) };
        searchArea.Children.Add(_search); searchArea.Children.Add(_resultCount);

        _results = new ListBox
        {
            Name = "FxResults", MaxHeight = 186, MinHeight = 0, Padding = new(0),
            Background = Ui.Workspace, BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_results, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_results, "Matching effects");
        _results.SelectionChanged += (_, _) =>
        {
            if (!_refreshing) ShowDefinition((_results.SelectedItem as ListBoxItem)?.Tag as FxDefinition);
        };

        _empty = Text("No matching effects. Try volume, gate, arpeggio or slide.", 12, Ui.Muted);
        _empty.Name = "FxNoResults"; _empty.Margin = new(0, 8); _empty.IsVisible = false;
        _name = Text("", 14); _name.Name = "FxEffectName"; _name.FontWeight = FontWeight.SemiBold;
        _description = Text("", 12, Ui.Muted); _description.Name = "FxDescription";
        _parameters = new StackPanel { Name = "FxParameters", Spacing = 8 };
        _preview = Text("", 20, Ui.Accent); _preview.Name = "FxCodePreview";
        _preview.FontFamily = new FontFamily("avares://MusicMachine.App/Assets/Fonts#DejaVu Sans Mono");
        _preview.FontWeight = FontWeight.SemiBold;
        AutomationProperties.SetName(_preview, "Generated FX code");
        _meaning = Text("", 11); _meaning.Name = "FxValueMeaning";
        var previewBody = new StackPanel { Spacing = 4 };
        previewBody.Children.Add(_preview); previewBody.Children.Add(_meaning);
        var preview = new Border { Background = Ui.Workspace, CornerRadius = new(4), Padding = new(9), Child = previewBody };
        _examples = new StackPanel { Name = "FxExamples", Spacing = 4 };
        _details = new StackPanel { Spacing = 10, Margin = new(0, 12, 0, 8) };
        _details.Children.Add(_name); _details.Children.Add(_description); _details.Children.Add(_parameters);
        _details.Children.Add(preview); _details.Children.Add(Text("Try a starting point", 10, Ui.Muted)); _details.Children.Add(_examples);

        var noteHelp = new StackPanel { Spacing = 8, Margin = new(0, 6, 0, 0) };
        noteHelp.Children.Add(Text("Enter these in a note cell:", 11, Ui.Muted));
        noteHelp.Children.Add(NoteHint("OFF", "Release the envelope"));
        noteHelp.Children.Add(NoteHint("CUT", "Stop immediately"));
        noteHelp.Children.Add(NoteHint("F#4T / F#4TT", "Repeat a held note every eighth / sixteenth triplet (beat ÷ 3 / 6)"));
        noteHelp.Children.Add(NoteHint("F#4 S", "Delay odd rows by the song's swing amount"));
        noteHelp.Children.Add(Text("Empty rows sustain. Timing modifiers need a pitched note and use the existing row.", 11, Ui.Muted));
        var noteSection = new Expander
        {
            Name = "FxNoteSyntax", Header = "Note & timing syntax", Content = noteHelp,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(noteSection, "Note and timing syntax reference");
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(_empty); body.Children.Add(_details); body.Children.Add(noteSection);
        var scroll = new ScrollViewer
        {
            Name = "FxDetailScroll", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        _context = Text("Select a tracker cell to insert an effect", 11, Ui.Muted);
        _context.Name = "FxInsertContext";
        _insertButton = Ui.Button("Insert effect", InsertSelected, "Insert this effect into the selected tracker row", accent: true);
        _insertButton.Name = "FxInsert"; _insertButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _insertButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        var insertContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        insertContent.Children.Add(new PackIconMaterial { Kind = PackIconMaterialKind.Plus, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center });
        insertContent.Children.Add(new TextBlock { Text = "Insert effect" });
        _insertButton.Content = insertContent;
        var footer = new StackPanel { Spacing = 7, Margin = new(0, 8, 0, 0) };
        footer.Children.Add(_context); footer.Children.Add(_insertButton);
        footer.Children.Add(Text("FX run on this row. No extra time slot.", 10, Ui.Muted));

        var layout = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(12, 10) };
        layout.Children.Add(searchArea);
        Grid.SetRow(_results, 1); layout.Children.Add(_results);
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        Grid.SetRow(footer, 3); layout.Children.Add(footer);
        Content = layout;
        RefreshResults();
    }

    /// <summary>Changes only the insertion target; search and parameter edits remain intact.</summary>
    public void SetContext(string targetDescription, bool canInsert)
    {
        _context.Text = targetDescription;
        ToolTip.SetTip(_context, targetDescription);
        _canInsert = canInsert;
        UpdateInsertState();
    }

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

    private void RefreshResults()
    {
        var matches = FxCatalog.Search(_search.Text);
        var selected = matches.FirstOrDefault(effect => effect.Command == _lastSelected) ?? matches.FirstOrDefault();
        if (selected is not null && FxParser.TryParse(_search.Text, out var parsed, out _) && !parsed.IsEmpty)
            _values[selected.Command] = selected.GetParameters(parsed.Value);
        _refreshing = true;
        try
        {
            _results.ItemsSource = matches.Select(effect =>
            {
                var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7 };
                var label = Text(effect.Name, 12);
                var syntax = Text(effect.Syntax, 10, Ui.Muted);
                row.Children.Add(label); Grid.SetColumn(syntax, 1); row.Children.Add(syntax);
                var item = new ListBoxItem { Name = "FxResult" + effect.Command, Tag = effect, Content = row, Padding = new(8, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, $"{effect.Name}, {effect.Syntax}");
                ToolTip.SetTip(item, effect.Description);
                return item;
            }).ToArray();
            _results.SelectedIndex = selected is null ? -1 : matches.ToList().IndexOf(selected);
        }
        finally { _refreshing = false; }
        _results.IsVisible = matches.Count > 0;
        _empty.IsVisible = matches.Count == 0;
        _resultCount.Text = matches.Count == 1 ? "1 effect" : $"{matches.Count} effects";
        ShowDefinition(selected);
    }

    private void ShowDefinition(FxDefinition? effect)
    {
        _refreshing = true;
        try { BuildDefinition(effect); }
        finally { _refreshing = false; }
        UpdatePreview();
    }

    private void BuildDefinition(FxDefinition? effect)
    {
        _selected = effect;
        _details.IsVisible = effect is not null;
        _parameters.Children.Clear(); _examples.Children.Clear(); _inputs.Clear(); _invalidInputs.Clear();
        if (effect is null) { SelectedCode = ""; UpdateInsertState(); return; }
        _lastSelected = effect.Command;
        _name.Text = effect.Name; _description.Text = effect.Description;
        if (!_values.TryGetValue(effect.Command, out var values))
            _values[effect.Command] = values = effect.Parameters.Select(parameter => parameter.DefaultValue).ToArray();
        for (var index = 0; index < effect.Parameters.Count; index++)
        {
            var parameter = effect.Parameters[index];
            var input = new NumericUpDown
            {
                Name = "FxParameter" + index, Minimum = parameter.Minimum, Maximum = parameter.Maximum,
                Increment = 1, FormatString = "0", NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
                ParsingNumberStyle = NumberStyles.Integer,
                Value = values[index], ShowButtonSpinner = true, HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };
            AutomationProperties.SetName(input, $"{effect.Name}: {parameter.Name}, {parameter.Minimum} to {parameter.Maximum}");
            ToolTip.SetTip(input, parameter.Help);
            input.ValueChanged += (_, _) => UpdatePreview();
            input.PropertyChanged += (_, args) =>
            {
                if (args.Property != NumericUpDown.TextProperty) return;
                // A NumericUpDown retains its last Value while its text is invalid. Do not insert that stale value.
                if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed) ||
                    typed < parameter.Minimum || typed > parameter.Maximum) _invalidInputs.Add(input);
                else _invalidInputs.Remove(input);
                UpdatePreview();
            };
            _inputs.Add(input);
            var field = new StackPanel { Spacing = 3 };
            field.Children.Add(Text($"{parameter.Name} ({parameter.Minimum}–{parameter.Maximum})", 11, Ui.Muted));
            field.Children.Add(input); _parameters.Children.Add(field);
        }
        foreach (var example in effect.Examples)
        {
            var button = Ui.Button(example.Description, () => ApplyExample(effect, example), $"Use {example.Code}: {example.Description}");
            button.Name = "FxExample" + example.Code;
            button.Content = Text(example.Description, 11);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new(8, 5);
            _examples.Children.Add(button);
        }
    }

    private void ApplyExample(FxDefinition effect, FxExample example)
    {
        if (_selected != effect || !FxParser.TryParse(example.Code, out var parsed, out _)) return;
        var values = effect.GetParameters(parsed.Value);
        _refreshing = true;
        try
        {
            for (var index = 0; index < values.Length; index++)
            {
                _inputs[index].Value = values[index];
                _inputs[index].Text = values[index].ToString(CultureInfo.InvariantCulture);
            }
        }
        finally { _refreshing = false; }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_refreshing || _selected is null || _inputs.Count != _selected.Parameters.Count) return;
        if (_invalidInputs.Count > 0 || _inputs.Any(input => input.Value is null || input.Value != decimal.Truncate(input.Value.Value) ||
            input.Value < input.Minimum || input.Value > input.Maximum))
        {
            SelectedCode = ""; _preview.Text = "…"; _meaning.Text = "Enter a whole number for each parameter";
            UpdateInsertState(); return;
        }
        var values = _inputs.Select(input => (int)input.Value!.Value).ToArray();
        _values[_selected.Command] = values;
        SelectedCode = _selected.Format(values);
        _preview.Text = SelectedCode;
        _meaning.Text = _selected.DescribeValue(FxParser.TryParse(SelectedCode, out var effect, out _) ? effect.Value : (byte)0);
        UpdateInsertState();
    }

    private void UpdateInsertState() => _insertButton.IsEnabled = _canInsert && SelectedCode.Length > 0;

    private void InsertSelected()
    {
        // Guard the handler as well as the button: a stale UI event must never insert without a target.
        if (_canInsert && SelectedCode.Length > 0) _insert(SelectedCode);
    }

    private static TextBlock Text(string text, double size, IBrush? brush = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Ui.Text, TextWrapping = TextWrapping.Wrap
    };

    private static StackPanel NoteHint(string syntax, string description)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Text(syntax, 11, Ui.Accent)); panel.Children.Add(Text(description, 11, Ui.Muted));
        return panel;
    }
}
