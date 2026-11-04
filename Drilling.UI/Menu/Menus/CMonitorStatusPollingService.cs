using System.Diagnostics;
using System.Globalization;
using Drilling.Common.Interface;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Threading;

namespace Drilling.UI.Menu.Menus;
internal sealed class CMonitorStatusPollingService(CInterfaceManager interfaceManager, CMotionManager motionManager) : CtrlThread
{
    private const int HeadCount = 8;
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan ActivePowerMeterMeasureInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SlowSafetyInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommunicationInterval = TimeSpan.FromSeconds(1);
    private readonly object _contextLock = new();
    private readonly object _snapshotLock = new();
    private bool _isActive;
    private readonly SemaphoreSlim _motorPollLock = new(1, 1);
    private ST_MONITOR_POLLING_CONTEXT _context = ST_MONITOR_POLLING_CONTEXT.Default;
    private ST_MONITOR_STATUS_SNAPSHOT _snapshot = ST_MONITOR_STATUS_SNAPSHOT.Empty;
    private DateTimeOffset _lastCommunicationPoll = DateTimeOffset.MinValue;
    private long? _lastSuccessfulMotorPollTimestamp;
    private DateTimeOffset _lastLaserPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttenuatorPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBetPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPowerMeterPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPicoMotorPoll = DateTimeOffset.MinValue;
    private int _communicationPollRunning;
    private int _activeDevicePollRunning;
    public void Start()
    {
        base.Start((int)LoopInterval.TotalMilliseconds, "MonitorStatusPolling");
    }

    public void UpdateContext(string selectedTab, int laserNumber, int attenuatorNumber, int betNumber, string melsecGroup)
    {
        var nextContext = new ST_MONITOR_POLLING_CONTEXT(NormalizeTab(selectedTab), Math.Clamp(laserNumber, 0, HeadCount - 1), Math.Clamp(attenuatorNumber, 0, HeadCount - 1), Math.Clamp(betNumber, 0, HeadCount - 1), NormalizeMelsecGroup(melsecGroup));
        lock (_contextLock)
        {
            _context = nextContext;
        }
    }

    public void SetActive(bool active)
    {
        lock (_contextLock)
        {
            _isActive = active;
        }
    }

    public ST_MONITOR_STATUS_SNAPSHOT GetSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public void RefreshMelsecNow() => PollMelsec(GetContext().MelsecGroup, CancellationToken.None);

    public void RefreshMotorNow(CancellationToken cancellationToken = default)
    {
        PollMotorSerialized(true, cancellationToken);
    }

    public override void Run()
    {
        PollDue(CancellationToken.None);
    }

    private void PollDue(CancellationToken cancellationToken)
    {
        if (!IsActive())
        {
            return;
        }

        var context = GetContext();
        var now = DateTimeOffset.Now;
        void PollLaserCallback(global::System.Threading.CancellationToken token)
        {
            PollLaser(context.LaserNumber, token);
        }

        void PollAttenuatorCallback(global::System.Threading.CancellationToken token)
        {
            PollAttenuator(context.AttenuatorNumber, token);
        }

        void PollBetCallback(global::System.Threading.CancellationToken token)
        {
            PollBet(context.BetNumber, token);
        }

        void PollMelsecCallback(global::System.Threading.CancellationToken token)
        {
            PollMelsec(context.MelsecGroup, token);
        }

            if (IsDue(ref _lastCommunicationPoll, now, CommunicationInterval))
            {
                SchedulePoll(PollCommunication, communicationPoll: true);
            }

            switch (context.SelectedTab)
            {
                case "XPS":
                    SchedulePoll(PollMotorWhenDue, communicationPoll: false);
                    break;
                case "LASER" when IsDue(ref _lastLaserPoll, now, ActiveInterval):
                    SchedulePoll(PollLaserCallback, communicationPoll: false);
                    break;
                case "ATTENUATOR" when IsDue(ref _lastAttenuatorPoll, now, ActiveInterval):
                    SchedulePoll(PollAttenuatorCallback, communicationPoll: false);
                    break;
                case "BET" when IsDue(ref _lastBetPoll, now, ActiveInterval):
                    SchedulePoll(PollBetCallback, communicationPoll: false);
                    break;
                case "POWER METER" when IsDue(ref _lastPowerMeterPoll, now, GetPowerMeterInterval()):
                    //TryPoll(PollPowerMeter, cancellationToken);
                    break;
                case "PICO MOTOR" when IsDue(ref _lastPicoMotorPoll, now, ActiveInterval):
                    SchedulePoll(PollPicoMotor, communicationPoll: false);
                    break;
                case "MELSEC":
                    SchedulePoll(PollMelsecCallback, communicationPoll: false);
                    break;
            }

            if (context.SelectedTab != "POWER METER" && IsDue(ref _lastPowerMeterPoll, now, SlowSafetyInterval))
            {
                //TryPoll(PollPowerMeter, cancellationToken);
            }
    }

