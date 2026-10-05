using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

public sealed class FxCatalogTests
{
    [Fact]
    public void CatalogAndParserAcceptExactlyTheSameCommandsAndRanges()
    {
        Assert.Equal(new[] { 'A', 'V', 'G', 'U', 'D', 'R' }, FxCatalog.All.Select(effect => effect.Command));
        for (var command = 'A'; command <= 'Z'; command++)
        for (var value = 0; value <= 255; value++)
        {
            var definition = FxCatalog.Find(command);
            var expected = definition is not null && value <= definition.MaximumValue;
            Assert.Equal(expected, FxParser.TryParse($"{command}{value:X2}", out var parsed, out var error));
            if (!expected) { Assert.NotEmpty(error); continue; }
            Assert.Equal(command, parsed.Code); Assert.Equal(value, parsed.Value);
            Assert.Equal(parsed.ToString(), definition!.Format(definition.GetParameters(parsed.Value)));
            Assert.NotEmpty(definition.DescribeValue(parsed.Value));
        }
        Assert.Null(FxCatalog.Find('S'));
        Assert.Same(FxCatalog.Find('V'), FxCatalog.Find('v'));
        Assert.False(FxParser.TryParse("R21", out _, out var rangeError));
        Assert.Equal(FxCatalog.Find('R')!.RangeError, rangeError);
    }

    [Fact]
    public void EveryExampleIsCanonicalValidatedAndUsesTheExistingRow()
    {
        foreach (var definition in FxCatalog.All)
        {
            Assert.NotEmpty(definition.Name); Assert.NotEmpty(definition.Description); Assert.NotEmpty(definition.Examples);
            Assert.True(FxParser.TryParse(definition.Format(definition.Parameters.Select(parameter => parameter.DefaultValue).ToArray()), out _, out _));
            foreach (var example in definition.Examples)
            {
                Assert.True(FxParser.TryParse(example.Code, out var effect, out var error), error);
                Assert.Equal(definition.Command, effect.Code);
                Assert.Equal(example.Code, effect.ToString());
                FxParser.Validate([example.Code]);
                var song = DemoSong.CreateEmpty(); var before = new SynthRenderer(song);
                song.Patterns[0].Tracks[0].Rows[0] = new NoteEvent { Kind = NoteKind.Note, Pitch = 60, Effects = [example.Code] };
                SongFile.Validate(song);
                var after = new SynthRenderer(song);
                Assert.Equal(before.TotalRows, after.TotalRows); Assert.Equal(before.MusicalFrames, after.MusicalFrames);
                Assert.Equal(before.FramesPerRow, after.FramesPerRow);
            }
        }
    }

    [Theory]
    [InlineData('A', 3, 7, "A37")]
    [InlineData('A', 0, 12, "A0C")]
    [InlineData('A', 15, 15, "AFF")]
    public void ArpeggioFormatsTwoIndependentSemitoneOffsets(char command, int first, int second, string expected) =>
        Assert.Equal(expected, FxCatalog.Find(command)!.Format(first, second));

    [Theory]
    [InlineData('V', 192, "VC0")]
    [InlineData('V', 0, "V00")]
    [InlineData('G', 128, "G80")]
    [InlineData('U', 255, "UFF")]
    [InlineData('D', 12, "D0C")]
    [InlineData('R', 16, "R10")]
    [InlineData('R', 32, "R20")]
    public void DecimalParametersFormatWithoutHexKnowledge(char command, int value, string expected) =>
        Assert.Equal(expected, FxCatalog.Find(command)!.Format(value));

