using System;
using System.Collections.Generic;
using WanderingOnion.Core.Audio;

namespace WanderingOnion.Core.Arranger;

public enum ArrangementMode
{
    Solo,
    Band
}

public enum TrackMusicalRole
{
    VocalLead,
    InstrumentMelody,
    Harmony,
    Bass,
    DrumsPercussion,
    SoundEffects
}

public class ArrangementConfig
{
    public ArrangementMode Mode { get; set; } = ArrangementMode.Solo;
    public int BandSize { get; set; } = 4; // 2 to 8 players for band mode
    public bool AutoTranspose { get; set; } = true;
    public bool PreferHarmonicOctaves { get; set; } = true;
    public int ManualSemitoneShift { get; set; } = 0;
    public bool DechokeNotes { get; set; } = true;
    public int MinNoteSpacingMs { get; set; } = 50;
    public int MinNoteDurationMs { get; set; } = 30;
    public bool FoldOutOfRangeNotes { get; set; } = true;
    public bool EnableSkylineMelody { get; set; } = true;
    public bool IncludeBassInSolo { get; set; } = true;

    // Melody & Vocal Priority
    public bool PrioritizeVocals { get; set; } = true;
    public bool DuckAccompanimentDuringVocals { get; set; } = true;
    public int SelectedVocalTrackIndex { get; set; } = -1; // -1 = auto-detect
}

public class TrackAnalysis
{
    public int TrackIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public TrackMusicalRole DetectedRole { get; set; } = TrackMusicalRole.Harmony;
    public int NoteCount { get; set; }
    public int MinPitch { get; set; }
    public int MaxPitch { get; set; }
    public double AveragePitch { get; set; }
    public int InRangeCount { get; set; }
    public int OutOfRangeCount { get; set; }
    public double InRangePercentage => NoteCount > 0 ? (double)InRangeCount / NoteCount * 100.0 : 0;
    public int MaxPolyphony { get; set; }
    public int ProgramNumber { get; set; } = -1;
    public FfxivInstrument SuggestedInstrument { get; set; } = FfxivInstrument.Harp;
}

public class TimedToneChange
{
    public long TimeMs { get; set; }
    public int ToneIndex { get; set; }
}

public class ConvertedTrack
{
    public int VoiceIndex { get; set; }
    public string TrackName { get; set; } = string.Empty;
    public FfxivInstrument Instrument { get; set; } = FfxivInstrument.Harp;
    public List<PlayableNote> Notes { get; set; } = new();
    public List<TimedToneChange> ToneChanges { get; set; } = new();
}

public class PlayableNote
{
    public long StartTimeMs { get; set; }
    public long DurationMs { get; set; }
    public int MidiNote { get; set; }
    public int Velocity { get; set; }
    public bool IsVocalLead { get; set; }
    public int FfxivKeyIndex => FfxivPitch.ToWideKeyIndex(MidiNote);
}

public class ConvertedSong
{
    public string Title { get; set; } = string.Empty;
    public long TotalDurationMs { get; set; }
    public int AppliedShift { get; set; }
    public ArrangementMode Mode { get; set; }
    public double PrimaryBpm { get; set; } = 120.0;
    public Melanchall.DryWetMidi.Interaction.TempoMap? SourceTempoMap { get; set; }
    public Melanchall.DryWetMidi.Core.TimeDivision? SourceTimeDivision { get; set; }
    public List<ConvertedTrack> Tracks { get; set; } = new();
}
