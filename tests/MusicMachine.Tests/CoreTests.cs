using System.Formats.Cbor;
using MusicMachine.Core;

namespace MusicMachine.Tests;

public class NoteParserTests
{
    [Theory]
    [InlineData("F", 4, 65, NoteTiming.Straight)]
    [InlineData("F#", 5, 78, NoteTiming.Straight)]
    [InlineData("f#4", 2, 66, NoteTiming.Straight)]
    [InlineData("F#4T", 4, 66, NoteTiming.TripletEighth)]
    [InlineData("F#4TT", 4, 66, NoteTiming.TripletSixteenth)]
    [InlineData("F#4 S", 4, 66, NoteTiming.Swing)]
    [InlineData("C-1", 4, 0, NoteTiming.Straight)]
    [InlineData("G9", 4, 127, NoteTiming.Straight)]
    [InlineData("C", 99, 60, NoteTiming.Straight)]
    public void NotesParseWithInheritedOctaves(string text, int previous, int pitch, NoteTiming timing)
    {
        Assert.True(NoteParser.TryParse(text, previous, out var note, out var error), error);
        Assert.Equal(NoteKind.Note, note.Kind); Assert.Equal(pitch, note.Pitch); Assert.Equal(timing, note.Timing);
        Assert.True(NoteParser.TryParse(NoteParser.Format(note), 4, out var again, out _)); Assert.Equal(pitch, again.Pitch); Assert.Equal(timing, again.Timing);
    }
    [Theory][InlineData("", NoteKind.Empty)][InlineData("...", NoteKind.Empty)][InlineData("OFF", NoteKind.Off)][InlineData("cut", NoteKind.Cut)]
    public void SpecialEvents(string text, NoteKind kind) { Assert.True(NoteParser.TryParse(text, 4, out var note, out _)); Assert.Equal(kind, note.Kind); Assert.Equal(4, NoteParser.GetOctave(note)); }
    [Theory][InlineData("H4")][InlineData("C10")][InlineData("G#9")][InlineData("C-2")][InlineData("F##4")][InlineData("F4TS")][InlineData("F4TTT")][InlineData("OFFT")][InlineData("4F")][InlineData("F 4")][InlineData("F# 4")][InlineData("F4x")]
    public void InvalidNotesExplainError(string text) { Assert.False(NoteParser.TryParse(text, 4, out _, out var error)); Assert.NotEmpty(error); }
    [Fact] public void OctavesFollowMidiConvention() { Assert.Equal(4, NoteParser.GetOctave(60)); Assert.Equal(-1, NoteParser.GetOctave(0)); }
}

public class FxTests
{
    [Theory][InlineData("A37", 'A', 0x37)][InlineData("vff", 'V', 255)][InlineData("G00", 'G', 0)][InlineData("U02", 'U', 2)][InlineData("DFF", 'D', 255)][InlineData("R20", 'R', 32)][InlineData("R00", 'R', 0)]
    public void ParsesHexCommands(string input, char code, int value) { Assert.True(FxParser.TryParse(input, out var fx, out _)); Assert.Equal(code, fx.Code); Assert.Equal(value, fx.Value); }
    [Theory][InlineData("X10")][InlineData("VGG")][InlineData("R21")][InlineData("V100")][InlineData("A1")]
    public void InvalidEffectsRejected(string input) => Assert.False(FxParser.TryParse(input, out _, out _));
    [Fact] public void DuplicateAndConflictingEffectsRejected() { Assert.Throws<SongFormatException>(() => FxParser.Validate(["V11", "v22"])); Assert.Throws<SongFormatException>(() => FxParser.Validate(["U01", "D01"])); FxParser.Validate(["A37", "VCC", "GFF", "", "R03"]); }
    [Fact] public void VariableEffectsColumnsAreBounded() => Assert.Throws<SongFormatException>(() => FxParser.Validate(Enumerable.Repeat("", 17).ToList()));
}

