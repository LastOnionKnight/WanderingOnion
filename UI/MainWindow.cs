using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Melanchall.DryWetMidi.Core;
using WanderingOnion.Core.Arranger;
using WanderingOnion.Core.Audio;
using WanderingOnion.Core.Playback;

namespace WanderingOnion.UI;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration _config;
    private readonly SongPlayer _player;

    // Library state
    private string _searchFilter = string.Empty;
    private List<FileInfo> _songFiles = new();
    private FileInfo? _selectedFile;

    // Arranger & Conversion state
    private MidiFile? _loadedRawMidi;
    private List<TrackAnalysis> _currentAnalysis = new();
    private ConvertedSong? _convertedSong;
    private readonly ArrangementConfig _arrangementConfig = new();
    private string _statusMessage = "Ready. Select a MIDI to begin.";
    private int _lastTriggeredKey = -1;

    public MainWindow(Configuration config, SongPlayer player)
        : base("Wandering Onion - FFXIV Bard Engine###WanderingOnionMain", ImGuiWindowFlags.None)
    {
        _config = config;
        _player = player;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 500),
            MaximumSize = new Vector2(1400, 1000)
        };

        _player.NoteTriggered += (k) => _lastTriggeredKey = k;
        RefreshSongList();
    }

    public void RefreshSongList()
    {
        try
        {
            if (Directory.Exists(_config.SongsDirectory))
            {
                var dir = new DirectoryInfo(_config.SongsDirectory);
                _songFiles = dir.EnumerateFiles("*.mid", SearchOption.AllDirectories)
                    .Concat(dir.EnumerateFiles("*.midi", SearchOption.AllDirectories))
                    .OrderBy(f => f.Name)
                    .ToList();
            }
            else
            {
                _songFiles.Clear();
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error scanning songs: {ex.Message}";
        }
    }

    public override void Draw()
    {
        DrawStatusBar();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##WanderingOnionTabs"))
        {
            if (ImGui.BeginTabItem("Library & Files"))
            {
                DrawLibraryTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Smart Arranger & Converter"))
            {
                DrawArrangerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Live Performance"))
            {
                DrawPerformanceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawStatusBar()
    {
        bool inPerf = PerformanceInput.IsInPerformanceMode(out bool isMini, out _);
        if (inPerf)
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), $"[Performance Active: {(isMini ? "Compact" : "Wide 3-Octave")}]");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "[Performance Inactive - Open Bard Performance in-game]");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextUnformatted(_statusMessage);

        ImGui.SameLine(ImGui.GetWindowWidth() - 220);
        if (ImGui.Button("Metronome Ready"))
        {
            PerformanceInput.BeginReadyCheck();
        }
        ImGui.SameLine();
        if (ImGui.Button("Accept"))
        {
            PerformanceInput.ConfirmReceiveReadyCheck();
        }
    }

    private void DrawLibraryTab()
    {
        ImGui.Text($"Songs Directory: {_config.SongsDirectory}");
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) RefreshSongList();

        ImGui.SetNextItemWidth(400);
        ImGui.InputTextWithHint("##Search", "Search songs by name...", ref _searchFilter, 128);

        ImGui.Separator();

        var filtered = string.IsNullOrWhiteSpace(_searchFilter)
            ? _songFiles
            : _songFiles.Where(f => f.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        ImGui.TextDisabled($"Showing {filtered.Count} of {_songFiles.Count} songs");

        if (ImGui.BeginListBox("##SongListBox", new Vector2(-1, -1)))
        {
            foreach (var file in filtered)
            {
                bool isSelected = _selectedFile == file;
                if (ImGui.Selectable($"{file.Name} ({file.Length / 1024} KB)##{file.FullName}", isSelected))
                {
                    LoadSelectedFile(file);
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndListBox();
        }
    }

    private void LoadSelectedFile(FileInfo file)
    {
        try
        {
            _selectedFile = file;
            _loadedRawMidi = MidiFile.Read(file.FullName);
            _currentAnalysis = MidiAnalyzer.AnalyzeMidi(_loadedRawMidi);

            // Auto-arrange immediately with current settings
            RunAutoConversion();
            _statusMessage = $"Loaded and arranged: {file.Name}";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Failed to load {file.Name}: {ex.Message}";
        }
    }

    private void RunAutoConversion()
    {
        if (_loadedRawMidi == null || _selectedFile == null) return;

        _convertedSong = MidiAutoConverter.Convert(_loadedRawMidi, _arrangementConfig, _selectedFile.Name);
        _player.LoadSong(_convertedSong, 0);
    }

    private void DrawArrangerTab()
    {
        if (_loadedRawMidi == null || _convertedSong == null)
        {
            ImGui.TextDisabled("No song currently loaded. Select a song from the Library tab first.");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), $"Arranging: {_convertedSong.Title}");
        ImGui.TextDisabled($"Original Tracks: {_currentAnalysis.Count} | Applied Transposition: {_convertedSong.AppliedShift:+0;-0;0} semitones");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Arrangement Controls");

        // Mode: Solo vs Band
        int modeIndex = (int)_arrangementConfig.Mode;
        if (ImGui.RadioButton("Solo Bard", ref modeIndex, 0))
        {
            _arrangementConfig.Mode = ArrangementMode.Solo;
            RunAutoConversion();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Band Ensemble", ref modeIndex, 1))
        {
            _arrangementConfig.Mode = ArrangementMode.Band;
            RunAutoConversion();
        }

        if (_arrangementConfig.Mode == ArrangementMode.Band)
        {
            int bandSize = _arrangementConfig.BandSize;
            if (ImGui.SliderInt("Band Size (Players)", ref bandSize, 2, 8))
            {
                _arrangementConfig.BandSize = bandSize;
                RunAutoConversion();
            }
        }
        else
        {
            bool bass = _arrangementConfig.IncludeBassInSolo;
            if (ImGui.Checkbox("Include Bass Alternation (Solo)", ref bass))
            {
                _arrangementConfig.IncludeBassInSolo = bass;
                RunAutoConversion();
            }
        }

        ImGui.Spacing();
        bool autoTrans = _arrangementConfig.AutoTranspose;
        if (ImGui.Checkbox("Auto-Fit to 3 Octaves (C3..C6)", ref autoTrans))
        {
            _arrangementConfig.AutoTranspose = autoTrans;
            RunAutoConversion();
        }

        int manualShift = _arrangementConfig.ManualSemitoneShift;
        if (ImGui.SliderInt("Manual Transpose", ref manualShift, -24, 24, "%+d semitones"))
        {
            _arrangementConfig.ManualSemitoneShift = manualShift;
            _arrangementConfig.AutoTranspose = false;
            RunAutoConversion();
        }

        int spacing = _arrangementConfig.MinNoteSpacingMs;
        if (ImGui.SliderInt("Min Note Spacing (Anti-Choke)", ref spacing, 25, 100, "%d ms"))
        {
            _arrangementConfig.MinNoteSpacingMs = spacing;
            RunAutoConversion();
        }

        ImGui.Spacing();
        if (ImGui.Button("Re-Convert / Optimize Now", new Vector2(200, 30)))
        {
            RunAutoConversion();
        }

        ImGui.SameLine();
        if (ImGui.Button("Export Arranged FFXIV MIDI (.mid)", new Vector2(250, 30)))
        {
            ExportArrangedMidi();
        }

        ImGui.Separator();
        ImGui.Text("Resulting Playable Tracks:");

        if (ImGui.BeginTable("##ArrangedTracksTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Track Role", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Instrument", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _convertedSong.Tracks.Count; i++)
            {
                var t = _convertedSong.Tracks[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"#{i + 1}");

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(t.TrackName);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(t.Instrument.Name);

                ImGui.TableSetColumnIndex(3);
                ImGui.Text($"{t.Notes.Count}");
            }
            ImGui.EndTable();
        }
    }

    private void ExportArrangedMidi()
    {
        if (_convertedSong == null || _selectedFile == null) return;
        try
        {
            var outDir = Path.Combine(_selectedFile.DirectoryName ?? "D:\\", "Playable_FFXIV_Export");
            Directory.CreateDirectory(outDir);
            var safeName = Path.GetFileNameWithoutExtension(_selectedFile.Name) + "_FFXIV_Playable.mid";
            var outPath = Path.Combine(outDir, safeName);

            MidiAutoConverter.ExportToMidiFile(_convertedSong, outPath);
            _statusMessage = $"Exported to: {outPath}";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Export failed: {ex.Message}";
        }
    }

    private void DrawPerformanceTab()
    {
        if (_convertedSong == null)
        {
            ImGui.TextDisabled("No song loaded to perform.");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), $"Now Playing: {_convertedSong.Title}");

        // Track selector for ensemble
        int curTrack = _player.ActiveTrackIndex;
        var trackNames = _convertedSong.Tracks.Select(t => t.TrackName).ToArray();
        ImGui.SetNextItemWidth(300);
        if (ImGui.Combo("Active Bard Track", ref curTrack, trackNames, trackNames.Length))
        {
            _player.ActiveTrackIndex = curTrack;
        }

        ImGui.Spacing();

        // Progress bar
        float prog = (float)_player.Progress;
        var curSec = _player.CurrentPositionMs / 1000;
        var totalSec = _player.TotalDurationMs / 1000;
        ImGui.ProgressBar(prog, new Vector2(-1, 20), $"{curSec / 60:D2}:{curSec % 60:D2} / {totalSec / 60:D2}:{totalSec % 60:D2}");

        // Transport buttons
        ImGui.Spacing();
        if (_player.State == SongPlayer.PlaybackState.Playing)
        {
            if (ImGui.Button("Pause", new Vector2(90, 32))) _player.Pause();
        }
        else
        {
            if (ImGui.Button("Play", new Vector2(90, 32))) _player.Play();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(90, 32))) _player.Stop();

        ImGui.SameLine();
        float speed = (float)_player.SpeedMultiplier;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderFloat("Speed", ref speed, 0.5f, 2.0f, "%.2fx"))
        {
            _player.SpeedMultiplier = speed;
        }

        // Live Keyboard display
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("3-Octave Performance Keyboard (C3..C6)");
        DrawKeyboardVisualizer();
    }

    private void DrawKeyboardVisualizer()
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float keyWidth = 18f;
        float whiteHeight = 90f;
        float blackHeight = 55f;
        float blackWidth = 12f;

        // Draw 37 keys
        int whiteIndex = 0;

        for (int i = 0; i < 37; i++)
        {
            int semitone = i % 12;
            bool isBlack = semitone == 1 || semitone == 3 || semitone == 6 || semitone == 8 || semitone == 10;
            if (!isBlack)
            {
                var pMin = origin + new Vector2(whiteIndex * keyWidth, 0);
                var pMax = pMin + new Vector2(keyWidth - 1, whiteHeight);

                uint col = (_lastTriggeredKey == i)
                    ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.8f, 1f, 1f))
                    : ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.9f, 1f));

                drawList.AddRectFilled(pMin, pMax, col);
                drawList.AddRect(pMin, pMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 1f)));
                whiteIndex++;
            }
        }

        // Black keys overlay
        whiteIndex = 0;
        for (int i = 0; i < 37; i++)
        {
            int semitone = i % 12;
            bool isBlack = semitone == 1 || semitone == 3 || semitone == 6 || semitone == 8 || semitone == 10;
            if (!isBlack)
            {
                whiteIndex++;
            }
            else
            {
                var pMin = origin + new Vector2((whiteIndex * keyWidth) - (blackWidth / 2f), 0);
                var pMax = pMin + new Vector2(blackWidth, blackHeight);

                uint col = (_lastTriggeredKey == i)
                    ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.8f, 1f, 1f))
                    : ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.15f, 1f));

                drawList.AddRectFilled(pMin, pMax, col);
                drawList.AddRect(pMin, pMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.05f, 0.05f, 1f)));
            }
        }

        ImGui.Dummy(new Vector2(whiteIndex * keyWidth, whiteHeight + 10));
    }

    private void DrawSettingsTab()
    {
        var dir = _config.SongsDirectory;
        if (ImGui.InputText("Songs Folder", ref dir, 260))
        {
            _config.SongsDirectory = dir;
            _config.Save();
            RefreshSongList();
        }

        int defBand = _config.DefaultBandMemberCount;
        if (ImGui.SliderInt("Default Band Size", ref defBand, 2, 8))
        {
            _config.DefaultBandMemberCount = defBand;
            _config.Save();
        }

        int noteSpacing = _config.MinNoteSpacingMs;
        if (ImGui.SliderInt("Default Note Spacing", ref noteSpacing, 25, 100, "%d ms"))
        {
            _config.MinNoteSpacingMs = noteSpacing;
            _config.Save();
        }
    }

    public void Dispose()
    {
    }
}
