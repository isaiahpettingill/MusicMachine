using System.Buffers.Binary;
using System.Formats.Cbor;

namespace MusicMachine.Core;

/// <summary>Versioned, self-contained CBOR song codec. No JSON, archives, reflection, or external instrument references.</summary>
public static class SongFile
{
    public static byte[] Write(Song song)
    {
        Validate(song);
        var w = new CborWriter(CborConformanceMode.Strict);
        w.WriteStartMap(19);
        CborCodec.K(w, 0); w.WriteTextString("MusicMachine.song"); CborCodec.K(w, 1); w.WriteInt32(song.Version);
        CborCodec.K(w, 2); w.WriteTextString(song.Title); CborCodec.K(w, 3); w.WriteTextString(song.Author);
        CborCodec.K(w, 4); w.WriteDouble(song.Bpm); CborCodec.K(w, 5); w.WriteInt32(song.RowsPerBeat);
        CborCodec.K(w, 6); w.WriteInt32(song.BeatsPerBar); CborCodec.K(w, 7); w.WriteDouble(song.Swing);
        CborCodec.K(w, 8); w.WriteInt32(song.SampleRate); CborCodec.K(w, 9); w.WriteUInt32(song.Seed);
        CborCodec.K(w, 10); w.WriteDouble(song.MasterVolumeDb); CborCodec.K(w, 11); w.WriteInt32(song.LoopStartSection);
        CborCodec.K(w, 12); w.WriteInt32(song.LoopEndSection);
        CborCodec.K(w, 13); CborCodec.Array(w, song.Instruments, CborCodec.WriteInstrument);
        CborCodec.K(w, 14); CborCodec.Array(w, song.Tracks, CborCodec.WriteTrack);
        CborCodec.K(w, 15); CborCodec.Array(w, song.Patterns, CborCodec.WritePattern);
        CborCodec.K(w, 16); CborCodec.Array(w, song.Arrangement, CborCodec.WriteSection);
        CborCodec.K(w, 17); w.WriteTextString("MusicMachine"); // producer hint, never used for behavior
        CborCodec.K(w, 18); w.WriteInt32(song.BeatUnit);
        w.WriteEndMap();
        return CborCodec.Finish(w);
    }
    public static Song Read(byte[] data) => CborCodec.Decode(data, r =>
    {
        var song = new Song();
        string magic = "";
        var version = -1;
        var required = 0;
        CborCodec.Map(r, key =>
        {
            switch (key)
            {
                case 0: magic = CborCodec.Text(r, 64); break;
                case 1: version = r.ReadInt32(); song.Version = version; break;
                case 2: song.Title = CborCodec.Text(r, 256); break;
                case 3: song.Author = CborCodec.Text(r, 256); break;
                case 4: song.Bpm = r.ReadDouble(); break;
                case 5: song.RowsPerBeat = r.ReadInt32(); break;
                case 6: song.BeatsPerBar = r.ReadInt32(); break;
                case 7: song.Swing = r.ReadDouble(); break;
                case 8: song.SampleRate = r.ReadInt32(); break;
                case 9: song.Seed = r.ReadUInt32(); break;
                case 10: song.MasterVolumeDb = r.ReadDouble(); break;
                case 11: song.LoopStartSection = r.ReadInt32(); break;
                case 12: song.LoopEndSection = r.ReadInt32(); break;
                case 13: song.Instruments = CborCodec.Array(r, SongLimits.MaxInstruments, CborCodec.ReadInstrument); required |= 1; break;
                case 14: song.Tracks = CborCodec.Array(r, SongLimits.MaxTracks, CborCodec.ReadTrack); required |= 2; break;
                case 15: song.Patterns = CborCodec.Array(r, SongLimits.MaxPatterns, CborCodec.ReadPattern); required |= 4; break;
                case 16: song.Arrangement = CborCodec.Array(r, SongLimits.MaxSections, CborCodec.ReadSection); required |= 8; break;
                case 18: song.BeatUnit = r.ReadInt32(); break;
                default: CborCodec.Skip(r); break;
            }
        });
        SongValidation.Require(magic == "MusicMachine.song", "This is not a MusicMachine song");
        SongValidation.Require(version == 1, $"Unsupported song version {version}");
        SongValidation.Require(required == 15, "Song is missing required instrument, track, pattern or arrangement data");
        Validate(song);
        return song;
    });
    public static void Validate(Song song) => SongValidation.Validate(song);
    public static Song Clone(Song song) => Read(Write(song));
    public static Song Load(string path) => Read(AtomicFile.Read(path));
    /// <summary>Read a bounded document from the current stream position without closing the caller's stream.</summary>
    public static async Task<Song> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        => Read(await BoundedStream.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
    public static void Save(string path, Song song) => AtomicFile.Write(path, Write(song));
    public static void Save(Song song, string path) => Save(path, song);
}

public static class InstrumentFile
{
    public static byte[] Write(Instrument instrument)
    {
        Validate(instrument);
        var w = new CborWriter(CborConformanceMode.Strict);
        w.WriteStartMap(3); CborCodec.K(w, 0); w.WriteTextString("MusicMachine.instrument");
        CborCodec.K(w, 1); w.WriteInt32(1); CborCodec.K(w, 2); CborCodec.WriteInstrument(w, instrument); w.WriteEndMap();
        return CborCodec.Finish(w);
    }
    public static Instrument Read(byte[] data) => CborCodec.Decode(data, r =>
    {
        string magic = ""; var version = -1; Instrument? instrument = null;
        CborCodec.Map(r, key => { switch (key) { case 0: magic = CborCodec.Text(r, 64); break; case 1: version = r.ReadInt32(); break; case 2: instrument = CborCodec.ReadInstrument(r); break; default: CborCodec.Skip(r); break; } });
        SongValidation.Require(magic == "MusicMachine.instrument", "This is not a MusicMachine instrument");
        SongValidation.Require(version == 1, $"Unsupported instrument version {version}");
        SongValidation.Require(instrument is not null, "Missing instrument data"); Validate(instrument!); return instrument!;
    });
    public static void Validate(Instrument instrument) => SongValidation.Validate(instrument);
    public static Instrument Clone(Instrument instrument) => Read(Write(instrument));
    public static Instrument Load(string path) => Read(AtomicFile.Read(path));
    /// <summary>Read a bounded document from the current stream position without closing the caller's stream.</summary>
    public static async Task<Instrument> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        => Read(await BoundedStream.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
    public static void Save(string path, Instrument instrument) => AtomicFile.Write(path, Write(instrument));
    public static void Save(Instrument instrument, string path) => Save(path, instrument);
}

internal static class BoundedStream
{
    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable", nameof(stream));
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            SongValidation.Require(remaining > 0 && remaining <= SongLimits.MaxFileBytes, "File is empty or exceeds the 16 MiB limit");
        }
        using var data = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            // At the boundary, probe exactly one extra byte without ever buffering it.
            var request = (int)Math.Min(buffer.Length, SongLimits.MaxFileBytes - data.Length + 1);
            var count = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            SongValidation.Require(data.Length + count <= SongLimits.MaxFileBytes, "File exceeds the 16 MiB limit");
            data.Write(buffer, 0, count);
        }
        SongValidation.Require(data.Length > 0, "File is empty");
        return data.ToArray();
    }
}

