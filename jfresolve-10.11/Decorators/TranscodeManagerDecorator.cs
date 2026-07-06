#nullable disable
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Services;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Decorators;

/// <summary>
/// Captures FFmpeg -ss seek offsets and fixes TorBox HLS remux args when Jellyfin still transcodes.
/// </summary>
public sealed class TranscodeManagerDecorator : ITranscodeManager
{
    private static readonly Regex TimeSeekRegex = new(
        @"-ss\s+(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericSeekRegex = new(
        @"-ss\s+([\d.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HevcBsfRegex = new(
        @"-bsf:v\s+hevc_mp4toannexb\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Dvh1TagRegex = new(
        @"-tag:v:0\s+dvh1\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AudioCopyCodecRegex = new(
        @"-codec:a:0\s+copy",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ITranscodeManager _inner;
    private readonly ILogger<TranscodeManagerDecorator> _log;

    public TranscodeManagerDecorator(ITranscodeManager inner, ILogger<TranscodeManagerDecorator> log)
    {
        _inner = inner;
        _log = log;
    }

    public TranscodingJob? GetTranscodingJob(string playSessionId) => _inner.GetTranscodingJob(playSessionId);

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
        => _inner.GetTranscodingJob(path, type);

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
        => _inner.PingTranscodingJob(playSessionId, isUserPaused);

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
        => _inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);

    public void ReportTranscodingProgress(
        TranscodingJob job,
        StreamState state,
        TimeSpan? transcodingPosition,
        float? framerate,
        double? percentComplete,
        long? bytesTranscoded,
        int? bitRate)
        => _inner.ReportTranscodingProgress(job, state, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);

    public async Task<TranscodingJob> StartFfMpeg(
        StreamState state,
        string outputPath,
        string commandLineArguments,
        Guid userId,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource,
        string? workingDirectory = null)
    {
        if (commandLineArguments.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase)
            && TryParseSeekTicks(commandLineArguments, out var seekTicks))
        {
            SeekPositionCache.SetPending(seekTicks);
            SeekPositionCache.MarkSeekRestart();
            _log.LogInformation(
                "Jfresolve: Captured FFmpeg seek {Seconds:F3}s before opening resolve URL",
                seekTicks / 10_000_000.0);
        }

        var fixedArgs = FixTorBoxHlsFfmpegArgs(commandLineArguments);
        if (!string.Equals(fixedArgs, commandLineArguments, StringComparison.Ordinal))
        {
            _log.LogInformation(
                "Jfresolve: Adjusted FFmpeg args for TorBox createstream HLS (codec + DTS segment fixes)");
            commandLineArguments = fixedArgs;
        }

        return await _inner.StartFfMpeg(
            state,
            outputPath,
            commandLineArguments,
            userId,
            transcodingJobType,
            cancellationTokenSource,
            workingDirectory);
    }

    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
        => _inner.OnTranscodeBeginRequest(path, type);

    public void OnTranscodeEndRequest(TranscodingJob job) => _inner.OnTranscodeEndRequest(job);

    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
        => _inner.LockAsync(outputPath, cancellationToken);

    private static bool TryParseSeekTicks(string args, out long ticks)
    {
        ticks = 0;
        var timeMatch = TimeSeekRegex.Match(args);
        if (timeMatch.Success)
        {
            var hours = int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(timeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            ticks = (long)((hours * 3600.0 + minutes * 60.0 + seconds) * 10_000_000.0);
            return ticks > 0;
        }

        var numericMatch = NumericSeekRegex.Match(args);
        if (numericMatch.Success
            && double.TryParse(numericMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
            && sec > 0)
        {
            ticks = (long)(sec * 10_000_000.0);
            return true;
        }

        return false;
    }

    private static string FixTorBoxHlsFfmpegArgs(string args)
    {
        if (!IsTorBoxHlsFfmpegInput(args))
            return args;

        var fixedArgs = HevcBsfRegex.Replace(args, string.Empty);
        fixedArgs = Dvh1TagRegex.Replace(fixedArgs, string.Empty);

        if (fixedArgs.Contains("-fflags +genpts", StringComparison.Ordinal)
            && !fixedArgs.Contains("+igndts", StringComparison.Ordinal))
        {
            fixedArgs = fixedArgs.Replace(
                "-fflags +genpts",
                "-fflags +genpts+igndts+discardcorrupt",
                StringComparison.Ordinal);
        }

        fixedArgs = fixedArgs.Replace(
            "-avoid_negative_ts disabled",
            "-avoid_negative_ts make_zero",
            StringComparison.Ordinal);
        fixedArgs = fixedArgs.Replace("-start_at_zero ", string.Empty, StringComparison.Ordinal);
        fixedArgs = fixedArgs.Replace("-copyts ", string.Empty, StringComparison.Ordinal);

        if (!fixedArgs.Contains("-reset_timestamps", StringComparison.Ordinal))
        {
            fixedArgs = fixedArgs.Replace(
                "-map_chapters -1 ",
                "-map_chapters -1 -reset_timestamps 1 ",
                StringComparison.Ordinal);
        }

        if (!fixedArgs.Contains("aac_adtstoasc", StringComparison.OrdinalIgnoreCase))
            fixedArgs = AudioCopyCodecRegex.Replace(fixedArgs, "-bsf:a aac_adtstoasc -codec:a:0 copy");

        return fixedArgs;
    }

    private static bool IsTorBoxHlsFfmpegInput(string args) =>
        args.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
        && (args.Contains("tb-cdn.io", StringComparison.OrdinalIgnoreCase)
            || args.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase));
}
