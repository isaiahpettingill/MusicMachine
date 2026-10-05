namespace MusicMachine.Core;

/// <summary>Fresh factory instances; callers own their copies. Songs always embed their instruments.</summary>
public static class InstrumentLibrary
{
    public static List<Instrument> CreatePresets()
    {
        var glass = Enumerable.Range(0, 64).Select(n => (short)(Math.Clamp(Math.Sin(n * Math.Tau / 64) + .25 * Math.Sin(n * Math.Tau * 3 / 64), -1, 1) * 28000)).ToArray();
        var sine = Enumerable.Range(0, 64).Select(n => (short)(Math.Sin(n * Math.Tau / 64) * 30000)).ToArray();
        var prism = Enumerable.Range(0, 64).Select(n => (short)((Math.Sin(n * Math.Tau / 64) + .3 * Math.Sin(n * Math.Tau * 2 / 64) + .12 * Math.Sin(n * Math.Tau * 5 / 64)) / 1.42 * 30000)).ToArray();
        return
        [
            new() { Id = "factory-pixel-lead", Name = "Pixel lead", Waveform = Waveform.Pulse, PulseWidth = .27, VolumeDb = -16, FilterCutoff = 7800, FilterResonance = .15, Amplitude = new() { AttackMs = 3, DecayMs = 95, Sustain = .58, ReleaseMs = 58 } },
            new() { Id = "factory-soft-bass", Name = "Soft triangle bass", Waveform = Waveform.Triangle, VolumeDb = -12, FilterCutoff = 1900, Amplitude = new() { AttackMs = 2, DecayMs = 95, Sustain = .68, ReleaseMs = 35 } },
            new() { Id = "factory-glass-pluck", Name = "Glass pluck", Waveform = Waveform.Custom, CustomWave = glass, VolumeDb = -20, FilterCutoff = 9500, Amplitude = new() { AttackMs = 1, DecayMs = 110, Sustain = .12, ReleaseMs = 70 } },
            new() { Id = "factory-prism-pad", Name = "Prism wavetable", Waveform = Waveform.Wavetable, Wavetable = [sine, prism, glass.ToArray()], WavetablePosition = .58, DetuneCents = -7, Phase = .2, VolumeDb = -24, FilterCutoff = 3200, Amplitude = new() { AttackMs = 80, DecayMs = 260, Sustain = .75, ReleaseMs = 240 } },
            new() { Id = "factory-round-kick", Name = "Round kick", Drum = DrumKind.Kick, Waveform = Waveform.Sine, VolumeDb = -9, PitchEnvelopeSemitones = 30, PitchEnvelopeMs = 42, FilterCutoff = 4800, Amplitude = new() { AttackMs = 0, DecayMs = 160, Sustain = 0, ReleaseMs = 30 } },
            new() { Id = "factory-clap-snare", Name = "Clap snare", Drum = DrumKind.Snare, Waveform = Waveform.Noise, VolumeDb = -17, PitchEnvelopeSemitones = 7, PitchEnvelopeMs = 32, FilterCutoff = 8400, Amplitude = new() { AttackMs = 0, DecayMs = 110, Sustain = 0, ReleaseMs = 32 } },
            new() { Id = "factory-tick-hat", Name = "Tick hat", Drum = DrumKind.ClosedHat, Waveform = Waveform.Noise, VolumeDb = -24, FilterCutoff = 14500, Amplitude = new() { AttackMs = 0, DecayMs = 30, Sustain = 0, ReleaseMs = 10 } },
            new() { Id = "factory-air-hat", Name = "Air hat", Drum = DrumKind.OpenHat, Waveform = Waveform.Noise, VolumeDb = -26, FilterCutoff = 12500, Amplitude = new() { AttackMs = 0, DecayMs = 130, Sustain = 0, ReleaseMs = 25 } },
            new() { Id = "factory-warm-saw", Name = "Warm saw", Waveform = Waveform.Saw, VolumeDb = -20, FilterCutoff = 2100, FilterResonance = .3, Amplitude = new() { AttackMs = 12, DecayMs = 210, Sustain = .45, ReleaseMs = 140 } },
            new() { Id = "factory-square-bell", Name = "Square bell", Waveform = Waveform.Square, VolumeDb = -23, FilterCutoff = 4900, Amplitude = new() { AttackMs = 1, DecayMs = 250, Sustain = .06, ReleaseMs = 190 } }
        ];
    }
    public static Instrument CreateLocalCopy(Instrument source, string? name = null)
    {
        var copy = InstrumentFile.Clone(source);
        copy.Id = Guid.NewGuid().ToString("N"); copy.IsLocal = true;
        if (name is not null) copy.Name = name;
        InstrumentFile.Validate(copy); return copy;
    }
    public static Instrument ImportToSong(Song song, Instrument source)
    { var copy = CreateLocalCopy(source); song.Instruments.Add(copy); return copy; }
}

