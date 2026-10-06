using MusicMachine.App;
using MusicMachine.Core;
namespace MusicMachine.Tests;
public sealed class InstrumentHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavesIndependentCopiesAndUpdatesTheSameInstrument(bool browser)
    {
        var storage = new MemoryProjectStorage(browser);
        var history = new InstrumentHistory(storage);
        var instrument = new Instrument { Name = "Bass" };
        await history.SaveAsync(instrument);
        instrument.Name = "Edited bass";
        Assert.Equal("Bass", Assert.Single(await new InstrumentHistory(storage).LoadAsync()).Name);
        await history.SaveAsync(instrument);
        var saved = Assert.Single(await history.LoadAsync());
        Assert.Equal("Edited bass", saved.Name);
        Assert.Equal(instrument.Id, InstrumentFile.Read(InstrumentFile.Write(saved)).Id);
        await history.SaveAsync(new Instrument { Name = "Drum" });
        Assert.Equal(2, (await history.LoadAsync()).Count);
    }
    [Fact]
    public async Task DesktopHistorySurvivesReopeningStorage()
    {
        using var directory = new StartupDirectory();
        await new InstrumentHistory(new DesktopProjectStorage(directory.Path)).SaveAsync(new Instrument { Name = "Lead" });
        var saved = await new InstrumentHistory(new DesktopProjectStorage(directory.Path)).LoadAsync();
        Assert.Equal("Lead", Assert.Single(saved).Name);
    }
    [Fact]
    public void BlankSongHasNoSoundsAndExportsSilence()
    {
        var song = DemoSong.CreateEmpty();
        StartupTests.AssertBlank(song);
        song.Patterns[0].Tracks[0].Rows[0].Kind = NoteKind.Note;
        var renderer = new MusicMachine.Audio.SynthRenderer(song);
        var buffer = new float[4096]; renderer.Render(buffer);
        Assert.All(buffer, sample => Assert.Equal(0, sample));
        StartupTests.AssertBlank(SongFile.Read(SongFile.Write(DemoSong.CreateEmpty())));
    }
}
