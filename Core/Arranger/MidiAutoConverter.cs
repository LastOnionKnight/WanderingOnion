using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using WanderingOnion.Core.Audio;

namespace WanderingOnion.Core.Arranger;

public class MidiAutoConverter
{
    private class TimedRawNote
    {
        public long StartTimeMs { get; set; }
        public long EndTimeMs { get; set; }
        public int MidiNote { get; set; }
        public int Velocity { get; set; }
        public int Channel { get; set; }
        public int TrackIndex { get; set; }
    }

    /// <summary>
    /// Converts any raw MIDI into a clean, fully playable FFXIV performance song (Solo or Band).
    /// </summary>
    public static ConvertedSong Convert(MidiFile midi, ArrangementConfig config, string title = "Converted Song")
    {
        var tempoMap = midi.GetTempoMap();
        var rawNotes = new List<TimedRawNote>();

        var tracks = midi.GetTrackChunks().ToList();
        for (int i = 0; i < tracks.Count; i++)
        {
            var chunk = tracks[i];
            foreach (var note in chunk.GetNotes())
            {
                var metricStart = note.TimeAs<MetricTimeSpan>(tempoMap);
                var metricEnd = note.EndTimeAs<MetricTimeSpan>(tempoMap);

                rawNotes.Add(new TimedRawNote
                {
                    StartTimeMs = metricStart.TotalMicroseconds / 1000,
                    EndTimeMs = metricEnd.TotalMicroseconds / 1000,
                    MidiNote = note.NoteNumber,
                    Velocity = note.Velocity,
                    Channel = note.Channel,
                    TrackIndex = i
                });
            }
        }

        if (rawNotes.Count == 0)
        {
            return new ConvertedSong
            {
                Title = title,
                Mode = config.Mode,
                TotalDurationMs = 0
            };
        }

        // 1. Determine optimal shift
        int shift = config.ManualSemitoneShift;
        if (config.AutoTranspose)
        {
            shift = MidiAnalyzer.FindOptimalShift(rawNotes.Select(n => n.MidiNote), config.PreferHarmonicOctaves);
        }

        // Apply shift & fold out-of-range notes if enabled
        foreach (var n in rawNotes)
        {
            n.MidiNote += shift;
            if (config.FoldOutOfRangeNotes)
            {
                n.MidiNote = FfxivPitch.ClampOrFoldToRange(n.MidiNote);
            }
        }

        rawNotes = rawNotes.OrderBy(n => n.StartTimeMs).ToList();
        long totalDurationMs = rawNotes.Max(n => n.EndTimeMs);

        ConvertedSong result = new()
        {
            Title = title,
            TotalDurationMs = totalDurationMs,
            AppliedShift = shift,
            Mode = config.Mode
        };

        if (config.Mode == ArrangementMode.Solo)
        {
            result.Tracks.Add(CreateSoloTrack(rawNotes, config));
        }
        else
        {
            result.Tracks.AddRange(CreateBandTracks(rawNotes, config));
        }

        return result;
    }

    private static ConvertedTrack CreateSoloTrack(List<TimedRawNote> rawNotes, ArrangementConfig config)
    {
        var track = new ConvertedTrack
        {
            VoiceIndex = 0,
            TrackName = "[Solo Lead] Performance",
            Instrument = FfxivInstrument.Piano
        };

        // Group notes by time windows (~25ms) to detect chords
        var grouped = rawNotes
            .GroupBy(n => n.StartTimeMs / 25)
            .OrderBy(g => g.Key)
            .ToList();

        var selected = new List<TimedRawNote>();

        foreach (var group in grouped)
        {
            var notesAtTime = group.OrderByDescending(n => n.MidiNote).ToList();
            if (notesAtTime.Count == 0) continue;

            // Skyline algorithm: pick the highest note (melody)
            var topNote = notesAtTime.First();
            selected.Add(topNote);

            // If bass enabled and there's a distinct low bass note, check if we can interleave
            if (config.IncludeBassInSolo && notesAtTime.Count > 1)
            {
                var bassNote = notesAtTime.Last();
                if (topNote.MidiNote - bassNote.MidiNote >= 12) // at least an octave separation
                {
                    // Delay bass by 50ms so it sounds as a rolled chord
                    var delayedBass = new TimedRawNote
                    {
                        StartTimeMs = topNote.StartTimeMs + config.MinNoteSpacingMs,
                        EndTimeMs = Math.Max(topNote.StartTimeMs + config.MinNoteSpacingMs + 40, bassNote.EndTimeMs),
                        MidiNote = bassNote.MidiNote,
                        Velocity = (int)(bassNote.Velocity * 0.8), // slightly softer bass
                        Channel = bassNote.Channel
                    };
                    selected.Add(delayedBass);
                }
            }
        }

        // Apply strict spacing and dechoking
        track.Notes = DechokeAndSpaceNotes(selected, config);
        return track;
    }

