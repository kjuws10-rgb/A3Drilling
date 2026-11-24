using System.Globalization;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Drilling.Common.Alarm;
using Drilling.Common.Correction;
using Drilling.Common.Interface;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.Common.Station;

namespace Drilling.UI.Menu.Menus;

/// <summary>Test seam: the state machine/controller never opens a vendor device directly.</summary>
public interface IManualInspectionBackend
{
    bool IsSimulation { get; }
    string StateDirectory { get; }
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> LastSavedValues => new Dictionary<string, ST_MANUAL_OFFSET>();
    IReadOnlyList<KeyValuePair<string, string>> ConsumedMeasurements => [];
    IReadOnlyList<string> WatchRoots => [];
    IReadOnlySet<string> ImportedInitializedOffsetKeys => new HashSet<string>(StringComparer.Ordinal);
    Task<CManualInspectionSource> LoadAsync(string? resultPath, CancellationToken token, IReadOnlyList<string>? displayKeys = null);
    Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource source, CancellationToken token)
        => Task.FromResult<CManualInspectionSavedOffsetSnapshot?>(null);
    Task<CManualInspectionSavedOffsetSnapshot?> ExportFinalOffsetBundleAsync(CManualInspectionSource source,string path,CancellationToken token)
        => throw new NotSupportedException("이 연결은 최종 저장 Offset 내보내기를 지원하지 않습니다.");
    Task<CManualInspectionSavedOffsetSnapshot?> ExportCurrentOffsetCsvAsync(CManualInspectionSource source,string path,CancellationToken token,bool overwriteConfirmed=false)
        => throw new NotSupportedException("이 연결은 현재 Offset CSV 내보내기를 지원하지 않습니다.");
    string ImportedOffsetDescription => "";
    Task EnsureCurrentAsync(CManualInspectionSource source, CancellationToken token);
    void GuardOperation();
    Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source, string key,
        EN_MANUAL_OPERATION_KIND kind, Action<string> progress, CancellationToken token);
    Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress, CancellationToken token);
    async Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress,
        Action<IReadOnlyList<string>> activeTargets, CancellationToken token)
    {
        try { activeTargets(keys); await ExecuteFlyingAsync(source,keys,measured,progress,token); }
        finally { activeTargets([]); }
    }
    async Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress,
        Action<IReadOnlyList<string>> activeTargets, Func<CancellationToken,Task> afterLine, CancellationToken token)
    {
        // Compatibility for old/test adapters. The native implementation fences each saved line.
        await ExecuteFlyingAsync(source,keys,measured,progress,activeTargets,token);
        await afterLine(token);
    }
    Task<string> SaveAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, CancellationToken token,
        IReadOnlyDictionary<string, string>? consumedMeasurements = null);
    Task<string> SaveCumulativeAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, bool initializeAllReviewRows,
        CancellationToken token, IReadOnlyDictionary<string,string>? consumedMeasurements = null)
        => initializeAllReviewRows
            ? throw new NotSupportedException("이 연결은 전체 측정 Offset 초기화 저장을 지원하지 않습니다.")
            : SaveAsync(source,values,sourceIdentity,measuredRows,token,consumedMeasurements);
    Task ExportOffsetBundleAsync(CManualInspectionSource source, IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,
        string path, bool isDraft, bool initializeAllReviewRows, CancellationToken token)
        => throw new NotSupportedException("이 연결은 통합 Offset 파일 내보내기를 지원하지 않습니다.");
    Task<IReadOnlyDictionary<string,ST_MANUAL_OFFSET>> ImportOffsetBundleAsync(CManualInspectionSource source,
        string path, CancellationToken token)
        => throw new NotSupportedException("이 연결은 통합 Offset 파일 불러오기를 지원하지 않습니다.");
    void Stop();
}

public sealed record CManualInspectionSource(
    ST_MANUAL_CONTEXT Context, IReadOnlyList<ST_MANUAL_POINT> Points,
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> Values, ST_MANUAL_LIMITS Limits,
    ManualInspectionFieldEvaluator FieldEvaluator, double ToleranceXUm, double ToleranceYUm,
    string SourcePath, string Description)
{
    public bool ReadOnly { get; init; }
    /// <summary>Explicitly stored offsets, including zero rows. Missing rows are not invented as applied.</summary>
    public IReadOnlySet<string> AppliedOffsetKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, CManualInspectionImportedResult> ImportedResults { get; init; }
        = new Dictionary<string, CManualInspectionImportedResult>(StringComparer.Ordinal);
    public IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM> RecentResults { get; init; } = [];
    public string ObservationFingerprint { get; init; } = "";
    public IReadOnlyList<Views.CManualInspectionUiCellLayout> CellLayouts { get; init; } = [];
    public double GlassWidthMm { get; init; }
    public double GlassHeightMm { get; init; }
    /// <summary>Explicit Cell reset scope, including saved legacy rows absent from the displayed plan.</summary>
    public IReadOnlyList<int> InitializationCellNos { get; init; } = [];
    /// <summary>Shot keys explicitly reset in this draft; retained reset timestamps are not renewed.</summary>
    public IReadOnlySet<string> InitializationOffsetKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Distinguishes a deliberate final state map from old full-Init API callers.</summary>
    public bool HasExplicitMeasurementState { get; init; }
    /// <summary>Authoritative empty/initialized saved Shot state, independent of newer unsaved raw.</summary>
    public IReadOnlySet<string>? StoredInitializedOffsetKeys { get; init; }
    public double ShiftOffsetXmm { get; init; }
    public double ShiftOffsetYmm { get; init; }
    public CManualInspectionSavedOffsetSnapshot? SavedOffsets { get; init; }
}

/// <summary>
/// An immutable read of the authoritative saved model, not an engine baseline, measurement or
/// correction receipt. Missing rows and explicit Init remain empty even when their numeric neutral is zero.
/// </summary>
public sealed record CManualInspectionSavedOffsetSnapshot(
    string RecipeId,string RunId,string CoordinateSignature,string RevisionId,
    string FilePath,string FileName,DateTimeOffset? SavedAt,string ContentSha256,int RowCount,int InitializedRowCount,
    IReadOnlyDictionary<string,ST_MANUAL_OFFSET> ValuesByPointKey,IReadOnlySet<string> InitializedOffsetKeys,
    IReadOnlySet<string> AppliedPointKeys);

public sealed record CManualInspectionImportedResult(string Path, DateTimeOffset SavedAt, string Origin);

public sealed record CManualInspectionMeasurement(string Key, ST_MANUAL_MEASUREMENT Measurement,
    double ReviewXmm, double StageYmm);

