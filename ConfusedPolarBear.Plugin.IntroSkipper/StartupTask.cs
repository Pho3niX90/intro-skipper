using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Initializes plugin logic during Jellyfin startup. Replaces IServerEntryPoint for Jellyfin 10.9+.
/// </summary>
public class StartupTask : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StartupTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupTask"/> class.
    /// </summary>
    /// <param name="userManager">User manager service.</param>
    /// <param name="userViewManager">User view manager service.</param>
    /// <param name="libraryManager">Library manager service.</param>
    /// <param name="logger">Logger for this class.</param>
    /// <param name="loggerFactory">Logger factory used for dependent loggers.</param>
    public StartupTask(
        IUserManager userManager,
        IUserViewManager userViewManager,
        ILibraryManager libraryManager,
        ILogger<StartupTask> logger,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userViewManager = userViewManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Called when the plugin is loaded and Jellyfin starts.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        FFmpegWrapper.Logger = _logger;

        try
        {
            _logger.LogInformation("Running startup enqueue");
            var queueManager = new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager);
            queueManager.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the plugin is being unloaded or Jellyfin is stopping.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup logic can be added here if needed
        return Task.CompletedTask;
    }
}
