# Example song

`neon-orchard-ui.song` is the 30-second Neon Orchard demo with changes made and saved through the actual desktop UI during verification:

- Pattern A, melody row 1 changed to B4 using literal keyboard entry
- Undo/redo used to verify the edit
- Added a kick hit at step 4 and an accented hat at step 2 through the drum machine
- Saved as CBOR and exported through the app's WAV and QOA dialogs

Both exported formats were decoded and verified as 48 kHz stereo, exactly 30 seconds. The source `.song` contains every required instrument. Open it in MusicMachine or export headlessly:

```sh
MusicMachine.Desktop --export examples/neon-orchard-ui.song neon-orchard.wav
```
