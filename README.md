# KaraoqueProMax

Karaoke player for Windows with singing scores, for karaoke videos (AVI/MP4) whose lyrics change color as they are sung.

- **Search** the catalog by song, artist or code (accent-insensitive); queue songs per singer.
- **Full-screen playback** (VLC) with auto-hiding controls, key change (±6 semitones) and volume.
- **Live scoring** from the microphone, 0–100:
  - *Afinação* (60): the sung note fits the backing track's harmony at that moment and is in tune.
  - *Ritmo* (25): singing while the lyrics wipe says to sing.
  - *Presença* (15): how much of the song was sung.
- **Speaker-bleed rejection**: the app knows the music it plays, learns the speaker→mic delay and per-band coupling, and only credits notes clearly above the predicted bleed.

## Projects

| Project | What it is |
|---|---|
| `KaraokeCore` | Song analysis (harmony/chroma, tuning, lyrics-wipe detection via FFmpeg), pitch tracking (YIN), bleed model, scoring engine, catalog |
| `KaraokePlayer` | WPF app (LibVLCSharp, NAudio) |
| `KaraokeSim` | Offline tool: scores recorded or synthesized singers against a song |

## Build

Requires the .NET 10 SDK and FFmpeg (`winget install Gyan.FFmpeg`).

```
dotnet build -c Release
```

Installer (self-contained, bundles FFmpeg; needs Inno Setup 6): `installer\build.ps1` → `installer\KaraokeSetup.exe`.

The song folders must contain a `_catalogo.csv` (`;`-separated: codigo, arquivo, interprete, titulo, …). Songs are not part of this repository.

## Calibration recordings

`Karaoke.exe --record <folder>` saves what the microphone hears during each song, aligned to the song clock, for replay with:

```
KaraokeSim --song <video> --mic <recording.wav> --offset <value from the .txt>
```