public static class DemoSong
{
    public static Song Create()
    {
        var presets = InstrumentLibrary.CreatePresets().Take(8).ToList();
        foreach (var i in presets) { i.Id = i.Id.Replace("factory-", "neon-", StringComparison.Ordinal); i.IsLocal = true; }
        var song = new Song
        {
            Title = "Neon Orchard", Author = "MusicMachine", Bpm = 128, RowsPerBeat = 4, BeatsPerBar = 4,
            Swing = .14, MasterVolumeDb = -1, Seed = 0x4E454F4E, Instruments = presets,
            Tracks =
            [
                new() { Id = "lead", Name = "Pixel melody", InstrumentId = presets[0].Id, Pan = -.12, VolumeDb = -1, Color = "#F3B5D8" },
                new() { Id = "bass", Name = "Triangle bass", InstrumentId = presets[1].Id, VolumeDb = -1, Color = "#C9B5FB" },
                new() { Id = "arp", Name = "Glass arpeggio", InstrumentId = presets[2].Id, Pan = .3, VolumeDb = -2, Color = "#8ED9D2" },
                new() { Id = "pad", Name = "Prism chords", InstrumentId = presets[3].Id, Pan = -.25, VolumeDb = -3, Color = "#AFC9F7" }
            ]
        };
        song.Tracks[0].VolumeAutomation = [new() { Row = 0, Decibels = -2 }, new() { Row = 64, Decibels = 0 }, new() { Row = 192, Decibels = 1 }, new() { Row = 255, Decibels = -1 }];
        string[] names = ["A · Morning pixels", "B · Orchard lights", "C · Glass canopy", "D · Homeward stars"];
        int[] roots = [40, 36, 43, 38];
        int[][] chords = [[64, 67, 71, 74], [60, 64, 67, 71], [62, 67, 71, 74], [62, 66, 69, 74]];
        int[][] melodies =
        [
            [76, 79, 83, 81, 79, 76, 74, 76, 79, 78, 76],
            [76, 79, 84, 83, 79, 76, 74, 72, 76, 79, 76],
            [79, 83, 86, 83, 81, 79, 78, 74, 79, 81, 83],
            [78, 81, 86, 84, 81, 78, 76, 74, 78, 74, 71]
        ];
        int[] leadRows = [0, 3, 6, 10, 12, 16, 19, 22, 24, 27, 30];
        for (var p = 0; p < 4; p++)
        {
            var pattern = new Pattern { Id = $"neon-pattern-{p + 1}", Name = names[p], Length = 32 };
            foreach (var track in song.Tracks) pattern.GetTrack(track.Id);
            var lead = pattern.GetTrack("lead").Rows;
            for (var n = 0; n < leadRows.Length; n++) lead[leadRows[n]] = Note(melodies[p][n], n is 2 or 8 ? "VFF" : "VCB", n is 3 or 10 ? "GCF" : "GBA");
            lead[0].InstrumentId = presets[0].Id;
            foreach (var row in new[] { 3, 19, 27 }) lead[row].Timing = NoteTiming.Swing;
            if (p == 3) { lead[30].Timing = NoteTiming.TripletEighth; lead[30].Effects = ["VA0"]; }
            if (p == 2) lead[27].Effects.Add("D03");
            var bass = pattern.GetTrack("bass").Rows;
            int[] bassRows = [0, 4, 7, 10, 12, 16, 20, 23, 26, 28, 30];
            for (var n = 0; n < bassRows.Length; n++) bass[bassRows[n]] = Note(roots[p] + (n is 2 or 7 ? 12 : n is 4 or 9 ? 7 : 0), "VD0", n is 2 or 7 ? "G70" : "GDC");
            bass[0].InstrumentId = presets[1].Id;
            var arp = pattern.GetTrack("arp").Rows;
            for (var row = 0; row < 32; row += 2)
            {
                arp[row] = Note(chords[p][(row / 2 + (row >= 16 ? 1 : 0)) % 4] + 12, row % 8 == 0 ? "VB8" : "V85", "G90");
            }
            arp[0].InstrumentId = presets[2].Id;
            if (p == 3) { arp[28].Timing = NoteTiming.TripletSixteenth; arp[28].Effects = ["V70"]; arp[30].Effects.Add("R03"); }
            var pad = pattern.GetTrack("pad").Rows;
            for (var row = 0; row < 31; row++) pad[row].Effects = [p == 0 ? "A37" : "A47"];
            pad[0] = Note(roots[p] + 24, p == 0 ? "A37" : "A47", "V90"); pad[0].InstrumentId = presets[3].Id;
            pad[16] = Note(roots[p] + 24, p == 0 ? "A37" : "A47", "VAA"); pad[31] = new() { Kind = NoteKind.Off };
            var kick = Lane("Round kick", presets[4].Id, 0);
            foreach (var row in new[] { 0, 8, 14, 16, 24, 27 }) kick.Steps[row] = (byte)(row is 14 or 27 ? 165 : 238);
            var snare = Lane("Clap snare", presets[5].Id, .1);
            foreach (var row in new[] { 4, 12, 20, 28 }) snare.Steps[row] = 218;
            if (p is 1 or 3) { snare.Steps[26] = 60; snare.Steps[30] = 120; snare.Steps[31] = 75; }
            var hat = Lane("Tick hat", presets[6].Id, -.25);
            for (var row = 0; row < 32; row += 2) hat.Steps[row] = (byte)(row % 8 == 0 ? 185 : row % 4 == 2 ? 105 : 140);
            if (p == 3) { hat.Steps[29] = 95; hat.Steps[31] = 115; }
            var air = Lane("Air hat", presets[7].Id, .35); air.Steps[6] = 118; air.Steps[22] = 138;
            pattern.Drums = [kick, snare, hat, air]; song.Patterns.Add(pattern);
        }
        foreach (var p in new[] { 0, 1, 0, 2, 0, 1, 3, 3 }) song.Arrangement.Add(new() { PatternId = song.Patterns[p].Id });
        song.LoopEndSection = song.Arrangement.Count;
        SongFile.Validate(song); return song;
    }
    public static Song CreateEmpty()
    {
        var song = Create(); song.Title = "Untitled song"; song.Author = "";
        song.Tracks.ForEach(t => t.VolumeAutomation.Clear());
        var pattern = song.Patterns[0]; pattern.Name = "Pattern 01";
        foreach (var track in pattern.Tracks) track.Rows = Enumerable.Range(0, pattern.Length).Select(_ => new NoteEvent()).ToList();
        foreach (var lane in pattern.Drums) lane.Steps = Enumerable.Repeat((byte)0, pattern.Length).ToList();
        song.Patterns = [pattern]; song.Arrangement = [new() { PatternId = pattern.Id }]; song.LoopStartSection = 0; song.LoopEndSection = 1;
        SongFile.Validate(song); return song;
    }
    private static NoteEvent Note(int pitch, params string[] fx) => new() { Kind = NoteKind.Note, Pitch = pitch, Effects = [.. fx] };
    private static DrumLane Lane(string name, string instrument, double pan) => new() { Name = name, InstrumentId = instrument, Pan = pan, Steps = Enumerable.Repeat((byte)0, 32).ToList() };
}
