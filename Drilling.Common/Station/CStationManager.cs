namespace Drilling.Common.Station
{
    using Drilling.Common.Log;
    using Drilling.Common.Managers;
    using Drilling.Common.Interface;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Station;
    using Drilling.Common.Product;
    using Drilling.Common.Recipe;
    using Drilling.Common.Automation;
    using Drilling.Common.Review;

    public sealed class CStationManager
    {
        public void StartZeroLinePrewaitMonitoring(Func<string> selectedRecipe) => _processStation.StartZeroLinePrewaitMonitoring(selectedRecipe);
        public Task StopZeroLinePrewaitMonitoring() => _processStation.StopZeroLinePrewaitMonitoring();
        public Task CompleteZeroLineFollowUp(Guid runId, CancellationToken token = default) => _processStation.CompleteZeroLineFollowUp(runId, token);
        public void StartProtectWindowCompletionMonitoring(Func<string> selectedRecipe) => _processStation.StartProtectWindowCompletionMonitoring(selectedRecipe);
        public Task StopProtectWindowCompletionMonitoring() => _processStation.StopProtectWindowCompletionMonitoring();
        public ST_MANUAL_MOF_STATUS ManualMofStatus => _processStation.ManualMofStatus;

        public Task RunManualMofAsync(ST_STAGE_PROCESS_REQUEST request, IReadOnlySet<int> heads, CancellationToken token = default) => _processStation.RunManualMofAsync(request, heads, token);
        public Task StopManualMofAsync() => _processStation.StopManualMofAsync();
        public CStageRequestSequence StageRequests => _processStation.StageRequests;

        public void StartStageRequests(Func<CancellationToken, Task<ST_STAGE_PROCESS_REQUEST>> request, ST_STAGE_REVIEW_ACTIONS? review = null, ST_STAGE_TPM_ACTIONS? tpm = null) => _processStation.StartStageRequests(request, review, tpm);
        private readonly CStationProcess _processStation;
        public CStationManager(
            CInterfaceManager interfaceManager, 
            CMotionManager motionManager, 
            CInterLockManager interLockManager, 
            CSettingManager settingManager, 
            CAutomationScriptFileBase automationScriptFile, 
            CAutomationManager automationManager, 
            CReviewOffsetFileBase? reviewOffsetFile = null, 
            CProductManager? productManager = null, 
            CLogManager? logManager = null, 
            string? scriptDirectory = null, 
            CRecipeManager? recipeManager = null, 
            Review.CReviewManager? zeroLineReview = null
        )
        {
            _processStation = new CStationProcess(
                interfaceManager, 
                motionManager, 
                interLockManager, 
                settingManager, 
                automationScriptFile, 
                automationManager, 
                reviewOffsetFile, 
                productManager, 
                logManager, 
                scriptDirectory: scriptDirectory, 
                recipeManager: recipeManager, 
                zeroLineReview: zeroLineReview
            );
        }

        public ST_STATION_PROCESS_STATUS GetStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _processStation.Current;
        }

        public IReadOnlyList<ST_INTERLOCK_ITEM> GetLiveInterlockItems(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default) => _processStation.GetLiveInterlockItems(parameters, cancellationToken);
        public IReadOnlyList<ST_INTERLOCK_ITEM> GetLiveInterlockItems(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _processStation.GetLiveInterlockItems(cancellationToken);
        }

        public IReadOnlyList<ST_INTERLOCK_ITEM> GetManualInterlockItems(IReadOnlyDictionary<string, string> parameters, IReadOnlySet<int> selectedHeads, CancellationToken cancellationToken = default) => _processStation.GetManualInterlockItems(parameters, selectedHeads, cancellationToken);
        public IReadOnlyList<ST_STATION_STATUS> GetStationStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return[_processStation.Status];
        }

        public IReadOnlyList<ST_STATION_PROCESS_FLOW_ITEM> GetProcessFlow()
        {
            return CStationProcess.GetProcessFlow();
        }

        public ST_STATION_PROCESS_STATUS PrepareProcess(string processId, string recipeId, string glassId, string lotId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            if (StageRequests.IsRunning)
                throw new InvalidOperationException("AUTO Stage request monitoring is active.");
            return _processStation.PrepareProcess(processId, recipeId, glassId, lotId, parameters, cancellationToken);
        }

        public ST_STATION_PROCESS_STATUS Start(CancellationToken cancellationToken = default)
        {
            if (StageRequests.IsRunning)
                throw new InvalidOperationException("AUTO execution is controlled by Stage requests.");
            return _processStation.Start(cancellationToken);
        }

        public ST_STATION_PROCESS_STATUS Stop(CancellationToken cancellationToken = default)
        {
            return _processStation.Stop(cancellationToken);
        }

        public ST_STATION_PROCESS_STATUS Reset(CancellationToken cancellationToken = default)
        {
            if (StageRequests.IsRunning)
                throw new InvalidOperationException("Stop AUTO Stage request monitoring before RESET.");
            return _processStation.Reset(cancellationToken);
        }
    }

    public enum EN_SEQUENCE_ACTION_STATE
    {
        Waiting,
        Running,
        Completed,
        Error,
        Stopped
    }

    public sealed record ST_SEQUENCE_ACTION_STATUS(string Id, EN_SEQUENCE_ACTION_STATE State, string Detail = "");
    public static class CSequenceActionIds
    {
        public const string PrepareData = "DOREADY_DATA_PREPARE";
        public const string BuildUploadScript = "DOREADY_SCRIPT_BUILD_UPLOAD";
        public const string DoReadySignalsClear = "DOREADY_SIGNALS_CLEAR";
        public const string ProcessReadyPosition = "PROCESS_READY_POSITION";
        public const string ProcessMofZero = "PROCESS_MOF_ZERO";
        public const string ProcessTaskRun = "PROCESS_TASK_RUN";
        public const string ProcessMachining = "PROCESS_MACHINING_INSPECTION";
        public const string ProcessResults = "PROCESS_RESULTS";
        public const string ProcessZeroPrepare = "PROCESS_ZERO_PREPARE";
        public const string ProcessZeroMeasure = "PROCESS_ZERO_MEASURE";
        public const string ProcessZeroResults = "PROCESS_ZERO_RESULTS";
        public const string ProcessSignalsClear = "PROCESS_SIGNALS_CLEAR";
        public const string ReviewPlan = "REVIEW_PLAN";
        public const string ReviewMeasure = "REVIEW_MEASURE";
        public const string ReviewResults = "REVIEW_RESULTS";
        public const string ReviewSignalsClear = "REVIEW_SIGNALS_CLEAR";
    }

    /// <summary>In-memory execution feedback. Never holds its lock while an action executes.</summary>
    public sealed class CSequenceActionStatusStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, (long Run, ST_SEQUENCE_ACTION_STATUS Status)> _states = new(StringComparer.OrdinalIgnoreCase);
        private long _run;
        public IReadOnlyDictionary<string, ST_SEQUENCE_ACTION_STATUS> GetSnapshot()
        {
            lock (_sync)
                return _states.ToDictionary(pair => pair.Key, pair => pair.Value.Status, StringComparer.OrdinalIgnoreCase);
        }

        public void Reset()
        {
            lock (_sync)
                _states.Clear();
        }

        public void ResetToWaiting(IEnumerable<string> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            var selected = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var id in selected)
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
            lock (_sync)
            {
                foreach (var id in selected)
                    // A fresh run identity prevents an earlier completion from restoring DONE.
                    _states[id] = (++_run, new(id, EN_SEQUENCE_ACTION_STATE.Waiting));
            }
        }

        public void SetError(string id, string detail)
        {
            lock (_sync)
                if (_states.TryGetValue(id, out var entry))
                    _states[id] = (entry.Run, new(id, EN_SEQUENCE_ACTION_STATE.Error, detail));
        }

        public void Run(string id, Action action) => RunAsync(id, () =>
        {
            action();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        public async Task RunAsync(string id, Func<Task> action, bool resetToWaitingOnSuccess = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(action);
            long run;
            lock (_sync)
            {
                run = ++_run;
                _states[id] = (run, new(id, EN_SEQUENCE_ACTION_STATE.Running));
            }

            try
            {
                await action().ConfigureAwait(false);
                Finish(resetToWaitingOnSuccess ? EN_SEQUENCE_ACTION_STATE.Waiting : EN_SEQUENCE_ACTION_STATE.Completed, "");
            }
            catch (OperationCanceledException exception)
            {
                Finish(EN_SEQUENCE_ACTION_STATE.Stopped, exception.Message);
                throw;
            }
            catch (Exception exception)
            {
                Finish(EN_SEQUENCE_ACTION_STATE.Error, exception.Message);
                throw;
            }

            void Finish(EN_SEQUENCE_ACTION_STATE state, string detail)
            {
                lock (_sync)
                {
                    // An earlier execution must not republish after RESET or a new execution.
                    if (_states.TryGetValue(id, out var entry) && entry.Run == run)
                        _states[id] = (run, new(id, state, detail));
                }
            }
        }
    }

    public enum EN_SCRIPT_STATUS
    {
        NotCreated,
        Created,
        Running,
        Completed,
        Error
    }

    public enum EN_PROCESS_STEP
    {
        Idle,
        PowerCheck = 3,
        Align,
        Process,
        Inspection,
        Completed,
        Stopped,
        Error
    }

    public enum EN_STATION_SEQUENCE
    {
        Ready,
        PowerCheck = 3,
        Align,
        Process,
        Inspection,
        Complete,
        Stop,
        Reset,
        Error
    }

    public enum EN_HEAD_PROCESS_STATUS
    {
        Ready,
        Running,
        Completed,
        Error,
        Disabled
    }

    public enum EN_STATION_ID
    {
        Process = 0
    }

    public enum EN_STATION_STATE
    {
        Idle,
        Check,
        Process,
        Complete,
        Alarm,
        Stopped
    }

    public sealed record ST_HEAD_PROCESS_PLAN(
        int HeadNo, 
        double LaserPower, 
        double FrequencyKhz, 
        int ShotCount, 
        double MarkSpeed, 
        double JumpSpeed, 
        double ScannerXRampRate, 
        double ScannerYRampRate, 
        double ScannerZRampRate, 
        double ScannerZSpeed, 
        double ScannerAccLimit, 
        double DoeZPosition, 
        IReadOnlyList<ST_HEAD_SHOT_POINT> Shots
    )
    {
        public int AutomationNo { get; init; } = 0;
        public int TaskNo { get; init; } = HeadNo;
        public string ScriptFileName { get; init; } = $"PROCESS_H{HeadNo:00}.ascript";
    }

    public sealed record ST_PROCESS_MODEL(
        string ProcessId, 
        string RecipeId, 
        string GlassId, 
        string LotId, 
        IReadOnlyList<ST_HEAD_PROCESS_PLAN> HeadPlans, 
        IReadOnlyDictionary<string, string> Parameters, 
        DateTimeOffset CreatedAt
    );
    public sealed record ST_HEAD_PREVIEW_DATA(int HeadNo, EN_HEAD_PROCESS_STATUS Status, int ShotCount);
    public sealed record ST_PROCESS_RESULT(bool IsSuccess, string Message, DateTimeOffset CompletedAt, string ResultCode = "");
    public sealed record ST_PROCESS_DISPLAY_ITEM(string Name, string Value, string Detail = "");
    public sealed record ST_PROCESS_LOG_ITEM(DateTimeOffset OccurredAt, string Level, string Source, string Message);
    public sealed record ST_PROCESS_STATISTICS(int TotalShots, int MoveCount, int LaserOnSegments, TimeSpan EstimatedTime, TimeSpan ElapsedTime, double ProgressPercent);
    public sealed record ST_SCRIPT_BUILD_RESULT(string FileName, string FilePath, int TotalShots, DateTimeOffset CreatedAt, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> HeadScripts)
    {
        public long MofReadyRunId { get; init; }
    }

    public sealed record ST_HEAD_SCRIPT_RESULT(int HeadNo, int AutomationNo, int TaskNo, string FileName, string FilePath, int TotalShots, int TotalLines, DateTimeOffset CreatedAt);
    public enum EN_AEROTECH_PSO_MODE
    {
        Unused = 0,
        WindowMask = 1,
        ExtSync = 2,
        LaserMask = 3,
        ExtSyncGalvo = 4
    }

    public enum EN_AEROTECH_MODE
    {
        Mof = 0,
        Ifov = 1,
        Scanner = 2
    }

    public abstract class CAutomation1ScriptBase
    {
        public abstract string FileName { get; }
        public abstract string FilePath { get; }
        public abstract IReadOnlyList<string> Lines { get; }

        public abstract void Clear();
        public abstract void AddLine(string line);
        public abstract void Start(string title = "");
        public abstract void SetDeviceNo(int deviceNo);
        public abstract void SetNMarkDriveLaserControl(bool use);
        public abstract void SetScanPlannerStageEncoderMode(bool use);
        public abstract void DefaultSetting(double scannerAcc = 500000.0, int motionUpdateRate = 0, int executeLineCount = 110, bool resetPso = true);
        public abstract void DefaultFigureScanSetting(int motionUpdateRate = 100, int executeLineCount = 110, bool moveToCenter = true);
        public abstract void SetAxis(string xAxis, string yAxis, string? laserAxis = null);
        public abstract void SetStageAxis(string xAxis, string yAxis);
        public abstract void SetFrequency(double frequencyKhz);
        public abstract void SetLaserPower(double powerPercent, double outputRate = 100.0, bool analogOutputUse = false);
        public abstract void SetLaserPowerNoDelay(double powerPercent, double outputRate = 100.0);
        public abstract void SetPulseOnTimeLaserPower(double powerPercent, double dutyPercent, double outputRate = 100.0);
        public abstract void SetLaserMode(int mode);
        public abstract void SetLaserDelay(double onDelay, double offDelay);
        public abstract void SetJumpSpeed(double speedMeterPerSec);
        public abstract void SetJumpSpeedRate(double speedMeterPerSec, double rate = 1.0);
        public abstract void SetMarkSpeed(double speedMeterPerSec);
        public abstract void SetStageSpeed(double speedX, double speedY);
        public abstract void SetScannerAcc(double acc);
        public abstract void SetMarkAcc(double acc);
        public abstract void SetIFOV(bool use);
        public abstract void SetIFOVEmulatedQuadratureDivider();
        public abstract void SetIFOVIO(bool use = true);
        public abstract void SetIFOVScaleXY();
        public abstract void SetIFOVTime(long time);
        public abstract void SetIFOVSize(double size);
        public abstract void SetIFOVTrackingSpeed(long speed);
        public abstract void SetIFOVTrackingAccel(long acc);
        public abstract void SetIFOVPair(string xStageAxis, string yStageAxis, bool xDirection, bool yDirection);
        public abstract void SetIFOVSyncAxis();
        public abstract void SetMoveBlending(bool use);
        public abstract void SetAbsoluteMode();
        public abstract void SetIncrementalMode();
        public abstract void SetWaitModeAuto();
        public abstract void SetMoveDelay(double delaySeconds, bool addTactTime = true, bool applyMinimumDelay = true);
        public abstract void SetExecuteLineCount(int lineCount);
        public abstract void SetScannerRotate(double angle);
        public abstract void SetScannerRotate(string laserAxis, double angle);
        public abstract void SetMoveUpdateRate(int rate);
        public abstract void SetCoordinatedAccelLimit(long acc, long arcAcc);
        public abstract void SetTaskAccelLimit(long acc, long arcAcc);
        public abstract void SetScanTrajectoryFIRFilterX(long delay);
        public abstract void SetScanTrajectoryFIRFilterY(long delay);
        public abstract void SetStageTrajectoryFIRFilterX(long delay);
        public abstract void SetStageTrajectoryFIRFilterY(long delay);
        public abstract void SetProjection(string axis, double offsetX, double offsetY, double offsetT);
        public abstract void SetProjectionOff(string axis);
        public abstract void SetGearing(string masterAxis, string slaveAxis);
        public abstract void SetGearingOff(string slaveAxis = "AUTO");
        public abstract void SetSoftwareLimitSetup(bool use = true);
        public abstract void SetAerotechEncoderReset(string axisX, string axisY);
        public abstract void SetScanPlannerStageEncoder(string stageAxis);
        public abstract void SetEmulatedQuadratureDividerX(int value);
        public abstract void SetEmulatedQuadratureDividerY(int value);
        public abstract void SetStageEmulatedQuadratureDivider(int xValue, int yValue);
        public abstract void SetPSO(
            double pulseDistance, 
            double totalTime, 
            double laserOnTime, 
            double delay, 
            EN_AEROTECH_MODE mode, 
            EN_AEROTECH_PSO_MODE psoMode, 
            double frequencyKhz, 
            double powerPercent, 
            int windowMaskDirection, 
            double markSpeed, 
            bool manual = false
        );
        public abstract void SetPSODistance(double pulseDistance);
        public abstract void SetPSOOnOff(bool on);
        public abstract void SetPSOChangePower(double frequencyKhz, double powerPercent);
        public abstract void SetPSOFire(double totalTime, double laserOnTime, int count, double delay, EN_AEROTECH_MODE mode);
        public abstract void SetPSOLaserWindowMask(bool on, double windowStartRange = 0, double windowEndRange = 0);
        public abstract void DeclareEncoderVariable(string axis = "", bool useFeedback = false);
        public abstract void InitDeclareVariable();
        public abstract void InitDeclareVariableIFOV();
        public abstract void SetWaitForEncoder(string axis, double position, bool directionPlus = true);
        public abstract void SetWaitForEncoder(string axis, bool directionPlus, double position, double limit, double encoderScale = 1.0);
        public abstract void SetWaitForEncoder2Axis(string axisX, string axisY, bool inToOut, double posX, double posY, double limitX, double limitY);
        public abstract void SetWaitForStartAxis2(string axisX, string axisY, bool inToOut, double posX, double posY, double limitX, double limitY);
        public abstract void SetEncoderScaleFactor(string galvoAxis, string encoderAxis, int scale);
        public abstract void SetEncoderScaleFactor(string galvoAxis, string encoderAxis, bool directionPlus);
        public abstract void SetEncoderScaleFactor(string galvoAxis, string encoderAxis, double encoderX, double encoderY, bool directionPlus);
        public abstract void SetEncoderScaleFactorByPrimaryDivider(string galvoAxis, string encoderAxis, bool directionPlus);
        public abstract void InitEncoderCount(string galvoAxis);
        public abstract void EncoderNotFeedback(string axis);
        public abstract void ReleaseEncoderScaleFactor(string galvoAxis);
        public abstract void LaserAuto();
        public abstract void LaserOn();
        public abstract void LaserOff();
        public abstract void PsoLaserControl(bool on, bool manual = false);
        public abstract void LaserFire(bool on);
        public abstract void Jump(double x, double y);
        public abstract void Mark(double x, double y);
        public abstract void GCodeMove(double x, double y);
        public abstract void JumpRel(double x, double y);
        public abstract void MarkRel(double x, double y);
        public abstract void Arc(double startX, double startY, double endX, double endY, double centerX, double centerY, double angle);
        public abstract void JumpLinear(double x, double y);
        public abstract void WaitMoveDone();
        public abstract void Dwell(double delay);
        public abstract void EnableAxisPair();
        public abstract void DisableAxisPair();
        public abstract void FaultAckAxisPair();
        public abstract void HomeAxisPair();
        public abstract void OffsetClearAxisPair();
        public abstract void OffsetSetAxisPair(double x, double y);
        public abstract void SetSignalLogTrigger(bool use);
        public abstract void ProgramStart();
        public abstract void ProgramEnd();
        public abstract void BufferedEnd();
        public abstract void WaitInpos();
        public abstract void SetHomePos();
        public abstract void SetGalvoPosZero();
        public abstract void End(bool bufferedRun = false);
        public abstract ST_SCRIPT_BUILD_RESULT Save(CancellationToken cancellationToken = default);
    }

    public abstract class CAutomationScriptFileBase
    {
        public abstract string ScriptFileName { get; }

        public abstract CAutomation1ScriptBase Create(string? fileName = null);
        public abstract ST_SCRIPT_BUILD_RESULT Build(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken = default);
        public abstract ST_SCRIPT_BUILD_RESULT Build(ST_PROCESS_MODEL processModel, string subDirectoryName, CancellationToken cancellationToken = default);
        public abstract ST_SCRIPT_BUILD_RESULT LoadLatest(ST_PROCESS_MODEL processModel, string subDirectoryName, CancellationToken cancellationToken = default);
    }

    public sealed record ST_STATION_PROCESS_STATUS(
        ST_PROCESS_MODEL? ProcessModel, 
        IReadOnlyList<ST_HEAD_PREVIEW_DATA> HeadPreviews, 
        EN_SCRIPT_STATUS ScriptStatus, 
        EN_PROCESS_STEP ProcessStep, 
        ST_PROCESS_RESULT? Result, 
        IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> ProcessSequence, 
        IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> CurrentStepDetails, 
        IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> ProcessSummary, 
        IReadOnlyList<ST_PROCESS_LOG_ITEM> ProcessLogs, 
        IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> ScriptStatusItems, 
        IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> ScriptLifecycleItems, 
        IReadOnlyList<ST_INTERLOCK_ITEM> InterlockItems, 
        ST_PROCESS_STATISTICS Statistics
    )
    {
        public IReadOnlyDictionary<string, ST_SEQUENCE_ACTION_STATUS> SequenceActions { get; init; } = new Dictionary<string, ST_SEQUENCE_ACTION_STATUS>();
        public ST_STAGE_REQUEST_STATUS? StageRequest { get; init; }
        public ST_ZERO_LINE_PREWAIT_STATUS? ZeroLinePrewait { get; init; }
        public Review.ST_ZERO_LINE_CYCLE_STATUS? ZeroLine { get; init; }
    }

    public sealed record ST_STATION_STATUS(
        EN_STATION_ID StationId, 
        string StationName, 
        EN_STATION_STATE State, 
        EN_PROCESS_STEP ProcessStep, 
        EN_SCRIPT_STATUS ScriptStatus, 
        string LastMessage, 
        DateTimeOffset ChangedAt
    )
    {
        public EN_STATION_SEQUENCE? ExplicitSequenceState { get; init; }
        public EN_PROCESS_STEP? ExplicitSequenceProcessStep { get; init; }

        public EN_STATION_SEQUENCE SequenceState
        {
            get
            {
                return ExplicitSequenceState is { } sequenceState && ExplicitSequenceProcessStep == ProcessStep ? sequenceState : ResolveSequenceState(ProcessStep);
            }
        }

        public ST_STATION_STATUS WithSequenceState(EN_STATION_SEQUENCE? sequenceState)
        {
            if (sequenceState is null || sequenceState == ResolveSequenceState(ProcessStep))
            {
                return this with
                {
                    ExplicitSequenceState = null,
                    ExplicitSequenceProcessStep = null
                };
            }

            return this with
            {
                ExplicitSequenceState = sequenceState,
                ExplicitSequenceProcessStep = ProcessStep
            };
        }

        private static EN_STATION_SEQUENCE ResolveSequenceState(EN_PROCESS_STEP processStep)
        {
            return processStep switch
            {
                EN_PROCESS_STEP.Idle => EN_STATION_SEQUENCE.Ready,
                EN_PROCESS_STEP.PowerCheck => EN_STATION_SEQUENCE.PowerCheck,
                EN_PROCESS_STEP.Align => EN_STATION_SEQUENCE.Align,
                EN_PROCESS_STEP.Process => EN_STATION_SEQUENCE.Process,
                EN_PROCESS_STEP.Inspection => EN_STATION_SEQUENCE.Inspection,
                EN_PROCESS_STEP.Completed => EN_STATION_SEQUENCE.Complete,
                EN_PROCESS_STEP.Stopped => EN_STATION_SEQUENCE.Stop,
                EN_PROCESS_STEP.Error => EN_STATION_SEQUENCE.Error,
                _ => EN_STATION_SEQUENCE.Error
            };
        }
    }

    public sealed record ST_STATION_PROCESS_FLOW_ITEM(
        int Order, 
        string StepKey, 
        string StepName, 
        EN_STATION_STATE RunningState, 
        EN_PROCESS_STEP RunningStep, 
        EN_SCRIPT_STATUS ScriptStatus, 
        string OnSuccess, 
        string OnFail
    );
}
