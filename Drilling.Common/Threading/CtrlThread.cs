using System.Diagnostics;

namespace Drilling.Common.Threading;
public class CtrlThread
{
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private readonly ManualResetEventSlim _stopEvent = new(false);
    private Thread? _thread;
    private volatile bool _stopRequested;
    private int _delayMs = 1;
    private string _threadName = "";
    private EN_CTRL_THREAD_STATE _state = EN_CTRL_THREAD_STATE.Stopped;
    private Exception? _lastError;
    private bool _disposed;
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _thread is { IsAlive: true };
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _state == EN_CTRL_THREAD_STATE.Paused;
            }
        }
    }

    public EN_CTRL_THREAD_STATE ThreadState
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public Exception? LastThreadError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    public void Start(int delayMs, string name)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _delayMs = Math.Max(1, delayMs);
            _threadName = string.IsNullOrWhiteSpace(name) ? GetType().Name : name;
            _stopRequested = false;
            _lastError = null;
            _stopEvent.Reset();
            _pauseEvent.Set();
            _state = EN_CTRL_THREAD_STATE.Running;
            _thread = new Thread(ThreadProcess)
            {
                IsBackground = true,
                Name = _threadName
            };
            try
            {
                _thread.Start();
            }
            catch (Exception exception)
            {
                _thread = null;
                _state = EN_CTRL_THREAD_STATE.Error;
                _lastError = exception;
                _stopRequested = true;
                Debug.WriteLine("Thread start failed : " + _threadName + " / " + exception);
                throw;
            }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_thread is not { IsAlive: true } || _stopRequested)
            {
                return;
            }

            _pauseEvent.Reset();
            _state = EN_CTRL_THREAD_STATE.Paused;
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_thread is not { IsAlive: true } || _stopRequested)
            {
                return;
            }

            _state = EN_CTRL_THREAD_STATE.Running;
            _pauseEvent.Set();
        }
    }

    public void Stop()
    {
        Thread? target;
        bool isCurrentThread;
        lock (_sync)
        {
            ThrowIfDisposed();
            _stopRequested = true;
            _state = EN_CTRL_THREAD_STATE.Stopping;
            _pauseEvent.Set();
            _stopEvent.Set();
            target = _thread;
            isCurrentThread = target == Thread.CurrentThread;
        }

        if (target is null)
        {
            lock (_sync)
            {
                if (_thread is null)
                {
                    _state = EN_CTRL_THREAD_STATE.Stopped;
                }
            }

            return;
        }

        if (isCurrentThread)
        {
            return;
        }

        var stopped = target.Join(3000);
        Exception? stopError = null;
        lock (_sync)
        {
            if (!ReferenceEquals(_thread, target))
            {
                return;
            }

            if (stopped || !target.IsAlive)
            {
                _thread = null;
                _state = EN_CTRL_THREAD_STATE.Stopped;
            }
            else
            {
                _state = EN_CTRL_THREAD_STATE.Error;
                stopError = new TimeoutException("Thread stop timeout: " + _threadName);
                _lastError = stopError;
                Debug.WriteLine(stopError.ToString());
            }
        }

        if (stopError is not null)
        {
            throw stopError;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_thread is { IsAlive: true })
            {
                throw new InvalidOperationException("CtrlThread cannot be disposed while the worker thread is running.");
            }

            _pauseEvent.Dispose();
            _stopEvent.Dispose();
            _disposed = true;
        }
    }

    public virtual void Run()
    {
    }

    protected bool IsStopRequested()
    {
        return _stopRequested;
    }

    protected virtual void OnThreadError(Exception exception)
    {
        Debug.WriteLine("Thread Error : " + _threadName + " / " + exception);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    private void ThreadProcess()
    {
        try
        {
            while (!_stopRequested)
            {
                _pauseEvent.Wait();
                if (_stopRequested)
                {
                    break;
                }

                try
                {
                    Run();
                }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _lastError = exception;
                    }

                    try
                    {
                        OnThreadError(exception);
                    }
                    catch (Exception callbackException)
                    {
                        Debug.WriteLine("Thread error callback failed : " + _threadName + " / " + callbackException);
                    }
                }

                if (_delayMs > 0 && _stopEvent.Wait(_delayMs))
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_thread == Thread.CurrentThread)
                {
                    _thread = null;
                }

                _state = EN_CTRL_THREAD_STATE.Stopped;
            }
        }
    }
}

public enum EN_CTRL_THREAD_STATE
{
    Stopped,
    Running,
    Paused,
    Stopping,
    Error
}