internal static class AtomicFile
{
    internal static byte[] Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        SongValidation.Require(stream.Length > 0 && stream.Length <= SongLimits.MaxFileBytes, "File is empty or exceeds the 16 MiB limit");
        var data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data); return data;
    }
    internal static void Write(string path, byte[] data)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(data); stream.Flush(flushToDisk: true); }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class CborCodec
{
    [ThreadStatic] private static int _remainingRows;
    internal static void K(CborWriter w, int key) => w.WriteInt32(key);
    internal static byte[] Finish(CborWriter w)
    {
        var data = w.Encode(); SongValidation.Require(data.Length <= SongLimits.MaxFileBytes, "File exceeds the 16 MiB limit"); return data;
    }
    internal static T Decode<T>(byte[] data, Func<CborReader, T> read)
    {
        ArgumentNullException.ThrowIfNull(data);
        SongValidation.Require(data.Length is > 0 and <= SongLimits.MaxFileBytes, "File is empty or exceeds the 16 MiB limit");
        try
        {
            _remainingRows = SongLimits.MaxTotalRows;
            var reader = new CborReader(data, CborConformanceMode.Strict);
            var result = read(reader);
            SongValidation.Require(reader.BytesRemaining == 0, "Unexpected trailing data after CBOR document");
            return result;
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new SongFormatException("Invalid or truncated MusicMachine CBOR: " + ex.Message, ex); }
    }
    internal static void Map(CborReader r, Action<int> field)
    {
        var count = r.ReadStartMap();
        SongValidation.Require(count.HasValue && count.Value <= 256, "CBOR maps must have a definite length of at most 256 fields");
        var keys = new HashSet<int>();
        for (var i = 0; i < count!.Value; i++) { var key = r.ReadInt32(); SongValidation.Require(key >= 0 && keys.Add(key), "Duplicate or negative CBOR field key"); field(key); }
        r.ReadEndMap();
    }
    internal static string Text(CborReader r, int max)
    {
        SongValidation.Require(r.PeekState() == CborReaderState.TextString, "Expected a definite CBOR text string");
        var text = r.ReadTextString(); SongValidation.Require(text.Length <= max, "CBOR text is too long"); return text;
    }
    internal static List<T> Array<T>(CborReader r, int max, Func<CborReader, T> read)
    {
        var count = r.ReadStartArray(); SongValidation.Require(count.HasValue && count.Value <= max, $"CBOR array must have a definite length of at most {max}");
        var items = new List<T>(count!.Value);
        for (var i = 0; i < count.Value; i++) items.Add(read(r)); r.ReadEndArray(); return items;
    }
    internal static void Array<T>(CborWriter w, IReadOnlyCollection<T> items, Action<CborWriter, T> write)
    { w.WriteStartArray(items.Count); foreach (var item in items) write(w, item); w.WriteEndArray(); }
    // Bound unknown fields too: forward compatibility must not open a nesting/count denial-of-service path.
    internal static void Skip(CborReader r) { var budget = 65536; Skip(r, 0, ref budget); }
    private static void Skip(CborReader r, int depth, ref int budget)
    {
        SongValidation.Require(depth <= 32 && --budget >= 0, "Unknown CBOR field is too deeply nested or too large");
        if (r.PeekState() == CborReaderState.StartArray)
        {
            var n = r.ReadStartArray(); SongValidation.Require(n.HasValue && n.Value <= 65536, "Unknown CBOR array is too large");
            for (var i = 0; i < n!.Value; i++) Skip(r, depth + 1, ref budget); r.ReadEndArray();
        }
        else if (r.PeekState() == CborReaderState.StartMap)
        {
            var n = r.ReadStartMap(); SongValidation.Require(n.HasValue && n.Value <= 32768, "Unknown CBOR map is too large");
            for (var i = 0; i < n!.Value; i++) { Skip(r, depth + 1, ref budget); Skip(r, depth + 1, ref budget); } r.ReadEndMap();
        }
        else if (r.PeekState() == CborReaderState.Tag) { r.ReadTag(); Skip(r, depth + 1, ref budget); }
        else { SongValidation.Require(r.PeekState() != CborReaderState.StartIndefiniteLengthByteString && r.PeekState() != CborReaderState.StartIndefiniteLengthTextString, "Indefinite CBOR strings are unsupported"); r.SkipValue(); }
    }
    internal static void WriteInstrument(CborWriter w, Instrument i)
    {
        w.WriteStartMap(19);
        K(w, 0); w.WriteTextString(i.Id); K(w, 1); w.WriteTextString(i.Name); K(w, 2); w.WriteInt32((int)i.Waveform); K(w, 3); w.WriteInt32((int)i.Drum);
        K(w, 4); w.WriteDouble(i.PulseWidth); K(w, 5); w.WriteDouble(i.DetuneCents); K(w, 6); w.WriteDouble(i.Phase); K(w, 7); w.WriteDouble(i.VolumeDb);
        K(w, 8); WriteEnvelope(w, i.Amplitude); K(w, 9); w.WriteDouble(i.PitchEnvelopeSemitones); K(w, 10); w.WriteDouble(i.PitchEnvelopeMs);
        K(w, 11); w.WriteDouble(i.FilterCutoff); K(w, 12); w.WriteDouble(i.FilterResonance); K(w, 13); WriteWave(w, i.CustomWave);
        K(w, 14); Array(w, i.Wavetable, WriteWave); K(w, 15); w.WriteDouble(i.WavetablePosition); K(w, 16); w.WriteBoolean(i.IsLocal);
        K(w, 17); w.WriteInt32(1); K(w, 18); w.WriteTextString("pcm16le"); w.WriteEndMap();
    }
    internal static Instrument ReadInstrument(CborReader r)
    {
        var i = new Instrument { Id = "" }; var version = 1;
        Map(r, k => { switch (k)
        {
            case 0: i.Id = Text(r, 128); break; case 1: i.Name = Text(r, 128); break; case 2: i.Waveform = (Waveform)r.ReadInt32(); break; case 3: i.Drum = (DrumKind)r.ReadInt32(); break;
            case 4: i.PulseWidth = r.ReadDouble(); break; case 5: i.DetuneCents = r.ReadDouble(); break; case 6: i.Phase = r.ReadDouble(); break; case 7: i.VolumeDb = r.ReadDouble(); break;
            case 8: i.Amplitude = ReadEnvelope(r); break; case 9: i.PitchEnvelopeSemitones = r.ReadDouble(); break; case 10: i.PitchEnvelopeMs = r.ReadDouble(); break;
            case 11: i.FilterCutoff = r.ReadDouble(); break; case 12: i.FilterResonance = r.ReadDouble(); break; case 13: i.CustomWave = ReadWave(r); break;
            case 14: i.Wavetable = Array(r, SongLimits.MaxWaveFrames, ReadWave); break; case 15: i.WavetablePosition = r.ReadDouble(); break; case 16: i.IsLocal = r.ReadBoolean(); break;
            case 17: version = r.ReadInt32(); break; case 18: SongValidation.Require(Text(r, 32) == "pcm16le", "Unsupported waveform sample encoding"); break; default: Skip(r); break;
        } });
        SongValidation.Require(version == 1, "Unsupported embedded instrument version"); SongValidation.Validate(i); return i;
    }
    private static void WriteWave(CborWriter w, short[] wave)
    {
        var bytes = new byte[wave.Length * 2]; for (var n = 0; n < wave.Length; n++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(n * 2, 2), wave[n]); w.WriteByteString(bytes);
    }
    private static short[] ReadWave(CborReader r)
    {
        SongValidation.Require(r.PeekState() == CborReaderState.ByteString, "Expected definite waveform bytes");
        var bytes = r.ReadByteString(); SongValidation.Require(bytes.Length % 2 == 0 && bytes.Length <= SongLimits.MaxWaveSamples * 2, "Invalid waveform byte length");
        var result = new short[bytes.Length / 2]; for (var n = 0; n < result.Length; n++) result[n] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(n * 2, 2)); return result;
    }
    private static void WriteEnvelope(CborWriter w, Envelope e)
    { w.WriteStartArray(4); w.WriteDouble(e.AttackMs); w.WriteDouble(e.DecayMs); w.WriteDouble(e.Sustain); w.WriteDouble(e.ReleaseMs); w.WriteEndArray(); }
    private static Envelope ReadEnvelope(CborReader r)
    { SongValidation.Require(r.ReadStartArray() == 4, "Envelope must contain four numbers"); var e = new Envelope { AttackMs = r.ReadDouble(), DecayMs = r.ReadDouble(), Sustain = r.ReadDouble(), ReleaseMs = r.ReadDouble() }; r.ReadEndArray(); return e; }
    internal static void WriteTrack(CborWriter w, Track t)
    {
        w.WriteStartMap(9); K(w, 0); w.WriteTextString(t.Id); K(w, 1); w.WriteTextString(t.Name); K(w, 2); w.WriteTextString(t.InstrumentId); K(w, 3); w.WriteDouble(t.VolumeDb);
        K(w, 4); w.WriteDouble(t.Pan); K(w, 5); w.WriteBoolean(t.Muted); K(w, 6); w.WriteBoolean(t.Solo); K(w, 7); w.WriteTextString(t.Color);
        K(w, 8); Array(w, t.VolumeAutomation, (writer, point) => { writer.WriteStartArray(2); writer.WriteDouble(point.Row); writer.WriteDouble(point.Decibels); writer.WriteEndArray(); }); w.WriteEndMap();
    }
    internal static Track ReadTrack(CborReader r)
    {
        var t = new Track { Id = "" };
        Map(r, k => { switch (k) { case 0: t.Id = Text(r, 128); break; case 1: t.Name = Text(r, 128); break; case 2: t.InstrumentId = Text(r, 128); break;
            case 3: t.VolumeDb = r.ReadDouble(); break; case 4: t.Pan = r.ReadDouble(); break; case 5: t.Muted = r.ReadBoolean(); break; case 6: t.Solo = r.ReadBoolean(); break;
            case 7: t.Color = Text(r, 16); break; case 8: t.VolumeAutomation = Array(r, SongLimits.MaxAutomationPoints, reader => { SongValidation.Require(reader.ReadStartArray() == 2, "Automation point must contain row and decibels"); var p = new AutomationPoint { Row = reader.ReadDouble(), Decibels = reader.ReadDouble() }; reader.ReadEndArray(); return p; }); break;
            default: Skip(r); break; } }); return t;
    }
    internal static void WritePattern(CborWriter w, Pattern p)
    {
        w.WriteStartMap(5); K(w, 0); w.WriteTextString(p.Id); K(w, 1); w.WriteTextString(p.Name); K(w, 2); w.WriteInt32(p.Length);
        K(w, 3); Array(w, p.Tracks, (writer, t) => { writer.WriteStartMap(2); K(writer, 0); writer.WriteTextString(t.TrackId); K(writer, 1); Array(writer, t.Rows, WriteNote); writer.WriteEndMap(); });
        K(w, 4); Array(w, p.Drums, WriteDrum); w.WriteEndMap();
    }
    internal static Pattern ReadPattern(CborReader r)
    {
        var p = new Pattern { Id = "" };
        Map(r, k => { switch (k) { case 0: p.Id = Text(r, 128); break; case 1: p.Name = Text(r, 128); break; case 2: p.Length = r.ReadInt32(); break;
            case 3: p.Tracks = Array(r, SongLimits.MaxTracks, reader => { var t = new PatternTrack(); Map(reader, key => { if (key == 0) t.TrackId = Text(reader, 128); else if (key == 1) t.Rows = Array(reader, SongLimits.MaxRows, ReadNote); else Skip(reader); }); return t; }); break;
            case 4: p.Drums = Array(r, SongLimits.MaxDrumLanes, ReadDrum); break; default: Skip(r); break; } }); return p;
    }
    private static void WriteNote(CborWriter w, NoteEvent n)
    {
        w.WriteStartMap(5); K(w, 0); w.WriteInt32((int)n.Kind); K(w, 1); w.WriteInt32(n.Pitch); K(w, 2); w.WriteInt32((int)n.Timing);
        K(w, 3); if (n.InstrumentId is null) w.WriteNull(); else w.WriteTextString(n.InstrumentId);
        K(w, 4); Array(w, n.Effects, (writer, fx) => writer.WriteTextString(fx)); w.WriteEndMap();
    }
    private static NoteEvent ReadNote(CborReader r)
    {
        SongValidation.Require(--_remainingRows >= 0, "Song has too many note rows and drum steps");
        var n = new NoteEvent(); Map(r, k => { switch (k) { case 0: n.Kind = (NoteKind)r.ReadInt32(); break; case 1: n.Pitch = r.ReadInt32(); break; case 2: n.Timing = (NoteTiming)r.ReadInt32(); break;
            case 3: if (r.PeekState() == CborReaderState.Null) r.ReadNull(); else n.InstrumentId = Text(r, 128); break;
            case 4: n.Effects = Array(r, FxParser.MaxColumns, reader => Text(reader, 16)); break; default: Skip(r); break; } }); return n;
    }
    private static void WriteDrum(CborWriter w, DrumLane d)
    {
        w.WriteStartMap(6); K(w, 0); w.WriteTextString(d.Name); K(w, 1); w.WriteTextString(d.InstrumentId); K(w, 2); w.WriteDouble(d.VolumeDb);
        K(w, 3); w.WriteDouble(d.Pan); K(w, 4); w.WriteBoolean(d.Muted); K(w, 5); w.WriteByteString(d.Steps.ToArray()); w.WriteEndMap();
    }
    private static DrumLane ReadDrum(CborReader r)
    {
        var d = new DrumLane(); Map(r, k => { switch (k) { case 0: d.Name = Text(r, 128); break; case 1: d.InstrumentId = Text(r, 128); break;
            case 2: d.VolumeDb = r.ReadDouble(); break; case 3: d.Pan = r.ReadDouble(); break; case 4: d.Muted = r.ReadBoolean(); break;
            case 5: SongValidation.Require(r.PeekState() == CborReaderState.ByteString, "Expected definite drum steps"); var steps = r.ReadByteString(); SongValidation.Require(steps.Length <= SongLimits.MaxRows, "Too many drum steps"); _remainingRows -= steps.Length; SongValidation.Require(_remainingRows >= 0, "Song has too many note rows and drum steps"); d.Steps = [.. steps]; break;
            default: Skip(r); break; } }); return d;
    }
    internal static void WriteSection(CborWriter w, SongSection s)
    { w.WriteStartMap(3); K(w, 0); w.WriteTextString(s.PatternId); K(w, 1); w.WriteInt32(s.Repeats); K(w, 2); w.WriteInt32(s.Transpose); w.WriteEndMap(); }
    internal static SongSection ReadSection(CborReader r)
    {
        var s = new SongSection(); Map(r, k => { switch (k) { case 0: s.PatternId = Text(r, 128); break; case 1: s.Repeats = r.ReadInt32(); break; case 2: s.Transpose = r.ReadInt32(); break; default: Skip(r); break; } }); return s;
    }
}
