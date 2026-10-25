using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WanderingOnion.Core.Audio;

public enum InstrumentCategory
{
    Strummed,
    Wind,
    Brass,
    Strings,
    Drums,
    ElectricGuitar
}

public class FfxivInstrument
{
    public string Name { get; }
    public int Id { get; }
    public int GeneralMidiProgram { get; }
    public int LowerNote { get; } // MIDI note number, e.g. 48 for C3
    public int UpperNote { get; } // MIDI note number, e.g. 84 for C6
    public bool IsSustained { get; }
    public InstrumentCategory Category { get; }
    public IReadOnlyList<string> Aliases { get; }

    public FfxivInstrument(
        string name,
        int id,
        int generalMidiProgram,
        int lowerNote,
        int upperNote,
        bool isSustained,
        InstrumentCategory category,
        params string[] aliases)
    {
        Name = name;
        Id = id;
        GeneralMidiProgram = generalMidiProgram;
        LowerNote = lowerNote;
        UpperNote = upperNote;
        IsSustained = isSustained;
        Category = category;
        Aliases = aliases;
    }

    // Standard FFXIV 3-octave span: C3 (48) to C6 (84)
    public const int DefaultLowerNote = 48; // C3
    public const int DefaultUpperNote = 84; // C6
    public const int NoteSpan = 37;         // 3 octaves + 1 note (inclusive)

    // Strummed
    public static readonly FfxivInstrument Harp = new("Harp", 1, 46, 48, 84, false, InstrumentCategory.Strummed, "harp", "orchestralharp");
    public static readonly FfxivInstrument Piano = new("Piano", 2, 0, 48, 84, false, InstrumentCategory.Strummed, "piano", "acousticgrandpiano", "grandpiano");
    public static readonly FfxivInstrument Lute = new("Lute", 3, 24, 36, 72, false, InstrumentCategory.Strummed, "lute", "acousticguitar");
    public static readonly FfxivInstrument Fiddle = new("Fiddle", 4, 45, 36, 72, false, InstrumentCategory.Strummed, "fiddle", "pizzicato");

    // Wind
    public static readonly FfxivInstrument Flute = new("Flute", 5, 73, 60, 96, true, InstrumentCategory.Wind, "flute");
    public static readonly FfxivInstrument Oboe = new("Oboe", 6, 68, 60, 96, true, InstrumentCategory.Wind, "oboe");
    public static readonly FfxivInstrument Clarinet = new("Clarinet", 7, 71, 48, 84, true, InstrumentCategory.Wind, "clarinet");
    public static readonly FfxivInstrument Fife = new("Fife", 8, 72, 72, 108, true, InstrumentCategory.Wind, "fife", "piccolo");
    public static readonly FfxivInstrument Panpipes = new("Panpipes", 9, 75, 60, 96, true, InstrumentCategory.Wind, "panpipes", "panflute");

    // Drums / Percussion
    public static readonly FfxivInstrument Timpani = new("Timpani", 10, 47, 36, 72, false, InstrumentCategory.Drums, "timpani");
    public static readonly FfxivInstrument Bongo = new("Bongo", 11, 116, 48, 84, false, InstrumentCategory.Drums, "bongo", "bongos");
    public static readonly FfxivInstrument BassDrum = new("BassDrum", 12, 117, 36, 72, false, InstrumentCategory.Drums, "bassdrum", "kick");
    public static readonly FfxivInstrument SnareDrum = new("SnareDrum", 13, 115, 48, 84, false, InstrumentCategory.Drums, "snaredrum", "snare");
    public static readonly FfxivInstrument Cymbal = new("Cymbal", 14, 127, 48, 84, false, InstrumentCategory.Drums, "cymbal", "cymbals", "crash", "ride", "hihat");

    // Brass
    public static readonly FfxivInstrument Trumpet = new("Trumpet", 15, 56, 48, 84, true, InstrumentCategory.Brass, "trumpet");
    public static readonly FfxivInstrument Trombone = new("Trombone", 16, 57, 36, 72, true, InstrumentCategory.Brass, "trombone");
    public static readonly FfxivInstrument Tuba = new("Tuba", 17, 58, 24, 60, true, InstrumentCategory.Brass, "tuba");
    public static readonly FfxivInstrument Horn = new("Horn", 18, 60, 36, 72, true, InstrumentCategory.Brass, "frenchhorn", "horn");
    public static readonly FfxivInstrument Saxophone = new("Saxophone", 19, 65, 48, 84, true, InstrumentCategory.Brass, "saxophone", "sax", "altosax");

