using System;

namespace Jfresolve.Services;

/// <summary>
/// Captures Jellyfin startTimeTicks from streaming API requests so TorBox HLS seek
/// can align with the client's scrub position.
/// </summary>
public static class SeekPositionCache
{
    private static readonly object Gate = new();
    private static long _pendingStartTicks;
    private static DateTime _pendingAt = DateTime.MinValue;
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(15);

    private static DateTime _seekRestartAt = DateTime.MinValue;

    public static void SetPending(long startTicks)
    {
        if (startTicks <= 0)
            return;

        lock (Gate)
        {
            _pendingStartTicks = startTicks;
            _pendingAt = DateTime.UtcNow;
        }
    }

    public static long? TryPeekPending()
    {
        lock (Gate)
        {
            if (_pendingStartTicks <= 0 || DateTime.UtcNow - _pendingAt > PendingWindow)
                return null;

            return _pendingStartTicks;
        }
    }

    public static long? TryConsumePending()
    {
        lock (Gate)
        {
            if (_pendingStartTicks <= 0 || DateTime.UtcNow - _pendingAt > PendingWindow)
            {
                _pendingStartTicks = 0;
                return null;
            }

            var ticks = _pendingStartTicks;
            _pendingStartTicks = 0;
            return ticks;
        }
    }

    public static void MarkSeekRestart()
    {
        lock (Gate)
        {
            _seekRestartAt = DateTime.UtcNow;
        }
    }

    public static void ClearSeekState()
    {
        lock (Gate)
        {
            _pendingStartTicks = 0;
            _pendingAt = DateTime.MinValue;
            _seekRestartAt = DateTime.MinValue;
        }
    }

    public static bool ShouldUseHlsPath()
    {
        lock (Gate)
        {
            if (_pendingStartTicks > 0 && DateTime.UtcNow - _pendingAt <= PendingWindow)
                return true;

            return DateTime.UtcNow - _seekRestartAt <= PendingWindow;
        }
    }
}
