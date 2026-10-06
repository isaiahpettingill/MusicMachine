using MusicMachine.Core;
namespace MusicMachine.Audio;

public static class InstrumentAudition
{
    public static Song CreateSong(Instrument instrument)
    {
        var copy = InstrumentFile.Clone(instrument);
        // Give slow envelopes time to reach their peak before releasing the note.
        var holdSeconds = Math.Max(.5, (copy.Amplitude.AttackMs + copy.Amplitude.DecayMs) / 1000 + .25);
        var holdRows = (int)Math.Ceiling(holdSeconds / .125);
        var track = new Track { Name = "Audition", InstrumentId = copy.Id };
        var pattern = new Pattern { Length = holdRows + 1 };
        var notes = pattern.GetTrack(track.Id).Rows;
        notes[0] = new() { Kind = NoteKind.Note, Pitch = 60, InstrumentId = copy.Id };
        notes[holdRows] = new() { Kind = NoteKind.Off };
        var song = new Song
        {
            Instruments = [copy], Tracks = [track], Patterns = [pattern],
            Arrangement = [new() { PatternId = pattern.Id }]
        };
        SongFile.Validate(song); return song;
    }
}
