using System.IO;
using System.IO.Ports;
using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;

namespace Drilling.Common.Interface;
[CCommType("Serial")]
[CCommType("ModbusSerial")]
internal class CSerialComm(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CCommBase(data, option)
{
    private const int MaxAutoReconnectAttempts = 3;
    private static readonly TimeSpan AutoReconnectInterval = TimeSpan.FromSeconds(5);
    private readonly object _reconnectPolicyLock = new();
    private readonly object _serialPortLifecycleLock = new();
    private int _autoReconnectAttempts;
    private bool _initialConnectAttempted;
    private bool _connectInProgress;
    private bool _autoReconnectSuppressed;
    private TaskCompletionSource<bool>? _connectAttemptCompletion;
    private DateTimeOffset _nextAutoReconnectAt = DateTimeOffset.MinValue;
    protected SerialPort? SerialPort;
    protected readonly SemaphoreSlim SerialLock = new(1, 1);

    protected virtual void ConfigureSerialPort(SerialPort serialPort)
    {
    }

    public override async Task Connect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBeginConnectAttempt(out var activeConnectAttempt))
        {
            if (activeConnectAttempt is not null)
            {
                await activeConnectAttempt.WaitAsync(cancellationToken);
            }

            return;
        }

        try
        {
            lock (_serialPortLifecycleLock)
            {
                if (SerialPort is { IsOpen: true } && ConnectionState == EN_COMM_STATE.Online)
                {
                    RegisterConnectSuccess();
                    LastError = "";
                    return;
                }

                CloseSerialPort();
                try
                {
                    if (string.IsNullOrWhiteSpace(Option.SerialPort) || Option.BaudRate <= 0)
                    {
                        SetError("Serial port option is invalid.");
                        return;
                    }

                    var serialPort = new SerialPort(Option.SerialPort, Option.BaudRate, ParseParity(Option.Parity), Option.DataBits, ParseStopBits(Option.StopBits))
                    {
                        Handshake = ParseHandshake(Option.Handshake),
                        ReadTimeout = Math.Max(500, Option.TimeoutMs),
                        WriteTimeout = Math.Max(500, Option.TimeoutMs),
                        NewLine = CommandNewLine
                    };
                    ConfigureSerialPort(serialPort);
                    SerialPort = serialPort;
                    SerialPort.Open();
                    RegisterConnectSuccess();
                    LastError = "";
                    SetState(EN_COMM_STATE.Online);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    CloseSerialPort();
                    SetError(ex);
                }
            }
        }
        finally
        {
            CompleteConnectAttempt();
        }

    }

    public override void ResetAutoReconnectPolicy()
    {
        lock (_reconnectPolicyLock)
        {
            _autoReconnectAttempts = 0;
            _initialConnectAttempted = false;
            _nextAutoReconnectAt = DateTimeOffset.MinValue;
            _autoReconnectSuppressed = false;
        }
    }

    public override void SuppressAutoReconnect()
    {
        lock (_reconnectPolicyLock)
        {
            _autoReconnectAttempts = MaxAutoReconnectAttempts;
            _initialConnectAttempted = true;
            _nextAutoReconnectAt = DateTimeOffset.MaxValue;
            _autoReconnectSuppressed = true;
        }
    }

    private bool TryBeginConnectAttempt(out Task? activeConnectAttempt)
    {
        lock (_reconnectPolicyLock)
        {
            if (_connectInProgress)
            {
                activeConnectAttempt = _connectAttemptCompletion?.Task;
                return false;
            }

            activeConnectAttempt = null;
            if (_autoReconnectSuppressed)
            {
                return false;
            }

            if (!_initialConnectAttempted)
            {
                _initialConnectAttempted = true;
                _connectInProgress = true;
                _connectAttemptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }

            if (_autoReconnectAttempts >= MaxAutoReconnectAttempts || DateTimeOffset.Now < _nextAutoReconnectAt)
            {
                return false;
            }

            _autoReconnectAttempts++;
            _connectInProgress = true;
            _connectAttemptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    private void CompleteConnectAttempt()
    {
        TaskCompletionSource<bool>? completion;
        lock (_reconnectPolicyLock)
        {
            _connectInProgress = false;
            completion = _connectAttemptCompletion;
            _connectAttemptCompletion = null;
        }

        completion?.TrySetResult(true);
    }

    private void RegisterConnectSuccess()
    {
        lock (_reconnectPolicyLock)
        {
            _autoReconnectAttempts = 0;
            _nextAutoReconnectAt = DateTimeOffset.MinValue;
        }
    }

    private void RegisterConnectFailure()
    {
        lock (_reconnectPolicyLock)
        {
            _nextAutoReconnectAt = DateTimeOffset.Now.Add(AutoReconnectInterval);
        }
    }

    public override Task Disconnect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_serialPortLifecycleLock)
        {
            CloseSerialPort();
            SetState(EN_COMM_STATE.Offline);
        }
        return Task.CompletedTask;
    }

    protected new void SetError(Exception ex)
    {
        base.SetError(ex);
        RegisterConnectFailure();
        SetState(EN_COMM_STATE.Offline);
    }

    protected new void SetError(string message)
    {
        base.SetError(message);
        RegisterConnectFailure();
        SetState(EN_COMM_STATE.Offline);
    }

    protected void SetResponseState(string response)
    {
        if (response.StartsWith("ERR:-1", StringComparison.OrdinalIgnoreCase))
        {
            SetError(response);
            return;
        }

        SetState(EN_COMM_STATE.Online);
    }

    public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
    {
        await SerialLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteSerial(function, cancellationToken);
        }
        finally
        {
            SerialLock.Release();
        }
    }

    private async Task<string> ExecuteSerial(string function, CancellationToken cancellationToken)
    {
        if (SerialPort is null || !SerialPort.IsOpen)
        {
            await Connect(cancellationToken);
        }

        if (SerialPort is null || !SerialPort.IsOpen)
        {
            return "";
        }

        LastSent = FormatCommand(function);

        // 로컬 함수 내부의 예외 처리 강화
        string? ifCallback()
        {
            if (this is CTalonLaser)
            {
                SerialPort.NewLine = "\n";
                SerialPort.Write(LastSent + "\r");
            }
            else
            {
                SerialPort.WriteLine(LastSent);
            }

            try
            {
                return SerialPort.ReadLine().Trim();

            }
            catch (TimeoutException)
            {
                // ReadExisting 호출 시 발생할 수 있는 2차 예외 방어
                try
                {
                    return SerialPort.ReadExisting().Trim();
                }
                catch
                {
                    return "";
                }
            }
            catch (Exception)
            {
                // 타임아웃 외에 포트 단선/폐쇄 등 모든 예외를 안전하게 던짐
                // 이 예외는 외부 try-catch에서 잡힙니다.
                throw;
            }
        }

        try
        {
            // 취소 토큰 전달
            cancellationToken.ThrowIfCancellationRequested();

            LastReceived = await Task.Run(ifCallback, cancellationToken) ?? "";
            LastError = string.IsNullOrWhiteSpace(LastReceived) ? "Serial response timeout." : "";
            if (!string.IsNullOrWhiteSpace(LastError))
            {
                SetError(LastError);
                return "";
            }

            SetState(EN_COMM_STATE.Online);
            return LastReceived;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            CloseSerialPort();
            SetError(ex);
            return "";
        }
        // if (SerialPort is null || !SerialPort.IsOpen)
        // {
        //     await Connect(cancellationToken);
        // }
        //
        // if (SerialPort is null || !SerialPort.IsOpen)
        // {
        //     return "";
        // }
        //
        // LastSent = FormatCommand(function);
        // string? ifCallback()
        // {
        //     SerialPort.WriteLine(LastSent);
        //     try
        //     {
        //         //return "";
        //         return SerialPort.ReadExisting().Trim();
        //      }
        //     catch (TimeoutException)
        //     {
        //         return SerialPort.ReadExisting().Trim();
        //     }
        // }
        //
        // try
        // {
        //     LastReceived = await Task.Run(ifCallback, cancellationToken) ?? "";
        //     LastError = string.IsNullOrWhiteSpace(LastReceived) ? "Serial response timeout." : "";
        //     if (!string.IsNullOrWhiteSpace(LastError))
        //     {
        //         SetError(LastError);
        //         return "";
        //     }
        //
        //     SetState(EN_COMM_STATE.Online);
        //     return LastReceived;
        // }
        // catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        // {
        //     CloseSerialPort();
        //     SetError(ex);
        //     return "";
        // }
    }

    protected virtual string CommandNewLine
    {
        get
        {
            return "\r\n";
        }
    }

    protected virtual string FormatCommand(string function)
    {
        return function;
    }

    protected void CloseSerialPort()
    {
        if (SerialPort is null)
        {
            return;
        }

        try
        {
            if (SerialPort.IsOpen)
            {
                SerialPort.Close();
            }
        }
        finally
        {
            SerialPort.Dispose();
            SerialPort = null;
        }
    }

    private static Parity ParseParity(string value)
    {
        return Enum.TryParse<Parity>(NormalizeEnumValue(value), ignoreCase: true, out var parity) ? parity : Parity.None;
    }

    private static StopBits ParseStopBits(string value)
    {
        return Enum.TryParse<StopBits>(NormalizeEnumValue(value), ignoreCase: true, out var stopBits) ? stopBits : StopBits.One;
    }

    private static Handshake ParseHandshake(string value)
    {
        return NormalizeEnumValue(value) switch
        {
            "" or "NONE" or "NO" or "OFF" => Handshake.None,
            "XONXOFF" or "XONOFF" => Handshake.XOnXOff,
            "REQUESTTOSEND" or "RTSCTS" => Handshake.RequestToSend,
            "REQUESTTOSENDXONXOFF" or "RTSCTSXONXOFF" => Handshake.RequestToSendXOnXOff,
            _ => Handshake.None
        };
    }

    private static string NormalizeEnumValue(string value)
    {
        return value.Replace("_", "", StringComparison.OrdinalIgnoreCase).Replace("-", "", StringComparison.OrdinalIgnoreCase).Replace("/", "", StringComparison.OrdinalIgnoreCase).Trim();
    }
}
