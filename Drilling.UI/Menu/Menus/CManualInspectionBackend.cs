using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Drilling.Common.Alarm;
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
    Task<CManualInspectionSource> LoadAsync(string? resultPath, CancellationToken token, IReadOnlyList<string>? displayKeys = null);
    Task EnsureCurrentAsync(CManualInspectionSource source, CancellationToken token);
    void GuardOperation();
    Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source, string key,
        EN_MANUAL_OPERATION_KIND kind, Action<string> progress, CancellationToken token);
    Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress, CancellationToken token);
    Task<string> SaveAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, CancellationToken token,
        IReadOnlyDictionary<string, string>? consumedMeasurements = null);
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
}

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
    public IReadOnlyDictionary<string, ST_MANUAL_OFFSET> LastSavedValues { get; private set; } = new Dictionary<string, ST_MANUAL_OFFSET>();
    public IReadOnlyList<KeyValuePair<string, string>> ConsumedMeasurements { get; private set; } = [];
    public bool IsSimulation => manager?.Interface().IsSimulation == true && manager.Motion().IsSimulation;
    public string StateDirectory => Path.Combine(Directory.GetParent(results.RootPath)?.FullName ?? results.RootPath, "ManualInspection");
    public IReadOnlyList<string> WatchRoots => [results.RootPath, offsets.RootPath];

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
        _recipe = (await recipes.LoadRecipes(token)).SingleOrDefault(r => r.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("현재 선택된 Recipe가 없습니다. Recipe를 선택한 후 불러오세요.");
        // Capture the run BEFORE asynchronous result/offset/plan work. A new product or machining
        // completion during loading must not relabel old Shift/observations as the new run.
        var capturedRun = RunIdentity();
        var legacyRun = LegacyRunIdentity();
        _sourceSimulation = IsSimulation;
        var file = string.IsNullOrWhiteSpace(resultPath) ? null : await ReadResult(resultPath, token);
        if (file is not null && !file.RecipeId.Equals(_recipe.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("측정 결과와 현재 Recipe가 다릅니다. 다른 Recipe의 위치로 이동하지 않습니다.");
        _parameters = Parameters(_recipe);
        _latest = await offsets.LoadLatest(_recipe.Id, token);
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
        var linkedGlass = file is null || CManualInspectionResultProjection.IsSameFrame(currentFrame, file);
        // Old CSV rows are IMPORTED, never native MEASURED anchors. Only a fresh native direct
        // measurement or a current representative may replace their working value.
        var lineage = linkedGlass;
        var context = new ST_MANUAL_CONTEXT(_recipe.Id, _sourceRun, Revision(_latest), CoordinateSignature())
        { LineageValid = lineage, SourceResultPath = file?.FilePath ?? "" };
        var points = new List<ST_MANUAL_POINT>();
        var bases = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var appliedKeys = new HashSet<string>(StringComparer.Ordinal);
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
                }
                else if (row is not null && row.Judge == "ERROR") p = p with { Judge = "ERROR", Source = "IMPORTED",
                    OriginalMeasurement = new("ERROR", null, null, "", context.RecipeId, context.RunId, context.CoordinateSignature)
                    { RawData = "IMPORTED ERROR · 원본 수신 실패, 숫자 편차 없음", MeasuredAt = row.SavedAt } };
                if (row is not null && p.Source == "IMPORTED") p = p with { ExternalObservedAt = row.SavedAt };
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
                + (imported.Count == 0 ? "" : $" · 다른 경로 결과 {imported.Count:N0}점 연결 · IMPORTED 조회값")
                + (file is null && !currentFrame.IsLinked ? " · 현재 기판/회차 미확인: 결과는 목록에서 선택 조회" : "")
                + (rejectedFiles == 0 && rejectedRows == 0 ? "" : $" · 불일치/읽기 실패 {rejectedFiles:N0}파일/{rejectedRows:N0}행 자동 제외")
                + (legacyReason.Length > 0 ? " · " + legacyReason : "")
                + (resolvedCompletion.Ambiguous ? " · 현재 Product와 선택 Recipe가 달라 가공 Shift의 출처를 확인할 수 없습니다. Product/Recipe를 맞춘 후 다시 불러오세요." : ""))
        { ReadOnly = !lineage || legacyReason.Length > 0 || resolvedCompletion.Ambiguous, AppliedOffsetKeys = appliedKeys, ImportedResults = imported,
          RecentResults = catalog, ObservationFingerprint = ObservationFingerprint(external, file, _latest, context.CoordinateSignature) };
        // Loading is read-only, but the published coordinate context must be internally current.
        // This also detects same-path offset replacement through the store's content identity.
        await EnsureCurrentAsync(source, token);
        return source;
    }

    private async Task<string> LegacyStateReason(string legacyRun, DateTimeOffset? productCreatedAt,
        ST_MANUAL_CONTEXT context, CancellationToken token)
    {
        const string message = "이전 버전의 같은 회차 작업 이력이 있습니다. 원본 이력을 보존한 조회 전용입니다. 새 기판/가공 회차에서 다시 불러오세요.";
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
        var recipe = (await recipes.LoadRecipes(token)).SingleOrDefault(r => r.Id == _recipe.Id)
            ?? throw new InvalidOperationException("현재 Recipe가 없습니다.");
        if (Signature(Parameters(recipe)) + ShiftSignature() != source.Context.CoordinateSignature)
            throw new InvalidOperationException("Recipe 또는 좌표 Setting이 바뀌었습니다. 다시 불러오세요.");
        if (RunIdentity() != source.Context.RunId)
            throw new InvalidOperationException("기판/가공 완료 회차가 바뀌었습니다. 새 작업공간을 불러오세요.");
        if (IsSimulation != _sourceSimulation)
            throw new InvalidOperationException("설비/시뮬레이션 모드가 바뀌었습니다. 현재 모드에서 다시 불러오세요.");
        var current = await offsets.LoadLatest(_recipe.Id, token);
        if (_latest is null || Revision(current) != Revision(_latest) || Revision(current) != source.Context.RevisionId)
            throw new InvalidOperationException("다른 작업에서 활성 보정이 바뀌었습니다. 현재 보정을 다시 불러오세요.");
    }

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

    public async Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress, CancellationToken token)
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

    public async Task<string> SaveAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, CancellationToken token,
        IReadOnlyDictionary<string, string>? consumedMeasurements = null)
    {
        GuardOperation(); await EnsureCurrentAsync(source, token);
        if (source.ReadOnly || !source.Context.LineageValid) throw new InvalidOperationException("조회 전용 자료는 활성 보정에 저장할 수 없습니다.");
        var rows = _latest!.Rows.ToDictionary(r => $"{r.CellId}:{Normalize(r.ShotId)}", StringComparer.OrdinalIgnoreCase);
        foreach (var group in source.Points.GroupBy(p => p.OffsetKey))
        {
            var point = group.First(); var final = values[point.Key];
            if (group.Any(p => values[p.Key] != final)) throw new InvalidOperationException($"{point.OffsetKey}: Beam별 보정값이 충돌합니다.");
            // Preserve unselected/legacy/unassigned rows exactly; they are not newly approved candidates.
            if (_baseline.TryGetValue(point.OffsetKey, out var previous) && previous == final) continue;
            var target = _targets[point.Key];
            var row = CManualInspectionCoordinateBridge.ToFile(point, target.PixelCountX, final);
            rows[$"{row.CellId}:{Normalize(row.ShotId)}"] = row;
        }
        var directory = Path.Combine(StateDirectory, "ApplySources", Digest(source.Context.RunId)); Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, Digest(sourceIdentity) + ".json");
        if (!System.IO.File.Exists(sourcePath)) AtomicText(sourcePath, sourceIdentity);
        // Per-measurement durable reservations prevent a crash or a different selected subset from
        // bypassing the same Run/raw guard. The existing apply history proves committed writes.
        var reservations = new List<string>();
        var driving = measuredRows.Where(r => r.MeasurementToken.Length > 0).ToDictionary(r => r.Key, r => r.MeasurementToken);
        var consumed = consumedMeasurements ?? driving;
        foreach (var pair in consumed)
        {
            var marker = Path.Combine(directory, "point-" + Digest(source.Context.RunId + pair.Key + pair.Value) + ".json");
            if (System.IO.File.Exists(marker))
            {
                var old = JsonSerializer.Deserialize<Reservation>(await System.IO.File.ReadAllTextAsync(marker, token))!;
                if (await offsets.FindCommittedSourceApplication(source.Context.RecipeId, old.SourcePath, token) is not null)
                {
                    if (driving.GetValueOrDefault(pair.Key) == pair.Value)
                        throw new InvalidOperationException($"{pair.Key}: 같은 Run의 측정 원본이 이미 적용됐습니다. 새 가공 후 재측정하세요.");
                    continue; // Already-consumed companion raw does not block a fresh driving measurement.
                }
                if (old.BaseRevision != Revision(_latest)) throw new InvalidOperationException("미완료 보정 저장 기록과 활성 파일이 다릅니다. 이력을 확인하세요.");
            }
            AtomicText(marker, JsonSerializer.Serialize(new Reservation(sourcePath, Revision(_latest),
                source.Context.RunId, pair.Key, pair.Value))); reservations.Add(marker);
        }
        // Build/validate the next pure coordinate plan before the authoritative file transaction.
        // After a successful writer return, no cancellable I/O or planning error may make the host
        // claim that a committed offset was not applied.
        static double Written(double number) => double.Parse(number.ToString("0.000000", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var nextRows = rows.Values.Select(row => row with { ReviewOffsetX = Written(row.ReviewOffsetX), ReviewOffsetY = Written(row.ReviewOffsetY) }).ToArray();
        var nextLookup = nextRows.ToDictionary(row => $"{row.CellId}:{Normalize(row.ShotId)}", StringComparer.OrdinalIgnoreCase);
        var actualValues = source.Points.ToDictionary(p => p.Key, p =>
            nextLookup.TryGetValue($"{p.CellNo}:{Normalize(CReviewShotNameFormatter.ToMatrixName(p.ShotNo, _targets[p.Key].PixelCountX))}", out var row)
                ? CManualInspectionCoordinateBridge.FromFile(row, _parameters, Math.Max(1, p.HeadNo)) : ST_MANUAL_OFFSET.Zero);
        var nextParameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase);
        CReviewOffsetData.ApplyToParameters(nextParameters, _latest with { Rows = nextRows });
        var rebuilt = await Task.Run(() => CShotCoordinatePlanBuilder.Build(nextParameters), token);
        var nextShots = rebuilt.Shots.ToDictionary(s => OffsetKey(s.CellNo, s.CellShotNo), StringComparer.Ordinal);
        manager?.Station().InvalidatePreparedReviewOffset();
        try
        {
            // Receipt I/O and plan building above can yield. Recheck external host/product/setting
            // changes at the write boundary as well as the writer's atomic offset-revision check.
            GuardOperation(); await EnsureCurrentAsync(source, token);
            var saved = await offsets.SaveNewIfUnchanged(source.Context.RecipeId, _latest.FilePath,
                nextRows.OrderBy(r => r.CellId).ThenBy(r => r.ShotId, StringComparer.OrdinalIgnoreCase).ToArray(), sourcePath, token,
                expectedLatestContentSha256: _latest.ContentSha256);
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
            if (await offsets.FindCommittedSourceApplication(source.Context.RecipeId, sourcePath, CancellationToken.None) is null)
                foreach (var path in reservations) System.IO.File.Delete(path);
            throw;
        }
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