public class SongFileTests
{
    [Fact] public void UnicodeAndAllDataRoundTripDeterministically()
    {
        var song = DemoSong.Create(); song.Title = "Neon 果樹園 🎵 – naïve"; song.Author = "Zoë";
        var bytes = SongFile.Write(song); var decoded = SongFile.Read(bytes);
        Assert.Equal(song.Title, decoded.Title); Assert.Equal(bytes, SongFile.Write(decoded));
        Assert.Equal(song.Instruments[3].Wavetable[1], decoded.Instruments[3].Wavetable[1]);
        Assert.Equal(song.Tracks[0].VolumeAutomation[2].Decibels, decoded.Tracks[0].VolumeAutomation[2].Decibels);
        Assert.NotEqual((byte)'{', bytes[0]); Assert.NotEqual((byte)'P', bytes[0]);
    }
    [Fact] public void InstrumentBinaryRoundTripsAndCopiesAreIndependent()
    {
        var original = InstrumentLibrary.CreatePresets().First(i => i.Waveform == Waveform.Wavetable);
        var data = InstrumentFile.Write(original); var loaded = InstrumentFile.Read(data);
        Assert.Equal(data, InstrumentFile.Write(loaded));
        var copy = InstrumentLibrary.CreateLocalCopy(original); Assert.NotEqual(original.Id, copy.Id); Assert.True(copy.IsLocal);
        copy.Wavetable[0][0] = 12345; copy.Amplitude.AttackMs = 12;
        Assert.NotEqual(copy.Wavetable[0][0], original.Wavetable[0][0]); Assert.NotEqual(copy.Amplitude.AttackMs, original.Amplitude.AttackMs);
    }
    [Fact] public void SongIsSelfContainedAndPresetFactoriesReturnIndependentData()
    {
        var song = DemoSong.Create(); var clone = SongFile.Clone(song); song.Instruments[0].Name = "Changed";
        Assert.NotEqual(song.Instruments[0].Name, clone.Instruments[0].Name);
        var a = InstrumentLibrary.CreatePresets(); var b = InstrumentLibrary.CreatePresets(); a[3].Wavetable[0][0] = 123;
        Assert.NotEqual(a[3].Wavetable[0][0], b[3].Wavetable[0][0]); SongFile.Validate(clone);
    }
    [Fact] public void EveryTruncationFailsGracefully()
    {
        var data = SongFile.Write(DemoSong.CreateEmpty());
        foreach (var length in new[] { 0, 1, 2, 15, data.Length / 2, data.Length - 1 }) Assert.Throws<SongFormatException>(() => SongFile.Read(data[..length]));
        Assert.Throws<SongFormatException>(() => SongFile.Read([.. data, 0]));
        Assert.Throws<SongFormatException>(() => SongFile.Read(new byte[SongLimits.MaxFileBytes + 1]));
    }
    [Fact] public void UnsupportedVersionAndWrongFileTypeRejected()
    {
        var bytes = RewriteSongRoot(1, w => w.WriteInt32(999));
        Assert.Contains("version", Assert.Throws<SongFormatException>(() => SongFile.Read(bytes)).Message);
        Assert.Throws<SongFormatException>(() => SongFile.Read(InstrumentFile.Write(InstrumentLibrary.CreatePresets()[0])));
    }
    [Fact] public void UnknownFutureFieldsAreSafelySkipped()
    {
        var expected = DemoSong.CreateEmpty(); var data = SongFile.Write(expected); var r = new CborReader(data); var w = new CborWriter();
        var count = r.ReadStartMap()!.Value; w.WriteStartMap(count + 1);
        for (var i = 0; i < count; i++) { w.WriteEncodedValue(r.ReadEncodedValue().Span); w.WriteEncodedValue(r.ReadEncodedValue().Span); }
        r.ReadEndMap(); w.WriteInt32(99); w.WriteStartMap(1); w.WriteTextString("future"); w.WriteStartArray(2); w.WriteInt32(123); w.WriteTextString("ignored"); w.WriteEndArray(); w.WriteEndMap(); w.WriteEndMap();
        Assert.Equal(data, SongFile.Write(SongFile.Read(w.Encode())));
    }
    [Fact] public void MaliciousDeclaredCountsAndDeepUnknownFieldsRejected()
    {
        Assert.Throws<SongFormatException>(() => SongFile.Read([0xBA, 0x7F, 0xFF, 0xFF, 0xFF]));
        var hugeTracks = RewriteSongRoot(14, w => { w.WriteStartArray(65); for (var i = 0; i < 65; i++) w.WriteNull(); w.WriteEndArray(); });
        Assert.Throws<SongFormatException>(() => SongFile.Read(hugeTracks));
        var deep = RewriteSongRoot(17, w => { for (var i = 0; i < 40; i++) w.WriteStartArray(1); w.WriteInt32(0); for (var i = 0; i < 40; i++) w.WriteEndArray(); });
        Assert.Throws<SongFormatException>(() => SongFile.Read(deep));
    }
    [Fact] public void DuplicateCborKeysRejected()
    {
        var writer = new CborWriter(CborConformanceMode.Lax); writer.WriteStartMap(2); writer.WriteInt32(0); writer.WriteTextString("MusicMachine.song"); writer.WriteInt32(0); writer.WriteTextString("MusicMachine.song"); writer.WriteEndMap();
        Assert.Throws<SongFormatException>(() => SongFile.Read(writer.Encode()));
    }
    [Fact] public void NonFiniteNumbersAndMissingReferencesRejected()
    {
        var song = DemoSong.CreateEmpty(); song.Bpm = double.NaN; Assert.Throws<SongFormatException>(() => SongFile.Write(song));
        song.Bpm = 120; song.Tracks[0].InstrumentId = "missing"; Assert.Throws<SongFormatException>(() => SongFile.Write(song));
        song = DemoSong.Create(); song.Patterns[0].Tracks[0].Rows.RemoveAt(0); Assert.Throws<SongFormatException>(() => SongFile.Write(song));
        song = DemoSong.Create(); song.Arrangement[0].PatternId = "missing"; Assert.Throws<SongFormatException>(() => SongFile.Write(song));
    }
    [Fact] public void InvalidEnumsFxWaveAndLoopBoundsRejected()
    {
        var song = DemoSong.Create(); song.Instruments[0].Waveform = (Waveform)99; Assert.Throws<SongFormatException>(() => SongFile.Write(song));
        song = DemoSong.Create(); song.Patterns[0].Tracks[0].Rows[0].Effects = ["QFF"]; Assert.Throws<SongFormatException>(() => SongFile.Write(song));
        song = DemoSong.Create(); song.LoopEndSection = 100; Assert.Throws<SongFormatException>(() => SongFile.Write(song));
        var instrument = new Instrument { Waveform = Waveform.Custom }; Assert.Throws<SongFormatException>(() => InstrumentFile.Write(instrument));
    }
    [Fact] public void FuzzedDocumentsNeverLeakParserImplementationExceptions()
    {
        var source = SongFile.Write(DemoSong.CreateEmpty()); var random = new Random(4309);
        for (var trial = 0; trial < 500; trial++)
        {
            var bytes = source.ToArray();
            for (var n = 0; n < 1 + trial % 4; n++) bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            try { SongFile.Validate(SongFile.Read(bytes)); }
            catch (SongFormatException) { }
        }
    }
    [Fact] public void GridDivisionsAndEmbeddedInstrumentRequirementAreExplicit()
    {
        var song = DemoSong.CreateEmpty(); song.RowsPerBeat = 3;
        Assert.Throws<SongFormatException>(() => SongFile.Validate(song)); song.RowsPerBeat = 16; SongFile.Validate(song);
        song.Instruments.Clear(); Assert.Throws<SongFormatException>(() => SongFile.Validate(song));
    }
    [Fact] public void AllStableWaveformAndDrumEnumsRoundTrip()
    {
        foreach (var wave in Enum.GetValues<Waveform>())
        foreach (var drum in Enum.GetValues<DrumKind>())
        {
            var i = new Instrument { Waveform = wave, Drum = drum, CustomWave = [-32768, 0, 32767], Wavetable = [[-32768, 32767]] };
            var restored = InstrumentFile.Read(InstrumentFile.Write(i)); Assert.Equal(wave, restored.Waveform); Assert.Equal(drum, restored.Drum);
            Assert.Equal(i.CustomWave, restored.CustomWave);
        }
    }
    [Theory][InlineData(2)][InlineData(4)][InlineData(8)][InlineData(16)]
    public void MeterDenominatorRoundTripsWithoutChangingQuarterNoteTempo(int beatUnit)
    {
        var song = DemoSong.Create(); song.BeatsPerBar = 7; song.BeatUnit = beatUnit;
        var clone = SongFile.Clone(song); Assert.Equal(7, clone.BeatsPerBar); Assert.Equal(beatUnit, clone.BeatUnit);
        Assert.Equal(song.Bpm, clone.Bpm); Assert.Equal(song.RowsPerBeat, clone.RowsPerBeat);
        var renderer = new MusicMachine.Audio.SynthRenderer(song);
        song.BeatUnit = 4; var commonTime = new MusicMachine.Audio.SynthRenderer(song);
        Assert.Equal(commonTime.MusicalFrames, renderer.MusicalFrames); Assert.Equal(commonTime.FramesPerRow, renderer.FramesPerRow);
        if (beatUnit == 8) Assert.Equal(3.5, clone.BeatsPerBar * 4d / clone.BeatUnit);
    }
    [Fact] public void LegacyVersionOneWithoutDenominatorDefaultsToQuarterNoteMeter()
    {
        var r = new CborReader(SongFile.Write(DemoSong.CreateEmpty())); var w = new CborWriter();
        var count = r.ReadStartMap()!.Value; w.WriteStartMap(count - 1);
        for (var index = 0; index < count; index++)
        {
            var key = r.ReadInt32(); if (key == 18) r.SkipValue();
            else { w.WriteInt32(key); w.WriteEncodedValue(r.ReadEncodedValue().Span); }
        }
        r.ReadEndMap(); w.WriteEndMap(); Assert.Equal(4, SongFile.Read(w.Encode()).BeatUnit);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(3)][InlineData(32)]
    public void UnsupportedMeterDenominatorsAreRejected(int beatUnit)
    {
        var song = DemoSong.CreateEmpty(); song.BeatUnit = beatUnit;
        Assert.Throws<SongFormatException>(() => SongFile.Validate(song));
        Assert.Throws<SongFormatException>(() => SongFile.Read(RewriteSongRoot(18, w => w.WriteInt32(beatUnit))));
    }
    [Fact] public void AtomicSaveReplacesAndLeavesNoTemporaryFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MusicMachine-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { var path = Path.Combine(dir, "test.song"); var song = DemoSong.Create(); SongFile.Save(path, song); song.Title = "Second"; SongFile.Save(path, song); Assert.Equal("Second", SongFile.Load(path).Title); Assert.Single(Directory.GetFiles(dir)); }
        finally { Directory.Delete(dir, true); }
    }
    [Fact] public void DemoHasFourPatternsReuseAndThirtySecondLoop()
    {
        var song = DemoSong.Create(); SongFile.Validate(song); Assert.Equal(4, song.Tracks.Count); Assert.Equal(4, song.Patterns.Count);
        Assert.True(song.Arrangement.Count > song.Patterns.Count); Assert.All(song.Patterns, p => Assert.True(p.Drums.Count >= 3));
        var seconds = song.Arrangement.Sum(s => song.FindPattern(s.PatternId)!.Length * s.Repeats) * 60d / song.Bpm / song.RowsPerBeat;
        Assert.InRange(seconds, 29, 31); Assert.Equal(SongFile.Write(song), SongFile.Write(DemoSong.Create()));
        var empty = DemoSong.CreateEmpty(); Assert.All(empty.Patterns[0].Tracks.SelectMany(t => t.Rows), n => Assert.Equal(NoteKind.Empty, n.Kind));
    }
    private static byte[] RewriteSongRoot(int keyToReplace, Action<CborWriter> value)
    {
        var r = new CborReader(SongFile.Write(DemoSong.CreateEmpty())); var w = new CborWriter(); var count = r.ReadStartMap()!.Value; w.WriteStartMap(count);
        for (var i = 0; i < count; i++) { var key = r.ReadInt32(); w.WriteInt32(key); if (key == keyToReplace) { r.SkipValue(); value(w); } else w.WriteEncodedValue(r.ReadEncodedValue().Span); }
        r.ReadEndMap(); w.WriteEndMap(); return w.Encode();
    }
}

