# KaraoqueProMax

A karaoke player for Windows that **scores your singing**. It plays ordinary karaoke videos (the kind where the lyrics change color as they are sung), listens through any microphone and gives each performance a score from 0 to 100.

- **Search** by song, artist or code, accent- and case-insensitive; queue songs per singer.
- **Full-screen playback** with auto-hiding controls, **key change** (±6 semitones) and volume.
- **Live scoring** with a pitch strip over the video, per-line verdicts and a final score.
- **Speaker bleed is filtered out**: a silent room scores ~0 even with the music loud.

**Download:** the installer is on the [Releases page](https://github.com/jamerico/KaraoqueProMax/releases). It bundles everything it needs (.NET, VLC, FFmpeg).

---

## Input format — what your song files need to be

### 1. The videos

| Requirement | Details |
|---|---|
| **Container / codec** | Anything VLC plays: `.avi` (XviD/DivX + MP3), `.mp4` (H.264 + AAC), `.mkv`, `.mpg`/`.mpeg`, `.vob`, `.wmv`, `.mov`, `.webm`, `.m4v`, `.flv`. |
| **Audio** | One audio track with the **instrumental backing track** (no lead vocal). A quiet guide melody is fine. |
| **Lyrics** | Burned into the picture, **changing color as they are sung** (a "wipe"). Any colors, fonts and backgrounds work: the detector looks for the moving edge of the color change, not for specific colors. |
| **Resolution** | Any (tested with 640×480 and 1280×720). |

What each part is used for:
- The **audio** gives the harmony at every moment (which notes fit), the song's exact tuning, and a reference for removing speaker bleed from the microphone.
- The **lyrics wipe** gives *when* to sing. Videos without a wipe still play, but the timing part of the score has nothing to compare against.

### 2. Folders

Songs can live in **any number of folders, on any drive**, in any subfolder structure. Add them in the app with **📁 Pastas das músicas → + Adicionar pasta…**. The list is saved, and the app reloads it on every start.

On first start with no folders configured, the app looks for song folders on its own: every top-level folder of every drive that contains a `_catalogo.csv`, plus `Videos\Karaoke` in your user folder.

Each folder is read in one of two ways:

| Folder contains… | How songs are found |
|---|---|
| a `_catalogo.csv` file | The catalog lists the songs (see below). Fast, even for tens of thousands of files. |
| no catalog | The folder **and all its subfolders** are scanned for video files; artist and title come from the file names. |

### 3. File names (folders without a catalog)

| File name | Code | Artist | Title |
|---|---|---|---|
| `00123 - RPM - Olhar 43.avi` | 00123 | RPM | Olhar 43 |
| `Legiao Urbana - Tempo Perdido.mp4` | | Legiao Urbana | Tempo Perdido |
| `Chitaozinho_e_Xororo_-_Evidencias.mpg` | | Chitaozinho e Xororo | Evidencias |
| `Evidencias.mkv` | | | Evidencias |

Rules: parts are separated by ` - ` (space, hyphen, space); underscores count as spaces; a first part made only of digits is the song code.

### 4. The catalog file `_catalogo.csv` (optional)

A text file placed **in the song folder itself** (not in a subfolder):

- **Encoding:** UTF-8 (with or without BOM). Accents are fine.
- **Separator:** semicolon `;`. Fields may be quoted with `"…"` (a quote inside a field is written `""`).
- **First line:** column names, in any order. Names are case-insensitive.

| Column | Required | Meaning |
|---|---|---|
| `arquivo` | **yes** | File name, relative to the folder (may include a subfolder, e.g. `rock\01001.avi`) |
| `titulo` | recommended | Song title |
| `interprete` | recommended | Artist |
| `codigo` | optional | Song code (searchable) |
| `observacao` | optional | A note about the file, shown to the singer |
| *(anything else)* | ignored | e.g. `album`, `ano`, `tipo`, `tamanho_MB` |

If `titulo`, `interprete` or `codigo` is empty for a row, it is taken from the file name using the rules above.

Example:

```csv
"codigo";"arquivo";"interprete";"titulo";"observacao"
"01001";"01001 - Paralamas Do Sucesso - A Lhe Esperar.mp4";"Paralamas Do Sucesso";"A Lhe Esperar";""
"01002";"01002.mp4";"Aviões Do Forró";"Agora Chora";"final do vídeo pode ter falhas"
```

PowerShell can write this format directly: `Export-Csv _catalogo.csv -NoTypeInformation -Encoding UTF8 -Delimiter ';'`.

---

## How the score works

| Part | Points | Measures |
|---|---|---|
| **Afinação** | 60 | The sung note belongs to the harmony at that moment (chord tones best, other notes of the key partly) and is in tune. Octaves don't matter, so any voice range works. |
| **Ritmo** | 25 | Voice while the lyrics wipe is moving, not in the instrumental parts. |
| **Presença** | 15 | How much of the sung parts had a voice. |

Pitch is judged against the **harmony** of the backing track, not an exact melody (karaoke tracks rarely contain one), so a wrong note that still fits the chord counts as good.

**Speaker bleed:** the app knows exactly what it is playing. It measures the speaker→microphone delay (up to 0.7 s, enough for Bluetooth) and how much of each frequency band reaches the mic, then credits a note only when it stands clearly above that prediction.

**Microphones:** any Windows input works. The laptop's built-in mic works if you sing close to it; an external mic, or a mic plugged into the speaker with the laptop 1–3 m away, works better. Turn off Windows "audio enhancements" / noise suppression for the mic.

---

## Where the app keeps its data

| What | Where |
|---|---|
| Settings (folders, microphone, volume) | `%APPDATA%\KaraokePlayer\settings.json` |
| Score history | `%APPDATA%\KaraokePlayer\placar.json` |
| Song analysis cache (a few MB per song, built on first play) | `%LOCALAPPDATA%\KaraokePlayer\cache` |
| Calibration recordings ("gravar para calibrar" shortcut) | `Documents\Karaoke gravacoes` |

---

## Building from source

| Project | What it is |
|---|---|
| `KaraokeCore` | Song analysis (harmony, tuning, lyrics-wipe detection via FFmpeg), pitch tracking (YIN), bleed model, scoring engine, catalog |
| `KaraokePlayer` | The WPF app (LibVLCSharp, NAudio) |
| `KaraokeSim` | Offline tool: scores recorded or synthesized singers against a song; lists what a folder contains |

Requirements: Windows, .NET 10 SDK, FFmpeg on `PATH` (`winget install Gyan.FFmpeg`).

```
dotnet build -c Release
```

Installer (needs Inno Setup 6, `winget install JRSoftware.InnoSetup`): run `installer\build.ps1` → `installer\KaraokeSetup.exe`.

### KaraokeSim

```
KaraokeSim --catalog <folder>                               # what the player would list for that folder
KaraokeSim --song <video> --make good|real|flat|random|offbeat|silent --out singer.wav
KaraokeSim --song <video> --mic <recording.wav> [--offset s] # score a recording
```

Recordings made with `Karaoke.exe --record <folder>` come with a `.txt` holding the `--offset` value.
