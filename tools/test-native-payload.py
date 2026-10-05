"""Run each built-in exporter through the actual native host without a display/audio device."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import wave

payload = Path(sys.argv[1]).resolve()
metadata = json.loads((payload / "release.json").read_text())
executable = str(payload / metadata["executable"])
with tempfile.TemporaryDirectory(prefix="musicmachine-smoke-") as temp:
    root = Path(temp)
    output = root / "neon-orchard.wav"
    song = root / "neon-orchard.song"
    subprocess.run([executable, "--render-demo", str(output)], check=True, timeout=120)
    subprocess.run([executable, "--demo-song", str(song)], check=True, timeout=120)
    with wave.open(str(output)) as audio:
        assert audio.getnchannels() == 2, "Expected stereo WAV"
        assert audio.getsampwidth() == 2, "Expected PCM16 WAV"
        assert audio.getframerate() == 48000, "Expected 48 kHz WAV"
        frames = audio.getnframes()
        assert frames > 48000, "Expected complete demo audio"
        pcm = audio.readframes(frames)
        assert any(pcm[:48000 * 4]), "Demo begins with an empty waveform"
    for extension in ("wav", "qoa", "flac"):
        destination = root / ("roundtrip." + extension)
        subprocess.run([executable, "--export", str(song), str(destination)], check=True, timeout=120)
        data = destination.read_bytes()
        if extension == "wav":
            assert data == output.read_bytes(), "Saved-song export differs from the deterministic demo"
        elif extension == "qoa":
            assert data[:4] == b"qoaf" and int.from_bytes(data[4:8], "big") == frames
            header = int.from_bytes(data[8:16], "big")
            assert header >> 56 == 2 and (header >> 32) & 0xffffff == 48000
        else:
            assert data[:4] == b"fLaC" and data[4] & 127 == 0, "Expected FLAC STREAMINFO"
            assert int.from_bytes(data[5:8], "big") == 34
            info = int.from_bytes(data[18:26], "big")
            assert info >> 44 == 48000 and (info >> 41) & 7 == 1 and (info >> 36) & 31 == 15
            assert info & ((1 << 36) - 1) == frames, "FLAC frame count differs from WAV"
            assert len(data) < len(pcm), "Demo FLAC should genuinely compress PCM"
            if ffmpeg := shutil.which("ffmpeg"):
                decoded = subprocess.run([ffmpeg, "-v", "error", "-err_detect", "explode", "-i", str(destination), "-f", "s16le", "-"],
                                         check=True, capture_output=True, timeout=120).stdout
                assert decoded == pcm, "Independent FLAC decode differs from the exported PCM16 WAV"
                print("Native FLAC independently decoded byte-for-byte with FFmpeg")
            else:
                print("FFmpeg unavailable: native FLAC metadata/compression checked; independent decoding is covered on Linux")
    print(f"Native {metadata['runtime']} WAV/QOA/FLAC exports verified ({frames} stereo frames)")
