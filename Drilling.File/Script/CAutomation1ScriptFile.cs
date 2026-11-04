using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Drilling.Common.Recipe;
using Drilling.Common.Automation;
using Drilling.Common.Station;

namespace Drilling.File.Script;
public sealed class CAutomation1ScriptFile : CAutomationScriptFileBase
{
    public const string DefaultScriptFileName = "PROCESS.ascript";
    public const string ProcessCoordinateListFileName = "PROCESS.txt";
    public const string ProcessShotCoordinateCsvFileName = "PROCESS_COORDINATES.csv";
    private const string StandardDirectoryName = "Standard";
    private const string BufferedRunDirectoryName = "BufferedRun";
    private const string Automation1SimulatorUseKey = "AUTOMATION1_SIMULATOR_USE";
    private const int Automation1SimulatorWaitMilliseconds = 5;
    private const double MinimumEncoderScaleMagnitude = 0.000001;
    private const double MofSetupDelayMsec = 0.5;
    private enum EN_SCRIPT_FOLLOW_AXIS
    {
        X,
        Y
    }

    private readonly string _scriptDirectory;
    public CAutomation1ScriptFile(string? scriptDirectory = null)
    {
        var resolvedDirectory = string.IsNullOrWhiteSpace(scriptDirectory) ? Path.Combine(AppContext.BaseDirectory, "Data", "Script") : scriptDirectory;
        _scriptDirectory = Path.GetFullPath(resolvedDirectory);
    }

    public override string ScriptFileName
    {
        get
        {
            return DefaultScriptFileName;
        }
    }

    public override CAutomation1ScriptBase Create(string? fileName = null)
    {
        return new CAutomation1Script(_scriptDirectory, NormalizeFileName(fileName));
    }

    public override ST_SCRIPT_BUILD_RESULT Build(ST_PROCESS_MODEL processModel, CancellationToken cancellationToken = default)
    {
        return Build(processModel, StandardDirectoryName, cancellationToken);
    }

