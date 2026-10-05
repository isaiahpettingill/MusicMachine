using System.Globalization;

namespace MusicMachine.Core;

public sealed record FxParameter(string Name, int Minimum, int Maximum, int DefaultValue, string Help);
public sealed record FxExample(string Code, string Description);

/// <summary>One supported row effect, shared by validation and the editor's reference.</summary>
public sealed class FxDefinition
{
    public char Command { get; }
    public string Syntax { get; }
    public string Name { get; }
    public string Description { get; }
    public int MaximumValue { get; }
    public string RangeError { get; }
    public IReadOnlyList<FxParameter> Parameters { get; }
    public IReadOnlyList<FxExample> Examples { get; }

    internal FxDefinition(char command, string syntax, string name, string description, int maximumValue,
        FxParameter[] parameters, FxExample[] examples, string? rangeError = null)
    {
        Command = command; Syntax = syntax; Name = name; Description = description; MaximumValue = maximumValue;
        Parameters = Array.AsReadOnly(parameters); Examples = Array.AsReadOnly(examples);
        RangeError = rangeError ?? $"{name} must be {command}00–{command}{maximumValue:X2} (0–{maximumValue})";
    }

    /// <summary>Decimal parameter values become the canonical tracker code; no hex arithmetic in the UI.</summary>
    public string Format(params int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != Parameters.Count) throw new ArgumentException($"{Name} needs {Parameters.Count} parameter(s)", nameof(values));
        for (var i = 0; i < values.Length; i++)
            if (values[i] < Parameters[i].Minimum || values[i] > Parameters[i].Maximum)
                throw new ArgumentOutOfRangeException(nameof(values), $"{Parameters[i].Name} must be {Parameters[i].Minimum}–{Parameters[i].Maximum}");
        var value = Parameters.Count == 2 ? (values[0] << 4) | values[1] : values[0];
        return new TrackerEffect(Command, (byte)value).ToString();
    }

    public int[] GetParameters(byte value)
    {
        if (value > MaximumValue) throw new ArgumentOutOfRangeException(nameof(value));
        return Parameters.Count == 2 ? [value >> 4, value & 15] : [value];
    }

    /// <summary>Explains the exact value, including persistence and special zero/one cases.</summary>
    public string DescribeValue(byte value)
    {
        if (value > MaximumValue) throw new ArgumentOutOfRangeException(nameof(value));
        var percentage = (value * 100d / 255).ToString("0.#", CultureInfo.InvariantCulture);
        return Command switch
        {
            'A' => $"Root → +{value >> 4} → +{value & 15} semitones, in three equal parts of this row",
            'V' when value == 0 => "Silent channel until another V command",
            'V' => $"{percentage}% channel volume; persists until another V command",
            'G' when value == 0 => "Release immediately at the row trigger; the envelope's release tail can still sound",
            'G' => $"Release the held note after {percentage}% of this row; the envelope's release tail can still sound",
            'U' or 'D' when value == 0 => "No pitch slide during this row",
            'U' => $"Slide up {value} semitones per second during this row",
            'D' => $"Slide down {value} semitones per second during this row",
            'R' when value == 0 => "No row retriggers; a note's T / TT triplet timing still applies",
            'R' when value == 1 => "One ordinary note trigger; no extra row retriggers",
            'R' => $"{value} equal subdivisions of this row; retriggers only while the note is held",
            _ => throw new InvalidOperationException("Unknown effect definition")
        };
    }
}