    private static List<ConvertedTrack> CreateBandTracks(List<TimedRawNote> rawNotes, ArrangementConfig config)
    {
        int bandSize = Math.Clamp(config.BandSize, 2, 8);
        var tracks = new List<ConvertedTrack>();

        // Default instrument assignments for band roles
        FfxivInstrument[] bandRoles =
        {
            FfxivInstrument.Flute,              // Role 0: Lead Melody
            FfxivInstrument.Piano,              // Role 1: Mid Harmony / Chords
            FfxivInstrument.Violin,             // Role 2: Counter Melody
            FfxivInstrument.DoubleBass,         // Role 3: Bassline
            FfxivInstrument.Saxophone,          // Role 4: Secondary Lead / Brass
            FfxivInstrument.Clarinet,           // Role 5: Pad / Sustained
            FfxivInstrument.ElectricGuitarClean,// Role 6: Rhythm
            FfxivInstrument.SnareDrum           // Role 7: Percussion
        };

        for (int i = 0; i < bandSize; i++)
        {
            var roleInstrument = bandRoles[i % bandRoles.Length];
            tracks.Add(new ConvertedTrack
            {
                VoiceIndex = i,
                TrackName = $"[{roleInstrument.Name}] Bard {i + 1}",
                Instrument = roleInstrument
            });
        }

        // Group notes occurring at similar times
        var grouped = rawNotes
            .GroupBy(n => n.StartTimeMs / 30)
            .OrderBy(g => g.Key)
            .ToList();

        var playerVoiceNotes = new List<TimedRawNote>[bandSize];
        for (int i = 0; i < bandSize; i++) playerVoiceNotes[i] = new List<TimedRawNote>();

        foreach (var group in grouped)
        {
            var notesAtTime = group.OrderByDescending(n => n.MidiNote).ToList();
            if (notesAtTime.Count == 1)
            {
                // Single note goes to Lead (Bard 1)
                playerVoiceNotes[0].Add(notesAtTime[0]);
            }
            else
            {
                // Melody goes to Bard 0 (highest pitch)
                playerVoiceNotes[0].Add(notesAtTime[0]);

                // Bass goes to lowest Bard (Bard 3 if 4 players, etc.)
                int bassPlayerIndex = Math.Min(3, bandSize - 1);
                playerVoiceNotes[bassPlayerIndex].Add(notesAtTime.Last());

                // Middle voices distributed across remaining bards
                int middleNotesCount = notesAtTime.Count - 2;
                if (middleNotesCount > 0)
                {
                    int bardCursor = 1;
                    for (int m = 1; m < notesAtTime.Count - 1; m++)
                    {
                        if (bardCursor == bassPlayerIndex) bardCursor++;
                        if (bardCursor >= bandSize) bardCursor = 1;

                        playerVoiceNotes[bardCursor].Add(notesAtTime[m]);
                        bardCursor++;
                    }
                }
            }
        }

        // Apply dechoking and normalization to each player track independently
        for (int i = 0; i < bandSize; i++)
        {
            tracks[i].Notes = DechokeAndSpaceNotes(playerVoiceNotes[i], config);
        }

        return tracks;
    }

    private static List<PlayableNote> DechokeAndSpaceNotes(List<TimedRawNote> notes, ArrangementConfig config)
    {
        var ordered = notes.OrderBy(n => n.StartTimeMs).ToList();
        var result = new List<PlayableNote>();

        long lastStartTime = -1000;
        long lastEndTime = -1000;

        foreach (var n in ordered)
        {
            long start = n.StartTimeMs;
            long duration = Math.Max(config.MinNoteDurationMs, n.EndTimeMs - n.StartTimeMs);

            // Dechoke: if this note starts too close to the previous note, enforce min spacing
            if (config.DechokeNotes && start < lastStartTime + config.MinNoteSpacingMs)
            {
                start = lastStartTime + config.MinNoteSpacingMs;
            }

            // Also truncate previous note duration if it overlaps into the new note
            if (result.Count > 0 && lastEndTime > start)
            {
                var prev = result[^1];
                prev.DurationMs = Math.Max(config.MinNoteDurationMs, start - prev.StartTimeMs - 5);
            }

            result.Add(new PlayableNote
            {
                StartTimeMs = start,
                DurationMs = duration,
                MidiNote = n.MidiNote,
                Velocity = n.Velocity
            });

            lastStartTime = start;
            lastEndTime = start + duration;
        }

        return result;
    }

    /// <summary>
    /// Exports the converted song into a standard MIDI (.mid) file with FFXIV track names and settings.
    /// Compatible with WanderingOnion, classic BMP, LightAmp, and MidiBard2.
    /// </summary>
    public static void ExportToMidiFile(ConvertedSong song, string filePath)
    {
        var midiFile = new MidiFile();
        var tempoMap = TempoMap.Create(Tempo.FromBeatsPerMinute(120));

        foreach (var track in song.Tracks)
        {
            var chunk = new TrackChunk();
            chunk.Events.Add(new SequenceTrackNameEvent(track.TrackName));

            // Set GM program number matching instrument
            chunk.Events.Add(new ProgramChangeEvent((SevenBitNumber)track.Instrument.GeneralMidiProgram));

            var midiNotes = track.Notes.Select(n =>
            {
                var metricStart = new MetricTimeSpan(n.StartTimeMs * 1000);
                var metricLength = new MetricTimeSpan(n.DurationMs * 1000);

                var tickStart = TimeConverter.ConvertFrom(metricStart, tempoMap);
                var tickLength = LengthConverter.ConvertFrom(metricLength, tickStart, tempoMap);

                return new Note((SevenBitNumber)n.MidiNote, tickLength, tickStart)
                {
                    Velocity = (SevenBitNumber)n.Velocity
                };
            }).ToList();

            chunk.AddObjects(midiNotes);
            midiFile.Chunks.Add(chunk);
        }

        midiFile.Write(filePath, true);
    }
}
