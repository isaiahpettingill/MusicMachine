using System.Globalization;

namespace MusicMachine.Core;

/// <summary>Tracker input grammar. MIDI C4 is 60; omitted octaves inherit their preceding pitched row.</summary>
public static class NoteParser
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static bool TryParse(string? text, int previousOctave, out NoteEvent note, out string error)
    {
        note = new();
        error = "";
        var input = (text ?? "").Trim().ToUpperInvariant();
        if (input.Length == 0 || input == "---" || input == "...") return true;
        if (input == "OFF") { note.Kind = NoteKind.Off; return true; }
        if (input == "CUT") { note.Kind = NoteKind.Cut; return true; }
        if (input.Length > 12) return Fail("A note is too long", out error);
        if (input.EndsWith("TT", StringComparison.Ordinal)) { note.Timing = NoteTiming.TripletSixteenth; input = input[..^2].TrimEnd(); }
        else if (input.EndsWith('T')) { note.Timing = NoteTiming.TripletEighth; input = input[..^1].TrimEnd(); }
        else if (input.EndsWith('S')) { note.Timing = NoteTiming.Swing; input = input[..^1].TrimEnd(); }
        if (input.Length == 0) return Fail("Enter a note A–G, optionally # and an octave", out error);
        var semitone = input[0] switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => -1 };
        if (semitone < 0) return Fail("Use A–G, OFF or CUT", out error);
        var index = 1;
        if (index < input.Length && input[index] == '#') { semitone++; index++; }
        var octave = previousOctave is >= -1 and <= 9 ? previousOctave : 4;
        if (index < input.Length && !int.TryParse(input.AsSpan(index), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out octave))
            return Fail("Use a note such as F#4, F#4T, F#4TT or F#4 S", out error);
        if (octave is < -1 or > 9) return Fail("Octaves must be −1 through 9 (MIDI 0–127)", out error);
        var pitch = (octave + 1) * 12 + semitone;
        if (pitch is < 0 or > 127) return Fail("Note is outside the MIDI range 0–127", out error);
        note.Kind = NoteKind.Note;
        note.Pitch = pitch;
        return true;
    }

    public static bool TryParse(string? text, out NoteEvent note, out string error) => TryParse(text, 4, out note, out error);
    public static int GetOctave(NoteEvent note, int fallback = 4) => note.Kind == NoteKind.Note ? GetOctave(note.Pitch) : fallback;
    public static int GetOctave(int pitch) => pitch / 12 - 1;
    public static string Format(NoteEvent note) => note.Kind switch
    {
        NoteKind.Empty => "",
        NoteKind.Off => "OFF",
        NoteKind.Cut => "CUT",
        NoteKind.Note when note.Pitch is >= 0 and <= 127 => Names[note.Pitch % 12] + GetOctave(note.Pitch).ToString(CultureInfo.InvariantCulture) +
            (note.Timing switch { NoteTiming.TripletEighth => "T", NoteTiming.TripletSixteenth => "TT", NoteTiming.Swing => " S", _ => "" }),
        _ => throw new ArgumentException("Invalid note", nameof(note))
    };
    private static bool Fail(string message, out string error) { error = message; return false; }
}

public readonly record struct TrackerEffect(char Code, byte Value)
{
    public bool IsEmpty => Code == '\0';
    public int HighNibble => Value >> 4;
    public int LowNibble => Value & 15;
    public override string ToString() => IsEmpty ? "" : $"{Code}{Value:X2}";
}

public static class FxParser
{
    public const int MaxColumns = 16;
    public static bool TryParse(string? text, out TrackerEffect effect, out string error)
    {
        effect = default;
        error = "";
        var input = (text ?? "").Trim().ToUpperInvariant();
        if (input.Length == 0 || input == "...") return true;
        if (input.Length != 3 || FxCatalog.Find(input[0]) is not { } definition || !byte.TryParse(input.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        { error = FxCatalog.SyntaxHelp; return false; }
        if (value > definition.MaximumValue)
        { error = definition.RangeError; return false; }
        effect = new(input[0], value);
        return true;
    }
    public static void Validate(IReadOnlyList<string> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        if (effects.Count > MaxColumns) throw new SongFormatException($"At most {MaxColumns} FX columns are allowed");
        var commands = new HashSet<char>();
        foreach (var text in effects)
        {
            if (text is null) throw new SongFormatException("FX columns cannot be null");
            if (!TryParse(text, out var effect, out var error)) throw new SongFormatException(error);
            if (!effect.IsEmpty && !commands.Add(effect.Code)) throw new SongFormatException($"Duplicate FX command {effect.Code}");
        }
        if (commands.Contains('U') && commands.Contains('D')) throw new SongFormatException("Up and down pitch slides cannot share a row");
    }
    public static string Format(TrackerEffect effect) => effect.ToString();
}
