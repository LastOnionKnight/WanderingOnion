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
        var tempoMap = midi.GetTempoMap();
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

            var suggested = FfxivInstrument.MatchByNameOrProgram(trackName, (int)programChange);

            results.Add(new TrackAnalysis
            {
                TrackIndex = i,
                Name = trackName,
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

        // Candidate range: -36 to +36 semitones
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
                    // Distance penalty
                    if (target < FfxivPitch.MinMidiNote) totalDist += FfxivPitch.MinMidiNote - target;
                    else totalDist += target - FfxivPitch.MaxMidiNote;
                }
            }

            int score = (inRange * 1000) - totalDist;

            // Give bonus to full octave shifts (0, +-12, +-24) to keep musical key intact
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
