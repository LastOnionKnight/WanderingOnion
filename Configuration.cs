using System;
using Dalamud.Configuration;

namespace WanderingOnion;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string SongsDirectory { get; set; } = @"D:\TOOLS\FF Bard Music Player\Songs";
    
    // Auto-Arranger / Converter settings
    public bool AutoTransposeToRange { get; set; } = true;
    public bool PreferHarmonicOctaves { get; set; } = true;
    public bool AutoDechokeChords { get; set; } = true;
    public int MinNoteSpacingMs { get; set; } = 50;
    public bool EnableSkylineMelody { get; set; } = true;
    public int DefaultBandMemberCount { get; set; } = 4;
    
    // Playback settings
    public bool AutoSwitchTone { get; set; } = true;
    public bool SyncEnsembleWithParty { get; set; } = true;

    public void Save()
    {
        DalamudServices.PluginInterface.SavePluginConfig(this);
    }
}
