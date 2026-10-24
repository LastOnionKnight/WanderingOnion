using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;

namespace WanderingOnion.Core.Arranger;

/// <summary>
/// Utility to intelligently merge multi-stem DAW exports (e.g. Stem (Vocals).mid, Stem (Guitar).mid)
/// into a unified multi-track MIDI file while preserving correct TimeDivision, TempoMap, and track names.
/// </summary>
public static class StemMerger
{
    private static readonly ReadingSettings SafeSettings = new()
    {
        InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
        MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
        SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOff,
        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
        InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
        NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
        NoHeaderChunkPolicy = NoHeaderChunkPolicy.Ignore
    };

    /// <summary>
    /// Merges multiple stem MIDI files into a single unified MIDI file.
    /// Preserves the source TimeDivision and all tempo/time-signature events.
    /// </summary>
    public static MidiFile MergeStemFiles(IEnumerable<string> filePaths, string songTitle)
    {
        var pathList = filePaths.Where(File.Exists).ToList();
        if (pathList.Count == 0)
        {
            throw new ArgumentException("No valid stem files provided to merge.", nameof(filePaths));
        }

        // Read first file to extract base TimeDivision and conductor track
        var firstMidi = MidiFile.Read(pathList.First(), SafeSettings);
        var mergedMidi = new MidiFile
        {
            TimeDivision = (TimeDivision)firstMidi.TimeDivision.Clone()
        };

        // Extract Conductor / Tempo events from the stems (if any track has tempo events)
        var conductorTrack = new TrackChunk();
        conductorTrack.Events.Add(new SequenceTrackNameEvent($"{songTitle} (Tempo Track)"));

        var tempoEventsSeen = new HashSet<string>();
        foreach (var path in pathList)
        {
            try
            {
                var midi = MidiFile.Read(path, SafeSettings);
                foreach (var chunk in midi.GetTrackChunks())
                {
                    foreach (var e in chunk.Events)
                    {
                        if (e is SetTempoEvent ste)
                        {
                            var key = $"{e.DeltaTime}_{ste.MicrosecondsPerQuarterNote}";
                            if (tempoEventsSeen.Add(key))
                            {
                                conductorTrack.Events.Add((MidiEvent)e.Clone());
                            }
                        }
                        else if (e is TimeSignatureEvent tse)
                        {
                            var key = $"{e.DeltaTime}_{tse.Numerator}_{tse.Denominator}";
                            if (tempoEventsSeen.Add(key))
                            {
                                conductorTrack.Events.Add((MidiEvent)e.Clone());
                            }
                        }
                    }
                }
            }
            catch
            {
                // Continue reading other stems
            }
        }

        if (conductorTrack.Events.Count > 1)
        {
            mergedMidi.Chunks.Add(conductorTrack);
        }

        // Add tracks from each stem
        foreach (var path in pathList)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                int parenIdx = fileName.IndexOf('(');
                string stemName = parenIdx >= 0 ? fileName.Substring(parenIdx).Trim('(', ')') : fileName;

                var midi = MidiFile.Read(path, SafeSettings);
                foreach (var chunk in midi.GetTrackChunks())
                {
                    var noteCount = chunk.Events.Count(e => e is NoteOnEvent);
                    if (noteCount == 0) continue; // Skip empty tracks

                    var copy = (TrackChunk)chunk.Clone();

                    // Ensure track has the stem name
                    var existingName = copy.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault();
                    if (existingName != null)
                    {
                        existingName.Text = $"{stemName} - {existingName.Text}";
                    }
                    else
                    {
                        copy.Events.Insert(0, new SequenceTrackNameEvent(stemName));
                    }

                    mergedMidi.Chunks.Add(copy);
                }
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, $"Failed to read stem file: {path}");
            }
        }

        return mergedMidi;
    }
}