/// <summary>
/// The supported FX grammar and reference. Semantics follow SynthRenderer.Channel.Apply/Next:
/// V persists, G releases the held envelope, and A/U/D/R act within the row.
/// Adding a command requires both a definition here and an engine implementation.
/// </summary>
public static class FxCatalog
{
    public static IReadOnlyList<FxDefinition> All { get; } = Array.AsReadOnly<FxDefinition>([
        new('A', "Axy", "Arpeggio", "Cycle the note's root pitch, then two semitone offsets, in three equal parts of this row. Each offset is 0–15 semitones.", 255,
            [new("First offset", 0, 15, 3, "Semitones above the root (0–15), played in the second third of the row"),
             new("Second offset", 0, 15, 7, "Semitones above the root (0–15), played in the final third of the row")],
            [new("A37", "Minor chord: root, +3, +7"), new("A47", "Major chord: root, +4, +7"), new("A0C", "Octave: root, root, +12")]),
        new('V', "Vxx", "Channel volume", "Set a linear channel volume multiplier. This persists across rows until another V command; 255 is full volume and 0 is silence.", 255,
            [new("Volume", 0, 255, 192, "0–255: the multiplier is this value divided by 255; 255 = 100%")],
            [new("VC0", "About 75% volume"), new("V80", "About 50% volume"), new("VFF", "Full volume"), new("V00", "Silence")]),
        new('G', "Gxx", "Note gate", "Release a new or already-held note after a fraction of this row (value ÷ 255). The envelope's release tail can still sound. A gate also ends retriggering when it releases the note.", 255,
            [new("Row fraction", 0, 255, 128, "0–255: 128 is about half a row; 255 is the end of the row")],
            [new("G80", "Release halfway through"), new("G40", "Release after a quarter"), new("GFF", "Release at row end"), new("G00", "Release immediately")]),
        new('U', "Uxx", "Pitch slide up", "Raise pitch at a rate of 0–255 semitones per second during this row. The reached pitch stays until a new note or retrigger resets it. Up and down slides cannot share a row.", 255,
            [new("Semitones / second", 0, 255, 2, "Pitch change per second, not per row; 0 applies no slide")],
            [new("U02", "Gentle: 2 semitones / sec"), new("U0C", "12 semitones / sec"), new("U00", "No upward slide")]),
        new('D', "Dxx", "Pitch slide down", "Lower pitch at a rate of 0–255 semitones per second during this row. The reached pitch stays until a new note or retrigger resets it. Up and down slides cannot share a row.", 255,
            [new("Semitones / second", 0, 255, 2, "Pitch change per second, not per row; 0 applies no slide")],
            [new("D02", "Gentle: 2 semitones / sec"), new("D0C", "12 semitones / sec"), new("D00", "No downward slide")]),
        new('R', "Rxx", "Retrigger", "Divide this row into 0–32 equal subdivisions, restarting the held note's envelope. 0 disables row retriggers; 1 adds no extra triggers. T / TT note timing is independent.", 32,
            [new("Subdivisions", 0, 32, 4, "Decimal 0–32; for example, 16 becomes R10 and 32 becomes R20")],
            [new("R04", "4 subdivisions"), new("R08", "8 subdivisions"), new("R20", "32 subdivisions"), new("R00", "No row retriggers")],
            "Retrigger count must be R00–R20 (0–32; 00 disables)")
    ]);

    public static string SyntaxHelp { get; } = "Use " + string.Join(", ", All.Take(All.Count - 1).Select(e => e.Syntax)) +
        " or " + All[^1].Syntax + " with two hexadecimal digits";

    public static FxDefinition? Find(char command) => All.FirstOrDefault(e => e.Command == char.ToUpperInvariant(command));

    /// <summary>Case-insensitive words, command templates and actual valid codes all find their effect.</summary>
    public static IReadOnlyList<FxDefinition> Search(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return All;
        if (text.Length == 1 && Find(text[0]) is { } command) return [command];
        if (FxParser.TryParse(text, out var parsed, out _) && !parsed.IsEmpty) return [Find(parsed.Code)!];
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Prefer friendly-name matches so "slide down" does not also match the up-slide's conflict warning.
        var names = All.Where(effect => words.All(word =>
            $"{effect.Syntax} {effect.Name}".Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (names.Length > 0) return names;
        return All.Where(effect =>
        {
            var searchable = $"{effect.Syntax} {effect.Name} {effect.Description} " +
                string.Join(" ", effect.Parameters.Select(p => p.Name)) + " " +
                string.Join(" ", effect.Examples.Select(example => $"{example.Code} {example.Description}"));
            return words.All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }
}
