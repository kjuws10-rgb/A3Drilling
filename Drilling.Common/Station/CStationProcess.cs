namespace Drilling.Common.Station
{
    using System.Globalization;
    using Drilling.Common.Interface;
    using System.Diagnostics;
    using Drilling.Common.Managers;
    using Drilling.Common.Recipe;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using Drilling.Common.Log;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Product;
    using Drilling.Common.Automation;
    using Drilling.Common.Review;
    using Drilling.Common.Station;
    using Drilling.Common.Threading;

    public sealed partial class CStationProcess : CtrlThread
    {
#region 공유 상태·초기값
        private const string AutoStepIdle = "IDLE";
        private const string AutoStepPowerCheck = "POWER_CHECK";
        private const string AutoStepAlign = "ALIGN";
        private const string AutoStepProcess = "PROCESS";
        private const string AutoStepInspection = "INSPECTION";
        private const string AutoStepComplete = "COMPLETE";
        private const string ScriptBufferedRunUseKey = "SCRIPT_BUFFERED_RUN_USE";
        private const string ScriptRunTimeoutMsKey = "SCRIPT_RUN_TIMEOUT_MS";
        private const string ScriptRunPollIntervalMsKey = "SCRIPT_RUN_POLL_INTERVAL_MS";
        private const string ScriptBufferedRunLinesPerCommandKey = "SCRIPT_BUFFERED_RUN_LINES_PER_COMMAND";
        private const string ScriptBufferedRunTimeoutMsKey = "SCRIPT_BUFFERED_RUN_TIMEOUT_MS";
        private const string ScriptBufferedRunQueueSizeKey = "SCRIPT_BUFFERED_RUN_QUEUE_SIZE";
        private const string StandardScriptDirectoryName = "Standard";
        private const string BufferedRunScriptDirectoryName = "BufferedRun";
        private const string QueueCountLogDirectoryName = "QueueCount";
        private const string ScannerStatusLogDirectoryName = "ScannerStatus";
        private const int ScriptRunDefaultTimeoutMs = 600000;
        private const int ScriptRunDefaultPollIntervalMs = 100;
        private const int EncoderScaleReleaseTimeoutMs = 5000;
        private const int QueueCountLogIntervalMs = 5000;
        private const int ScannerStatusLogIntervalMs = 1000;
        private const int QueueCountLogStopWaitMs = 2000;
        private const int MaximumProcessLogCount = 500;
        private const string AutoStepWait = "WAIT";
        private const string AutoStepRunning = "RUNNING";
        private const string AutoStepOk = "OK";
        private const string AutoStepDone = "DONE";
        private const string AutoStepError = "ERROR";
        private const string AutoStepStop = "STOP";
        private static readonly IReadOnlyList<ST_AUTO_STEP_INFO> AutoStepInfos = [
            new(AutoStepIdle, "IDLE"), 
            new(AutoStepPowerCheck, "POWERCHECK"), 
            new(AutoStepAlign, "ALIGN"), 
            new(AutoStepProcess, "PROCESS"), 
            new(AutoStepInspection, "INSPECTION"), 
            new(AutoStepComplete, "COMPLETE")
        ];
        private static readonly IReadOnlyList<ST_STATION_PROCESS_FLOW_ITEM> ProcessFlowItems = [
            new(1, AutoStepIdle, "IDLE", EN_STATION_STATE.Idle, EN_PROCESS_STEP.Idle, EN_SCRIPT_STATUS.NotCreated, AutoStepPowerCheck, "ALARM"), 
            new(2, AutoStepPowerCheck, "POWERCHECK", EN_STATION_STATE.Check, EN_PROCESS_STEP.PowerCheck, EN_SCRIPT_STATUS.NotCreated, AutoStepAlign, "ALARM"), 
            new(3, AutoStepAlign, "ALIGN", EN_STATION_STATE.Process, EN_PROCESS_STEP.Align, EN_SCRIPT_STATUS.NotCreated, AutoStepProcess, "ALARM"), 
            new(4, AutoStepProcess, "PROCESS", EN_STATION_STATE.Process, EN_PROCESS_STEP.Process, EN_SCRIPT_STATUS.Running, AutoStepInspection, "ALARM"), 
            new(5, AutoStepInspection, "INSPECTION", EN_STATION_STATE.Process, EN_PROCESS_STEP.Inspection, EN_SCRIPT_STATUS.Running, AutoStepComplete, "ALARM"), 
            new(6, AutoStepComplete, "COMPLETE", EN_STATION_STATE.Complete, EN_PROCESS_STEP.Completed, EN_SCRIPT_STATUS.Completed, "IDLE/RESET", "ALARM")
        ];
        private readonly CInterfaceManager _interfaceManager;
        private readonly CMotionManager _motionManager;
        private readonly CInterLockManager _interLockManager;
        private readonly CSettingManager _settingManager;
        private readonly CProductManager? _productManager;
        private readonly CRecipeManager? _recipeManager;
        private readonly CReviewManager? _zeroLineReview;
        private readonly CProtectWindowUsageRequest? _protectWindowUsageRequest;
        private readonly CReviewOffsetFileBase? _reviewOffsetFile;
        private readonly CAutomationScriptFileBase _automationScriptFile;
        private readonly CAutomationManager _automationManager;
        private readonly CLogManager? _logManager;
        private readonly object _sequenceLock = new();
        private readonly object _processLogLock = new();
        private readonly CSequenceActionStatusStore _sequenceActions = new();
        private readonly List<ST_PROCESS_LOG_ITEM> _processLogs = [];
        private readonly Dictionary<string, string> _autoStepStates = CreateAutoStepStateMap();
        private DateTimeOffset? _scriptCreatedAt;
        private DateTimeOffset? _scriptStartedAt;
        private DateTimeOffset? _scriptCompletedAt;
        private ST_SCRIPT_BUILD_RESULT? _lastScript;
        private IReadOnlyList<ST_INTERLOCK_ITEM> _lastInterLockItems = [];
        private ST_PROCESS_MODEL? _processModel;
        private ST_PROCESS_STATISTICS _statistics = EmptyStatistics();
        private ST_STATION_PROCESS_STATUS _snapshot;
        private ST_STATION_STATUS _stationStatus;
        private EN_STATION_SEQUENCE _sequenceState = EN_STATION_SEQUENCE.Ready;
        private CancellationTokenSource? _sequenceCancellation;
        private string _sequenceError = "";
        private readonly CSequenceActionStatusStore _manualMofActions = new();
        private CancellationTokenSource? _manualMofCancellation;
        private Task _manualMofExecution = Task.CompletedTask;
        private bool _manualMofRunning;
        private bool _manualMofRequiresStop;
        private string _manualMofMessage = "";
        private string _manualMofDirectory = "";
        private readonly SemaphoreSlim _protectWindowCountGate = new(1, 1);
        private bool _protectWindowCounted;
        private bool _stageMachiningCompleted;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Controller, int Task), ST_HEAD_SCRIPT_RESULT> _stageOwnedTasks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CStageBufferedRun> _stageBufferedRuns = new();
        private string _stageModelLogCycle = "";
        private CZeroLineProcessCoordinator? _zeroLineCycle;
        private CZeroLinePrewaitMonitor? _zeroLinePrewait;
#endregion
#region 생성·초기화
        public CStationProcess(
            CInterfaceManager interfaceManager, 
            CMotionManager motionManager, 
            CInterLockManager interLockManager, 
            CSettingManager settingManager, 
            CAutomationScriptFileBase automationScriptFile, 
            CAutomationManager automationManager, 
            CReviewOffsetFileBase? reviewOffsetFile = null, 
            CProductManager? productManager = null, 
            CLogManager? logManager = null, 
            string stationName = "PROCESS", 
            string? scriptDirectory = null, 
            CRecipeManager? recipeManager = null, 
            CReviewManager? zeroLineReview = null
        )
        {
            _interfaceManager = interfaceManager;
            _motionManager = motionManager;
            _interLockManager = interLockManager;
            _settingManager = settingManager;
            _productManager = productManager;
            _recipeManager = recipeManager;
            _zeroLineReview = zeroLineReview;
            if (recipeManager is not null)
                _protectWindowUsageRequest = new(recipeManager, interfaceManager, message => AddProcessLog("INFO", "PROTECT_WINDOW", message));
            _reviewOffsetFile = reviewOffsetFile;
            _automationScriptFile = automationScriptFile;
            _automationManager = automationManager;
            _logManager = logManager;
            _snapshot = CreateSnapshot(null, [], EN_SCRIPT_STATUS.NotCreated, EN_PROCESS_STEP.Idle, null);
            _stationStatus = new ST_STATION_STATUS(EN_STATION_ID.Process, stationName, EN_STATION_STATE.Idle, EN_PROCESS_STEP.Idle, EN_SCRIPT_STATUS.NotCreated, "Station idle.", DateTimeOffset.Now);
            StageRequests = CreateStageRequestSequence();
            if (_logManager is not null)
                _interfaceManager.StageReviewSequences.Trace += item => AddProcessLog("INFO", "STAGE_MOVE_IOF", FormattableString.Invariant($"Event={item.Number}; MELSEC_{item.DeviceNo}; {item.Phase}; Value={item.Value}; {item.Detail}"));
        }

#endregion
#region 기본 처리
        private delegate void CProcessStepAction(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken);
        public ST_STATION_PROCESS_STATUS Current
        {
            get
            {
                var actions = _sequenceActions.GetSnapshot().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                if (_interfaceManager is not null)
                    foreach (var pair in _interfaceManager.StageReviewSequences.Actions.GetSnapshot())
                        actions[pair.Key] = pair.Value;
                return _snapshot with
                {
                    SequenceActions = actions,
                    StageRequest = StageRequests.Status,
                    ZeroLinePrewait = _zeroLinePrewait?.Status,
                    ZeroLine = _zeroLineCycle?.Status
                };
            }
        }

        public ST_STATION_STATUS Status
        {
            get
            {
                return ReadStationStatus();
            }
        }

        public EN_STATION_SEQUENCE SequenceState
        {
            get
            {
                return ReadSequenceState();
            }
        }

        public static IReadOnlyList<ST_STATION_PROCESS_FLOW_ITEM> GetProcessFlow()
        {
            return ProcessFlowItems;
        }

        public ST_STATION_PROCESS_STATUS PrepareProcess(
            string processId, 
            string recipeId, 
            string glassId, 
            string lotId, 
            IReadOnlyDictionary<string, string> parameters, 
            CancellationToken cancellationToken = default, 
            bool preserveSequenceActions = false
        )
        {
            lock (_sequenceLock)
            {
                if (_manualMofRunning || _manualMofRequiresStop || ReadSequenceState() != EN_STATION_SEQUENCE.Ready)
                    throw new InvalidOperationException("Station is busy. PrepareProcess is not allowed during processing.");
                return PrepareProcessCore(processId, recipeId, glassId, lotId, parameters, cancellationToken, preserveSequenceActions);
            }
        }

        private ST_STATION_PROCESS_STATUS PrepareProcessCore(
            string processId, 
            string recipeId, 
            string glassId, 
            string lotId, 
            IReadOnlyDictionary<string, string> parameters, 
            CancellationToken cancellationToken, 
            bool preserveSequenceActions, 
            IReadOnlySet<int>? manualHeads = null
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearProcessLogs();
            _lastInterLockItems = [];
            _scriptCreatedAt = null;
            _scriptStartedAt = null;
            _scriptCompletedAt = null;
            _lastScript = null;
            ResetAutoStepStatesCore(preserveSequenceActions);
            SetSequenceState(EN_STATION_SEQUENCE.Ready);
            var runtimeParameters = BuildRuntimeParameters(recipeId, parameters, cancellationToken);
            if (manualHeads is not null)
            {
                var scoped = new Dictionary<string, string>(runtimeParameters, StringComparer.OrdinalIgnoreCase);
                for (var head = 1; head <= 8; head++)
                    scoped[$"H{head:00}_USE"] = manualHeads.Contains(head) ? "ON" : "OFF";
                runtimeParameters = scoped;
            }

            _processModel = BuildProcessModel(processId, recipeId, glassId, lotId, runtimeParameters);
            _stageModelLogCycle = StageRequests?.LogCycleId ?? "";
            _protectWindowCounted = false;
            var preview = BuildPreview(_processModel, EN_HEAD_PROCESS_STATUS.Ready);
            _statistics = BuildStatistics(_processModel, 0.0, TimeSpan.Zero);
            AddProcessLog("INFO", "SCAN_PC", $"Process model prepared ({_processModel.ProcessId})");
            AddProcessLog("INFO", "PARSER", $"Head parameter parsed ({preview.Count} heads / {_statistics.TotalShots} shots)");
            SetAutoStepState(AutoStepIdle, AutoStepOk);
            _snapshot = CreateSnapshot(_processModel, preview, EN_SCRIPT_STATUS.NotCreated, EN_PROCESS_STEP.Idle, null);
            SetStationState(EN_STATION_STATE.Idle, EN_PROCESS_STEP.Idle, EN_SCRIPT_STATUS.NotCreated, $"Process model prepared: {_processModel.ProcessId}");
            return _snapshot;
        }

        public ST_STATION_PROCESS_STATUS Start(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ST_PROCESS_MODEL processModel;
            lock (_sequenceLock)
            {
                if (_sequenceState != EN_STATION_SEQUENCE.Ready)
                {
                    return _snapshot;
                }

                if (_manualMofRunning || _manualMofRequiresStop)
                    throw new InvalidOperationException("Manual MOF is active or requires STOP.");
                EnsureStartAllowed();
                processModel = ReadProcessModelForStart();
            }

            var startInterLock = GetInterLockSummary(cancellationToken, processModel);
            if (!startInterLock.CanAutoRun)
            {
                throw new InvalidOperationException(FormatStartInterLockBlockedMessage(startInterLock));
            }

            lock (_sequenceLock)
            {
                if (_sequenceState != EN_STATION_SEQUENCE.Ready)
                {
                    return _snapshot;
                }

                if (_manualMofRunning || _manualMofRequiresStop)
                    throw new InvalidOperationException("Manual MOF is active or requires STOP.");
                EnsureStartAllowed();
                _sequenceCancellation?.Dispose();
                _sequenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _sequenceError = "";
                _sequenceState = EN_STATION_SEQUENCE.PowerCheck;
                _stationStatus = (_stationStatus with
                {
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(EN_STATION_SEQUENCE.PowerCheck);
            }

            base.Start(10, "StationProcess");
            return _snapshot;
        }

        public override void Run()
        {
            EN_STATION_SEQUENCE sequenceState;
            lock (_sequenceLock)
            {
                // RESET can leave CtrlThread alive. Manual MOF owns its own async
                // execution; the legacy AUTO tick must never execute the same model.
                if (_manualMofRunning || _manualMofRequiresStop)
                    return;
                sequenceState = _sequenceState;
            }

            try
            {
                switch (sequenceState)
                {
                    case EN_STATION_SEQUENCE.Ready:
                        return;
                    case EN_STATION_SEQUENCE.PowerCheck:
                        RunSequenceStep(EN_STATION_SEQUENCE.PowerCheck, AutoStepPowerCheck, RunPowerCheckStep, EN_STATION_SEQUENCE.Align);
                        break;
                    case EN_STATION_SEQUENCE.Align:
                        RunSequenceStep(EN_STATION_SEQUENCE.Align, AutoStepAlign, RunAlignStep, EN_STATION_SEQUENCE.Process);
                        break;
                    case EN_STATION_SEQUENCE.Process:
                        RunSequenceStep(EN_STATION_SEQUENCE.Process, AutoStepProcess, RunProcessStep, EN_STATION_SEQUENCE.Inspection);
                        break;
                    case EN_STATION_SEQUENCE.Inspection:
                        RunSequenceStep(EN_STATION_SEQUENCE.Inspection, AutoStepInspection, RunInspectionStep, EN_STATION_SEQUENCE.Complete);
                        break;
                    case EN_STATION_SEQUENCE.Complete:
                        RunSequenceStep(EN_STATION_SEQUENCE.Complete, AutoStepComplete, CompleteSequence, EN_STATION_SEQUENCE.Ready);
                        FinishSequence();
                        break;
                    case EN_STATION_SEQUENCE.Stop:
                        ProcessStop(CancellationToken.None);
                        FinishSequence();
                        break;
                    case EN_STATION_SEQUENCE.Reset:
                        ProcessReset(CancellationToken.None);
                        FinishSequence();
                        break;
                    case EN_STATION_SEQUENCE.Error:
                        SetAlarm(_sequenceError, CancellationToken.None);
                        FinishSequence();
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                SetSequenceState(EN_STATION_SEQUENCE.Stop);
            }
            catch (Exception exception)when (exception is InvalidOperationException or TimeoutException or IOException or KeyNotFoundException)
            {
                _sequenceError = exception.Message;
                SetSequenceState(EN_STATION_SEQUENCE.Error);
            }
        }

        private void RunSequenceStep(EN_STATION_SEQUENCE expectedSequence, string stepKey, CProcessStepAction action, EN_STATION_SEQUENCE nextSequence)
        {
            var cancellationToken = GetSequenceCancellationToken();
            var processModel = CheckProcessModelLoaded();
            ExecuteAutoStep(stepKey, processModel, action, cancellationToken);
            if (!TryMoveSequenceState(expectedSequence, nextSequence))
            {
                ProcessInterruptedSequence();
            }
        }

        private ST_PROCESS_MODEL CheckProcessModelLoaded()
        {
            return _processModel ?? throw new InvalidOperationException("Process Model is not loaded.");
        }

        private CancellationToken GetSequenceCancellationToken()
        {
            lock (_sequenceLock)
            {
                return _sequenceCancellation?.Token ?? CancellationToken.None;
            }
        }

        private void FinishSequence()
        {
            lock (_sequenceLock)
            {
                _sequenceState = EN_STATION_SEQUENCE.Ready;
                _stationStatus = (_stationStatus with
                {
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(EN_STATION_SEQUENCE.Ready);
                _sequenceCancellation?.Dispose();
                _sequenceCancellation = null;
            }
        }

        private void ProcessInterruptedSequence()
        {
            var sequenceState = ReadSequenceState();
            if (sequenceState == EN_STATION_SEQUENCE.Stop)
            {
                throw new OperationCanceledException();
            }

            throw new InvalidOperationException($"Station sequence entered {sequenceState} during auto start.");
        }

        private void CompleteSequence(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            CompleteProcess(cancellationToken);
        }

        private void EnsureStartAllowed()
        {
            if (ReadStationStatus().State == EN_STATION_STATE.Alarm)
            {
                throw new InvalidOperationException("Station alarm is active. Reset alarm before auto start.");
            }
        }

        private ST_PROCESS_MODEL ReadProcessModelForStart()
        {
            if (_snapshot.ProcessModel is not null)
            {
                return _snapshot.ProcessModel;
            }

            SetAutoStepState(AutoStepIdle, AutoStepError);
            SetSequenceState(EN_STATION_SEQUENCE.Error);
            throw new InvalidOperationException("Process model is not loaded.");
        }

        public IReadOnlyList<ST_INTERLOCK_ITEM> GetLiveInterlockItems(CancellationToken cancellationToken = default) => EvaluateInterlocks(_snapshot.ProcessModel?.Parameters, cancellationToken, cachedStatusOnly: true, requireAllHeadsUsed: true).Items;
        public IReadOnlyList<ST_INTERLOCK_ITEM> GetLiveInterlockItems(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default) => EvaluateInterlocks(parameters, cancellationToken, cachedStatusOnly: true, requireAllHeadsUsed: true).Items;
        public IReadOnlyList<ST_INTERLOCK_ITEM> GetManualInterlockItems(IReadOnlyDictionary<string, string> parameters, IReadOnlySet<int> selectedHeads, CancellationToken cancellationToken = default)
        {
            var heads = selectedHeads.Where(head => head is >= 1 and <= 8).ToHashSet();
            var manualParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            for (var head = 1; head <= 8; head++)
                manualParameters[$"H{head:00}_USE"] = heads.Contains(head) ? "ON" : "OFF";
            // Same rules and shared device snapshots as Main, without changing AUTO's published interlocks.
            var items = EvaluateInterlocks(manualParameters, cancellationToken, cachedStatusOnly: true, publish: false).Items;
            var firstBet = _interfaceManager.GetInterfaceList(EN_EQP_MODULE.Bet).FirstOrDefault();
            return items.Select(item => item.HeadNo is null && item.Signal.Equals("BET", StringComparison.OrdinalIgnoreCase) && firstBet is { Number: >= 0 and < 8 } ? item with { HeadNo = firstBet.Number + 1 } : item).Where(item => item.HeadNo is not > 0 || heads.Contains(item.HeadNo.Value)).ToArray();
        }

        private void ExecuteAutoStep(string stepKey, ST_PROCESS_MODEL processModel, CProcessStepAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepName = GetAutoStepName(stepKey);
            var flowItem = GetProcessFlowItem(stepKey);
            var runningScriptStatus = ResolveStepScriptStatus(stepKey, flowItem.ScriptStatus);
            SetAutoStepState(stepKey, AutoStepRunning);
            AddProcessLog("INFO", "STEP", $"{stepName} started.");
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, SetHeadStatus(_snapshot.HeadPreviews, flowItem.RunningStep), runningScriptStatus, flowItem.RunningStep, null);
            SetStationState(flowItem.RunningState, flowItem.RunningStep, runningScriptStatus, $"{stepName} started.");
            RefreshSnapshot();
            try
            {
                action(processModel, cancellationToken);
                SetAutoStepState(stepKey, stepKey == AutoStepComplete ? AutoStepDone : AutoStepOk);
                AddProcessLog("INFO", "STEP", $"{stepName} completed.");
                RefreshSnapshot();
            }
            catch (Exception exception)
            {
                SetAutoStepState(stepKey, AutoStepError);
                AddProcessLog("ERROR", "STEP", $"{stepName} failed. {exception.Message}");
                RefreshSnapshot();
                throw;
            }
        }

        private void RunAlignStep(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = processModel.Parameters;
            var reviewCameraAlignKeyPosX = ReadDoubleAny(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
            var reviewCameraAlignKeyPosY = ReadDoubleAny(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var stageYSpeed = ReadDoubleAny(parameters, 100.0, "STAGE_SPEED");
            try
            {
                UpdateProductAlignment(processModel, EN_PRODUCT_PROCESS_STATE.Running, "RUNNING", cancellationToken);
                SendProcessControlCommand("ALIGN_REVIEW_CAMERA_MOVE", [("CAMERA_X", FormatDouble(reviewCameraAlignKeyPosX)), ("STAGE_Y", FormatDouble(reviewCameraAlignKeyPosY)), ("SPEED_Y", FormatDouble(stageYSpeed))], cancellationToken);
                UpdateProductAlignment(processModel, EN_PRODUCT_PROCESS_STATE.Completed, "OK", cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryUpdateProductAlignmentAfterFailure(processModel, EN_PRODUCT_PROCESS_STATE.Stopped, "STOPPED");
                throw;
            }
            catch
            {
                TryUpdateProductAlignmentAfterFailure(processModel, EN_PRODUCT_PROCESS_STATE.Error, "ERROR");
                throw;
            }

            AddProcessLog("INFO", "ALIGN", $"Review camera align key position requested. CameraX={FormatDouble(reviewCameraAlignKeyPosX)}, StageY={FormatDouble(reviewCameraAlignKeyPosY)}");
        }

        private void UpdateProductAlignment(ST_PROCESS_MODEL processModel, EN_PRODUCT_PROCESS_STATE state, string result, CancellationToken cancellationToken)
        {
            var productId = GetCurrentProcessProductId();
            if (_productManager is null || string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            var parameters = processModel.Parameters;
            var includeResults = state == EN_PRODUCT_PROCESS_STATE.Completed;
            _productManager.SetAlignment(
                productId, 
                new ST_PRODUCT_ALIGN_DATA(
                state, 
                result, 
                includeResults ? ReadNullableDoubleAny(parameters, "ALIGN_OFFSET_X") : null, 
                includeResults ? ReadNullableDoubleAny(parameters, "ALIGN_OFFSET_Y") : null, 
                includeResults ? ReadNullableDoubleAny(parameters, "ALIGN_ROTATION") : null
            ), 
                new ST_PRODUCT_DISTORTION_DATA(state, result, includeResults ? ReadNullableDoubleAny(parameters, "DISTORTION_MAX_DA", "MAX_DA") : null), 
                cancellationToken
            ).GetAwaiter().GetResult();
        }

        private void TryUpdateProductAlignmentAfterFailure(ST_PROCESS_MODEL processModel, EN_PRODUCT_PROCESS_STATE state, string result)
        {
            try
            {
                UpdateProductAlignment(processModel, state, result, CancellationToken.None);
            }
            catch (Exception updateException)
            {
                AddProcessLog("ERROR", "PRODUCT", $"Product align state update failed. {updateException.Message}");
            }
        }

        private void RunProcessStep(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            _sequenceActions.Run(CSequenceActionIds.BuildUploadScript, () =>
            {
                BuildAutomationScript(processModel, cancellationToken);
                UploadAutomationScript(processModel, cancellationToken);
            });
            //test �Ⱓ���� �ӽ� �ּ�
            //MoveStageToScanStart(processModel, cancellationToken);
            RunScannerProcess(processModel, cancellationToken);
        }

        private void MoveStageToScanStart(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = processModel.Parameters;
            var startX = ReadDoubleAny(parameters, 0.0, "STAGE_START_POS_X");
            var startY = ReadDoubleAny(parameters, 0.0, "STAGE_START_POS_Y");
            var reviewCameraAlignKeyPosX = ReadDoubleAny(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
            var reviewCameraAlignKeyPosY = ReadDoubleAny(parameters, startY, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var reviewToHead1GapY = ReadDoubleAny(parameters, 0.0, "REVIEW_TO_HEAD1_GAP_Y");
            var headGapY = ReadDoubleAny(parameters, 0.0, "HeadGapY");
            var head1AlignKeyY = reviewCameraAlignKeyPosY + reviewToHead1GapY;
            var head2AlignKeyY = head1AlignKeyY + headGapY;
            var delayLengthY = Math.Abs(ReadDoubleAny(parameters, 0.0, "SCAN_START_DELAY_LENGTH_Y"));
            var stageScanDirectionY = ReadDirectionAny(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
            var firstShotStageY = ResolveFirstShotStageY(processModel, stageScanDirectionY);
            var scanStartY = reviewCameraAlignKeyPosY + firstShotStageY - (stageScanDirectionY * delayLengthY);
            var stageYSpeed = ReadDoubleAny(parameters, 100.0, "STAGE_SPEED");
            SendProcessControlCommand("PROCESS_STAGE_START_MOVE", [("X", FormatDouble(startX)), ("Y", FormatDouble(startY)), ("SPEED_Y", FormatDouble(stageYSpeed))], cancellationToken);
            SendProcessControlCommand("PROCESS_STAGE_SCAN_START_MOVE", [
                ("Y", FormatDouble(scanStartY)), 
                ("REVIEW_CAMERA_X", FormatDouble(reviewCameraAlignKeyPosX)), 
                ("REVIEW_CAMERA_ALIGN_KEY_Y", FormatDouble(reviewCameraAlignKeyPosY)), 
                ("REVIEW_TO_HEAD1_GAP_Y", FormatDouble(reviewToHead1GapY)), 
                ("HeadGapY", FormatDouble(headGapY)), 
                ("HEAD1_ALIGN_KEY_Y", FormatDouble(head1AlignKeyY)), 
                ("HEAD2_ALIGN_KEY_Y", FormatDouble(head2AlignKeyY)), 
                ("FIRST_SHOT_STAGE_Y", FormatDouble(reviewCameraAlignKeyPosY + firstShotStageY)), 
                ("DELAY_LENGTH_Y", FormatDouble(delayLengthY)), 
                ("SCAN_DIRECTION_Y", FormatDouble(stageScanDirectionY)), 
                ("SPEED_Y", FormatDouble(stageYSpeed))
            ], cancellationToken);
            AddProcessLog("INFO", "STAGE", $"Stage scan start ready. Start=({FormatDouble(startX)}, {FormatDouble(startY)}), ReviewCameraAlignKey=({FormatDouble(reviewCameraAlignKeyPosX)}, {FormatDouble(reviewCameraAlignKeyPosY)}), ReviewToHead1GapY={FormatDouble(reviewToHead1GapY)}, HeadGapY={FormatDouble(headGapY)}, FirstShotY={FormatDouble(reviewCameraAlignKeyPosY + firstShotStageY)}, DelayY={FormatDouble(delayLengthY)}, ScanStartY={FormatDouble(scanStartY)}");
        }

        private double ResolveFirstShotStageY(ST_PROCESS_MODEL processModel, double stageScanDirectionY)
        {
            global::System.Collections.Generic.IEnumerable<global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> SelectShots(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
            {
                return head.Shots;
            }

            var shots = processModel.HeadPlans.SelectMany(SelectShots).ToArray();
            if (shots.Length == 0)
            {
                var reviewToHead1GapY = ReadDoubleAny(processModel.Parameters, 0.0, "REVIEW_TO_HEAD1_GAP_Y");
                var headGapY = ReadDoubleAny(processModel.Parameters, 0.0, "HeadGapY");
                return reviewToHead1GapY + headGapY;
            }

            double SelectStageY(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.StageY;
            }

            double SelectStageY2(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.StageY;
            }

            return stageScanDirectionY < 0.0 ? shots.Max(SelectStageY) : shots.Min(SelectStageY2);
        }

        private void SendProcessControlCommand(string commandName, IReadOnlyList<(string Key, string Value)> arguments, CancellationToken cancellationToken)
        {
            var command = FormatCommand(commandName, arguments);
            AddProcessLog("INFO", "STAGE", $"Stage command requested. {command}");
            var response = _interfaceManager.ExecuteFunction(EN_EQP_MODULE.WonikCtrl, 0, command, cancellationToken).GetAwaiter().GetResult();
            if (!IsProcessControlSuccessResponse(response))
            {
                AddProcessLog("ERROR", "STAGE", $"Stage command rejected. {commandName}: {FormatResponseForLog(response)}");
                throw new InvalidOperationException($"Stage command failed: {commandName}. Response={FormatResponseForLog(response)}");
            }

            AddProcessLog("INFO", "STAGE", $"Stage command accepted. {response}");
        }

        private void RunInspectionStep(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            RunReviewStep(processModel, cancellationToken);
        }

        private void BuildAutomationScript(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            var bufferedRun = IsBufferedRunEnabled(processModel.Parameters);
            var scriptDirectoryName = bufferedRun ? BufferedRunScriptDirectoryName : StandardScriptDirectoryName;
            _lastScript = _automationScriptFile.Build(GetProcessModel(), scriptDirectoryName, cancellationToken);
            _scriptCreatedAt = _lastScript.CreatedAt;
            _statistics = BuildStatistics(processModel, 35.0, TimeSpan.FromSeconds(12));
            AddProcessLog("INFO", "SCRIPT", $"{_lastScript.FileName} generated in {scriptDirectoryName}. ({_lastScript.TotalShots} shots)");
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, _snapshot.HeadPreviews, EN_SCRIPT_STATUS.Created, EN_PROCESS_STEP.Process, null);
        }

        private void UploadAutomationScript(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lastScript is null)
            {
                throw new InvalidOperationException("Automation script is not built.");
            }

            bool ShouldIncludeItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.TotalShots > 0;
            }

            foreach (var headScript in _lastScript.HeadScripts.Where(ShouldIncludeItem))
            {
                AddProcessLog("INFO", "A1_UPLOAD", $"Upload requested. H{headScript.HeadNo:00} -> Automation1 #{headScript.AutomationNo}, {headScript.FileName}");
                var response = _automationManager.UploadScript(headScript.FilePath, headScript.FileName, headScript.AutomationNo, cancellationToken: cancellationToken).GetAwaiter().GetResult();
                var simulationSucceeded = !string.IsNullOrWhiteSpace(response) && response.StartsWith("SIM:", StringComparison.OrdinalIgnoreCase) && response.EndsWith(":OK", StringComparison.OrdinalIgnoreCase);
                if (!IsProcessControlSuccessResponse(response) && !simulationSucceeded)
                    throw new InvalidOperationException($"Automation1 script upload failed. H{headScript.HeadNo:00}, {headScript.FileName}: {FormatResponseForLog(response)}");
                AddProcessLog("INFO", "A1_UPLOAD", $"Upload completed. H{headScript.HeadNo:00}, Automation1 #{headScript.AutomationNo}: {response}");
            }
        }

        private void RunScannerProcess(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            SetStationState(EN_STATION_STATE.Process, EN_PROCESS_STEP.Process, EN_SCRIPT_STATUS.Running, $"Scanner run started. Process: {processModel.ProcessId}");
            _scriptStartedAt = DateTimeOffset.Now;
            var runningPreview = SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Process);
            _statistics = BuildStatistics(processModel, 56.3, TimeSpan.FromSeconds(45));
            if (_lastScript is null)
            {
                throw new InvalidOperationException("Automation script is not built.");
            }

            bool ShouldIncludeItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.TotalShots > 0;
            }

            var headScripts = _lastScript.HeadScripts.Where(ShouldIncludeItem).ToArray();
            var bufferedRun = IsBufferedRunEnabled(processModel.Parameters);
            using var queueCountLogger = CQueueCountLogger.Create(this, processModel.RecipeId, bufferedRun, headScripts);
            using var scannerStatusLogger = CScannerStatusLogger.Create(this, processModel.RecipeId, bufferedRun, processModel.Parameters, headScripts);
            var stopTasksOnExit = false;
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, runningPreview, EN_SCRIPT_STATUS.Running, EN_PROCESS_STEP.Process, null);
            try
            {
                if (bufferedRun)
                {
                    queueCountLogger.Start();
                    scannerStatusLogger.Start();
                    RunBufferedHeadScripts(processModel, headScripts, cancellationToken);
                }
                else
                {
                    foreach (var headScript in headScripts)
                    {
                        AddProcessLog("INFO", "A1_RUN", $"Automation1 #{headScript.AutomationNo} Task {headScript.TaskNo} run requested. H{headScript.HeadNo:00}, {headScript.FileName}");
                        var response = _automationManager.RunScript(headScript.FileName, headScript.TaskNo, headScript.AutomationNo, cancellationToken: cancellationToken).GetAwaiter().GetResult();
                        AddProcessLog("INFO", "A1_RUN", $"Run command accepted. H{headScript.HeadNo:00}, Automation1 #{headScript.AutomationNo}, Task {headScript.TaskNo}: {response}");
                    }

                    queueCountLogger.Start();
                    scannerStatusLogger.Start();
                    WaitAutomationHeadScriptsComplete(processModel, headScripts, cancellationToken);
                }
            }
            catch
            {
                stopTasksOnExit = !bufferedRun;
                throw;
            }
            finally
            {
                scannerStatusLogger.StopAndWriteFinal();
                queueCountLogger.StopAndWriteFinal();
                if (stopTasksOnExit)
                {
                    StopAutomationScriptTasks(headScripts, "scanner process ended before task completion");
                }
            }
        }

        private void RunBufferedHeadScripts(ST_PROCESS_MODEL processModel, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts, CancellationToken cancellationToken)
        {
            var queueSize = ReadBufferedRunQueueSize(processModel.Parameters);
            var linesPerCommand = ReadBufferedRunLinesPerCommand(processModel.Parameters);
            var timeoutMs = ReadBufferedRunTimeoutMs(processModel.Parameters);
            int SelectAutomationNo(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.AutomationNo;
            }

            var groups = headScripts.GroupBy(SelectAutomationNo).ToArray();
            string SelectItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return $"H{script.HeadNo:00}/T{script.TaskNo}";
            }

            foreach (var group in groups)
            {
                AddProcessLog("INFO", "A1_RUN", $"Automation1 #{group.Key} buffered run group requested. Heads={string.Join(", ", group.Select(SelectItem))}, Queue={queueSize}, LinesPerCommand={linesPerCommand}, TimeoutMs={timeoutMs}");
            }

            using var bufferedRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workers = groups.Select(group => new CBufferedRunWorker(this, group.Key, group.ToArray(), queueSize, linesPerCommand, timeoutMs, bufferedRunCancellation.Token)).ToArray();
            var results = new List<ST_BUFFERED_RUN_GROUP_RESULT>();
            Exception? firstException = null;
            foreach (var worker in workers)
            {
                worker.Start();
            }

            try
            {
                var completedWorkers = new HashSet<CBufferedRunWorker>();
                while (completedWorkers.Count < workers.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var worker in workers)
                    {
                        if (completedWorkers.Contains(worker) || !worker.IsComplete)
                        {
                            continue;
                        }

                        completedWorkers.Add(worker);
                        if (worker.Error is not null)
                        {
                            firstException = worker.Error;
                            AddProcessLog("ERROR", "A1_RUN", $"Buffered run group failed. {firstException.Message}");
                            break;
                        }

                        if (worker.Result is not null)
                        {
                            results.Add(worker.Result);
                        }
                    }

                    if (firstException is not null)
                    {
                        break;
                    }

                    if (completedWorkers.Count < workers.Length)
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
                var stopException = new OperationCanceledException("Automation1 buffered run canceled by station stop.", cancellationToken);
                bufferedRunCancellation.Cancel();
                WaitForBufferedRunWorkers(workers, 5000, "station stop");
                StopBufferedRunTasks(headScripts, stopException);
                WaitForBufferedRunWorkers(workers, 5000, "station stop fallback");
                throw;
            }

            if (firstException is not null)
            {
                bufferedRunCancellation.Cancel();
                StopBufferedRunTasks(headScripts, firstException);
                WaitForBufferedRunWorkers(workers, 3000, "failure cleanup");
                ExceptionDispatchInfo.Capture(firstException).Throw();
            }

            foreach (var result in results)
            {
                AddProcessLog("INFO", "A1_RUN", $"Buffered run completed. Automation1 #{result.AutomationNo}, Heads={result.HeadSummary}: {result.Response}");
            }
        }

        private void WaitForBufferedRunWorkers(IReadOnlyList<CBufferedRunWorker> workers, int timeoutMs, string reason)
        {
            var startedAt = Environment.TickCount64;
            foreach (var worker in workers)
            {
                if (worker.IsComplete)
                {
                    continue;
                }

                var elapsedMs = Environment.TickCount64 - startedAt;
                var remainingTimeoutMs = Math.Max(0, timeoutMs - (int)Math.Min(timeoutMs, elapsedMs));
                if (remainingTimeoutMs > 0 && worker.WaitForCompletion(remainingTimeoutMs))
                {
                    continue;
                }

                if (worker.IsComplete)
                {
                    continue;
                }

                AddProcessLog("WARN", "A1_RUN", $"Buffered run worker did not finish during {reason}. Automation1 #{worker.AutomationNo}");
            }
        }

        private void WaitAutomationHeadScriptsComplete(ST_PROCESS_MODEL processModel, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts, CancellationToken cancellationToken)
        {
            var targets = CreateAutomationTaskTargets(headScripts);
            if (targets.Count == 0)
            {
                return;
            }

            var timeoutMs = ReadScriptRunTimeoutMs(processModel.Parameters);
            var pollIntervalMs = ReadScriptRunPollIntervalMs(processModel.Parameters);
            var startedAt = Environment.TickCount64;
            global::Drilling.Common.Station.CStationProcess.ST_AUTOMATION_TASK_KEY BuildAutomation(global::Drilling.Common.Station.CStationProcess.ST_AUTOMATION_TASK_TARGET target)
            {
                return new ST_AUTOMATION_TASK_KEY(target.AutomationNo, target.TaskNo);
            }

            var pendingTargets = targets.ToDictionary(BuildAutomation);
            while (pendingTargets.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadSequenceState() == EN_STATION_SEQUENCE.Stop)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                foreach (var target in pendingTargets.Values.ToArray())
                {
                    var response = _automationManager.ReadTaskStatus(target.TaskNo, target.AutomationNo, cancellationToken).GetAwaiter().GetResult();
                    var status = ParseAutomationTaskStatus(response);
                    if (status.TaskNo != target.TaskNo)
                    {
                        throw new InvalidOperationException($"Automation1 task status response task mismatch. Expected={target.TaskNo}, Response={status.RawResponse}");
                    }

                    if (IsAutomationTaskComplete(status.State))
                    {
                        pendingTargets.Remove(new ST_AUTOMATION_TASK_KEY(target.AutomationNo, target.TaskNo));
                        AddProcessLog("INFO", "A1_RUN", $"Automation1 #{target.AutomationNo} Task {target.TaskNo} completed. Heads={target.HeadSummary}, State={status.State}");
                        continue;
                    }

                    if (IsAutomationTaskFailed(status.State))
                    {
                        throw new InvalidOperationException($"Automation1 task failed. Automation1=#{target.AutomationNo}, Task={target.TaskNo}, Heads={target.HeadSummary}, State={status.State}, Error={status.ErrorMessage}");
                    }
                }

                if (pendingTargets.Count == 0)
                {
                    return;
                }

                if (timeoutMs > 0)
                {
                    var elapsedMs = Environment.TickCount64 - startedAt;
                    if (elapsedMs >= timeoutMs)
                    {
                        throw new TimeoutException($"Automation1 task completion timeout. TimeoutMs={timeoutMs}, Pending={FormatAutomationTaskTargets(pendingTargets.Values)}");
                    }

                    cancellationToken.WaitHandle.WaitOne((int)Math.Min(pollIntervalMs, Math.Max(1, timeoutMs - elapsedMs)));
                }
                else
                {
                    cancellationToken.WaitHandle.WaitOne(pollIntervalMs);
                }
            }
        }

        private void StopAutomationScriptTasks(IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts, string reason)
        {
            var targets = CreateAutomationTaskTargets(headScripts);
            if (targets.Count == 0)
            {
                return;
            }

            AddProcessLog("WARN", "A1_RUN", $"Stopping Automation1 tasks. Reason={reason}, Targets={FormatAutomationTaskTargets(targets)}");
            foreach (var target in targets)
            {
                try
                {
                    var response = _automationManager.StopTask(target.TaskNo, target.AutomationNo, CancellationToken.None).GetAwaiter().GetResult();
                    AddProcessLog("WARN", "A1_RUN", $"Automation1 task stop requested. Automation1 #{target.AutomationNo}, Task {target.TaskNo}, Heads={target.HeadSummary}: {response}");
                }
                catch (Exception exception)
                {
                    AddProcessLog("ERROR", "A1_RUN", $"Automation1 task stop failed. Automation1 #{target.AutomationNo}, Task {target.TaskNo}, Heads={target.HeadSummary}: {exception.Message}");
                }
            }

            WaitAutomationScriptTasksStopped(targets);
        }

        private void WaitAutomationScriptTasksStopped(IReadOnlyList<ST_AUTOMATION_TASK_TARGET> targets)
        {
            foreach (var target in targets)
            {
                try
                {
                    WaitAutomationScriptTaskStopped(target);
                    AddProcessLog("INFO", "A1_RUN", $"Automation1 task stopped. Automation1 #{target.AutomationNo}, Task {target.TaskNo}, Heads={target.HeadSummary}");
                }
                catch (Exception exception)
                {
                    AddProcessLog("ERROR", "A1_RUN", $"Automation1 task stop confirmation failed. Automation1 #{target.AutomationNo}, Task {target.TaskNo}, Heads={target.HeadSummary}: {exception.Message}");
                }
            }
        }

        private void WaitAutomationScriptTaskStopped(ST_AUTOMATION_TASK_TARGET target)
        {
            var startedAt = Environment.TickCount64;
            while (true)
            {
                using var statusTimeout = new CancellationTokenSource(EncoderScaleReleaseTimeoutMs);
                var response = _automationManager.ReadTaskStatus(target.TaskNo, target.AutomationNo, statusTimeout.Token).GetAwaiter().GetResult();
                var status = ParseAutomationTaskStatus(response);
                if (status.TaskNo != target.TaskNo)
                {
                    throw new InvalidOperationException($"Automation1 task status response task mismatch. Expected={target.TaskNo}, Response={status.RawResponse}");
                }

                if (IsAutomationTaskComplete(status.State) || IsAutomationTaskFailed(status.State))
                {
                    return;
                }

                var elapsedMs = Environment.TickCount64 - startedAt;
                if (elapsedMs >= EncoderScaleReleaseTimeoutMs)
                {
                    throw new TimeoutException($"Automation1 task did not stop after StopTask. State={status.State}, Response={status.RawResponse}");
                }

                Thread.Sleep((int)Math.Min(ScriptRunDefaultPollIntervalMs, Math.Max(1, EncoderScaleReleaseTimeoutMs - elapsedMs)));
            }
        }

        private static IReadOnlyList<ST_AUTOMATION_TASK_TARGET> CreateAutomationTaskTargets(IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
        {
            bool ShouldIncludeItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.TotalShots > 0;
            }

            return headScripts.Where(ShouldIncludeItem).GroupBy(script => new { script.AutomationNo, script.TaskNo }).Select(group => new ST_AUTOMATION_TASK_TARGET(group.Key.AutomationNo, group.Key.TaskNo, string.Join(", ", group.Select(script => $"H{script.HeadNo:00}")))).ToArray();
        }

        private static ST_AUTOMATION_TASK_STATUS ParseAutomationTaskStatus(string response)
        {
            var automationError = FormatAutomationErrorResponse(response);
            if (!string.IsNullOrWhiteSpace(automationError))
            {
                throw new InvalidOperationException($"Automation1 task status response failed: {automationError}");
            }

            var fields = response.Split(':');
            if (fields.Length < 4 || !fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !fields[1].Equals("TASK", StringComparison.OrdinalIgnoreCase) || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskNo))
            {
                throw new InvalidOperationException($"Automation1 task status response is invalid: {response}");
            }

            return new ST_AUTOMATION_TASK_STATUS(taskNo, fields[3].Trim(), fields.Length > 4 ? fields[4].Trim() : "", fields.Length > 5 ? string.Join(":", fields.Skip(5)).Trim() : "", response);
        }

        private ST_AUTOMATION_TASK_LINE_STATUS ReadAutomationTaskLineStatus(int automationNo, int taskNo, CancellationToken cancellationToken)
        {
            var response = _automationManager.ReadTaskLineStatus(taskNo, automationNo, cancellationToken).GetAwaiter().GetResult();
            return ParseAutomationTaskLineStatus(response);
        }

        private static ST_AUTOMATION_TASK_LINE_STATUS ParseAutomationTaskLineStatus(string response)
        {
            var automationError = FormatAutomationErrorResponse(response);
            if (!string.IsNullOrWhiteSpace(automationError))
            {
                throw new InvalidOperationException($"Automation1 task line status response failed: {automationError}");
            }

            var fields = response.Split(':');
            if (fields.Length < 4 || !fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !fields[1].Equals("TASK_LINE", StringComparison.OrdinalIgnoreCase) || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskNo) || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentLine))
            {
                throw new InvalidOperationException($"Automation1 task line status response is invalid: {response}");
            }

            return new ST_AUTOMATION_TASK_LINE_STATUS(taskNo, Math.Max(0, currentLine), response);
        }

        private static string FormatAutomationErrorResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return "";
            }

            var fields = response.Trim().Split(':', 4, StringSplitOptions.TrimEntries);
            if (fields.Length >= 4 && fields[0].Equals("ERROR", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("AUTOMATION1", StringComparison.OrdinalIgnoreCase))
            {
                return $"{fields[2]}: {fields[3]}";
            }

            return "";
        }

        private static bool IsAutomationTaskComplete(string state)
        {
            return state.Equals("Idle", StringComparison.OrdinalIgnoreCase) || state.Equals("ProgramComplete", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAutomationTaskFailed(string state)
        {
            return state.Equals("Error", StringComparison.OrdinalIgnoreCase) || state.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) || state.Equals("Inactive", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatAutomationTaskTargets(IEnumerable<ST_AUTOMATION_TASK_TARGET> targets)
        {
            string SelectItem(global::Drilling.Common.Station.CStationProcess.ST_AUTOMATION_TASK_TARGET target)
            {
                return $"Automation1 #{target.AutomationNo}/Task {target.TaskNo}/Heads={target.HeadSummary}";
            }

            return string.Join(", ", targets.Select(SelectItem));
        }

        private ST_BUFFERED_RUN_GROUP_RESULT RunBufferedHeadScriptGroup(int automationNo, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts, int queueSize, int linesPerCommand, int timeoutMs, CancellationToken cancellationToken)
        {
            global::Drilling.Common.Automation.ST_BUFFERED_SCRIPT_RUN_ITEM BuildBuffered(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return new ST_BUFFERED_SCRIPT_RUN_ITEM(script.FilePath, script.FileName, script.TaskNo);
            }

            var response = _automationManager.RunBufferedScripts(headScripts.Select(BuildBuffered).ToArray(), automationNo, queueSize, linesPerCommand, timeoutMs, cancellationToken).GetAwaiter().GetResult();
            string SelectItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return $"H{script.HeadNo:00}/T{script.TaskNo}";
            }

            return new ST_BUFFERED_RUN_GROUP_RESULT(automationNo, string.Join(", ", headScripts.Select(SelectItem)), response);
        }

        private void StopBufferedRunTasks(IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts, Exception cause)
        {
            bool ShouldIncludeItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.TotalShots > 0;
            }

            var targets = headScripts.Where(ShouldIncludeItem).GroupBy(script => new { script.AutomationNo, script.TaskNo }).Select(group => new { group.Key.AutomationNo, group.Key.TaskNo, HeadSummary = string.Join(", ", group.Select(script => $"H{script.HeadNo:00}")) }).ToArray();
            AddProcessLog("WARN", "A1_RUN", $"Stopping buffered run tasks after failure. Reason={cause.Message}, Targets={targets.Length}");
            var stopResults = targets.Select(target =>
            {
                try
                {
                    var response = _automationManager.StopTask(target.TaskNo, target.AutomationNo, CancellationToken.None).GetAwaiter().GetResult();
                    return new ST_BUFFERED_RUN_STOP_RESULT(target.AutomationNo, target.TaskNo, target.HeadSummary, true, response, "");
                }
                catch (Exception ex)
                {
                    return new ST_BUFFERED_RUN_STOP_RESULT(target.AutomationNo, target.TaskNo, target.HeadSummary, false, "", ex.Message);
                }
            }).ToArray();
            foreach (var stopResult in stopResults)
            {
                if (stopResult.IsSuccess)
                {
                    AddProcessLog("WARN", "A1_RUN", $"Buffered run stop requested. Automation1 #{stopResult.AutomationNo}, Task {stopResult.TaskNo}, Heads={stopResult.HeadSummary}: {stopResult.Response}");
                }
                else
                {
                    AddProcessLog("ERROR", "A1_RUN", $"Buffered run stop failed. Automation1 #{stopResult.AutomationNo}, Task {stopResult.TaskNo}, Heads={stopResult.HeadSummary}: {stopResult.ErrorMessage}");
                }
            }
        }

        private void RunReviewStep(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddProcessLog("INFO", "REVIEW", "Review step reserved. Stage PC Y move, Vision X move, and Vision measure will be connected later.");
        }

        private void RunPowerCheckStep(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken)
        {
            var powerMeterStatus = _interfaceManager.GetPowerMeterStatus(cancellationToken);
            AddProcessLog("INFO", "POWER", $"Power check step reserved. Current power={powerMeterStatus.MeasuredPower.ToString("F4", CultureInfo.InvariantCulture)} {powerMeterStatus.Unit}");
        }

        private void CompleteProcess(CancellationToken cancellationToken)
        {
            var scriptStatus = GetCurrentScriptStatusForComplete();
            if (scriptStatus == EN_SCRIPT_STATUS.Completed)
            {
                _scriptCompletedAt = DateTimeOffset.Now;
                AddProcessLog("INFO", "A1_TASK", "Automation1 task completed.");
            }
            else
            {
                AddProcessLog("INFO", "A1_TASK", "Automation1 script was not executed. PROCESS step was skipped.");
            }

            var processExecuted = scriptStatus == EN_SCRIPT_STATUS.Completed;
            if (processExecuted)
                CountCompletedProtectWindowGlass().GetAwaiter().GetResult();
            global::Drilling.Common.Station.ST_HEAD_PREVIEW_DATA SelectItem(global::Drilling.Common.Station.ST_HEAD_PREVIEW_DATA head)
            {
                return head with
                {
                    Status = EN_HEAD_PROCESS_STATUS.Disabled
                };
            }

            var completedPreview = processExecuted ? SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Completed) : _snapshot.HeadPreviews.Select(SelectItem).ToArray();
            _statistics = BuildStatistics(_snapshot.ProcessModel, processExecuted ? 100.0 : _statistics.ProgressPercent, processExecuted ? TimeSpan.FromSeconds(80) : _statistics.ElapsedTime);
            var result = processExecuted ? new ST_PROCESS_RESULT(true, "Station PROCESS completed.", DateTimeOffset.Now, "OK") : new ST_PROCESS_RESULT(true, "Station PROCESS skipped by setting option.", DateTimeOffset.Now, "SKIPPED");
            CompleteProduct(result, processExecuted, cancellationToken);
            ReportProcessResult(result, processExecuted ? "COMPLETE" : "SKIP", cancellationToken, EN_STATION_STATE.Complete);
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, completedPreview, scriptStatus, EN_PROCESS_STEP.Completed, result);
            SetStationState(EN_STATION_STATE.Complete, EN_PROCESS_STEP.Completed, scriptStatus, "Process station completed.");
        }

        public ST_STATION_PROCESS_STATUS Stop(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sequenceLock)
            {
                if (_manualMofRunning || _manualMofRequiresStop)
                {
                    _ = StopManualMofAsync().ContinueWith(task =>
                    {
                        if (task.Exception is { } error)
                            AddProcessLog("ERROR", "MANUAL_MOF_STOP", error.GetBaseException().Message);
                    }, TaskScheduler.Default);
                    return _snapshot;
                }
            }

            lock (_sequenceLock)
            {
                _sequenceCancellation?.Cancel();
                _sequenceState = EN_STATION_SEQUENCE.Stop;
                _stationStatus = (_stationStatus with
                {
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(EN_STATION_SEQUENCE.Stop);
            }

            base.Start(10, "StationProcess");
            return _snapshot;
        }

        private ST_STATION_PROCESS_STATUS ProcessStop(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadStationStatus().State == EN_STATION_STATE.Stopped)
            {
                return _snapshot;
            }

            AddProcessLog("WARN", "OPERATOR", "Station PROCESS stopped by operator.");
            if (_lastScript is not null && _snapshot.ScriptStatus == EN_SCRIPT_STATUS.Running)
            {
                StopAutomationScriptTasks(_lastScript.HeadScripts, "operator stop requested");
            }

            _statistics = BuildStatistics(_snapshot.ProcessModel, _statistics.ProgressPercent, _statistics.ElapsedTime);
            var result = new ST_PROCESS_RESULT(false, "Station PROCESS stopped by operator.", DateTimeOffset.Now);
            TryStopProduct(result.Message, cancellationToken);
            TryReportProcessResult(result, "STOP", cancellationToken, EN_STATION_STATE.Stopped);
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Stopped), EN_SCRIPT_STATUS.NotCreated, EN_PROCESS_STEP.Stopped, result);
            MarkRunningAutoSteps(AutoStepStop);
            RefreshSnapshot();
            SetStationState(EN_STATION_STATE.Stopped, EN_PROCESS_STEP.Stopped, EN_SCRIPT_STATUS.NotCreated, "Station stopped by operator.");
            return _snapshot;
        }

        public ST_STATION_PROCESS_STATUS Reset(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sequenceLock)
            {
                if (_manualMofRunning || _manualMofRequiresStop || _sequenceState != EN_STATION_SEQUENCE.Ready || _stationStatus.State == EN_STATION_STATE.Process)
                {
                    AddProcessLog("WARN", "RESET", "Reset ignored while station is running.");
                    RefreshSnapshot();
                    return _snapshot;
                }

                _sequenceState = EN_STATION_SEQUENCE.Reset;
                _stationStatus = (_stationStatus with
                {
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(EN_STATION_SEQUENCE.Reset);
            }

            base.Start(10, "StationProcess");
            return _snapshot;
        }

        private ST_STATION_PROCESS_STATUS ProcessReset(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stationStatus = ReadStationStatus();
            if (stationStatus.State == EN_STATION_STATE.Alarm)
            {
                var interLock = GetInterLockSummary(cancellationToken);
                if (!interLock.CanAutoRun)
                {
                    var message = FormatInterLockBlockedMessage(interLock);
                    AddProcessLog("WARN", "RESET", $"Reset blocked. {message}");
                    SetSequenceState(EN_STATION_SEQUENCE.Error);
                    _snapshot = CreateSnapshot(_snapshot.ProcessModel, SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Error), EN_SCRIPT_STATUS.Error, EN_PROCESS_STEP.Error, new ST_PROCESS_RESULT(false, message, DateTimeOffset.Now));
                    SetStationState(EN_STATION_STATE.Alarm, EN_PROCESS_STEP.Error, EN_SCRIPT_STATUS.Error, $"Reset blocked. {message}");
                    return _snapshot;
                }

                AddProcessLog("INFO", "RESET", "Alarm reset accepted. InterLock is clear.");
            }

            ClearProcessLogs();
            _lastInterLockItems = [];
            _scriptCreatedAt = null;
            _scriptStartedAt = null;
            _scriptCompletedAt = null;
            _lastScript = null;
            _processModel = null;
            ResetAutoStepStates();
            _statistics = EmptyStatistics();
            _snapshot = CreateSnapshot(null, [], EN_SCRIPT_STATUS.NotCreated, EN_PROCESS_STEP.Idle, null);
            SetStationState(EN_STATION_STATE.Idle, EN_PROCESS_STEP.Idle, EN_SCRIPT_STATUS.NotCreated, "Station reset to idle.");
            SetSequenceState(EN_STATION_SEQUENCE.Ready);
            return _snapshot;
        }

        private ST_STATION_PROCESS_STATUS CreateSnapshot(
            ST_PROCESS_MODEL? processModel, 
            IReadOnlyList<ST_HEAD_PREVIEW_DATA> preview, 
            EN_SCRIPT_STATUS scriptStatus, 
            EN_PROCESS_STEP processStep, 
            ST_PROCESS_RESULT? result
        )
        {
            return new ST_STATION_PROCESS_STATUS(
                processModel, 
                preview, 
                scriptStatus, 
                processStep, 
                result, 
                BuildProcessSequence(scriptStatus, processStep), 
                BuildCurrentStepDetails(processModel, scriptStatus, processStep), 
                BuildProcessSummary(processModel, _statistics), 
                GetProcessLogsSnapshot(), 
                BuildScriptStatusItems(processModel, scriptStatus, result), 
                BuildScriptLifecycleItems(scriptStatus, processStep), 
                _lastInterLockItems, 
                _statistics
            );
        }

        private ST_INTERLOCK_SUMMARY GetInterLockSummary(CancellationToken cancellationToken, ST_PROCESS_MODEL? processModel = null) => EvaluateInterlocks(processModel?.Parameters, cancellationToken);
        private ST_INTERLOCK_SUMMARY EvaluateInterlocks(
            IReadOnlyDictionary<string, string>? parameters, 
            CancellationToken cancellationToken, 
            bool cachedStatusOnly = false, 
            bool publish = true, 
            bool requireAllHeadsUsed = false
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _interLockManager.Evaluate(GetDeviceStatus(cancellationToken, cachedStatusOnly));
            if (parameters is not null)
            {
                var optics = CInterLockManager.EvaluateOptics(parameters, _interfaceManager.GetInterfaceList(), _interfaceManager.OpticSnapshots.GetLaser, _interfaceManager.OpticSnapshots.GetBet, _interfaceManager.AttenuatorSnapshots.GetSnapshot).Concat(CInterLockManager.EvaluateDoeZ(parameters, axis => _automationManager is not null && (_automationManager.IsSimul(axis.AutomationNo) || _automationManager.IsConnect(axis.AutomationNo)) ? _automationManager.StatusSnapshots.GetAxis(axis.AxisReference, axis.AutomationNo, axis.AioNo, axis.ReadExtendedStatus) : new(null, "OFFLINE", "Automation controller is offline.", default))).ToArray();
                result = result with
                {
                    Items = result.Items.Concat(optics).ToArray(),
                    CanAutoRun = result.CanAutoRun && optics.All(item => !item.BlocksAutoRun),
                    HasError = result.HasError || optics.Any(item => item.Level == EN_INTERLOCK_LEVEL.Error)
                };
            }

            if (requireAllHeadsUsed)
            {
                var headUse = CInterLockManager.EvaluateHeadUse(parameters);
                result = result with
                {
                    Items = result.Items.Concat(headUse).ToArray(),
                    CanAutoRun = result.CanAutoRun && headUse.All(item => !item.BlocksAutoRun),
                    HasError = result.HasError || headUse.Any(item => item.Level == EN_INTERLOCK_LEVEL.Error)
                };
            }

            if (publish)
                _lastInterLockItems = result.Items;
            return result;
        }

        private ST_DEVICE_STATUS GetDeviceStatus(CancellationToken cancellationToken, bool cachedStatusOnly = false)
        {
            IReadOnlyList<ST_IO_STATUS> io = [];
            var motors = cachedStatusOnly ? _motionManager.GetCachedAxisStatus() : _motionManager.GetAxisStatus(cancellationToken);
            var laserNo = _interfaceManager.GetInterfaceList(EN_EQP_MODULE.TalonLaser).FirstOrDefault()?.Number ?? 0;
            var laserStatus = _interfaceManager.GetCachedLaserStatus(laserNo);
            var attenuatorStatus = _interfaceManager.GetAttenuatorStatus(cancellationToken);
            var betNo = _interfaceManager.GetInterfaceList(EN_EQP_MODULE.Bet).FirstOrDefault()?.Number ?? 0;
            var betStatus = _interfaceManager.GetCachedBetStatus(betNo);
            var powerMeterStatus = _interfaceManager.GetPowerMeterStatus(cancellationToken);
            return new ST_DEVICE_STATUS(io, motors, laserStatus, attenuatorStatus, betStatus, powerMeterStatus);
        }

        private void CompleteProduct(ST_PROCESS_RESULT result, bool processExecuted, CancellationToken cancellationToken)
        {
            var productId = GetCurrentProcessProductId();
            if (_productManager is null || string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            _productManager.CompleteProduct(productId, result.IsSuccess, processExecuted, result.Message, cancellationToken).GetAwaiter().GetResult();
            AddProcessLog("INFO", "PRODUCT", $"Product completed ({productId})");
        }

        private void StopProduct(string message, CancellationToken cancellationToken)
        {
            var productId = GetCurrentProcessProductId();
            if (_productManager is null || string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            _productManager.StopProduct(productId, message, cancellationToken).GetAwaiter().GetResult();
            AddProcessLog("WARN", "PRODUCT", $"Product stopped ({productId})");
        }

        private void TryStopProduct(string message, CancellationToken cancellationToken)
        {
            try
            {
                StopProduct(message, cancellationToken);
            }
            catch (Exception exception)when (IsTerminalCleanupException(exception))
            {
                AddSequenceLog("WARN", "PRODUCT", $"Product stop failed during station stop cleanup. {exception.Message}");
            }
        }

        private void SetProductError(string message, CancellationToken cancellationToken)
        {
            var productId = GetCurrentProcessProductId();
            if (_productManager is null || string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            _productManager.SetError(productId, message, cancellationToken).GetAwaiter().GetResult();
            AddProcessLog("ERROR", "PRODUCT", $"Product error ({productId})");
        }

        private void TrySetProductError(string message, CancellationToken cancellationToken)
        {
            try
            {
                SetProductError(message, cancellationToken);
            }
            catch (Exception exception)when (IsTerminalCleanupException(exception))
            {
                AddSequenceLog("WARN", "PRODUCT", $"Product error update failed during station alarm cleanup. {exception.Message}");
            }
        }

        private void ReportProcessResult(ST_PROCESS_RESULT result, string action, CancellationToken cancellationToken, EN_STATION_STATE? resultStationState = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processId = _snapshot.ProcessModel?.ProcessId ?? "-";
            var recipeId = _snapshot.ProcessModel?.RecipeId ?? "-";
            var productId = GetCurrentProcessProductId() ?? processId;
            var reportState = GetProcessResultCode(result);
            var detail = FormatProcessResultDetail(result, processId, recipeId, productId);
            var hostNickName = _interfaceManager.GetInterfaceData(EN_EQP_MODULE.WonikCtrl, 0)?.NickName ?? "WONIK_CTRL";
            var stationStatus = ReadStationStatus();
            var stationState = resultStationState ?? stationStatus.State;
            AddProcessLog("INFO", "RESULT", $"{action} result reported. {detail}");
            _logManager?.WriteStationState(stationStatus.StationName, stationState.ToString().ToUpperInvariant(), $"RESULT_{action}", detail);
            _logManager?.WriteInterfaceCommand(EN_EQP_MODULE.WonikCtrl, hostNickName, $"PROCESS_RESULT_{action}", reportState, detail);
        }

        private void TryReportProcessResult(ST_PROCESS_RESULT result, string action, CancellationToken cancellationToken, EN_STATION_STATE resultStationState)
        {
            try
            {
                ReportProcessResult(result, action, cancellationToken, resultStationState);
            }
            catch (Exception exception)when (IsTerminalCleanupException(exception))
            {
                AddSequenceLog("WARN", "RESULT", $"{action} result report failed during station terminal cleanup. {exception.Message}");
            }
        }

        private static bool IsTerminalCleanupException(Exception exception)
        {
            return exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException or OperationCanceledException or UnauthorizedAccessException;
        }

        private string? GetCurrentProcessProductId()
        {
            if (_productManager?.Current is null || _snapshot.ProcessModel is null)
            {
                return null;
            }

            return _productManager.Current.ProcessId.Equals(_snapshot.ProcessModel.ProcessId, StringComparison.OrdinalIgnoreCase) ? _productManager.Current.GlassId : null;
        }

        private static IReadOnlyList<ST_HEAD_PREVIEW_DATA> BuildPreview(ST_PROCESS_MODEL processModel, EN_HEAD_PROCESS_STATUS status)
        {
            global::Drilling.Common.Station.ST_HEAD_PREVIEW_DATA BuildHead(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
            {
                return new ST_HEAD_PREVIEW_DATA(head.HeadNo, status, head.Shots.Count);
            }

            return processModel.HeadPlans.Select(BuildHead).ToArray();
        }

        private IReadOnlyDictionary<string, string> BuildRuntimeParameters(string recipeId, IReadOnlyDictionary<string, string> sourceParameters, CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, string>(sourceParameters, StringComparer.OrdinalIgnoreCase);
            var processSettings = new List<ST_SYSTEM_PARAMETER>();
            processSettings.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken));
            processSettings.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Motor, cancellationToken));
            foreach (var setting in processSettings)
            {
                if (string.IsNullOrWhiteSpace(setting.Key))
                {
                    continue;
                }

                parameters[setting.Key] = setting.Value;
            }

            MergeLatestReviewOffsets(recipeId, parameters, cancellationToken);
            return parameters;
        }

        private void MergeLatestReviewOffsets(string recipeId, IDictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            if (_reviewOffsetFile is null)
            {
                CReviewOffsetData.RemoveFromParameters(parameters);
                return;
            }

            var reviewOffset = _reviewOffsetFile.LoadLatest(recipeId, cancellationToken).GetAwaiter().GetResult();
            CReviewOffsetData.ApplyToParameters(parameters, reviewOffset);
            AddProcessLog("INFO", "REVIEW_OFFSET", $"Loaded latest Review Offset file: {reviewOffset.FileName} ({reviewOffset.Rows.Count} rows)");
        }

        private static string NormalizeReviewOffsetShotId(string value)
        {
            return (value ?? "").Trim().Replace("-", "_", StringComparison.Ordinal).Replace(".", "_", StringComparison.Ordinal).ToUpperInvariant();
        }

        private static ST_PROCESS_MODEL BuildProcessModel(string processId, string recipeId, string glassId, string lotId, IReadOnlyDictionary<string, string> sourceParameters)
        {
            var parameters = new Dictionary<string, string>(sourceParameters, StringComparer.OrdinalIgnoreCase);
            var coordinatePlan = CShotCoordinatePlanBuilder.Build(parameters);
            var shotMaskingPlan = CRecipeShotMaskingPlan.Build(parameters);
            var processShots = coordinatePlan.Shots.Where(shot => !shotMaskingPlan.EvaluateShot(shot).IsChessSkipped).Select((shot, index) => shot with { SequenceNo = index + 1 }).ToArray();
            var defaultMarkSpeed = ReadDoubleAny(parameters, 900.0, "SCANNER_MARK_SPEED");
            var defaultJumpSpeed = ReadDoubleAny(parameters, 1500.0, "SCANNER_JUMP_SPEED");
            int SelectHeadNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.HeadNo;
            }

            int SelectKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> group)
            {
                return group.Key;
            }

            global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT[] SelectValue(global::System.Linq.IGrouping<int, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> group)
            {
                return group.ToArray();
            }

            var shotsByHead = processShots.GroupBy(SelectHeadNo).ToDictionary(SelectKey, SelectValue, EqualityComparer<int>.Default);
            var activeHeadNos = new List<int>(coordinatePlan.HeadCount);
            for (var headNo = 1; headNo <= coordinatePlan.HeadCount; headNo++)
            {
                if (ReadHeadUse(parameters, headNo))
                {
                    activeHeadNos.Add(headNo);
                }
            }

            if (activeHeadNos.Count == 0)
            {
                throw new InvalidOperationException("At least one head must be enabled. Check Hxx_USE option settings.");
            }

            global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN BuildHead(int headNo)
            {
                var headShots = shotsByHead.TryGetValue(headNo, out var values) ? values : [];
                var scannerMarkSpeed = ReadDoubleAny(parameters, defaultMarkSpeed, CreateHeadKeys(headNo, "SCANNER_MARK_SPEED"));
                var scannerJumpSpeed = ReadDoubleAny(parameters, defaultJumpSpeed, CreateHeadKeys(headNo, "SCANNER_JUMP_SPEED"));
                var scannerXRampRate = ReadDoubleAny(parameters, 30000.0, CreateHeadKeys(headNo, "SCANNER_X_RAMP_RATE", "SCANNER_RAMP_RATE"));
                var scannerYRampRate = ReadDoubleAny(parameters, scannerXRampRate, CreateHeadKeys(headNo, "SCANNER_Y_RAMP_RATE", "SCANNER_RAMP_RATE"));
                var scannerZRampRate = ReadDoubleAny(parameters, scannerYRampRate, CreateHeadKeys(headNo, "SCANNER_Z_RAMP_RATE"));
                var scannerZSpeed = ReadDoubleAny(parameters, scannerJumpSpeed, CreateHeadKeys(headNo, "SCANNER_Z_SPEED"));
                var scannerAccLimit = ReadDoubleAny(parameters, 100000.0, "SCAN_ACC_LIMIT", "SCANNER_ACC_LIMIT");
                return new ST_HEAD_PROCESS_PLAN(
                    headNo, 
                    ReadDoubleAny(parameters, 1.0, CreateHeadKeys(headNo, "LASER_POWER")), 
                    ReadDoubleAny(parameters, 20.0, CreateHeadKeys(headNo, "LASER_FREQUENCY")), 
                    ReadIntAny(parameters, 10, CreateHeadKeys(headNo, "SHOT_COUNT")), 
                    scannerMarkSpeed, 
                    scannerJumpSpeed, 
                    scannerXRampRate, 
                    scannerYRampRate, 
                    scannerZRampRate, 
                    scannerZSpeed, 
                    scannerAccLimit, 
                    ReadDoubleAny(parameters, 0.0, CreateHeadKeys(headNo, "DOE_Z_POSITION")), 
                    headShots
                )
                {
                    AutomationNo = ReadHeadAutomationNo(parameters, headNo),
                    TaskNo = ReadHeadAutomationTaskNo(parameters, headNo),
                    ScriptFileName = $"PROCESS_H{headNo:00}.ascript"};
            }

            var headPlans = activeHeadNos.Select(BuildHead).ToArray();
            return new ST_PROCESS_MODEL(processId, recipeId, glassId, lotId, headPlans, parameters, DateTimeOffset.Now);
        }

        private static IReadOnlyList<ST_HEAD_PREVIEW_DATA> SetHeadStatus(IReadOnlyList<ST_HEAD_PREVIEW_DATA> preview, EN_PROCESS_STEP processStep)
        {
            global::Drilling.Common.Station.ST_HEAD_PREVIEW_DATA SelectItem(global::Drilling.Common.Station.ST_HEAD_PREVIEW_DATA head)
            {
                var status = processStep switch
                {
                    EN_PROCESS_STEP.Process => head.ShotCount > 0 ? EN_HEAD_PROCESS_STATUS.Running : EN_HEAD_PROCESS_STATUS.Disabled,
                    EN_PROCESS_STEP.Completed => head.ShotCount > 0 ? EN_HEAD_PROCESS_STATUS.Completed : EN_HEAD_PROCESS_STATUS.Disabled,
                    EN_PROCESS_STEP.Stopped => EN_HEAD_PROCESS_STATUS.Ready,
                    EN_PROCESS_STEP.Error => EN_HEAD_PROCESS_STATUS.Error,
                    _ => head.Status
                };
                return head with
                {
                    Status = status
                };
            }

            return preview.Select(SelectItem).ToArray();
        }

        private IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> BuildProcessSequence(EN_SCRIPT_STATUS scriptStatus, EN_PROCESS_STEP processStep)
        {
            global::Drilling.Common.Station.ST_PROCESS_DISPLAY_ITEM BuildProcess(global::Drilling.Common.Station.ST_STATION_PROCESS_FLOW_ITEM step)
            {
                return new ST_PROCESS_DISPLAY_ITEM(step.Order.ToString(CultureInfo.InvariantCulture), step.StepName, ReadAutoStepState(step.StepKey, scriptStatus, processStep));
            }

            return ProcessFlowItems.Select(BuildProcess).ToArray();
        }

        private IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> BuildCurrentStepDetails(ST_PROCESS_MODEL? processModel, EN_SCRIPT_STATUS scriptStatus, EN_PROCESS_STEP processStep)
        {
            global::Drilling.Common.Station.ST_PROCESS_DISPLAY_ITEM BuildProcess(global::Drilling.Common.Station.CStationProcess.ST_AUTO_STEP_INFO step)
            {
                return new ST_PROCESS_DISPLAY_ITEM(step.DisplayName, AutoStepWait);
            }

            if (processModel is null)
            {
                return AutoStepInfos.Select(BuildProcess).ToArray();
            }

            global::Drilling.Common.Station.ST_PROCESS_DISPLAY_ITEM BuildProcess2(global::Drilling.Common.Station.CStationProcess.ST_AUTO_STEP_INFO step)
            {
                return new ST_PROCESS_DISPLAY_ITEM(step.DisplayName, ReadAutoStepState(step.Key, scriptStatus, processStep));
            }

            return AutoStepInfos.Select(BuildProcess2).ToArray();
        }

        private static IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> BuildProcessSummary(ST_PROCESS_MODEL? processModel, ST_PROCESS_STATISTICS statistics)
        {
            var glassId = processModel?.GlassId ?? "-";
            return[new("Product Glass ID", string.IsNullOrWhiteSpace(glassId) ? "-" : glassId), new("Tact Time", FormatDuration(statistics.EstimatedTime))];
        }

        private IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> BuildScriptStatusItems(ST_PROCESS_MODEL? processModel, EN_SCRIPT_STATUS scriptStatus, ST_PROCESS_RESULT? result)
        {
            var hasScript = processModel is not null && _lastScript is not null;
            string SelectFileName(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.FileName;
            }

            var scriptFiles = hasScript && _lastScript!.HeadScripts.Count > 0 ? string.Join(", ", _lastScript.HeadScripts.Select(SelectFileName)) : hasScript ? _lastScript!.FileName : "-";
            string SelectItem(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
            {
                return script.TaskNo.ToString(CultureInfo.InvariantCulture);
            }

            var taskNumbers = hasScript && _lastScript!.HeadScripts.Count > 0 ? string.Join(", ", _lastScript.HeadScripts.Select(SelectItem)) : hasScript ? "1" : "-";
            return[
                new("Script Build", FormatScriptStatus(scriptStatus)), 
                new("Script File", scriptFiles, hasScript ? _lastScript!.FilePath : ""), 
                new("Task No", taskNumbers), 
                new("Execute State", FormatExecuteState(scriptStatus, result, hasScript)), 
                new("Created Time", FormatDateTime(_scriptCreatedAt)), 
                new("Started Time", FormatDateTime(_scriptStartedAt)), 
                new("Completed Time", FormatDateTime(_scriptCompletedAt)), 
                new("Result", FormatScriptResult(scriptStatus, result, hasScript)), 
                new("Error Code", result is { IsSuccess: false } ? "1" : "0")
            ];
        }

        private static IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> BuildScriptLifecycleItems(EN_SCRIPT_STATUS scriptStatus, EN_PROCESS_STEP processStep)
        {
            return[
                new("Not Created", LifecycleState(scriptStatus, processStep, 0)), 
                new("Created", LifecycleState(scriptStatus, processStep, 1)), 
                new("Started", LifecycleState(scriptStatus, processStep, 2)), 
                new("Running", LifecycleState(scriptStatus, processStep, 3)), 
                new("Completed", LifecycleState(scriptStatus, processStep, 4))
            ];
        }

        private static ST_PROCESS_STATISTICS BuildStatistics(ST_PROCESS_MODEL? processModel, double progressPercent, TimeSpan elapsedTime)
        {
            int SelectValue(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
            {
                return head.Shots.Count;
            }

            var totalShots = processModel?.HeadPlans.Sum(SelectValue) ?? 0;
            return new ST_PROCESS_STATISTICS(totalShots, (int)Math.Round(totalShots * 0.60), (int)Math.Round(totalShots * 0.40), totalShots == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(80), elapsedTime, Math.Clamp(progressPercent, 0.0, 100.0));
        }

        private static ST_PROCESS_STATISTICS EmptyStatistics()
        {
            return new ST_PROCESS_STATISTICS(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, 0.0);
        }

        private void RefreshSnapshot()
        {
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, _snapshot.HeadPreviews, _snapshot.ScriptStatus, _snapshot.ProcessStep, _snapshot.Result);
        }

        private void ResetAutoStepStates() => ResetAutoStepStatesCore(false);
        private void ResetAutoStepStatesCore(bool preserveSequenceActions)
        {
            if (!preserveSequenceActions)
                _sequenceActions.Reset();
            _autoStepStates.Clear();
            foreach (var step in AutoStepInfos)
            {
                _autoStepStates[step.Key] = AutoStepWait;
            }
        }

        private void SetAutoStepState(string stepKey, string state)
        {
            _autoStepStates[stepKey] = state;
        }

        private void MarkRunningAutoSteps(string state)
        {
            foreach (var step in AutoStepInfos)
            {
                if (_autoStepStates.TryGetValue(step.Key, out var currentState) && currentState == AutoStepRunning)
                {
                    _autoStepStates[step.Key] = state;
                }
            }
        }

        private string ReadAutoStepState(string stepKey, EN_SCRIPT_STATUS scriptStatus, EN_PROCESS_STEP processStep)
        {
            if (_autoStepStates.TryGetValue(stepKey, out var state))
            {
                return state;
            }

            return (stepKey, scriptStatus, processStep) switch
            {
                (AutoStepProcess, EN_SCRIPT_STATUS.Created or EN_SCRIPT_STATUS.Running or EN_SCRIPT_STATUS.Completed, EN_PROCESS_STEP.Process) => AutoStepRunning,
                (AutoStepInspection, EN_SCRIPT_STATUS.Running, EN_PROCESS_STEP.Inspection) => AutoStepRunning,
                (AutoStepComplete, EN_SCRIPT_STATUS.Completed, EN_PROCESS_STEP.Completed) => AutoStepDone,
                _ => AutoStepWait
            };
        }

        private EN_SCRIPT_STATUS ResolveStepScriptStatus(string stepKey, EN_SCRIPT_STATUS defaultStatus)
        {
            if (stepKey.Equals(AutoStepComplete, StringComparison.OrdinalIgnoreCase))
            {
                return GetCurrentScriptStatusForComplete();
            }

            if (stepKey.Equals(AutoStepInspection, StringComparison.OrdinalIgnoreCase) && _lastScript is null)
            {
                return EN_SCRIPT_STATUS.NotCreated;
            }

            return defaultStatus;
        }

        private EN_SCRIPT_STATUS GetCurrentScriptStatusForComplete()
        {
            if (_lastScript is null)
            {
                return EN_SCRIPT_STATUS.NotCreated;
            }

            return _scriptStartedAt is null ? EN_SCRIPT_STATUS.Created : EN_SCRIPT_STATUS.Completed;
        }

        private static ST_STATION_PROCESS_FLOW_ITEM GetProcessFlowItem(string stepKey)
        {
            bool MatchesItem(global::Drilling.Common.Station.ST_STATION_PROCESS_FLOW_ITEM step)
            {
                return step.StepKey.Equals(stepKey, StringComparison.OrdinalIgnoreCase);
            }

            return ProcessFlowItems.FirstOrDefault(MatchesItem) ?? throw new InvalidOperationException($"Auto step is not defined: {stepKey}");
        }

        private static string GetAutoStepName(string stepKey)
        {
            bool MatchesItem(global::Drilling.Common.Station.CStationProcess.ST_AUTO_STEP_INFO step)
            {
                return step.Key == stepKey;
            }

            return AutoStepInfos.FirstOrDefault(MatchesItem)?.DisplayName ?? stepKey;
        }

        private static Dictionary<string, string> CreateAutoStepStateMap()
        {
            string SelectKey(global::Drilling.Common.Station.CStationProcess.ST_AUTO_STEP_INFO step)
            {
                return step.Key;
            }

            string SelectAutoStepWait(global::Drilling.Common.Station.CStationProcess.ST_AUTO_STEP_INFO arg0)
            {
                return AutoStepWait;
            }

            return AutoStepInfos.ToDictionary(SelectKey, SelectAutoStepWait, StringComparer.OrdinalIgnoreCase);
        }

        private static string LifecycleState(EN_SCRIPT_STATUS scriptStatus, EN_PROCESS_STEP processStep, int stepNo)
        {
            return (scriptStatus, processStep, stepNo) switch
            {
                (EN_SCRIPT_STATUS.NotCreated, _, 0) => "ACTIVE",
                (EN_SCRIPT_STATUS.Created, _, 0) => "DONE",
                (EN_SCRIPT_STATUS.Created, _, 1) => "ACTIVE",
                (EN_SCRIPT_STATUS.Running, _, <= 2) => "DONE",
                (EN_SCRIPT_STATUS.Running, _, 3) => "ACTIVE",
                (EN_SCRIPT_STATUS.Completed, _, _) => stepNo < 4 ? "DONE" : "ACTIVE",
                (EN_SCRIPT_STATUS.Error, _, _) => stepNo == 3 ? "ERROR" : "-",
                _ => "-"
            };
        }

        private static string FormatAttenuatorStatus(ST_ATTENUATOR_STATUS status)
        {
            return $"Current={FormatDouble(status.CurrentPosition)}, Target={FormatDouble(status.TargetPosition)}, State={status.CommandState}, Error={status.LastError}";
        }

        private static string FormatBETStatus(ST_BET_STATUS status)
        {
            return $"MAG={FormatDouble(status.CurrentMagnification)}/{FormatDouble(status.TargetMagnification)}, DIV={FormatDouble(status.CurrentDivergence)}/{FormatDouble(status.TargetDivergence)}, Moving={status.IsMoving}, Alarm={status.AlarmOn}, Error={status.LastError}";
        }

        private static string FormatCommState(EN_COMM_STATE state)
        {
            return state switch
            {
                EN_COMM_STATE.Online => "ONLINE",
                EN_COMM_STATE.Simulation => "SIMULATION",
                _ => "OFFLINE"
            };
        }

        private static string FormatInterfaceName(ST_INTERFACE_DATA data)
        {
            return $"{data.Device}[{data.Number}]/{data.NickName}";
        }

        private static string FormatInterfaceName(ST_INTERFACE_COMM_STATUS status)
        {
            return $"{status.Module}[{status.Number}]/{status.NickName}";
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static double ReadDirectionAny(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                return NormalizeDirection(value.Trim(), key);
            }

            return NormalizeDirection(defaultValue, "DEFAULT_DIRECTION");
        }

        private static double NormalizeDirection(string value, string key)
        {
            var normalized = value.Trim().ToUpperInvariant();
            if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return NormalizeDirection(number, key);
            }

            if (normalized is "-" or "-1" or "REVERSE" or "REV" or "NEGATIVE" or "MINUS")
            {
                return -1.0;
            }

            if (normalized is "+" or "+1" or "FORWARD" or "FWD" or "POSITIVE" or "PLUS")
            {
                return 1.0;
            }

            throw new InvalidOperationException($"Invalid direction value. {key}={value}");
        }

        private static double NormalizeDirection(double value, string key)
        {
            if (!double.IsFinite(value) || (value != -1.0 && value != 1.0))
            {
                throw new InvalidOperationException($"Direction setting {key} must be -1 or +1. Current value: {value}");
            }

            return value;
        }

        private static string FormatCommand(string command, IReadOnlyList<(string Key, string Value)> arguments)
        {
            string SelectItem((string Key, string Value) argument)
            {
                return $"{argument.Key}={argument.Value}";
            }

            return string.Join(";", new[] { command }.Concat(arguments.Select(SelectItem)));
        }

        private static bool IsProcessControlSuccessResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var firstToken = response.Trim().ToUpperInvariant().Split([';', '|', ':', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return firstToken is "OK" or "ACK" or "SUCCESS" or "ACCEPTED" or "DONE";
        }

        private static string FormatResponseForLog(string response)
        {
            return string.IsNullOrWhiteSpace(response) ? "<empty>" : response.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static double? ReadNullableDoubleAny(IReadOnlyDictionary<string, string> parameters, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                {
                    return result;
                }
            }

            return null;
        }

        private static int? ReadNullableIntAny(IReadOnlyDictionary<string, string> parameters, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (!parameters.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return (int)Math.Round(doubleResult);
                }
            }

            return null;
        }

        private static double ReadDouble(IReadOnlyDictionary<string, string> parameters, string key, double defaultValue)
        {
            return parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private static int ReadInt(IReadOnlyDictionary<string, string> parameters, string key, int defaultValue)
        {
            return parameters.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private static double ReadDoubleAny(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                {
                    return result;
                }
            }

            return defaultValue;
        }

        private static int ReadIntAny(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (!parameters.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return (int)Math.Round(doubleResult);
                }
            }

            return defaultValue;
        }

        private static bool IsBufferedRunEnabled(IReadOnlyDictionary<string, string> parameters)
        {
            return ReadBoolAny(parameters, false, ScriptBufferedRunUseKey);
        }

        private static int ReadScriptRunTimeoutMs(IReadOnlyDictionary<string, string> parameters)
        {
            return Math.Max(0, ReadIntAny(parameters, ScriptRunDefaultTimeoutMs, ScriptRunTimeoutMsKey, ScriptBufferedRunTimeoutMsKey));
        }

        private static int ReadScriptRunPollIntervalMs(IReadOnlyDictionary<string, string> parameters)
        {
            return Math.Max(10, ReadIntAny(parameters, ScriptRunDefaultPollIntervalMs, ScriptRunPollIntervalMsKey));
        }

        private static int ReadBufferedRunLinesPerCommand(IReadOnlyDictionary<string, string> parameters)
        {
            return Math.Max(2, ReadIntAny(parameters, 1000, ScriptBufferedRunLinesPerCommandKey));
        }

        private static int ReadBufferedRunQueueSize(IReadOnlyDictionary<string, string> parameters)
        {
            return Math.Max(1, ReadIntAny(parameters, 100, ScriptBufferedRunQueueSizeKey));
        }

        private static int ReadBufferedRunTimeoutMs(IReadOnlyDictionary<string, string> parameters)
        {
            return Math.Max(0, ReadIntAny(parameters, 600000, ScriptBufferedRunTimeoutMsKey));
        }

        private static bool ReadBool(IReadOnlyDictionary<string, string> parameters, string key, bool defaultValue)
        {
            if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim().ToUpperInvariant() switch
            {
                "1" or "Y" or "YES" or "TRUE" or "ON" or "USE" => true,
                "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" => false,
                _ => defaultValue
            };
        }

        private static bool ReadBoolAny(IReadOnlyDictionary<string, string> parameters, bool defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim().ToUpperInvariant() switch
                    {
                        "1" or "Y" or "YES" or "TRUE" or "ON" or "USE" => true,
                        "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" => false,
                        _ => defaultValue
                    };
                }
            }

            return defaultValue;
        }

        private static bool ReadBoolValue(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim().ToUpperInvariant() switch
            {
                "1" or "Y" or "YES" or "TRUE" or "ON" or "USE" => true,
                "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" => false,
                _ => defaultValue
            };
        }

        private static string ReadText(IReadOnlyDictionary<string, string> parameters, string key, string defaultValue)
        {
            return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : defaultValue;
        }

        private static string ReadTextAny(IReadOnlyDictionary<string, string> parameters, string defaultValue, IEnumerable<string> keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return defaultValue;
        }

        private static string[] CreateHeadKeys(int headNo, params string[] names)
        {
            var paddedHeadNoText = headNo.ToString("00", CultureInfo.InvariantCulture);
            string SelectItem(string name)
            {
                return $"H{paddedHeadNoText}_{name}";
            }

            return names.Select(SelectItem).ToArray();
        }

        private static bool ReadHeadUse(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            return ReadBoolAny(parameters, true, CreateHeadKeys(headNo, "USE"));
        }

        private static int ReadHeadAutomationNo(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            return Math.Max(0, ReadIntAny(parameters, headNo <= 4 ? 0 : 1, CreateHeadKeys(headNo, "AUTOMATION_NO")));
        }

        private static int ReadHeadAutomationTaskNo(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            var defaultTaskNo = ((headNo - 1) % 4) + 1;
            return Math.Max(1, ReadIntAny(parameters, defaultTaskNo, CreateHeadKeys(headNo, "AUTOMATION_TASK_NO")));
        }

        private static string ReadAnyParameter(ST_PROCESS_MODEL processModel, string defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (processModel.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return defaultValue;
        }

        private ST_PROCESS_MODEL GetProcessModel()
        {
            return _processModel ?? throw new InvalidOperationException("Process Model is not built.");
        }

        private ST_STATION_PROCESS_STATUS SetAlarm(string message, CancellationToken cancellationToken)
        {
            SetSequenceState(EN_STATION_SEQUENCE.Error);
            AddProcessLog("ERROR", "STATION", message);
            MarkRunningAutoSteps(AutoStepError);
            TrySetProductError(message, cancellationToken);
            var result = new ST_PROCESS_RESULT(false, message, DateTimeOffset.Now);
            TryReportProcessResult(result, "ALARM", cancellationToken, EN_STATION_STATE.Alarm);
            _snapshot = CreateSnapshot(_snapshot.ProcessModel, SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Error), EN_SCRIPT_STATUS.Error, EN_PROCESS_STEP.Error, result);
            SetStationState(EN_STATION_STATE.Alarm, EN_PROCESS_STEP.Error, EN_SCRIPT_STATUS.Error, message);
            return _snapshot;
        }

        private void SetSequenceState(EN_STATION_SEQUENCE sequenceState)
        {
            lock (_sequenceLock)
            {
                if (_sequenceState == sequenceState)
                {
                    return;
                }

                _sequenceState = sequenceState;
                _stationStatus = (_stationStatus with
                {
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(sequenceState);
            }
        }

        private bool TryMoveSequenceState(EN_STATION_SEQUENCE expectedState, EN_STATION_SEQUENCE nextState)
        {
            lock (_sequenceLock)
            {
                if (_sequenceState != expectedState)
                {
                    return false;
                }

                _sequenceState = nextState;
                _stationStatus = (_stationStatus with
                {
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(nextState);
                return true;
            }
        }

        private EN_STATION_SEQUENCE ReadSequenceState()
        {
            lock (_sequenceLock)
            {
                return _sequenceState;
            }
        }

        private ST_STATION_STATUS ReadStationStatus()
        {
            lock (_sequenceLock)
            {
                return _stationStatus;
            }
        }

        private void SetStationState(EN_STATION_STATE state, EN_PROCESS_STEP processStep, EN_SCRIPT_STATUS scriptStatus, string message)
        {
            ST_STATION_STATUS stationStatus;
            lock (_sequenceLock)
            {
                _stationStatus = (_stationStatus with
                {
                    State = state,
                    ProcessStep = processStep,
                    ScriptStatus = scriptStatus,
                    LastMessage = message,
                    ChangedAt = DateTimeOffset.Now
                }

                ).WithSequenceState(_sequenceState);
                stationStatus = _stationStatus;
            }

            _logManager?.WriteStationState(stationStatus.StationName, state.ToString().ToUpperInvariant(), processStep.ToString().ToUpperInvariant(), message);
        }

        private void AddProcessLog(string level, string source, string message)
        {
            // File logging is queued independently of the bounded UI history. Clearing UI rows does not erase trace files.
            var cycle = StageRequests?.LogCycleId ?? "";
            var model = _processModel;
            if (StageRequests?.IsRunning == true && (cycle.Length == 0 || cycle != _stageModelLogCycle))
                model = null;
            _logManager?.WriteSequence(level, source, message, $"Cycle={cycle}; Process={model?.ProcessId}; Recipe={model?.RecipeId}; Glass={model?.GlassId}; Lot={model?.LotId}");
            lock (_processLogLock)
            {
                _processLogs.Add(new ST_PROCESS_LOG_ITEM(DateTimeOffset.Now, level, source, message));
                var overflowCount = _processLogs.Count - MaximumProcessLogCount;
                if (overflowCount > 0)
                {
                    _processLogs.RemoveRange(0, overflowCount);
                }
            }
        }

        private void ClearProcessLogs()
        {
            lock (_processLogLock)
            {
                _processLogs.Clear();
            }
        }

        private ST_PROCESS_LOG_ITEM[] GetProcessLogsSnapshot()
        {
            lock (_processLogLock)
            {
                return _processLogs.ToArray();
            }
        }

        private void AddSequenceLog(string level, string source, string message)
        {
            var stationStatus = ReadStationStatus();
            AddProcessLog(level, source, message);
            try
            {
                _logManager?.WriteStationState(stationStatus.StationName, stationStatus.State.ToString().ToUpperInvariant(), $"SEQ_{source}_{level}", message);
            }
            catch
            {
            // Sequence execution must not be retried because a diagnostic log write failed.
            }
        }

        private static string FormatInterLockBlockedMessage(ST_INTERLOCK_SUMMARY interLock)
        {
            bool MatchesItem(global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM x)
            {
                return x.Level != EN_INTERLOCK_LEVEL.Ok;
            }

            var item = interLock.Items.FirstOrDefault(MatchesItem);
            return item is null ? "InterLock is not ready." : $"InterLock is not ready. {item.Signal}: {item.Detail}";
        }

        private static string FormatStartInterLockBlockedMessage(ST_INTERLOCK_SUMMARY interLock)
        {
            bool MatchesItem(global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM item)
            {
                return item.Level != EN_INTERLOCK_LEVEL.Ok;
            }

            string FormatItem(global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM item)
            {
                var detail = string.IsNullOrWhiteSpace(item.Detail) ? item.State : item.Detail;
                return $"- {item.Signal}: {detail}";
            }

            var blockedItems = interLock.Items.Where(MatchesItem).Select(FormatItem).ToArray();
            if (blockedItems.Length == 0)
            {
                return "공정 시작에 실패했습니다. 인터락을 확인하세요.";
            }

            return "공정 시작에 실패했습니다. 인터락을 확인하세요." + Environment.NewLine + string.Join(Environment.NewLine, blockedItems);
        }

        private static string FormatScriptStatus(EN_SCRIPT_STATUS status)
        {
            return status switch
            {
                EN_SCRIPT_STATUS.NotCreated => "Not Created",
                _ => status.ToString()};
        }

        private static string FormatExecuteState(EN_SCRIPT_STATUS scriptStatus, ST_PROCESS_RESULT? result, bool hasScript)
        {
            if (!hasScript)
            {
                return result is null ? "-" : "Not Executed";
            }

            if (result is not null)
            {
                return GetProcessResultCode(result) == "SKIPPED" ? "Skipped" : result.IsSuccess ? "Completed" : "Failed";
            }

            return scriptStatus switch
            {
                EN_SCRIPT_STATUS.Running => "Running",
                EN_SCRIPT_STATUS.Created => "Ready",
                EN_SCRIPT_STATUS.Error => "Error",
                _ => "-"
            };
        }

        private static string FormatScriptResult(EN_SCRIPT_STATUS scriptStatus, ST_PROCESS_RESULT? result, bool hasScript)
        {
            if (!hasScript)
            {
                return result is null ? "-" : "Not Executed";
            }

            if (result is not null)
            {
                return GetProcessResultCode(result);
            }

            return scriptStatus is EN_SCRIPT_STATUS.Created or EN_SCRIPT_STATUS.Running ? "In Progress" : "-";
        }

        private static string FormatDateTime(DateTimeOffset? value)
        {
            return value?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";
        }

        private static string FormatProcessResultDetail(ST_PROCESS_RESULT result, string processId, string recipeId, string productId)
        {
            var state = GetProcessResultCode(result);
            return string.Join(
                ", ", 
                $"Process={processId}", 
                $"Recipe={recipeId}", 
                $"Product={productId}", 
                $"Result={state}", 
                $"Message={result.Message}", 
                $"Time={result.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}"
            );
        }

        private static string GetProcessResultCode(ST_PROCESS_RESULT result)
        {
            return string.IsNullOrWhiteSpace(result.ResultCode) ? result.IsSuccess ? "OK" : "NG" : result.ResultCode.Trim().ToUpperInvariant();
        }

        private static string FormatDuration(TimeSpan value)
        {
            return value == TimeSpan.Zero ? "00:00:00" : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        private sealed record ST_BUFFERED_RUN_GROUP_RESULT(int AutomationNo, string HeadSummary, string Response);
        private sealed record ST_BUFFERED_RUN_STOP_RESULT(int AutomationNo, int TaskNo, string HeadSummary, bool IsSuccess, string Response, string ErrorMessage);
        private readonly record struct ST_AUTOMATION_TASK_KEY(int AutomationNo, int TaskNo);
        private sealed record ST_AUTOMATION_TASK_TARGET(int AutomationNo, int TaskNo, string HeadSummary);
        private sealed record ST_AUTOMATION_TASK_STATUS(int TaskNo, string State, string SourceFile, string ErrorMessage, string RawResponse);
        private sealed record ST_AUTOMATION_TASK_LINE_STATUS(int TaskNo, int CurrentLine, string RawResponse);
        private sealed record ST_SCANNER_STATUS_LOG_AXIS_TARGET(int HeadNo, int AutomationNo, int ControllerNo, int TaskNo, string AxisRole, string AxisName, int AioNo, bool ReadExtendedStatus, string FilePath);
        private sealed record ST_QUEUE_COUNT_LOG_TARGET(int AutomationNo, int ControllerNo, int TaskNo, string HeadNo, string ScriptFile, int TotalLine, string FilePath);
        private sealed record ST_AUTO_STEP_INFO(string Key, string DisplayName);
        private sealed class CScannerStatusLogger : IDisposable
        {
            private const int ScannerStatusReadTimeoutMs = 5000;
            private const string Header = "Timestamp,RecipeId,Head,ControllerNo,TaskNo,AxisName,AxisPosition,AxisCurrentFeedback,AxisEnableStatus,AxisHomeStatus,AxisFault,GalvoTemperature,GalvoLaserOutputRaw,PositionFbkAux,TaskStatus,ProcessorUsage";
            private readonly CStationProcess _owner;
            private readonly string _recipeId;
            private readonly IReadOnlyList<ST_SCANNER_STATUS_LOG_AXIS_TARGET> _targets;
            private readonly CancellationTokenSource _cancellation = new();
            private readonly object _writeLock = new();
            private readonly Thread _thread;
            private Guid _snapshotOwnerId = Guid.Empty;
            private string _lastError = "";
            private bool _started;
            private bool _stopped;
            private CScannerStatusLogger(CStationProcess owner, string recipeId, IReadOnlyList<ST_SCANNER_STATUS_LOG_AXIS_TARGET> targets)
            {
                _owner = owner;
                _recipeId = recipeId;
                _targets = targets;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "ScannerStatusLogger"
                };
            }

            public static CScannerStatusLogger Create(CStationProcess owner, string recipeId, bool bufferedRun, IReadOnlyDictionary<string, string> parameters, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
            {
                var targets = CreateTargets(bufferedRun, parameters, headScripts);
                return new CScannerStatusLogger(owner, recipeId, targets);
            }

            public void Start()
            {
                if (_started || _targets.Count == 0)
                {
                    return;
                }

                _snapshotOwnerId = CScannerStatusLiveSnapshotStore.Begin();
                _started = true;
                _thread.Start();
            }

            public void StopAndWriteFinal()
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _cancellation.Cancel();
                if (_started && !_thread.Join(QueueCountLogStopWaitMs))
                {
                    _owner.AddProcessLog("WARN", "SCANNER_STATUS", "Scanner status logger did not stop before timeout.");
                }

                if (_started)
                {
                    WriteSnapshotOnce();
                }

                if (_snapshotOwnerId != Guid.Empty)
                {
                    CScannerStatusLiveSnapshotStore.End(_snapshotOwnerId);
                    _snapshotOwnerId = Guid.Empty;
                }
            }

            public void Dispose()
            {
                StopAndWriteFinal();
                _cancellation.Dispose();
            }

            private static IReadOnlyList<ST_SCANNER_STATUS_LOG_AXIS_TARGET> CreateTargets(bool bufferedRun, IReadOnlyDictionary<string, string> parameters, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
            {
                var targets = new List<ST_SCANNER_STATUS_LOG_AXIS_TARGET>();
                var rootDirectory = ResolveRootDirectory(headScripts);
                var dateDirectory = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var modeDirectory = bufferedRun ? "Buffered" : "Standard";
                foreach (var script in headScripts)
                {
                    if (script.TotalShots <= 0)
                    {
                        continue;
                    }

                    var headNo = script.HeadNo;
                    if (!ReadHeadUse(parameters, headNo))
                    {
                        continue;
                    }

                    var controllerNo = script.AutomationNo + 1;
                    var filePath = Path.Combine(rootDirectory, dateDirectory, modeDirectory, $"Controller {controllerNo.ToString(CultureInfo.InvariantCulture)}", $"Task {script.TaskNo.ToString(CultureInfo.InvariantCulture)}.txt");
                    var aioNo = Math.Max(0, ReadIntAny(parameters, 0, CreateHeadKeys(headNo, "SCANNER_AIO_NO")));
                    AddAxisTarget(targets, script, controllerNo, "X", ReadTextAny(parameters, "", CreateHeadKeys(headNo, "X_AXIS", "GX_AXIS")), aioNo, true, filePath);
                    AddAxisTarget(targets, script, controllerNo, "Y", ReadTextAny(parameters, "", CreateHeadKeys(headNo, "Y_AXIS", "GY_AXIS")), aioNo, true, filePath);
                    if (ReadBoolAny(parameters, false, CreateHeadKeys(headNo, "SCANNER_Z_USE", "Z_USE")))
                    {
                        AddAxisTarget(targets, script, controllerNo, "Z", ReadTextAny(parameters, "", CreateHeadKeys(headNo, "SCANNER_Z_AXIS", "Z_AXIS")), aioNo, false, filePath);
                    }
                }

                return targets;
            }

            private static void AddAxisTarget(
                List<ST_SCANNER_STATUS_LOG_AXIS_TARGET> targets, 
                ST_HEAD_SCRIPT_RESULT script, 
                int controllerNo, 
                string axisRole, 
                string axisName, 
                int aioNo, 
                bool readExtendedStatus, 
                string filePath
            )
            {
                targets.Add(new ST_SCANNER_STATUS_LOG_AXIS_TARGET(script.HeadNo, script.AutomationNo, controllerNo, script.TaskNo, axisRole, axisName.Trim(), aioNo, readExtendedStatus, filePath));
            }

            private static string ResolveRootDirectory(IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
            {
                foreach (var script in headScripts)
                {
                    if (string.IsNullOrWhiteSpace(script.FilePath))
                    {
                        continue;
                    }

                    var directoryPath = Path.GetDirectoryName(Path.GetFullPath(script.FilePath));
                    if (string.IsNullOrWhiteSpace(directoryPath))
                    {
                        continue;
                    }

                    var directory = new DirectoryInfo(directoryPath);
                    while (directory is not null)
                    {
                        if (directory.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null)
                        {
                            return Path.Combine(directory.Parent.FullName, "Log", ScannerStatusLogDirectoryName);
                        }

                        directory = directory.Parent;
                    }
                }

                return Path.Combine(AppContext.BaseDirectory, "Log", ScannerStatusLogDirectoryName);
            }

            private void Run()
            {
                while (!_cancellation.Token.WaitHandle.WaitOne(ScannerStatusLogIntervalMs))
                {
                    WriteSnapshotOnce();
                }
            }

            private void WriteSnapshotOnce()
            {
                if (_targets.Count == 0)
                {
                    return;
                }

                var elapsed = Stopwatch.StartNew();
                lock (_writeLock)
                {
                    var taskStatusCache = new Dictionary<ST_AUTOMATION_TASK_KEY, string>();
                    var processorUsageCache = new Dictionary<int, string>();
                    var snapshot = new List<ST_SCANNER_AXIS_LIVE_STATUS>(_targets.Count);
                    foreach (var target in _targets)
                    {
                        var liveStatus = ReadTargetSnapshot(target, taskStatusCache, processorUsageCache);
                        snapshot.Add(liveStatus);
                        if (!target.AxisRole.Equals("Z", StringComparison.OrdinalIgnoreCase))
                        {
                            WriteTargetSnapshot(target, liveStatus);
                        }
                    }

                    if (_snapshotOwnerId != Guid.Empty)
                    {
                        CScannerStatusLiveSnapshotStore.Update(_snapshotOwnerId, snapshot);
                    }
                }

                elapsed.Stop();
                if (elapsed.ElapsedMilliseconds >= ScannerStatusReadTimeoutMs)
                {
                    RememberError($"Scanner status snapshot delayed. ElapsedMs={elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}, TargetCount={_targets.Count.ToString(CultureInfo.InvariantCulture)}");
                }
            }

            private ST_SCANNER_AXIS_LIVE_STATUS ReadTargetSnapshot(ST_SCANNER_STATUS_LOG_AXIS_TARGET target, Dictionary<ST_AUTOMATION_TASK_KEY, string> taskStatusCache, Dictionary<int, string> processorUsageCache)
            {
                var taskStatus = ReadTaskStatus(target, taskStatusCache);
                var processorUsage = ReadProcessorUsage(target, processorUsageCache);
                var headName = $"H{target.HeadNo:00}";
                var automationLabel = $"#{target.AutomationNo.ToString(CultureInfo.InvariantCulture)}";
                if (string.IsNullOrWhiteSpace(target.AxisName) || target.AxisName.Equals("-", StringComparison.Ordinal))
                {
                    return new ST_SCANNER_AXIS_LIVE_STATUS(
                        target.HeadNo, 
                        target.AutomationNo, 
                        target.ControllerNo, 
                        target.TaskNo, 
                        headName, 
                        automationLabel, 
                        target.AxisRole, 
                        "-", 
                        "-", 
                        "-", 
                        "NOT CONFIGURED", 
                        "NO", 
                        "-", 
                        "-", 
                        "-", 
                        "-", 
                        taskStatus, 
                        processorUsage, 
                        "NOT CONFIGURED", 
                        $"{headName} {target.AxisRole} axis name is not configured."
                    );
                }

                if (!IsAutomationAxisName(target.AxisName))
                {
                    return new ST_SCANNER_AXIS_LIVE_STATUS(
                        target.HeadNo, 
                        target.AutomationNo, 
                        target.ControllerNo, 
                        target.TaskNo, 
                        headName, 
                        automationLabel, 
                        target.AxisRole, 
                        target.AxisName, 
                        "-", 
                        "-", 
                        "NOT CONFIGURED", 
                        "NO", 
                        "-", 
                        "-", 
                        "-", 
                        "-", 
                        taskStatus, 
                        processorUsage, 
                        "NOT CONFIGURED", 
                        $"{headName} {target.AxisRole} axis name is invalid: {target.AxisName}."
                    );
                }

                try
                {
                    if (!_owner._automationManager.IsSimul(target.AutomationNo) && !_owner._automationManager.IsConnect(target.AutomationNo))
                    {
                        return new ST_SCANNER_AXIS_LIVE_STATUS(
                            target.HeadNo, 
                            target.AutomationNo, 
                            target.ControllerNo, 
                            target.TaskNo, 
                            headName, 
                            automationLabel, 
                            target.AxisRole, 
                            target.AxisName, 
                            "-", 
                            "-", 
                            "OFFLINE", 
                            "-", 
                            "-", 
                            "-", 
                            "-", 
                            "-", 
                            taskStatus, 
                            processorUsage, 
                            "OFFLINE", 
                            "Automation not connected."
                        );
                    }

                    var cached = _owner._automationManager.StatusSnapshots.GetAxis(target.AxisName, target.AutomationNo, target.AioNo, target.ReadExtendedStatus);
                    if (cached.Value is not { } status)
                        return new ST_SCANNER_AXIS_LIVE_STATUS(
                            target.HeadNo, 
                            target.AutomationNo, 
                            target.ControllerNo, 
                            target.TaskNo, 
                            headName, 
                            automationLabel, 
                            target.AxisRole, 
                            target.AxisName, 
                            "-", 
                            "-", 
                            cached.State, 
                            "-", 
                            "-", 
                            "-", 
                            "-", 
                            "-", 
                            taskStatus, 
                            processorUsage, 
                            cached.State, 
                            cached.Detail
                        );
                    return new ST_SCANNER_AXIS_LIVE_STATUS(
                        target.HeadNo, 
                        target.AutomationNo, 
                        target.ControllerNo, 
                        target.TaskNo, 
                        headName, 
                        automationLabel, 
                        target.AxisRole, 
                        target.AxisName, 
                        FormatScannerPosition(status.PositionFeedback), 
                        FormatExtendedScannerValue(status.CurrentFeedback, target.ReadExtendedStatus), 
                        status.Able ? "ABLE" : "DISABLE", 
                        status.HomeDone ? "OK" : "NO", 
                        FormatStatusText(status.Fault), 
                        FormatExtendedScannerTemperature(status.MotorTemperature, target.ReadExtendedStatus), 
                        FormatExtendedScannerValue(status.GalvoLaserOutputRaw, target.ReadExtendedStatus), 
                        FormatExtendedScannerValue(status.AuxiliaryFeedback, target.ReadExtendedStatus), 
                        taskStatus, 
                        processorUsage, 
                        status.HasError ? "ERROR" : "OK", 
                        string.IsNullOrWhiteSpace(status.RawResponse) ? "-" : status.RawResponse
                    );
                }
                catch (Exception ex)
                {
                    RememberError($"Scanner status log skipped. Controller {target.ControllerNo}, Task {target.TaskNo}, H{target.HeadNo:00} {target.AxisRole}: {ex.Message}");
                    return new ST_SCANNER_AXIS_LIVE_STATUS(
                        target.HeadNo, 
                        target.AutomationNo, 
                        target.ControllerNo, 
                        target.TaskNo, 
                        headName, 
                        automationLabel, 
                        target.AxisRole, 
                        target.AxisName, 
                        "-", 
                        "-", 
                        "ERROR", 
                        "NO", 
                        "-", 
                        "-", 
                        "-", 
                        "-", 
                        taskStatus, 
                        processorUsage, 
                        "ERROR", 
                        ex.Message
                    );
                }
            }

            private string ReadTaskStatus(ST_SCANNER_STATUS_LOG_AXIS_TARGET target, Dictionary<ST_AUTOMATION_TASK_KEY, string> cache)
            {
                var key = new ST_AUTOMATION_TASK_KEY(target.AutomationNo, target.TaskNo);
                if (cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var status = "ERROR";
                try
                {
                    if (_owner._automationManager.IsSimul(target.AutomationNo))
                    {
                        status = "SIM";
                    }
                    else if (!_owner._automationManager.IsConnect(target.AutomationNo))
                    {
                        status = "OFFLINE";
                    }
                    else
                    {
                        var shared = _owner._automationManager.StatusSnapshots.GetTask(target.TaskNo, target.AutomationNo);
                        status = shared.Value is { } response ? ParseAutomationTaskStatus(response).State : shared.State;
                    }
                }
                catch (Exception ex)
                {
                    RememberError($"Scanner status task read skipped. Controller {target.ControllerNo}, Task {target.TaskNo}: {ex.Message}");
                }

                cache[key] = status;
                return status;
            }

            private string ReadProcessorUsage(ST_SCANNER_STATUS_LOG_AXIS_TARGET target, Dictionary<int, string> cache)
            {
                if (cache.TryGetValue(target.AutomationNo, out var cached))
                {
                    return cached;
                }

                var processorUsage = "ERROR";
                try
                {
                    if (_owner._automationManager.IsSimul(target.AutomationNo))
                    {
                        processorUsage = "SIM";
                    }
                    else if (!_owner._automationManager.IsConnect(target.AutomationNo))
                    {
                        processorUsage = "OFFLINE";
                    }
                    else
                    {
                        var shared = _owner._automationManager.StatusSnapshots.GetProcessorUsage(target.AutomationNo);
                        processorUsage = shared.Value is { } response ? ParseProcessorUsage(response) : shared.State;
                    }
                }
                catch (Exception ex)
                {
                    RememberError($"Scanner status processor usage read skipped. Controller {target.ControllerNo}: {ex.Message}");
                }

                cache[target.AutomationNo] = processorUsage;
                return processorUsage;
            }

            private void WriteTargetSnapshot(ST_SCANNER_STATUS_LOG_AXIS_TARGET target, ST_SCANNER_AXIS_LIVE_STATUS status)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target.FilePath) ?? "");
                    if (!File.Exists(target.FilePath) || new FileInfo(target.FilePath).Length == 0)
                    {
                        File.AppendAllText(target.FilePath, Header + Environment.NewLine, Encoding.UTF8);
                    }

                    File.AppendAllText(target.FilePath, FormatLine(status) + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    RememberError($"Scanner status log write skipped. Controller {target.ControllerNo}, Task {target.TaskNo}: {ex.Message}");
                }
            }

            private void RememberError(string message)
            {
                if (message.Equals(_lastError, StringComparison.Ordinal))
                {
                    return;
                }

                _lastError = message;
                _owner.AddProcessLog("WARN", "SCANNER_STATUS", message);
            }

            private string FormatLine(ST_SCANNER_AXIS_LIVE_STATUS status)
            {
                var fields = new[]
                {
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    _recipeId,
                    status.Head,
                    status.ControllerNo.ToString(CultureInfo.InvariantCulture),
                    status.TaskNo.ToString(CultureInfo.InvariantCulture),
                    status.AxisNo,
                    status.AxisPosition,
                    status.AxisCurrentFeedback,
                    status.AxisEnableStatus,
                    status.AxisHomeStatus,
                    status.AxisFault,
                    status.GalvoTemperature,
                    status.GalvoLaserOutputRaw,
                    status.PositionFbkAux,
                    status.TaskStatus,
                    status.ProcessorUsage
                };
                return FormatCsv(fields);
            }

            private static string ParseProcessorUsage(string response)
            {
                var fields = response.Split(':');
                if (fields.Length >= 4 && fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) && fields[2].Equals("PROCESSOR_USAGE", StringComparison.OrdinalIgnoreCase) && double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var usage))
                {
                    return Math.Clamp(usage, 0.0, 100.0).ToString("F1", CultureInfo.InvariantCulture);
                }

                return response.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? "0.0" : "ERROR";
            }

            private static string FormatScannerPosition(double value)
            {
                if (!double.IsFinite(value))
                {
                    return "-";
                }

                return value.ToString("F3", CultureInfo.InvariantCulture);
            }

            private static string FormatExtendedScannerValue(double value, bool readExtendedStatus)
            {
                return readExtendedStatus ? FormatScannerPosition(value) : "-";
            }

            private static string FormatExtendedScannerTemperature(double value, bool readExtendedStatus)
            {
                if (!readExtendedStatus)
                {
                    return "-";
                }

                return CGalvoTemperature.FormatCelsius(value);
            }

            private static string FormatStatusText(string value)
            {
                return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            }

            private static bool IsAutomationAxisName(string value)
            {
                if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
                {
                    return false;
                }

                foreach (var character in value)
                {
                    if (!char.IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '_')
                    {
                        return false;
                    }
                }

                return true;
            }

            private static string FormatCsv(IReadOnlyList<string> fields)
            {
                var builder = new StringBuilder();
                for (var i = 0; i < fields.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(FormatCsvField(fields[i]));
                }

                return builder.ToString();
            }

            private static string FormatCsvField(string value)
            {
                var text = value ?? "";
                if (!text.Contains(',', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal) && !text.Contains('\n', StringComparison.Ordinal))
                {
                    return text;
                }

                return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            }
        }

        private sealed class CQueueCountLogger : IDisposable
        {
            private const string Header = "Timestamp,RecipeId,ControllerNo,TaskNo,HeadNo,ScriptFile,CurrentLine,TotalLine";
            private readonly CStationProcess _owner;
            private readonly string _recipeId;
            private readonly IReadOnlyList<ST_QUEUE_COUNT_LOG_TARGET> _targets;
            private readonly CancellationTokenSource _cancellation = new();
            private readonly object _writeLock = new();
            private readonly Thread _thread;
            private string _lastError = "";
            private bool _started;
            private bool _stopped;
            private CQueueCountLogger(CStationProcess owner, string recipeId, IReadOnlyList<ST_QUEUE_COUNT_LOG_TARGET> targets)
            {
                _owner = owner;
                _recipeId = recipeId;
                _targets = targets;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "QueueCountLogger"
                };
            }

            public static CQueueCountLogger Create(CStationProcess owner, string recipeId, bool bufferedRun, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
            {
                var targets = CreateTargets(bufferedRun, headScripts);
                return new CQueueCountLogger(owner, recipeId, targets);
            }

            public void Start()
            {
                if (_started || _targets.Count == 0)
                {
                    return;
                }

                _started = true;
                _thread.Start();
            }

            public void StopAndWriteFinal()
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _cancellation.Cancel();
                if (!_started)
                {
                    return;
                }

                if (_started && !_thread.Join(QueueCountLogStopWaitMs))
                {
                    _owner.AddProcessLog("WARN", "QUEUE_COUNT", "Queue count logger did not stop before timeout.");
                }

                WriteSnapshotOnce();
            }

            public void Dispose()
            {
                StopAndWriteFinal();
                _cancellation.Dispose();
            }

            private static IReadOnlyList<ST_QUEUE_COUNT_LOG_TARGET> CreateTargets(bool bufferedRun, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
            {
                var targets = new List<ST_QUEUE_COUNT_LOG_TARGET>();
                var rootDirectory = ResolveRootDirectory(headScripts);
                var dateDirectory = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var modeDirectory = bufferedRun ? "Buffered" : "Standard";
                foreach (var script in headScripts)
                {
                    if (script.TotalShots <= 0)
                    {
                        continue;
                    }

                    var controllerNo = script.AutomationNo + 1;
                    var filePath = Path.Combine(rootDirectory, dateDirectory, modeDirectory, $"Controller {controllerNo.ToString(CultureInfo.InvariantCulture)}", $"Task {script.TaskNo.ToString(CultureInfo.InvariantCulture)}.txt");
                    targets.Add(new ST_QUEUE_COUNT_LOG_TARGET(script.AutomationNo, controllerNo, script.TaskNo, $"H{script.HeadNo:00}", script.FileName, Math.Max(0, script.TotalLines), filePath));
                }

                return targets;
            }

            private static string ResolveRootDirectory(IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
            {
                foreach (var script in headScripts)
                {
                    if (string.IsNullOrWhiteSpace(script.FilePath))
                    {
                        continue;
                    }

                    var directoryPath = Path.GetDirectoryName(Path.GetFullPath(script.FilePath));
                    if (string.IsNullOrWhiteSpace(directoryPath))
                    {
                        continue;
                    }

                    var directory = new DirectoryInfo(directoryPath);
                    while (directory is not null)
                    {
                        if (directory.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null)
                        {
                            return Path.Combine(directory.Parent.FullName, "Log", QueueCountLogDirectoryName);
                        }

                        directory = directory.Parent;
                    }
                }

                return Path.Combine(AppContext.BaseDirectory, "Log", QueueCountLogDirectoryName);
            }

            private void Run()
            {
                while (!_cancellation.Token.WaitHandle.WaitOne(QueueCountLogIntervalMs))
                {
                    WriteSnapshotOnce();
                }
            }

            private void WriteSnapshotOnce()
            {
                if (_targets.Count == 0)
                {
                    return;
                }

                lock (_writeLock)
                {
                    foreach (var target in _targets)
                    {
                        WriteTargetSnapshot(target);
                    }
                }
            }

            private void WriteTargetSnapshot(ST_QUEUE_COUNT_LOG_TARGET target)
            {
                try
                {
                    var lineStatus = _owner.ReadAutomationTaskLineStatus(target.AutomationNo, target.TaskNo, CancellationToken.None);
                    Directory.CreateDirectory(Path.GetDirectoryName(target.FilePath) ?? "");
                    if (!File.Exists(target.FilePath) || new FileInfo(target.FilePath).Length == 0)
                    {
                        File.AppendAllText(target.FilePath, Header + Environment.NewLine, Encoding.UTF8);
                    }

                    File.AppendAllText(target.FilePath, FormatLine(target, lineStatus.CurrentLine) + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    RememberError($"Queue count log skipped. Controller {target.ControllerNo}, Task {target.TaskNo}: {ex.Message}");
                }
            }

            private void RememberError(string message)
            {
                if (message.Equals(_lastError, StringComparison.Ordinal))
                {
                    return;
                }

                _lastError = message;
                _owner.AddProcessLog("WARN", "QUEUE_COUNT", message);
            }

            private string FormatLine(ST_QUEUE_COUNT_LOG_TARGET target, int currentLine)
            {
                var fields = new[]
                {
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    _recipeId,
                    target.ControllerNo.ToString(CultureInfo.InvariantCulture),
                    target.TaskNo.ToString(CultureInfo.InvariantCulture),
                    target.HeadNo,
                    target.ScriptFile,
                    Math.Max(0, currentLine).ToString(CultureInfo.InvariantCulture),
                    target.TotalLine.ToString(CultureInfo.InvariantCulture)
                };
                return FormatCsv(fields);
            }

            private static string FormatCsv(IReadOnlyList<string> fields)
            {
                var builder = new StringBuilder();
                for (var i = 0; i < fields.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(FormatCsvField(fields[i]));
                }

                return builder.ToString();
            }

            private static string FormatCsvField(string value)
            {
                var text = value ?? "";
                if (!text.Contains(',', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal) && !text.Contains('\n', StringComparison.Ordinal))
                {
                    return text;
                }

                return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            }
        }

        private sealed class CBufferedRunWorker
        {
            private readonly CStationProcess _owner;
            private readonly int _automationNo;
            private readonly IReadOnlyList<ST_HEAD_SCRIPT_RESULT> _headScripts;
            private readonly int _queueSize;
            private readonly int _linesPerCommand;
            private readonly int _timeoutMs;
            private readonly CancellationToken _cancellationToken;
            private readonly Thread _thread;
            private volatile bool _isComplete;
            public CBufferedRunWorker(
                CStationProcess owner, 
                int automationNo, 
                IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts, 
                int queueSize, 
                int linesPerCommand, 
                int timeoutMs, 
                CancellationToken cancellationToken
            )
            {
                _owner = owner;
                _automationNo = automationNo;
                _headScripts = headScripts;
                _queueSize = queueSize;
                _linesPerCommand = linesPerCommand;
                _timeoutMs = timeoutMs;
                _cancellationToken = cancellationToken;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "BufferedRunA1_" + automationNo.ToString(CultureInfo.InvariantCulture)
                };
            }

            public bool IsComplete
            {
                get
                {
                    return _isComplete;
                }
            }

            public ST_BUFFERED_RUN_GROUP_RESULT? Result { get; private set; }
            public Exception? Error { get; private set; }

            public int AutomationNo
            {
                get
                {
                    return _automationNo;
                }
            }

            public void Start()
            {
                _thread.Start();
            }

            public bool WaitForCompletion(int timeoutMs)
            {
                return _thread.Join(timeoutMs);
            }

            private void Run()
            {
                try
                {
                    Result = _owner.RunBufferedHeadScriptGroup(_automationNo, _headScripts, _queueSize, _linesPerCommand, _timeoutMs, _cancellationToken);
                }
                catch (Exception exception)
                {
                    Error = exception;
                }
                finally
                {
                    _isComplete = true;
                }
            }
        }

#endregion
#region ManualMof
        public ST_MANUAL_MOF_STATUS ManualMofStatus
        {
            get
            {
                lock (_sequenceLock)
                    return new(_manualMofRunning, _manualMofRequiresStop, _manualMofMessage, _manualMofDirectory, _manualMofActions.GetSnapshot());
            }
        }

        public Task RunManualMofAsync(ST_STAGE_PROCESS_REQUEST request, IReadOnlySet<int> selectedHeads, CancellationToken token = default)
        {
            var heads = selectedHeads.Where(head => head is >= 1 and <= 8).ToHashSet();
            if (heads.Count == 0)
                throw new InvalidOperationException("Head를 선택해 주세요.");
            var frozen = request with
            {
                Parameters = new Dictionary<string, string>(request.Parameters, StringComparer.OrdinalIgnoreCase)
            };
            lock (_sequenceLock)
            {
                if (_manualMofRunning || _manualMofRequiresStop || StageRequests.IsRunning || _sequenceState != EN_STATION_SEQUENCE.Ready || !_stageOwnedTasks.IsEmpty || !_stageBufferedRuns.IsEmpty)
                    throw new InvalidOperationException("다른 가공이 실행 중이거나 정지가 확인되지 않았습니다.");
                token.ThrowIfCancellationRequested();
                _manualMofCancellation?.Dispose();
                _manualMofCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _manualMofRunning = true;
                _manualMofMessage = "MOF 준비 중";
                _manualMofDirectory = "";
                _manualMofActions.ResetToWaiting(CManualMofActionIds.All);
                var runToken = _manualMofCancellation.Token;
                _manualMofExecution = Task.Run(() => ExecuteManualMof(frozen, heads, runToken));
                return _manualMofExecution;
            }
        }

        public Task StopManualMofAsync()
        {
            lock (_sequenceLock)
            {
                if (_manualMofRunning)
                {
                    _manualMofCancellation?.Cancel();
                    return ObserveManualMofStop(_manualMofExecution);
                }

                if (!_manualMofRequiresStop)
                    return Task.CompletedTask;
                _manualMofRunning = true;
                return _manualMofExecution = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await StopStageTasks(timeout.Token).ConfigureAwait(false);
                        lock (_sequenceLock)
                        {
                            _manualMofRequiresStop = false;
                            _sequenceState = EN_STATION_SEQUENCE.Ready;
                            _manualMofMessage = "MOF 정지 완료";
                        }
                    }
                    finally
                    {
                        lock (_sequenceLock)
                            _manualMofRunning = false;
                    }
                });
            }
        }

        private static async Task ObserveManualMofStop(Task execution)
        {
            try
            {
                await execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ExecuteManualMof(ST_STAGE_PROCESS_REQUEST request, HashSet<int> heads, CancellationToken token)
        {
            CStageReviewSequences.Session? stage = null;
            var success = false;
            var cancelled = false;
            var message = "";
            try
            {
                double start = 0, target = 0, speed = 0;
                await _manualMofActions.RunAsync("PREPARE", async () =>
                {
                    PrepareProcessCore(request.ProcessId, request.RecipeId, request.GlassId, request.LotId, request.Parameters, token, preserveSequenceActions: true, manualHeads: heads);
                    SetSequenceState(EN_STATION_SEQUENCE.Process);
                    var model = GetProcessModel();
                    if (model.HeadPlans.Count == 0 || !model.HeadPlans.Any(head => head.Shots.Count > 0))
                        throw new InvalidOperationException("선택한 Head에 가공할 Shot이 없습니다.");
                    CheckManualMofInterlocks(model, heads, token);
                    await CheckManualMofTasksIdle(model, token).ConfigureAwait(false);
                    stage = OpenManualMofStage();
                    start = await stage.ReadStationaryPosition(EN_MANUAL_STAGE_AXIS.StageY, token).ConfigureAwait(false);
                    var length = ReadRequiredManualMofNumber(model.Parameters, "GLASS_SIZE_Y");
                    speed = ReadRequiredManualMofNumber(model.Parameters, "STAGE_SPEED");
                    var direction = ReadRequiredManualMofNumber(model.Parameters, "STAGE_SCAN_DIRECTION_Y");
                    if (length <= 0 || speed <= 0 || direction is not (-1 or 1))
                        throw new InvalidOperationException("Glass Size Y와 Stage Speed는 0보다 커야 하며 Stage Direction은 -1 또는 1이어야 합니다.");
                    target = start + length * direction;
                    if (!double.IsFinite(target) || !double.IsFinite(length / speed))
                        throw new InvalidOperationException("Stage 목표 위치 또는 이동 시간을 확인해 주세요.");
                    AddProcessLog("INFO", "MANUAL_MOF", $"Stage {(stage.IsSimulation ? "SIMULATION" : "ONLINE")} Process Move: {start:F3} -> {target:F3} mm, {speed:F3} mm/sec; Heads={string.Join(",", heads.Order())}");
                }).ConfigureAwait(false);
                var model = GetProcessModel();
                await _manualMofActions.RunAsync("BUILD", () =>
                {
                    token.ThrowIfCancellationRequested();
                    var mode = IsBufferedRunEnabled(model.Parameters) ? "Buffered" : "Standard";
                    _lastScript = _automationScriptFile.Build(model, Path.Combine("ManualMOF", mode), token);
                    _scriptCreatedAt = _lastScript.CreatedAt;
                    lock (_sequenceLock)
                        _manualMofDirectory = Path.GetDirectoryName(_lastScript.FilePath) ?? "";
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                await _manualMofActions.RunAsync("UPLOAD", () =>
                {
                    UploadAutomationScript(model, token);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                await _manualMofActions.RunAsync("ZERO", async () =>
                {
                    CheckManualMofInterlocks(model, heads, token);
                    var targets = model.HeadPlans.Select(head => new ST_MOF_ZERO_TARGET(head.AutomationNo, head.TaskNo, ReadTextAny(model.Parameters, ReadTextAny(model.Parameters, "GX", CreateHeadKeys(head.HeadNo, "X_AXIS")), CreateHeadKeys(head.HeadNo, "ENCODER_AXIS"))));
                    await _automationManager.ZeroMofFeedbackIfNeeded(targets, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                await _manualMofActions.RunAsync("RUN", () => RunStageTasks(token)).ConfigureAwait(false);
                await _manualMofActions.RunAsync("SCAN", async () =>
                {
                    token.ThrowIfCancellationRequested();
                    var current = await stage!.ReadStationaryPosition(EN_MANUAL_STAGE_AXIS.StageY, token).ConfigureAwait(false);
                    if (Math.Abs(current - start) > 0.001)
                        throw new InvalidOperationException("준비 중 Stage 위치가 변경되었습니다. 다시 실행해 주세요.");
                    using var scan = CancellationTokenSource.CreateLinkedTokenSource(token);
                    // Once accepted, Stage travel belongs to this independent
                    // Process Move transaction. MOF STOP stops Tasks only. The
                    // separate Stage STOP can explicitly cancel the owned move.
                    var acceptedStage = stage;
                    stage = null;
                    var stageTravel = RunManualMofStageMove(acceptedStage, target, speed);
                    _ = ObserveManualMofStageMove(stageTravel);
                    var move = stageTravel.WaitAsync(scan.Token);
                    var machining = WaitStageMachining(scan.Token);
                    var first = await Task.WhenAny(move, machining).ConfigureAwait(false);
                    if (!first.IsCompletedSuccessfully)
                        scan.Cancel();
                    try
                    {
                        await Task.WhenAll(move, machining).ConfigureAwait(false);
                    }
                    catch
                    {
                        scan.Cancel();
                        throw;
                    }
                }).ConfigureAwait(false);
                await _manualMofActions.RunAsync("COMPLETE", () => ReleaseCompletedStageTasks(token)).ConfigureAwait(false);
                success = true;
                message = "MOF 가공 완료";
            }
            catch (Exception exception)
            {
                cancelled = exception is OperationCanceledException && token.IsCancellationRequested;
                message = cancelled ? "MOF 정지" : $"MOF 실패: {exception.Message}";
                // MOF cancellation/failure stops only our Tasks/queues. An accepted Stage
                // move continues; the operator's separate Stage STOP remains available.
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await StopStageTasks(stop.Token).ConfigureAwait(false);
                }
                catch (Exception stopError)
                {
                    message += $" / Task 정지 확인 실패: {stopError.Message}";
                }

                AddProcessLog(cancelled ? "WARN" : "ERROR", "MANUAL_MOF", message);
                if (cancelled)
                    throw new OperationCanceledException(message, exception, token);
                throw new InvalidOperationException(message, exception);
            }
            finally
            {
                stage?.Dispose();
                var result = new ST_PROCESS_RESULT(success, message, DateTimeOffset.Now);
                _snapshot = CreateSnapshot(
                    _processModel, 
                    SetHeadStatus(_snapshot.HeadPreviews, success ? EN_PROCESS_STEP.Completed : cancelled ? EN_PROCESS_STEP.Stopped : EN_PROCESS_STEP.Error), 
                    success ? EN_SCRIPT_STATUS.Completed : EN_SCRIPT_STATUS.Error, 
                    success ? EN_PROCESS_STEP.Completed : cancelled ? EN_PROCESS_STEP.Stopped : EN_PROCESS_STEP.Error, 
                    result
                );
                SetStationState(success ? EN_STATION_STATE.Complete : cancelled ? EN_STATION_STATE.Stopped : EN_STATION_STATE.Alarm, _snapshot.ProcessStep, _snapshot.ScriptStatus, message);
                lock (_sequenceLock)
                {
                    _manualMofRunning = false;
                    _manualMofRequiresStop = !_stageOwnedTasks.IsEmpty || !_stageBufferedRuns.IsEmpty;
                    _manualMofMessage = message;
                    _sequenceState = _manualMofRequiresStop ? EN_STATION_SEQUENCE.Error : EN_STATION_SEQUENCE.Ready;
                }
            }
        }

        private void CheckManualMofInterlocks(ST_PROCESS_MODEL model, IReadOnlySet<int> heads, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var items = GetManualInterlockItems(model.Parameters, heads, token);
            if (items.Count == 0 || items.Any(item => item.Level != EN_INTERLOCK_LEVEL.Ok || item.BlocksAutoRun))
                throw new InvalidOperationException("Interlock/Safety를 확인해 주세요.");
        }

        private async Task CheckManualMofTasksIdle(ST_PROCESS_MODEL model, CancellationToken token)
        {
            var targets = model.HeadPlans.Where(head => head.Shots.Count > 0).ToArray();
            if (targets.GroupBy(head => (head.AutomationNo, head.TaskNo)).Any(group => group.Count() > 1))
                throw new InvalidOperationException("선택한 Head의 Controller / Task 할당이 중복되었습니다.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var ready = true;
                foreach (var head in targets)
                {
                    var sample = _automationManager.StatusSnapshots.GetTask(head.TaskNo, head.AutomationNo);
                    if (sample.State is "ERROR" or "OFFLINE" or "STOPPED")
                        throw new InvalidOperationException($"HEAD {head.HeadNo:00} Task 상태를 확인할 수 없습니다. {sample.Detail}");
                    if (sample.State != "OK" || sample.Value is null)
                    {
                        ready = false;
                        continue;
                    }

                    var task = ParseAutomationTaskStatus(sample.Value);
                    if (task.TaskNo != head.TaskNo || task.State.ToUpperInvariant()is not ("IDLE" or "PROGRAMREADY" or "PROGRAMCOMPLETE" or "PROGRAMSTOPPED" or "QUEUECOMPLETE" or "STOPPED" or "READY"))
                        throw new InvalidOperationException($"HEAD {head.HeadNo:00}의 Task가 실행 가능한 대기 상태가 아닙니다. {task.State}");
                }

                if (ready)
                    return;
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Task 대기 상태 확인 시간이 초과되었습니다.");
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private CStageReviewSequences.Session OpenManualMofStage()
        {
            var row = _interfaceManager.Melsec.Map.SingleOrDefault(item => item.Use && item.Id.Equals("MOTOR_STAGE_Y_CUR_POS", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("MOTOR_STAGE_Y_CUR_POS 맵이 사용 설정되어 있지 않습니다.");
            _interfaceManager.StageReviewSequences.ValidateMoveReadiness(row.DeviceNo);
            return _interfaceManager.StageReviewSequences.Open(row.DeviceNo);
        }

        private static async Task RunManualMofStageMove(CStageReviewSequences.Session stage, double target, double speed)
        {
            using (stage)
            {
                await stage.Move(new(stage.CurrentX, target, speed, speed, Mode: EN_STAGE_MOVE_MODE.StageY),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task ObserveManualMofStageMove(Task move)
        {
            try { await move.ConfigureAwait(false); }
            catch (Exception error)
            {
                // Observe the Stage transaction even if Task-only MOF STOP has
                // returned; never leave a failed continuation unobserved.
                try { AddProcessLog("ERROR", "MANUAL_MOF_STAGE", error.ToString()); }
                catch { System.Diagnostics.Trace.TraceError(error.ToString()); }
            }
        }

        private static double ReadRequiredManualMofNumber(IReadOnlyDictionary<string, string> parameters, string key)
        {
            if (!parameters.TryGetValue(key, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException($"{key} 설정값을 확인해 주세요.");
            return value;
        }

#endregion
#region ProtectWindow
        public void StartProtectWindowCompletionMonitoring(Func<string> selectedRecipe) => _protectWindowUsageRequest?.StartCompletionMonitoring(selectedRecipe);
        public Task StopProtectWindowCompletionMonitoring() => _protectWindowUsageRequest?.StopCompletionMonitoring() ?? Task.CompletedTask;
        private async Task CountCompletedProtectWindowGlass()
        {
            if (_recipeManager is null)
                return;
            await _protectWindowCountGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_protectWindowCounted)
                    return;
                var model = GetProcessModel();
                // All participating Tasks have already completed. Finish the durable commit even if STOP
                // arrives now; neither a later result-save failure nor a Stage ACK changes laser usage.
                var count = await _recipeManager.IncrementProtectWindowCount(model.RecipeId, CancellationToken.None).ConfigureAwait(false);
                _protectWindowCounted = true;
                AddProcessLog("INFO", "PROTECT_WINDOW", $"Recipe={model.RecipeId}, Process={model.ProcessId}, Current Process Count={count}");
            }
            finally
            {
                _protectWindowCountGate.Release();
            }
        }

#endregion
#region StageProcess
        private async Task WaitStageMachining(CancellationToken token, bool publishProtectWindow = false)
        {
            var heads = _stageOwnedTasks.Values.OrderBy(head => head.HeadNo).ToArray();
            if (heads.Length == 0)
                throw new InvalidOperationException("No Tasks owned by the current Process request.");
            var model = GetProcessModel();
            var buffered = IsBufferedRunEnabled(model.Parameters);
            var timeoutMs = buffered ? ReadBufferedRunTimeoutMs(model.Parameters) : ReadScriptRunTimeoutMs(model.Parameters);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (timeoutMs > 0)
                limit.CancelAfter(timeoutMs);
            _snapshot = CreateSnapshot(model, SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Process), EN_SCRIPT_STATUS.Running, EN_PROCESS_STEP.Process, null);
            SetStationState(EN_STATION_STATE.Process, EN_PROCESS_STEP.Process, EN_SCRIPT_STATUS.Running, "Stage Process START acknowledged.");
            // No second RUN. Real Tasks already execute from Process preparation; only simulated controllers get a virtual clock.
            _automationManager.StartStageMachiningSimulation(heads.Select(head => new ST_STAGE_TASK_SIMULATION_TARGET(head.HeadNo, head.AutomationNo, head.TaskNo)).ToArray(), limit.Token);
            var completed = new HashSet<(int Controller, int Task)>();
            try
            {
                while (completed.Count < heads.Length)
                {
                    limit.Token.ThrowIfCancellationRequested();
                    CheckStageBufferedRuns();
                    foreach (var head in heads)
                    {
                        var key = (head.AutomationNo, head.TaskNo);
                        if (completed.Contains(key))
                            continue;
                        var sample = _automationManager.StatusSnapshots.GetTask(head.TaskNo, head.AutomationNo);
                        if (sample.State is "ERROR" or "OFFLINE" or "STOPPED")
                            throw new InvalidOperationException($"Task feedback unavailable: H{head.HeadNo:00}. {sample.Detail}");
                        if (sample.State != "OK" || sample.Value is null)
                            continue;
                        var status = ParseAutomationTaskStatus(sample.Value);
                        if (status.TaskNo != head.TaskNo)
                            throw new InvalidOperationException("Task feedback identity mismatch.");
                        if (IsAutomationTaskFailed(status.State) || status.State.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Task did not finish normally: H{head.HeadNo:00}. {sample.Value}");
                        // An ordinary Idle is never sufficient evidence of a successful program end.
                        var done = status.State.Equals("ProgramComplete", StringComparison.OrdinalIgnoreCase);
                        if (buffered)
                        {
                            var groupComplete = _stageBufferedRuns.TryGetValue(head.AutomationNo, out var group) && group.Completion.IsCompletedSuccessfully;
                            done = _automationManager.IsSimul(head.AutomationNo) ? groupComplete && status.State.Equals("QueueComplete", StringComparison.OrdinalIgnoreCase) : groupComplete; // Includes the communication owner's drain + queue cleanup acknowledgement.
                        }

                        if (!done)
                            continue;
                        completed.Add(key);
                        AddProcessLog("INFO", "STAGE_PROCESS", $"H{head.HeadNo:00} normal completion. A{head.AutomationNo}/T{head.TaskNo}, State={status.State}, Simulation={_automationManager.IsSimul(head.AutomationNo)}");
                    }

                    if (completed.Count < heads.Length)
                        await Task.Delay(50, limit.Token).ConfigureAwait(false);
                }

                _stageMachiningCompleted = true;
                _scriptCompletedAt = DateTimeOffset.Now;
                await CountCompletedProtectWindowGlass().ConfigureAwait(false);
                // Persist actual usage even if the later MELSEC completion/readback fails.
                if (publishProtectWindow)
                    await StageProcessData().PublishMachiningComplete(token).ConfigureAwait(false);
                if (publishProtectWindow)
                    await UpdateProtectWindowUsageRequest(token).ConfigureAwait(false);
                _snapshot = CreateSnapshot(model, SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Completed), EN_SCRIPT_STATUS.Completed, EN_PROCESS_STEP.Process, null);
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested && limit.IsCancellationRequested)
            {
                throw new TimeoutException($"Process machining timeout. TimeoutMs={timeoutMs}.");
            }
        }

        private Task SaveStageProcessResults(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!_stageMachiningCompleted)
                throw new InvalidOperationException("Machining has not completed normally.");
            var model = GetProcessModel();
            var now = DateTimeOffset.Now;
            var heads = _stageOwnedTasks.Values.OrderBy(head => head.HeadNo).ToArray();
            var directory = Path.Combine(_logManager?.LogDirectory ?? Path.Combine(AppContext.BaseDirectory, "Log"), "Station", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"ProcessResult_{now:yyyyMMdd_HHmmss_fffffff}.csv");
            static string Field(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var lines = new List<string>
            {
                "Process ID,Recipe ID,Glass ID,Head,Automation,Task,Simulation,Result,Completed At"
            };
            lines.AddRange(heads.Select(head => string.Join(",", new[] { model.ProcessId, model.RecipeId, model.GlassId, $"HEAD {head.HeadNo:00}", head.AutomationNo.ToString(CultureInfo.InvariantCulture), head.TaskNo.ToString(CultureInfo.InvariantCulture), _automationManager.IsSimul(head.AutomationNo) ? "SIMULATION" : "ONLINE", "OK", now.ToString("O", CultureInfo.InvariantCulture) }.Select(Field))));
            System.IO.File.WriteAllLines(path, lines, new UTF8Encoding(true));
            token.ThrowIfCancellationRequested();
            var simulation = heads.Any(head => _automationManager.IsSimul(head.AutomationNo));
            var result = new ST_PROCESS_RESULT(true, simulation ? "Stage Process completed (includes simulated Tasks)." : "Stage Process completed.", now, simulation ? "SIMULATION" : "OK");
            CompleteProduct(result, true, token);
            ReportProcessResult(result, "COMPLETE", token, EN_STATION_STATE.Complete);
            _snapshot = CreateSnapshot(model, SetHeadStatus(_snapshot.HeadPreviews, EN_PROCESS_STEP.Completed), EN_SCRIPT_STATUS.Completed, EN_PROCESS_STEP.Completed, result);
            AddProcessLog("INFO", "STAGE_PROCESS", $"Process result saved: {path}");
        }, token);
        private async Task ReleaseCompletedStageTasks(CancellationToken token)
        {
            if (!_stageMachiningCompleted)
                throw new InvalidOperationException("Cannot release unfinished Process Tasks.");
            foreach (var pair in _stageBufferedRuns.ToArray())
            {
                await pair.Value.Completion.WaitAsync(token).ConfigureAwait(false);
                if (_stageBufferedRuns.TryRemove(pair.Key, out _))
                    pair.Value.Stop.Dispose();
            }

            _stageOwnedTasks.Clear();
        // Preserve DONE feedback. Do not issue STOP, rerun Tasks or change laser-device state on normal completion.
        }

#endregion
#region StageRequests
        public CStageRequestSequence StageRequests { get; }

        private CStageRequestSequence CreateStageRequestSequence() => new(
            () =>
        {
            var melsec = _interfaceManager.Melsec;
            var cache = melsec.GetCachedSnapshot(melsec.Map.Where(row => row.Id.StartsWith("HANDSHAKE_", StringComparison.OrdinalIgnoreCase)).Select(row => row.Id), out var map);
            var required = new[]
            {
                CStageRequestSequence.StgAble,
                CStageRequestSequence.StgStart,
                CStageRequestSequence.StgComplete,
                CStageRequestSequence.PrcAble,
                CStageRequestSequence.PrcStart,
                CStageRequestSequence.PrcComplete
            };
            var rows = required.Select(id => map.FirstOrDefault(row => row.Use && row.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
            var devices = _interfaceManager.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec);
            var connected = rows.All(row => row is not null && devices.Any(device => device.Number == row.DeviceNo && (device.IsSimulation || device.ConnectionState == EN_COMM_STATE.Online)));
            return new(connected, cache);
        }, 
            (id, value, token) => Task.Run(() => _interfaceManager.Melsec.WriteWord(id, value, token), token), 
            _sequenceActions, 
            message => AddProcessLog(message.Contains("FAILED") || message.Contains(" ERROR:") ? "ERROR" : "INFO", "STAGE_SEQUENCE", message), 
            StopStageTasks, 
            CheckStageBufferedRuns, 
            VerifyStageReadyPosition, 
            token => WaitStageMachining(token, publishProtectWindow: true), 
            SaveStageProcessResults, 
            ReleaseCompletedStageTasks, 
            UpdateStageProcessOptions, 
            ZeroLineActions(), 
            DescribeSequenceSignal
        );
        private string DescribeSequenceSignal(string id)
        {
            var row = _interfaceManager.Melsec.Map.FirstOrDefault(item => item.Use && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return row is null ? id : FormattableString.Invariant($"{id} [MELSEC_{row.DeviceNo}; Address={row.Address}; {row.Direction}; {row.DataType}; ReadScale={row.ReadScale}; WriteScale={row.WriteScale}]");
        }

        private CStageProcessDataPublisher StageProcessData() => new(_interfaceManager, message => AddProcessLog("INFO", "STAGE_DATA", message));
        private double StageProcessSpeed() => ReadDoubleAny(GetProcessModel().Parameters, double.NaN, "STAGE_SPEED");
        private async Task UpdateStageProcessOptions(CancellationToken token)
        {
            await StageProcessData().PublishSpeed(StageProcessSpeed(), token).ConfigureAwait(false);
            await UpdateProtectWindowUsageRequest(token).ConfigureAwait(false);
        }

        private Task UpdateProtectWindowUsageRequest(CancellationToken token)
        {
            var model = GetProcessModel();
            return _protectWindowUsageRequest?.Update(model.RecipeId, token, model) ?? Task.CompletedTask;
        }

        private async Task VerifyStageReadyPosition(CancellationToken token)
        {
            var check = new CStageReadyPositionCheck(_interfaceManager, _settingManager);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                CheckStageBufferedRuns();
                var result = check.Evaluate();
                if (result.IsReady)
                {
                    await StageProcessData().PublishSpeed(StageProcessSpeed(), token).ConfigureAwait(false);
                    AddProcessLog("INFO", "STAGE_READY", result.Detail);
                    return;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        public void StartStageRequests(Func<CancellationToken, Task<ST_STAGE_PROCESS_REQUEST>> request, ST_STAGE_REVIEW_ACTIONS? review = null, ST_STAGE_TPM_ACTIONS? tpm = null)
        {
            lock (_sequenceLock)
            {
                if (_manualMofRunning || _manualMofRequiresStop)
                    throw new InvalidOperationException("Manual MOF is active or requires STOP.");
                if (_interfaceManager.StageReviewSequences.IsActive)
                    throw new InvalidOperationException("Stage/Review motion or cleanup must finish before AUTO request monitoring.");
                if (SequenceState != EN_STATION_SEQUENCE.Ready)
                    throw new InvalidOperationException("Station must be Ready before AUTO request monitoring.");
                if (!_stageOwnedTasks.IsEmpty || !_stageBufferedRuns.IsEmpty)
                    throw new InvalidOperationException("Previous AUTO Tasks have not been confirmed stopped.");
                StageRequests.Start(
                    async token =>
                {
                    var data = await request(token).ConfigureAwait(false);
                    var parameters = new Dictionary<string, string>(data.Parameters, StringComparer.OrdinalIgnoreCase);
                    PrepareProcess(data.ProcessId, data.RecipeId, data.GlassId, data.LotId, parameters, token, preserveSequenceActions: true);
                    await StageProcessData().PrepareDoReady(StageProcessSpeed(), token).ConfigureAwait(false);
                    await CaptureZeroLineCycle(data, token).ConfigureAwait(false);
                }, 
                    token => Task.Run(() =>
                {
                    var model = GetProcessModel();
                    BuildAutomationScript(model, token);
                    UploadAutomationScript(model, token);
                    token.ThrowIfCancellationRequested();
                // DoReady uploads only. MOF zero and Task RUN execute after DoReady signal reset,
                // in Process preparation, once Stage Ready position and Busy OFF are verified.
                }, token), 
                    token =>
                {
                    var model = GetProcessModel();
                    var targets = model.HeadPlans.Select(head => new ST_MOF_ZERO_TARGET(head.AutomationNo, head.TaskNo, ReadTextAny(model.Parameters, ReadTextAny(model.Parameters, "GX", CreateHeadKeys(head.HeadNo, "X_AXIS")), CreateHeadKeys(head.HeadNo, "ENCODER_AXIS"))));
                    var frozen = targets.ToArray();
                    AddProcessLog("INFO", "MOF_ZERO", $"Auxiliary zero check requested. Targets={string.Join(";", frozen.Select(target => target.ToString()))}");
                    return _automationManager.ZeroMofFeedbackIfNeeded(frozen, token);
                }, 
                    RunStageTasks, 
                    review, tpm,
                    token => Task.Run(() =>
                    {
                        var model = GetProcessModel();
                        var mode = IsBufferedRunEnabled(model.Parameters) ? BufferedRunScriptDirectoryName : StandardScriptDirectoryName;
                        _lastScript = _automationScriptFile.LoadLatest(model, mode, token);
                        _scriptCreatedAt = _lastScript.CreatedAt;
                        AddProcessLog("INFO", "SCRIPT", $"Process 단독 실행 · 마지막 {mode} 스크립트: {_lastScript.FilePath} / {_lastScript.HeadScripts.Count} Head / {_lastScript.TotalShots} Shot");
                        UploadAutomationScript(model, token);
                    }, token)
                );
            }
        }

        private async Task RunStageTasks(CancellationToken token)
        {
            _stageMachiningCompleted = false;
            if (_lastScript is null)
                throw new InvalidOperationException("No prepared scripts.");
            var heads = _lastScript.HeadScripts.Where(head => head.TotalShots > 0).ToArray();
            if (heads.Length == 0)
                throw new InvalidOperationException("No USE head machining scripts.");
            AddProcessLog("INFO", "STAGE_PROCESS", $"가공 대상: {heads.Length} Head / {heads.Sum(head => (long)head.TotalShots):N0} Shot");
            if (heads.GroupBy(head => (head.AutomationNo, head.TaskNo)).Any(group => group.Count() != 1))
                throw new InvalidOperationException("USE heads must have distinct controller / Task assignments.");
            var readyRunId = _lastScript.MofReadyRunId;
            if (readyRunId <= 0)
                throw new InvalidOperationException("Prepared scripts have no MOF readiness run ID. Rebuild the scripts before RUN.");
            var buffered = IsBufferedRunEnabled(GetProcessModel().Parameters);
            // Reset every controller-local slot before starting any queue. A write must never wait behind a running queue.
            foreach (var head in heads)
            {
                AddProcessLog("INFO", "MOF_READY", $"Readiness slot reset requested. H{head.HeadNo:00}; Automation={head.AutomationNo}; Task={head.TaskNo}; RunId={readyRunId}");
                await _automationManager.PrepareMofReadiness(head.TaskNo, head.AutomationNo, readyRunId, token).ConfigureAwait(false);
                AddProcessLog("INFO", "MOF_READY", $"Readiness slot reset complete. H{head.HeadNo:00}; Automation={head.AutomationNo}; Task={head.TaskNo}; RunId={readyRunId}");
            }

            foreach (var head in heads)
            {
                token.ThrowIfCancellationRequested();
                // Register before issuing: a lost acknowledgement can follow an accepted RUN.
                _stageOwnedTasks[(head.AutomationNo, head.TaskNo)] = head;
                _automationManager.StatusSnapshots.InvalidateTask(head.TaskNo, head.AutomationNo);
                if (!buffered)
                {
                    AddProcessLog("INFO", "A1_RUN", $"Task RUN requested. H{head.HeadNo:00}; Automation={head.AutomationNo}; Task={head.TaskNo}; File={head.FileName}; RunId={readyRunId}");
                    await _automationManager.RunStagePreparedScript(head.FileName, head.TaskNo, head.AutomationNo, token).ConfigureAwait(false);
                    AddProcessLog("INFO", "A1_RUN", $"Task RUN command accepted. H{head.HeadNo:00}; Automation={head.AutomationNo}; Task={head.TaskNo}; awaiting encoder-wait readiness.");
                }
            }

            if (buffered)
            {
                var parameters = GetProcessModel().Parameters;
                foreach (var group in heads.GroupBy(head => head.AutomationNo))
                {
                    token.ThrowIfCancellationRequested();
                    var controller = group.Key;
                    var scripts = group.Select(head => new ST_BUFFERED_SCRIPT_RUN_ITEM(head.FilePath, head.FileName, head.TaskNo)).ToArray();
                    // This lifetime must outlive Process preparation's RUN-verification action token.
                    // STOP owns cancellation and retains the session until its queue worker finishes.
                    var stop = new CancellationTokenSource();
                    var run = new CStageBufferedRun(stop);
                    if (!_stageBufferedRuns.TryAdd(controller, run))
                    {
                        stop.Dispose();
                        throw new InvalidOperationException($"Buffered group already exists: Automation1 #{controller}.");
                    }

                    run.Completion = Task.Run(() => _automationManager.RunStagePreparedBufferedScripts(scripts, controller, ReadBufferedRunQueueSize(parameters), ReadBufferedRunLinesPerCommand(parameters), ReadBufferedRunTimeoutMs(parameters), stop.Token));
                    AddProcessLog("INFO", "A1_RUN", $"Stage buffered group started. Automation1 #{controller}, Tasks={string.Join(",", scripts.Select(script => script.TaskIndex))}");
                }
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (limit.IsCancellationRequested && !token.IsCancellationRequested)
                    throw new TimeoutException("MOF 준비 완료 확인 시간이 초과되었습니다. Task 실행 상태와 이번 실행번호가 15초 이내에 확인되지 않았습니다.");
                limit.Token.ThrowIfCancellationRequested();
                CheckStageBufferedRuns();
                var allRunning = true;
                foreach (var head in heads)
                {
                    var ready = _automationManager.StatusSnapshots.GetMofReadiness(head.TaskNo, head.AutomationNo);
                    if (ready.State is "ERROR" or "OFFLINE" or "STOPPED")
                        throw new InvalidOperationException($"MOF readiness unavailable: H{head.HeadNo:00}. {ready.Detail}");
                    var sample = _automationManager.StatusSnapshots.GetTask(head.TaskNo, head.AutomationNo);
                    if (sample.State is "ERROR" or "OFFLINE" or "STOPPED")
                        throw new InvalidOperationException($"Task status unavailable: H{head.HeadNo:00}. {sample.Detail}");
                    if (sample.State != "OK" || sample.Value is null)
                    {
                        allRunning = false;
                        continue;
                    }

                    var status = ParseAutomationTaskStatus(sample.Value);
                    if (IsAutomationTaskFailed(status.State))
                        throw new InvalidOperationException($"Task failed: H{head.HeadNo:00}. {sample.Value}");
                    // Command queues may report QueueRunning rather than ProgramRunning.
                    // A ProgramRunning sample must include this queue's status, not a previous ordinary program.
                    var running = buffered ? status.State.Equals("QueueRunning", StringComparison.OrdinalIgnoreCase) || (status.State.Equals("ProgramRunning", StringComparison.OrdinalIgnoreCase) && status.ErrorMessage.StartsWith("QueueEmpty=", StringComparison.Ordinal)) : status.State.Equals("ProgramRunning", StringComparison.OrdinalIgnoreCase);
                    allRunning &= status.TaskNo == head.TaskNo && running && ready.State == "OK" && ready.Value?.RunId == readyRunId;
                }

                if (allRunning)
                {
                    AddProcessLog("INFO", "MOF_READY", $"All participating Tasks initialized for RunId={readyRunId}. Heads={string.Join(",", heads.Select(head => head.HeadNo))}");
                    return;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private async Task StopStageTasks(CancellationToken token)
        {
            // End command queues before issuing ordinary task/laser commands on the same controller.
            foreach (var run in _stageBufferedRuns.Values)
                run.Stop.Cancel();
            foreach (var pair in _stageBufferedRuns.ToArray())
            {
                var run = pair.Value;
                try
                {
                    await run.Completion.WaitAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception)when (run.Completion.IsCompleted)
                {
                    // Execution failure is already exposed by CheckStageBufferedRuns; cleanup still stops all owned Tasks.
                    if (exception is not OperationCanceledException)
                        AddProcessLog("WARN", "A1_RUN", $"Buffered group ended with an error. Automation1 #{pair.Key}: {exception.Message}");
                }
                finally
                {
                    if (run.Completion.IsCompleted && _stageBufferedRuns.TryRemove(pair.Key, out _))
                        run.Stop.Dispose();
                }
            }

            var errors = new List<Exception>();
            foreach (var pair in _stageOwnedTasks.ToArray())
            {
                try
                {
                    AddProcessLog("WARN", "A1_RUN", $"Task STOP requested. H{pair.Value.HeadNo:00}; Automation={pair.Key.Controller}; Task={pair.Key.Task}");
                    await _automationManager.StopStagePreparedTask(pair.Key.Task, pair.Key.Controller, token).ConfigureAwait(false);
                    AddProcessLog("INFO", "A1_RUN", $"Task STOP confirmed. H{pair.Value.HeadNo:00}; Automation={pair.Key.Controller}; Task={pair.Key.Task}");
                    var model = GetProcessModel();
                    var x = ReadTextAny(model.Parameters, "GX", CreateHeadKeys(pair.Value.HeadNo, "X_AXIS"));
                    var laser = ReadTextAny(model.Parameters, x, CreateHeadKeys(pair.Value.HeadNo, "LASER_AXIS"));
                    await _automationManager.SetGalvoLaserOutput(laser, false, pair.Key.Task, pair.Key.Controller, token).ConfigureAwait(false);
                    AddProcessLog("INFO", "A1_RUN", $"Stopped Task Galvo laser OFF command completed. H{pair.Value.HeadNo:00}; Automation={pair.Key.Controller}; Task={pair.Key.Task}; Axis={laser}");
                    _stageOwnedTasks.TryRemove(pair.Key, out _);
                }
                catch (Exception exception)
                {
                    AddProcessLog("ERROR", "A1_RUN", $"Task STOP cleanup failed. H{pair.Value.HeadNo:00}; Automation={pair.Key.Controller}; Task={pair.Key.Task}; {exception}");
                    errors.Add(exception);
                }
            }

            if (errors.Count != 0)
                throw new AggregateException("AUTO Task stop failed.", errors);
        }

        private void CheckStageBufferedRuns()
        {
            foreach (var pair in _stageBufferedRuns)
            {
                if (!pair.Value.Completion.IsCompleted)
                    continue;
                try
                {
                    var response = pair.Value.Completion.GetAwaiter().GetResult();
                    if (!IsProcessControlSuccessResponse(response) && !(response.StartsWith("SIM:", StringComparison.OrdinalIgnoreCase) && response.EndsWith(":OK", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(response);
                }
                catch (Exception exception)
                {
                    var error = new InvalidOperationException($"Buffered run failed. Automation1 #{pair.Key}: {exception.Message}", exception);
                    var action = _sequenceActions.GetSnapshot().GetValueOrDefault(CSequenceActionIds.ProcessMachining)?.State == EN_SEQUENCE_ACTION_STATE.Running ? CSequenceActionIds.ProcessMachining : CSequenceActionIds.ProcessTaskRun;
                    if (!_manualMofRunning)
                        _sequenceActions.SetError(action, error.Message);
                    throw error;
                }
            }
        }

        private sealed class CStageBufferedRun(CancellationTokenSource stop)
        {
            public CancellationTokenSource Stop { get; } = stop;
            public Task<string> Completion { get; set; } = Task.FromResult("OK:STARTING");
        }

#endregion
#region ZeroLine
        private ST_STAGE_ZERO_LINE_ACTIONS ZeroLineActions() => new(
            () => _zeroLineCycle is not null, 
            token => _zeroLineCycle?.Prepare(token) ?? Task.CompletedTask, 
            token => _zeroLineCycle?.Measure(token) ?? Task.CompletedTask, 
            token => _zeroLineCycle?.Save(token) ?? Task.CompletedTask, 
            () => _zeroLineCycle?.Release() ?? Task.CompletedTask
        )
        {
            ReadResults = token => _zeroLineCycle?.ReadResults(token) ?? Task.CompletedTask
        };
        private async Task CaptureZeroLineCycle(ST_STAGE_PROCESS_REQUEST request, CancellationToken token)
        {
            if (_zeroLineCycle is not null)
                await _zeroLineCycle.Release().ConfigureAwait(false);
            _zeroLineCycle = null;
            _sequenceActions.ResetToWaiting([CSequenceActionIds.ProcessZeroPrepare, CSequenceActionIds.ProcessZeroMeasure, CSequenceActionIds.ProcessZeroResults]);
            // A newly accepted glass owns this result flag. In particular, OFF must not
            // inherit the preceding glass's NG follow-up request.
            if (_interfaceManager.Melsec.Map.Any(row => row.Use && row.Id == "SDC_REVIEW_RETRY_REQUEST"))
                await CZeroLineProcessCoordinator.PublishFollowUp(_interfaceManager, false, token).ConfigureAwait(false);
            var recipe = new ST_RECIPE_DEFINITION(request.RecipeId, request.RecipeId, request.Parameters.Select(p => new ST_RECIPE_PARAMETER(p.Key, p.Value, "", "", p.Value, Key: p.Key)).ToArray(), []);
            var parameters = _settingManager.LoadSection(EN_SETTING_TAB.Option).Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor)).ToArray();
            var plan = CZeroLineCyclePlan.Build(recipe, request.GlassId, parameters);
            if (plan is null)
            {
                _zeroLinePrewait?.DiscardCompleted();
                if (!_interfaceManager.StageReviewSequences.IsActive)
                    _interfaceManager.StageReviewSequences.ClearZeroLineSimulation();
                return;
            }

            if (CRunOptionSettings.Read(parameters).CurrentGroup != plan.Group)
                _settingManager.SetValue(EN_SETTING_TAB.Option, CRunOptionSettings.CurrentGroup, plan.Group.ToString(CultureInfo.InvariantCulture), token);
            if (_zeroLineReview is null || _zeroLinePrewait is null)
                throw new InvalidOperationException("0선방어 Review/선대기 서비스가 시작되지 않았습니다.");
            _zeroLineCycle = new(plan, _interfaceManager, _zeroLineReview, _settingManager, _zeroLinePrewait.WaitForFirstX, message => AddProcessLog("INFO", "ZERO_LINE", message));
            AddProcessLog("INFO", "ZERO_LINE", $"Plan frozen: Glass={request.GlassId}, Group={plan.Group}/{plan.TotalGroups}, N={plan.Points.Count}, FollowUp={plan.FollowUp}");
            token.ThrowIfCancellationRequested();
        }

#endregion
#region ZeroLinePrewait
        public void StartZeroLinePrewaitMonitoring(Func<string> selectedRecipe)
        {
            if (_recipeManager is null)
                return;
            _zeroLinePrewait ??= new(_interfaceManager, _recipeManager, _settingManager,
                () => StageRequests.IsRunning && StageRequests.Status.State is not ("STOPPING" or "ERROR" or "STOPPED"),
                selectedRecipe, message => AddProcessLog("INFO", "ZERO_LINE_PREWAIT", message));
            _zeroLinePrewait.Start();
        }

        public Task StopZeroLinePrewaitMonitoring() => _zeroLinePrewait?.Stop() ?? Task.CompletedTask;
#endregion
    }

    /// <summary>Reset only processing-PC handshake outputs. Data and Stage inputs are never targets.</summary>
    public static class CHandshakeOutputReset
    {
        public static bool IsTarget(ST_MELSEC_MAP_DATA row) => row.Use && row.Direction == EN_MELSEC_DIRECTION.Out && row.Access == EN_MELSEC_ACCESS.ReadWrite && row.Id.StartsWith("HANDSHAKE_PRC_", StringComparison.OrdinalIgnoreCase);
        public static async Task Reset(CInterfaceManager interfaces, CancellationToken token = default)
        {
            var failures = new List<string>();
            var targets = interfaces.Melsec.Map.Where(IsTarget).ToArray();
            if (targets.Length == 0)
                throw new InvalidOperationException("초기화할 활성 Handshake 출력 맵이 없습니다.");
            foreach (var row in targets)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var device = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).SingleOrDefault(d => d.Number == row.DeviceNo);
                    if (device is null || !device.IsSimulation && device.ConnectionState != EN_COMM_STATE.Online)
                        throw new IOException($"MELSEC_{row.DeviceNo} OFFLINE");
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(TimeSpan.FromSeconds(5));
                    await Task.Run(() => interfaces.Melsec.WriteWord(row.Id, 0, limit.Token), limit.Token).ConfigureAwait(false);
                    var value = await interfaces.Melsec.RefreshCachedValueAsync(row.Id, limit.Token).ConfigureAwait(false);
                    if (!IsOff(value))
                        throw new IOException("OFF 읽기 확인 실패");
                }
                catch (Exception error)when (!token.IsCancellationRequested)
                {
                    failures.Add($"{row.Id}: {error.Message}");
                }
            }

            if (failures.Count > 0)
                throw new IOException(string.Join(Environment.NewLine, failures));
        }

        private static bool IsOff(ST_MELSEC_CACHED_VALUE value) => value.IsValid && value.Value is not null && Convert.ToDouble(value.Value, CultureInfo.InvariantCulture) == 0;
    }

    public sealed partial class CProtectWindowUsageRequest(CRecipeManager recipes, CInterfaceManager interfaces, Action<string>? log = null)
    {
#region 공유 상태·초기값
        public const string CompleteId = "PTW_EXCHANGE_SEQ_COMPLETE";
        public const string HandoverCountId = "PTW_TRAY_COUNT";
        private readonly object _monitorLock = new();
        private CancellationTokenSource? _monitorStop;
        private Task _monitor = Task.CompletedTask;
        private string? _pendingRecipeId;
        private object? _pendingCycle;
        private object? _currentCycle;
        private object? _acknowledgedCycle;
        private bool _completionArmed;
        private int? _committedHandoverCount;
        private bool? _lastRequest;
        private bool? _lastCompletion;
        private string? _lastSignalTrace;
        public string MonitoringError { get; private set; } = "";

        public const string CountId = "PROCESS_SEQUENCE_PTW_PROCESS_COUNT";
        public const string RequestId = "PROCESS_SEQUENCE_TPD_PTW_EXCHANGE_FLAG";
        private readonly SemaphoreSlim _gate = new(1, 1);
#endregion
#region 기본 처리
        public async Task Update(string recipeId, CancellationToken token = default, object? cycle = null)
        {
            token.ThrowIfCancellationRequested();
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // LoadRecipes returns the shared in-memory recipe snapshot once initialized.
                var recipe = (await recipes.LoadRecipes(token).ConfigureAwait(false)).SingleOrDefault(item => item.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"Protect Window recipe not found: {recipeId}");
                int Count(string key) => CProtectWindowRecipeData.ParseCount(recipe.Parameters.LastOrDefault(item => item.Use && item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value, key);
                var current = Count(CProtectWindowRecipeData.CurrentCountKey);
                var maximum = Count(CProtectWindowRecipeData.MaximumCountKey);
                WriteLog($"USAGE_CHECK Recipe={recipeId}; Current={current}; Maximum={maximum}; ThresholdReached={current >= maximum}; AlreadyAcknowledged={cycle is not null && ReferenceEquals(cycle, _acknowledgedCycle)}");
                await PublishIfChanged(CountId, current, token).ConfigureAwait(false);
                _currentCycle = cycle;
                // Max=0 is immediate, once per glass cycle. A completed exchange must not reassert
                // from the later COMPLETE-boundary update of that same glass.
                if (current >= maximum && !(cycle is not null && ReferenceEquals(cycle, _acknowledgedCycle)))
                {
                    var values = interfaces.Melsec.GetCachedSnapshot();
                    if (!Matches(values.GetValueOrDefault(RequestId), 1) || _pendingRecipeId is null)
                    {
                        _pendingRecipeId = recipeId;
                        _pendingCycle = cycle;
                        _committedHandoverCount = null;
                        _completionArmed = Matches(values.GetValueOrDefault(CompleteId), 0) || (_lastRequest is null && Matches(values.GetValueOrDefault(RequestId), 1));
                    }

                    WriteLog($"EXCHANGE_REQUEST Recipe={recipeId}; Current={current}; Maximum={maximum}; CompletionArmed={_completionArmed}");
                    await PublishIfChanged(RequestId, 1, token).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task PublishIfChanged(string id, int expected, CancellationToken token)
        {
            var values = interfaces.Melsec.GetCachedSnapshot([id], out var map);
            EnsureConnectedRoute(id, map);
            if (Matches(values.GetValueOrDefault(id), expected))
            {
                WriteLog($"OUTPUT_UNCHANGED {id}={expected}; no write required.");
                return;
            }

            var route = map.First(item => item.Use && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var elapsed = Stopwatch.StartNew();
            WriteLog(FormattableString.Invariant($"OUTPUT_REQUEST {id}; MELSEC_{route.DeviceNo}; Address={route.Address}; Logical={expected}; ReadScale={route.ReadScale}; WriteScale={route.WriteScale}"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                // WriteWord forwards the logical value and token to the single communication owner.
                // WRITE_SCALE/READ_SCALE are applied there, exactly once.
                // A polling sample can arrive while the write is still returning.
                var writeStartedAt = DateTimeOffset.UtcNow;
                await Task.Run(() => interfaces.Melsec.WriteWord(id, expected, timeout.Token), timeout.Token).ConfigureAwait(false);
                WriteLog($"OUTPUT_SENT {id}={expected}; ElapsedMs={elapsed.ElapsedMilliseconds}");
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    values = interfaces.Melsec.GetCachedSnapshot([id], out map);
                    EnsureConnectedRoute(id, map);
                    var sample = values.GetValueOrDefault(id);
                    if (Matches(sample, expected) && sample!.UpdatedAt >= writeStartedAt)
                    {
                        WriteLog($"{id}={expected} confirmed from the shared snapshot. ElapsedMs={elapsed.ElapsedMilliseconds}; SampleAt={sample.UpdatedAt:O}");
                        return;
                    }

                    await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                }

                throw new TimeoutException($"Protect Window readback timeout: {id}={expected}");
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                WriteLog($"OUTPUT_FAILED {id}={expected}; Timeout; ElapsedMs={elapsed.ElapsedMilliseconds}");
                throw new TimeoutException($"Protect Window write/readback timeout: {id}={expected}");
            }
            catch (Exception error)
            {
                WriteLog($"OUTPUT_FAILED {id}={expected}; ElapsedMs={elapsed.ElapsedMilliseconds}; {error}");
                throw;
            }
        }

        private void WriteLog(string message)
        {
            try
            {
                log?.Invoke(message);
            }
            catch (Exception error)
            {
                // Logging is diagnostic; even a logging callback cancellation must not abort confirmed control.
                try
                {
                    Trace.TraceError($"Protect Window log callback failed: {error}");
                }
                catch
                { /* A failing trace listener must not interrupt the exchange either. */
                }
            }
        }

        private void EnsureConnectedRoute(string id, IReadOnlyList<ST_MELSEC_MAP_DATA> map)
        {
            var row = map.FirstOrDefault(item => item.Use && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is null || row.Address == "UNASSIGNED" || row.Access != EN_MELSEC_ACCESS.ReadWrite || row.Direction != EN_MELSEC_DIRECTION.Out)
                throw new InvalidOperationException($"Protect Window read/write route is unavailable: {id}");
            if (!interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).Any(device => device.Number == row.DeviceNo && (device.IsSimulation || device.ConnectionState == EN_COMM_STATE.Online)))
                throw new InvalidOperationException($"MELSEC offline for Protect Window: {id}");
        }

        private static bool Matches(ST_MELSEC_CACHED_VALUE? sample, int expected)
        {
            if (sample?.IsValid != true || sample.Value is null)
                return false;
            try
            {
                return Math.Abs(Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) - expected) < 1e-8;
            }
            catch (Exception error)when (error is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

#endregion
#region Completion
        public void StartCompletionMonitoring(Func<string> selectedRecipe)
        {
            lock (_monitorLock)
            {
                if (!_monitor.IsCompleted)
                    return;
                _monitorStop?.Dispose();
                _monitorStop = new();
                var token = _monitorStop.Token;
                _monitor = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    do
                    {
                        try
                        {
                            await CheckCompletion(selectedRecipe, token).ConfigureAwait(false);
                            MonitoringError = "";
                        }
                        catch (OperationCanceledException)when (token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception error)
                        {
                            if (MonitoringError != error.Message)
                            {
                                System.Diagnostics.Trace.TraceError($"Protect Window completion check failed: {error}");
                                WriteLog($"ERROR: Protect Window completion acknowledgement failed: {error.Message}");
                            }

                            MonitoringError = error.Message;
                        }
                    }
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
                }, token);
            }
        }

        public async Task StopCompletionMonitoring()
        {
            Task monitor;
            CancellationTokenSource? stop;
            lock (_monitorLock)
            {
                stop = _monitorStop;
                stop?.Cancel();
                monitor = _monitor;
            }

            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_monitorLock)
                {
                    // Start may already have disposed this source and installed a new monitor.
                    if (ReferenceEquals(_monitorStop, stop))
                    {
                        _monitorStop = null;
                        stop?.Dispose();
                    }
                }
            }
        }

        /// <summary>Checks request/completion snapshots. Reads Stage's count only for an armed completion.</summary>
        public async Task CheckCompletion(Func<string> selectedRecipe, CancellationToken token = default)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var values = interfaces.Melsec.GetCachedSnapshot([RequestId, CompleteId, CountId, HandoverCountId], out var map);
                bool? Signal(string id) => Matches(values.GetValueOrDefault(id), 0) ? false : Matches(values.GetValueOrDefault(id), 1) ? true : null;
                var request = Signal(RequestId);
                var complete = Signal(CompleteId);
                var completionRow = map.FirstOrDefault(row => row.Use && row.Id == CompleteId && row.Direction == Drilling.Common.Interface.EN_MELSEC_DIRECTION.In);
                if (completionRow is null || !interfaces.GetInterfaceCommunicationList(Drilling.Common.Interface.EN_EQP_MODULE.Melsec).Any(device => device.Number == completionRow.DeviceNo && (device.IsSimulation || device.ConnectionState == Drilling.Common.Interface.EN_COMM_STATE.Online)))
                    complete = null;
                var signalTrace = $"Request={request?.ToString() ?? "INVALID"}; Complete={complete?.ToString() ?? "INVALID"}";
                if (_lastSignalTrace != signalTrace)
                {
                    _lastSignalTrace = signalTrace;
                    var requestRoute = map.FirstOrDefault(row => row.Use && row.Id == RequestId);
                    WriteLog($"EXCHANGE_SIGNALS {signalTrace}; RequestAddress={requestRoute?.Address}; CompleteAddress={completionRow?.Address}; Recipe={_pendingRecipeId}");
                }

                if (request is null)
                    return;
                if (request == false)
                {
                    ClearPending();
                    _lastRequest = false;
                    if (complete is not null)
                        _lastCompletion = complete;
                    return;
                }

                if (_pendingRecipeId is null)
                {
                    // Also handle requests issued from Monitor or left ON across restart.
                    _pendingRecipeId = selectedRecipe();
                    _pendingCycle = _currentCycle;
                    _completionArmed = complete == false || _lastCompletion == false || _lastRequest is null;
                    _committedHandoverCount = null;
                    WriteLog($"EXCHANGE_RECOVERY_REQUEST Recipe={_pendingRecipeId}; Complete={complete}; CompletionArmed={_completionArmed}; FirstObservation={_lastRequest is null}");
                }

                if (complete == false)
                    _completionArmed = true;
                _lastRequest = true;
                if (complete is not null)
                    _lastCompletion = complete;
                if (complete != true || !_completionArmed)
                    return;
                // Section completion precedes TPM. Preserve Stage's official count instead of
                // assuming that every shift/exchange starts a completely unused section at zero.
                EnsureConnectedRoute(RequestId, map);
                EnsureConnectedRoute(CountId, map);
                if (_committedHandoverCount is null)
                {
                    WriteLog($"EXCHANGE_COMPLETE_ACCEPTED Recipe={_pendingRecipeId}; reading {HandoverCountId} before persistence.");
                    var count = await ReadHandoverCount(map, token).ConfigureAwait(false);
                    WriteLog($"TRAY_COUNT_RECEIVED Recipe={_pendingRecipeId}; Count={count}; persistence requested.");
                    _committedHandoverCount = await recipes.SetProtectWindowCount(_pendingRecipeId, count, token).ConfigureAwait(false);
                    WriteLog($"TRAY_COUNT_PERSISTED Recipe={_pendingRecipeId}; Count={_committedHandoverCount}");
                }

                await PublishIfChanged(CountId, _committedHandoverCount.Value, token).ConfigureAwait(false);
                await PublishIfChanged(RequestId, 0, token).ConfigureAwait(false);
                var completedRecipe = _pendingRecipeId;
                var completedCount = _committedHandoverCount;
                _acknowledgedCycle = _pendingCycle;
                ClearPending();
                _lastRequest = false;
                _lastCompletion = true;
                WriteLog($"Stage replacement complete: Recipe={completedRecipe}; Stage Tray Count={completedCount}; usage persisted/reported; request OFF. TPM remains separate.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private void ClearPending()
        {
            _pendingRecipeId = null;
            _pendingCycle = null;
            _completionArmed = false;
            _committedHandoverCount = null;
        }

        private async Task<int> ReadHandoverCount(IReadOnlyList<ST_MELSEC_MAP_DATA> map, CancellationToken token)
        {
            var route = map.SingleOrDefault(row => row.Use && row.Id.Equals(HandoverCountId, StringComparison.OrdinalIgnoreCase));
            if (route is null || route.Address == "UNASSIGNED" || route.Direction != EN_MELSEC_DIRECTION.In || route.Access == EN_MELSEC_ACCESS.Write || route.DataType != EN_MELSEC_DATA_TYPE.Word || route.Length != 1)
                throw new InvalidOperationException("보호윈도우 Tray Count 인계 수신 맵을 확인해 주세요: " + HandoverCountId);
            if (!interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).Any(device => device.Number == route.DeviceNo && (device.IsSimulation || device.ConnectionState == EN_COMM_STATE.Online)))
                throw new IOException("보호윈도우 Tray Count 수신 MELSEC가 OFFLINE입니다.");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                // Refresh via the existing communication owner, not a separate device reader/poller.
                var sample = await interfaces.Melsec.RefreshCachedValueAsync(HandoverCountId, limit.Token).ConfigureAwait(false);
                var count = sample.IsValid && sample.Value is not null ? Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) : double.NaN;
                if (!double.IsFinite(count) || count < 0 || count > ushort.MaxValue || count != Math.Truncate(count))
                    throw new IOException("보호윈도우 Tray Count 인계값이 유효한 1 Word 매수가 아닙니다.");
                return (int)count;
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested && limit.IsCancellationRequested)
            {
                throw new TimeoutException("보호윈도우 Tray Count 수신 확인 시간이 초과되었습니다.");
            }
        }
#endregion
    }

    /// <summary>Phase-boundary process data writes through the existing MELSEC owner and shared readback.</summary>
    public sealed class CStageProcessDataPublisher(CInterfaceManager interfaces, Action<string>? log = null)
    {
        public const string SpeedId = "PROCESS_STAGE_SPEED";
        public const string MachiningCompleteId = "PROCESS_PROCESS_COMPLETE";
        public async Task PrepareDoReady(double speed, CancellationToken token)
        {
            ValidateSpeed(speed);
            await Publish(MachiningCompleteId, 0, EN_MELSEC_DATA_TYPE.DWord, token).ConfigureAwait(false);
            await PublishSpeed(speed, token).ConfigureAwait(false);
        }

        public Task PublishSpeed(double speed, CancellationToken token)
        {
            ValidateSpeed(speed);
            return Publish(SpeedId, speed, EN_MELSEC_DATA_TYPE.Double, token);
        }

        public Task PublishMachiningComplete(CancellationToken token) => Publish(MachiningCompleteId, 1, EN_MELSEC_DATA_TYPE.DWord, token);
        private static void ValidateSpeed(double speed)
        {
            if (!double.IsFinite(speed) || speed <= 0)
                throw new InvalidOperationException("Recipe STAGE_SPEED must be a finite positive speed in mm/sec.");
        }

        private async Task Publish(string id, double expected, EN_MELSEC_DATA_TYPE type, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var values = interfaces.Melsec.GetCachedSnapshot(out var map);
            var row = Route(map);
            var tolerance = Math.Max(1e-8, Math.Abs(row.ReadScale) / 2 + 1e-9);
            if (Matches(values.GetValueOrDefault(id), expected, tolerance))
            {
                WriteLog($"OUTPUT_UNCHANGED {id}; Logical={expected.ToString(CultureInfo.InvariantCulture)}");
                return;
            }

            var elapsed = Stopwatch.StartNew();
            WriteLog(FormattableString.Invariant($"OUTPUT_REQUEST {id}; MELSEC_{row.DeviceNo}; Address={row.Address}; Logical={expected}; ReadScale={row.ReadScale}; WriteScale={row.WriteScale}"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                // Accept actual shared readback collected during the write, as well as after it.
                var writeStartedAt = DateTimeOffset.UtcNow;
                await Task.Run(() =>
                {
                    if (type == EN_MELSEC_DATA_TYPE.Double)
                        interfaces.Melsec.WriteDouble(id, expected, timeout.Token);
                    else
                        interfaces.Melsec.WriteWord(id, (int)expected, timeout.Token);
                }, timeout.Token).ConfigureAwait(false);
                WriteLog($"OUTPUT_SENT {id}; ElapsedMs={elapsed.ElapsedMilliseconds}");
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    values = interfaces.Melsec.GetCachedSnapshot(out map);
                    Route(map);
                    var sample = values.GetValueOrDefault(id);
                    if (Matches(sample, expected, tolerance) && sample!.UpdatedAt >= writeStartedAt)
                    {
                        WriteLog($"{id}={expected.ToString(CultureInfo.InvariantCulture)} confirmed from shared readback. ElapsedMs={elapsed.ElapsedMilliseconds}; SampleAt={sample.UpdatedAt:O}");
                        return;
                    }

                    await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                WriteLog($"OUTPUT_FAILED {id}; Timeout; ElapsedMs={elapsed.ElapsedMilliseconds}");
                throw new TimeoutException($"Process data write/readback timeout: {id}={expected}.");
            }
            catch (Exception error)
            {
                WriteLog($"OUTPUT_FAILED {id}; ElapsedMs={elapsed.ElapsedMilliseconds}; {error}");
                throw;
            }

            ST_MELSEC_MAP_DATA Route(IReadOnlyList<ST_MELSEC_MAP_DATA> rows)
            {
                var found = rows.SingleOrDefault(item => item.Use && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (found is null || found.Direction != EN_MELSEC_DIRECTION.Out || found.Access != EN_MELSEC_ACCESS.ReadWrite || found.DataType != type || found.Length != 2)
                    throw new InvalidOperationException($"Process data route unavailable: {id}.");
                if (!interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).Any(device => device.Number == found.DeviceNo && (device.IsSimulation || device.ConnectionState == EN_COMM_STATE.Online)))
                    throw new InvalidOperationException($"MELSEC offline for process data: {id}.");
                return found;
            }
        }

        private void WriteLog(string detail)
        {
            try
            {
                log?.Invoke(detail);
            }
            catch (Exception error)
            {
                try
                {
                    Trace.TraceError($"Process data log failed: {error}");
                }
                catch
                {
                }
            }
        }

        private static bool Matches(ST_MELSEC_CACHED_VALUE? sample, double expected, double tolerance)
        {
            if (sample?.IsValid != true || sample.Value is null)
                return false;
            try
            {
                return Math.Abs(Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) - expected) <= tolerance;
            }
            catch (Exception error)when (error is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }
    }

    public sealed record ST_STAGE_READY_POSITION_RESULT(bool IsReady, string Detail);
    /// <summary>Reads existing MELSEC and setting snapshots only; never requests a device read or move.</summary>
    public sealed class CStageReadyPositionCheck(CInterfaceManager interfaces, CSettingManager settings)
    {
        private const decimal ToleranceValueMm = 0.03m;
        public const double ToleranceMm = (double)ToleranceValueMm;
        public const string PositionId = "MOTOR_STAGE_Y_CUR_POS";
        public const string BusyId = "MOTOR_STAGE_Y_BUSY";
        public ST_STAGE_READY_POSITION_RESULT Evaluate()
        {
            var values = interfaces.Melsec.GetCachedSnapshot([PositionId, BusyId], out var map);
            var positionRow = map.FirstOrDefault(row => row.Use && row.Id.Equals(PositionId, StringComparison.OrdinalIgnoreCase));
            var busyRow = map.FirstOrDefault(row => row.Use && row.Id.Equals(BusyId, StringComparison.OrdinalIgnoreCase));
            var devices = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec);
            // A handshake-only simulator has no physical feedback routes. Retain its explicit Ready-OK policy.
            if (positionRow is null && busyRow is null)
            {
                var stageDeviceNo = map.FirstOrDefault(row => row.Use && row.Id.Equals(CStageRequestSequence.StgAble, StringComparison.OrdinalIgnoreCase))?.DeviceNo;
                if (devices.Any(device => device.Number == stageDeviceNo && device.IsSimulation))
                    return new(true, "MELSEC SIMULATION: Stage Y Ready OK.");
            }

            if (!IsInputRoute(positionRow) || !IsInputRoute(busyRow))
                return new(false, "Stage Y position / Busy input map is missing or unassigned.");
            var positionDevice = devices.FirstOrDefault(device => device.Number == positionRow.DeviceNo);
            var busyDevice = devices.FirstOrDefault(device => device.Number == busyRow.DeviceNo);
            if (positionDevice is null || (!positionDevice.IsSimulation && positionDevice.ConnectionState != EN_COMM_STATE.Online))
                return new(false, "Stage Y position MELSEC is offline.");
            if (busyDevice is null || (!busyDevice.IsSimulation && busyDevice.ConnectionState != EN_COMM_STATE.Online))
                return new(false, "Stage Y Busy MELSEC is offline.");
            // Simulation is the operating setting, including offline simulated interfaces. Mixed routes
            // still require each source to be available and their shared feedback to satisfy the checks below.
            if (positionDevice.IsSimulation && busyDevice.IsSimulation)
                return new(true, "MELSEC SIMULATION: Stage Y Ready OK.");
            var targetText = settings.GetValue(EN_SETTING_TAB.Motor, "STAGE_START_POS_Y");
            if (!double.TryParse(targetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var target) || !double.IsFinite(target) || target < (double)decimal.MinValue || target > (double)decimal.MaxValue)
                throw new InvalidOperationException("Stage Y Start Position setting is missing or invalid.");
            if (!TryNumber(positionRow, values, out var position) || !TryNumber(busyRow, values, out var busy))
                return new(false, "Waiting for valid shared Stage Y position / Busy feedback.");
            if (position < (double)decimal.MinValue || position > (double)decimal.MaxValue)
                return new(false, "Stage Y position is out of range.");
            // Decimal comparison accepts the inclusive 30 µm boundary despite binary floating-point noise.
            var difference = Math.Abs((decimal)position - (decimal)target);
            var ready = difference <= ToleranceValueMm && busy == 0;
            return new(ready, string.Create(CultureInfo.InvariantCulture, $"Stage Y Current={position:F4} mm, Setting Target={target:F4} mm, Difference={difference:F4} mm, Tolerance={ToleranceValueMm:F4} mm, Busy={busy}."));
        }

        private static bool IsInputRoute([NotNullWhen(true)] ST_MELSEC_MAP_DATA? row) => row is { Use: true, Direction: EN_MELSEC_DIRECTION.In } && row.Access != EN_MELSEC_ACCESS.Write && !string.IsNullOrWhiteSpace(row.Address) && !row.Address.Trim().Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase);
        private static bool TryNumber(ST_MELSEC_MAP_DATA? row, IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> values, out double number)
        {
            number = double.NaN;
            if (!IsInputRoute(row) || !values.TryGetValue(row.Id, out var sample) || !sample.IsValid || sample.Value is null)
                return false;
            try
            {
                var value = Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture);
                number = row.DataType switch
                {
                    EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float => value,
                    _ => double.NaN
                };
                return double.IsFinite(number);
            }
            catch (Exception exception)when (exception is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }
    }

    public sealed record ST_STAGE_REQUEST_STATUS(string State, string Detail, bool ReadyForProcess = false);
    public sealed record ST_STAGE_SIGNAL_SNAPSHOT(bool Connected, IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> Values);
    /// <summary>Stage is master. Reads only shared snapshots; all writes use the central MELSEC queue.</summary>
    public sealed partial class CStageRequestSequence(
        Func<ST_STAGE_SIGNAL_SNAPSHOT> read, 
        Func<string, int, CancellationToken, Task> write, 
        CSequenceActionStatusStore actions, 
        Action<string> log, 
        Func<CancellationToken, Task>? stopTasks = null, 
        Action? checkActiveWork = null, 
        Func<CancellationToken, Task>? verifyProcessReady = null, 
        Func<CancellationToken, Task>? processMachining = null, 
        Func<CancellationToken, Task>? processResults = null, 
        Func<CancellationToken, Task>? releaseProcessTasks = null, 
        Func<CancellationToken, Task>? updateProcessOptions = null, 
        ST_STAGE_ZERO_LINE_ACTIONS? zeroLine = null, 
        Func<string, string>? describeSignal = null
    )
    {
        public const string StgAble = "HANDSHAKE_STG_DOREADY_ABLE";
        public const string StgStart = "HANDSHAKE_STG_DOREADY_START";
        public const string StgComplete = "HANDSHAKE_STG_DOREADY_COMPLETE";
        public const string PrcAble = "HANDSHAKE_PRC_DOREADY_ABLE";
        public const string PrcStart = "HANDSHAKE_PRC_DOREADY_START";
        public const string PrcComplete = "HANDSHAKE_PRC_DOREADY_COMPLETE";
        public const string StgProcessAble = "HANDSHAKE_STG_PROCESS_ABLE";
        private readonly object _sync = new();
        private CancellationTokenSource? _stop;
        private Task? _worker;
        private bool _cleanupFailed;
        private bool _cleanupTasks;
        private bool _cleanupOutputs;
        private bool _tasksIssued;
        private Func<CancellationToken, Task>? _loadLatestScripts;
        public bool NeedsCleanup => Volatile.Read(ref _cleanupFailed);
        public event Action<CSequenceFailureException>? Failed;
        private string _scenario = "AUTO";
        private void ReportFailure(CSequenceFailureException failure) => failure.Publish(Failed);

        private ST_STAGE_REQUEST_STATUS _status = new("DISABLED", "Stage request monitoring disabled.");
        public ST_STAGE_REQUEST_STATUS Status => Volatile.Read(ref _status);

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                    return _worker is { IsCompleted: false };
            }
        }

        public void Start(
            Func<CancellationToken, Task> prepare, 
            Func<CancellationToken, Task> buildUpload, 
            Func<CancellationToken, Task> zero, 
            Func<CancellationToken, Task> runTasks, 
            ST_STAGE_REVIEW_ACTIONS? review = null, ST_STAGE_TPM_ACTIONS? tpm = null,
            Func<CancellationToken, Task>? loadLatestScripts = null
        )
        {
            lock (_sync)
            {
                if (_worker is { IsCompleted: false })
                    throw new InvalidOperationException("Previous Stage sequence has not stopped.");
                if (_cleanupFailed)
                    throw new InvalidOperationException("Previous Stage cleanup has not completed.");
                _stop?.Dispose();
                _stop = new CancellationTokenSource();
                lock (_signalTraceLock)
                    _signalTraceValues.Clear();
                Volatile.Write(ref _logCycleId, "");
                LogEvent("AUTO_MONITOR_START Actions reset to WAIT.");
                actions.Reset();
                _reviewActions = review;
                _scenario = "AUTO";
                _reviewOutputsOwned = false;
                _tpmActions = tpm;
                _loadLatestScripts = loadLatestScripts;
                _tpmOutputsOwned = false;
                _worker = Task.Run(() => Run(prepare, buildUpload, zero, runTasks, _stop.Token));
            }
        }

        public async Task<bool> StopAsync()
        {
            LogEvent("AUTO_MONITOR_STOP_REQUESTED");
            Task? worker;
            lock (_sync)
            {
                _stop?.Cancel();
                if (_worker is { IsCompleted: true } && _cleanupFailed)
                    _worker = Task.Run(() => Cleanup(_cleanupTasks, _cleanupOutputs));
                worker = _worker;
            }

            if (worker is null)
                return true;
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return !NeedsCleanup;
            }
            catch (TimeoutException)
            {
                Set("STOPPING", "Waiting for the existing device operation to stop; restart is blocked.");
                return false; // Keep the worker and prohibit overlapping runs, even if a driver ignores cancellation.
            }
        }

        private async Task Run(
            Func<CancellationToken, Task> prepare, 
            Func<CancellationToken, Task> buildUpload, 
            Func<CancellationToken, Task> zero, 
            Func<CancellationToken, Task> runTasks, 
            CancellationToken token
        )
        {
            var outputsOwned = false;
            _tasksIssued = false;
            var failed = false;
            try
            {
                while (true)
                {
                    _scenario = "AUTO";
                    Set(_reviewActions is null ? "WAIT_DOREADY" : "WAIT_REQUEST", "Waiting for Stage DoReady / Review ABLE (START/COMPLETE must be OFF).");
                    // Synchronize our side first. A previous high COMPLETE can otherwise
                    // prevent Stage from clearing its own COMPLETE after a reconnect.
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var initial = ReadSignals();
                        if (TrySignal(initial, StgAble, out _) && TrySignal(initial, StgStart, out _) && TrySignal(initial, StgComplete, out _))
                            break;
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }

                    outputsOwned = true;
                    if (processMachining is not null)
                    {
                        _processOutputsOwned = true;
                        foreach (var id in ProcessOutputs)
                            await WriteConfirmed(id, 0, token).ConfigureAwait(false);
                    }

                    foreach (var id in new[]
                    {
                        PrcComplete,
                        PrcStart,
                        PrcAble
                    }

                    )
                        await WriteConfirmed(id, 0, token).ConfigureAwait(false);
                    if (_reviewActions is not null)
                    {
                        _reviewOutputsOwned = true;
                        foreach (var id in ReviewOutputs)
                            await WriteConfirmed(id, 0, token).ConfigureAwait(false);
                    }

                    // Standing ABLE is allowed, but an old START/COMPLETE must never replay a request.
                    if (_tpmActions is not null)
                    {
                        _tpmOutputsOwned = true;
                        foreach (var id in TpmOutputs) await WriteConfirmed(id, 0, token).ConfigureAwait(false);
                    }
                    var processOnly = false;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var sample = ReadSignals();
                        if (IsTpmReady(sample))
                        {
                            await RunTpm(token).ConfigureAwait(false);
                            _scenario = "AUTO";
                            Set("WAIT_REQUEST", "Waiting for Stage DoReady / Review / TPM ABLE.");
                            continue;
                        }
                        // Review is independent of Process. When both requests are present, finish the
                        // previous glass's Review before preparing a new glass with its newly saved offsets.
                        if (IsReviewReady(sample))
                        {
                            await RunReview(token).ConfigureAwait(false);
                            _scenario = "AUTO";
                            Set("WAIT_REQUEST", "Waiting for Stage DoReady / Review ABLE.");
                            continue;
                        }

                        if (TrySignal(sample, StgAble, out var able) && TrySignal(sample, StgStart, out var start) && TrySignal(sample, StgComplete, out var complete) && able == 1 && start == 0 && complete == 0)
                            break;
                        if (processMachining is not null && TrySignal(sample, StgProcessAble, out var processAble) && processAble == 1
                            && TrySignal(sample, StgProcessStart, out var processStart) && processStart == 0
                            && TrySignal(sample, StgProcessComplete, out var processComplete) && processComplete == 0)
                        {
                            processOnly = true;
                            break;
                        }
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }

                    if (processOnly)
                    {
                        _scenario = "PROCESS";
                        BeginLogCycle("PROCESS_ONLY");
                        // Re-capture conditions, but do not rebuild. Re-upload the latest
                        // stored AUTO script set; a Manual/fault transition stopped its Tasks.
                        LogEvent("Process 단독 요청 접수 · DoReady 신호 교환 없이 마지막 스크립트 준비");
                        actions.Reset();
                        Set("PROCESS_SCRIPT_PREPARE", "Preparing conditions for the latest stored AUTO script set.");
                        await Action(CSequenceActionIds.PrepareData, prepare, token).ConfigureAwait(false);
                        Set("PROCESS_SCRIPT_UPLOAD", "Loading and uploading the latest stored AUTO scripts; no script rebuild.");
                        await Action(CSequenceActionIds.BuildUploadScript, _loadLatestScripts ?? (_ => throw new InvalidOperationException("마지막 AUTO 스크립트를 읽는 기능이 연결되지 않았습니다.")), token).ConfigureAwait(false);
                        actions.ResetToWaiting([CSequenceActionIds.PrepareData, CSequenceActionIds.BuildUploadScript]);
                        await RunProcess(zero, runTasks, token).ConfigureAwait(false);
                        _tasksIssued = false;
                        LogEvent("CYCLE_COMPLETE Kind=PROCESS_ONLY");
                        Volatile.Write(ref _logCycleId, "");
                        continue;
                    }

                    _scenario = "DOREADY";
                    BeginLogCycle("DOREADY_PROCESS");
                    LogEvent("ACTIONS_RESET All actions -> WAIT for a new request.");
                    actions.Reset(); // Reset execution feedback only when accepting a new request.
                    await WriteConfirmed(PrcAble, 1, token).ConfigureAwait(false);
                    Set("WAIT_START", "DoReady ABLE acknowledged; waiting for Stage START.");
                    await WaitSignal(StgStart, 1, token).ConfigureAwait(false);
                    await WriteConfirmed(PrcStart, 1, token).ConfigureAwait(false);
                    Set("PREPARE", "Preparing the request recipe and coordinates.");
                    await Action(CSequenceActionIds.PrepareData, prepare, token).ConfigureAwait(false);
                    Set("BUILD_UPLOAD", "Building and uploading scripts only; MOF initialization and Task RUN belong to Process preparation.");
                    await Action(CSequenceActionIds.BuildUploadScript, buildUpload, token).ConfigureAwait(false);
                    await WriteConfirmed(PrcComplete, 1, token).ConfigureAwait(false);
                    Set("WAIT_COMPLETE", "Scripts uploaded; waiting for Stage DoReady COMPLETE.");
                    await WaitSignal(StgComplete, 1, token).ConfigureAwait(false);
                    Set("DOREADY_SIGNALS_CLEAR", "Clearing our DoReady signals and waiting for Stage DoReady signals OFF.");
                    await Action(
                        CSequenceActionIds.DoReadySignalsClear, 
                        async ct =>
                    {
                        foreach (var id in new[]
                        {
                            PrcComplete,
                            PrcStart,
                            PrcAble
                        }

                        )
                            await WriteConfirmed(id, 0, ct).ConfigureAwait(false);
                        actions.ResetToWaiting([CSequenceActionIds.PrepareData, CSequenceActionIds.BuildUploadScript]);
                        LogEvent("ACTIONS_RESET DOREADY_DATA_PREPARE,DOREADY_SCRIPT_BUILD_UPLOAD -> WAIT");
                        // Keep Clear RUNNING until the peer releases its signals; only a successful clear returns to WAIT.
                        foreach (var id in new[]
                        {
                            StgComplete,
                            StgStart,
                            StgAble
                        }

                        )
                            await WaitSignal(id, 0, ct).ConfigureAwait(false);
                    }, 
                        token, 
                        resetToWaitingOnSuccess: true
                    ).ConfigureAwait(false);
                    Set("WAIT_PROCESS", "DoReady handshake cleared; waiting for Stage Process ABLE before MOF initialization and Task RUN.");
                    _scenario = "PROCESS";
                    if (processMachining is not null)
                    {
                        await RunProcess(zero, runTasks, token).ConfigureAwait(false);
                        // Completed Task/queue ownership was released by RunProcess, without STOP.
                        // Continue AUTO, accepting a new glass only after the old START/COMPLETE clear.
                        _tasksIssued = false;
                        LogEvent("CYCLE_COMPLETE Kind=DOREADY_PROCESS");
                        Volatile.Write(ref _logCycleId, "");
                        continue;
                    }

                    var processPrepared = false;
                    // Process begins only after DoReady has finished, even when its ABLE arrived earlier.
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var sample = EnsureConnectedSignals();
                        CheckActiveWork();
                        if (!processPrepared && TrySignal(sample, StgProcessAble, out var able) && able == 1)
                        {
                            await PrepareProcessTasks(zero, runTasks, token).ConfigureAwait(false);
                            processPrepared = true;
                            Set("PROCESS_READY_VERIFIED", "Stage Y Ready position and Task encoder-wait readiness verified.", true);
                        }

                        // Observer-only callers without Process callbacks stay at prepared readiness.
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
                Set("STOPPED", "Stage request sequence stopped.");
            }
            catch (Exception exception)
            {
                failed = true;
                LogEvent($"SEQUENCE_FAILED {exception}");
                var failure = CSequenceFailureException.ScenarioFailure(_scenario, exception);
                Set("ERROR", failure.Message);
                ReportFailure(failure);
            }
            finally
            {
                await Cleanup(_tasksIssued, outputsOwned).ConfigureAwait(false);
                if (!failed && !NeedsCleanup)
                    Set("STOPPED", "Stage request sequence stopped.");
            }
        }

        private async Task Cleanup(bool tasksIssued, bool outputsOwned)
        {
            LogEvent($"CLEANUP_BEGIN TasksIssued={tasksIssued}; OutputsOwned={outputsOwned}; ReviewOwned={_reviewOutputsOwned}; ProcessOwned={_processOutputsOwned}");
            _cleanupTasks = tasksIssued;
            _cleanupOutputs = outputsOwned;
            var failed = false;
            if (zeroLine is not null)
            {
                try
                {
                    await zeroLine.Release().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failed = true;
                    Set("ERROR", $"0선 정리 실패: {error.Message}");
                    ReportFailure(CSequenceFailureException.Action(CSequenceActionIds.ProcessSignalsClear, error));
                }
            }

            if (tasksIssued && stopTasks is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await stopTasks(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failed = true;
                    Set("ERROR", $"{Status.Detail} Task cleanup failed: {exception.Message}");
                    ReportFailure(CSequenceFailureException.Action(CSequenceActionIds.ProcessSignalsClear, exception));
                }
            }

            if (outputsOwned)
            {
                foreach (var id in new[]
                {
                    PrcComplete,
                    PrcStart,
                    PrcAble
                }.Concat(_processOutputsOwned ? ProcessOutputs : []).Concat(_reviewOutputsOwned ? ReviewOutputs : []).Concat(_tpmOutputsOwned ? TpmOutputs : []))
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        LogEvent($"CLEANUP_OUTPUT_REQUEST {SignalRoute(id)}; Logical=0");
                        await write(id, 0, cleanup.Token).ConfigureAwait(false);
                        LogEvent($"CLEANUP_OUTPUT_SENT {SignalRoute(id)}; Logical=0; Readback=not requested by cleanup policy");
                    }
                    catch (Exception exception)
                    {
                        failed = true;
                        Set("ERROR", $"{Status.Detail} Signal cleanup failed: {id}. {exception.Message}");
                        ReportFailure(CSequenceFailureException.Signal(_scenario, id, exception is TimeoutException or OperationCanceledException, exception));
                    }
                }
            }

            LogEvent($"CLEANUP_END Success={!failed}");
            Volatile.Write(ref _cleanupFailed, failed);
            if (!failed && (Status.State is "STOPPING" or "ERROR") && _stop?.IsCancellationRequested == true)
                Set("STOPPED", "Stage request sequence stop and cleanup confirmed.");
        }

        private async Task Action(string id, Func<CancellationToken, Task> action, CancellationToken token, TimeSpan? timeout = null, bool resetToWaitingOnSuccess = false)
        {
            var elapsed = Stopwatch.StartNew();
            LogEvent($"ACTION_BEGIN {id}; TimeoutMs={(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds}");
            try
            {
                await actions.RunAsync(id, async () =>
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
                    try
                    {
                        await action(limit.Token).ConfigureAwait(false);
                        limit.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException error)when (!token.IsCancellationRequested && limit.IsCancellationRequested)
                    {
                        if (error is CSequenceSignalCanceledException wait)
                            throw CSequenceFailureException.Signal(wait.Scenario, wait.Signal, true, error);
                        throw new TimeoutException($"Sequence action timed out: {id}.");
                    }

                    EnsureConnectedSignals();
                }, resetToWaitingOnSuccess).ConfigureAwait(false);
                LogEvent($"ACTION_DONE {id}; ElapsedMs={elapsed.ElapsedMilliseconds}; State={(resetToWaitingOnSuccess ? "WAIT" : "DONE")}");
            }
            catch (Exception error)
            {
                LogEvent($"ACTION_{(error is OperationCanceledException ? "CANCELED" : "FAILED")} {id}; ElapsedMs={elapsed.ElapsedMilliseconds}; {error}");
                if (error is OperationCanceledException && token.IsCancellationRequested) throw;
                throw CSequenceFailureException.Action(id, error);
            }
        }

        private async Task WriteConfirmed(string id, int value, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            LogEvent($"OUTPUT_REQUEST {SignalRoute(id)}; Logical={value}");
            try
            {
                EnsureConnectedSignals();
                var acknowledgedAt = DateTimeOffset.UtcNow;
                await write(id, value, token).ConfigureAwait(false);
                LogEvent($"OUTPUT_SENT {SignalRoute(id)}; Logical={value}; ElapsedMs={elapsed.ElapsedMilliseconds}");
                await WaitSignal(id, value, token, acknowledgedAt, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                LogEvent($"OUTPUT_CONFIRMED {SignalRoute(id)}; Logical={value}; ElapsedMs={elapsed.ElapsedMilliseconds}");
            }
            catch (Exception error)
            {
                LogEvent($"OUTPUT_FAILED {SignalRoute(id)}; Logical={value}; ElapsedMs={elapsed.ElapsedMilliseconds}; {error}");
                if (error is OperationCanceledException && token.IsCancellationRequested) throw;
                throw CSequenceFailureException.Signal(_scenario, id, error is TimeoutException or OperationCanceledException, error);
            }
        }

        private async Task WaitSignal(string id, int value, CancellationToken token, DateTimeOffset? after = null, TimeSpan? timeout = null)
        {
            var elapsed = Stopwatch.StartNew();
            LogEvent($"WAIT_BEGIN {SignalRoute(id)}; Expected={value}; TimeoutMs={(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds}; SampleAfter={after:O}");
            try
            {
                while (elapsed.Elapsed < (timeout ?? TimeSpan.FromMinutes(2)))
                {
                    token.ThrowIfCancellationRequested();
                    var sample = EnsureConnectedSignals();
                    CheckActiveWork();
                    if (!TrySignal(sample, id, out var actual))
                    {
                        if (!sample.Values.TryGetValue(id, out var pending) || pending.State == EN_MELSEC_CACHE_STATE.Pending)
                        {
                            await Task.Delay(50, token).ConfigureAwait(false);
                            continue;
                        }

                        throw new InvalidOperationException($"Invalid or unavailable handshake: {id}.");
                    }

                    if (actual == value && (after is null || sample.Values[id].UpdatedAt >= after))
                    {
                        LogEvent($"WAIT_MATCH {SignalRoute(id)}; Expected={value}; Actual={actual}; ElapsedMs={elapsed.ElapsedMilliseconds}; SampleAt={sample.Values[id].UpdatedAt:O}");
                        return;
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                throw new TimeoutException($"Handshake timeout: {id} = {value}.");
            }
            catch (Exception error)
            {
                LogEvent($"WAIT_{(error is OperationCanceledException ? "CANCELED" : "FAILED")} {SignalRoute(id)}; Expected={value}; ElapsedMs={elapsed.ElapsedMilliseconds}; {error}");
                if (error is OperationCanceledException canceled) throw new CSequenceSignalCanceledException(_scenario, id, canceled);
                throw CSequenceFailureException.Signal(_scenario, id, error is TimeoutException, error);
            }
        }

        private void CheckActiveWork()
        {
            try { checkActiveWork?.Invoke(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw CSequenceFailureException.Action(CSequenceActionIds.ProcessMachining, error); }
        }

        private ST_STAGE_SIGNAL_SNAPSHOT EnsureConnectedSignals()
        {
            var sample = ReadSignals();
            if (!sample.Connected)
                throw new InvalidOperationException("MELSEC offline during Stage sequence.");
            foreach (var id in new[]
            {
                StgAble,
                StgStart,
                StgComplete
            }

            )
                if (!TrySignal(sample, id, out _))
                    throw new InvalidOperationException($"Invalid or stale Stage handshake: {id}.");
            return sample;
        }

        private static bool TrySignal(ST_STAGE_SIGNAL_SNAPSHOT snapshot, string id, out int value)
        {
            value = -1;
            if (!snapshot.Connected || !snapshot.Values.TryGetValue(id, out var sample) || !sample.IsValid || sample.Value is null)
                return false;
            try
            {
                var number = Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture);
                if (number is not (0 or 1))
                    return false;
                value = (int)number;
                return true;
            }
            catch (Exception exception)when (exception is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        private void Set(string state, string detail, bool ready = false)
        {
            Volatile.Write(ref _status, new(state, detail, ready));
            LogEvent($"{state}: {detail}");
        }

        public const string StgProcessStart = "HANDSHAKE_STG_PROCESS_START";
        public const string StgProcessComplete = "HANDSHAKE_STG_PROCESS_COMPLETE";
        public const string PrcProcessAble = "HANDSHAKE_PRC_PROCESS_ABLE";
        public const string PrcProcessStart = "HANDSHAKE_PRC_PROCESS_START";
        public const string PrcProcessComplete = "HANDSHAKE_PRC_PROCESS_COMPLETE";
        private static readonly string[] ProcessOutputs = [PrcProcessComplete, PrcProcessStart, PrcProcessAble];
        private static readonly string[] CompletedProcessActions = [
            CSequenceActionIds.ProcessMofZero, 
            CSequenceActionIds.ProcessTaskRun, 
            CSequenceActionIds.ProcessReadyPosition, 
            CSequenceActionIds.ProcessMachining, 
            CSequenceActionIds.ProcessResults, 
            CSequenceActionIds.ProcessZeroPrepare, 
            CSequenceActionIds.ProcessZeroMeasure, 
            CSequenceActionIds.ProcessZeroResults
        ];
        private bool _processOutputsOwned;
        private async Task PrepareProcessTasks(Func<CancellationToken, Task> zero, Func<CancellationToken, Task> runTasks, CancellationToken token)
        {
            Set("PROCESS_READY_CHECK", "Checking Stage Y Ready position and Busy OFF before MOF initialization.");
            await Action(CSequenceActionIds.ProcessReadyPosition, verifyProcessReady ?? (_ => Task.CompletedTask), token).ConfigureAwait(false);
            if (zeroLine?.Enabled() == true)
            {
                Set("PROCESS_ZERO_PREPARE", "Preparing 0-line IOF buffer, first X and Vision before Process ABLE.");
                await Action(CSequenceActionIds.ProcessZeroPrepare, zeroLine.Prepare, token).ConfigureAwait(false);
            }

            Set("PROCESS_MOF_ZERO", "Checking auxiliary feedback and zeroing only nonzero axes.");
            await Action(CSequenceActionIds.ProcessMofZero, zero, token).ConfigureAwait(false);
            // Register ownership before issuing RUN: an accepted command can lose its acknowledgement.
            _tasksIssued = true;
            Set("PROCESS_TASK_RUN", "Initializing MOF and verifying every participating Task is waiting for encoder motion.");
            await Action(CSequenceActionIds.ProcessTaskRun, runTasks, token).ConfigureAwait(false);
        }

        private async Task RunProcess(Func<CancellationToken, Task> zero, Func<CancellationToken, Task> runTasks, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var sample = EnsureConnectedSignals();
                CheckActiveWork();
                if (TrySignal(sample, StgProcessAble, out var able) && able == 1 && TrySignal(sample, StgProcessStart, out var start) && start == 0 && TrySignal(sample, StgProcessComplete, out var complete) && complete == 0)
                    break;
                await Task.Delay(100, token).ConfigureAwait(false);
            }

            await PrepareProcessTasks(zero, runTasks, token).ConfigureAwait(false);
            await WriteConfirmed(PrcProcessAble, 1, token).ConfigureAwait(false);
            Set("PROCESS_WAIT_START", "Process preparation complete and ABLE acknowledged; waiting for Stage START.", true);
            await WaitSignal(StgProcessStart, 1, token).ConfigureAwait(false);
            if (updateProcessOptions is not null)
                await updateProcessOptions(token).ConfigureAwait(false);
            await WriteConfirmed(PrcProcessStart, 1, token).ConfigureAwait(false);
            Set("PROCESS_MACHINING", "Waiting for normal completion of every machining Task / queue.");
            // Task/queue execution has its configured run timeout, not the short handshake/action timeout.
            await WaitMachiningAndZeroLine(token).ConfigureAwait(false);
            if (zeroLine?.Enabled() == true)
            {
                Set("PROCESS_ZERO_RESULTS", "Saving 0-line judgments and the final follow-up request.");
                await Action(CSequenceActionIds.ProcessZeroResults, zeroLine.Save, token).ConfigureAwait(false);
            }

            Set("PROCESS_RESULTS", "Validating and saving machining results.");
            await Action(CSequenceActionIds.ProcessResults, processResults ?? (_ => Task.CompletedTask), token).ConfigureAwait(false);
            if (updateProcessOptions is not null)
                await updateProcessOptions(token).ConfigureAwait(false);
            await WriteConfirmed(PrcProcessComplete, 1, token).ConfigureAwait(false);
            Set("PROCESS_WAIT_COMPLETE", "Machining complete; waiting for Stage Process COMPLETE.");
            await WaitSignal(StgProcessComplete, 1, token).ConfigureAwait(false);
            Set("PROCESS_SIGNALS_CLEAR", "Clearing our Process signals and action feedback; waiting for Stage Process signals OFF.");
            await Action(
                CSequenceActionIds.ProcessSignalsClear, 
                async ct =>
            {
                foreach (var id in ProcessOutputs)
                    await WriteConfirmed(id, 0, ct).ConfigureAwait(false);
                actions.ResetToWaiting(CompletedProcessActions);
                LogEvent($"ACTIONS_RESET {string.Join(",", CompletedProcessActions)} -> WAIT");
                // This action remains RUNNING while waiting for the peer and releasing ownership.
                // On success it returns directly to WAIT; errors and cancellation remain visible.
                foreach (var id in new[]
                {
                    StgProcessComplete,
                    StgProcessStart,
                    StgProcessAble
                }

                )
                    await WaitSignal(id, 0, ct).ConfigureAwait(false);
                if (releaseProcessTasks is not null)
                    await releaseProcessTasks(ct).ConfigureAwait(false);
                if (zeroLine is not null)
                    await zeroLine.Release().ConfigureAwait(false);
            }, 
                token, 
                resetToWaitingOnSuccess: true
            ).ConfigureAwait(false);
            Set("PROCESS_COMPLETE", "Process handshake completed; waiting for the next DoReady request.");
        }

        public const string StgReviewAble = "HANDSHAKE_STG_REVIEW_ABLE";
        public const string StgReviewStart = "HANDSHAKE_STG_REVIEW_START";
        public const string StgReviewComplete = "HANDSHAKE_STG_REVIEW_COMPLETE";
        public const string PrcReviewAble = "HANDSHAKE_PRC_REVIEW_ABLE";
        public const string PrcReviewStart = "HANDSHAKE_PRC_REVIEW_START";
        public const string PrcReviewComplete = "HANDSHAKE_PRC_REVIEW_COMPLETE";
        private static readonly string[] ReviewOutputs = [PrcReviewComplete, PrcReviewStart, PrcReviewAble];
        public const string StgTpmAble = "HANDSHAKE_STG_TOP_POWERMETER_ABLE";
        public const string StgTpmComplete = "HANDSHAKE_STG_TOP_POWERMETER_COMPLETE";
        public const string PrcTpmAble = "HANDSHAKE_PRC_TOP_POWERMETER_ABLE";
        public const string PrcTpmComplete = "HANDSHAKE_PRC_TOP_POWERMETER_COMPLETE";
        private static readonly string[] TpmOutputs = [PrcTpmComplete, PrcTpmAble];
        private ST_STAGE_TPM_ACTIONS? _tpmActions;
        private bool _tpmOutputsOwned;
        private bool IsTpmReady(ST_STAGE_SIGNAL_SNAPSHOT sample) => _tpmActions is not null && TrySignal(sample, StgTpmAble, out var able) && able == 1 && TrySignal(sample, StgTpmComplete, out var complete) && complete == 0;
        private async Task RunTpm(CancellationToken token)
        {
            _scenario = "TPM";
            var tpm = _tpmActions ?? throw new InvalidOperationException("TPM actions are not configured.");
            BeginLogCycle("TPM");
            _tpmOutputsOwned = true;
            actions.ResetToWaiting(["TPM_PREPARE", "TPM_MEASURE", "TPM_RESULTS", "TPM_SIGNALS_CLEAR"]);
            try
            {
                foreach (var id in TpmOutputs) await WriteConfirmed(id, 0, token).ConfigureAwait(false);
                Set("TPM_PREPARE", "Stage TPM ABLE received; checking Stage Y=0, dedicated sensors and CENTER conditions.");
                await Action("TPM_PREPARE", tpm.Prepare, token).ConfigureAwait(false);
                await WriteConfirmed(PrcTpmAble, 1, token).ConfigureAwait(false);
                Set("TPM_MEASURE", "Parallel Head power measurement and ATT correction.");
                await Action("TPM_MEASURE", tpm.Measure, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                await Action("TPM_RESULTS", tpm.Save, token).ConfigureAwait(false);
                await WriteConfirmed(PrcTpmComplete, 1, token).ConfigureAwait(false);
                Set("TPM_WAIT_COMPLETE", "Power correction saved; waiting for Stage TPM COMPLETE.");
                await WaitSignal(StgTpmComplete, 1, token).ConfigureAwait(false);
                await Action("TPM_SIGNALS_CLEAR", async ct =>
                {
                    foreach (var id in TpmOutputs) await WriteConfirmed(id, 0, ct).ConfigureAwait(false);
                    await WaitSignal(StgTpmComplete, 0, ct).ConfigureAwait(false);
                    await WaitSignal(StgTpmAble, 0, ct).ConfigureAwait(false);
                    actions.ResetToWaiting(["TPM_PREPARE", "TPM_MEASURE", "TPM_RESULTS"]);
                }, token, resetToWaitingOnSuccess: true).ConfigureAwait(false);
                LogEvent("CYCLE_COMPLETE Kind=TPM");
                Volatile.Write(ref _logCycleId, "");
            }
            finally { await tpm.Release().ConfigureAwait(false); }
        }
        private ST_STAGE_REVIEW_ACTIONS? _reviewActions;
        private bool _reviewOutputsOwned;
        private bool IsReviewReady(ST_STAGE_SIGNAL_SNAPSHOT sample) => _reviewActions is not null && TrySignal(sample, StgReviewAble, out var able) && able == 1 && TrySignal(sample, StgReviewStart, out var start) && start == 0 && TrySignal(sample, StgReviewComplete, out var complete) && complete == 0;
        private async Task RunReview(CancellationToken token)
        {
            _scenario = "REVIEW";
            BeginLogCycle("REVIEW");
            var review = _reviewActions ?? throw new InvalidOperationException("Review actions are not configured.");
            actions.ResetToWaiting([CSequenceActionIds.ReviewPlan, CSequenceActionIds.ReviewMeasure, CSequenceActionIds.ReviewResults, CSequenceActionIds.ReviewSignalsClear]);
            Set("REVIEW_PLAN", "Stage Review ABLE received; freezing Glass / Recipe / Run Option and Beam 11 plan before our ABLE.");
            await Action(CSequenceActionIds.ReviewPlan, review.Prepare, token).ConfigureAwait(false);
            await WriteConfirmed(PrcReviewAble, 1, token).ConfigureAwait(false);
            Set("REVIEW_WAIT_START", "Inspection plan ready; waiting for Stage Review START.");
            await WaitSignal(StgReviewStart, 1, token).ConfigureAwait(false);
            await WriteConfirmed(PrcReviewStart, 1, token).ConfigureAwait(false);
            Set("REVIEW_MEASURE", "FINE inspection running. Quality NG does not stop later targets.");
            await Action(CSequenceActionIds.ReviewMeasure, review.Measure, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            Set("REVIEW_RESULTS", "Validating all results; save accumulated FINE offset for the next Glass only when all targets are OK.");
            await Action(CSequenceActionIds.ReviewResults, review.Save, token).ConfigureAwait(false);
            await WriteConfirmed(PrcReviewComplete, 1, token).ConfigureAwait(false);
            Set("REVIEW_WAIT_COMPLETE", "All inspection results handled; waiting for Stage Review COMPLETE.");
            await WaitSignal(StgReviewComplete, 1, token).ConfigureAwait(false);
            Set("REVIEW_SIGNALS_CLEAR", "Clearing Review signals and waiting for all Stage Review signals OFF.");
            await Action(
                CSequenceActionIds.ReviewSignalsClear, 
                async ct =>
            {
                foreach (var id in ReviewOutputs)
                    await WriteConfirmed(id, 0, ct).ConfigureAwait(false);
                foreach (var id in new[]
                {
                    StgReviewComplete,
                    StgReviewStart,
                    StgReviewAble
                }

                )
                    await WaitSignal(id, 0, ct).ConfigureAwait(false);
                actions.ResetToWaiting([CSequenceActionIds.ReviewPlan, CSequenceActionIds.ReviewMeasure, CSequenceActionIds.ReviewResults]);
            }, 
                token, 
                resetToWaitingOnSuccess: true
            ).ConfigureAwait(false);
            Set("REVIEW_COMPLETE", "Review handshake completed; waiting for Stage's next request.");
            LogEvent("CYCLE_COMPLETE Kind=REVIEW");
            Volatile.Write(ref _logCycleId, "");
        }

        private async Task WaitMachiningAndZeroLine(CancellationToken token)
        {
            var machining = Action(CSequenceActionIds.ProcessMachining, processMachining!, token, Timeout.InfiniteTimeSpan);
            if (zeroLine?.Enabled() != true)
            {
                await machining.ConfigureAwait(false);
                return;
            }

            using var inspectionStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var inspection = Action(CSequenceActionIds.ProcessZeroMeasure, async ct =>
            {
                await zeroLine.Measure(ct).ConfigureAwait(false); // Capture / X travel only.
                await machining.WaitAsync(ct).ConfigureAwait(false);
                if (zeroLine.ReadResults is not null)
                    await zeroLine.ReadResults(ct).ConfigureAwait(false);
            }, inspectionStop.Token, Timeout.InfiniteTimeSpan);
            var first = await Task.WhenAny(machining, inspection).ConfigureAwait(false);
            // Measurement/transport errors do not interrupt already running laser Tasks. Await their
            // normal end, then fail without Prc COMPLETE. A machining failure cancels the observer.
            if (first == machining && !machining.IsCompletedSuccessfully)
                inspectionStop.Cancel();
            await Task.WhenAll(machining, inspection).ConfigureAwait(false);
        }

        private readonly object _signalTraceLock = new();
        private readonly Dictionary<string, string> _signalTraceValues = new(StringComparer.OrdinalIgnoreCase);
        private string _logCycleId = "";
        public string LogCycleId => Volatile.Read(ref _logCycleId);

        private void BeginLogCycle(string kind)
        {
            Volatile.Write(ref _logCycleId, Guid.NewGuid().ToString("N"));
            LogEvent($"CYCLE_ACCEPTED Kind={kind}; Stage ABLE=1 START=0 COMPLETE=0.");
        }

        private void LogEvent(string detail)
        {
            try
            {
                log($"Cycle={LogCycleId} {detail}");
            }
            catch (Exception error)
            {
                try
                {
                    Trace.TraceError($"Stage sequence log failed: {error}");
                }
                catch
                {
                }
            }
        }

        private string SignalRoute(string id)
        {
            try
            {
                return describeSignal?.Invoke(id) ?? id;
            }
            catch
            {
                return id;
            }
        }

        private ST_STAGE_SIGNAL_SNAPSHOT ReadSignals()
        {
            var snapshot = read();
            // Observe the already collected cache. Repeated identical polls never create file records.
            // UpdatedAt is evidence in the message, not part of the change key.
            lock (_signalTraceLock)
            {
                Changed("MELSEC_CONNECTED", snapshot.Connected.ToString(), "");
                foreach (var(id, sample)in snapshot.Values)
                {
                    if (!id.StartsWith("HANDSHAKE_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = Convert.ToString(sample.Value, CultureInfo.InvariantCulture) ?? "<null>";
                    Changed(id, $"State={sample.State}; Value={value}; Error={sample.ErrorMessage}", $"; SampleAt={sample.UpdatedAt:O}");
                }
            }

            return snapshot;
            void Changed(string id, string signature, string evidence)
            {
                var initial = !_signalTraceValues.TryGetValue(id, out var previous);
                if (!initial && previous == signature)
                    return;
                _signalTraceValues[id] = signature;
                LogEvent($"SIGNAL_{(initial ? "INITIAL" : "CHANGED")} {SignalRoute(id)}; {signature}{evidence}");
            }
        }
    }

    public sealed record ST_STAGE_TPM_ACTIONS(Func<CancellationToken, Task> Prepare, Func<CancellationToken, Task> Measure, Func<CancellationToken, Task> Save, Func<Task> Release);
    public sealed record ST_STAGE_REVIEW_ACTIONS(Func<CancellationToken, Task> Prepare, Func<CancellationToken, Task> Measure, Func<CancellationToken, Task> Save);
    public sealed record ST_STAGE_ZERO_LINE_ACTIONS(Func<bool> Enabled, Func<CancellationToken, Task> Prepare, Func<CancellationToken, Task> Measure, Func<CancellationToken, Task> Save, Func<Task> Release)
    {
        public Func<CancellationToken, Task>? ReadResults { get; init; }
    }
    public sealed record ST_MANUAL_MOF_STATUS(bool IsRunning, bool RequiresStop, string Message, string ScriptDirectory, IReadOnlyDictionary<string, ST_SEQUENCE_ACTION_STATUS> Actions);
    public static class CManualMofActionIds
    {
        public static readonly string[] All = ["PREPARE", "BUILD", "UPLOAD", "ZERO", "RUN", "SCAN", "COMPLETE"];
    }

    public sealed record ST_STAGE_PROCESS_REQUEST(string ProcessId, string RecipeId, string GlassId, string LotId, IReadOnlyDictionary<string, string> Parameters);
    public sealed record ST_ZERO_LINE_PREWAIT_STATUS(string State, string Detail, int Group = 0, ST_ZERO_DEFENCE_POINT? FirstPoint = null, string RecipeId = "");
    /// <summary>AUTO-only Align observer. Reads cached flags and never joins the DoReady completion condition.</summary>
    public sealed class CZeroLinePrewaitMonitor(
        CInterfaceManager interfaces, 
        CRecipeManager recipes, 
        CSettingManager settings, 
        Func<bool> autoEnabled, 
        Func<string> selectedRecipe, 
        Action<string>? log = null
    )
    {
        public const string AlignWordId = CMelsec.StageAlignBusyWordId;
        public const int AlignBusyMask = 1 << 1;
        private readonly object _sync = new();
        private CancellationTokenSource? _stop;
        private Task? _worker;
        private ST_ZERO_LINE_PREWAIT_STATUS _status = new("WAIT", "Align 선대기 요청 대기");
        private ST_ZERO_LINE_PREWAIT_STATUS? _completed;
        private long _generation, _consumed;
        public ST_ZERO_LINE_PREWAIT_STATUS Status => Volatile.Read(ref _status);

        public void DiscardCompleted()
        {
            lock (_sync)
                _consumed = _generation;
        }

        public async Task WaitForFirstX(ST_ZERO_LINE_CYCLE_PLAN plan, CancellationToken token)
        {
            var end = DateTimeOffset.UtcNow.AddSeconds(30);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Status.State == "ERROR")
                    throw new InvalidOperationException(Status.Detail);
                lock (_sync)
                {
                    var first = plan.Points[0];
                    if (_completed is { } done && _generation > _consumed && done.RecipeId.Equals(plan.RecipeId, StringComparison.OrdinalIgnoreCase) && done.Group == plan.Group && done.FirstPoint == first && !interfaces.StageReviewSequences.IsActive)
                    {
                        _consumed = _generation;
                        return;
                    }
                }

                if (DateTimeOffset.UtcNow >= end)
                    throw new TimeoutException("첫 Review X 선대기 완료 대기: " + Status.Detail);
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }

        private void Waiting(string detail)
        {
            if (Status.State == "WAIT" && Status.Detail == detail)
                return;
            SetStatus(new("WAIT", detail));
        }

        private void SetStatus(ST_ZERO_LINE_PREWAIT_STATUS status)
        {
            var previous = Status;
            Volatile.Write(ref _status, status);
            if (previous == status)
                return;
            try
            {
                log?.Invoke($"{status.State}: {status.Detail}; Recipe={status.RecipeId}; Group={status.Group}; FirstPoint={status.FirstPoint}");
            }
            catch (Exception error)
            {
                try
                {
                    System.Diagnostics.Trace.TraceError($"Prewait log failed: {error}");
                }
                catch
                {
                }
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_worker is { IsCompleted: false })
                    return;
                _stop?.Dispose();
                _stop = new();
                _worker = Task.Run(() => Run(_stop.Token));
            }
        }

        public async Task Stop()
        {
            Task? task;
            lock (_sync)
            {
                _stop?.Cancel();
                task = _worker;
            }

            if (task is not null)
                await task.ConfigureAwait(false);
        }

        private async Task Run(CancellationToken token)
        {
            var handled = false;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var issued = false;
                    try
                    {
                        if (!autoEnabled())
                        {
                            handled = false;
                            DiscardCompleted();
                            if (Status.State != "STOPPED")
                                Waiting("AUTO Stage 요청 감시 대기");
                            continue;
                        }

                        var options = CRunOptionSettings.Read(settings);
                        if (!options.ZeroDefence)
                        {
                            handled = false;
                            DiscardCompleted();
                            Waiting("0선방어 OFF");
                            continue;
                        }

                        var values = interfaces.Melsec.GetCachedSnapshot([AlignWordId, CStageReviewSequences.MoveAble], out var map);
                        bool Signal(string id, double expected) => values.GetValueOrDefault(id)is { IsValid: true, Value: not null } v && Math.Abs(Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) - expected) < 1e-8;
                        var align = values.GetValueOrDefault(AlignWordId);
                        if (align?.IsValid != true || align.Value is null)
                        {
                            if (!handled)
                                Waiting("FAB3 ALIGN BUSY 상태 수신 대기");
                            continue;
                        }

                        var raw = Convert.ToInt32(align.Value, CultureInfo.InvariantCulture);
                        if ((raw & AlignBusyMask) == 0)
                        {
                            handled = false;
                            Waiting($"FAB3 ALIGN BUSY 대기 · 현재 {raw} / bit 1 OFF");
                            continue;
                        }

                        if (handled)
                            continue;
                        if (interfaces.StageReviewSequences.IsActive)
                        {
                            Waiting("공통 Process Move / IOF 사용 종료 대기");
                            continue;
                        }

                        if (!Signal(CStageReviewSequences.MoveAble, 1))
                        {
                            Waiting("WC124 이동 허가 대기");
                            continue;
                        }

                        handled = true;
                        var recipeId = selectedRecipe();
                        var recipe = (await recipes.LoadRecipes(token).ConfigureAwait(false)).Single(r => r.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase));
                        var parameters = settings.LoadSection(EN_SETTING_TAB.Option).Concat(settings.LoadSection(EN_SETTING_TAB.Motor)).ToArray();
                        var plan = new CZeroDefenceRecipePlan(recipe, parameters);
                        if (plan.Count is not (2 or 4))
                            throw new InvalidOperationException("0선방어 검사 개수/현재 그룹을 확인해 주세요.");
                        var group = Math.Clamp(options.CurrentGroup, 1, 8 / plan.Count);
                        var points = plan.ReadSelections(requireComplete: true);
                        var valid = CZeroDefenceTiming.Validate(plan.Count, points, plan.Profile);
                        if (!valid.IsValid)
                            throw new InvalidOperationException(valid.Message);
                        if (group != options.CurrentGroup)
                            settings.SetValue(EN_SETTING_TAB.Option, CRunOptionSettings.CurrentGroup, group.ToString(CultureInfo.InvariantCulture), token);
                        var first = points.Where(point => (point.HeadNo - 1) / plan.Count + 1 == group).OrderBy(point => point.StageY * plan.Profile.StageDirection).ThenBy(point => point.HeadNo).First();
                        var route = map.Single(row => row.Use && row.Id == CStageReviewSequences.MoveAble);
                        bool RequestActive()
                        {
                            var current = CRunOptionSettings.Read(settings);
                            return autoEnabled() && current.ZeroDefence && current.CurrentGroup == group && recipeId.Equals(selectedRecipe(), StringComparison.OrdinalIgnoreCase);
                        }
                        bool StartSignalsOn()
                        {
                            var current = interfaces.Melsec.GetCachedSnapshot([AlignWordId, CStageReviewSequences.MoveAble], out _);
                            var busy = current.GetValueOrDefault(AlignWordId);
                            var able = current.GetValueOrDefault(CStageReviewSequences.MoveAble);
                            return busy is { IsValid: true, Value: not null } && (Convert.ToInt32(busy.Value, CultureInfo.InvariantCulture) & AlignBusyMask) != 0
                                && able is { IsValid: true, Value: not null } && Math.Abs(Convert.ToDouble(able.Value, CultureInfo.InvariantCulture) - 1) < 1e-8;
                        }
                        SetStatus(new("RUNNING", "0선 첫 Review X 선대기 이동", group, first, recipeId));
                        // Once issued, Align/DoReady may finish independently. AUTO exit or option OFF cancels this owner.
                        var alreadyAtFirst = false;
                        using (var session = interfaces.StageReviewSequences.Open(route.DeviceNo, () => !RequestActive(), reviewXOnly: true))
                        {
                            var currentX = await session.ReadStationaryPosition(EN_MANUAL_STAGE_AXIS.ReviewX, token).ConfigureAwait(false);
                            if (!RequestActive() || !StartSignalsOn())
                            {
                                handled = false;
                                Waiting("선이동 직전 AUTO / FAB3 / ReviewAble 조건 재확인 대기");
                                continue;
                            }
                            var handshake = interfaces.Melsec.GetCachedSnapshot([CStageReviewSequences.MoveStart, CStageReviewSequences.MoveAck, CStageReviewSequences.MoveComplete], out _);
                            if (handshake.Count != 3 || handshake.Values.Any(value => value is not { IsValid: true, Value: not null } || Convert.ToDouble(value.Value, CultureInfo.InvariantCulture) != 0))
                            {
                                handled = false;
                                Waiting("이전 SDC 이동 요청 / 응답 복귀 대기");
                                continue;
                            }
                            alreadyAtFirst = Math.Abs(currentX - first.ReviewX) <= .0005 + 1e-9;
                            if (!alreadyAtFirst)
                            {
                                issued = true;
                                var motion = ST_REVIEW_MOTION_PARAMETERS.Read(parameters);
                                await session.Move(new(first.ReviewX, session.CurrentY, motion.Speed, Math.Max(1, plan.Profile.StageSpeed), motion.Acceleration, EN_STAGE_MOVE_MODE.ReviewX), token).ConfigureAwait(false);
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        if (!RequestActive())
                            throw new OperationCanceledException("0선 선대기 요청이 변경되었습니다.");
                        var done = new ST_ZERO_LINE_PREWAIT_STATUS("DONE", alreadyAtFirst ? "첫 검사 X 위치 일치 · 선이동 요청 생략" : "0선 첫 Review X 선대기·SDC 복귀 완료", group, first, recipeId);
                        lock (_sync)
                        {
                            _completed = done;
                            _generation++;
                        }

                        SetStatus(done);
                    }
                    catch (OperationCanceledException)when (!token.IsCancellationRequested)
                    {
                        SetStatus(new("STOPPED", "0선 선대기 중지"));
                    }
                    catch (Exception error)when (error is not OperationCanceledException)
                    {
                        SetStatus(new("ERROR", error.Message));
                        // A failed operation already published its own action error. Do not overwrite it,
                        // or the status of a different owner, with a plan/observer error.
                        if (!issued && !interfaces.StageReviewSequences.IsActive)
                        {
                            interfaces.StageReviewSequences.Actions.ResetToWaiting(["MOVE_TARGETS"]);
                            interfaces.StageReviewSequences.Actions.SetError("MOVE_TARGETS", error.Message);
                            interfaces.StageReviewSequences.ReportFailure(CSequenceFailureException.Action("MOVE_TARGETS", error));
                        }
                    }
                }
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Per-glass 0-line lifetime. Stage owns travel; no offset updates and no second Task RUN.</summary>
    public sealed class CZeroLineProcessCoordinator(
        ST_ZERO_LINE_CYCLE_PLAN plan, 
        CInterfaceManager interfaces, 
        CReviewManager review, 
        CSettingManager settings, 
        Func<ST_ZERO_LINE_CYCLE_PLAN, CancellationToken, Task> waitFirstX, 
        Action<string>? log = null
    )
    {
        private CStageReviewSequences.Session? _stage;
        private CReviewManager.ZeroLineVisionSession? _vision;
        private CancellationTokenSource? _stageStop;
        private Task? _stageRun;
        private bool _saved, _advanced;
        private ST_ZERO_LINE_CYCLE_STATUS _status = new(true, "WAIT", "0선 준비 대기", plan.Group, Array.AsReadOnly(plan.VisionPlan.Points.Select(p => new ST_ZERO_LINE_POINT_RESULT(p, "WAIT")).ToArray()))
        {
            Plan = plan
        };
        public ST_ZERO_LINE_CYCLE_PLAN Plan => plan;
        public ST_ZERO_LINE_CYCLE_STATUS Status => Volatile.Read(ref _status);

        private void Set(string state, string detail, IReadOnlyList<ST_ZERO_LINE_POINT_RESULT>? results = null)
        {
            Volatile.Write(ref _status, Status with { State = state, Detail = detail, Results = results is null ? Status.Results : Array.AsReadOnly(results.ToArray()) });
            log?.Invoke(detail);
        }

        public async Task Prepare(CancellationToken token)
        {
            try
            {
                var device = interfaces.Melsec.Map.Single(row => row.Id == CZeroLinePrewaitMonitor.AlignWordId).DeviceNo;
                var stageSimulation = interfaces.IsSimul(EN_EQP_MODULE.Melsec, device);
                interfaces.StageReviewSequences.ValidateMoveReadiness(device);
                Set("PREPARING", "첫 Review X 선대기·SDC 복귀 확인");
                await waitFirstX(plan, token).ConfigureAwait(false);
                _stage = interfaces.StageReviewSequences.Open(device, stageBufferOwner: true);
                if (Math.Abs(_stage.CurrentX - plan.Points[0].ReviewX) > .0005)
                    throw new InvalidOperationException("첫 Review X 선대기 위치가 변경되었습니다.");
                // In simulator, Stage DoReady COMPLETE represents arrival at the configured Ready Y.
                if (stageSimulation)
                    _stage.InitializeZeroLineReadyY(plan.Motion.StageStartY);
                Set("PREPARING", $"0선 Group {plan.Group} · {plan.Points.Count}개 Y 배열·IOF Buffer 준비");
                await _stage.PrepareIof(plan.Points.Select(p => new ST_STAGE_IOF_POINT(p.ReviewX, p.StageY)).ToArray(), true, token).ConfigureAwait(false);
                _vision = await review.OpenZeroLineVision(plan, stageSimulation, token).ConfigureAwait(false);
                Set("PREPARING", $"0선 Vision 1900 1회·전체 {plan.Points.Count}개 1001 준비");
                await _vision.PrepareFirst(token).ConfigureAwait(false);
                Set("READY", "0선 IOF·첫 X·Vision 준비 완료");
            }
            catch (Exception error)
            {
                Set(error is OperationCanceledException ? "STOPPED" : "ERROR", error.Message);
                await Release().ConfigureAwait(false);
                throw;
            }
        }

        public async Task Measure(CancellationToken token)
        {
            if (_stage is null || _vision is null || Status.State != "READY")
                throw new InvalidOperationException("0선 준비가 완료되지 않았습니다.");
            var results = Status.Results!.ToArray();
            _stageStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _stageRun = _stage.RunZeroLineCaptureMonitoring(plan, _stageStop.Token);
            try
            {
                for (var i = 0; i < results.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    results[i] = results[i] with
                    {
                        State = "RUNNING"
                    };
                    Set("RUNNING", $"0선 {i + 1}/{results.Length} · H{results[i].Point.HeadNo:00} Stage Y 촬영 위치 통과 대기", results.ToArray());
                    var segment = _stage.WaitZeroLineCapture(i + 1, _stageStop.Token);
                    var first = await Task.WhenAny(segment, _stageRun).ConfigureAwait(false);
                    if (first == _stageRun && (_stageRun.IsFaulted || _stageRun.IsCanceled))
                        await _stageRun.ConfigureAwait(false);
                    await segment.ConfigureAwait(false);
                    results[i] = results[i] with { State = "CAPTURED", Detail = "촬영 위치 통과 · 가공 완료/Stage Y 정지 후 결과 일괄 수신 대기" };
                    Set("RUNNING", $"0선 {i + 1}/{results.Length} · 촬영 위치 통과", results.ToArray());
                }

                await _stageRun.ConfigureAwait(false);
                Set("CAPTURED", $"0선 {results.Length}개 촬영 위치 통과 · 가공 완료/Stage Y 정지 대기", results.ToArray());
            }
            catch (Exception error)
            {
                for (var i = 0; i < results.Length; i++)
                    if (results[i].State == "RUNNING")
                        results[i] = results[i] with
                        {
                            State = error is OperationCanceledException ? "STOPPED" : "ERROR",
                            Detail = error.Message
                        };
                Set(error is OperationCanceledException ? "STOPPED" : "ERROR", error.Message, results.ToArray());
                _stageStop.Cancel();
                try
                {
                    await _stageRun.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                throw;
            }
        }

        public async Task ReadResults(CancellationToken token)
        {
            if (_stage is null || _vision is null || Status.State != "CAPTURED" || _stageRun?.IsCompletedSuccessfully != true)
                throw new InvalidOperationException("0선 촬영 위치 통과가 완료되지 않았습니다.");
            Set("WAIT_STAGE_END", "가공 완료 확인 · Stage Y 가공 완료 위치 정지 대기");
            try
            {
                await _stage.WaitZeroLineEndPosition(plan, token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Set(error is OperationCanceledException ? "STOPPED" : "ERROR", error.Message);
                throw; // No 1002 / 1003 before a confirmed stopped end position.
            }
            try
            {
                Set("READING", $"0선 {plan.Points.Count}개 · Vision 1002/1003 일괄 수신");
                var results = await _vision.MeasureAll(token).ConfigureAwait(false);
                Set("DONE", $"0선 {results.Count}개 결과 처리 완료 · NG {results.Count(point => point.IsNg)}", results);
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
                Set("STOPPED", "0선 일괄 결과 수신 중지");
                throw;
            }
            catch (Exception error)
            {
                var results = Status.Results!.Select(result => result with
                {
                    State = "ERROR",
                    Point = result.Point with { State = EN_REVIEW_POINT_STATE.Ng, Judge = "NG" },
                    Detail = error.Message
                }).ToArray();
                Set("DONE", "0선 일괄 결과 실패: " + error.Message, results);
            }
        }

        public async Task Save(CancellationToken token)
        {
            if (Status.State != "DONE" || _vision is null)
                throw new InvalidOperationException("0선 결과 처리가 완료되지 않았습니다.");
            await SaveFile(token).ConfigureAwait(false);
            // Inspection geometry stays frozen; the editable follow-up option is re-evaluated
            // at the final flag boundary, including NG confirmed after machining ended.
            var ngCount = Status.Results!.Count(point => point.IsNg);
            var follow = CRunOptionSettings.Read(settings).FollowUp && ngCount > 0;
            await PublishFollowUp(interfaces, follow, token).ConfigureAwait(false);
            if (!_advanced)
            {
                // A user INIT to another group wins over this old cycle's advancement.
                var current = CRunOptionSettings.Read(settings).CurrentGroup;
                if (current == plan.Group)
                    settings.SetValue(EN_SETTING_TAB.Option, CRunOptionSettings.CurrentGroup, (plan.Group % plan.TotalGroups + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _advanced = true;
            }
            // RunProcess invokes Save only after both machining and 0-line acquisition finish.
            // Preserve the report and final follow-up flag before failing the results Action.
            if (!follow && ngCount > 0)
                throw new InvalidDataException($"0선 NG {ngCount}개 / 후속검사 OFF · 가공 완료 후 알람 처리");
        }

        private async Task SaveFile(CancellationToken token)
        {
            if (_saved || _vision is null)
                return;
            var path = await _vision.Save(Status, token).ConfigureAwait(false);
            Volatile.Write(ref _status, Status with { ResultFile = path });
            _saved = true;
        }

        internal static async Task PublishFollowUp(CInterfaceManager interfaces, bool requested, CancellationToken token)
        {
            var value = requested ? 1 : 0;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.Run(() => interfaces.Melsec.WriteWord("SDC_REVIEW_RETRY_REQUEST", value, limit.Token), limit.Token).ConfigureAwait(false);
            var sample = await interfaces.Melsec.RefreshCachedValueAsync("SDC_REVIEW_RETRY_REQUEST", limit.Token).ConfigureAwait(false);
            if (!sample.IsValid || Convert.ToDouble(sample.Value) != value)
                throw new IOException("0선 후속 요청 W8218 읽기 확인 실패");
        }

        public async Task Release()
        {
            _stageStop?.Cancel();
            if (_stageRun is not null)
                try
                {
                    await _stageRun.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    log?.Invoke("0선 Stage 위치 감시 종료: " + error.Message);
                }

            try
            {
                if (!_saved && _vision is not null)
                {
                    if (Status.State is "READY" or "RUNNING")
                        Set("STOPPED", "Process 중단으로 0선 검사를 종료했습니다.");
                    using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await SaveFile(save.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _vision?.Dispose();
                _vision = null;
                _stage?.Dispose();
                _stage = null;
                _stageStop?.Dispose();
                _stageStop = null;
            }
        }
    }
}
