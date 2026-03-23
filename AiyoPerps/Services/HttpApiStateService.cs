using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public enum HttpApiState
{
    Off,
    Initializing,
    Ready,
    Error
}

public sealed class HttpApiStateService
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _initializationTcs = CreateInitializationTcs();
    private HttpApiState _state;
    private int? _port;

    public event Action? StateChanged;

    public HttpApiState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public int? Port
    {
        get
        {
            lock (_sync)
            {
                return _port;
            }
        }
    }

    public bool IsInitializing => State == HttpApiState.Initializing;

    public bool IsReady => State == HttpApiState.Ready;

    public void MarkInitializing(int? port)
    {
        lock (_sync)
        {
            _port = port;
            if (_state != HttpApiState.Initializing)
            {
                _initializationTcs = CreateInitializationTcs();
            }

            _state = HttpApiState.Initializing;
        }

        RaiseStateChanged();
    }

    public void MarkReady(int? port)
    {
        lock (_sync)
        {
            _port = port;
            _state = HttpApiState.Ready;
            _initializationTcs.TrySetResult(true);
        }

        RaiseStateChanged();
    }

    public void MarkOff(int? port)
    {
        lock (_sync)
        {
            _port = port;
            _state = HttpApiState.Off;
            _initializationTcs.TrySetResult(false);
        }

        RaiseStateChanged();
    }

    public void MarkError(int? port)
    {
        lock (_sync)
        {
            _port = port;
            _state = HttpApiState.Error;
            _initializationTcs.TrySetResult(false);
        }

        RaiseStateChanged();
    }

    public async Task<bool> WaitForReadyOrInitializationExitAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_sync)
        {
            if (_state != HttpApiState.Initializing)
            {
                return _state == HttpApiState.Ready;
            }

            waitTask = _initializationTcs.Task;
        }

        await waitTask.WaitAsync(cancellationToken);
        return IsReady;
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // Never throw from state notifications.
        }
    }

    private static TaskCompletionSource<bool> CreateInitializationTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
