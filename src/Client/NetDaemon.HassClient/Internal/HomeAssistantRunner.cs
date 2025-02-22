namespace NetDaemon.Client.Internal;

internal class HomeAssistantRunner : IHomeAssistantRunner
{
    private readonly IHomeAssistantClient _client;

    // The internal token source will make sure we 
    // always cancel operations on dispose
    private readonly CancellationTokenSource _internalTokenSource = new();
    private readonly Subject<IHomeAssistantConnection> _onConnectSubject = new();

    private readonly Subject<DisconnectReason> _onDisconnectSubject = new();


    private Task? _runTask;

    public HomeAssistantRunner(
        IHomeAssistantClient client,
        ILogger<IHomeAssistantRunner> logger
    )
    {
        _client = client;
        _logger = logger;
    }

    private readonly ILogger<IHomeAssistantRunner> _logger;
    public IObservable<IHomeAssistantConnection> OnConnect => _onConnectSubject;
    public IObservable<DisconnectReason> OnDisconnect => _onDisconnectSubject;
    public IHomeAssistantConnection? CurrentConnection { get; internal set; }

    public Task RunAsync(string host, int port, bool ssl, string token, TimeSpan timeout,
        CancellationToken cancelToken)
    {
        return RunAsync(host, port, ssl, token, HomeAssistantSettings.DefaultWebSocketPath, timeout, cancelToken);
    }
    public Task RunAsync(string host, int port, bool ssl, string token, string websocketPath, TimeSpan timeout, CancellationToken cancelToken)
    {
        _runTask = InternalRunAsync(host, port, ssl, token, websocketPath, timeout, cancelToken);
        return _runTask;
    }

    private bool _isDisposed;
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
        _internalTokenSource.Cancel();

        if (_runTask?.IsCompleted == false)
            try
            {
                await Task.WhenAny(
                    _runTask,
                    Task.Delay(5000)
                ).ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors
            }

        _onConnectSubject.Dispose();
        _onDisconnectSubject.Dispose();
        _internalTokenSource.Dispose();
        _isDisposed = true;
    }

    private async Task InternalRunAsync(string host, int port, bool ssl, string token, string websocketPath, TimeSpan timeout,
        CancellationToken cancelToken)
    {
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(_internalTokenSource.Token, cancelToken);
        var isRetry = false;
        while (!combinedToken.IsCancellationRequested)
        {
            if (isRetry)
            {
                _logger.LogDebug("Client disconnected, retrying in {seconds} seconds...", timeout.TotalSeconds);
                // This is a retry
                await Task.Delay(timeout, combinedToken.Token).ConfigureAwait(false);
            }

            try
            {
                CurrentConnection = await _client.ConnectAsync(host, port, ssl, token, websocketPath, combinedToken.Token)
                    .ConfigureAwait(false);
                // Start the event processing before publish the connection
                var eventsTask = CurrentConnection.ProcessHomeAssistantEventsAsync(combinedToken.Token);
                _onConnectSubject.OnNext(CurrentConnection);
                await eventsTask.ConfigureAwait(false);
            }
            catch (HomeAssistantConnectionException de)
            {
                switch (de.Reason)
                {
                    case DisconnectReason.Unauthorized:
                        _logger.LogDebug("User token unauthorized! Will not retry connecting...");
                        _onDisconnectSubject.OnNext(DisconnectReason.Unauthorized);
                        return;
                    case DisconnectReason.NotReady:
                        _logger.LogDebug("Home Assistant is not ready yet!");
                        _onDisconnectSubject.OnNext(DisconnectReason.NotReady);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                if (_internalTokenSource.IsCancellationRequested)
                {
                    // We have internal cancellation due to dispose
                    // just return without any further due
                    return;
                }

                _onDisconnectSubject.OnNext(cancelToken.IsCancellationRequested
                    ? DisconnectReason.Client
                    : DisconnectReason.Remote);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e,"Error running HassClient");
                _onDisconnectSubject.OnNext(DisconnectReason.Error);
            }
            finally
            {
                if (CurrentConnection is not null)
                {
                    // Just try to dispose the connection silently
                    try
                    {
                        await CurrentConnection.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        CurrentConnection = null;
                    }
                }
            }

            isRetry = true;
        }
    }
}