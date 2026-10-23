using System;
using System.Collections.Generic;
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
        public TrackMusicalRole Role { get; set; }

        public double CalculateSalience()
        {
            double baseScore = Role switch
            {
                TrackMusicalRole.VocalLead => 10000.0,
                TrackMusicalRole.InstrumentMelody => 5000.0,
                TrackMusicalRole.Harmony => 1000.0,
                TrackMusicalRole.Bass => 800.0,
                TrackMusicalRole.DrumsPercussion => 200.0,
                TrackMusicalRole.SoundEffects => 50.0,
                _ => 500.0
            };

            // Subtle pitch bonus for higher vocal presence
            baseScore += (MidiNote * 2.0) + (Velocity * 0.5);
            return baseScore;
        }
    }

    public static ConvertedSong Convert(MidiFile midi, ArrangementConfig config, string title = "Converted Song")
    {
        var tempoMap = midi.GetTempoMap();
        var rawNotes = new List<TimedRawNote>();
        var sourceToneChanges = new List<TimedToneChange>();

        var tracks = midi.GetTrackChunks().ToList();
        var analyses = MidiAnalyzer.AnalyzeMidi(midi);

        for (int i = 0; i < tracks.Count; i++)
        {
            var chunk = tracks[i];
            var analysis = analyses.FirstOrDefault(a => a.TrackIndex == i);
            var role = analysis?.DetectedRole ?? TrackMusicalRole.Harmony;

            if (config.SelectedVocalTrackIndex >= 0 && i == config.SelectedVocalTrackIndex)
            {
                role = TrackMusicalRole.VocalLead;
            }

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
                    TrackIndex = i,
                    Role = role
                });
            }

            foreach (var timedEvent in chunk.GetTimedEvents())
            {
                if (timedEvent.Event is ProgramChangeEvent pce)
                {
                    int tone = FfxivInstrument.ProgramToGuitarTone(pce.ProgramNumber);
                    if (tone >= 0)
                    {
                        var metric = timedEvent.TimeAs<MetricTimeSpan>(tempoMap);
                        sourceToneChanges.Add(new TimedToneChange
                        {
                            TimeMs = metric.TotalMicroseconds / 1000,
                            ToneIndex = tone
                        });
                    }
                }
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

        // 1. Determine optimal shift, heavily weighting vocal and melody tracks
        int shift = config.ManualSemitoneShift;
        if (config.AutoTranspose)
        {
            var pitchSample = new List<int>();
            foreach (var n in rawNotes)
            {
                int multiplier = n.Role switch
                {
                    TrackMusicalRole.VocalLead => 10,
                    TrackMusicalRole.InstrumentMelody => 5,
                    TrackMusicalRole.Bass => 2,
                    _ => 1
                };
                for (int m = 0; m < multiplier; m++) pitchSample.Add(n.MidiNote);
            }

            shift = MidiAnalyzer.FindOptimalShift(pitchSample, config.PreferHarmonicOctaves);
        }

        // Apply shift & fold out-of-range notes
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
            result.Tracks.Add(CreateSoloVocalPriorityTrack(rawNotes, config, sourceToneChanges));
        }
        else
        {
            result.Tracks.AddRange(CreateBandVocalPriorityTracks(rawNotes, config, sourceToneChanges));
        }

        return result;
    }

    private static ConvertedTrack CreateSoloVocalPriorityTrack(List<TimedRawNote> rawNotes, ArrangementConfig config, List<TimedToneChange> sourceToneChanges)
    {
        var track = new ConvertedTrack
        {
            VoiceIndex = 0,
            TrackName = "[Solo Lead] Vocal-Priority",
            Instrument = FfxivInstrument.Flute // Clear, melodic lead
        };

        // Group notes by time windows (~25ms)
        var grouped = rawNotes
            .GroupBy(n => n.StartTimeMs / 25)
            .OrderBy(g => g.Key)
            .ToList();

        var selected = new List<TimedRawNote>();
        long lastVocalEndTime = -1000;

        foreach (var group in grouped)
        {
            var notesAtTime = group.ToList();
            if (notesAtTime.Count == 0) continue;

            // Find highest salience note
            var winner = notesAtTime.OrderByDescending(n => n.CalculateSalience()).First();

            bool isVocalOrLead = winner.Role == TrackMusicalRole.VocalLead || winner.Role == TrackMusicalRole.InstrumentMelody;

            if (isVocalOrLead)
            {
                selected.Add(winner);
                lastVocalEndTime = Math.Max(lastVocalEndTime, winner.EndTimeMs);
            }
            else
            {
                // Non-vocal note: only allow if not actively colliding with a sustained vocal syllable
                if (!config.DuckAccompanimentDuringVocals || winner.StartTimeMs >= lastVocalEndTime - 20)
                {
                    selected.Add(winner);
                }
            }

            // If bass enabled and vocalist isn't rapid-firing, interleave a bass note in resting gaps
            if (config.IncludeBassInSolo && !isVocalOrLead)
            {
                var bassNote = notesAtTime.FirstOrDefault(n => n.Role == TrackMusicalRole.Bass);
                if (bassNote != null && bassNote != winner && bassNote.StartTimeMs >= lastVocalEndTime + 40)
                {
                    selected.Add(bassNote);
                }
            }
        }

        if (track.Instrument.GuitarToneIndex >= 0)
        {
            track.ToneChanges.Add(new TimedToneChange { TimeMs = 0, ToneIndex = track.Instrument.GuitarToneIndex });
            track.ToneChanges.AddRange(sourceToneChanges);
            track.ToneChanges = track.ToneChanges.OrderBy(t => t.TimeMs).ToList();
        }
        track.Notes = DechokeAndSpaceNotes(selected, config);
        return track;
    }

    private static List<ConvertedTrack> CreateBandVocalPriorityTracks(List<TimedRawNote> rawNotes, ArrangementConfig config, List<TimedToneChange> sourceToneChanges)
    {
        int bandSize = Math.Clamp(config.BandSize, 2, 8);
        var tracks = new List<ConvertedTrack>();

        FfxivInstrument[] bandRoles =
        {
            FfxivInstrument.Flute,              // Bard 1: Vocal Lead
            FfxivInstrument.Piano,              // Bard 2: Main Harmony / Keyboard
            FfxivInstrument.Violin,             // Bard 3: Counter Melody / Strings
            FfxivInstrument.DoubleBass,         // Bard 4: Bassline
            FfxivInstrument.Saxophone,          // Bard 5: Brass / Secondary Lead
            FfxivInstrument.Clarinet,           // Bard 6: Woodwind Pad
            FfxivInstrument.ElectricGuitarClean,// Bard 7: Rhythm Guitar
            FfxivInstrument.SnareDrum           // Bard 8: Percussion
        };

        for (int i = 0; i < bandSize; i++)
        {
            var inst = bandRoles[i % bandRoles.Length];
            string roleLabel = i == 0 ? "Vocal Lead" : (i == 3 ? "Bass" : $"Harmony {i}");
            var track = new ConvertedTrack
            {
                VoiceIndex = i,
                TrackName = $"[{inst.Name}] {roleLabel}",
                Instrument = inst
            };
            if (inst.GuitarToneIndex >= 0)
            {
                track.ToneChanges.Add(new TimedToneChange { TimeMs = 0, ToneIndex = inst.GuitarToneIndex });
                track.ToneChanges.AddRange(sourceToneChanges);
                track.ToneChanges = track.ToneChanges.OrderBy(t => t.TimeMs).ToList();
            }
            tracks.Add(track);
        }

        var playerVoiceNotes = new List<TimedRawNote>[bandSize];
        for (int i = 0; i < bandSize; i++) playerVoiceNotes[i] = new List<TimedRawNote>();

        // Separate tracks by musical roles
        var vocalNotes = rawNotes.Where(n => n.Role == TrackMusicalRole.VocalLead || n.Role == TrackMusicalRole.InstrumentMelody).ToList();
        var bassNotes = rawNotes.Where(n => n.Role == TrackMusicalRole.Bass).ToList();
        var harmonyNotes = rawNotes.Where(n => n.Role != TrackMusicalRole.VocalLead && n.Role != TrackMusicalRole.InstrumentMelody && n.Role != TrackMusicalRole.Bass).ToList();

        // 1. Assign Vocal Notes strictly to Bard 1 (Lead Bard)
        if (vocalNotes.Count > 0)
        {
            playerVoiceNotes[0].AddRange(vocalNotes);
        }
        else
        {
            // Fallback: highest pitch line to Bard 1
            var groupedAll = rawNotes.GroupBy(n => n.StartTimeMs / 30).OrderBy(g => g.Key);
            foreach (var g in groupedAll) playerVoiceNotes[0].Add(g.OrderByDescending(n => n.MidiNote).First());
        }

        // 2. Assign Bass strictly to Bard 4 (or lowest bard slot)
        int bassBardIndex = Math.Min(3, bandSize - 1);
        playerVoiceNotes[bassBardIndex].AddRange(bassNotes);

        // 3. Distribute Harmony across remaining Bards
        if (bandSize > 2)
        {
            var availableHarmonyBards = Enumerable.Range(1, bandSize - 1)
                .Where(b => b != bassBardIndex)
                .ToList();

            if (availableHarmonyBards.Count > 0)
            {
                var harmonyGrouped = harmonyNotes.GroupBy(n => n.StartTimeMs / 30).OrderBy(g => g.Key);
                foreach (var g in harmonyGrouped)
                {
                    var list = g.OrderByDescending(n => n.MidiNote).ToList();
                    for (int h = 0; h < list.Count; h++)
                    {
                        var targetBard = availableHarmonyBards[h % availableHarmonyBards.Count];
                        playerVoiceNotes[targetBard].Add(list[h]);
                    }
                }
            }
        }

        // Normalize & dechoke every track
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

            if (config.DechokeNotes && start < lastStartTime + config.MinNoteSpacingMs)
            {
                start = lastStartTime + config.MinNoteSpacingMs;
            }

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
                Velocity = n.Velocity,
                IsVocalLead = n.Role == TrackMusicalRole.VocalLead
            });

            lastStartTime = start;
            lastEndTime = start + duration;
        }

        return result;
    }

    public static void ExportToMidiFile(ConvertedSong song, string filePath)
    {
        var midiFile = new MidiFile();
        var tempoMap = TempoMap.Create(Tempo.FromBeatsPerMinute(120));

        foreach (var track in song.Tracks)
        {
            var chunk = new TrackChunk();
            chunk.Events.Add(new SequenceTrackNameEvent(track.TrackName));
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
