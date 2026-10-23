# Wandering Onion 🧅🎶

An intelligent, next-generation Bard Performance MIDI player, arranger, and auto-converter for **Final Fantasy XIV** (built on the Dalamud plugin framework).

Rebuilt from scratch to replace classic Bard Music Player (BMP) with direct zero-latency in-game hooking and an intelligent MIDI transformation engine.

---

## Key Features

1. **Intelligent MIDI Auto-Conversion (No DAW required!)**:
   - Takes raw, arbitrary, unedited MIDI files and turns them into clean, playable FFXIV tracks without human rewriting.
   - **Range & Pitch Optimization**: Evaluates pitch histograms to calculate optimal key/octave shifts to fit FFXIV's 3-octave span (C3 to C6 / MIDI 48..84), preferring harmonic octave preserves ($\pm 12, \pm 24$).
   - **Skyline Melody Extraction**: Distills multi-note chords down to prominent vocal/lead melodies with optional delayed bass pulses for a complete solo sound.
   - **De-choking & Anti-Lag Normalization**: Enforces strict minimum note spacing ($\ge 50$ms) to prevent note eating and packet stuttering in crowded game areas.

2. **Solo & Band Modes**:
   - **Solo Mode**: Monophonic lead with rhythmic bassline integration.
   - **Band / Ensemble Mode**: Dynamically decomposes polyphonic chords across 2 to 8 band members (Lead, Mid Harmony, Counter Melody, Bassline, Percussion), automatically assigning appropriate FFXIV instruments.

3. **Zero-Latency In-Game Playback**:
   - Direct callback hooks into FFXIV's native `PerformanceMode` and `PerformanceModeWide` addons.
   - Zero key-simulation delay, no Windows focus issues, and no virtual keystroke conflicts.
   - Live visual 3-octave keyboard visualizer.

4. **FFXIV MIDI Export**:
   - Export any arranged song as an FFXIV-ready `.mid` file with track tags (`[Flute]`, `[Piano]`, etc.) compatible with Wandering Onion, classic BMP, LightAmp, or MidiBard2.

---

## Commands

- `/wanderingonion` or `/onion`: Open or close the main UI window.

---

## Building

```bash
dotnet build -c Release
```
