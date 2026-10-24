using System;
using System.Collections.Generic;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using WanderingOnion.Core.Audio;

namespace WanderingOnion.Core.Arranger;

public class MidiAnalyzer
{
    public static List<TrackAnalysis> AnalyzeMidi(MidiFile midi)
    {
        var tracks = midi.GetTrackChunks().ToList();
        var results = new List<TrackAnalysis>();

        for (int i = 0; i < tracks.Count; i++)
        {
            var chunk = tracks[i];
            var notes = chunk.GetNotes().ToList();
            if (notes.Count == 0) continue;

            var trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text ?? $"Track {i + 1}";
            var programChange = chunk.Events.OfType<ProgramChangeEvent>().FirstOrDefault()?.ProgramNumber ?? -1;

            var pitches = notes.Select(n => (int)n.NoteNumber).ToList();
            var minPitch = pitches.Min();
            var maxPitch = pitches.Max();
            var avgPitch = pitches.Average();
            var inRange = pitches.Count(p => FfxivPitch.IsInRange(p));

            // Estimate polyphony by scanning overlapping note intervals
            int maxPolyphony = 1;
            var ordered = notes.OrderBy(n => n.Time).ToList();
            for (int j = 0; j < ordered.Count; j++)
            {
                var cur = ordered[j];
                var overlap = ordered.Skip(j + 1).TakeWhile(n => n.Time < cur.Time + cur.Length).Count() + 1;
                if (overlap > maxPolyphony) maxPolyphony = overlap;
            }

            var detectedRole = DetectMusicalRole(trackName, (int)programChange, avgPitch, maxPolyphony);
            var suggested = SuggestInstrumentForRole(detectedRole, trackName, (int)programChange);

            results.Add(new TrackAnalysis
            {
                TrackIndex = i,
                Name = trackName,
                DetectedRole = detectedRole,
                NoteCount = notes.Count,
                MinPitch = minPitch,
                MaxPitch = maxPitch,
                AveragePitch = avgPitch,
                InRangeCount = inRange,
                OutOfRangeCount = notes.Count - inRange,
                MaxPolyphony = maxPolyphony,
                ProgramNumber = (int)programChange,
                SuggestedInstrument = suggested
            });
        }

        return results;
    }

    public static TrackMusicalRole DetectMusicalRole(string trackName, int program, double avgPitch, int polyphony)
    {
        var name = trackName.ToLowerInvariant();

        if (name.Contains("fx") || name.Contains("effect")) return TrackMusicalRole.SoundEffects;
        if (name.Contains("drum") || name.Contains("perc") || name.Contains("cymbal") || name.Contains("snare") || name.Contains("hihat"))
            return TrackMusicalRole.DrumsPercussion;

        if (name.Contains("vocal") || name.Contains("vox") || name.Contains("lead") || name.Contains("voice") || name.Contains("sing"))
            return TrackMusicalRole.VocalLead;

        if (name.Contains("bass") || name.Contains("tuba") || avgPitch < 45)
            return TrackMusicalRole.Bass;

        if (name.Contains("woodwind") || name.Contains("flute") || name.Contains("sax") || name.Contains("violin") || name.Contains("oboe") || name.Contains("trumpet"))
            return TrackMusicalRole.InstrumentMelody;

        // If mostly monophonic in vocal range [55..75]
        if (polyphony <= 2 && avgPitch >= 55 && avgPitch <= 75)
            return TrackMusicalRole.InstrumentMelody;

        return TrackMusicalRole.Harmony;
    }

    private static FfxivInstrument SuggestInstrumentForRole(TrackMusicalRole role, string name, int program)
    {
        switch (role)
        {
            case TrackMusicalRole.VocalLead:
                return FfxivInstrument.Saxophone; // AuraDj signature vocal instrument with natural vibrato
            case TrackMusicalRole.InstrumentMelody:
                return FfxivInstrument.Flute;
            case TrackMusicalRole.Bass:
                return FfxivInstrument.DoubleBass;
            case TrackMusicalRole.DrumsPercussion:
                return FfxivInstrument.SnareDrum;
            case TrackMusicalRole.SoundEffects:
                return FfxivInstrument.ElectricGuitarSpecial;
            default:
                return FfxivInstrument.MatchByNameOrProgram(name, program);
        }
    }

    /// <summary>
    /// Finds the exact octave transposition (0, +-12, +-24, +-36) that keeps 100% of the vocal/melody line
    /// within FFXIV's 3-octave span (48..84) without inverting any melody intervals.
    /// </summary>
    public static int FindVocalCentricShift(IEnumerable<int> vocalPitches)
    {
        var pitches = vocalPitches.ToList();
        if (pitches.Count == 0) return 0;

        int min = pitches.Min();
        int max = pitches.Max();

        // 1. First test pure octave shifts (-36, -24, -12, 0, +12, +24, +36)
        int[] octaveCandidates = { 0, 12, -12, 24, -24, 36, -36 };
        foreach (var oct in octaveCandidates)
        {
            if (min + oct >= FfxivPitch.MinMidiNote && max + oct <= FfxivPitch.MaxMidiNote)
            {
                // Perfect fit! 100% of vocal melody preserved in natural octave without touching key signature
                return oct;
            }
        }

        // 2. If range is wide, find the octave shift that maximizes in-range notes while centering the melody
        int bestOctave = 0;
        int maxInBounds = -1;
        double bestDistToCenter = double.MaxValue;

        foreach (var oct in octaveCandidates)
        {
            int inBounds = pitches.Count(p => p + oct >= FfxivPitch.MinMidiNote && p + oct <= FfxivPitch.MaxMidiNote);
            double avg = pitches.Average(p => p + oct);
            double distToCenter = Math.Abs(avg - 66.0); // 66 = F#4 (middle of C3..C6)

            if (inBounds > maxInBounds || (inBounds == maxInBounds && distToCenter < bestDistToCenter))
            {
                maxInBounds = inBounds;
                bestDistToCenter = distToCenter;
                bestOctave = oct;
            }
        }

        return bestOctave;
    }

    /// <summary>
    /// Searches for the optimal transposition shift in semitones to maximize notes in [48..84].
    /// Prefers octave shifts (0, +-12, +-24) to maintain the original musical key.
    /// </summary>
    public static int FindOptimalShift(IEnumerable<int> pitches, bool preferHarmonicOctaves = true)
    {
        var pitchList = pitches.ToList();
        if (pitchList.Count == 0) return 0;

        int bestShift = 0;
        int bestScore = int.MinValue;

        for (int shift = -36; shift <= 36; shift++)
        {
            int inRange = 0;
            int totalDist = 0;

            foreach (var p in pitchList)
            {
                int target = p + shift;
                if (target >= FfxivPitch.MinMidiNote && target <= FfxivPitch.MaxMidiNote)
                {
                    inRange++;
                }
                else
                {
                    if (target < FfxivPitch.MinMidiNote) totalDist += FfxivPitch.MinMidiNote - target;
                    else totalDist += target - FfxivPitch.MaxMidiNote;
                }
            }

            int score = (inRange * 1000) - totalDist;

            if (preferHarmonicOctaves && shift % 12 == 0)
            {
                score += (shift == 0) ? 500 : 350;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestShift = shift;
            }
        }

        return bestShift;
    }
}
