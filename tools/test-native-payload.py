"""Run the actual native host without a display or sound device and inspect PCM."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import wave

payload = Path(sys.argv[1]).resolve()
metadata = json.loads((payload / "release.json").read_text())
with tempfile.TemporaryDirectory(prefix="musicmachine-smoke-") as temp:
    output = Path(temp) / "neon-orchard.wav"
    subprocess.run([str(payload / metadata["executable"]), "--render-demo", str(output)], check=True, timeout=120)
    with wave.open(str(output)) as audio:
        assert audio.getnchannels() == 2, "Expected stereo WAV"
        assert audio.getsampwidth() == 2, "Expected PCM16 WAV"
        assert audio.getframerate() == 48000, "Expected 48 kHz WAV"
        assert audio.getnframes() > 48000, "Expected complete demo audio"
        assert any(audio.readframes(min(48000, audio.getnframes()))), "Demo begins with an empty waveform"
    print(f"Native {metadata['runtime']} export verified ({output.stat().st_size} bytes)")