    // Strings
    public static readonly FfxivInstrument Violin = new("Violin", 20, 40, 48, 84, true, InstrumentCategory.Strings, "violin");
    public static readonly FfxivInstrument Viola = new("Viola", 21, 41, 48, 84, true, InstrumentCategory.Strings, "viola");
    public static readonly FfxivInstrument Cello = new("Cello", 22, 42, 36, 72, true, InstrumentCategory.Strings, "cello");
    public static readonly FfxivInstrument DoubleBass = new("DoubleBass", 23, 43, 24, 60, true, InstrumentCategory.Strings, "doublebass", "contrabass", "acousticbass", "electricbass");

    // Electric Guitar
    public static readonly FfxivInstrument ElectricGuitarOverdriven = new("Guitar (Overdriven)", 24, 29, 36, 72, true, InstrumentCategory.ElectricGuitar, "overdriven", "guitaroverdriven", "overdrive");
    public static readonly FfxivInstrument ElectricGuitarClean = new("Guitar (Clean)", 25, 27, 36, 72, true, InstrumentCategory.ElectricGuitar, "cleanguitar", "guitarclean", "clean");
    public static readonly FfxivInstrument ElectricGuitarMuted = new("Guitar (Muted)", 26, 28, 36, 72, false, InstrumentCategory.ElectricGuitar, "mutedguitar", "guitarmuted", "muted");
    public static readonly FfxivInstrument ElectricGuitarPowerChords = new("Guitar (Power Chords)", 27, 30, 24, 60, true, InstrumentCategory.ElectricGuitar, "powerchords", "powerchord");
    public static readonly FfxivInstrument ElectricGuitarSpecial = new("Guitar (Special)", 28, 31, 48, 84, false, InstrumentCategory.ElectricGuitar, "guitarspecial", "special");

    public int GuitarToneIndex => this.Id switch
    {
        24 => 0, // Overdriven
        25 => 1, // Clean
        26 => 2, // Muted
        27 => 3, // Power Chords
        28 => 4, // Special
        _ => -1
    };

    public static int ProgramToGuitarTone(int programNumber) => programNumber switch
    {
        29 => 0, // Overdriven
        27 => 1, // Clean
        28 => 2, // Muted
        30 => 3, // Power Chords
        31 => 4, // Special
        _ => -1
    };

    public static readonly IReadOnlyList<FfxivInstrument> All = new List<FfxivInstrument>
    {
        Harp, Piano, Lute, Fiddle,
        Flute, Oboe, Clarinet, Fife, Panpipes,
        Timpani, Bongo, BassDrum, SnareDrum, Cymbal,
        Trumpet, Trombone, Tuba, Horn, Saxophone,
        Violin, Viola, Cello, DoubleBass,
        ElectricGuitarOverdriven, ElectricGuitarClean, ElectricGuitarMuted, ElectricGuitarPowerChords, ElectricGuitarSpecial
    };

    private static readonly Regex TagRegex = new(@"[#\[]([A-Za-z0-9_\+\-\s\(\)]+)[#\]]", RegexOptions.Compiled);

    public static FfxivInstrument MatchByNameOrProgram(string name, int programNumber = -1, int? channel = null)
    {
        // 1. Channel 10 General MIDI Drums
        if (channel == 9 || channel == 10)
        {
            return SnareDrum;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            // 2. Check for BMP tags like #Overdriven# or [ElectricGuitarOverdriven]
            var tagMatch = TagRegex.Match(name);
            if (tagMatch.Success)
            {
                var tagContent = tagMatch.Groups[1].Value.Trim().ToLowerInvariant();
                var tagClean = Regex.Replace(tagContent, @"[^a-z0-9]", "");

                var byTag = All.FirstOrDefault(i =>
                {
                    var iClean = Regex.Replace(i.Name.ToLowerInvariant(), @"[^a-z0-9]", "");
                    return iClean == tagClean || iClean.Contains(tagClean) || tagClean.Contains(iClean) ||
                           i.Aliases.Any(a => tagClean.Contains(Regex.Replace(a, @"[^a-z0-9]", "")));
                });
                if (byTag != null) return byTag;
            }

            // 3. Fallback to broad substring matching on raw track name
            var clean = name.Trim().ToLowerInvariant();
            var byAlias = All.FirstOrDefault(i =>
                clean.Contains(i.Name.ToLowerInvariant()) ||
                i.Aliases.Any(a => clean.Contains(a)));
            if (byAlias != null) return byAlias;
        }

        if (programNumber >= 0)
        {
            var byProg = All.FirstOrDefault(i => i.GeneralMidiProgram == programNumber);
            if (byProg != null) return byProg;
        }

        return Harp; // Default fallback
    }
}
