using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace WanderingOnion.Core.Playback;

public static class PerformanceInput
{
    private static unsafe nint GetWindowByName(string s) => (nint)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(s);

    private static unsafe bool SendAction(nint ptr, params ulong[] param)
    {
        if (ptr == nint.Zero || param.Length % 2 != 0) return false;
        var pairCount = param.Length / 2;
        fixed (ulong* u = param)
        {
            AtkUnitBase.MemberFunctionPointers.FireCallback((AtkUnitBase*)ptr, (uint)pairCount, (AtkValue*)u, true);
        }
        return true;
    }

    private static bool SendActionToWindow(string windowName, params ulong[] param)
    {
        var ptr = GetWindowByName(windowName);
        if (ptr == nint.Zero) return false;
        return SendAction(ptr, param);
    }

    public static bool IsInPerformanceMode(out bool isMiniMode, out nint windowPtr)
    {
        // Check wide mode first (standard 3-octave view)
        windowPtr = GetWindowByName("PerformanceModeWide");
        if (windowPtr != nint.Zero)
        {
            isMiniMode = false;
            return true;
        }

        // Check compact mode
        windowPtr = GetWindowByName("PerformanceMode");
        if (windowPtr != nint.Zero)
        {
            isMiniMode = true;
            return true;
        }

        isMiniMode = false;
        windowPtr = nint.Zero;
        return false;
    }

    public static bool PlayNoteOn(int ffxivKeyIndex)
    {
        if (!IsInPerformanceMode(out var isMiniMode, out var windowPtr)) return false;

        var key = ffxivKeyIndex;
        if (isMiniMode)
        {
            // Mini mode only has keys 0..12. Adjust octave shift if needed
            key = Math.Clamp(key % 12, 0, 12);
        }

        return SendAction(windowPtr, 3, 1, 4, (ulong)key);
    }

    public static bool PlayNoteOff(int ffxivKeyIndex)
    {
        if (!IsInPerformanceMode(out var isMiniMode, out var windowPtr)) return false;

        var key = ffxivKeyIndex;
        if (isMiniMode)
        {
            key = Math.Clamp(key % 12, 0, 12);
        }

        return SendAction(windowPtr, 3, 2, 4, (ulong)key);
    }

    public static bool SwitchGuitarTone(int toneIndex)
    {
        var ptr = GetWindowByName("PerformanceToneChange");
        if (ptr == nint.Zero) return false;
        return SendAction(ptr, 3, 0, 3, (ulong)toneIndex);
    }

    public static bool BeginReadyCheck() => SendActionToWindow("PerformanceMetronome", 3, 2, 2, 0);
    public static bool ConfirmReadyCheck() => SendActionToWindow("PerformanceReadyCheck", 3, 2);
    public static bool ConfirmReceiveReadyCheck() => SendActionToWindow("PerformanceReadyCheckReceive", 3, 2);
}
