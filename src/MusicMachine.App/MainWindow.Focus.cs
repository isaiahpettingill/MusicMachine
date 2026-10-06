using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private bool pointerInteraction, pointerRefreshPending;
    private int pointerGesture;
    private Window? pointerWindow;
    private void PointerWindowDeactivated(object? sender, EventArgs e) => CompletePointerRefresh();
    private void CompletePointerRefresh()
    {
        pointerGesture++; pointerInteraction = false;
        if (!pointerRefreshPending) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!pointerInteraction && pointerRefreshPending) Refresh();
        }, DispatcherPriority.Background);
    }

    private sealed record EditorFocus(Control Original, string Workspace, int NumberIndex, string? Name,
        decimal? ModelValue, string? Text, int Caret, int SelectionStart, int SelectionEnd);

    private bool CommitEditorFields()
    {
        // Save/close shortcuts can run without a blur or file picker. Validate every
        // live numeric draft before taking a snapshot or deciding the song is clean.
        foreach (var number in this.GetLogicalDescendants().OfType<EditorNumber>()
            .Where(n => n.IsEffectivelyEnabled && n.ModelValue is not null
                && n.GetLogicalAncestors().OfType<Control>().All(parent => parent.IsEnabled)).ToArray())
        {
            if (number.TryCommitDraft()) continue;
            number.Focus(); SetStatus("Finish the numeric value or press Escape to cancel before continuing"); return false;
        }
        return true;
    }

    private EditorFocus? CaptureEditorFocus()
    {
        if (FocusManager?.GetFocusedElement() is not Control focused) return null;
        var number = focused as NumericUpDown ?? focused.GetVisualAncestors().OfType<NumericUpDown>().FirstOrDefault();
        if (focused is DrumGrid) return new(focused, displayedMode, -2, null, null, null, 0, 0, 0);
        if (number is null)
            return focused.Name is { Length: > 0 } name ? new(focused, displayedMode, -1, name, null, null, 0, 0, 0) : null;
        var index = this.GetLogicalDescendants().OfType<NumericUpDown>().ToList().IndexOf(number);
        var text = focused as TextBox;
        return new(focused, displayedMode, index, number.Name, (number as EditorNumber)?.ModelValue ?? number.Value, text?.Text, text?.CaretIndex ?? 0,
            text?.SelectionStart ?? 0, text?.SelectionEnd ?? 0);
    }

    private void RestoreEditorFocus(EditorFocus? saved)
    {
        if (saved is null || saved.Workspace != mode || TopLevel.GetTopLevel(saved.Original) is not null) return;
        // Dynamic workspaces (Arrangement/Drums) rebuild from the current snapshot.
        // Restore the corresponding editor after layout, but never steal newer focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (mode != saved.Workspace || OverlayRoot.Children.Count > 1 || FocusManager?.GetFocusedElement() is { } current && !ReferenceEquals(current, saved.Original) && !(saved.NumberIndex == -2 && ReferenceEquals(current, this))) return;
            if (saved.NumberIndex == -2) { drumGridSurface?.Focus(); return; }
            if (saved.NumberIndex < 0)
            {
                this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == saved.Name)?.Focus();
                return;
            }
            var numbers = this.GetLogicalDescendants().OfType<NumericUpDown>().ToArray();
            var number = saved.Name is { Length: > 0 }
                ? numbers.FirstOrDefault(n => n.Name == saved.Name)
                : numbers.ElementAtOrDefault(saved.NumberIndex);
            if (number is null) return;
            number.ApplyTemplate();
            var text = number.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (text is null || !text.Focus()) return;
            if (((number as EditorNumber)?.ModelValue ?? number.Value) == saved.ModelValue) text.Text = saved.Text;
            text.CaretIndex = Math.Min(saved.Caret, text.Text?.Length ?? 0);
            text.SelectionStart = Math.Min(saved.SelectionStart, text.Text?.Length ?? 0);
            text.SelectionEnd = Math.Min(saved.SelectionEnd, text.Text?.Length ?? 0);
        }, DispatcherPriority.Background);
    }
}