/// <summary>
/// Production adapter for the NEW tab only. It reuses the owning Review manager's execution lock,
/// Process Move/IOF handshake, arrived/busy verification and existing CSV writer. Existing Correction
/// workflows neither call this adapter nor share its candidate/session state.
/// </summary>
public sealed class CManualInspectionBackend(
    CReviewManager review, CRecipeManager recipes, CSettingManager settings,
    CReviewResultFileBase results, CReviewOffsetFileBase offsets,
    Func<string> selectedRecipe, CManager? manager, Action guard) : IManualInspectionBackend
{
    private ST_RECIPE_DEFINITION? _recipe;
    private ST_REVIEW_OFFSET_FILE_DATA? _latest;
    private Dictionary<string, string> _parameters = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ST_REVIEW_PLAN_POINT> _targets = new(StringComparer.Ordinal);
    private Dictionary<int, ST_REVIEW_PLAN> _plans = [];
    private Dictionary<string, ST_HEAD_SHOT_POINT> _shots = new(StringComparer.Ordinal);
    private Dictionary<string, ST_MANUAL_OFFSET> _baseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedResult> _resultCache = new(StringComparer.OrdinalIgnoreCase);
    private string _sourceRun = "";
    private bool _sourceSimulation;
    private double _shiftX, _shiftY;
    private string _legacyInitializationRevision = "";
    private bool _legacyInitializationConfirmed;
    public IReadOnlyDictionary<string, ST_MANUAL_OFFSET> LastSavedValues { get; private set; } = new Dictionary<string, ST_MANUAL_OFFSET>();
    public IReadOnlyList<KeyValuePair<string, string>> ConsumedMeasurements { get; private set; } = [];
    public bool IsSimulation => manager?.Interface().IsSimulation == true && manager.Motion().IsSimulation;
    public string StateDirectory => Path.Combine(Directory.GetParent(results.RootPath)?.FullName ?? results.RootPath, "ManualInspection");
    public IReadOnlyList<string> WatchRoots => results.SearchRootPaths.Append(offsets.RootPath)
        .Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlySet<string> ImportedInitializedOffsetKeys { get; private set; } = new HashSet<string>(StringComparer.Ordinal);
    public string ImportedOffsetDescription { get; private set; } = "";

    public void GuardOperation()
    {
        guard();
        if (manager is null) throw new InvalidOperationException("장비 Manager가 연결되지 않았습니다.");
        var station = manager.Station();
        if (station.StageRequests.IsRunning || station.StageRequests.NeedsCleanup
            || manager.Interface().StageReviewSequences.IsActive
            || station.ManualMofStatus is { IsRunning: true } or { RequiresStop: true })
            throw new InvalidOperationException("AUTO/Manual MOF 또는 정지 후 정리 작업이 진행 중입니다.");
        if (manager.Alarm().CurAlarm.Any(a => a.Severity == EN_ALARM_LEVEL.Critical))
            throw new InvalidOperationException("현재 HEAVY 알람을 해제한 후 다시 실행하세요.");
    }

    public async Task<CManualInspectionSource> LoadAsync(string? resultPath, CancellationToken token, IReadOnlyList<string>? displayKeys = null)
    {
        var selected = selectedRecipe();
        _recipe = (await ReadRecipesOffUiAsync(token)).SingleOrDefault(r => r.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("현재 선택된 Recipe가 없습니다. Recipe를 선택한 후 불러오세요.");
        // Capture the run BEFORE asynchronous result/offset/plan work. A new product or machining
        // completion during loading must not relabel old Shift/observations as the new run.
        var capturedRun = RunIdentity();
        var legacyRun = LegacyRunIdentity();
        _sourceSimulation = IsSimulation;
        var file = string.IsNullOrWhiteSpace(resultPath) ? null : await ReadResult(resultPath, token);
        if (file is not null && !file.RecipeId.Equals(_recipe.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("측정 결과와 현재 Recipe가 다릅니다. 다른 Recipe의 위치로 이동하지 않습니다.");
        var fileMeasurementModeMatches = file is null || file.IsStageSimulation==_sourceSimulation && file.IsVisionSimulation==_sourceSimulation;
        _parameters = Parameters(_recipe);
        _latest = await ReadLatestOffsetOffUiAsync(_recipe.Id, token);
        _latest = await RestoreLegacyInitializationMetadata(_latest, token);
        ImportedInitializedOffsetKeys = new HashSet<string>(StringComparer.Ordinal);
        ImportedOffsetDescription = "";
        CReviewOffsetData.ApplyToParameters(_parameters, _latest);
        var split = Int(_parameters, "SPLITED_BEAM_COUNT");
        if (split <= 0) throw new InvalidOperationException("SPLITED_BEAM_COUNT를 확인하세요.");
        var defaultBeam = Math.Min(11, checked(split * split));
        var completion = manager?.Station().LastCompletedManualMof;
        var product = manager?.Product().Current;
        // The persisted completion record has no Glass/Recipe key. A completion predating this
        // product cannot supply its Shift or prove its result run.
        var resolvedCompletion = ResolveCompletion(_recipe.Id, product?.RecipeId, product?.CreatedAt, completion);
        completion = resolvedCompletion.Completion;
        var currentGlass = product is not null && product.RecipeId.Equals(_recipe.Id, StringComparison.OrdinalIgnoreCase)
            ? product.GlassId : "";
        var minimumTime = product?.CreatedAt;
        if (completion is not null && (minimumTime is null || completion.CompletedAt > minimumTime)) minimumTime = completion.CompletedAt;
        var currentFrame = new ST_MANUAL_RESULT_FRAME(_recipe.Id, currentGlass, minimumTime,
            completion?.ShiftOffsetX ?? 0, completion?.ShiftOffsetY ?? 0, _sourceSimulation);
        var catalog = (await results.ListCorrectionResults(token))
            .Where(item => item.RecipeId.Equals(_recipe.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.SavedAt).ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var external = new List<ST_MANUAL_EXTERNAL_RESULT>();
        var rejectedFiles = 0;
        var rejectedRows = 0;
        if (file is null && currentFrame.IsLinked)
        {
            foreach (var item in catalog)
            {
                token.ThrowIfCancellationRequested();
                if (!item.GlassId.Equals(currentGlass, StringComparison.OrdinalIgnoreCase)
                    || item.SavedAt < minimumTime || item.ResultStatus == "INVALID") continue;
                try
                {
                    var candidate = await ReadResult(item.FilePath, token);
                    if (!CManualInspectionResultProjection.IsSameFrame(currentFrame, candidate)
                        || candidate.Rows.Any(row => (row.BeamNo > 0 ? row.BeamNo : candidate.BeamNo) is var b && (b < 1 || b > split * split)))
                    { rejectedFiles++; continue; }
                    if (!candidate.Rows.Any(row => row.DesignX.HasValue && row.DesignY.HasValue
                        && row.ResultSource is "MEASURED" or "EDITED" or "MANUAL" or "ERROR")) continue;
                    var origin = string.Join(" · ", new[] { item.ResultType, candidate.Rule, candidate.Origin, candidate.RunType }
                        .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase));
                    external.Add(new(candidate, origin));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
                { rejectedFiles++; }
            }
        }
        var beamNos = (file is not null ? file.Rows.Select(r => r.BeamNo > 0 ? r.BeamNo : file.BeamNo)
            : external.SelectMany(item => item.File.Rows.Where(row => row.DesignX.HasValue && row.DesignY.HasValue
                && row.ResultSource is "MEASURED" or "EDITED" or "MANUAL" or "ERROR")
                .Select(row => row.BeamNo > 0 ? row.BeamNo : item.File.BeamNo)))
            .Append(defaultBeam).Distinct().ToArray();
        var requestedKeys = displayKeys?.ToHashSet(StringComparer.Ordinal);
        if (displayKeys is not null)
        {
            if (displayKeys.Count == 0 || requestedKeys!.Count != displayKeys.Count) throw new InvalidDataException("세션 위치가 비었거나 중복됐습니다.");
            var extra = displayKeys.Select(key => Regex.Match(key, @"/B(\d+)$"))
                .Select(m => m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
            beamNos = beamNos.Concat(extra).Distinct().ToArray();
        }
        if (beamNos.Any(b => b < 1 || b > split * split)) throw new InvalidOperationException("결과 파일의 Beam 번호가 현재 Recipe 범위를 벗어납니다.");
        _shiftX = file?.ShiftOffsetX ?? completion?.ShiftOffsetX ?? 0;
        _shiftY = file?.ShiftOffsetY ?? completion?.ShiftOffsetY ?? 0;
        _parameters["SHIFT_OFFSET_X"] = _shiftX.ToString("R", CultureInfo.InvariantCulture);
        _parameters["SHIFT_OFFSET_Y"] = _shiftY.ToString("R", CultureInfo.InvariantCulture);
        _plans = await Task.Run(() => beamNos.ToDictionary(b => b, b => review.CreateAllPlan(_recipe, b, _shiftX, _shiftY)), token);
        var process = await Task.Run(() => CShotCoordinatePlanBuilder.Build(_parameters), token);
        _shots = process.Shots.ToDictionary(s => OffsetKey(s.CellNo, s.CellShotNo), StringComparer.Ordinal);
        var baselineByRow = _latest.Rows.ToDictionary(r => $"{r.CellId}:{Normalize(r.ShotId)}", StringComparer.OrdinalIgnoreCase);
        var fileRows = new Dictionary<string, ST_REVIEW_RESULT_FILE_ROW>(StringComparer.OrdinalIgnoreCase);
        var observations = new Dictionary<string, ST_MANUAL_IMPORTED_OBSERVATION>(StringComparer.OrdinalIgnoreCase);
        if (file is not null)
        {
            foreach (var row in file.Rows)
            {
                var beam = row.BeamNo > 0 ? row.BeamNo : file.BeamNo;
                var key = $"{row.CellNo}:{Normalize(row.ShotName)}:B{beam}";
                if (!fileRows.TryAdd(key, row)) throw new InvalidDataException($"동일 Cell/Shot/Beam 결과가 중복됐습니다: {key}");
                observations[key] = new(row, file.FilePath, file.RunType.Length > 0 ? file.RunType : "직접 연 결과");
            }
        }
        else
        {
            var projection = CManualInspectionResultProjection.Merge(currentFrame, external, _plans.Values.SelectMany(p => p.Points));
            rejectedFiles += projection.RejectedFileCount;
            observations = new(projection.Rows, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in observations) fileRows[pair.Key] = pair.Value.Row;
            rejectedRows = projection.RejectedRowCount;
        }
        _sourceRun = capturedRun;
        // An explicitly opened CSV is an intentional input to the CURRENT Recipe/Run, after
        // Recipe/Head/Beam/design-position validation below. Historical Glass identity remains
        // provenance; it is not a blanket prohibition on reinspection or correction.
        // Automatic observations still do not acquire a correction receipt.
        var lineage = true;
        var context = new ST_MANUAL_CONTEXT(_recipe.Id, _sourceRun, Revision(_latest), CoordinateSignature())
        { LineageValid = lineage, SourceResultPath = file?.FilePath ?? "" };
        var points = new List<ST_MANUAL_POINT>();
        var bases = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var appliedKeys = new HashSet<string>(StringComparer.Ordinal);
        var emptyOffsetKeys = new HashSet<string>(StringComparer.Ordinal);
        var imported = new Dictionary<string, CManualInspectionImportedResult>(StringComparer.Ordinal);
        _targets.Clear(); _baseline.Clear();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var beam in beamNos)
        {
            foreach (var target in _plans[beam].Points)
            {
                token.ThrowIfCancellationRequested();
                // One normal view uses Beam11. Additional Beams are present only where imported data exists.
                var matrix = CReviewShotNameFormatter.ToMatrixName(target.ShotNo, target.PixelCountX);
                var rowKey = $"{target.CellNo}:{Normalize(matrix)}:B{beam}";
                var row = fileRows.GetValueOrDefault(rowKey);
                if (beam != defaultBeam && row is null && requestedKeys is null) continue;
                if (row is not null)
                {
                    if (row.HeadNo != target.HeadNo || (row.DesignX.HasValue && Math.Abs(row.DesignX.Value - target.DesignX) > .000001)
                        || (row.DesignY.HasValue && Math.Abs(row.DesignY.Value - target.DesignY) > .000001))
                        throw new InvalidDataException($"{rowKey}: 결과의 Head/설계 좌표와 현재 Recipe가 다릅니다.");
                    matched.Add(rowKey);
                }
                var key = $"C{target.CellNo}/H{target.HeadNo}/S{target.ShotNo}/B{beam}";
                if (requestedKeys is not null && !requestedKeys.Contains(key)) continue;
                var p = new ST_MANUAL_POINT(key, target.CellNo, target.HeadNo, target.ShotNo, beam, target.DesignX, target.DesignY)
                { Row = (target.ShotNo - 1) / Math.Max(1, target.PixelCountX), Column = (target.ShotNo - 1) % Math.Max(1, target.PixelCountX),
                  Masked = !target.Use || target.HeadNo <= 0 || target.ExclusionReason.Length > 0 };
                var basis = baselineByRow.TryGetValue($"{target.CellNo}:{Normalize(matrix)}", out var offset)
                    ? CManualInspectionCoordinateBridge.FromFile(offset, _parameters, Math.Max(1, target.HeadNo)) : ST_MANUAL_OFFSET.Zero;
                if (offset is not null) appliedKeys.Add(key);
                p = p with { HasAppliedOffsetEvidence=offset is { IsMeasurementOffsetInitialized: false }
                    && (offset.IsDesignCoordinate || target.HeadNo is >= 1 and <= 8) };
                if (offset is null || offset.IsMeasurementOffsetInitialized) emptyOffsetKeys.Add(p.OffsetKey);
                if (observations.TryGetValue(rowKey, out var observation))
                    imported[key] = new(observation.FilePath, observation.Row.SavedAt, observation.Origin);
                if (row is not null && row.Judge is "OK" or "NG")
                {
                    var design = CManualInspectionCoordinateBridge.FromStage(row.ErrorX, row.ErrorY, _parameters, Math.Max(1, target.HeadNo));
                    var measurement = Measurement(context, key, target.HeadNo, row.Judge, design,
                        row.ErrorX, row.ErrorY, FormattableString.Invariant($"Stage-mm:{row.ErrorX:R},{row.ErrorY:R};Judge:{row.Judge}"))
                        with { MeasuredAt = row.SavedAt };
                    p = p with { Judge = row.Judge, Source = "IMPORTED", ErrorXUm = design.XUm, ErrorYUm = design.YUm,
                        // An EDITED/MANUAL legacy row has a known working value, but its missing
                        // original raw must not be invented from that value.
                        OriginalMeasurement = row.ResultSource is "MEASURED" or "RETAINED" ? measurement : null, MeasurementVersion = 0 };
                    if (file is not null && fileMeasurementModeMatches)
                        p = p with { AdoptedFileMeasurement = measurement with
                        {
                            RawData = $"FILE RESULT · source={row.ResultSource} · glass={file.GlassId} · {measurement.RawData}"
                        } };
                }
                else if (row is not null && row.Judge == "ERROR") p = p with { Judge = "ERROR", Source = "IMPORTED",
                    OriginalMeasurement = new("ERROR", null, null, "", context.RecipeId, context.RunId, context.CoordinateSignature)
                    { RawData = "IMPORTED ERROR · 원본 수신 실패, 숫자 편차 없음", MeasuredAt = row.SavedAt } };
                if (row is not null && p.Source == "IMPORTED") p = p with { ExternalObservedAt = row.SavedAt };
                // A retained raw from before Init is provenance, not a fresh working measurement.
                // Each row keeps its own reset time across unrelated partial saves.
                if (offset?.IsMeasurementOffsetInitialized == true
                    && (row is null || row.SavedAt <= (offset.MeasurementInitializedAt ?? _latest.SavedAt)))
                    p = p with { IsMeasurementOffsetInitialized = true, Judge = "UNMEASURED", Source = "OFFSET_INIT",
                        ErrorXUm = null, ErrorYUm = null, AdoptedFileMeasurement = null };
                else if(offset is null && p.Judge is not ("OK" or "NG"))
                    p=p with { IsMeasurementOffsetInitialized=true };
                points.Add(p); bases[key] = basis; _targets[key] = target;
                _baseline[p.OffsetKey] = basis;
            }
        }
        if (matched.Count != fileRows.Count) throw new InvalidDataException("현재 Recipe에서 찾을 수 없는 결과 위치가 있습니다. 행을 생략해 적용하지 않습니다.");
        if (requestedKeys is not null && requestedKeys.Count != points.Count) throw new InvalidDataException("세션 위치를 실제 Recipe에서 전부 확인하지 못했습니다.");
        var widths = Enumerable.Range(1, 8).Select(h => Number(_parameters, $"H{h:00}_SCAN_FIELD_WIDTH_X", 110)).Where(x => x > 0).ToArray();
        var limits = new ST_MANUAL_LIMITS(30, 200, widths.DefaultIfEmpty(110).Min());
        var plan = _plans[defaultBeam];
        var consumed = new List<KeyValuePair<string, string>>();
        var appliedSources = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var receipts = Path.Combine(StateDirectory, "ApplySources", Digest(context.RunId));
        if (Directory.Exists(receipts)) foreach (var path in Directory.EnumerateFiles(receipts, "point-*.json"))
        {
            token.ThrowIfCancellationRequested();
            var reservation = JsonSerializer.Deserialize<Reservation>(await System.IO.File.ReadAllTextAsync(path, token))
                ?? throw new InvalidDataException("보정 소비 기록이 비었습니다.");
            if (reservation.RunId != context.RunId) continue;
            if (!appliedSources.TryGetValue(reservation.SourcePath, out var applied))
                appliedSources[reservation.SourcePath] = applied = await offsets.FindCommittedSourceApplication(context.RecipeId, reservation.SourcePath, token) is not null;
            if (applied) consumed.Add(new(reservation.PointKey, reservation.MeasurementToken));
        }
        ConsumedMeasurements = consumed.AsReadOnly();
        var legacyReason = await LegacyStateReason(legacyRun, product?.CreatedAt, context, token);
        var source = new CManualInspectionSource(context, points, bases, limits, Field, plan.ToleranceX * 1000, plan.ToleranceY * 1000,
            file?.FilePath ?? "", $"Beam {defaultBeam} · Shift {_shiftX:0.###}/{_shiftY:0.###} mm · DESIGN µm · {points.Count:N0}점"
                + (imported.Count == 0 ? "" : $" · 다른 경로 결과 {imported.Count:N0}점 연결 · IMPORTED")
                + (file is null ? "" : " · 불러온 값을 현재 Recipe 작업에 사용 · 이동/측정/검증 후 보정 가능")
                + (fileMeasurementModeMatches ? "" : " · 파일의 SIM/실측 모드가 현재와 다름: 이동·새 측정은 가능하며 새 실측값으로 보정하세요.")
                + (file is null && !currentFrame.IsLinked ? " · 현재 기판/회차 미확인: 결과는 목록에서 선택 조회" : "")
                + (rejectedFiles == 0 && rejectedRows == 0 ? "" : $" · 불일치/읽기 실패 {rejectedFiles:N0}파일/{rejectedRows:N0}행 자동 제외")
                + (legacyReason.Length > 0 ? " · 이전 작업 이력은 보존합니다. 현재 Recipe 기준으로 새 측정·보정을 진행할 수 있습니다." : "")
                + (file is null && resolvedCompletion.Ambiguous ? " · 현재 Product의 가공 Shift 출처가 달라 현재 Recipe의 Shift 0 기준을 사용합니다. 현재 위치를 확인하고 새로 측정하세요." : ""))
        { ReadOnly = false, AppliedOffsetKeys = appliedKeys, ImportedResults = imported,StoredInitializedOffsetKeys=emptyOffsetKeys,
          CellLayouts = BuildCellLayouts(_parameters), GlassWidthMm = Number(_parameters,"GLASS_SIZE_X",0), GlassHeightMm = Number(_parameters,"GLASS_SIZE_Y",0),
          RecentResults = catalog, ObservationFingerprint = ObservationFingerprint(external, file, _latest, context.CoordinateSignature),
          ShiftOffsetXmm=_shiftX,ShiftOffsetYmm=_shiftY };
        source=source with { SavedOffsets=SavedSnapshot(source,_latest) };
        // Loading is read-only, but the published coordinate context must be internally current.
        // This also detects same-path offset replacement through the store's content identity.
        await EnsureCurrentAsync(source, token);
        return source;
    }

    /// <summary>
    /// Mirrors Recipe preview's cell boundary (CMenuRecipe.BuildCellBoundaryGeometry), not point
    /// bounding boxes or a guessed row/column order. Pixel centres start at FirstTarget; the cell
    /// outline includes half a pixel on each edge. Display metadata never changes processing math.
    /// </summary>
    public static IReadOnlyList<Views.CManualInspectionUiCellLayout> BuildCellLayouts(IReadOnlyDictionary<string,string> parameters)
    {
        if (!CCellPatternCalculator.TryParseCellCount(parameters.GetValueOrDefault("MAX_CELL_NUMBER"),out var count,out var reason))
            throw new InvalidDataException(reason);
        var margins = CAlignKeyMarginSettings.Read(parameters);
        var pixel = Number(parameters,"PIXEL_SIZE",.09);
        var modelCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameters);
        var cells = new List<Views.CManualInspectionUiCellLayout>();
        for (var cell = 1; cell <= count; cell++)
        {
            var model = CRecipeModelPixelCountReader.ReadCellModelType(parameters,cell,modelCount);
            var pattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters,model);
            var definition = new ST_CELL_PATTERN_DEFINITION(cell,
                Number(parameters,$"CELL{cell}_ALIGN_TO_1ST_PIXEL_X",0),Number(parameters,$"CELL{cell}_ALIGN_TO_1ST_PIXEL_Y",0),
                Number(parameters,$"CELL{cell}_ROTATION",0),pixel,pattern.PixelCountX,pattern.PixelCountY,pattern.ProcessPitch,
                Int(parameters,"SPLITED_BEAM_COUNT"),margins.X,margins.Y);
            var layout = CCellPatternCalculator.Calculate(definition,includeShotCenters:false);
            if (!layout.IsValid) throw new InvalidDataException(layout.ValidationMessage);
            var angle = definition.RotationDegrees * Math.PI / 180; var cos = Math.Cos(angle); var sin = Math.Sin(angle);
            var half = pixel / 2; var width = (pattern.PixelCountX - .5) * pixel; var height = (pattern.PixelCountY - .5) * pixel;
            var local = new[] { (-half,-half),(width,-half),(width,height),(-half,height) };
            var boundary = local.Select(p => new Views.CManualInspectionUiLayoutVertex(
                margins.X+definition.FirstTargetX+p.Item1*cos-p.Item2*sin,
                margins.Y+definition.FirstTargetY+p.Item1*sin+p.Item2*cos)).ToArray();
            if (boundary.Any(p=> !double.IsFinite(p.Xmm) || !double.IsFinite(p.Ymm))) throw new InvalidDataException("Cell 배치 좌표가 유한하지 않습니다.");
            cells.Add(new() { CellNo=cell,BoundaryMm=Array.AsReadOnly(boundary) });
        }
        return cells.AsReadOnly();
    }

    /// <summary>
    /// The 11.21.1 writer had no per-row state. Recover only a proven global OFFSET_INIT commit
    /// targeting this exact active file; numerical zeros by themselves never prove an Init.
    /// This read-only compatibility lookup is cached by immutable active-file revision.
    /// </summary>
    private async Task<ST_REVIEW_OFFSET_FILE_DATA> RestoreLegacyInitializationMetadata(
        ST_REVIEW_OFFSET_FILE_DATA latest, CancellationToken token)
    {
        if (latest.HasMeasurementStateMetadata || string.IsNullOrWhiteSpace(latest.FilePath)) return latest;
        var revision = latest.RecipeId + "|" + Revision(latest);
        if (_legacyInitializationRevision != revision)
        {
            _legacyInitializationConfirmed = false;
            var directory = Path.Combine(StateDirectory, "ApplySources");
            if (latest.Rows.Count > 0 && latest.Rows.All(r => r.ReviewOffsetX == 0 && r.ReviewOffsetY == 0)
                && Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    if (Path.GetFileName(path).StartsWith("point-", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(path, token));
                        var root = document.RootElement;
                        if (!root.TryGetProperty("Kind", out var kind) || kind.GetString() != "OFFSET_INIT"
                            || !root.TryGetProperty("RecipeId", out var recipe)
                            || !string.Equals(recipe.GetString(), latest.RecipeId, StringComparison.OrdinalIgnoreCase)) continue;
                        var committed = await offsets.FindCommittedSourceApplication(latest.RecipeId, path, token);
                        if (committed is not null
                            && string.Equals(Path.GetFileName(committed.OffsetFile), latest.FileName, StringComparison.OrdinalIgnoreCase)
                            // File names retain milliseconds; the ledger's O timestamp can keep finer ticks.
                            && committed.AppliedAt.ToUnixTimeMilliseconds() == latest.SavedAt.ToUnixTimeMilliseconds())
                        {
                            _legacyInitializationConfirmed = true;
                            break;
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                        or InvalidOperationException or ArgumentException)
                    {
                        // An unrelated unreadable legacy receipt supplies no state evidence.
                    }
                }
            _legacyInitializationRevision = revision;
        }
        return !_legacyInitializationConfirmed ? latest : latest with
        {
            Rows = latest.Rows.Select(r => r with
                { IsMeasurementOffsetInitialized = true, MeasurementInitializedAt = latest.SavedAt }).ToArray()
        };
    }

    private async Task<string> LegacyStateReason(string legacyRun, DateTimeOffset? productCreatedAt,
        ST_MANUAL_CONTEXT context, CancellationToken token)
    {
        const string message = "이전 버전의 회차 이력은 보존합니다. 현재 모델에서 새 측정할 수 있으며 옛 측정값을 새 원측정으로 자동 승격하지 않습니다.";
        var directory = Path.Combine(StateDirectory, "ApplySources", Digest(legacyRun));
        var checkedSources = new Dictionary<string, ST_REVIEW_OFFSET_SOURCE_APPLICATION?>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory)) foreach (var path in Directory.EnumerateFiles(directory, "point-*.json"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var marker = JsonSerializer.Deserialize<Reservation>(await System.IO.File.ReadAllTextAsync(path, token));
                if (marker is null) return message;
                if (marker.RunId != legacyRun) continue;
                if (!checkedSources.TryGetValue(marker.SourcePath, out var application))
                    checkedSources[marker.SourcePath] = application = await offsets.FindCommittedSourceApplication(context.RecipeId, marker.SourcePath, token);
                if (application is not null && (productCreatedAt is null || application.AppliedAt >= productCreatedAt)) return message;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            { return message + " 기록 확인 필요: " + error.Message; }
        }
        // Do not reinterpret an interrupted old-format session as a fresh empty workspace. Old
        // JSON remains untouched; schema/run tokens are deliberately never relabelled as new raw.
        var oldRevision = string.IsNullOrWhiteSpace(_latest!.FilePath) ? "INITIAL_ZERO" : Path.GetFullPath(_latest.FilePath);
        var sessionPath = Path.Combine(StateDirectory, "Sessions", Digest(context.RecipeId + "|" + legacyRun
            + "|" + context.CoordinateSignature + "|" + oldRevision) + ".json");
        if (System.IO.File.Exists(sessionPath))
        {
            try
            {
                using var document = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(sessionPath, token));
                var session = document.RootElement.GetProperty("Session").Deserialize<ST_MANUAL_SESSION>();
                if (session is null) return message;
                if (session.Context.RecipeId == context.RecipeId && session.Context.RunId == legacyRun
                    && (productCreatedAt is null || session.Audit.Any(a => a.At >= productCreatedAt))
                    && (session.ConsumedMeasurementHistory.Count > 0
                        || session.Operation is { Kind: not EN_MANUAL_OPERATION_KIND.Move, RemainingKeys.Count: > 0 }
                        || session.Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied })) return message;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
            { return message + " 기록 확인 필요: " + error.Message; }
        }
        return "";
    }

    private async Task<ST_REVIEW_RESULT_FILE_DATA> ReadResult(string path, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        // A copied replacement can preserve file time and length. Compare bytes as well, so a
        // filesystem change event cannot keep showing an old cache entry for the same pathname.
        var (stamp, beforeDigest) = await Task.Run(() => (FileStamp(fullPath), System.IO.File.Exists(fullPath) ? DigestFile(fullPath) : ""), token);
        if (_resultCache.TryGetValue(fullPath, out var cached) && stamp.Length > 0 && cached.Stamp == stamp && cached.Sha256 == beforeDigest)
            return cached.File;
        var loaded = await Task.Run(() => results.Load(fullPath, token), token);
        var (after, digest) = await Task.Run(() => (FileStamp(fullPath), System.IO.File.Exists(fullPath)
            ? DigestFile(fullPath) : Digest(JsonSerializer.Serialize(loaded))), token);
        if (stamp != after) throw new IOException("측정 결과가 저장 중입니다. 저장 완료 후 다시 연결합니다.");
        if (beforeDigest.Length > 0 && digest != beforeDigest) throw new IOException("측정 결과 내용이 변경 중입니다. 저장 완료 후 다시 연결합니다.");
        if (stamp.Length > 0) _resultCache[fullPath] = new(stamp, loaded, digest);
        return loaded;
    }

    private string ObservationFingerprint(IReadOnlyList<ST_MANUAL_EXTERNAL_RESULT> external,
        ST_REVIEW_RESULT_FILE_DATA? explicitFile, ST_REVIEW_OFFSET_FILE_DATA latest, string coordinateSignature)
    {
        var paths = external.Select(item => item.File.FilePath).Concat(explicitFile is null ? [] : new[] { explicitFile.FilePath })
            .Append(latest.FilePath).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        return Digest(coordinateSignature + "\n" + string.Join("\n", paths.Select(path => Path.GetFullPath(path) + "|"
            + FileStamp(path) + "|" + (_resultCache.TryGetValue(Path.GetFullPath(path), out var cached) ? cached.Sha256
                : System.IO.File.Exists(path) ? DigestFile(path) : ""))));
    }

    private static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "";
    }
    private sealed record CachedResult(string Stamp, ST_REVIEW_RESULT_FILE_DATA File, string Sha256);

    public async Task EnsureCurrentAsync(CManualInspectionSource source, CancellationToken token)
    {
        if (_recipe is null || !selectedRecipe().Equals(_recipe.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("선택 Recipe가 바뀌었습니다. 현재 Recipe를 다시 불러오세요.");
        var recipeId = _recipe.Id;
        // These stores can finish their Task synchronously while parsing/hashing CSV on the
        // caller. Keep only file/cache work off the UI; device, selection and frame checks
        // remain on the owning context and are repeated after the asynchronous boundary.
        await ReadRecipesOffUiAsync(token);
        var current = await ReadLatestOffsetOffUiAsync(recipeId, token);
        token.ThrowIfCancellationRequested();
        if (!selectedRecipe().Equals(recipeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("선택 Recipe가 바뀌었습니다. 현재 Recipe를 다시 불러오세요.");
        var recipe = (await recipes.LoadRecipes(token)).SingleOrDefault(r => r.Id == recipeId)
            ?? throw new InvalidOperationException("현재 Recipe가 없습니다.");
        if (Signature(Parameters(recipe)) + ShiftSignature() != source.Context.CoordinateSignature)
            throw new InvalidOperationException("Recipe 또는 좌표 Setting이 바뀌었습니다. 다시 불러오세요.");
        if (RunIdentity() != source.Context.RunId)
            throw new InvalidOperationException("기판/가공 완료 회차가 바뀌었습니다. 새 작업공간을 불러오세요.");
        if (IsSimulation != _sourceSimulation)
            throw new InvalidOperationException("설비/시뮬레이션 모드가 바뀌었습니다. 현재 모드에서 다시 불러오세요.");
        if (_latest is null || Revision(current) != Revision(_latest) || Revision(current) != source.Context.RevisionId)
            throw new InvalidOperationException("다른 작업에서 활성 보정이 바뀌었습니다. 현재 보정을 다시 불러오세요.");
    }

    private Task<IReadOnlyList<ST_RECIPE_DEFINITION>> ReadRecipesOffUiAsync(CancellationToken token)
        => Task.Run(() => recipes.LoadRecipes(token), token);

    private Task<ST_REVIEW_OFFSET_FILE_DATA> ReadLatestOffsetOffUiAsync(string recipeId, CancellationToken token)
        => Task.Run(() => offsets.LoadLatest(recipeId, token), token);

    public async Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(
        CManualInspectionSource source,CancellationToken token)
        => (await ReadSavedState(source,token)).Source.SavedOffsets;

    public async Task<CManualInspectionSavedOffsetSnapshot?> ExportFinalOffsetBundleAsync(
        CManualInspectionSource source,string path,CancellationToken token)
    {
        // Use a detached adapter and the full current model. A draft, an old opened CSV or a
        // filtered display must never become the active saved map merely because it was exported.
        var saved=await ReadSavedState(source,token);
        await saved.Adapter.ExportOffsetBundleAsync(saved.Source,saved.Source.Values,path,false,false,token);
        return saved.Source.SavedOffsets;
    }

    /// <summary>Copy the authoritative CSV byte-for-byte, never a measurement or unsaved draft.
    /// Download names in the live folder must not join LoadLatest's *_reviewoffset.csv candidates.</summary>
    public async Task<CManualInspectionSavedOffsetSnapshot?> ExportCurrentOffsetCsvAsync(
        CManualInspectionSource source,string path,CancellationToken token,bool overwriteConfirmed=false)
    {
        var saved = await ReadSavedState(source,token);
        var snapshot = saved.Source.SavedOffsets!;
        if (string.IsNullOrWhiteSpace(snapshot.FilePath))
            throw new InvalidDataException("저장된 최종 Review Offset CSV가 없습니다. 모델 Offset을 먼저 저장하세요.");
        var origin = Path.GetFullPath(snapshot.FilePath);
        var destination = RequireOffsetCsvExportPath(origin,path,overwriteConfirmed);
        await using var input = new FileStream(origin,FileMode.Open,FileAccess.Read,FileShare.Read);
        var actualSha = Convert.ToHexString(await SHA256.HashDataAsync(input,token));
        if (!actualSha.Equals(snapshot.ContentSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("내보낼 최종 CSV가 변경됐습니다. 현재 파일을 다시 확인하세요.");
        input.Position = 0;
        await saved.Adapter.EnsureCurrentAsync(saved.Source,token);
        if (destination.Equals(origin,StringComparison.OrdinalIgnoreCase)) return snapshot;
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await using (var output = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                await input.CopyToAsync(output,token);
                await output.FlushAsync(token); output.Flush(true);
            }
            if (!DigestFile(temporary).Equals(actualSha,StringComparison.OrdinalIgnoreCase))
                throw new IOException("내보낸 CSV의 원문 SHA를 확인하지 못했습니다. 최종 파일은 만들지 않았습니다.");
            await saved.Adapter.EnsureCurrentAsync(saved.Source,token);
            token.ThrowIfCancellationRequested();
            RequireOffsetCsvExportPath(origin,destination,overwriteConfirmed);
            System.IO.File.Move(temporary,destination,overwrite:overwriteConfirmed);
            return snapshot;
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }

    private string RequireOffsetCsvExportPath(string origin,string path,bool overwriteConfirmed)
    {
        var destination = Path.GetFullPath(path);
        if (!Path.GetExtension(destination).Equals(".csv",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("현재 Offset은 .csv 파일로 내보내세요.");
        var name = Path.GetFileName(destination);
        if (Regex.IsMatch(name,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)
            || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException("일반 CSV 파일 이름을 사용하세요.");
        AssertNoLinkedCsvPath(destination);
        if (destination.Equals(origin,StringComparison.OrdinalIgnoreCase)) return destination;
        var live = Path.GetDirectoryName(origin)!;
        var directory = Path.GetDirectoryName(destination)!;
        var inLiveDirectory = directory.Equals(live,StringComparison.OrdinalIgnoreCase);
        if (inLiveDirectory && name.EndsWith("_reviewoffset.csv",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("이 폴더의 *_reviewoffset.csv 이름은 활성 보정 이력용입니다. 예: 비교용_export.csv로 내보내세요. 기존 활성 파일은 바꾸지 않습니다.");
        foreach (var root in new[]{Directory.GetParent(offsets.RootPath)?.FullName ?? offsets.RootPath,
            Directory.GetParent(results.RootPath)?.FullName ?? results.RootPath,StateDirectory})
            if (!inLiveDirectory && IsInsidePath(destination,root))
                throw new InvalidDataException("다른 모델·설정·측정·자동보관 폴더에는 내보내지 않습니다. 현재 CSV 폴더 또는 일반 사용자 폴더를 선택하세요.");
        if (!inLiveDirectory && (FindGitAncestor(directory) is not null || IsRecipeConfigurationPath(directory)))
            throw new InvalidDataException("다른 프로젝트 또는 Recipe 설정 폴더에는 내보내지 않습니다. 일반 사용자 폴더를 선택하세요.");
        if (Directory.Exists(destination) || System.IO.File.Exists(destination) && !overwriteConfirmed)
            throw new InvalidDataException("동일한 이름의 파일이 이미 있습니다. 저장창에서 덮어쓰기를 직접 확인하거나 다른 이름을 선택하세요.");
        return destination;
    }

    private static bool IsInsidePath(string path,string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        return path.Equals(prefix,StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    private static string? FindGitAncestor(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current=current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName,".git")) || System.IO.File.Exists(Path.Combine(current.FullName,".git"))) return current.FullName;
        return null;
    }
    private static bool IsRecipeConfigurationPath(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current=current.Parent)
            if (current.Name.Equals("RECIPE",StringComparison.OrdinalIgnoreCase)
                && current.Parent?.Name.Equals("Config",StringComparison.OrdinalIgnoreCase)==true) return true;
        return false;
    }
    private static string? FindRecipeOffsetDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current=current.Parent)
            if (current.Name.EndsWith("_reviewoffset",StringComparison.OrdinalIgnoreCase)
                && current.Parent?.Name.Equals("RECIPE",StringComparison.OrdinalIgnoreCase)==true) return current.FullName;
        return null;
    }
    private static void AssertNoLinkedCsvPath(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current is not null; current=current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("연결된 경로에는 CSV를 내보내지 않습니다. 실제 폴더를 선택하세요.");
        if (System.IO.File.Exists(path) && (System.IO.File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("연결된 CSV 파일은 자동으로 바꾸지 않습니다.");
    }

    private sealed record SavedRead(CManualInspectionBackend Adapter,CManualInspectionSource Source);

    private async Task<SavedRead> ReadSavedState(CManualInspectionSource source,CancellationToken token)
    {
        // None of the fields belonging to the live motion/candidate adapter are modified here.
        // This permits a read while a different tab saves, or this workspace keeps a pending draft.
        var reader=new CManualInspectionBackend(review,recipes,settings,results,offsets,selectedRecipe,manager,guard);
        var selected=selectedRecipe();
        if(!selected.Equals(source.Context.RecipeId,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("선택 모델이 달라 최종 저장 보정을 연결하지 않았습니다.");
        reader._recipe=(await reader.ReadRecipesOffUiAsync(token)).SingleOrDefault(recipe=>recipe.Id.Equals(selected,StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("현재 모델이 없습니다.");
        reader._sourceSimulation=IsSimulation;
        reader._sourceRun=reader.RunIdentity();
        reader._shiftX=source.ShiftOffsetXmm;reader._shiftY=source.ShiftOffsetYmm;
        reader._parameters=reader.Parameters(reader._recipe);
        if(reader._sourceRun!=source.Context.RunId || reader.CoordinateSignature()!=source.Context.CoordinateSignature)
            throw new InvalidOperationException("모델·기판 회차·좌표 기준이 달라 최종 저장 보정을 연결하지 않았습니다.");
        reader._latest=await Task.Run(()=>offsets.LoadLatest(selected,token),token);
        if(!reader._latest.RecipeId.Equals(selected,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("최종 보정 파일의 모델이 다릅니다.");
        reader._latest=await reader.RestoreLegacyInitializationMetadata(reader._latest,token);
        CReviewOffsetData.ApplyToParameters(reader._parameters,reader._latest);
        reader._parameters["SHIFT_OFFSET_X"]=reader._shiftX.ToString("R",CultureInfo.InvariantCulture);
        reader._parameters["SHIFT_OFFSET_Y"]=reader._shiftY.ToString("R",CultureInfo.InvariantCulture);
        var split=Int(reader._parameters,"SPLITED_BEAM_COUNT");
        if(split<1) throw new InvalidDataException("최종 보정의 Beam 구성을 확인하세요.");
        var beams=source.Points.Select(point=>point.BeamNo).Append(Math.Min(11,checked(split*split))).Distinct().ToArray();
        if(beams.Any(beam=>beam<1 || beam>checked(split*split))) throw new InvalidDataException("최종 보정의 Beam 범위가 다릅니다.");
        reader._plans=await Task.Run(()=>beams.ToDictionary(beam=>beam,
            beam=>review.CreateAllPlan(reader._recipe,beam,reader._shiftX,reader._shiftY)),token);
        var process=await Task.Run(()=>CShotCoordinatePlanBuilder.Build(reader._parameters),token);
        reader._shots=process.Shots.ToDictionary(shot=>OffsetKey(shot.CellNo,shot.CellShotNo),StringComparer.Ordinal);
        var rows=reader._latest.Rows.ToDictionary(row=>$"{row.CellId}:{Normalize(row.ShotId)}",StringComparer.OrdinalIgnoreCase);
        var points=new List<ST_MANUAL_POINT>();
        var values=new Dictionary<string,ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var applied=new HashSet<string>(StringComparer.Ordinal);
        var empty=new HashSet<string>(StringComparer.Ordinal);
        foreach(var target in reader._plans.Values.SelectMany(plan=>plan.Points))
        {
            token.ThrowIfCancellationRequested();
            var key=$"C{target.CellNo}/H{target.HeadNo}/S{target.ShotNo}/B{target.BeamNo}";
            var point=new ST_MANUAL_POINT(key,target.CellNo,target.HeadNo,target.ShotNo,target.BeamNo,target.DesignX,target.DesignY)
            {
                Row=(target.ShotNo-1)/Math.Max(1,target.PixelCountX),Column=(target.ShotNo-1)%Math.Max(1,target.PixelCountX),
                Masked=!target.Use || target.HeadNo<=0 || target.ExclusionReason.Length>0
            };
            var matrix=CReviewShotNameFormatter.ToMatrixName(target.ShotNo,target.PixelCountX);
            rows.TryGetValue($"{target.CellNo}:{Normalize(matrix)}",out var row);
            if(row is { IsDesignCoordinate:false,IsMeasurementOffsetInitialized:false } && target.HeadNo is <1 or >8)
                throw new InvalidDataException($"최종 보정의 Head가 미배정되어 SCANNER→DESIGN 변환을 확정할 수 없습니다. 파일: {reader._latest.FilePath} · Cell {target.CellNo} / Shot {matrix}. 실제 Head 배정 또는 원본 좌표계를 확인하세요.");
            var basis=row is null?ST_MANUAL_OFFSET.Zero:CManualInspectionCoordinateBridge.FromFile(row,reader._parameters,Math.Max(1,target.HeadNo));
            if(row is not null) applied.Add(key);
            if(row is null || row.IsMeasurementOffsetInitialized) empty.Add(point.OffsetKey);
            point=point with { IsMeasurementOffsetInitialized=empty.Contains(point.OffsetKey),HasAppliedOffsetEvidence=row is { IsMeasurementOffsetInitialized:false }
                && (row.IsDesignCoordinate || target.HeadNo is >=1 and <=8) };
            points.Add(point);values.Add(key,basis);reader._targets.Add(key,target);reader._baseline[point.OffsetKey]=basis;
        }
        var geometry=points.ToDictionary(point=>point.Key,StringComparer.Ordinal);
        foreach(var original in source.Points)
            if(!geometry.TryGetValue(original.Key,out var actual) || original.CellNo!=actual.CellNo || original.HeadNo!=actual.HeadNo
                || original.ShotNo!=actual.ShotNo || original.BeamNo!=actual.BeamNo || original.Masked!=actual.Masked
                || original.XMm!=actual.XMm || original.YMm!=actual.YMm)
                throw new InvalidDataException("작업 화면과 현재 모델의 실제 위치가 달라 최종 저장 보정을 연결하지 않았습니다.");
        var current=source with
        {
            Context=source.Context with { RevisionId=Revision(reader._latest),SourceResultPath="" },
            Points=points.ToArray(),Values=new ReadOnlyDictionary<string,ST_MANUAL_OFFSET>(values),AppliedOffsetKeys=applied.ToFrozenSet(StringComparer.Ordinal),
            StoredInitializedOffsetKeys=empty.ToFrozenSet(StringComparer.Ordinal),SourcePath="",SavedOffsets=null,
            InitializationCellNos=[],InitializationOffsetKeys=new HashSet<string>(StringComparer.Ordinal),HasExplicitMeasurementState=true
        };
        current=current with { SavedOffsets=SavedSnapshot(current,reader._latest) };
        await reader.EnsureCurrentAsync(current,token);
        return new(reader,current);
    }

    private static CManualInspectionSavedOffsetSnapshot SavedSnapshot(CManualInspectionSource source,ST_REVIEW_OFFSET_FILE_DATA file)
        => new(source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature,Revision(file),file.FilePath,file.FileName,
            string.IsNullOrWhiteSpace(file.FilePath)?null:file.SavedAt,file.ContentSha256,file.Rows.Count,file.Rows.Count(row=>row.IsMeasurementOffsetInitialized),
            new ReadOnlyDictionary<string,ST_MANUAL_OFFSET>(source.Values.ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal)),
            (source.StoredInitializedOffsetKeys??source.Points.Where(point=>point.IsMeasurementOffsetInitialized).Select(point=>point.OffsetKey).ToHashSet(StringComparer.Ordinal)).ToFrozenSet(StringComparer.Ordinal),
            source.AppliedOffsetKeys.ToFrozenSet(StringComparer.Ordinal));

    public async Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source, string key,
        EN_MANUAL_OPERATION_KIND kind, Action<string> progress, CancellationToken token)
    {
        GuardOperation(); await EnsureCurrentAsync(source, token);
        var point = _targets[key]; var plan = _plans[point.BeamNo];
        CStageReviewSequences.Session? positionLease = null;
        try
        {
          if (kind == EN_MANUAL_OPERATION_KIND.Measure)
          {
            // Inspection-only has no move inside ExecuteOneShot. Verify actual stopped feedback first.
            var interfaces = manager!.Interface();
            var number = interfaces.Melsec.Map.FirstOrDefault(r => r.Id == "MOTOR_STAGE_Y_CUR_POS")?.DeviceNo ?? 0;
            positionLease = interfaces.StageReviewSequences.Open(number, () => token.IsCancellationRequested);
            var motion = ST_REVIEW_MOTION_PARAMETERS.Read(settings);
            await positionLease.VerifyCurrentTarget(new(point.ReviewTargetX, point.ReviewTargetY, motion.Speed,
                plan.StageSpeed ?? motion.Speed, motion.Acceleration), token);
          }
        var action = kind == EN_MANUAL_OPERATION_KIND.Move ? EN_ONE_SHOT_ACTION.Move
            : kind == EN_MANUAL_OPERATION_KIND.Measure ? EN_ONE_SHOT_ACTION.Inspection : EN_ONE_SHOT_ACTION.MoveAndInspection;
        var measured = await review.ExecuteOneShot(plan, point, action, EN_ONE_SHOT_METHOD.MarkFindSingle, progress, token);
        if (!string.IsNullOrEmpty(measured.ResultSaveError)) throw new IOException("측정 결과 저장 실패: " + measured.ResultSaveError);
        var converted = CManualInspectionCoordinateBridge.FromVision(measured.Point.ErrorX, measured.Point.ErrorY,
            plan.VisionFlipType, _parameters, point.HeadNo);
        var stage = CReviewManager.VisionErrorToStageOffset(measured.Point.ErrorX, measured.Point.ErrorY, plan.VisionFlipType);
        var packet = FormattableString.Invariant($"Stage-mm:{stage.X:R},{stage.Y:R};Judge:{measured.Point.Judge}");
        return new(key, Measurement(source.Context, key, point.HeadNo, measured.Point.Judge, converted, stage.X, stage.Y, packet),
            point.ReviewTargetX, point.ReviewTargetY);
        }
        finally { positionLease?.Dispose(); }
    }

    public Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress, CancellationToken token)
        => ExecuteFlyingAsync(source,keys,measured,progress,_=>{},token);

    public Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress,
        Action<IReadOnlyList<string>> activeTargets, CancellationToken token)
        => ExecuteFlyingAsync(source,keys,measured,progress,activeTargets,_=>Task.CompletedTask,token);

    public async Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress,
        Action<IReadOnlyList<string>> activeTargets, Func<CancellationToken,Task> afterLine, CancellationToken token)
    {
        GuardOperation(); await EnsureCurrentAsync(source, token);
        foreach (var beamGroup in keys.Select(k => (Key: k, Target: _targets[k])).GroupBy(x => x.Target.BeamNo))
        {
            var lookup = beamGroup.ToDictionary(x => Normalize(x.Target.ShotKey), x => x.Key, StringComparer.OrdinalIgnoreCase);
            var plan = _plans[beamGroup.Key];
            var lines = beamGroup.GroupBy(x => (x.Target.CellNo, x.Target.HeadNo, Row: (x.Target.ShotNo - 1) / Math.Max(1, x.Target.PixelCountX)))
                .OrderBy(g => g.Key.CellNo).ThenBy(g => g.Key.HeadNo).ThenBy(g => g.Key.Row)
                .Select((g, i) => new ST_FINE_REVIEW_LINE($"MANUAL_{i + 1}", i + 1, 1,
                    g.Select(x => x.Target with { Use = true, State = EN_REVIEW_POINT_STATE.Ready, Judge = "WAIT" }).OrderBy(p => p.ReviewTargetX).ToArray())).ToArray();
            var received = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                GuardOperation(); await EnsureCurrentAsync(source, token);
                var request = CreateSingleFlyingRequest(plan, line,
                    new(_recipe!.Name, manager?.Product().Current?.GlassId ?? "", "MANUAL_INSPECTION", 1,
                        new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase)));
                try
                {
                activeTargets(line.Points.Select(p=>lookup[Normalize(p.ShotKey)]).ToArray());
                // A line is a saved-result transaction. Keep progress visible, but do not mark points
                // completed in the new engine until the owning manager saved the official result.
                var finished = await review.StartFineCorrection(request,
                    snapshot => progress($"Flying {line.Number}/{lines.Length} · {snapshot.Message}"), token);
                if (!string.IsNullOrEmpty(finished.ResultSaveError)) throw new IOException("Flying 결과 저장 실패: " + finished.ResultSaveError);
                foreach (var p in finished.Lines.SelectMany(l => l.Points).Where(p => p.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng))
                {
                    var key = lookup[Normalize(p.ShotKey)]; if (!received.Add(key)) continue;
                    var value = CManualInspectionCoordinateBridge.FromVision(p.ErrorX, p.ErrorY, plan.VisionFlipType, _parameters, p.HeadNo);
                    var stage = CReviewManager.VisionErrorToStageOffset(p.ErrorX, p.ErrorY, plan.VisionFlipType);
                    measured(new(key, Measurement(source.Context, key, p.HeadNo, p.Judge, value, stage.X, stage.Y,
                        FormattableString.Invariant($"Stage-mm:{stage.X:R},{stage.Y:R};Judge:{p.Judge}")), p.ReviewTargetX, p.ReviewTargetY));
                }
                if (finished.State != EN_REVIEW_SEQUENCE_STATE.Completed || line.Points.Any(p => !received.Contains(lookup[Normalize(p.ShotKey)])))
                    throw new InvalidOperationException("Flying 행이 전수 완료되지 않았습니다. 저장된 정상 응답은 보존하고 잔여 위치를 다시 측정하세요.");
                // Do not begin another motion/line before a due recovery checkpoint is durable.
                // The owner captures state; only its serializer/file writer runs on a worker.
                await afterLine(token);
                }
                finally { activeTargets([]); }
            }
            if (received.Count != beamGroup.Count())
                throw new InvalidOperationException("Flying 결과가 전수 완료되지 않았습니다. 미측정 위치를 다시 측정하세요.");
        }
    }

    internal static ST_FINE_REVIEW_REQUEST CreateSingleFlyingRequest(ST_REVIEW_PLAN plan,
        ST_FINE_REVIEW_LINE line, ST_FINE_REVIEW_CONTEXT context)
    {
        // Each official result save is a fresh one-line transaction, not a resume of the whole batch.
        // The manager requires contiguous local line numbers starting at 1. Keep the original key
        // and outer progress number while rebasing only the request-local number.
        var localLine = line with { Number = 1 };
        var executionPlan = plan with { Points = localLine.Points, ResultType = "ONE_LINE", InspectionMethod = "Flying" };
        return new(executionPlan, [localLine], EN_FINE_INSPECTION_METHOD.Flying, StartLineNo: 1)
        { StopOnNgLine = false, Context = context };
    }

    public Task<string> SaveAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, CancellationToken token,
        IReadOnlyDictionary<string, string>? consumedMeasurements = null)
        => SaveCoreAsync(source,values,sourceIdentity,measuredRows,false,token,consumedMeasurements);

    public Task<string> SaveCumulativeAsync(CManualInspectionSource source, IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,
        string sourceIdentity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows,bool initializeAllReviewRows,
        CancellationToken token,IReadOnlyDictionary<string,string>? consumedMeasurements=null)
        => SaveCoreAsync(source,values,sourceIdentity,measuredRows,initializeAllReviewRows,token,consumedMeasurements);

    private async Task<string> SaveCoreAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, bool initializeAllReviewRows,
        CancellationToken token, IReadOnlyDictionary<string,string>? consumedMeasurements)
    {
        GuardOperation(); await EnsureCurrentAsync(source, token);
        if (source.ReadOnly || !source.Context.LineageValid) throw new InvalidOperationException("작업 기준을 다시 확인한 후 새 측정·보정을 진행하세요.");
        ValidateWorkingValues(source,values);
        var rows = BuildWorkingRows(SourceWithSaveState(source,sourceIdentity),values,initializeAllReviewRows);
        var directory = Path.Combine(StateDirectory, "ApplySources", Digest(source.Context.RunId));
        var sourcePath = Path.Combine(directory, Digest(sourceIdentity) + ".json");
        // Per-measurement durable reservations prevent a crash or a different selected subset from
        // bypassing the same Run/raw guard. The existing apply history proves committed writes.
        var reservations = new List<string>();
        var isMeasurementIncrement=false;
        try
        {
            using var saveIdentity=JsonDocument.Parse(sourceIdentity);
            isMeasurementIncrement=saveIdentity.RootElement.ValueKind==JsonValueKind.Object&&saveIdentity.RootElement.TryGetProperty("IsMeasurementReplacement",out var mode)&&mode.ValueKind==JsonValueKind.True;
        }
        catch(JsonException) { } // Existing legacy source identities may be plain text.
        var pointsByOffset=source.Points.ToLookup(point=>point.OffsetKey,StringComparer.Ordinal);
        var driving = measuredRows.Where(r => r.MeasurementToken.Length > 0).SelectMany(row=>isMeasurementIncrement&&row.Source=="MANUAL"
            ?pointsByOffset[row.OffsetKey].Select(point=>new KeyValuePair<string,string>(point.Key,row.MeasurementToken))
            :new[]{new KeyValuePair<string,string>(row.Key,row.MeasurementToken)}).ToHashSet();
        // Preserve both a point's original observation token and its correction-driving token.
        var consumed = (consumedMeasurements ?? new Dictionary<string, string>()).Concat(driving).Distinct().ToArray();
        var reservationRevision = Revision(_latest!);
        await Task.Run(async () =>
        {
            Directory.CreateDirectory(directory);
            if (!System.IO.File.Exists(sourcePath)) AtomicText(sourcePath, sourceIdentity);
            foreach (var pair in consumed)
            {
                token.ThrowIfCancellationRequested();
                var marker = Path.Combine(directory, "point-" + Digest(source.Context.RunId + pair.Key + pair.Value) + ".json");
                if (System.IO.File.Exists(marker))
                {
                    var old = JsonSerializer.Deserialize<Reservation>(await System.IO.File.ReadAllTextAsync(marker, token))!;
                    if (await offsets.FindCommittedSourceApplication(source.Context.RecipeId, old.SourcePath, token) is not null)
                    {
                        if (driving.Contains(pair))
                            throw new InvalidOperationException($"{pair.Key}: 같은 Run의 측정 원본이 이미 적용됐습니다. 새 가공 후 재측정하세요.");
                        continue; // Already-consumed companion raw does not block a fresh driving measurement.
                    }
                    if (old.BaseRevision != reservationRevision) throw new InvalidOperationException("미완료 보정 저장 기록과 활성 파일이 다릅니다. 이력을 확인하세요.");
                }
                AtomicText(marker, JsonSerializer.Serialize(new Reservation(sourcePath, reservationRevision,
                    source.Context.RunId, pair.Key, pair.Value))); reservations.Add(marker);
            }
        }, token);
        // Build/validate the next pure coordinate plan before the authoritative file transaction.
        // After a successful writer return, no cancellable I/O or planning error may make the host
        // claim that a committed offset was not applied.
        var nextRows = rows.Values.Select(WrittenRow).ToArray();
        var nextLookup = nextRows.ToDictionary(row => $"{row.CellId}:{Normalize(row.ShotId)}", StringComparer.OrdinalIgnoreCase);
        var actualValues = source.Points.ToDictionary(p => p.Key, p =>
            nextLookup.TryGetValue($"{p.CellNo}:{Normalize(CReviewShotNameFormatter.ToMatrixName(p.ShotNo, _targets[p.Key].PixelCountX))}", out var row)
                ? CManualInspectionCoordinateBridge.FromFile(row, _parameters, Math.Max(1, p.HeadNo)) : ST_MANUAL_OFFSET.Zero);
        var nextParameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase);
        CReviewOffsetData.ApplyToParameters(nextParameters, _latest! with { Rows = nextRows });
        var rebuilt = await Task.Run(() => CShotCoordinatePlanBuilder.Build(nextParameters), token);
        var nextShots = rebuilt.Shots.ToDictionary(s => OffsetKey(s.CellNo, s.CellShotNo), StringComparer.Ordinal);
        manager?.Station().InvalidatePreparedReviewOffset();
        try
        {
            // Receipt I/O and plan building above can yield. Recheck external host/product/setting
            // changes at the write boundary as well as the writer's atomic offset-revision check.
            GuardOperation(); await EnsureCurrentAsync(source, token);
            var expected = _latest!;
            var saved = await Task.Run(() => offsets.SaveNewIfUnchanged(source.Context.RecipeId, expected.FilePath,
                nextRows.OrderBy(r => r.CellId).ThenBy(r => r.ShotId, StringComparer.OrdinalIgnoreCase).ToArray(), sourcePath, token,
                expectedLatestContentSha256: expected.ContentSha256), token);
            _latest = saved;
            LastSavedValues = actualValues;
            foreach (var p in source.Points) _baseline[p.OffsetKey] = actualValues[p.Key];
            _parameters = nextParameters;
            _shots = nextShots;
            return Revision(saved);
        }
        catch
        {
            // Keep a marker when the authoritative writer committed but subsequent host work failed.
            await Task.Run(async () =>
            {
                if (await offsets.FindCommittedSourceApplication(source.Context.RecipeId, sourcePath, CancellationToken.None) is null)
                    foreach (var path in reservations) System.IO.File.Delete(path);
            });
            throw;
        }
    }

    private Dictionary<string,ST_REVIEW_OFFSET_ROW> BuildWorkingRows(CManualInspectionSource source,
        IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,bool initializeAllReviewRows)
    {
        // Init covers the complete saved Review map, including legacy/unassigned rows not displayed
        // by this Recipe plan. Only the new version is written; no old measurement/offset file is deleted.
        var cells = source.InitializationCellNos.ToHashSet();
        var previousRows = _latest!.Rows.ToDictionary(r => $"{r.CellId}:{Normalize(r.ShotId)}",StringComparer.OrdinalIgnoreCase);
        var rows = _latest.Rows.Select(r=>initializeAllReviewRows || cells.Contains(r.CellId)
                ? r with { ReviewOffsetX=0,ReviewOffsetY=0,IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=null } : r)
            .ToDictionary(r => $"{r.CellId}:{Normalize(r.ShotId)}", StringComparer.OrdinalIgnoreCase);
        foreach (var group in source.Points.GroupBy(p => p.OffsetKey))
        {
            var point = group.First(); var final = values[point.Key];
            if (group.Any(p => values[p.Key] != final)) throw new InvalidOperationException($"{point.OffsetKey}: Beam별 보정값이 충돌합니다.");
            var initialized = initializeAllReviewRows && !source.HasExplicitMeasurementState || point.IsMeasurementOffsetInitialized;
            if (!(initializeAllReviewRows && !source.HasExplicitMeasurementState)
                && group.Any(p => p.IsMeasurementOffsetInitialized != initialized))
                throw new InvalidOperationException($"{point.OffsetKey}: Beam별 측정 초기화 상태가 충돌합니다.");
            if (initialized && final != ST_MANUAL_OFFSET.Zero)
                throw new InvalidDataException($"{point.OffsetKey}: 초기화된 측정 Offset은 X/Y 모두 0이어야 합니다.");
            var target = _targets[point.Key];
            var shotId = CReviewShotNameFormatter.ToMatrixName(point.ShotNo,target.PixelCountX);
            var rowKey = $"{point.CellNo}:{Normalize(shotId)}";
            var existing = previousRows.GetValueOrDefault(rowKey);
            // An absent row is neutral arithmetic, not a new reset event. Materializing it with
            // today's reset timestamp would hide an unrelated, freshly measured OK/NG on reload.
            if(existing is null && initialized && !initializeAllReviewRows && !cells.Contains(point.CellNo)
                && !source.InitializationOffsetKeys.Contains(point.OffsetKey)) continue;
            var renewInitialization = initialized && (initializeAllReviewRows || cells.Contains(point.CellNo)
                || source.InitializationOffsetKeys.Contains(point.OffsetKey) || existing?.IsMeasurementOffsetInitialized != true);
            // Preserve unselected/legacy/unassigned rows exactly; they are not newly approved candidates.
            if (existing is not null && !initializeAllReviewRows && !cells.Contains(point.CellNo) && !renewInitialization
                && _baseline.TryGetValue(point.OffsetKey, out var previous) && previous == final
                && (existing?.IsMeasurementOffsetInitialized ?? false) == initialized) continue;
            var row = CManualInspectionCoordinateBridge.ToFile(point, target.PixelCountX, final) with
            {
                IsMeasurementOffsetInitialized = initialized,
                MeasurementInitializedAt = initialized && !renewInitialization ? existing?.MeasurementInitializedAt : null
            };
            rows[rowKey] = row;
        }
        return rows;
    }

    private static CManualInspectionSource SourceWithSaveState(CManualInspectionSource source,string identity)
    {
        JsonDocument document;
        try { document=JsonDocument.Parse(identity); }
        catch(JsonException) { return source; } // Older callers used an opaque receipt identity.
        using(document)
        {
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object) return source;
            HashSet<string>? ReadKeys(string name)
            {
                if(!root.TryGetProperty(name,out var value)) return null;
                if(value.ValueKind!=JsonValueKind.Array || value.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
                    throw new InvalidDataException($"{name}: 측정 Offset 키 목록이 올바르지 않습니다.");
                var keys=value.EnumerateArray().Select(v=>v.GetString()!).ToHashSet(StringComparer.Ordinal);
                if(keys.Except(source.Points.Select(p=>p.OffsetKey),StringComparer.Ordinal).Any())
                    throw new InvalidDataException($"{name}: 현재 Recipe에 없는 측정 Offset 키입니다.");
                return keys;
            }
            var desired=ReadKeys("InitializedOffsetKeys");
            var actual=ReadKeys("InitializationOffsetKeys");
            var cells=source.InitializationCellNos;
            if(root.TryGetProperty("InitializationCellNos",out var cellList))
            {
                if(cellList.ValueKind!=JsonValueKind.Array || cellList.EnumerateArray().Any(v=>!v.TryGetInt32(out var n)||n<=0))
                    throw new InvalidDataException("초기화 Cell 범위가 올바르지 않습니다.");
                cells=cellList.EnumerateArray().Select(v=>v.GetInt32()).Distinct().ToArray();
            }
            return source with
            {
                Points=desired is null ? source.Points : source.Points.Select(p=>p with { IsMeasurementOffsetInitialized=desired.Contains(p.OffsetKey) }).ToArray(),
                InitializationOffsetKeys=actual ?? source.InitializationOffsetKeys,InitializationCellNos=cells,
                HasExplicitMeasurementState=desired is not null || source.HasExplicitMeasurementState
            };
        }
    }

    /// <summary>
    /// A single portable snapshot of the absolute accumulated Review map and its combined current
    /// scanner-coordinate result. Export is not a second active store and does not alter the Recipe.
    /// Protected parameters are explicitly reference-only and are never written by the importer.
    /// </summary>
    public async Task ExportOffsetBundleAsync(CManualInspectionSource source,
        IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string path,bool isDraft,
        bool initializeAllReviewRows,CancellationToken token)
    {
        await EnsureCurrentAsync(source,token);
        ValidateWorkingValues(source,values);
        var destination=RequireBundlePath(path);
        if (!isDraft && !initializeAllReviewRows)
        {
            // A new observation can make the screen measurable without having saved any offset.
            // An active export describes the persisted map, not that unsaved observation state.
            var activeRows=_latest!.Rows.ToDictionary(r=>$"{r.CellId}:{Normalize(r.ShotId)}",StringComparer.OrdinalIgnoreCase);
            source=source with
            {
                Points=source.Points.Select(p=>p with
                {
                    IsMeasurementOffsetInitialized=source.StoredInitializedOffsetKeys?.Contains(p.OffsetKey)==true
                        || activeRows.GetValueOrDefault($"{p.CellNo}:{Normalize(CReviewShotNameFormatter.ToMatrixName(p.ShotNo,_targets[p.Key].PixelCountX))}")?.IsMeasurementOffsetInitialized==true
                }).ToArray(),
                InitializationCellNos=[],InitializationOffsetKeys=new HashSet<string>(StringComparer.Ordinal),HasExplicitMeasurementState=true
            };
        }
        var workingRows=BuildWorkingRows(source,values,initializeAllReviewRows).Values.Select(WrittenRow).ToArray();
        var workingLookup=workingRows.ToDictionary(r=>$"{r.CellId}:{Normalize(r.ShotId)}",StringComparer.OrdinalIgnoreCase);
        var pointRows=source.Points.OrderBy(p=>p.CellNo).ThenBy(p=>p.ShotNo).ThenBy(p=>p.BeamNo).Select(p=>
            new ST_MANUAL_OFFSET_BUNDLE_POINT(p.Key,p.CellNo,p.HeadNo,p.ShotNo,p.BeamNo,p.XMm,p.YMm,p.Masked,
                CReviewShotNameFormatter.ToMatrixName(p.ShotNo,_targets[p.Key].PixelCountX))).ToArray();
        var known=source.Points.GroupBy(p=>p.OffsetKey).Select(g=>
        {
            var p=g.First(); var row=WrittenRow(CManualInspectionCoordinateBridge.ToFile(p,_targets[p.Key].PixelCountX,values[p.Key]));
            var stored=workingLookup.GetValueOrDefault($"{row.CellId}:{Normalize(row.ShotId)}");
            return new ST_MANUAL_OFFSET_BUNDLE_ROW(row.CellId,row.ShotId,row.ReviewOffsetX,row.ReviewOffsetY)
            { IsMeasurementOffsetInitialized=stored?.IsMeasurementOffsetInitialized ?? p.IsMeasurementOffsetInitialized,
              MeasurementInitializedAt=stored?.MeasurementInitializedAt };
        }).OrderBy(r=>r.CellNo).ThenBy(r=>r.ShotId,StringComparer.Ordinal).ToArray();
        var knownKeys=known.Select(r=>$"{r.CellNo}:{Normalize(r.ShotId)}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameters=new Dictionary<string,string>(_parameters,StringComparer.OrdinalIgnoreCase);
        CReviewOffsetData.ApplyToParameters(parameters,_latest! with { Rows=workingRows });
        var plan=await Task.Run(()=>CShotCoordinatePlanBuilder.Build(parameters),token);
        var bundle=new ST_MANUAL_OFFSET_BUNDLE
        {
            SchemaVersion=CManualInspectionOffsetBundle.Schema,ProgramVersion=CManualInspectionOffsetBundle.ProgramVersion,
            RecipeId=source.Context.RecipeId,CoordinateSignature=source.Context.CoordinateSignature,
            ActiveOffsetFileName=_latest!.FileName,ActiveOffsetContentSha256=_latest.ContentSha256,
            ExportedAt=DateTimeOffset.Now,IsDraft=isDraft,InitializeAllReviewRows=initializeAllReviewRows,
            ReviewCoordinateSpace="DESIGN",ReviewUnit="mm",FinalCoordinateSpace="SCANNER",FinalCoordinateUnit="mm",
            ProtectedSnapshotPolicy=CManualInspectionOffsetBundle.ReferenceOnly,Points=pointRows,ReviewRows=known,
            RetainedHistoricalRows=workingRows.Where(r=>!knownKeys.Contains($"{r.CellId}:{Normalize(r.ShotId)}"))
                .OrderBy(r=>r.CellId).ThenBy(r=>r.ShotId,StringComparer.Ordinal)
                .Select(r=>new ST_MANUAL_OFFSET_BUNDLE_HISTORY_ROW(r.CellId,r.ShotId,r.ReviewOffsetX,r.ReviewOffsetY,r.IsDesignCoordinate)
                    { IsMeasurementOffsetInitialized=r.IsMeasurementOffsetInitialized,MeasurementInitializedAt=r.MeasurementInitializedAt }).ToArray(),
            ProtectedParameters=parameters.Where(p=>CManualInspectionOffsetBundle.IsProtectedParameter(p.Key))
                .OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal),
            FinalCoordinates=plan.Shots.Select(s=>new ST_MANUAL_OFFSET_BUNDLE_SHOT(s.CellNo,s.HeadNo,s.CellShotNo,
                s.DesignX,s.DesignY,s.ScannerGx,s.ScannerGy,s.StageX,s.StageY,s.RecipeOffsetX,s.RecipeOffsetY,
                s.HeadDefaultOffsetX,s.HeadDefaultOffsetY,s.FirstBeamOffsetX,s.FirstBeamOffsetY,
                s.AlignOffsetX,s.AlignOffsetY,s.AlignScannerOffsetX,s.AlignScannerOffsetY,s.ReviewOffsetX,s.ReviewOffsetY,
                s.ShiftOffsetX,s.ShiftOffsetY,s.ShiftScannerOffsetX,s.ShiftScannerOffsetY,
                s.FinalScannerOffsetX,s.FinalScannerOffsetY,s.EncoderWaitPosition)).ToArray()
        };
        var text=CManualInspectionOffsetBundle.Serialize(bundle);
        await EnsureCurrentAsync(source,token);
        token.ThrowIfCancellationRequested();
        AtomicText(destination,text);
    }

    /// <summary>
    /// Returns absolute final values for a replacement draft. The caller must not add these values
    /// to current B. Neither the protected snapshot nor exported scanner commands are applied.
    /// </summary>
    public async Task<IReadOnlyDictionary<string,ST_MANUAL_OFFSET>> ImportOffsetBundleAsync(
        CManualInspectionSource source,string path,CancellationToken token)
    {
        ImportedInitializedOffsetKeys=new HashSet<string>(StringComparer.Ordinal);
        ImportedOffsetDescription="";
        await EnsureCurrentAsync(source,token);
        if (Path.GetExtension(path).Equals(".csv",StringComparison.OrdinalIgnoreCase))
            return await ImportOffsetCsvAsync(source,path,token);
        var bundle=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path,token));
        if(!bundle.RecipeId.Equals(source.Context.RecipeId,StringComparison.OrdinalIgnoreCase)
            || bundle.CoordinateSignature!=source.Context.CoordinateSignature)
            throw new InvalidDataException("통합 Offset의 Recipe·좌표/Setting·Shift 기준이 현재 작업과 다릅니다. 보호 설정을 자동 변경하지 않습니다.");
        var rows=bundle.ReviewRows.ToDictionary(r=>CManualInspectionOffsetBundle.RowKey(r.CellNo,r.ShotId),StringComparer.Ordinal);
        var currentRows=source.Points.Select(p=>CManualInspectionOffsetBundle.RowKey(p.CellNo,
            CReviewShotNameFormatter.ToMatrixName(p.ShotNo,_targets[p.Key].PixelCountX))).ToHashSet(StringComparer.Ordinal);
        if(!currentRows.SetEquals(rows.Keys))
            throw new InvalidDataException("통합 Offset의 전체 Cell/Shot 맵이 현재 모델과 다릅니다. 일부 Shot만 골라 적용하지 않습니다.");
        // Review offsets are shared by Cell/Shot, not by the Beam rows currently visible in the
        // workspace. Validate every exported Beam against a temporary real Recipe plan, then expand
        // the same absolute Shot value to the current display. Do not replace the live movement plan.
        var split=Int(_parameters,"SPLITED_BEAM_COUNT");
        var beams=bundle.Points.Select(p=>p.BeamNo).Distinct().ToArray();
        if(split<1 || beams.Any(b=>b<1 || b>checked(split*split)))
            throw new InvalidDataException("통합 Offset의 Beam 번호가 현재 모델 범위를 벗어납니다.");
        var recipe=_recipe!;var shiftX=_shiftX;var shiftY=_shiftY;
        var plans=await Task.Run(()=>beams.ToDictionary(b=>b,
            b=>review.CreateAllPlan(recipe,b,shiftX,shiftY).Points.ToDictionary(p=>(p.CellNo,p.ShotNo))),token);
        foreach(var imported in bundle.Points)
        {
            token.ThrowIfCancellationRequested();
            if(!plans[imported.BeamNo].TryGetValue((imported.CellNo,imported.ShotNo),out var expected))
                throw new InvalidDataException($"{imported.Key}: 현재 모델에 없는 Cell/Shot입니다.");
            var expectedKey=$"C{expected.CellNo}/H{expected.HeadNo}/S{expected.ShotNo}/B{imported.BeamNo}";
            var masked=!expected.Use || expected.HeadNo<=0 || expected.ExclusionReason.Length>0;
            var shot=CReviewShotNameFormatter.ToMatrixName(expected.ShotNo,expected.PixelCountX);
            if(imported.Key!=expectedKey || imported.HeadNo!=expected.HeadNo || imported.Masked!=masked || imported.ShotId!=shot
                || Math.Abs(imported.XMm-expected.DesignX)>.000001 || Math.Abs(imported.YMm-expected.DesignY)>.000001)
                throw new InvalidDataException($"{imported.Key}: 통합 Offset 위치·Head·Shot·Beam·Masking이 현재 Recipe와 다릅니다.");
        }
        var values=new Dictionary<string,ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        foreach(var current in source.Points)
        {
            token.ThrowIfCancellationRequested();
            var shot=CReviewShotNameFormatter.ToMatrixName(current.ShotNo,_targets[current.Key].PixelCountX);
            var row=rows[CManualInspectionOffsetBundle.RowKey(current.CellNo,shot)];
            var value=new ST_MANUAL_OFFSET(row.XMm*1000,row.YMm*1000);
            if(!value.IsFinite) throw new InvalidDataException("통합 Offset의 단위 변환이 표현 범위를 초과했습니다.");
            values.Add(current.Key,value);
        }
        ValidateWorkingValues(source,values);
        await EnsureCurrentAsync(source,token);
        ImportedInitializedOffsetKeys=source.Points.Where(p=>rows[CManualInspectionOffsetBundle.RowKey(p.CellNo,
                CReviewShotNameFormatter.ToMatrixName(p.ShotNo,_targets[p.Key].PixelCountX))].IsMeasurementOffsetInitialized)
            .Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        ImportedOffsetDescription=$"JSON 통합 Offset 초안 · 모델 {source.Context.RecipeId} · 좌표 기준 확인 완료 · 아직 모델 파일에 저장하지 않음";
        return values;
    }

    private async Task<IReadOnlyDictionary<string,ST_MANUAL_OFFSET>> ImportOffsetCsvAsync(
        CManualInspectionSource source,string path,CancellationToken token)
    {
        var latest = _latest ?? throw new InvalidOperationException("현재 모델 Offset을 먼저 불러오세요.");
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var ownDirectory = !string.IsNullOrWhiteSpace(latest.FilePath) ? Path.GetDirectoryName(latest.FilePath)!
            : Path.Combine(offsets.RootPath,source.Context.RecipeId+"_reviewoffset");
        var claimedModelDirectory = FindRecipeOffsetDirectory(directory);
        if (IsInsidePath(full,offsets.RootPath) && !IsInsidePath(full,ownDirectory)
            || claimedModelDirectory is not null && !Path.GetFileName(claimedModelDirectory)
                .Equals(Path.GetFileName(ownDirectory),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("다른 모델의 Recipe 보정 폴더에서 가져오지 않습니다. 현재 모델과 파일 출처를 확인하세요.");
        var file = await Task.Run(()=>Drilling.File.ReviewOffset.CReviewOffsetFile.ReadSnapshotFile(source.Context.RecipeId,full,token),token);
        var rows = new Dictionary<string,ST_REVIEW_OFFSET_ROW>(StringComparer.Ordinal);
        foreach (var row in file.Rows)
            if (!rows.TryAdd($"{row.CellId}:{Normalize(row.ShotId)}",row))
                throw new InvalidDataException("통합 CSV에 같은 Cell/Shot 이름을 가리키는 중복 행이 있습니다.");
        var known = source.Points.Select(p=>$"{p.CellNo}:{Normalize(CReviewShotNameFormatter.ToMatrixName(p.ShotNo,_targets[p.Key].PixelCountX))}").ToHashSet(StringComparer.Ordinal);
        var retained = latest.Rows.ToDictionary(row=>$"{row.CellId}:{Normalize(row.ShotId)}",StringComparer.Ordinal);
        var identicalActiveBytes = file.ContentSha256.Equals(latest.ContentSha256,StringComparison.OrdinalIgnoreCase);
        foreach (var pair in rows.Where(pair=>!known.Contains(pair.Key)))
            if (!retained.TryGetValue(pair.Key,out var existing)
                || pair.Value!=existing && !(identicalActiveBytes && !file.HasMeasurementStateMetadata))
                throw new InvalidDataException($"{pair.Key}: 현재 모델에 없는 새 행 또는 변경된 숨은 행입니다. 현재 모델의 전체 CSV를 확인하세요.");
        if (rows.Count>0 && !rows.Keys.Any(known.Contains) && !identicalActiveBytes)
            throw new InvalidDataException("CSV에 현재 모델의 Cell/Shot이 없습니다. 다른 모델 파일인지 확인하세요.");
        var values = new Dictionary<string,ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var initialized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in source.Points)
        {
            token.ThrowIfCancellationRequested();
            var shot = CReviewShotNameFormatter.ToMatrixName(point.ShotNo,_targets[point.Key].PixelCountX);
            rows.TryGetValue($"{point.CellNo}:{Normalize(shot)}",out var row);
            if (identicalActiveBytes && !file.HasMeasurementStateMetadata && row is not null)
                row = retained.GetValueOrDefault($"{point.CellNo}:{Normalize(shot)}") ?? row;
            if (row is { IsDesignCoordinate:false,IsMeasurementOffsetInitialized:false } && point.HeadNo is <1 or >8)
                throw new InvalidDataException($"{point.Key}: Head 미배정 상태에서 SCANNER CSV를 DESIGN으로 추정 변환하지 않습니다.");
            var value = row is null ? ST_MANUAL_OFFSET.Zero : CManualInspectionCoordinateBridge.FromFile(row,_parameters,Math.Max(1,point.HeadNo));
            if (!value.IsFinite) throw new InvalidDataException("CSV Offset의 µm 단위 변환이 유효 범위를 넘었습니다.");
            values.Add(point.Key,value);
            if (row is null || row.IsMeasurementOffsetInitialized) initialized.Add(point.OffsetKey);
        }
        ValidateWorkingValues(source,values);
        await EnsureCurrentAsync(source,token);
        ImportedInitializedOffsetKeys=initialized;
        ImportedOffsetDescription=$"CSV 통합 Offset 초안 · CSV에는 모델 정보 없음 · 현재 모델 {source.Context.RecipeId}의 Cell/Shot 확인 · 숨은행 {rows.Keys.Count(key=>!known.Contains(key))}개는 현재값 보존 · 아직 모델 파일에 저장하지 않음";
        return values;
    }

    private void ValidateWorkingValues(CManualInspectionSource source,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if(values.Count!=source.Points.Count || source.Points.Any(p=>!values.TryGetValue(p.Key,out var value)||!value.IsFinite)
            || source.Points.Any(p=>!_targets.ContainsKey(p.Key)))
            throw new InvalidDataException("통합 Offset에 현재 Recipe의 모든 위치와 유효한 최종값이 필요합니다.");
        foreach(var group in source.Points.GroupBy(p=>p.OffsetKey))
            if(group.Any(p=>values[p.Key]!=values[group.First().Key]))
                throw new InvalidDataException($"{group.Key}: 같은 Shot의 Beam별 최종 Offset이 충돌합니다.");
    }

    private static ST_REVIEW_OFFSET_ROW WrittenRow(ST_REVIEW_OFFSET_ROW row)
    {
        static double Written(double value)=>double.Parse(value.ToString("0.000000",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture);
        return row with { ReviewOffsetX=Written(row.ReviewOffsetX),ReviewOffsetY=Written(row.ReviewOffsetY) };
    }

    private string RequireBundlePath(string path)
    {
        var full=Path.GetFullPath(path);
        if(!Path.GetExtension(full).Equals(".json",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("통합 Offset은 .json 파일로 저장하세요.");
        foreach(var root in new[]{results.RootPath,offsets.RootPath,StateDirectory})
        {
            var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("통합 Offset 휴대 파일은 기존 Recipe·측정·자동보관 폴더 밖에 저장하세요. 기존 파일을 덮어쓰지 않습니다.");
        }
        return full;
    }

    public void Stop() => review.Stop();
    private ST_MANUAL_FIELD_RESULT Field(ST_MANUAL_POINT p, ST_MANUAL_OFFSET f, ST_MANUAL_LIMITS limits)
        => _shots.TryGetValue(p.OffsetKey, out var shot)
            ? CManualInspectionCoordinateBridge.EvaluateField(shot, _baseline[p.OffsetKey], f, _parameters, limits)
            : new(false, "현재 가공 좌표에 없는 Shot입니다.");
    private Dictionary<string, string> Parameters(ST_RECIPE_DEFINITION recipe)
    {
        var map = recipe.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Key)).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var item in settings.LoadSection(EN_SETTING_TAB.Option).Concat(settings.LoadSection(EN_SETTING_TAB.Motor)))
            if (!string.IsNullOrWhiteSpace(item.Key)) map[item.Key] = item.Value;
        return map;
    }
    private string RunIdentity()
    {
        var p = manager?.Product().Current; var c = manager?.Station().LastCompletedManualMof;
        return CreateRunIdentity(_recipe!.Id, p?.ProductId, p?.GlassId, p?.RecipeId, p?.CreatedAt,
            c?.CompletedAt, c?.ShiftOffsetX, c?.ShiftOffsetY);
    }
    private static (ST_MANUAL_MOF_COMPLETION? Completion, bool Ambiguous) ResolveCompletion(
        string selectedRecipeId, string? productRecipeId, DateTimeOffset? createdAt, ST_MANUAL_MOF_COMPLETION? completion)
    {
        if (completion is null || createdAt is not null && completion.CompletedAt < createdAt) return (null, false);
        if (productRecipeId is not null && !productRecipeId.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase))
            // The old completion format has no Recipe/Glass identity. Do not silently borrow its
            // nonzero Shift for another selected Recipe, or silently authorize a zero-shift move.
            return (null, completion.ShiftOffsetX != 0 || completion.ShiftOffsetY != 0);
        return (completion, false);
    }
    private string LegacyRunIdentity()
    {
        var p = manager?.Product().Current; var c = manager?.Station().LastCompletedManualMof;
        return "NATIVE:" + Digest($"{_recipe!.Id}|{p?.ProductId}|{p?.GlassId}|{c?.CompletedAt:O}");
    }
    private static string CreateRunIdentity(string recipe, string? product, string? glass,
        string? productRecipe, DateTimeOffset? createdAt, DateTimeOffset? completedAt,
        double? shiftX, double? shiftY)
        // Host IDs may repeat on a newly created Product. CreatedAt separates those physical runs;
        // completion Shift also belongs to the identity, rather than only the displayed plan.
        => "NATIVE:" + Digest(JsonSerializer.Serialize(new { recipe, product, glass, productRecipe,
            CreatedAt = createdAt?.ToString("O", CultureInfo.InvariantCulture),
            CompletedAt = completedAt?.ToString("O", CultureInfo.InvariantCulture), shiftX, shiftY }));
    private ST_MANUAL_MEASUREMENT Measurement(ST_MANUAL_CONTEXT context, string key, int head, string judge,
        ST_MANUAL_OFFSET design, double stageX, double stageY, string stableRaw)
    {
        var scanner = CScannerAxisTransform.Read(_parameters, Math.Max(1, head)).Apply(stageX, stageY);
        return new(judge, design.XUm, design.YUm, Digest(context.RunId + key + stableRaw),
            context.RecipeId, context.RunId, context.CoordinateSignature)
        { RawData = stableRaw, ScannerErrorXUm = head > 0 ? scanner.X * 1000 : null, ScannerErrorYUm = head > 0 ? scanner.Y * 1000 : null };
    }
    private static string Signature(IReadOnlyDictionary<string, string> values) => Digest(string.Join("\n",
        values.Where(p => !CReviewOffsetData.IsParameterKey(p.Key) && p.Key is not ("CURRENT_PROCESS_COUNT" or "SHIFT_OFFSET_X" or "SHIFT_OFFSET_Y")
            && !p.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) && !p.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
            && !p.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)));
    private string ShiftSignature() => FormattableString.Invariant($"|SHIFT:{_shiftX:R},{_shiftY:R}");
    private string CoordinateSignature() => Signature(_parameters) + ShiftSignature();
    public static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string DigestFile(string path) { using var stream = System.IO.File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Revision(ST_REVIEW_OFFSET_FILE_DATA value) => string.IsNullOrEmpty(value.FilePath) ? "INITIAL_ZERO"
        : Path.GetFullPath(value.FilePath) + (value.ContentSha256.Length > 0 ? "|SHA256:" + value.ContentSha256 : "");
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string OffsetKey(int cell, int shot) => $"C{cell}/S{shot}";
    private static int Int(IReadOnlyDictionary<string, string> p, string key) => int.Parse(p[key], CultureInfo.InvariantCulture);
    private static double Number(IReadOnlyDictionary<string, string> p, string key, double fallback)
        => p.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    public static void AtomicText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false)); System.IO.File.Move(tmp, path, true); }
        finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
    }
    private sealed record Reservation(string SourcePath, string BaseRevision, string RunId, string PointKey, string MeasurementToken);
}