    public override ST_SCRIPT_BUILD_RESULT Build(ST_PROCESS_MODEL processModel, string subDirectoryName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var createdAt = DateTimeOffset.Now;
        var readyRunId = CMofReadiness.CreateRunId();
        var shotMaskingPlan = CRecipeShotMaskingPlan.Build(processModel.Parameters);
        bool ShouldIncludeItem(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
        {
            return ReadBool(processModel.Parameters, true, CreateHeadKeys(head.HeadNo, "USE"));
        }

        ST_HEAD_PROCESS_PLAN SelectItem(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
        {
            var processShots = head.Shots
                .Where(shot => !shotMaskingPlan.EvaluateShot(shot).IsChessSkipped)
                .ToArray();
            return head with { Shots = processShots };
        }

        bool ShouldIncludeHead(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
        {
            return head.Shots.Count > 0;
        }

        var activeHeads = processModel.HeadPlans.Where(ShouldIncludeItem).Select(SelectItem).Where(ShouldIncludeHead).ToArray();
        ValidateActiveHeads(activeHeads);
        var targetDirectory = ResolveScriptBuildDirectory(subDirectoryName, createdAt, processModel.GlassId);
        var isBufferedRun = IsBufferedRunDirectory(subDirectoryName);
        Directory.CreateDirectory(targetDirectory);
        var headScripts = new List<ST_HEAD_SCRIPT_RESULT>();

        var shotCoordinateCsvLines = BuildShotCoordinateCsvLines(processModel, createdAt, activeHeads);
        var shotCoordinateCsvPath = Path.Combine(targetDirectory, ProcessShotCoordinateCsvFileName);
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.File.WriteAllLines(shotCoordinateCsvPath, shotCoordinateCsvLines, Encoding.UTF8);

        // Every build has an isolated directory, and upload/execution use only these manifest paths.
        foreach (var head in activeHeads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headLines = BuildHeadLines(processModel, head, createdAt, isBufferedRun, readyRunId);
            var headFilePath = Path.Combine(targetDirectory, head.ScriptFileName);
            System.IO.File.WriteAllLines(headFilePath, headLines, Encoding.UTF8);
            headScripts.Add(new ST_HEAD_SCRIPT_RESULT(head.HeadNo, head.AutomationNo, head.TaskNo, head.ScriptFileName, headFilePath, head.Shots.Count, headLines.Count, createdAt));
        }

        var lines = BuildCoordinateListLines(processModel, createdAt, activeHeads, headScripts);
        var filePath = Path.Combine(targetDirectory, ProcessCoordinateListFileName);
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.File.WriteAllLines(filePath, lines, Encoding.UTF8);
        int SelectTotalShots(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
        {
            return script.TotalShots;
        }

        return new ST_SCRIPT_BUILD_RESULT(ProcessCoordinateListFileName, filePath, headScripts.Sum(SelectTotalShots), createdAt, headScripts)
            { MofReadyRunId = readyRunId };
    }

    public override ST_SCRIPT_BUILD_RESULT LoadLatest(ST_PROCESS_MODEL processModel, string subDirectoryName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (subDirectoryName is not (StandardDirectoryName or BufferedRunDirectoryName))
            throw new ArgumentException("AUTO 스크립트 실행 방식이 올바르지 않습니다.", nameof(subDirectoryName));
        var root = Path.Combine(_scriptDirectory, subDirectoryName);
        if (!Directory.Exists(root))
            throw new FileNotFoundException("마지막 AUTO 스크립트 폴더가 없습니다.", root);
        // Select a build as one set, never the newest individual file per head.
        // A partially written newest set must fail, rather than silently run an older set.
        var directory = Directory.EnumerateDirectories(root).Select(path => new DirectoryInfo(path))
            .Where(info => info.Name.Length >= 15 && DateTime.TryParseExact(info.Name[..15], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderByDescending(info => info.Name[..15], StringComparer.Ordinal).ThenByDescending(info => info.CreationTimeUtc)
            .ThenByDescending(info => info.Name, StringComparer.Ordinal).FirstOrDefault()?.FullName
            ?? throw new FileNotFoundException("실행할 AUTO 스크립트 생성 회차가 없습니다.", root);
        var manifest = Path.Combine(directory, ProcessCoordinateListFileName);
        var lines = System.IO.File.ReadAllLines(manifest);
        string Header(string key) => lines.FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..]
            ?? throw new InvalidDataException($"스크립트 목록 정보가 없습니다: {key} / {manifest}");
        if (!Header("RecipeId").Equals(processModel.RecipeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"마지막 스크립트의 Recipe({Header("RecipeId")})가 현재 Recipe({processModel.RecipeId})와 다릅니다. {manifest}");
        var createdAt = DateTimeOffset.ParseExact(Header("CreatedAt"), "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var heads = new List<ST_HEAD_SCRIPT_RESULT>();
        long? readyRunId = null;
        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headMatch = Regex.Match(lines[i], @"^\[H(\d{2})\]$");
            if (!headMatch.Success) continue;
            var headNo = int.Parse(headMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var metadata = Regex.Match(i + 1 < lines.Length ? lines[++i] : "", @"^Automation=(\d+) Task=(\d+) Script=(\S+) Shots=(\d+)$");
            if (!metadata.Success || heads.Any(head => head.HeadNo == headNo))
                throw new InvalidDataException($"HEAD {headNo:00} 스크립트 목록이 올바르지 않습니다: {manifest}");
            var controller = int.Parse(metadata.Groups[1].Value, CultureInfo.InvariantCulture);
            var task = int.Parse(metadata.Groups[2].Value, CultureInfo.InvariantCulture);
            var fileName = metadata.Groups[3].Value;
            var shots = int.Parse(metadata.Groups[4].Value, CultureInfo.InvariantCulture);
            var plan = processModel.HeadPlans.SingleOrDefault(head => head.HeadNo == headNo);
            if (plan is null || plan.AutomationNo != controller || plan.TaskNo != task || Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".ascript", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"HEAD {headNo:00}의 현재 Controller/Task와 저장 스크립트 정보를 확인해 주세요.");
            var filePath = Path.Combine(directory, fileName);
            long? headRunId = null;
            var lineCount = 0;
            foreach (var line in System.IO.File.ReadLines(filePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineCount++;
                if (lineCount > 16) continue;
                var marker = Regex.Match(line, @"^// MOF Ready: GlobalInteger\[\d+\], RunId=(\d+)$");
                if (marker.Success) headRunId = long.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            if (headRunId is not > 0 || readyRunId is not null && readyRunId != headRunId)
                throw new InvalidDataException($"HEAD {headNo:00}의 MOF 실행번호가 없거나 다른 회차와 섞였습니다: {filePath}");
            readyRunId = headRunId;
            heads.Add(new(headNo, controller, task, fileName, filePath, shots, lineCount, createdAt));
        }
        var expectedHeads = processModel.HeadPlans.Where(head => ReadBool(processModel.Parameters, true, CreateHeadKeys(head.HeadNo, "USE")) && head.Shots.Count > 0).Select(head => head.HeadNo).Order().ToArray();
        if (heads.Count != int.Parse(Header("HeadScriptCount"), CultureInfo.InvariantCulture) || heads.Count == 0
            || !heads.Select(head => head.HeadNo).Order().SequenceEqual(expectedHeads)
            || heads.Select(head => (head.AutomationNo, head.TaskNo)).Distinct().Count() != heads.Count)
            throw new InvalidDataException("마지막 스크립트 세트의 Head/Task 구성이 현재 가공 대상과 일치하지 않습니다: " + manifest);
        return new(ProcessCoordinateListFileName, manifest, heads.Sum(head => head.TotalShots), createdAt, heads) { MofReadyRunId = readyRunId!.Value };
    }

    private static IReadOnlyList<string> BuildShotCoordinateCsvLines(ST_PROCESS_MODEL processModel, DateTimeOffset createdAt, IReadOnlyList<ST_HEAD_PROCESS_PLAN> activeHeads)
    {
        var lines = new List<string>();
        var common = new Dictionary<string, string>(processModel.Parameters, StringComparer.OrdinalIgnoreCase);
        var simulatorMode = ReadBool(common, false, Automation1SimulatorUseKey);
        var maskingPlan = CRecipeShotMaskingPlan.Build(common);
        lines.Add(CreateCsvLine(

            "RecipeId",

            "GlassId",

            "CreatedAt",

            "Automation1SimulatorUse",

            "HeadNo",

            "AutomationNo",

            "TaskNo",

            "ScriptFileName",

            "ShotName",

            "CellNo",

            "ShotRowNo",

            "ShotColNo",

            "DesignX",

            "DesignY",

            "ScannerGx",

            "ScannerGy",

            "RecipeOffsetX",

            "RecipeOffsetY",

            "HeadDefaultOffsetX",

            "HeadDefaultOffsetY",

            "FirstBeamOffsetX",

            "FirstBeamOffsetY",

            "ReviewOffsetX",

            "ReviewOffsetY",

            "AlignOffsetX",

            "AlignOffsetY",

            "ShiftOffset X",

            "ShiftOffset Y",

            "TotalScannerX",

            "TotalScannerY",

            "가공여부"
        ));

        foreach (var head in activeHeads)
        {
            var orderedShots = head.Shots
                .OrderBy(ShotCellNo)
                .ThenBy(ShotRowNo)
                .ThenBy(ShotColumnNo)
                .ThenBy(ShotHeadNo)
                .ToArray();
            foreach (var point in orderedShots)
            {
                var totalScannerY = ReadScriptScannerY(point, simulatorMode);
                lines.Add(CreateCsvLine(

                    processModel.RecipeId,

                    processModel.GlassId,

                    createdAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),

                    simulatorMode ? "1" : "0",

                    head.HeadNo.ToString(CultureInfo.InvariantCulture),

                    head.AutomationNo.ToString(CultureInfo.InvariantCulture),

                    head.TaskNo.ToString(CultureInfo.InvariantCulture),

                    head.ScriptFileName,

                    FormatShotName(point),

                    point.CellNo.ToString(CultureInfo.InvariantCulture),

                    point.GroupRow.ToString(CultureInfo.InvariantCulture),

                    point.GroupColumn.ToString(CultureInfo.InvariantCulture),

                    Format(point.DesignX),

                    Format(point.DesignY),

                    Format(point.ScannerGx),

                    Format(point.ScannerGy),

                    Format(point.RecipeOffsetX),

                    Format(point.RecipeOffsetY),

                    Format(point.HeadDefaultOffsetX),

                    Format(point.HeadDefaultOffsetY),

                    Format(point.FirstBeamOffsetX),

                    Format(point.FirstBeamOffsetY),

                    Format(point.ReviewOffsetX),

                    Format(point.ReviewOffsetY),

                    CAlignShotOffsetData.FormatValue(point.AlignScannerOffsetX),

                    CAlignShotOffsetData.FormatValue(point.AlignScannerOffsetY),

                    Format(point.ShiftScannerOffsetX),

                    Format(point.ShiftScannerOffsetY),

                    Format(point.ScannerGx),

                    Format(totalScannerY),

                    maskingPlan.EvaluateShot(point).ShouldSkipLaser ? "미가공" : "가공"
                ));
            }
        }

        return lines;
    }

    private static int ShotCellNo(ST_HEAD_SHOT_POINT point)
    {
        return point.CellNo;
    }

    private static int ShotRowNo(ST_HEAD_SHOT_POINT point)
    {
        return point.GroupRow;
    }

    private static int ShotColumnNo(ST_HEAD_SHOT_POINT point)
    {
        return point.GroupColumn;
    }

    private static int ShotHeadNo(ST_HEAD_SHOT_POINT point)
    {
        return point.HeadNo;
    }

    private static string ReadParameterText(IReadOnlyDictionary<string, string> parameters, string key, string defaultValue)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;
    }

    private static IReadOnlyList<string> BuildCoordinateListLines(ST_PROCESS_MODEL processModel, DateTimeOffset createdAt, IReadOnlyList<ST_HEAD_PROCESS_PLAN> activeHeads, IReadOnlyList<ST_HEAD_SCRIPT_RESULT> headScripts)
    {
        var lines = new List<string>();
        int SelectHeadNo(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
        {
            return script.HeadNo;
        }

        var scriptByHead = headScripts.ToDictionary(SelectHeadNo);
        lines.Add("Laser Drilling Process Coordinate List");
        lines.Add($"ProcessId={processModel.ProcessId}");
        lines.Add($"RecipeId={processModel.RecipeId}");
        lines.Add($"GlassId={processModel.GlassId}");
        lines.Add($"CreatedAt={createdAt:yyyy-MM-dd HH:mm:ss.fff zzz}");
        lines.Add($"ShiftOffsetX={ReadParameterText(processModel.Parameters, "SHIFT_OFFSET_X", "0")}");
        lines.Add($"ShiftOffsetY={ReadParameterText(processModel.Parameters, "SHIFT_OFFSET_Y", "0")}");
        int SelectTotalShots(global::Drilling.Common.Station.ST_HEAD_SCRIPT_RESULT script)
        {
            return script.TotalShots;
        }

        lines.Add($"TotalShots={headScripts.Sum(SelectTotalShots)}");
        lines.Add($"HeadScriptCount={headScripts.Count}");
        lines.Add("");
        foreach (var head in activeHeads)
        {
            var script = scriptByHead[head.HeadNo];
            lines.Add($"[H{head.HeadNo:00}]");
            lines.Add($"Automation={script.AutomationNo} Task={script.TaskNo} Script={script.FileName} Shots={script.TotalShots}");
            AppendHeadShotList(lines, head);
            lines.Add("");
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildHeadLines(ST_PROCESS_MODEL processModel, ST_HEAD_PROCESS_PLAN head, DateTimeOffset createdAt, bool bufferedRun, long readyRunId)
    {
        var lines = new List<string>();
        var common = new Dictionary<string, string>(processModel.Parameters, StringComparer.OrdinalIgnoreCase);
        var simulatorMode = ReadBool(common, false, Automation1SimulatorUseKey);
        var xAxis = ReadText(common, "GX", CreateHeadKeys(head.HeadNo, "X_AXIS"));
        xAxis = ValidateAeroScriptAxisName("X_AXIS", xAxis);
        var yAxis = ReadText(common, "GY", CreateHeadKeys(head.HeadNo, "Y_AXIS"));
        yAxis = ValidateAeroScriptAxisName("Y_AXIS", yAxis);
        var laserAxis = ReadText(common, xAxis, CreateHeadKeys(head.HeadNo, "LASER_AXIS"));
        if (!simulatorMode)
        {
            laserAxis = ValidateAeroScriptAxisName("LASER_AXIS", laserAxis);
        }

        var encoderAxis = ReadText(common, xAxis, CreateHeadKeys(head.HeadNo, "ENCODER_AXIS"));
        if (!simulatorMode)
        {
            encoderAxis = ValidateAeroScriptAxisName("ENCODER_AXIS", encoderAxis);
        }

        var encoderScaleKeys = CreateEncoderScaleKeys(head.HeadNo);
        var encoderScaleName = ResolveParameterName(common, encoderScaleKeys, $"H{head.HeadNo:00}_ENCODER_SCALE");
        var encoderScale = ReadDouble(common, 16000.0, encoderScaleKeys);
        if (!simulatorMode)
        {
            ValidateEncoderScale(encoderScaleName, encoderScale);
        }

        var encoderFeedbackFlip = ReadOnOff(common, false, CreateEncoderFeedbackFlipKeys(head.HeadNo));
        var scannerCoordinatedRampRate = ResolveCoordinatedRampRate(head.ScannerXRampRate, head.ScannerYRampRate);
        var motionUpdateRate = ReadInt(common, 100, "SCAN_MOTION_UPDATE_RATE");
        var executeLineCount = ReadInt(common, 110, "SCAN_EXECUTE_NUM_LINES");
        var settlingDelaySeconds = ReadDelaySeconds(common, 0.0, "PROCESS_SETTLING_TIME_MS");
        var laserOutputRate = ReadDouble(common, 10.0, "LASER_OUTPUT_RATE");
        var laserOutputPeriodUsec = CalculateLaserPeriod(head.FrequencyKhz);
        var shotDelaySeconds = CalculateShotDelaySeconds(head.ShotCount, head.FrequencyKhz);
        var laserOnDelayMsec = ReadDouble(common, 0.8, CreateHeadKeys(head.HeadNo, "LASER_ON_DELAY"));
        var laserOffDelayMsec = ReadDouble(common, 0.2, CreateHeadKeys(head.HeadNo, "LASER_OFF_DELAY"));
        var linePreApproachUse = ReadBool(common, false, "SCAN_LINE_PRE_APPROACH_USE");
        var linePreApproachDistance = Math.Abs(ReadDouble(common, 0.0, "SCAN_LINE_PRE_APPROACH_DISTANCE"));
        var linePreApproachDefaultAxis = ResolveMachiningAxis(head.Shots, simulatorMode);
        var linePreApproachLimit = ReadHeadScanFieldHalfWidth(common, head.HeadNo);
        lines.Add("// Laser Drilling Automation1 head process script");
        lines.Add($"// ProcessId={processModel.ProcessId}");
        lines.Add($"// RecipeId={processModel.RecipeId}");
        lines.Add($"// Head=H{head.HeadNo:00}");
        lines.Add($"// Task={head.TaskNo}");
        var readyIndex = CMofReadiness.VariableIndex(head.TaskNo);
        lines.Add($"// MOF Ready: GlobalInteger[{readyIndex}], RunId={readyRunId.ToString(CultureInfo.InvariantCulture)}");
        lines.Add($"// CreatedAt={createdAt:yyyy-MM-dd HH:mm:ss.fff zzz}");
        lines.Add($"// EncoderFeedbackFlip={(encoderFeedbackFlip ? "ON" : "OFF")}");
        lines.Add($"// LaserOnDelay={Format(laserOnDelayMsec)} msec LaserOffDelay={Format(laserOffDelayMsec)} msec");
        if (linePreApproachUse && linePreApproachDistance > 0.000001)
        {
            lines.Add($"// ScanLinePreApproach=ON Distance={Format(linePreApproachDistance)} mm");
        }

        if (simulatorMode)
        {
            lines.Add("// Automation1 simulator mode: scanner motion only");
        }

        if (!bufferedRun)
        {
            lines.Add("program");
        }

        lines.Add($"$iglobal[{readyIndex}] = 0");
        lines.Add("IfovOff()");
        lines.Add("SetupTaskDistanceUnits(DistanceUnits.Primary)");
        lines.Add("SetupTaskTimeUnits(TimeUnits.Seconds)");
        lines.Add("VelocityBlendingOff()");
        lines.Add("SetupTaskWaitMode(WaitMode.Auto)");
        lines.Add("SetupTaskTargetMode(TargetMode.Absolute)");
        if (!simulatorMode)
        {
            lines.Add($"GalvoConfigureLaserOutputPeriod({laserAxis}, 0)");
            lines.Add($"GalvoConfigureLaser1PulseWidth({laserAxis}, 0)");
            lines.Add($"GalvoConfigureLaserMode({laserAxis}, 0)");
        }

        lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.MotionUpdateRate, {motionUpdateRate})");
        lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.ExecuteNumLines, {executeLineCount})");
        if (!simulatorMode)
        {
            lines.Add($"PsoReset({laserAxis})");
        }

        lines.Add($"SetupCoordinatedAccelLimit({Format(head.ScannerAccLimit)},{Format(head.ScannerAccLimit)})");
        lines.Add($"SetupAxisRampType([{xAxis},{yAxis}], RampType.Sine)");
        lines.Add($"SetupAxisRampValue([{xAxis}], RampMode.Rate, {Format(head.ScannerXRampRate)})");
        lines.Add($"SetupAxisRampValue([{yAxis}], RampMode.Rate, {Format(head.ScannerYRampRate)})");
        lines.Add("SetupCoordinatedRampType(RampType.Sine)");
        lines.Add($"SetupCoordinatedRampValue(RampMode.Rate,{Format(scannerCoordinatedRampRate)})");
        lines.Add($"SetupCoordinatedSpeed({Format(ToAutomationSpeed(head.MarkSpeed))})");
        lines.Add($"SetupAxisSpeed({xAxis}, {Format(ToAutomationSpeed(head.JumpSpeed))})");
        lines.Add($"SetupAxisSpeed({yAxis}, {Format(ToAutomationSpeed(head.JumpSpeed))})");
        // DOE Z must already match the recipe target through the START interlock.
        // Process scripts configure and move scanner X/Y only.
        lines.Add($"MoveRapid([{xAxis},{yAxis}], [0,0])");
        if (!simulatorMode)
        {
            // Center before zeroing auxiliary feedback, then enable MOF tracking.
            // Emit both setup pauses in milliseconds, without seconds conversion.
            lines.Add($"DriveSetAuxiliaryFeedback({encoderAxis}, 0)");
            lines.Add($"MoveDelay([{xAxis},{yAxis}], {Format(MofSetupDelayMsec)})");
            // Encoder scale is recipe-managed at 0.000001 resolution; the standard Automation format is sufficient here.
            lines.Add($"GalvoEncoderScaleFactorSet({encoderAxis}, ParameterGetAxisValue({encoderAxis}, AxisParameter.CountsPerUnit) / {Format(encoderScale)})");
            lines.Add($"MoveDelay([{xAxis},{yAxis}], {Format(MofSetupDelayMsec)})");
            lines.Add($"GalvoConfigureLaserOutputPeriod({laserAxis}, {Format(laserOutputPeriodUsec)})");
            lines.Add($"AnalogOutputSet({laserAxis}, 0, {Format(laserOutputRate)})");
            // Keep 100% electrical duty; optical power is controlled separately with the ATT.
            lines.Add($"GalvoConfigureLaser1PulseWidth({laserAxis}, {Format(laserOutputPeriodUsec)})");
            lines.Add($"GalvoConfigureLaserDelays({laserAxis}, 0.000, 0.000)");
            lines.Add($"GalvoConfigureLaserMode({laserAxis}, 0)");
        }

        // Complete initialization and scanner centering before publishing this build's readiness.
        // Publish before AppendStartFollowMoves: that routine can already wait for Stage encoder motion.
        lines.Add($"WaitForMotionDone([{xAxis},{yAxis}])");
        lines.Add($"$iglobal[{readyIndex}] = {readyRunId.ToString(CultureInfo.InvariantCulture)}");
        if (!simulatorMode)
            AppendStartFollowMoves(lines, common, head, xAxis, yAxis, encoderAxis, encoderScale, encoderFeedbackFlip);

        var maskingPlan = CRecipeShotMaskingPlan.Build(common);
        ST_HEAD_SHOT_POINT? previousPoint = null;
        for (var index = 0; index < head.Shots.Count; index++)
        {
            var point = head.Shots[index];
            var scannerGy = ReadScriptScannerY(point, simulatorMode);
            if (!simulatorMode && previousPoint is not null)
            {
                AppendFollowTransitionMoves(lines, common, head, xAxis, yAxis, encoderAxis, encoderFeedbackFlip, previousPoint, point);
            }

            if (linePreApproachUse && linePreApproachDistance > 0.000001 && IsScanLineFirstShot(previousPoint, point))
            {
                var nextPoint = FindNextPointInSameScanLine(head.Shots, index, point);
                var linePreApproachAxis = ResolveLineMachiningAxis(point, nextPoint, linePreApproachDefaultAxis, simulatorMode);
                AppendLinePreApproachMove(lines, xAxis, yAxis, point, nextPoint, linePreApproachAxis, linePreApproachDistance, linePreApproachLimit, simulatorMode);
            }

            lines.Add($"MoveRapid([{xAxis},{yAxis}], [{Format(point.ScannerGx)},{Format(scannerGy)}])");
            if (simulatorMode)
            {
                lines.Add($"wait(false, {Automation1SimulatorWaitMilliseconds})");
            }
            else
            {
                AppendEncoderWait(lines, encoderAxis, point.EncoderWaitPosition, encoderFeedbackFlip);
                if (settlingDelaySeconds > 0.0)
                {
                    lines.Add($"MoveDelay([{xAxis},{yAxis}], {Format(settlingDelaySeconds)})");
                }

                var maskingState = maskingPlan.EvaluateShot(point);
                if (!maskingState.ShouldSkipLaser)
                {
                    AppendSingleAxisMoveDelay(lines, xAxis, laserOnDelayMsec);
                    lines.Add($"GalvoLaserOutput({laserAxis},GalvoLaser.On)");
                    lines.Add($"MoveDelay([{xAxis},{yAxis}], {Format(shotDelaySeconds)})");
                    lines.Add($"GalvoLaserOutput({laserAxis},GalvoLaser.Off)");
                    AppendSingleAxisMoveDelay(lines, xAxis, laserOffDelayMsec);
                }
            }

            previousPoint = point;
        }

        if (!simulatorMode)
        {
            lines.Add($"MoveDelay([{xAxis},{yAxis}], 0.001)");
            lines.Add($"GalvoLaserOutput({laserAxis},GalvoLaser.Off)");
            // Normal-completion cleanup inside the generated Automation1 script.
            lines.Add($"GalvoEncoderScaleFactorSet({encoderAxis}, 0)");
            lines.Add($"GalvoEncoderScaleFactorSet({xAxis}, 0)");
            lines.Add($"GalvoEncoderScaleFactorSet({yAxis}, 0)");
        }

        lines.Add($"MoveRapid([{xAxis},{yAxis}], [0,0])");
        if (bufferedRun)
        {
            lines.Add("CommandQueueStop()");
        }
        else
        {
            lines.Add("end");
        }

        return lines;
    }

    private static EN_SCRIPT_FOLLOW_AXIS ResolveMachiningAxis(IReadOnlyList<ST_HEAD_SHOT_POINT> shots, bool simulatorMode)
    {
        if (shots.Count <= 1)
        {
            return EN_SCRIPT_FOLLOW_AXIS.X;
        }

        var minGx = shots[0].ScannerGx;
        var maxGx = shots[0].ScannerGx;
        var minGy = ReadScriptScannerY(shots[0], simulatorMode);
        var maxGy = ReadScriptScannerY(shots[0], simulatorMode);
        for (var index = 1; index < shots.Count; index++)
        {
            var shot = shots[index];
            var scannerY = ReadScriptScannerY(shot, simulatorMode);
            minGx = Math.Min(minGx, shot.ScannerGx);
            maxGx = Math.Max(maxGx, shot.ScannerGx);
            minGy = Math.Min(minGy, scannerY);
            maxGy = Math.Max(maxGy, scannerY);
        }

        return maxGy - minGy > maxGx - minGx ? EN_SCRIPT_FOLLOW_AXIS.Y : EN_SCRIPT_FOLLOW_AXIS.X;
    }

    private static EN_SCRIPT_FOLLOW_AXIS ResolveLineMachiningAxis(ST_HEAD_SHOT_POINT targetPoint, ST_HEAD_SHOT_POINT? nextPoint, EN_SCRIPT_FOLLOW_AXIS fallbackAxis, bool simulatorMode)
    {
        if (nextPoint is null)
        {
            return fallbackAxis;
        }

        var deltaX = Math.Abs(nextPoint.ScannerGx - targetPoint.ScannerGx);
        var deltaY = Math.Abs(ReadScriptScannerY(nextPoint, simulatorMode) - ReadScriptScannerY(targetPoint, simulatorMode));
        if (Math.Max(deltaX, deltaY) <= 0.000001)
        {
            return fallbackAxis;
        }

        return deltaY > deltaX ? EN_SCRIPT_FOLLOW_AXIS.Y : EN_SCRIPT_FOLLOW_AXIS.X;
    }

    private static bool IsScanLineFirstShot(ST_HEAD_SHOT_POINT? previousPoint, ST_HEAD_SHOT_POINT currentPoint)
    {
        if (previousPoint is null)
        {
            return true;
        }

        return Math.Abs(currentPoint.EncoderWaitPosition - previousPoint.EncoderWaitPosition) > 0.000001;
    }

    private static ST_HEAD_SHOT_POINT? FindNextPointInSameScanLine(IReadOnlyList<ST_HEAD_SHOT_POINT> shots, int currentIndex, ST_HEAD_SHOT_POINT currentPoint)
    {
        var nextIndex = currentIndex + 1;
        if (nextIndex >= shots.Count)
        {
            return null;
        }

        var nextPoint = shots[nextIndex];
        if (Math.Abs(nextPoint.EncoderWaitPosition - currentPoint.EncoderWaitPosition) > 0.000001)
        {
            return null;
        }

        return nextPoint;
    }

    private static void AppendLinePreApproachMove(
        List<string> lines, 
        string xAxis, 
        string yAxis, 
        ST_HEAD_SHOT_POINT targetPoint, 
        ST_HEAD_SHOT_POINT? nextPoint, 
        EN_SCRIPT_FOLLOW_AXIS machiningAxis, 
        double distance, 
        double scanFieldHalfWidth, 
        bool simulatorMode
    )
    {
        var targetAxisPosition = ReadScriptAxisPosition(targetPoint, machiningAxis, simulatorMode);
        var direction = ResolveLinePreApproachDirection(targetPoint, nextPoint, machiningAxis, simulatorMode);
        var preAxisPosition = Clamp(targetAxisPosition - (direction * distance), -scanFieldHalfWidth, scanFieldHalfWidth);
        if (Math.Abs(preAxisPosition - targetAxisPosition) <= 0.000001)
        {
            return;
        }

        var scannerX = machiningAxis == EN_SCRIPT_FOLLOW_AXIS.X ? preAxisPosition : targetPoint.ScannerGx;
        var targetScannerY = ReadScriptScannerY(targetPoint, simulatorMode);
        var scannerY = machiningAxis == EN_SCRIPT_FOLLOW_AXIS.Y ? preAxisPosition : targetScannerY;
        lines.Add($"MoveRapid([{xAxis},{yAxis}], [{Format(scannerX)},{Format(scannerY)}])");
    }

    private static double ResolveLinePreApproachDirection(ST_HEAD_SHOT_POINT targetPoint, ST_HEAD_SHOT_POINT? nextPoint, EN_SCRIPT_FOLLOW_AXIS machiningAxis, bool simulatorMode)
    {
        if (nextPoint is not null)
        {
            var delta = ReadScriptAxisPosition(nextPoint, machiningAxis, simulatorMode) - ReadScriptAxisPosition(targetPoint, machiningAxis, simulatorMode);
            if (Math.Abs(delta) > 0.000001)
            {
                return delta > 0.0 ? 1.0 : -1.0;
            }
        }

        return 1.0;
    }

    private static double ReadHeadScanFieldHalfWidth(IReadOnlyDictionary<string, string> common, int headNo)
    {
        var scanFieldWidth = Math.Abs(ReadDouble(common, 110.0, CreateHeadKeys(headNo, "SCAN_FIELD_WIDTH_X", "HEAD_FIELD_WIDTH_X")));
        if (scanFieldWidth <= 0.000001)
        {
            scanFieldWidth = 110.0;
        }

        return scanFieldWidth / 2.0;
    }

    private static double ReadScriptAxisPosition(ST_HEAD_SHOT_POINT point, EN_SCRIPT_FOLLOW_AXIS followAxis, bool simulatorMode)
    {
        if (followAxis == EN_SCRIPT_FOLLOW_AXIS.X)
        {
            return point.ScannerGx;
        }

        return ReadScriptScannerY(point, simulatorMode);
    }

    private static double ReadScriptScannerY(ST_HEAD_SHOT_POINT point, bool simulatorMode)
    {
        return simulatorMode ? point.FinalScannerOffsetY + point.ShiftScannerOffsetY : point.ScannerGy;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private string ResolveScriptBuildDirectory(
        string subDirectoryName,
        DateTimeOffset createdAt,
        string glassId)
    {
        var requested = subDirectoryName.Replace('\\', '/').Trim('/');
        // Manual MOF uses the same build naming and contents in its own two mode folders.
        var normalized = requested.Equals("ManualMOF/Buffered", StringComparison.OrdinalIgnoreCase) ? Path.Combine("ManualMOF", "Buffered")
            : requested.Equals("ManualMOF/Standard", StringComparison.OrdinalIgnoreCase) ? Path.Combine("ManualMOF", "Standard")
            : string.IsNullOrWhiteSpace(subDirectoryName) ? StandardDirectoryName : Path.GetFileName(subDirectoryName.Trim());
        var buildDirectoryName = $"{createdAt:yyyyMMdd_HHmmss}_{NormalizeScriptBuildName(glassId)}";
        var buildDirectory = Path.Combine(_scriptDirectory, normalized, buildDirectoryName);
        if (!Directory.Exists(buildDirectory))
        {
            return buildDirectory;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(_scriptDirectory, normalized, $"{buildDirectoryName}_{index:000}");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Script build directory already exists too many times: {buildDirectory}");
    }

    private static string NormalizeScriptBuildName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "NO_GLASS_ID" : value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character == '-' || invalidChars.Contains(character) ? '_' : character);
        }

        var normalized = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(normalized) ? "NO_GLASS_ID" : normalized;
    }

    private static bool IsBufferedRunDirectory(string subDirectoryName)
    {
        return string.Equals(subDirectoryName, BufferedRunDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            subDirectoryName.Replace('\\', '/').Trim('/').Equals("ManualMOF/Buffered", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateActiveHeads(IReadOnlyList<ST_HEAD_PROCESS_PLAN> activeHeads)
    {
        int SelectHeadNo(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
        {
            return head.HeadNo;
        }

        bool ShouldIncludeItem(global::System.Linq.IGrouping<int, global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN> group)
        {
            return group.Count() > 1;
        }

        int SelectKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN> group)
        {
            return group.Key;
        }

        int SelectHeadNo2(int headNo)
        {
            return headNo;
        }

        var duplicateHeadNumbers = activeHeads.GroupBy(SelectHeadNo).Where(ShouldIncludeItem).Select(SelectKey).OrderBy(SelectHeadNo2).ToArray();
        if (duplicateHeadNumbers.Length > 0)
        {
            throw new InvalidOperationException($"Duplicate active head numbers: {string.Join(", ", duplicateHeadNumbers)}.");
        }

        foreach (var head in activeHeads)
        {
            if (string.IsNullOrWhiteSpace(head.ScriptFileName) || !string.Equals(Path.GetFileName(head.ScriptFileName), head.ScriptFileName, StringComparison.Ordinal) || head.ScriptFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException($"H{head.HeadNo:00} has an invalid script file name: {head.ScriptFileName}.");
            }

            if (IsReservedBuildFileName(head.ScriptFileName))
            {
                throw new InvalidOperationException($"H{head.HeadNo:00} script file name conflicts with a reserved build output file: {head.ScriptFileName}.");
            }
        }

        string SelectScriptFileName(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
        {
            return head.ScriptFileName;
        }

        bool ShouldIncludeHead(global::System.Linq.IGrouping<string, global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN> group)
        {
            return group.Count() > 1;
        }

        string SelectDuplicateScriptFileName(global::System.Linq.IGrouping<string, global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN> group)
        {
            return group.Key;
        }

        string SelectFileName(string fileName)
        {
            return fileName;
        }

        var duplicateFileNames = activeHeads.GroupBy(SelectScriptFileName, StringComparer.OrdinalIgnoreCase).Where(ShouldIncludeHead).Select(SelectDuplicateScriptFileName).OrderBy(SelectFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (duplicateFileNames.Length > 0)
        {
            throw new InvalidOperationException($"Duplicate active script file names: {string.Join(", ", duplicateFileNames)}.");
        }
    }

    private static void AppendStartFollowMoves(
        List<string> lines, 
        IReadOnlyDictionary<string, string> common, 
        ST_HEAD_PROCESS_PLAN head, 
        string xAxis, 
        string yAxis, 
        string encoderAxis, 
        double encoderScale, 
        bool encoderFeedbackFlip
    )
    {
        if (head.Shots.Count == 0)
        {
            return;
        }

        var firstPoint = head.Shots[0];
        var targetWaitPosition = firstPoint.EncoderWaitPosition;
        if (Math.Abs(encoderScale) <= 0.000001 || Math.Abs(targetWaitPosition) <= 0.000001)
        {
            return;
        }

        var jumpStep = Math.Abs(ReadDouble(common, 5.0, "SCAN_START_JUMP_STEP", "MOF_START_JUMP_STEP", "SCAN_JUMP_SCALE"));
        if (jumpStep <= 0.000001)
        {
            return;
        }

        var targetScanDistance = -(targetWaitPosition / Math.Abs(encoderScale));
        var targetScannerMove = CScannerAxisTransform.Read(common, head.HeadNo).Apply(0.0, targetScanDistance);
        var targetDistance = Math.Max(Math.Abs(targetScannerMove.X), Math.Abs(targetScannerMove.Y));
        if (targetDistance <= 0.000001)
        {
            return;
        }

        for (var distance = jumpStep; distance < targetDistance; distance += jumpStep)
        {
            var ratio = distance / targetDistance;
            var scannerX = targetScannerMove.X * ratio;
            var scannerY = targetScannerMove.Y * ratio;
            var waitPosition = targetWaitPosition * ratio;
            lines.Add($"MoveRapid([{xAxis},{yAxis}], [{Format(scannerX)},{Format(scannerY)}])");
            AppendEncoderWait(lines, encoderAxis, waitPosition, encoderFeedbackFlip);
        }
    }

    private static void AppendFollowTransitionMoves(
        List<string> lines, 
        IReadOnlyDictionary<string, string> common, 
        ST_HEAD_PROCESS_PLAN head, 
        string xAxis, 
        string yAxis, 
        string encoderAxis, 
        bool encoderFeedbackFlip, 
        ST_HEAD_SHOT_POINT previousPoint, 
        ST_HEAD_SHOT_POINT targetPoint
    )
    {
        var jumpStep = Math.Abs(ReadDouble(common, 5.0, "SCAN_START_JUMP_STEP", "MOF_START_JUMP_STEP", "SCAN_JUMP_SCALE"));
        if (jumpStep <= 0.000001)
        {
            return;
        }

        var scanFieldWidth = Math.Abs(ReadDouble(common, 110.0, CreateHeadKeys(head.HeadNo, "SCAN_FIELD_WIDTH_X", "HEAD_FIELD_WIDTH_X")));
        if (scanFieldWidth <= 0.000001)
        {
            scanFieldWidth = 110.0;
        }

        var transitionLimit = scanFieldWidth / 2.0;
        var followAxis = ResolveFollowAxis(CScannerAxisTransform.Read(common, head.HeadNo));
        var previousFollowPosition = ReadFollowAxisPosition(previousPoint, followAxis);
        var targetFollowPosition = ReadFollowAxisPosition(targetPoint, followAxis);
        var followDistance = Math.Abs(targetFollowPosition - previousFollowPosition);
        if (followDistance <= transitionLimit)
        {
            return;
        }

        var deltaX = targetPoint.ScannerGx - previousPoint.ScannerGx;
        var deltaY = targetPoint.ScannerGy - previousPoint.ScannerGy;
        var deltaWaitPosition = targetPoint.EncoderWaitPosition - previousPoint.EncoderWaitPosition;
        for (var distance = jumpStep; distance < followDistance; distance += jumpStep)
        {
            var ratio = distance / followDistance;
            var scannerX = previousPoint.ScannerGx + (deltaX * ratio);
            var scannerY = previousPoint.ScannerGy + (deltaY * ratio);
            var waitPosition = previousPoint.EncoderWaitPosition + (deltaWaitPosition * ratio);
            lines.Add($"MoveRapid([{xAxis},{yAxis}], [{Format(scannerX)},{Format(scannerY)}])");
            AppendEncoderWait(lines, encoderAxis, waitPosition, encoderFeedbackFlip);
        }
    }

    private static EN_SCRIPT_FOLLOW_AXIS ResolveFollowAxis(ST_SCANNER_AXIS_TRANSFORM transform)
    {
        var followDirection = transform.Apply(0.0, 1.0);
        if (Math.Abs(followDirection.X) >= Math.Abs(followDirection.Y))
        {
            return EN_SCRIPT_FOLLOW_AXIS.X;
        }

        return EN_SCRIPT_FOLLOW_AXIS.Y;
    }

    private static double ReadFollowAxisPosition(ST_HEAD_SHOT_POINT point, EN_SCRIPT_FOLLOW_AXIS followAxis)
    {
        if (followAxis == EN_SCRIPT_FOLLOW_AXIS.X)
        {
            return point.ScannerGx;
        }

        return point.ScannerGy;
    }

    private static void AppendEncoderWait(List<string> lines, string encoderAxis, double waitPosition, bool encoderFeedbackFlip)
    {
        if (Math.Abs(waitPosition) <= 0.000001)
        {
            return;
        }

        var feedbackWaitPosition = encoderFeedbackFlip ? -waitPosition : waitPosition;
        var comparison = feedbackWaitPosition >= 0.0 ? ">=" : "<=";
        lines.Add($"wait(StatusGetAxisItem({encoderAxis}, AxisStatusItem.AuxiliaryFeedback) {comparison} {Format(feedbackWaitPosition)})");
    }

    private static void AppendHeadShotList(List<string> lines, ST_HEAD_PROCESS_PLAN head)
    {
        lines.Add("ShotList:");
        int SelectCellNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT point)
        {
            return point.CellNo;
        }

        int SelectKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> cell)
        {
            return cell.Key;
        }

        foreach (var cell in head.Shots.GroupBy(SelectCellNo).OrderBy(SelectKey))
        {
            foreach (var shotNames in cell.Select(FormatShotName).Chunk(12))
            {
                lines.Add($"CELL{cell.Key:00}: {string.Join(", ", shotNames)}");
            }
        }
    }

    private static string FormatShotName(ST_HEAD_SHOT_POINT point)
    {
        return $"{FormatRowName(point.GroupRow)}{Math.Max(1, point.GroupColumn + 1):00}";
    }

    private static string FormatRowName(int zeroBasedRow)
    {
        var row = Math.Max(0, zeroBasedRow);
        var name = "";
        do
        {
            name = (char)('A' + (row % 26)) + name;
            row = (row / 26) - 1;
        }
        while (row >= 0);
        return name;
    }

    private static void AppendDefaultSetting(List<string> lines, IReadOnlyDictionary<string, string> parameters)
    {
        var scannerAcc = ReadDouble(parameters, 500000.0, "SCANNER_ACC");
        var motionUpdateRate = ReadInt(parameters, 0, "SCAN_MOTION_UPDATE_RATE");
        lines.Add("IfovOff()");
        lines.Add("SetupTaskDistanceUnits(DistanceUnits.Primary)");
        lines.Add("SetupTaskTimeUnits(TimeUnits.Seconds)");
        lines.Add("SetupCoordinatedRampType(RampType.Sine)");
        lines.Add($"SetupCoordinatedRampValue(RampMode.Rate, {Format(scannerAcc)})");
        if (motionUpdateRate > 0)
        {
            lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.MotionUpdateRate, {motionUpdateRate})");
        }

        lines.Add("");
    }

    private static void AppendScriptEnd(List<string> lines, IReadOnlyDictionary<string, string> parameters)
    {
        if (ReadBool(parameters, false, "A1_BUFFERED_RUN"))
        {
            lines.Add("CommandQueueStop()");
        }

        lines.Add("end");
    }

    private static double CalculateLaserPeriod(double frequencyKhz)
    {
        return frequencyKhz <= 0.0 ? 0.0 : 1.0 / (frequencyKhz / 1000.0);
    }

    private static double CalculateShotDelaySeconds(int shotCount, double frequencyKhz)
    {
        if (shotCount <= 0 || frequencyKhz <= 0.0)
        {
            return 0.001;
        }

        // Match the drilling test bench script rule: MoveDelay = (ShotCount + 1) / Frequency(kHz).
        return (shotCount + 1.0) / frequencyKhz;
    }

    private static void AppendSingleAxisMoveDelay(List<string> lines, string axis, double delayMsec)
    {
        if (delayMsec <= 0.0)
        {
            return;
        }

        lines.Add($"MoveDelay([{axis}], {Format(delayMsec)})");
    }

    private static double ResolveCoordinatedRampRate(double xRampRate, double yRampRate)
    {
        if (xRampRate <= 0.0)
        {
            return yRampRate;
        }

        if (yRampRate <= 0.0)
        {
            return xRampRate;
        }

        return Math.Min(xRampRate, yRampRate);
    }

    private static double ToAutomationSpeed(double recipeSpeed)
    {
        return recipeSpeed;
    }

    private static double ReadDelaySeconds(IReadOnlyDictionary<string, string> parameters, double defaultSeconds, params string[] keys)
    {
        bool ShouldIncludeItem(string key)
        {
            return !string.IsNullOrWhiteSpace(key);
        }

        foreach (var key in keys.Where(ShouldIncludeItem))
        {
            if (!parameters.TryGetValue(key, out var value) || !double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                continue;
            }

            return key.EndsWith("_MS", StringComparison.OrdinalIgnoreCase) || result > 10.0 ? result / 1000.0 : result;
        }

        return defaultSeconds;
    }

    private static IReadOnlyList<string> CreateHeadKeys(int headNo, params string[] names)
    {
        string SelectItem(string name)
        {
            return $"H{headNo:00}_{name}";
        }

        return names.Select(SelectItem).ToArray();
    }

    private static IReadOnlyList<string> CreateEncoderScaleKeys(int headNo)
    {
        return CreateHeadKeys(headNo, "ENCODER_SCALE")
            .Concat(["SCAN_ENCODER_SCALE"])
            .ToArray();
    }

    private static IReadOnlyList<string> CreateEncoderFeedbackFlipKeys(int headNo)
    {
        return CreateHeadKeys(headNo, "ENCODER_FEEDBACK_FLIP")
            .Concat(["SCAN_ENCODER_FEEDBACK_FLIP"])
            .ToArray();
    }

    private static string ResolveParameterName(IReadOnlyDictionary<string, string> parameters, IEnumerable<string> keys, string defaultName)
    {
        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key) && parameters.ContainsKey(key))
            {
                return key;
            }
        }

        return defaultName;
    }

    private static string ReadText(IReadOnlyDictionary<string, string> parameters, string defaultValue, IEnumerable<string> keys)
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

    private static double ReadDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
    {
        return ReadDouble(parameters, defaultValue, (IEnumerable<string>)keys);
    }

    private static double ReadDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, IEnumerable<string> keys)
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

    private static bool ReadOnOff(IReadOnlyDictionary<string, string> parameters, bool defaultValue, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (parameters.TryGetValue(key, out var value))
            {
                return ParseOnOff(value, defaultValue, key);
            }
        }

        return defaultValue;
    }

    private static bool ParseOnOff(string? value, bool defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized is "ON" or "TRUE" or "1" or "YES")
        {
            return true;
        }

        if (normalized is "OFF" or "FALSE" or "0" or "NO")
        {
            return false;
        }

        throw new InvalidOperationException($"{key} must be ON or OFF. Current value: {value}");
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
    {
        return ReadInt(parameters, defaultValue, (IEnumerable<string>)keys);
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, IEnumerable<string> keys)
    {
        bool ShouldIncludeItem(string key)
        {
            return !string.IsNullOrWhiteSpace(key);
        }

        foreach (var key in keys.Where(ShouldIncludeItem))
        {
            if (parameters.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
        }

        return defaultValue;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> parameters, bool defaultValue, params string[] keys)
    {
        return ReadBool(parameters, defaultValue, (IEnumerable<string>)keys);
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> parameters, bool defaultValue, IEnumerable<string> keys)
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

            if (bool.TryParse(value, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                return intValue != 0;
            }

            if (value.Equals("Y", StringComparison.OrdinalIgnoreCase) || value.Equals("YES", StringComparison.OrdinalIgnoreCase) || value.Equals("ON", StringComparison.OrdinalIgnoreCase) || value.Equals("USE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("N", StringComparison.OrdinalIgnoreCase) || value.Equals("NO", StringComparison.OrdinalIgnoreCase) || value.Equals("OFF", StringComparison.OrdinalIgnoreCase) || value.Equals("UNUSE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return defaultValue;
    }

    private static string Format(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string CreateCsvLine(params string?[] values)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            AppendCsvField(builder, values[index]);
        }

        return builder.ToString();
    }

    private static void AppendCsvField(StringBuilder builder, string? value)
    {
        value ??= "";
        var needsQuote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (!needsQuote)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (var character in value)
        {
            if (character == '"')
            {
                builder.Append('"');
            }

            builder.Append(character);
        }

        builder.Append('"');
    }

    private static bool IsReservedBuildFileName(string fileName)
    {
        return fileName.Equals(ProcessCoordinateListFileName, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(ProcessShotCoordinateCsvFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateEncoderScale(string name, double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < MinimumEncoderScaleMagnitude)
        {
            throw new InvalidOperationException($"{name} must be a finite non-zero value at least {MinimumEncoderScaleMagnitude} in magnitude.");
        }

        var scriptValue = double.Parse(Format(value), NumberStyles.Float, CultureInfo.InvariantCulture);
        var scaleUnitError = Math.Abs(scriptValue - value) / MinimumEncoderScaleMagnitude;
        var scaleUnitTolerance = Math.Min(0.499999, Math.Max(0.000001, Math.Abs(value) * 0.000000000001 / MinimumEncoderScaleMagnitude));
        if (scaleUnitError > scaleUnitTolerance)
        {
            throw new InvalidOperationException($"{name} must be aligned to {MinimumEncoderScaleMagnitude} resolution because Automation script output uses six decimal places.");
        }
    }

    private static string ValidateAeroScriptAxisName(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} axis name is required.");
        }

        var normalized = value.Trim();
        bool MatchesItem(char character)
        {
            return char.IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_';
        }

        if (normalized.All(char.IsAsciiDigit) || (char.IsAsciiLetter(normalized[0]) && normalized.All(MatchesItem)))
        {
            return normalized;
        }

        throw new InvalidOperationException($"{name} axis name must be a numeric axis number or start with a letter and use letters, numbers, and underscores only.");
    }

    private static string NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DefaultScriptFileName;
        }

        var normalized = Path.GetFileName(fileName.Trim());
        return Path.HasExtension(normalized) ? normalized : $"{normalized}.ascript";
    }

    private sealed class CAutomation1Script : CAutomation1ScriptBase
    {
        private readonly string _scriptDirectory;
        private readonly List<string> _lines = [];
        private string _xAxis = "X";
        private string _yAxis = "Y";
        private string _laserAxis = "X";
        private string _stageXAxis = "STAGE_X";
        private string _stageYAxis = "STAGE_Y";
        private int _deviceNo;
        private int _pointCount;
        private double _currentX;
        private double _currentY;
        private double _laserOutputPeriod;
        private double _jumpSpeedMeterPerSec;
        private double _markSpeedMeterPerSec;
        private double _tactTime;
        private bool _nMarkDriveLaserControl;
        private bool _scanPlannerStageEncoderMode;
        private DateTimeOffset _createdAt = DateTimeOffset.Now;
        public CAutomation1Script(string scriptDirectory, string fileName)
        {
            _scriptDirectory = scriptDirectory;
            FileName = fileName;
        }

        public override string FileName { get; }

        public override string FilePath
        {
            get
            {
                return Path.Combine(_scriptDirectory, FileName);
            }
        }

        public override IReadOnlyList<string> Lines
        {
            get
            {
                return _lines;
            }
        }

        public override void Clear()
        {
            _lines.Clear();
            _pointCount = 0;
            _currentX = 0.0;
            _currentY = 0.0;
            _createdAt = DateTimeOffset.Now;
            _xAxis = "X";
            _yAxis = "Y";
            _laserAxis = "X";
            _stageXAxis = "STAGE_X";
            _stageYAxis = "STAGE_Y";
            _deviceNo = 0;
            _laserOutputPeriod = 0.0;
            _jumpSpeedMeterPerSec = 0.0;
            _markSpeedMeterPerSec = 0.0;
            _tactTime = 0.0;
            _nMarkDriveLaserControl = false;
            _scanPlannerStageEncoderMode = false;
        }

        public override void AddLine(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                _lines.Add(line);
            }
        }

        public override void Start(string title = "")
        {
            _createdAt = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(title))
            {
                _lines.Add($"// {title.Trim()}");
            }

            _lines.Add($"// CreatedAt={_createdAt:yyyy-MM-dd HH:mm:ss.fff zzz}");
            _lines.Add("program");
        }

        public override void SetDeviceNo(int deviceNo)
        {
            _deviceNo = Math.Max(0, deviceNo);
        }

        public override void SetNMarkDriveLaserControl(bool use)
        {
            _nMarkDriveLaserControl = use;
        }

        public override void SetScanPlannerStageEncoderMode(bool use)
        {
            _scanPlannerStageEncoderMode = use;
        }

        public override void DefaultSetting(double scannerAcc = 500000.0, int motionUpdateRate = 0, int executeLineCount = 110, bool resetPso = true)
        {
            _lines.Add("SetupTaskDistanceUnits(DistanceUnits.Primary)");
            _lines.Add("SetupTaskTimeUnits(TimeUnits.Seconds)");
            SetMoveBlending(true);
            SetWaitModeAuto();
            SetAbsoluteMode();
            SetScannerAcc(scannerAcc);
            if (!_nMarkDriveLaserControl)
            {
                _lines.Add($"GalvoConfigureLaserOutputPeriod({_laserAxis}, 0)");
                _lines.Add($"GalvoConfigureLaser1PulseWidth({_laserAxis}, 0)");
                SetLaserMode(0);
            }

            if (motionUpdateRate > 0)
            {
                SetMoveUpdateRate(motionUpdateRate);
            }

            SetExecuteLineCount(executeLineCount);
            if (resetPso && !_nMarkDriveLaserControl)
            {
                _lines.Add($"PsoReset({_laserAxis})");
            }
        }

        public override void DefaultFigureScanSetting(int motionUpdateRate = 100, int executeLineCount = 110, bool moveToCenter = true)
        {
            SetIFOV(false);
            _lines.Add("SetupTaskDistanceUnits(DistanceUnits.Primary)");
            _lines.Add("SetupTaskTimeUnits(TimeUnits.Seconds)");
            SetMoveBlending(false);
            SetWaitModeAuto();
            SetAbsoluteMode();
            if (!_nMarkDriveLaserControl)
            {
                _lines.Add($"GalvoConfigureLaserOutputPeriod({_laserAxis}, 0)");
                _lines.Add($"GalvoConfigureLaser1PulseWidth({_laserAxis}, 0)");
                SetLaserMode(0);
            }

            SetMoveUpdateRate(motionUpdateRate);
            SetExecuteLineCount(executeLineCount);
            if (!_nMarkDriveLaserControl)
            {
                _lines.Add($"PsoReset ({_laserAxis})");
            }

            if (moveToCenter) GCodeMove(0.0, 0.0);
        }

        public override void SetAxis(string xAxis, string yAxis, string? laserAxis = null)
        {
            _xAxis = NormalizeAxis(xAxis, "X");
            _yAxis = NormalizeAxis(yAxis, "Y");
            _laserAxis = NormalizeAxis(laserAxis, _xAxis);
        }

        public override void SetStageAxis(string xAxis, string yAxis)
        {
            _stageXAxis = NormalizeAxis(xAxis, "STAGE_X");
            _stageYAxis = NormalizeAxis(yAxis, "STAGE_Y");
        }

        public override void SetFrequency(double frequencyKhz)
        {
            _laserOutputPeriod = CalculateLaserPeriod(frequencyKhz);
            if (!_nMarkDriveLaserControl)
            {
                _lines.Add($"GalvoConfigureLaserOutputPeriod({_laserAxis}, {Format(_laserOutputPeriod)})");
            }
        }

        public override void SetLaserPower(double powerPercent, double outputRate = 100.0, bool analogOutputUse = false)
        {
            if (!_nMarkDriveLaserControl)
            {
                SetMoveDelay(1.0, false);
            }

            var voltage = (powerPercent / 100.0) * 10.0 * (outputRate / 100.0);
            _lines.Add($"AnalogOutputSet({_laserAxis}, 0, {Format(voltage)})");
            if (!_nMarkDriveLaserControl)
            {
                var pulseWidth = _laserOutputPeriod * (powerPercent / 100.0) * (outputRate / 100.0);
                _lines.Add($"GalvoConfigureLaser1PulseWidth({_laserAxis}, {Format(pulseWidth)})");
            }
        }

        public override void SetLaserPowerNoDelay(double powerPercent, double outputRate = 100.0)
        {
            var voltage = (powerPercent / 100.0) * 10.0 * (outputRate / 100.0);
            _lines.Add($"AnalogOutputSet({_laserAxis}, 0, {Format(voltage)})");
            if (!_nMarkDriveLaserControl)
            {
                var pulseWidth = _laserOutputPeriod * (powerPercent / 100.0) * (outputRate / 100.0);
                _lines.Add($"GalvoConfigureLaser1PulseWidth({_laserAxis}, {Format(pulseWidth)})");
            }
        }

        public override void SetPulseOnTimeLaserPower(double powerPercent, double dutyPercent, double outputRate = 100.0)
        {
            if (!_nMarkDriveLaserControl)
            {
                SetMoveDelay(1.0, false);
            }

            var voltage = (powerPercent / 100.0) * 10.0 * (outputRate / 100.0);
            _lines.Add($"AnalogOutputSet({_laserAxis}, 0, {Format(voltage)})");
            if (!_nMarkDriveLaserControl)
            {
                var pulseWidth = _laserOutputPeriod * (dutyPercent / 100.0);
                _lines.Add($"GalvoConfigureLaser1PulseWidth({_laserAxis}, {Format(pulseWidth)})");
            }

            SetLaserMode(0);
        }

        public override void SetLaserMode(int mode)
        {
            if (!_nMarkDriveLaserControl)
            {
                _lines.Add($"GalvoConfigureLaserMode({_laserAxis}, {mode})");
            }
        }

        public override void SetLaserDelay(double onDelay, double offDelay)
        {
            // AeroScript accepts microseconds directly; manual millisecond waits are emitted as MoveDelay.
            // Emit zero values too, so a previous program's hardware delays are cleared.
            _lines.Add($"GalvoConfigureLaserDelays({_laserAxis}, {Format(onDelay)}, {Format(offDelay)})");
        }

        public override void SetJumpSpeed(double speedMeterPerSec)
        {
            _lines.Add($"SetupAxisSpeed({_xAxis}, {Format(speedMeterPerSec * 1000.0)})");
            _lines.Add($"SetupAxisSpeed({_yAxis}, {Format(speedMeterPerSec * 1000.0)})");
            _jumpSpeedMeterPerSec = speedMeterPerSec;
        }

        public override void SetJumpSpeedRate(double speedMeterPerSec, double rate = 1.0)
        {
            if (rate < 1.0)
            {
                rate = 1.0;
            }

            _lines.Add($"SetupAxisSpeed({_xAxis}, {Format(speedMeterPerSec * rate * 1000.0)})");
            _lines.Add($"SetupAxisSpeed({_yAxis}, {Format(speedMeterPerSec * rate * 1000.0)})");
            _jumpSpeedMeterPerSec = speedMeterPerSec;
        }

        public override void SetMarkSpeed(double speedMeterPerSec)
        {
            _lines.Add($"SetupCoordinatedSpeed({Format(speedMeterPerSec * 1000.0)})");
            _markSpeedMeterPerSec = speedMeterPerSec;
        }

        public override void SetStageSpeed(double speedX, double speedY)
        {
            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"SetupAxisSpeed({_stageXAxis}, {Format(speedX)})");
            }

            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"SetupAxisSpeed({_stageYAxis}, {Format(speedY)})");
            }
        }

        public override void SetScannerAcc(double acc)
        {
            if (acc <= 0.0)
            {
                return;
            }

            _lines.Add($"SetupAxisRampType([{_xAxis}, {_yAxis}], RampType.Sine)");
            _lines.Add($"SetupAxisRampValue([{_xAxis}], RampMode.Rate, {Format(acc)})");
            _lines.Add($"SetupAxisRampValue([{_yAxis}], RampMode.Rate, {Format(acc)})");
            _lines.Add("SetupCoordinatedRampType(RampType.Sine)");
            _lines.Add($"SetupCoordinatedRampValue(RampMode.Rate, {Format(acc)})");
        }

        public override void SetMarkAcc(double acc)
        {
            if (acc <= 0.0)
            {
                return;
            }

            _lines.Add("SetupCoordinatedRampType(RampType.Sine)");
            _lines.Add($"SetupCoordinatedRampValue(RampMode.Rate, {Format(acc)})");
        }

        public override void SetIFOV(bool use)
        {
            _lines.Add(use ? "IfovOn()" : "IfovOff()");
        }

        public override void SetIFOVEmulatedQuadratureDivider()
        {
            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"ParameterSetAxisValue({_stageXAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, 16)");
            }

            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"ParameterSetAxisValue({_stageYAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, 16)");
            }

            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"$rglobal[{_deviceNo}] = ParameterGetAxisValue({_xAxis}, AxisParameter.CountsPerUnit) / (ParameterGetAxisValue({_stageXAxis}, AxisParameter.CountsPerUnit) / ParameterGetAxisValue({_stageXAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider))");
                _lines.Add($"ParameterSetAxisValue({_xAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, $rglobal[{_deviceNo}])");
            }

            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"$rglobal[{_deviceNo + 1}] = ParameterGetAxisValue({_yAxis}, AxisParameter.CountsPerUnit) / (ParameterGetAxisValue({_stageYAxis}, AxisParameter.CountsPerUnit) / ParameterGetAxisValue({_stageYAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider))");
                _lines.Add($"ParameterSetAxisValue({_yAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, $rglobal[{_deviceNo + 1}])");
            }
        }

        public override void SetIFOVIO(bool use = true)
        {
            if (use)
            {
                if (IsUsableAxis(_stageXAxis))
                {
                    _lines.Add($"DriveEncoderOutputConfigureInput({_stageXAxis}, 0, 0)");
                    _lines.Add($"DriveEncoderOutputConfigureDivider({_stageXAxis}, 0, 1)");
                    _lines.Add($"DriveEncoderOutputOn({_stageXAxis}, 0, 0)");
                }

                if (IsUsableAxis(_stageYAxis))
                {
                    _lines.Add($"DriveEncoderOutputConfigureInput({_stageYAxis}, 0, 0)");
                    _lines.Add($"DriveEncoderOutputConfigureDivider({_stageYAxis}, 0, 1)");
                    _lines.Add($"DriveEncoderOutputOn({_stageYAxis}, 0, 0)");
                }

                return;
            }

            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"DriveEncoderOutputOff({_stageXAxis}, 0)");
            }

            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"DriveEncoderOutputOff({_stageYAxis}, 0)");
            }
        }

        public override void SetIFOVScaleXY()
        {
            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"$rglobal[{_deviceNo + 100}] = ParameterGetAxisValue({_xAxis}, AxisParameter.CountsPerUnit) / ParameterGetAxisValue({_stageXAxis}, AxisParameter.CountsPerUnit) * ParameterGetAxisValue({_stageXAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider)");
            }

            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"$rglobal[{_deviceNo + 101}] = ParameterGetAxisValue({_yAxis}, AxisParameter.CountsPerUnit) / ParameterGetAxisValue({_stageYAxis}, AxisParameter.CountsPerUnit) * ParameterGetAxisValue({_stageYAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider)");
            }
        }

        public override void SetIFOVTime(long time)
        {
            _lines.Add($"IfovSetTime({time})");
        }

        public override void SetIFOVSize(double size)
        {
            _lines.Add($"IfovSetSize({Format(size)})");
        }

        public override void SetIFOVTrackingSpeed(long speed)
        {
            _lines.Add($"IfovSetTrackingSpeed({speed})");
        }

        public override void SetIFOVTrackingAccel(long acc)
        {
            _lines.Add($"IfovSetTrackingAcceleration({acc})");
        }

        public override void SetIFOVPair(string xStageAxis, string yStageAxis, bool xDirection, bool yDirection)
        {
            var scaleX = xDirection ? $"$rglobal[{_deviceNo + 100}]" : $"$rglobal[{_deviceNo + 100}]*-1";
            var scaleY = yDirection ? $"$rglobal[{_deviceNo + 101}]" : $"$rglobal[{_deviceNo + 101}]*-1";
            _lines.Add($"IfovSetAxisPairs([{_xAxis},{NormalizeAxis(xStageAxis, _stageXAxis)}],[{_yAxis},{NormalizeAxis(yStageAxis, _stageYAxis)}], {scaleX},{scaleY})");
        }

        public override void SetIFOVSyncAxis()
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetIFOVSYNCAXIS.
        }

        public override void SetMoveBlending(bool use)
        {
            _lines.Add(use ? "VelocityBlendingOn()" : "VelocityBlendingOff()");
        }

        public override void SetAbsoluteMode()
        {
            _lines.Add("SetupTaskTargetMode(TargetMode.Absolute)");
        }

        public override void SetIncrementalMode()
        {
            _lines.Add("SetupTaskTargetMode(TargetMode.Incremental)");
        }

        public override void SetWaitModeAuto()
        {
            _lines.Add("SetupTaskWaitMode(WaitMode.Auto)");
        }

        public override void SetMoveDelay(double delaySeconds, bool addTactTime = true, bool applyMinimumDelay = true)
        {
            if (delaySeconds <= 0.0 || applyMinimumDelay && delaySeconds < 0.001)
            {
                return;
            }

            if (applyMinimumDelay && delaySeconds <= 0.021)
            {
                delaySeconds = 0.021;
            }

            _lines.Add($"MoveDelay([{_xAxis},{_yAxis}], {Format(delaySeconds)})");
            if (addTactTime)
            {
                _tactTime += delaySeconds;
            }
        }

        public override void SetExecuteLineCount(int lineCount)
        {
            if (lineCount <= 0)
            {
                return;
            }

            _lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.ExecuteNumLines, {lineCount})");
        }

        public override void SetScannerRotate(double angle)
        {
            if (!_nMarkDriveLaserControl)
            {
                _lines.Add($"GalvoRotationSet({_laserAxis}, {Format(angle)})");
            }
        }

        public override void SetScannerRotate(string laserAxis, double angle)
        {
            _lines.Add($"GalvoRotationSet({NormalizeAxis(laserAxis, _laserAxis)}, {Format(angle)})");
        }

        public override void SetMoveUpdateRate(int rate)
        {
            _lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.MotionUpdateRate, {rate})");
        }

        public override void SetCoordinatedAccelLimit(long acc, long arcAcc)
        {
            _lines.Add($"SetupCoordinatedAccelLimit({acc},{arcAcc})");
        }

        public override void SetTaskAccelLimit(long acc, long arcAcc)
        {
            _lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.DefaultCoordinatedAccelLimit, {acc})");
            _lines.Add($"ParameterSetTaskValue(TaskGetIndex(), TaskParameter.DefaultCoordinatedCircularAccelLimit, {arcAcc})");
        }

        public override void SetScanTrajectoryFIRFilterX(long delay)
        {
            _lines.Add($"ParameterSetAxisValue({_xAxis},AxisParameter.TrajectoryFirFilter, {delay})");
        }

        public override void SetScanTrajectoryFIRFilterY(long delay)
        {
            _lines.Add($"ParameterSetAxisValue({_yAxis},AxisParameter.TrajectoryFirFilter, {delay})");
        }

        public override void SetStageTrajectoryFIRFilterX(long delay)
        {
            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"ParameterSetAxisValue({_stageXAxis},AxisParameter.TrajectoryFirFilter, {delay})");
            }
        }

        public override void SetStageTrajectoryFIRFilterY(long delay)
        {
            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"ParameterSetAxisValue({_stageYAxis},AxisParameter.TrajectoryFirFilter, {delay})");
            }
        }

        public override void SetProjection(string axis, double offsetX, double offsetY, double offsetT)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetProjection.
        }

        public override void SetProjectionOff(string axis)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetProjectionOFF.
        }

        public override void SetGearing(string masterAxis, string slaveAxis)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetGearing.
        }

        public override void SetGearingOff(string slaveAxis = "AUTO")
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetGearingOFF.
        }

        public override void SetSoftwareLimitSetup(bool use = true)
        {
            if (use)
            {
                return;
            }

            _lines.Add($"ParameterSetAxisValue({_xAxis}, AxisParameter.SoftwareLimitSetup, 0)");
            _lines.Add($"ParameterSetAxisValue({_yAxis}, AxisParameter.SoftwareLimitSetup, 0)");
        }

        public override void SetAerotechEncoderReset(string axisX, string axisY)
        {
            var x = NormalizeAxis(axisX, "NONE");
            var y = NormalizeAxis(axisY, "NONE");
            if (IsUsableAxis(x))
            {
                _lines.Add($"DriveSetAuxiliaryFeedback({x},0)");
            }

            if (IsUsableAxis(y))
            {
                _lines.Add($"DriveSetAuxiliaryFeedback({y},0)");
            }
        }

        public override void SetScanPlannerStageEncoder(string stageAxis)
        {
            var axis = NormalizeAxis(stageAxis, "NONE");
            if (IsUsableAxis(axis))
            {
                _lines.Add($"$StageEncoder = ParameterGetAxisValue({axis}, AxisParameter.CountsPerUnit) / ParameterGetAxisValue({axis}, AxisParameter.PrimaryEmulatedQuadratureDivider)");
            }
        }

        public override void SetEmulatedQuadratureDividerX(int value)
        {
            _lines.Add($"ParameterSetAxisValue({_xAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, {value})");
        }

        public override void SetEmulatedQuadratureDividerY(int value)
        {
            _lines.Add($"ParameterSetAxisValue({_yAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, {value})");
        }

        public override void SetStageEmulatedQuadratureDivider(int xValue, int yValue)
        {
            if (IsUsableAxis(_stageXAxis))
            {
                _lines.Add($"ParameterSetAxisValue({_stageXAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, {xValue})");
            }

            if (IsUsableAxis(_stageYAxis))
            {
                _lines.Add($"ParameterSetAxisValue({_stageYAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, {yValue})");
            }
        }

        public override void SetPSO(
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
        )
        {
            if (_nMarkDriveLaserControl)
            {
                return;
            }

            if (mode == EN_AEROTECH_MODE.Mof)
            {
                WaitMoveDone();
                _lines.Add($"$Cpu{_xAxis} = {Format(markSpeed * 1000.0)} * ParameterGetAxisValue({_xAxis}, AxisParameter.CountsPerUnit) / 15000000 ; pso max inputrate 15Mhz ; marking velocity 6000");
                _lines.Add($"$Cpu{_yAxis} = {Format(markSpeed * 1000.0)} * ParameterGetAxisValue({_yAxis}, AxisParameter.CountsPerUnit) / 15000000 ; pso max inputrate 15Mhz ; marking velocity 6000");
                _lines.Add($"ParameterSetAxisValue({_xAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, $Cpu{_xAxis})");
                _lines.Add($"ParameterSetAxisValue({_yAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, $Cpu{_yAxis})");
            }
            else if (mode == EN_AEROTECH_MODE.Ifov)
            {
                SetIFOVEmulatedQuadratureDivider();
            }
            else
            {
                WaitMoveDone();
                _lines.Add($"$rglobal[{_deviceNo}] = {Format(markSpeed * 1000.0)} * ParameterGetAxisValue({_xAxis}, AxisParameter.CountsPerUnit) / 15000000 ; pso max inputrate 15Mhz ; marking velocity 6000");
                _lines.Add($"$rglobal[{_deviceNo + 1}] = {Format(markSpeed * 1000.0)} * ParameterGetAxisValue({_yAxis}, AxisParameter.CountsPerUnit) / 15000000 ; pso max inputrate 15Mhz ; marking velocity 6000");
                _lines.Add($"ParameterSetAxisValue({_xAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, $rglobal[{_deviceNo}])");
                _lines.Add($"ParameterSetAxisValue({_yAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, $rglobal[{_deviceNo + 1}])");
            }

            if (_scanPlannerStageEncoderMode)
            {
                _lines.Add($"ParameterSetAxisValue({_xAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, ParameterGetAxisValue({_xAxis}, AxisParameter.CountsPerUnit) / $StageEncoder)");
                _lines.Add($"ParameterSetAxisValue({_yAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider, ParameterGetAxisValue({_yAxis}, AxisParameter.CountsPerUnit) / $StageEncoder)");
            }

            _lines.Add($"PsoReset({_laserAxis})");
            if (psoMode == EN_AEROTECH_PSO_MODE.Unused)
            {
                SetPSOOnOff(false);
                LaserAuto();
                return;
            }

            _lines.Add($"PsoDistanceConfigureInputs({_laserAxis}, [PsoDistanceInput.GL4PrimaryFeedbackAxis1Encoder0, PsoDistanceInput.GL4IfovFeedbackAxis2])");
            if (mode == EN_AEROTECH_MODE.Mof)
            {
                _lines.Add($"PsoDistanceConfigureFixedDistance({_laserAxis},  Round(   UnitsToCounts({_laserAxis}, {Format(pulseDistance)}) / $Cpu{_xAxis} ))");
            }
            else
            {
                _lines.Add($"PsoDistanceConfigureFixedDistance({_laserAxis},  Round(   UnitsToCounts({_laserAxis}, {Format(pulseDistance)}) / $rglobal[{_deviceNo}] ))");
            }

            _lines.Add($"PsoDistanceCounterOn({_laserAxis})");
            _lines.Add($"PsoDistanceEventsOn({_laserAxis})");
            _lines.Add($"PsoDistanceConfigureCounterReset({_laserAxis}, PsoDistanceCounterResetMask.ResetWhenLaserOff)");
            _lines.Add($"PsoWaveformConfigureMode({_laserAxis}, PsoWaveformMode.Pulse)");
            _lines.Add($"PsoWaveformConfigurePulseFixedTotalTime({_laserAxis}, {Format(totalTime)})");
            _lines.Add($"PsoWaveformConfigurePulseFixedOnTime({_laserAxis},{Format(laserOnTime)})");
            _lines.Add($"PsoWaveformConfigurePulseFixedCount({_laserAxis}, 1)");
            _lines.Add($"PsoWaveformApplyPulseConfiguration({_laserAxis})");
            SetPSOOnOff(true);
            _lines.Add($"PsoOutputConfigureSource({_laserAxis}, PsoOutputSource.Waveform)");
            _lines.Add($"PsoOutputConfigureOutput({_laserAxis}, PsoOutputPin.GL4LaserOutput0)");
            _lines.Add($"PsoEventConfigureMask({_laserAxis}, PsoEventMask.LaserMask)");
            LaserAuto();
        }

        public override void SetPSODistance(double pulseDistance)
        {
            _lines.Add($"PsoDistanceConfigureFixedDistance({_laserAxis}, Round(UnitsToCounts({_laserAxis}, {Format(pulseDistance)}) / ParameterGetAxisValue({_laserAxis}, AxisParameter.PrimaryEmulatedQuadratureDivider)))");
        }

        public override void SetPSOOnOff(bool on)
        {
            _lines.Add(on ? $"PsoWaveformOn({_laserAxis})" : $"PsoWaveformOff({_laserAxis})");
        }

        public override void SetPSOChangePower(double frequencyKhz, double powerPercent)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetPSOChangePower.
        }

        public override void SetPSOFire(double totalTime, double laserOnTime, int count, double delay, EN_AEROTECH_MODE mode)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetPSOFire.
        }

        public override void SetPSOLaserWindowMask(bool on, double windowStartRange = 0, double windowEndRange = 0)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetPSOLaserWindowMask.
        }

        public override void DeclareEncoderVariable(string axis = "", bool useFeedback = false)
        {
        }

        public override void InitDeclareVariable()
        {
            _lines.Add($"var $EncoderScale{_xAxis} as real");
            _lines.Add($"var $EncoderScale{_yAxis} as real");
            _lines.Add($"var $Cpu{_xAxis} as real");
            _lines.Add($"var $Cpu{_yAxis} as real");
            _lines.Add("var $StageEncoder as real");
        }

        public override void InitDeclareVariableIFOV()
        {
        // Automation1 branch only documents rglobal usage in ScanMaster; no script line is generated.
        }

        public override void SetWaitForEncoder(string axis, double position, bool directionPlus = true)
        {
            var encoderAxis = NormalizeAxis(axis, _xAxis);
            var op = directionPlus ? ">" : "<";
            var offset = directionPlus ? position : -position;
            _lines.Add($"wait(StatusGetAxisItem({encoderAxis}, AxisStatusItem.AuxiliaryFeedback) {op} {Format(offset)})");
        }

        public override void SetWaitForEncoder(string axis, bool directionPlus, double position, double limit, double encoderScale = 1.0)
        {
            var encoderAxis = NormalizeAxis(axis, _xAxis);
            if (_scanPlannerStageEncoderMode)
            {
                _lines.Add($"wait(Abs(StatusGetAxisItem({_yAxis}, AxisStatusItem.AuxiliaryFeedback)) > $StageEncoder * {Format(Math.Abs(position))})");
                return;
            }

            if (Math.Abs(encoderScale) > 0.000001 && Math.Abs(encoderScale - 1.0) > 0.000001)
            {
                _lines.Add($"wait(Abs(StatusGetAxisItem({_yAxis}, AxisStatusItem.AuxiliaryFeedback)) > {Format(Math.Abs(encoderScale))} * {Format(Math.Abs(position))})");
                return;
            }

            if (directionPlus)
            {
                var posEncoder = (position - limit) * encoderScale;
                _lines.Add($"wait(StatusGetAxisItem({encoderAxis}, AxisStatusItem.PositionFeedback) >= {Format(posEncoder)})");
            }
            else
            {
                var posEncoder = (position + limit) * encoderScale;
                _lines.Add($"wait(StatusGetAxisItem({encoderAxis}, AxisStatusItem.PositionFeedback) <= {Format(posEncoder)})");
            }
        }

        public override void SetWaitForEncoder2Axis(string axisX, string axisY, bool inToOut, double posX, double posY, double limitX, double limitY)
        {
            var x = NormalizeAxis(axisX, _stageXAxis);
            var y = NormalizeAxis(axisY, _stageYAxis);
            if (inToOut)
            {
                _lines.Add($"while(StatusGetAxisItem({x}, AxisStatusItem.PositionFeedback) > {Format(posX - limitX)} && StatusGetAxisItem({x}, AxisStatusItem.PositionFeedback) < {Format(posX + limitX)} && StatusGetAxisItem({y}, AxisStatusItem.PositionFeedback) > {Format(posY - limitY)} && StatusGetAxisItem({y}, AxisStatusItem.PositionFeedback) < {Format(posY + limitY)})");
            }
            else
            {
                _lines.Add($"while(!(StatusGetAxisItem({x}, AxisStatusItem.PositionFeedback) > {Format(posX - limitX)} && StatusGetAxisItem({x}, AxisStatusItem.PositionFeedback) < {Format(posX + limitX)}) || !(StatusGetAxisItem({y}, AxisStatusItem.PositionFeedback) > {Format(posY - limitY)} && StatusGetAxisItem({y}, AxisStatusItem.PositionFeedback) < {Format(posY + limitY)}))");
            }

            _lines.Add("end");
        }

        public override void SetWaitForStartAxis2(string axisX, string axisY, bool inToOut, double posX, double posY, double limitX, double limitY)
        {
            var x = NormalizeAxis(axisX, "NONE");
            var y = NormalizeAxis(axisY, "NONE");
            if (!IsUsableAxis(x) && IsUsableAxis(y))
            {
                _lines.Add($"while(Abs(StatusGetAxisItem({y}, AxisStatusItem.PositionFeedback) - {Format(posY)}) < 0.005)");
                _lines.Add("end");
                return;
            }

            if (!IsUsableAxis(y) && IsUsableAxis(x))
            {
                _lines.Add($"while(Abs(StatusGetAxisItem({x}, AxisStatusItem.PositionFeedback) - {Format(posX)}) < 0.005)");
                _lines.Add("end");
                return;
            }

            if (IsUsableAxis(x) && IsUsableAxis(y))
            {
                _lines.Add($"while(Abs(StatusGetAxisItem({x}, AxisStatusItem.PositionFeedback) - {Format(posX)}) < 0.01 && Abs(StatusGetAxisItem({y}, AxisStatusItem.PositionFeedback) - {Format(posY)}) < 0.01)");
                _lines.Add("end");
            }
        }

        public override void SetEncoderScaleFactor(string galvoAxis, string encoderAxis, int scale)
        {
            if (scale == 0)
            {
                return;
            }

            var galvo = NormalizeAxis(galvoAxis, _laserAxis);
            var encoder = NormalizeAxis(encoderAxis, _xAxis);
            _lines.Add($"GalvoEncoderScaleFactorSet({galvo}, ParameterGetAxisValue({encoder}, AxisParameter.CountsPerUnit) / {scale})");
        }

        public override void SetEncoderScaleFactor(string galvoAxis, string encoderAxis, bool directionPlus)
        {
            var galvo = NormalizeAxis(galvoAxis, _laserAxis);
            var encoder = NormalizeAxis(encoderAxis, _stageXAxis);
            var sign = directionPlus ? "" : " * -1";
            _lines.Add($"$EncoderScale{galvo} = ParameterGetAxisValue({galvo},AxisParameter.CountsPerUnit) / (ParameterGetAxisValue({encoder},AxisParameter.CountsPerUnit) / ParameterGetAxisValue({encoder},AxisParameter.AuxiliaryEmulatedQuadratureDivider)){sign}");
            _lines.Add($"GalvoEncoderScaleFactorSet({galvo}, $EncoderScale{galvo})");
        }

        public override void SetEncoderScaleFactor(string galvoAxis, string encoderAxis, double encoderX, double encoderY, bool directionPlus)
        {
            if (Math.Abs(encoderX) < 0.000001)
            {
                return;
            }

            var galvo = NormalizeAxis(galvoAxis, _laserAxis);
            var sign = directionPlus ? "" : "-";
            _lines.Add($"$EncoderScale{galvo} = ParameterGetAxisValue({galvo},AxisParameter.CountsPerUnit) / {Format(Math.Abs(encoderX))}");
            _lines.Add($"GalvoEncoderScaleFactorSet({galvo}, {sign}$EncoderScale{galvo})");
        }

        public override void SetEncoderScaleFactorByPrimaryDivider(string galvoAxis, string encoderAxis, bool directionPlus)
        {
            var galvo = NormalizeAxis(galvoAxis, _laserAxis);
            var encoder = NormalizeAxis(encoderAxis, "NONE");
            if (!IsUsableAxis(encoder))
            {
                return;
            }

            var sign = directionPlus ? "" : " * -1";
            _lines.Add($"$EncoderScale{galvo} = ParameterGetAxisValue({galvo}, AxisParameter.CountsPerUnit) / (ParameterGetAxisValue({encoder}, AxisParameter.CountsPerUnit) / ParameterGetAxisValue({encoder}, AxisParameter.PrimaryEmulatedQuadratureDivider)){sign}");
            _lines.Add($"GalvoEncoderScaleFactorSet({galvo}, $EncoderScale{galvo})");
        }

        public override void InitEncoderCount(string galvoAxis)
        {
            var galvo = NormalizeAxis(galvoAxis, _laserAxis);
            _lines.Add($"DriveSetAuxiliaryFeedback({galvo}, 0)");
        }

        public override void EncoderNotFeedback(string axis)
        {
            var encoderAxis = NormalizeAxis(axis, _stageXAxis);
            _lines.Add($"DriveEncoderOutputOff({encoderAxis}, 0)");
        }

        public override void ReleaseEncoderScaleFactor(string galvoAxis)
        {
            var galvo = NormalizeAxis(galvoAxis, _laserAxis);
            _lines.Add($"GalvoEncoderScaleFactorSet({galvo}, 0)");
        }

        public override void LaserAuto()
        {
            _lines.Add($"GalvoLaserOutput({_laserAxis}, GalvoLaser.Auto)");
        }

        public override void LaserOn()
        {
            _lines.Add($"GalvoLaserOutput({_laserAxis}, GalvoLaser.On)");
        }

        public override void LaserOff()
        {
            _lines.Add($"GalvoLaserOutput({_laserAxis}, GalvoLaser.Off)");
        }

        public override void PsoLaserControl(bool on, bool manual = false)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetPSOLaserControl.
        }

        public override void LaserFire(bool on)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::ListLaserFire.
        }

        public override void Jump(double x, double y)
        {
            if (!(x == 0.0 && y == 0.0) && _currentX == x && _currentY == y)
            {
                return;
            }

            _pointCount++;
            _lines.Add($"MoveRapid([{_xAxis}, {_yAxis}], [{Format(x)}, {Format(y)}])");
            AddMoveTactTime(x, y, _jumpSpeedMeterPerSec);
            _currentX = x;
            _currentY = y;
        }

        public override void Mark(double x, double y)
        {
            if (_currentX == x && _currentY == y)
            {
                return;
            }

            _pointCount++;
            _lines.Add($"MoveLinear([{_xAxis}, {_yAxis}], [{Format(x)}, {Format(y)}])");
            AddMoveTactTime(x, y, _markSpeedMeterPerSec);
            _currentX = x;
            _currentY = y;
        }

        public override void GCodeMove(double x, double y)
        {
            _lines.Add($"G0 {_xAxis} {Format(x)} {_yAxis} {Format(y)}");
            _currentX = x;
            _currentY = y;
        }

        public override void JumpRel(double x, double y)
        {
            Jump(_currentX + x, _currentY + y);
        }

        public override void MarkRel(double x, double y)
        {
            Mark(_currentX + x, _currentY + y);
        }

        public override void Arc(double startX, double startY, double endX, double endY, double centerX, double centerY, double angle)
        {
            var i = centerX - startX;
            var j = centerY - startY;
            var command = angle >= 0.0 ? "MoveCw" : "MoveCcw";
            _pointCount++;
            _lines.Add($"{command}([{_xAxis}, {_yAxis}], [{Format(endX)}, {Format(endY)}], [{Format(i)}, {Format(j)}])");
            _currentX = endX;
            _currentY = endY;
        }

        public override void JumpLinear(double x, double y)
        {
            _lines.Add($"SetupCoordinatedSpeed({Format(_jumpSpeedMeterPerSec * 1000.0)})");
            LaserOff();
            _pointCount++;
            _lines.Add($"MoveLinear([{_xAxis}, {_yAxis}], [{Format(x)}, {Format(y)}])");
            _lines.Add($"SetupCoordinatedSpeed({Format(_markSpeedMeterPerSec * 1000.0)})");
            _currentX = x;
            _currentY = y;
        }

        public override void WaitMoveDone()
        {
            _lines.Add($"WaitForMotionDone([{_xAxis}, {_yAxis}])");
        }

        public override void Dwell(double delay)
        {
            if (delay <= 0.0)
            {
                return;
            }

            _lines.Add($"Dwell({Format(delay)})");
        }

        public override void EnableAxisPair()
        {
            _lines.Add($"Enable([{_xAxis}, {_yAxis}])");
        }

        public override void DisableAxisPair()
        {
            _lines.Add($"Disable([{_xAxis}, {_yAxis}])");
        }

        public override void FaultAckAxisPair()
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::ListFaultAckXY.
        }

        public override void HomeAxisPair()
        {
            _lines.Add($"Home([{_xAxis}, {_yAxis}])");
        }

        public override void OffsetClearAxisPair()
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::ListOffsetClearXY.
        }

        public override void OffsetSetAxisPair(double x, double y)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::ListOffsetSetXY.
        }

        public override void SetSignalLogTrigger(bool use)
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetSignalLogTrigger.
        }

        public override void ProgramStart()
        {
            _lines.Add("program");
        }

        public override void ProgramEnd()
        {
            _lines.Add("end");
        }

        public override void BufferedEnd()
        {
            _lines.Add("CommandQueueStop()");
        }

        public override void WaitInpos()
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetWaitInpos.
        }

        public override void SetHomePos()
        {
        // Automation1 branch is empty in ScanMaster CMakeAerotech::SetHomePos.
        }

        public override void SetGalvoPosZero()
        {
            _lines.Add($"MoveRapid([{_xAxis}, {_yAxis}], [0, 0])");
            _currentX = 0.0;
            _currentY = 0.0;
        }

        public override void End(bool bufferedRun = false)
        {
            if (bufferedRun)
            {
                _lines.Add("CommandQueueStop()");
            }

            if (_lines.Count == 0 || !_lines[^1].Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                _lines.Add("end");
            }
        }

        public override ST_SCRIPT_BUILD_RESULT Save(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lines.Count == 0 || !_lines[^1].Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                End();
            }

            Directory.CreateDirectory(_scriptDirectory);
            System.IO.File.WriteAllLines(FilePath, _lines, Encoding.UTF8);
            return new ST_SCRIPT_BUILD_RESULT(FileName, FilePath, _pointCount, _createdAt, []);
        }

        private void AddMoveTactTime(double nextX, double nextY, double speed)
        {
            if (speed <= 0.0)
            {
                return;
            }

            var distance = Math.Sqrt(Math.Pow(nextY - _currentY, 2) + Math.Pow(nextX - _currentX, 2));
            _tactTime += distance / (speed * 1000.0);
        }

        private static string NormalizeAxis(string? axis, string defaultAxis)
        {
            return string.IsNullOrWhiteSpace(axis) ? defaultAxis : axis.Trim();
        }

        private static bool IsUsableAxis(string? axis)
        {
            return !string.IsNullOrWhiteSpace(axis) && !axis.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase);
        }
    }
}
