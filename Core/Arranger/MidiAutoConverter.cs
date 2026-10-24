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

        // 1. Automatic Vocal-Centric Octave Shift (Guarantees Vocal Melody Contour is Preserved)
        int shift = config.ManualSemitoneShift;
        if (config.AutoTranspose)
        {
            var vocalNotes = rawNotes.Where(n => n.Role == TrackMusicalRole.VocalLead).ToList();
            if (vocalNotes.Count == 0)
            {
                vocalNotes = rawNotes.Where(n => n.Role == TrackMusicalRole.InstrumentMelody).ToList();
            }

            if (vocalNotes.Count > 0)
            {
                // Strict vocal-centric octave shift: locks entire vocal line inside [48..84] without pitch inversions!
                shift = MidiAnalyzer.FindVocalCentricShift(vocalNotes.Select(n => n.MidiNote));
            }
            else
            {
                shift = MidiAnalyzer.FindOptimalShift(rawNotes.Select(n => n.MidiNote), config.PreferHarmonicOctaves);
            }
        }

        // Apply shift: preserve vocal melody contour intact, fold only non-vocal outliers
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

        double primaryBpm = tempoMap.GetTempoChanges().FirstOrDefault()?.Value.BeatsPerMinute ?? 120.0;

        ConvertedSong result = new()
        {
            Title = title,
            TotalDurationMs = totalDurationMs,
            AppliedShift = shift,
            Mode = config.Mode,
            PrimaryBpm = primaryBpm,
            SourceTempoMap = tempoMap,
            SourceTimeDivision = midi.TimeDivision
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
            Instrument = FfxivInstrument.Saxophone // AuraDj signature vocal instrument
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

            var vocalNote = notesAtTime.FirstOrDefault(n => n.Role == TrackMusicalRole.VocalLead || n.Role == TrackMusicalRole.InstrumentMelody);
            var bassNote = notesAtTime.FirstOrDefault(n => n.Role == TrackMusicalRole.Bass);
            var harmonyNote = notesAtTime.FirstOrDefault(n => n.Role != TrackMusicalRole.VocalLead && n.Role != TrackMusicalRole.InstrumentMelody && n.Role != TrackMusicalRole.Bass);

            if (vocalNote != null)
            {
                // Active vocal note!
                if (config.EnableMicroStagger && config.IncludeBassInSolo && (bassNote != null || harmonyNote != null))
                {
                    // AuraDj 12ms Micro-Stagger:
                    // Bass/Rhythm strikes first at T, Vocal Lead strikes at T + MicroStaggerMs (12ms)!
                    var accomp = bassNote ?? harmonyNote;
                    if (accomp != null && Math.Abs(vocalNote.MidiNote - accomp.MidiNote) >= 5)
                    {
                        var staggeredAccomp = new TimedRawNote
                        {
                            StartTimeMs = vocalNote.StartTimeMs,
                            EndTimeMs = vocalNote.StartTimeMs + Math.Max(config.MinNoteDurationMs, config.MicroStaggerMs),
                            MidiNote = accomp.MidiNote,
                            Velocity = (int)(accomp.Velocity * 0.8),
                            Channel = accomp.Channel,
                            Role = accomp.Role
                        };
                        selected.Add(staggeredAccomp);

                        var staggeredVocal = new TimedRawNote
                        {
                            StartTimeMs = vocalNote.StartTimeMs + config.MicroStaggerMs,
                            EndTimeMs = Math.Max(vocalNote.StartTimeMs + config.MicroStaggerMs + config.MinNoteDurationMs, vocalNote.EndTimeMs),
                            MidiNote = vocalNote.MidiNote,
                            Velocity = vocalNote.Velocity,
                            Channel = vocalNote.Channel,
                            Role = vocalNote.Role
                        };
                        selected.Add(staggeredVocal);
                        lastVocalEndTime = Math.Max(lastVocalEndTime, staggeredVocal.EndTimeMs);
                        continue;
                    }
                }

                // Pure solo vocal note
                selected.Add(vocalNote);
                lastVocalEndTime = Math.Max(lastVocalEndTime, vocalNote.EndTimeMs);
            }
            else
            {
                // In between vocal phrases (lyrical rests): weave in accompaniment and bass!
                var winner = notesAtTime.OrderByDescending(n => n.CalculateSalience()).First();

                if (!config.DuckAccompanimentDuringVocals || winner.StartTimeMs >= lastVocalEndTime + config.MinNoteSpacingMs)
                {
                    selected.Add(winner);
                }

                if (config.IncludeBassInSolo && bassNote != null && bassNote != winner)
                {
                    if (bassNote.StartTimeMs >= lastVocalEndTime + config.MinNoteSpacingMs)
                    {
                        selected.Add(bassNote);
                    }
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
            FfxivInstrument.Saxophone,          // Bard 1: Vocal Lead (AuraDj signature)
            FfxivInstrument.ElectricGuitarOverdriven, // Bard 2: Rock Rhythm / Guitar
            FfxivInstrument.Piano,              // Bard 3: Keyboard / Harmony
            FfxivInstrument.DoubleBass,         // Bard 4: Bassline
            FfxivInstrument.Flute,              // Bard 5: Woodwind Lead
            FfxivInstrument.Violin,             // Bard 6: Strings Pad
            FfxivInstrument.ElectricGuitarClean,// Bard 7: Clean Guitar
            FfxivInstrument.SnareDrum           // Bard 8: Drums / Percussion
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
                // If it's a micro-staggered pair (e.g. Bass at T, Vocal at T + 12ms), allow the stagger!
                if (!config.EnableMicroStagger || start < lastStartTime + Math.Min(config.MicroStaggerMs, 15))
                {
                    start = lastStartTime + config.MinNoteSpacingMs;
                }
            }

            if (result.Count > 0 && lastEndTime > start)
            {
                var prev = result[^1];
                // Vocal Protection: Never truncate an active vocal note for an accompaniment note
                if (prev.IsVocalLead && n.Role != TrackMusicalRole.VocalLead)
                {
                    continue; // Accompaniment yields to the vocal voice
                }
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
        if (song.SourceTimeDivision != null)
        {
            midiFile.TimeDivision = (TimeDivision)song.SourceTimeDivision.Clone();
        }

        var tempoMap = song.SourceTempoMap ?? TempoMap.Create(Tempo.FromBeatsPerMinute(song.PrimaryBpm));

        // Create Conductor / Tempo track to preserve original BPM, tempo shifts, and time signatures
        var conductorChunk = new TrackChunk();
        conductorChunk.Events.Add(new SequenceTrackNameEvent($"{song.Title} (Tempo Track)"));

        var tempoEvents = new List<ITimedObject>();
        foreach (var tempo in tempoMap.GetTempoChanges())
        {
            tempoEvents.Add(new TimedEvent(new SetTempoEvent(tempo.Value.MicrosecondsPerQuarterNote), tempo.Time));
        }

        foreach (var ts in tempoMap.GetTimeSignatureChanges())
        {
            byte denPower = (byte)Math.Max(0, (int)Math.Round(Math.Log2(ts.Value.Denominator)));
            tempoEvents.Add(new TimedEvent(new TimeSignatureEvent((byte)ts.Value.Numerator, denPower), ts.Time));
        }

        conductorChunk.AddObjects(tempoEvents);
        midiFile.Chunks.Add(conductorChunk);

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

                return new Note((SevenBitNumber)n.MidiNote, Math.Max(1, tickLength), tickStart)
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
