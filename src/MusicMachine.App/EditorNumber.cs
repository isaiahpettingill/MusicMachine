using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;

namespace MusicMachine.App;

/// <summary>A numeric text draft is one edit: Enter/blur applies it, Escape cancels it.</summary>
internal sealed class EditorNumber : NumericUpDown
{
    private decimal? committed;
    private bool synchronizing;
    public event Action<decimal>? Committed;
    internal decimal? ModelValue => committed;
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    public void SetModelValue(decimal value, bool resetDraft = false)
    {
        // Unrelated refreshes must not erase an unfinished or rejected text draft.
        // An actual model change (undo, selection change) supersedes that draft.
        if (!resetDraft && committed == value) return;
        synchronizing = true;
        try { committed = value; Value = value; Text = Format(value); SetValue(DataValidationErrors.ErrorsProperty, null); }
        finally { synchronizing = false; }
    }

    protected override void OnValueChanged(decimal? oldValue, decimal? newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        // Programmatic assignments and unfocused spinner/accessibility changes remain
        // supported. Focused typing is only a local draft until explicitly committed.
        if (!synchronizing && !IsKeyboardFocusWithin && newValue is { } value)
            Apply(value);
    }

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        base.OnTextChanged(oldValue, newValue);
        if (TryDraft(out _)) SetValue(DataValidationErrors.ErrorsProperty, null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (committed is { } value) SetModelValue(value, true);
            e.Handled = true; return;
        }
        if (e.Key == Key.Enter)
        {
            CommitDraft(); e.Handled = true; return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Up or Key.Down) CommitDraft();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        var draft = Text;
        var unchanged = committed is { } existing && draft == Format(existing);
        var valid = TryDraft(out var value);
        // Avalonia normalizes Text on blur, including invalid text. Preserve rejected
        // text so a typo never commits the last parseable prefix or disappears silently.
        synchronizing = true;
        try
        {
            base.OnLostFocus(e);
            if (!valid) Text = draft;
        }
        finally { synchronizing = false; }
        if (!IsKeyboardFocusWithin)
        {
            if (valid && !unchanged) Apply(value);
            else if (!valid) RejectDraft();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Spinner buttons can keep focus inside the control. Clicking the text itself
        // is not a commit: it must remain possible to position the caret in a draft.
        if (e.Source is Control source && (source is Button || source.FindAncestorOfType<Button>() is not null))
            CommitDraft();
    }

    private void CommitDraft() => TryCommitDraft();
    internal bool TryCommitDraft()
    {
        // A formatted readout may round a higher-precision model value. Only user
        // text differing from that readout is a draft; saving must not quantize it.
        if (committed is { } existing && Text == Format(existing)) return true;
        if (!TryDraft(out var value)) { RejectDraft(); return false; }
        Apply(value);
        return committed == value;
    }
    private void RejectDraft() => SetValue(DataValidationErrors.ErrorsProperty,
        new object[] { $"Enter a number from {Minimum} to {Maximum}, or press Escape to cancel." });
    private bool TryDraft(out decimal value) => decimal.TryParse(Text, ParsingNumberStyle, NumberFormat, out value)
        && value >= Minimum && value <= Maximum;
    private string Format(decimal value) => value.ToString(FormatString, NumberFormat);
    private void Apply(decimal value)
    {
        if (synchronizing || committed == value || value < Minimum || value > Maximum) return;
        committed = value;
        Committed?.Invoke(value);
    }
}