    private void TryPoll(Action<CancellationToken> poll, CancellationToken cancellationToken)
    {
        try
        {
            poll(cancellationToken);
        }
        catch (Exception exception)when (exception is not OperationCanceledException)
        {
        }
    }

    private void PollCommunication(CancellationToken cancellationToken)
    {
        var communication = interfaceManager.GetCommunicationStatus(cancellationToken);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                Communication = communication
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollMotorWhenDue(CancellationToken cancellationToken)
    {
        PollMotorSerialized(false, cancellationToken);
    }

    private void PollMotorSerialized(bool force, CancellationToken cancellationToken)
    {
        _motorPollLock.Wait(cancellationToken);
        try
        {
            if (!force && _lastSuccessfulMotorPollTimestamp is long lastSuccessfulPollTimestamp && Stopwatch.GetElapsedTime(lastSuccessfulPollTimestamp) < GetMotorInterval())
            {
                return;
            }

            PollMotorCore(cancellationToken);
            _lastSuccessfulMotorPollTimestamp = Stopwatch.GetTimestamp();
        }
        finally
        {
            _motorPollLock.Release();
        }
    }

    private void PollMotorCore(CancellationToken cancellationToken)
    {
        var motors = motionManager.GetAxisStatus(cancellationToken);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                DeviceStatus = snapshot.DeviceStatus with
                {
                    Motors = motors
                }
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollLaser(int laserNumber, CancellationToken cancellationToken)
    {
        var talonStatus = interfaceManager.RefreshTalonLaserStatus(laserNumber, cancellationToken, suppressCommandLog: true);
        var laserStatus = new ST_LASER_STATUS(talonStatus.LaserOn, talonStatus.ShutterOpen, talonStatus.GateOpen, talonStatus.OutputPower);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                DeviceStatus = snapshot.DeviceStatus with
                {
                    Laser = laserStatus
                },
                TalonStatus = talonStatus
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollAttenuator(int attenuatorNumber, CancellationToken cancellationToken)
    {
        var attenuator = interfaceManager.GetAttenuatorStatus(attenuatorNumber, cancellationToken);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                DeviceStatus = snapshot.DeviceStatus with
                {
                    Attenuator = attenuator
                }
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollBet(int betNumber, CancellationToken cancellationToken)
    {
        var bet = interfaceManager.RefreshBETStatus(betNumber, cancellationToken, suppressCommandLog: true);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                DeviceStatus = snapshot.DeviceStatus with
                {
                    Bet = bet
                }
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollPowerMeter(CancellationToken cancellationToken)
    {
        var powerMeter = interfaceManager.GetPowerMeterStatus(cancellationToken);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                DeviceStatus = snapshot.DeviceStatus with
                {
                    PowerMeter = powerMeter
                }
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollPicoMotor(CancellationToken cancellationToken)
    {
        var picoMotor = interfaceManager.GetPicoMotorStatus(cancellationToken);
        global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT SelectValue(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_SNAPSHOT snapshot)
        {
            return snapshot with
            {
                PicoMotorStatus = picoMotor
            };
        }

        UpdateSnapshot(SelectValue);
    }

    private void PollMelsec(string selectedGroup, CancellationToken cancellationToken)
    {
        var cachedValues = interfaceManager.Melsec.GetCachedSnapshot(out var mapRows);
        var values = new Dictionary<string, ST_MONITOR_MELSEC_VALUE>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CreateMelsecPollingRows(mapRows, selectedGroup))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Access == EN_MELSEC_ACCESS.Write)
            {
                continue;
            }

            if (!row.Use || !cachedValues.TryGetValue(row.Id, out var cached))
            {
                values[row.Id] = new ST_MONITOR_MELSEC_VALUE(row.Id, "-", row.Use ? "WAIT" : "DISABLED", row.Description, null);
                continue;
            }

            var state = cached.IsValid ? "OK" : cached.State.ToString().ToUpperInvariant();
            var value = cached.IsValid ? FormatMelsecValue(row, cached.Value)
                : cached.State == EN_MELSEC_CACHE_STATE.Pending ? "-" : "ERR";
            var message = CMelsecCachePresentation.Describe(cached);
            if (cached.IsValid && value == "ERR" && row.DataType != EN_MELSEC_DATA_TYPE.String)
            {
                state = "ERROR";
                message = $"{row.Id} has invalid {row.DataType} feedback. {message}";
            }

            values[row.Id] = new ST_MONITOR_MELSEC_VALUE(row.Id, value, state, message, cached.UpdatedAt);
        }

        UpdateSnapshot(snapshot => snapshot with { MelsecValues = values });
    }

    private static IReadOnlyList<ST_MELSEC_MAP_DATA> CreateMelsecPollingRows(IReadOnlyList<ST_MELSEC_MAP_DATA> mapRows, string selectedGroup)
    {
        if (selectedGroup.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            return mapRows;
        }

        var rows = new Dictionary<string, ST_MELSEC_MAP_DATA>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in mapRows.Where(row => string.IsNullOrWhiteSpace(selectedGroup) || row.Group.Equals(selectedGroup, StringComparison.OrdinalIgnoreCase)))
        {
            rows[row.Id] = row;
        }

        foreach (var row in mapRows)
        {
            if (row.Id.StartsWith("IF_", StringComparison.OrdinalIgnoreCase) ||
                row.Id.StartsWith("HANDSHAKE_", StringComparison.OrdinalIgnoreCase))
            {
                rows.TryAdd(row.Id, row);
            }
        }

        return rows.Values.ToArray();
    }

    private static string FormatMelsecValue(ST_MELSEC_MAP_DATA row, object? value)
    {
        return row.DataType switch
        {
            EN_MELSEC_DATA_TYPE.Bit when value is bool bit => bit ? "ON" : "OFF",
            EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord when double.IsFinite(CMelsecCachePresentation.NumericValue(value)) =>
                CMelsecCachePresentation.NumericValue(value).ToString("0.########", CultureInfo.InvariantCulture),
            EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float when value is double number && double.IsFinite(number) => number.ToString("F3", CultureInfo.InvariantCulture),
            EN_MELSEC_DATA_TYPE.String when value is string text => text,
            _ => "ERR"
        };
    }

    private ST_MONITOR_POLLING_CONTEXT GetContext()
    {
        lock (_contextLock)
        {
            return _context;
        }
    }

    private void SchedulePoll(Action<CancellationToken> poll, bool communicationPoll)
    {
        if (communicationPoll
            ? Interlocked.CompareExchange(ref _communicationPollRunning, 1, 0) != 0
            : Interlocked.CompareExchange(ref _activeDevicePollRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                TryPoll(poll, CancellationToken.None);
            }
            finally
            {
                if (communicationPoll)
                {
                    Interlocked.Exchange(ref _communicationPollRunning, 0);
                }
                else
                {
                    Interlocked.Exchange(ref _activeDevicePollRunning, 0);
                }
            }
        });
    }

    private bool IsActive()
    {
        lock (_contextLock)
        {
            return _isActive;
        }
    }

    private TimeSpan GetMotorInterval()
    {
        return motionManager.IsSimulation ? LoopInterval : ActiveInterval;
    }

    private TimeSpan GetPowerMeterInterval()
    {
        var snapshot = GetSnapshot();
        return snapshot.DeviceStatus.PowerMeter.IsMeasuring ? ActivePowerMeterMeasureInterval : ActiveInterval;
    }

    private void UpdateSnapshot(Func<ST_MONITOR_STATUS_SNAPSHOT, ST_MONITOR_STATUS_SNAPSHOT> update)
    {
        lock (_snapshotLock)
        {
            _snapshot = update(_snapshot);
        }
    }

    private static bool IsDue(ref DateTimeOffset lastPoll, DateTimeOffset now, TimeSpan interval)
    {
        if (now - lastPoll < interval)
        {
            return false;
        }

        lastPoll = now;
        return true;
    }

    private static string NormalizeTab(string tab)
    {
        var normalized = string.IsNullOrWhiteSpace(tab) ? "PRODUCT" : tab.Trim().ToUpperInvariant();
        return normalized switch
        {
            "ATT" => "ATTENUATOR",
            "POWER" or "POWERMETER" or "POWER_METER" => "POWER METER",
            "MOTOR" or "XPS" or "SCANNERTILT" or "SCANNER TILT" or "SCANNER_TILT" => "XPS",
            "PICO" or "PICOMOTOR" or "PICO_MOTOR" or "DOETILT" or "DOE TILT" or "DOE_TILT" => "PICO MOTOR",
            "COORDINATE" or "COORDINATEVIEWER" or "COORDINATE_VIEWER" => "COORDINATE VIEWER",
            "LASER" or "ATTENUATOR" or "BET" or "POWER METER" or "PICO MOTOR" or "PRODUCT" or "MELSEC" or "COORDINATE VIEWER" => normalized,
            _ => "PRODUCT"
        };
    }

    private static string NormalizeMelsecGroup(string group)
    {
        return string.IsNullOrWhiteSpace(group) ? "ALL" : group.Trim().ToUpperInvariant();
    }
}

internal sealed record ST_MONITOR_POLLING_CONTEXT(string SelectedTab, int LaserNumber, int AttenuatorNumber, int BetNumber, string MelsecGroup)
{
    public static ST_MONITOR_POLLING_CONTEXT Default { get; } = new("PRODUCT", 0, 0, 0, "ALL");
}

internal sealed record ST_MONITOR_STATUS_SNAPSHOT(
    ST_DEVICE_STATUS DeviceStatus, 
    IReadOnlyList<ST_DEVICE_COMM_STATUS> Communication, 
    ST_TALON_STATUS TalonStatus, 
    ST_PICO_MOTOR_STATUS PicoMotorStatus, 
    IReadOnlyDictionary<string, ST_MONITOR_MELSEC_VALUE> MelsecValues
)
{
    public static ST_MONITOR_STATUS_SNAPSHOT Empty { get; } = new(
        new ST_DEVICE_STATUS(
        [], 
        [], 
        new ST_LASER_STATUS(false, false, false, 0.0), 
        new ST_ATTENUATOR_STATUS(0.0, 0.0, "IDLE", false), 
        new ST_BET_STATUS(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, false, false, false, false, false), 
        ST_POWER_METER_STATUS.Empty
    ), 
        [], 
        ST_TALON_STATUS.Empty, 
        ST_PICO_MOTOR_STATUS.Empty, 
        new Dictionary<string, ST_MONITOR_MELSEC_VALUE>(StringComparer.OrdinalIgnoreCase)
    );
}

internal sealed record ST_MONITOR_MELSEC_VALUE(string Id, string Value, string State, string Message, DateTimeOffset? UpdatedAt);
