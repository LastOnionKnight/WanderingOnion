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
    /// Remaps standard General MIDI Channel 10 percussion note numbers to native FFXIV drum key pitches:
    /// Bass Drum: 48 (C3), Snare: 50 (D3), Cymbal/HiHat: 52 (E3), Bongo: 53 (F3), Timpani/Toms: 55 (G3).
    /// </summary>
    public static int RemapGeneralMidiDrumToFfxiv(int gmDrumNote)
    {
        return gmDrumNote switch
        {
            // Bass Drum / Kick
            35 or 36 => 48, // C3 Bass Drum

            // Snare Drums / Side Stick / Claps
            37 or 38 or 39 or 40 => 50, // D3 Snare Drum

            // Hi-Hats & Cymbals (Closed/Open HiHat, Pedal, Crash, Ride, Splash, China)
            42 or 44 or 46 or 49 or 51 or 52 or 53 or 55 or 57 or 59 => 52, // E3 Cymbal

            // Toms / Timpani
            41 or 43 or 45 or 47 or 48 or 50 => 55, // G3 Timpani

            // Bongos / Congas / Agogo / Timbale / Tambourine / Woodblock
            54 or 56 or 58 or 60 or 61 or 62 or 63 or 64 or 65 or 66 or 67 or 68 or 69 or 70 or 75 or 76 => 53, // F3 Bongo

            // Default fallback
            _ => 50 // D3 Snare fallback
        };
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
