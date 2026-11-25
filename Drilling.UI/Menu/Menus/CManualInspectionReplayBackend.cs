using System.Globalization;
using System.IO;
using System.Text.Json;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Views;

namespace Drilling.UI.Menu.Menus;

/// <summary>Explicit fixture, isolated from equipment managers and active Recipe/Offset folders.</summary>
public sealed class CManualInspectionReplayBackend : IManualInspectionBackend
{
    private readonly string _scenario;
    private readonly ST_MANUAL_SESSION? _imported;
    private CManualInspectionSource? _source;
    private Dictionary<string, ST_MANUAL_OFFSET> _values = [];
    private readonly Dictionary<string, ST_MANUAL_OFFSET> _truth = [];
    private CManualInspectionUiRequest _request = new();
    private bool _stop;
    private int _attempt;
    private int _faultAt;
    private string _positionKey = "";
    public bool IsSimulation => true;
    public string StateDirectory { get; }
    public IReadOnlyDictionary<string, ST_MANUAL_OFFSET> LastSavedValues => _values;
    public CManualInspectionReplayBackend(string scenario, string directory, ST_MANUAL_SESSION? imported = null)
    {
        if (scenario is not ("normal" or "good" or "stress")) throw new InvalidOperationException("지원하지 않는 SIM 예제입니다.");
        _scenario = scenario; _imported = imported; StateDirectory = Path.Combine(directory, "Simulation");
    }
    public void Configure(CManualInspectionUiRequest request)
    {
        if (!double.IsFinite(request.Speed) || request.Speed <= 0 || request.Speed > 16
            || request.Fault is not ("NONE" or "VISION_TIMEOUT" or "PLC_STALE" or "UID_MISMATCH"))
            throw new InvalidOperationException("SIM 속도/오류 조건을 확인하세요.");
        _request = request; _attempt = 0; _faultAt = Math.Max(0, request.Keys.Count / 2); _stop = false;
    }
    public Task<CManualInspectionSource> LoadAsync(string? resultPath, CancellationToken token, IReadOnlyList<string>? displayKeys = null)
    {
        if (resultPath is not null) throw new InvalidOperationException("SIM 예제는 실제 결과 파일과 섞지 않습니다.");
        var n = _scenario == "stress" ? 25 : 8; var spacing = n == 25 ? .16 : .5;
        var context = _imported?.Context ?? new ST_MANUAL_CONTEXT("SIM_A3_REVIEW", "SIM-RUN-" + Guid.NewGuid().ToString("N"), "SIM-INITIAL", "SIM-DESIGN-UM-v1");
        var points = new List<ST_MANUAL_POINT>(16 * n * n); _truth.Clear();
        for (var cell = 1; cell <= 16; cell++) for (var row = 0; row < n; row++) for (var col = 0; col < n; col++)
        {
            token.ThrowIfCancellationRequested(); var head = (cell - 1) % 8 + 1; var shot = row * n + col + 1;
            var key = $"C{cell}/H{head}/S{shot}/B11";
            var u = (double)col / (n - 1); var v = (double)row / (n - 1);
            var truth = new ST_MANUAL_OFFSET(-(1.2 + .13 * (cell % 4) + .7 * Math.Sin(u * Math.PI) + .55 * v),
                -(-1.4 + .11 * (cell % 5) + .5 * Math.Cos(u * Math.PI) - .45 * v));
            _truth[key] = truth;
            var p = new ST_MANUAL_POINT(key, cell, head, shot, 11,
                ((cell - 1) % 4) * 40 + col * spacing, (cell - 1) / 4 * 40 + row * spacing)
            { Row = row, Column = col, Masked = row == 0 && col == 0 };
            if (!p.Masked && !(_scenario != "good" && shot % 23 == 0))
            {
                if (_scenario != "good" && shot % 37 == 0)
                    p = p with { Judge = "ERROR", Source = "MEASURED", OriginalMeasurement = new("ERROR", null, null, "", context.RecipeId, context.RunId, context.CoordinateSignature)
                    { RawData = "SIM 촬영 오류" }, MeasurementVersion = 1 };
                else
                {
                    var error = _scenario != "good" && shot % 11 == 0 ? truth with { XUm = -(11 + cell / 10.0) } : truth;
                    var m = Measure(context, p.Key, error);
                    p = p with { Judge = m.Judge, Source = "MEASURED", ErrorXUm = error.XUm, ErrorYUm = error.YUm, OriginalMeasurement = m, MeasurementVersion = 1 };
                }
            }
            points.Add(p);
        }
        _values = points.ToDictionary(p => p.Key, p => _imported?.ActiveValuesByPointKey.GetValueOrDefault(p.Key) ?? ST_MANUAL_OFFSET.Zero);
        var limits = new ST_MANUAL_LIMITS(30, 200, 5);
        ST_MANUAL_FIELD_RESULT Field(ST_MANUAL_POINT p, ST_MANUAL_OFFSET f, ST_MANUAL_LIMITS l)
        {
            var x = (p.Column - (n - 1) / 2.0) * spacing + f.XUm / 1000;
            var y = (p.Row - (n - 1) / 2.0) * spacing + f.YUm / 1000;
            return new(Math.Abs(x) <= l.FieldWidthMm / 2 && Math.Abs(y) <= l.FieldWidthMm / 2, "SIM Field 범위 확인")
            { ScannerXMm = x, ScannerYMm = y };
        }
        _source = new(context, points, new Dictionary<string, ST_MANUAL_OFFSET>(_values), limits, Field, 5, 5, "", $"SIM · {_scenario} · {points.Count:N0}점 · 설비와 활성 보정 미연결");
        return Task.FromResult(_source);
    }
    public void GuardOperation() { }
    public Task EnsureCurrentAsync(CManualInspectionSource source, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (_source?.Context.RunId != source.Context.RunId) throw new InvalidOperationException("SIM Run이 다릅니다."); return Task.CompletedTask; }
    public async Task<CManualInspectionPosition> ReadCurrentPositionAsync(CManualInspectionSource source,CancellationToken token)
    {
        await EnsureCurrentAsync(source,token);
        var point=source.Points.FirstOrDefault(point=>point.Key==_positionKey);
        return point is null?new(null,double.NaN,double.NaN):new(point.Key,point.XMm,point.YMm);
    }
    public async Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source, string key,
        EN_MANUAL_OPERATION_KIND kind, Action<string> progress, CancellationToken token)
    {
        if (_stop) throw new OperationCanceledException("SIM STOP", token);
        if (kind == EN_MANUAL_OPERATION_KIND.Measure && _positionKey != key)
            throw new InvalidOperationException("SIM 현재 위치와 선택점이 다릅니다. 먼저 해당 위치로 이동하세요.");
        await Task.Delay(Math.Max(1, (int)(40 / _request.Speed)), token);
        progress("SIM 이동/측정 · 설비 명령 없음");
        if (_request.Fault != "NONE" && _attempt++ == _faultAt)
            throw _request.Fault == "UID_MISMATCH" ? new InvalidDataException("SIM 결과 식별 정보 불일치") : new TimeoutException("SIM " + _request.Fault);
        var point = source.Points.First(p => p.Key == key);
        var truth = _truth[key];
        // Applying software offsets does not remake holes in the same glass/run. The fixture must
        // not show improved measurements until an explicitly new simulated machining Run is opened.
        var error = _request.Scenario == "same" && point.OriginalMeasurement is { ErrorXUm: double x, ErrorYUm: double y }
            ? new ST_MANUAL_OFFSET(x, y) : truth;
        if (kind != EN_MANUAL_OPERATION_KIND.Measure) _positionKey = key;
        return new(key, Measure(source.Context, key, error), point.XMm, point.YMm);
    }
    public Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress, CancellationToken token)
        => ExecuteFlyingAsync(source,keys,measured,progress,_=>{},token);
    public async Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured, Action<string> progress,
        Action<IReadOnlyList<string>> activeTargets, CancellationToken token)
        =>await ExecuteFlyingAsync(source,keys,measured,progress,activeTargets,_=>Task.CompletedTask,token);
    public async Task ExecuteFlyingAsync(CManualInspectionSource source,IReadOnlyList<string> keys,
        Action<CManualInspectionMeasurement> measured,Action<string> progress,
        Action<IReadOnlyList<string>> activeTargets,Func<CancellationToken,Task> afterLine,CancellationToken token)
    {
        try
        {
            foreach (var key in keys)
            {
                token.ThrowIfCancellationRequested();
                // Replay executes one synthetic point at a time. Do not mark later cells/rows
                // as active merely because they belong to the requested Flying batch.
                activeTargets([key]);
                measured(await ExecutePointAsync(source,key,EN_MANUAL_OPERATION_KIND.Flying,progress,token));
                await afterLine(token);
            }
        }
        finally { activeTargets([]); }
    }
    public Task<string> SaveAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
        string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, CancellationToken token,
        IReadOnlyDictionary<string, string>? consumedMeasurements = null)
    {
        token.ThrowIfCancellationRequested(); var id = "SIM-REV-" + Guid.NewGuid().ToString("N");
        CManualInspectionBackend.AtomicText(Path.Combine(StateDirectory, "Revisions", id + ".json"), JsonSerializer.Serialize(values));
        _values = new(values); _source = _source! with { Context = _source.Context with { RevisionId = id }, Values = new Dictionary<string, ST_MANUAL_OFFSET>(_values) };
        return Task.FromResult(id);
    }
    public void Stop() => _stop = true;
    private static ST_MANUAL_MEASUREMENT Measure(ST_MANUAL_CONTEXT c, string key, ST_MANUAL_OFFSET error)
    {
        var raw = FormattableString.Invariant($"SIM-DESIGN-UM:{error.XUm:R},{error.YUm:R}");
        return new(Math.Abs(error.XUm) <= 5 && Math.Abs(error.YUm) <= 5 ? "OK" : "NG", error.XUm, error.YUm,
            CManualInspectionBackend.Digest(c.RunId + key + raw), c.RecipeId, c.RunId, c.CoordinateSignature) { RawData = raw };
    }
}