public class SongEditorTests
{
    [Fact] public void SaveUndoRedoAndBranchTrackContentDirtyState()
    {
        var e = new SongEditor(DemoSong.CreateEmpty()); Assert.False(e.IsDirty); Assert.False(e.Undo());
        e.Change(s => s.Title = "First"); Assert.True(e.IsDirty); e.MarkSaved(); Assert.False(e.IsDirty);
        e.Change(s => s.Title = "Second"); Assert.True(e.IsDirty); Assert.True(e.Undo()); Assert.False(e.IsDirty); Assert.Equal("First", e.Song.Title);
        Assert.True(e.Redo()); Assert.True(e.IsDirty); Assert.True(e.Undo()); e.Change(s => s.Title = "Branch"); Assert.False(e.CanRedo); Assert.True(e.IsDirty);
        e.Load(DemoSong.Create()); Assert.False(e.IsDirty); Assert.False(e.CanUndo); Assert.False(e.CanRedo);
    }
    [Fact] public void NoOpDoesNotIncreaseRevisionAndFailedEditIsAtomic()
    {
        var e = new SongEditor(DemoSong.CreateEmpty()); var revision = e.Revision; e.Change(_ => { }); Assert.Equal(revision, e.Revision);
        Assert.Throws<SongFormatException>(() => e.Change(s => { s.Title = "No"; s.Bpm = 0; })); Assert.Equal("Untitled song", e.Song.Title); Assert.Equal(revision, e.Revision);
        Assert.Throws<InvalidOperationException>(() => e.Change(s => { s.Title = "No"; throw new InvalidOperationException(); })); Assert.False(e.IsDirty);
    }
    [Fact] public void OriginalSongIsIndependentAndUndoReturnsToSavedBytes()
    {
        var source = DemoSong.CreateEmpty(); var e = new SongEditor(source); source.Title = "External"; Assert.Equal("Untitled song", e.Song.Title);
        e.Change(s => s.Instruments[0].Amplitude.AttackMs = 45); Assert.True(e.IsDirty); Assert.True(e.Undo()); Assert.False(e.IsDirty);
        e.Change(s => s.Title = "Third"); var serialized = SongFile.Write(e.Song); e.MarkSaved(); e.Change(s => s.Title = "Fourth"); e.Undo(); Assert.Equal(serialized, SongFile.Write(e.Song)); Assert.False(e.IsDirty);
    }
}

