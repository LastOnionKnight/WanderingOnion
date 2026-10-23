using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WanderingOnion.Core.Arranger;

namespace WanderingOnion.Core.Playback;

public class SongPlayer : IDisposable
{
    public enum PlaybackState
    {
        Stopped,
        Playing,
        Paused
    }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public ConvertedSong? CurrentSong { get; private set; }
    public int ActiveTrackIndex { get; set; } = 0;
    public double SpeedMultiplier { get; set; } = 1.0;

    public long CurrentPositionMs { get; private set; }
    public long TotalDurationMs => CurrentSong?.TotalDurationMs ?? 0;
    public double Progress => TotalDurationMs > 0 ? (double)CurrentPositionMs / TotalDurationMs : 0;

    private readonly Stopwatch _stopwatch = new();
    private long _offsetMs = 0;
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;

    private readonly HashSet<int> _currentlyPlayingKeys = new();
    private readonly object _lock = new();

    public event Action? PlaybackStateChanged;
    public event Action<int>? NoteTriggered;

    public void LoadSong(ConvertedSong song, int trackIndex = 0)
    {
        Stop();
        lock (_lock)
        {
            CurrentSong = song;
            ActiveTrackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, song.Tracks.Count - 1));
            CurrentPositionMs = 0;
            _offsetMs = 0;
        }
        PlaybackStateChanged?.Invoke();
    }

    public void Play()
    {
        if (CurrentSong == null || CurrentSong.Tracks.Count == 0) return;

        if (State == PlaybackState.Playing) return;

        _cts = new CancellationTokenSource();
        _stopwatch.Restart();
        State = PlaybackState.Playing;
        PlaybackStateChanged?.Invoke();

        var token = _cts.Token;
        _playbackTask = Task.Run(() => PlaybackLoop(token), token);
    }

    public void Pause()
    {
        if (State != PlaybackState.Playing) return;

        _cts?.Cancel();
        _stopwatch.Stop();
        _offsetMs = CurrentPositionMs;
        ReleaseAllNotes();

        State = PlaybackState.Paused;
        PlaybackStateChanged?.Invoke();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _stopwatch.Reset();
        _offsetMs = 0;
        CurrentPositionMs = 0;
        ReleaseAllNotes();

        State = PlaybackState.Stopped;
        PlaybackStateChanged?.Invoke();
    }

    public void Seek(long targetMs)
    {
        bool wasPlaying = State == PlaybackState.Playing;
        if (wasPlaying) Pause();

        lock (_lock)
        {
            CurrentPositionMs = Math.Clamp(targetMs, 0, TotalDurationMs);
            _offsetMs = CurrentPositionMs;
        }

        if (wasPlaying) Play();
        else PlaybackStateChanged?.Invoke();
    }

    private void PlaybackLoop(CancellationToken token)
    {
        var track = CurrentSong?.Tracks.ElementAtOrDefault(ActiveTrackIndex);
        if (track == null)
        {
            Stop();
            return;
        }

        // Apply initial guitar tone if track has one
        if (track.Instrument.GuitarToneIndex >= 0)
        {
            PerformanceInput.SwitchGuitarTone(track.Instrument.GuitarToneIndex);
        }

        var notes = track.Notes.OrderBy(n => n.StartTimeMs).ToList();
        var toneChanges = track.ToneChanges.OrderBy(t => t.TimeMs).ToList();

        var activeNoteOffs = new PriorityQueue<PlayableNote, long>();

        int nextNoteIndex = notes.FindIndex(n => n.StartTimeMs >= _offsetMs);
        if (nextNoteIndex < 0) nextNoteIndex = notes.Count;

        int nextToneIndex = toneChanges.FindIndex(t => t.TimeMs >= _offsetMs);
        if (nextToneIndex < 0) nextToneIndex = toneChanges.Count;

        while (!token.IsCancellationRequested)
        {
            long elapsed = (long)(_stopwatch.ElapsedMilliseconds * SpeedMultiplier) + _offsetMs;
            CurrentPositionMs = elapsed;

            if (elapsed >= TotalDurationMs && activeNoteOffs.Count == 0)
            {
                Stop();
                break;
            }

            // 1. Process Auto-Tone Changes
            while (nextToneIndex < toneChanges.Count && toneChanges[nextToneIndex].TimeMs <= elapsed)
            {
                PerformanceInput.SwitchGuitarTone(toneChanges[nextToneIndex].ToneIndex);
                nextToneIndex++;
            }

            // 2. Fire note-offs that have expired
            while (activeNoteOffs.Count > 0 && activeNoteOffs.Peek().StartTimeMs + activeNoteOffs.Peek().DurationMs <= elapsed)
            {
                var offNote = activeNoteOffs.Dequeue();
                ReleaseNote(offNote.FfxivKeyIndex);
            }

            // 3. Fire note-ons ready at this timestamp
            while (nextNoteIndex < notes.Count && notes[nextNoteIndex].StartTimeMs <= elapsed)
            {
                var curNote = notes[nextNoteIndex];
                PressNote(curNote.FfxivKeyIndex);
                activeNoteOffs.Enqueue(curNote, curNote.StartTimeMs + curNote.DurationMs);
                nextNoteIndex++;
            }

            // High frequency spin/sleep (~1ms resolution)
            Thread.Sleep(1);
        }

        ReleaseAllNotes();
    }

    private void PressNote(int keyIndex)
    {
        lock (_lock)
        {
            if (_currentlyPlayingKeys.Contains(keyIndex))
            {
                PerformanceInput.PlayNoteOff(keyIndex);
                _currentlyPlayingKeys.Remove(keyIndex);
            }

            if (PerformanceInput.PlayNoteOn(keyIndex))
            {
                _currentlyPlayingKeys.Add(keyIndex);
                NoteTriggered?.Invoke(keyIndex);
            }
        }
    }

    private void ReleaseNote(int keyIndex)
    {
        lock (_lock)
        {
            if (_currentlyPlayingKeys.Remove(keyIndex))
            {
                PerformanceInput.PlayNoteOff(keyIndex);
            }
        }
    }

    private void ReleaseAllNotes()
    {
        lock (_lock)
        {
            foreach (var key in _currentlyPlayingKeys.ToList())
            {
                PerformanceInput.PlayNoteOff(key);
            }
            _currentlyPlayingKeys.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
