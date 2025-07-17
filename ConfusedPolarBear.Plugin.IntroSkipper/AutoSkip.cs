using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Background service that automatically skips intros during playback.
/// </summary>
public class AutoSkip : IHostedService, IDisposable
{
    private readonly ILogger<AutoSkip> _logger;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly Dictionary<string, bool> _sentSeekCommand = new();
    private readonly object _sentSeekCommandLock = new();
    private readonly System.Timers.Timer _playbackTimer = new(1000);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoSkip"/> class.
    /// </summary>
    /// <param name="userDataManager">User data manager service.</param>
    /// <param name="sessionManager">Session manager service.</param>
    /// <param name="logger">Logger instance.</param>
    public AutoSkip(
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        ILogger<AutoSkip> logger)
    {
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts the intro skip logic when the plugin is loaded.
    /// </summary>
    /// <param name="cancellationToken">.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Initializing AutoSkip...");

        _userDataManager.UserDataSaved += UserDataManager_UserDataSaved;
        Plugin.Instance!.AutoSkipChanged += AutoSkipChanged;

        _playbackTimer.AutoReset = true;
        _playbackTimer.Elapsed += PlaybackTimer_Elapsed;

        AutoSkipChanged(null, EventArgs.Empty); // Start or stop timer based on config

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background service.
    /// </summary>
    /// <param name="cancellationToken">.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping AutoSkip service.");

        _userDataManager.UserDataSaved -= UserDataManager_UserDataSaved;
        _playbackTimer.Stop();
        _playbackTimer.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when plugin config changes (e.g., enabling/disabling AutoSkip).
    /// </summary>
    private void AutoSkipChanged(object? sender, EventArgs e)
    {
        var enabled = Plugin.Instance!.Configuration.AutoSkip;
        _logger.LogDebug("Setting playback timer enabled to {Enabled}", enabled);
        _playbackTimer.Enabled = enabled;
    }

    /// <summary>
    /// Tracks playback start/stop to determine if a seek should be issued.
    /// </summary>
    private void UserDataManager_UserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason != UserDataSaveReason.PlaybackStart && e.SaveReason != UserDataSaveReason.PlaybackFinished)
        {
            return;
        }

        var itemId = e.Item.Id;
        var episodeNumber = e.Item.IndexNumber.GetValueOrDefault(-1);
        var skipState = false;

        SessionInfo? session = null;

        try
        {
            foreach (var needle in _sessionManager.Sessions)
            {
                if (needle.UserId == e.UserId && needle.NowPlayingItem?.Id == itemId)
                {
                    session = needle;
                    break;
                }
            }

            if (session == null)
            {
                _logger.LogInformation("Unable to find session for {ItemId}", itemId);
                return;
            }
        }
        catch (Exception ex) when (ex is NullReferenceException or ResourceNotFoundException)
        {
            return;
        }

        if (!Plugin.Instance!.Configuration.SkipFirstEpisode && episodeNumber == 1)
        {
            skipState = true;
        }

        lock (_sentSeekCommandLock)
        {
            _logger.LogDebug("Resetting seek command state for session {Session}", session.DeviceId);
            _sentSeekCommand[session.DeviceId] = skipState;
        }
    }

    /// <summary>
    /// Periodically checks playback positions and issues seek commands if in intro range.
    /// </summary>
    private void PlaybackTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var session in _sessionManager.Sessions)
        {
            var deviceId = session.DeviceId;
            var item = session.NowPlayingItem;

            if (item == null)
            {
                continue;
            }

            var itemId = item.Id;
            var position = session.PlayState.PositionTicks / TimeSpan.TicksPerSecond;

            lock (_sentSeekCommandLock)
            {
                if (_sentSeekCommand.TryGetValue(deviceId, out var sent) && sent)
                {
                    _logger.LogTrace("Seek already sent for session {DeviceId}", deviceId);
                    continue;
                }
            }

            if (!Plugin.Instance!.Intros.TryGetValue(itemId, out var intro) || !intro.Valid)
            {
                continue;
            }

            var adjustedStart = Math.Max(5, intro.IntroStart);

            _logger.LogTrace("Position: {Position}s, Intro: {Start}s–{End}s", position, adjustedStart, intro.IntroEnd);

            if (position < adjustedStart || position > intro.IntroEnd)
            {
                continue;
            }

            var message = Plugin.Instance!.Configuration.AutoSkipNotificationText;
            if (!string.IsNullOrWhiteSpace(message))
            {
                _sessionManager.SendMessageCommand(
                    session.Id,
                    session.Id,
                    new MessageCommand
                    {
                        Header = string.Empty,
                        Text = message,
                        TimeoutMs = 2000
                    },
                    CancellationToken.None);
            }

            _logger.LogDebug("Sending seek command to {DeviceId}", deviceId);
            var introEnd = (long)intro.IntroEnd - Plugin.Instance!.Configuration.SecondsOfIntroToPlay;

            _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest
                {
                    Command = PlaystateCommand.Seek,
                    ControllingUserId = session.UserId.ToString("N"),
                    SeekPositionTicks = introEnd * TimeSpan.TicksPerSecond
                },
                CancellationToken.None);

            lock (_sentSeekCommandLock)
            {
                _logger.LogTrace("Marking seek as sent for session {DeviceId}", deviceId);
                _sentSeekCommand[deviceId] = true;
            }
        }
    }

    /// <summary>
    /// Actual dispose method following the dispose pattern.
    /// </summary>
    /// <param name="disposing">True when called from Dispose(), false when called from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _userDataManager.UserDataSaved -= UserDataManager_UserDataSaved;
            Plugin.Instance!.AutoSkipChanged -= AutoSkipChanged;

            _playbackTimer.Stop();
            _playbackTimer.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Releases unmanaged and managed resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
