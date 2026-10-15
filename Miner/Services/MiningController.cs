namespace Miner.Services;

/// <summary>
/// State of the mining process.
/// </summary>
public enum MiningState
{
    Idle,
    Syncing,
    Mining,
    Paused
}

/// <summary>
/// Thread-safe controller for coordinating the mining lifecycle.
/// Allows pausing mining during candidate block verification, Merkle tree updates,
/// or initial blockchain synchronization, and resuming or restarting candidate generation.
/// </summary>
public class MiningController
{
    private readonly ILogger<MiningController> _logger;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _pauseSignal = new(1, 1);
    private CancellationTokenSource _iterationCts = new();

    private MiningState _state = MiningState.Idle;
    private string? _pauseReason;
    private bool _isSyncing;

    public MiningController(ILogger<MiningController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Current mining lifecycle state.
    /// </summary>
    public MiningState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Indicates whether mining is currently paused or syncing.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                return _state == MiningState.Paused || _state == MiningState.Syncing;
            }
        }
    }

    /// <summary>
    /// Indicates whether the node is currently in initial sync or catching up.
    /// </summary>
    public bool IsSyncing
    {
        get
        {
            lock (_stateLock)
            {
                return _isSyncing;
            }
        }
    }

    /// <summary>
    /// The reason for the current pause, if applicable.
    /// </summary>
    public string? PauseReason
    {
        get
        {
            lock (_stateLock)
            {
                return _pauseReason;
            }
        }
    }

    /// <summary>
    /// Cancellation token for the active mining iteration.
    /// Canceled when Pause or Restart is triggered to stop hashing candidate blocks immediately.
    /// </summary>
    public CancellationToken IterationToken
    {
        get
        {
            lock (_stateLock)
            {
                return _iterationCts.Token;
            }
        }
    }

    /// <summary>
    /// Sets the node synchronization status.
    /// </summary>
    public void SetSyncing(bool syncing)
    {
        lock (_stateLock)
        {
            _isSyncing = syncing;
            if (syncing)
            {
                _state = MiningState.Syncing;
                _logger.LogInformation("Mining controller set to Syncing state.");
                CancelCurrentIteration();
            }
            else
            {
                _state = MiningState.Mining;
                _logger.LogInformation("Mining controller synchronization finished. State set to Mining.");
                RenewIterationToken();
            }
        }
    }

    /// <summary>
    /// Pauses mining with a specified reason (e.g., verifying an incoming block or updating Merkle tree).
    /// Halts any active candidate iteration.
    /// </summary>
    public void Pause(string reason)
    {
        lock (_stateLock)
        {
            _pauseReason = reason;
            _state = MiningState.Paused;
            _logger.LogInformation("Mining PAUSED. Reason: {Reason}", reason);
            CancelCurrentIteration();
        }
    }

    /// <summary>
    /// Resumes mining if previously paused.
    /// </summary>
    public void Resume()
    {
        lock (_stateLock)
        {
            if (_isSyncing)
            {
                _state = MiningState.Syncing;
                _logger.LogInformation("Mining cannot resume while node is still syncing.");
                return;
            }

            _pauseReason = null;
            _state = MiningState.Mining;
            _logger.LogInformation("Mining RESUMED.");
            RenewIterationToken();
        }
    }

    /// <summary>
    /// Signals the miner that the candidate block is obsolete (e.g., new block added to tip)
    /// and mining should restart on the new tip.
    /// </summary>
    public void Restart(string reason)
    {
        lock (_stateLock)
        {
            _pauseReason = null;
            if (!_isSyncing)
            {
                _state = MiningState.Mining;
            }

            _logger.LogInformation("Mining RESTARTED. Reason: {Reason}", reason);
            CancelCurrentIteration();
            RenewIterationToken();
        }
    }

    /// <summary>
    /// Waits asynchronously while the mining controller is paused or syncing.
    /// </summary>
    public async Task WaitUntilActiveAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool paused;
            lock (_stateLock)
            {
                paused = _state == MiningState.Paused || _state == MiningState.Syncing;
            }

            if (!paused)
            {
                break;
            }

            await Task.Delay(200, stoppingToken);
        }
    }

    private void CancelCurrentIteration()
    {
        if (!_iterationCts.IsCancellationRequested)
        {
            _iterationCts.Cancel();
        }
    }

    private void RenewIterationToken()
    {
        if (_iterationCts.IsCancellationRequested)
        {
            _iterationCts.Dispose();
            _iterationCts = new CancellationTokenSource();
        }
    }
}
