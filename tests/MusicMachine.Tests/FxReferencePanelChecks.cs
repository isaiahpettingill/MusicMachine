using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

// Run from the existing headless editor test after its Fluent theme is installed.
internal static class FxReferencePanelChecks
{
    internal static void Run()
    {
        var inserted = new List<string>();
        var panel = new FxReferencePanel(inserted.Add);
        T Named<T>(string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Click(string name) => Named<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var search = Named<TextBox>("FxSearch");
        var results = Named<ListBox>("FxResults");
        Assert.Equal(6, results.ItemCount);
        Assert.Equal("A37", panel.SelectedCode);
        Assert.False(Named<Button>("FxInsert").IsEnabled);
        Click("FxInsert"); Assert.Empty(inserted);

        panel.SetContext("Lead · row 1 · FX 1", true);
        Assert.True(Named<Button>("FxInsert").IsEnabled);
        Click("FxInsert"); Click("FxInsert"); Assert.Equal(new[] { "A37", "A37" }, inserted);
        Assert.Equal(6, results.ItemCount);
        search.Text = "  VOLume  ";
        Assert.Equal(1, results.ItemCount); Assert.Equal("VC0", panel.SelectedCode);
        Assert.Contains("75.3%", Named<TextBlock>("FxValueMeaning").Text);
        Named<NumericUpDown>("FxParameter0").Value = 128;
        Assert.Equal("V80", panel.SelectedCode);
        Assert.Contains("50.2%", Named<TextBlock>("FxValueMeaning").Text);
        Assert.Equal(2, inserted.Count); // browsing, presets, and context changes never edit the song
        panel.SetContext("Bass · row 12 · FX 2", true);
        Assert.Equal("  VOLume  ", search.Text); Assert.Equal("V80", panel.SelectedCode);
        Click("FxInsert"); Assert.Equal("V80", inserted[^1]);
        Assert.Equal("Bass · row 12 · FX 2", Named<TextBlock>("FxInsertContext").Text);
        Click("FxExampleVFF"); Assert.Equal("VFF", panel.SelectedCode); Assert.Equal(3, inserted.Count);

        search.Text = "arpeggio";
        Named<NumericUpDown>("FxParameter0").Value = 4;
        Named<NumericUpDown>("FxParameter1").Value = 12;
        Assert.Equal("A4C", panel.SelectedCode);
        search.Text = "Vxx"; Assert.Equal("VFF", panel.SelectedCode);
        search.Text = "Axy"; Assert.Equal("A4C", panel.SelectedCode);
        Click("FxExampleA37"); Assert.Equal("A37", panel.SelectedCode);

        search.Text = "retrig";
        var count = Named<NumericUpDown>("FxParameter0");
        Assert.Equal(32, count.Maximum); Assert.Equal(0, count.Minimum);
        count.Value = 16; Assert.Equal("R10", panel.SelectedCode);
        count.Value = 32; Assert.Equal("R20", panel.SelectedCode);
        count.Value = 0; Assert.Equal("R00", panel.SelectedCode);
        Assert.Contains("T / TT", Named<TextBlock>("FxValueMeaning").Text);
        count.Value = null;
        Assert.Empty(panel.SelectedCode); Assert.False(Named<Button>("FxInsert").IsEnabled);
        Click("FxInsert"); Assert.Equal(3, inserted.Count);
        count.Value = 4; Assert.Equal("R04", panel.SelectedCode);
        count.Text = "banana";
        Assert.Empty(panel.SelectedCode); Assert.False(Named<Button>("FxInsert").IsEnabled);
        Click("FxInsert"); Assert.Equal(3, inserted.Count);
        count.Text = "4"; Assert.Equal("R04", panel.SelectedCode);
        count.Text = "2.5"; Assert.Empty(panel.SelectedCode);
        count.Text = "4"; Assert.Equal("R04", panel.SelectedCode);
        search.Text = "r1f"; Assert.Equal("R1F", panel.SelectedCode);
        Assert.Equal(31, Named<NumericUpDown>("FxParameter0").Value);

        search.Text = "no such sound code";
        Assert.Equal(0, results.ItemCount); Assert.True(Named<TextBlock>("FxNoResults").IsVisible);
        Assert.Empty(panel.SelectedCode); Assert.False(Named<Button>("FxInsert").IsEnabled);
        Click("FxInsert"); Assert.Equal(3, inserted.Count);
        search.Text = ""; Assert.Equal(6, results.ItemCount); Assert.Equal("R1F", panel.SelectedCode);
        Assert.False(Named<TextBlock>("FxNoResults").IsVisible);
        panel.SetContext("Select a tracker track first", false);
        Assert.Equal("R1F", panel.SelectedCode); Click("FxInsert"); Assert.Equal(3, inserted.Count);
        panel.SetContext("Lead · row 2 · FX 1", true); Click("FxInsert"); Assert.Equal("R1F", inserted[^1]);

        foreach (var example in FxCatalog.All.SelectMany(effect => effect.Examples))
        {
            search.Text = example.Code;
            Assert.Equal(example.Code, panel.SelectedCode);
            Assert.True(FxParser.TryParse(panel.SelectedCode, out _, out _));
        }
        foreach (var name in new[] { "FxSearch", "FxResults", "FxInsert", "FxParameter0" })
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(Named<Control>(name))));
        Assert.NotNull(ToolTip.GetTip(Named<Button>("FxInsert")));
        foreach (var syntax in new[] { "OFF", "CUT", "F#4T", "F#4TT", "F#4 S" })
            Assert.True(NoteParser.TryParse(syntax, out _, out _));

        // The actual template is laid out at the inspector's 248 px width, including expanded help.
        search.Text = "";
        var window = new Window { Width = 248, Height = 650, Content = panel };
        window.Show(); window.UpdateLayout();
        try
        {
            foreach (var query in new[] { "", "arpeggio", "volume", "gate", "slide down", "retrig", "no match" })
            {
                search.Text = query;
                Named<Expander>("FxNoteSyntax").IsExpanded = true;
                window.UpdateLayout();
                var scroll = Named<ScrollViewer>("FxDetailScroll");
                Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                    $"FX reference overflowed at 248px for '{query}': {scroll.Extent.Width}/{scroll.Viewport.Width}");
                Assert.True(Named<Button>("FxInsert").Bounds.Bottom <= panel.Bounds.Height);
            }
            search.Text = "R04"; window.UpdateLayout();
            panel.FocusSearch(); Assert.True(search.IsFocused);
            panel.SetContext("A long track name that should wrap · row 1024 · FX 16", true);
            window.UpdateLayout(); Assert.Equal("R04", panel.SelectedCode);
            Assert.True(search.IsFocused);
        }
        finally { window.Close(); }
    }
}