    [Fact]
    public void ParameterBuildersRejectWrongCountsAndOutOfRangeValues()
    {
        foreach (var definition in FxCatalog.All)
        {
            Assert.Throws<ArgumentException>(() => definition.Format());
            Assert.Throws<ArgumentException>(() => definition.Format(0, 0, 0));
            for (var index = 0; index < definition.Parameters.Count; index++)
            {
                var values = definition.Parameters.Select(parameter => parameter.DefaultValue).ToArray();
                values[index] = -1; Assert.Throws<ArgumentOutOfRangeException>(() => definition.Format(values));
                values[index] = definition.Parameters[index].Maximum + 1;
                Assert.Throws<ArgumentOutOfRangeException>(() => definition.Format(values));
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => FxCatalog.Find('R')!.GetParameters(33));
        Assert.Throws<ArgumentOutOfRangeException>(() => FxCatalog.Find('R')!.DescribeValue(33));
    }

    [Theory]
    [InlineData("volume", "V")]
    [InlineData("  PITCH down ", "D")]
    [InlineData("Axy", "A")]
    [InlineData("GfE", "G")]
    [InlineData("r10", "R")]
    [InlineData("minor chord", "A")]
    [InlineData("semitones per second", "UD")]
    [InlineData("slide", "UD")]
    [InlineData("r", "R")]
    [InlineData("not a supported effect", "")]
    [InlineData("Sxx", "")]
    public void SearchFindsFriendlyNamesParametersAndActualCodes(string query, string commands) =>
        Assert.Equal(commands, new string(FxCatalog.Search(query).Select(effect => effect.Command).ToArray()));

    [Fact]
    public void BlankSearchShowsAllAndValueDescriptionsExplainSpecialCases()
    {
        Assert.Same(FxCatalog.All, FxCatalog.Search(null)); Assert.Same(FxCatalog.All, FxCatalog.Search(" \t "));
        Assert.Contains("50.2%", FxCatalog.Find('V')!.DescribeValue(128));
        Assert.Contains("persists", FxCatalog.Find('V')!.DescribeValue(255));
        Assert.Contains("release tail", FxCatalog.Find('G')!.DescribeValue(0));
        Assert.Contains("T / TT", FxCatalog.Find('R')!.DescribeValue(0));
        Assert.Contains("no extra", FxCatalog.Find('R')!.DescribeValue(1));
        Assert.Contains("32 equal", FxCatalog.Find('R')!.DescribeValue(32));
        Assert.Contains("No pitch slide", FxCatalog.Find('U')!.DescribeValue(0));
        Assert.Contains("+12", FxCatalog.Find('A')!.DescribeValue(12));
        Assert.Throws<SongFormatException>(() => FxParser.Validate(["U00", "D00"]));
    }

    [Theory]
    [InlineData(NoteTiming.TripletEighth)]
    [InlineData(NoteTiming.TripletSixteenth)]
    public void ZeroRowRetriggersLeaveTripletNoteTimingIntact(NoteTiming timing)
    {
        var song = DemoSong.CreateEmpty();
        var note = song.Patterns[0].Tracks[0].Rows[0];
        note.Kind = NoteKind.Note; note.Pitch = 60; note.Timing = timing;
        var expected = OfflineExporter.Render(song);
        note.Effects = ["R00"];
        Assert.Equal(expected, OfflineExporter.Render(song));
    }

    [Fact]
    public void GateReleasesAnAlreadyHeldNoteAndKeepsItsEnvelopeTail()
    {
        var song = DemoSong.CreateEmpty();
        var instrument = song.FindInstrument(song.Tracks[0].InstrumentId)!;
        instrument.Waveform = Waveform.Sine;
        instrument.Amplitude = new Envelope { AttackMs = 0, DecayMs = 0, Sustain = 1, ReleaseMs = 80 };
        var rows = song.Patterns[0].Tracks[0].Rows;
        rows[0].Kind = NoteKind.Note; rows[0].Pitch = 69;
        rows[1].Effects = [FxCatalog.Find('G')!.Format(0)];
        var rowFrame = (int)Math.Round(new SynthRenderer(song).FramesPerRow);
        var gated = OfflineExporter.Render(song);
        Assert.Contains(gated.Skip((rowFrame + 100) * 2).Take(1000), sample => Math.Abs(sample) > .0001f);
        Assert.All(gated.Skip((rowFrame + 4800) * 2).Take(1000), sample => Assert.Equal(0f, sample));
        rows[1].Kind = NoteKind.Cut; rows[1].Effects.Clear();
        var cut = OfflineExporter.Render(song);
        Assert.All(cut.Skip((rowFrame + 100) * 2).Take(1000), sample => Assert.Equal(0f, sample));
    }
}
