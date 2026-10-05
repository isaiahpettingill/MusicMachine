# Synthetic decoder fixtures

Both fixtures contain only a generated 0.5-second, 440 Hz sine at 44.1 kHz.
Generated with the system FFmpeg 7.1.5 (libmp3lame / FLAC encoders):

```sh
ffmpeg -v error -nostdin -y -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=0.5' -c:a libmp3lame sine-440hz.mp3
ffmpeg -v error -nostdin -y -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=0.5' -c:a flac sine-440hz.flac
```

They contain no recorded third-party content. Tests expect 24,000 resampled mono
frames at 48 kHz and non-silent, bounded PCM. This is decoder verification, not a
claim that browser UI or browser worker execution passed.