public class StreamReadTests
{
    [Fact] public async Task ReadsPartialNonSeekableSongWithoutOwningStream()
    {
        var bytes = SongFile.Write(DemoSong.Create()); using var stream = new TestReadStream(bytes, bytes.Length, false, 7);
        var song = await SongFile.ReadAsync(stream); Assert.Equal("Neon Orchard", song.Title);
        Assert.Equal(bytes.Length, stream.BytesRead); Assert.False(stream.IsDisposed);
    }
    [Fact] public async Task ReadsInstrumentAndStartsAtCurrentPosition()
    {
        var instrument = InstrumentLibrary.CreatePresets()[3]; var bytes = InstrumentFile.Write(instrument);
        using var stream = new MemoryStream([1, 2, 3, .. bytes]); stream.Position = 3;
        var actual = await InstrumentFile.ReadAsync(stream); Assert.Equal(bytes, InstrumentFile.Write(actual)); Assert.True(stream.CanRead);
    }
    [Fact] public async Task SeekableOversizeIsRejectedBeforeAnyRead()
    {
        using var stream = new TestReadStream(null, long.MaxValue, true);
        await Assert.ThrowsAsync<SongFormatException>(() => SongFile.ReadAsync(stream)); Assert.Equal(0, stream.BytesRead);
    }
    [Fact] public async Task NonSeekableOversizeStopsAfterExactlyOneProbeByte()
    {
        using var stream = new TestReadStream(null, long.MaxValue, false);
        var error = await Assert.ThrowsAsync<SongFormatException>(() => SongFile.ReadAsync(stream));
        Assert.Contains("16 MiB", error.Message); Assert.Equal(SongLimits.MaxFileBytes + 1L, stream.BytesRead); Assert.False(stream.IsDisposed);
    }
    [Fact] public async Task InstrumentStreamUsesTheSameSizeBound()
    {
        using var stream = new TestReadStream(null, SongLimits.MaxFileBytes + 1L, false);
        await Assert.ThrowsAsync<SongFormatException>(() => InstrumentFile.ReadAsync(stream));
        Assert.Equal(SongLimits.MaxFileBytes + 1L, stream.BytesRead);
    }
    [Fact] public async Task EmptyAndTruncatedStreamsFailWithFormatErrors()
    {
        using var empty = new TestReadStream([], 0, false);
        await Assert.ThrowsAsync<SongFormatException>(() => SongFile.ReadAsync(empty));
        var bytes = SongFile.Write(DemoSong.CreateEmpty()); using var truncated = new TestReadStream(bytes, bytes.Length - 1, false, 19);
        await Assert.ThrowsAsync<SongFormatException>(() => SongFile.ReadAsync(truncated));
    }
    [Fact] public async Task CancellationIsHonoredWithoutReadingOrClosing()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var stream = new TestReadStream(null, 100, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SongFile.ReadAsync(stream, source.Token));
        Assert.Equal(0, stream.BytesRead); Assert.False(stream.IsDisposed);
    }
    private sealed class TestReadStream(byte[]? bytes, long length, bool seekable, int chunk = int.MaxValue) : Stream
    {
        public long BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();
        public override long Position { get => seekable ? BytesRead : throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(ReadCore(buffer.Span)); }
        private int ReadCore(Span<byte> buffer)
        {
            var count = (int)Math.Min(Math.Min(buffer.Length, chunk), length - BytesRead);
            if (bytes is null) buffer[..count].Clear(); else bytes.AsSpan((int)BytesRead, count).CopyTo(buffer);
            BytesRead += count; return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
}
