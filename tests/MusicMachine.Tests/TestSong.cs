using MusicMachine.Core;
namespace MusicMachine.Tests;
internal static class TestSong
{
    internal static Song CreateEmpty()
    {
        var song = DemoSong.Create(); song.Title = "Untitled song"; song.Author = "";
        song.Tracks.ForEach(t => t.VolumeAutomation.Clear());
        var pattern = song.Patterns[0]; pattern.Name = "Pattern 01";
        foreach (var track in pattern.Tracks) track.Rows = Enumerable.Range(0, pattern.Length).Select(_ => new NoteEvent()).ToList();
        foreach (var lane in pattern.Drums) lane.Steps = Enumerable.Repeat((byte)0, pattern.Length).ToList();
        song.Patterns = [pattern]; song.Arrangement = [new() { PatternId = pattern.Id }]; song.LoopStartSection = 0; song.LoopEndSection = 1;
        SongFile.Validate(song); return song;
    }
}
