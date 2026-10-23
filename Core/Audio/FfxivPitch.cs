namespace WanderingOnion.Core.Audio;

public static class FfxivPitch
{
    public const int MinMidiNote = 48; // C3
    public const int MaxMidiNote = 84; // C6
    public const int PitchCount = 37;

    /// <summary>
    /// Checks if a MIDI note is within the standard 3-octave FFXIV range (C3 to C6).
    /// </summary>
    public static bool IsInRange(int midiNote) => midiNote >= MinMidiNote && midiNote <= MaxMidiNote;

    /// <summary>
    /// Folds notes that exceed the range into the nearest valid octave.
    /// </summary>
    public static int ClampOrFoldToRange(int midiNote)
    {
        while (midiNote < MinMidiNote) midiNote += 12;
        while (midiNote > MaxMidiNote) midiNote -= 12;
        return midiNote;
    }

    /// <summary>
    /// Converts a MIDI note number (48..84) into the FFXIV key index (0..36).
    /// In PerformanceModeWide, key index 0 is C3, 12 is C4, 24 is C5, 36 is C6.
    /// </summary>
    public static int ToWideKeyIndex(int midiNote)
    {
        var clamped = ClampOrFoldToRange(midiNote);
        return clamped - MinMidiNote;
    }

    /// <summary>
    /// Note name for display (e.g., C3, C#3, D3...)
    /// </summary>
    private static readonly string[] NoteNames = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

    public static string GetNoteName(int midiNote)
    {
        if (midiNote < 0 || midiNote > 127) return "?";
        var octave = (midiNote / 12) - 1;
        var note = NoteNames[midiNote % 12];
        return $"{note}{octave}";
    }
}
