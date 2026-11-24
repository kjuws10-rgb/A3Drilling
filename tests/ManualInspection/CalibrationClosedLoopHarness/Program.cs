using System.Text.Json;
using Drilling.Common.Review.ManualInspection;

// Independent end-to-end arithmetic probe: production Core + an explicitly assumed 1D plant.
// Never opens the equipment App, Manager, a vendor API or a device script.
var checks = new List<object>();
var traces = new List<object>();
void Assert(string name, bool value)
{
    checks.Add(new { name, passed = value });
    if (!value) throw new InvalidOperationException(name);
}
const string key = "C1/H1/S1/B11";
const string offsetKey = "C1/S1";
CManualInspectionWorkspace Workspace(double current, double processed, double residual, double scale = 1,
    bool known = true, string run = "SYNTHETIC_RUN")
{
    var context = new ST_MANUAL_CONTEXT("SYNTHETIC_MODEL", run, "SYNTHETIC_CURRENT_CSV", "SYNTHETIC_STATIC_FRAME");
    var basis = new ST_MANUAL_CALIBRATION_BASIS("SYNTHETIC_BASIS_" + run, "SYNTHETIC_PROCESS_" + run,
        context.RecipeId, "SYNTHETIC_GLASS", DateTimeOffset.UtcNow.AddMinutes(-1), context.CoordinateSignature,
        new string('A', 64), new(processed, 0), scale)
        { RunId = context.RunId, CellNo = 1, ShotNo = 1, HeadNo = 1, IsSimulation = false };
    var raw = new ST_MANUAL_MEASUREMENT("OK", residual, 0, "SYNTHETIC_RAW_" + run, context.RecipeId,
        context.RunId, context.CoordinateSignature) { CalibrationBasis = known ? basis : null, IsStageSimulation = false, IsVisionSimulation = false };
    var point = new ST_MANUAL_POINT(key, 1, 1, 1, 11, 0, 0)
        { Judge = "OK", Source = "MEASURED", ErrorXUm = residual, ErrorYUm = 0, OriginalMeasurement = raw,
          HasAppliedOffsetEvidence = true };
    var engine = new CManualInspectionWorkspace();
    engine.Load(context, [point], new Dictionary<string, ST_MANUAL_OFFSET> { [key] = new(current, 0) },
        new(30, 200, 5), (_, _, _) => new(true, "Synthetic Field: arithmetic separation only"), null,
        known ? new Dictionary<string, ST_MANUAL_CALIBRATION_BASIS> { [offsetKey] = basis } : null);
    return engine;
}
double Commit(CManualInspectionWorkspace engine)
{
    engine.BuildMeasurementReplacementCandidate([key]);
    var validation = engine.ValidateCandidate(false);
    Assert("candidate validates under real Core", validation.Ok);
    engine.CommitApplied(engine.CreateApplySnapshot(false), "SYNTHETIC_NEXT_CSV", "independent assumed-plant test");
    return engine.ActiveOffsets[key].XUm;
}
foreach (var scale in new[] { .5, 1d, 2d })
{
    var current = 10d;
    var requiredContribution = 12 * scale;
    var rows = new List<object>();
    for (var cycle = 0; cycle < 4; cycle++)
    {
        var residual = requiredContribution - current * scale;
        var engine = Workspace(current, current, residual, scale, run: $"SCALE_{scale}_NEW_GLASS_{cycle}");
        var next = Commit(engine);
        var nextResidual = requiredContribution - next * scale;
        Assert($"scale {scale} cycle {cycle}: closed loop reaches zero", Math.Abs(nextResidual) < 1e-9);
        rows.Add(new { cycle, appliedOffsetUm = current, residualUm = residual, nextOffsetUm = next, nextResidualUm = nextResidual });
        current = next;
    }
    traces.Add(new { scale, rows });
}
Assert("same machined hole uses processing B10 despite current CSV20", Math.Abs(Commit(Workspace(20, 10, 2)) - 12) < 1e-9);
Assert("confirmed first process with explicit zero baseline can calibrate", Math.Abs(Commit(Workspace(0, 0, 2)) - 2) < 1e-9);
var unknown = Workspace(10, 10, 2, known: false);
Assert("legacy unknown process cannot invent a baseline", unknown.PreviewMeasurementReplacement().Keys.Count == 0);
var zeroScale = Workspace(10, 0, 2, 0);
Assert("zero scale cannot auto-correct", zeroScale.PreviewMeasurementReplacement().Keys.Count == 0);
var blankContext = new ST_MANUAL_CONTEXT("SYNTHETIC_MODEL", "ABSOLUTE_EDIT", "INITIAL", "STATIC");
var blankSource = new ST_MANUAL_POINT(key, 1, 1, 1, 11, 0, 0);
var blankTarget = new ST_MANUAL_POINT("C1/H1/S2/B11", 1, 1, 2, 11, .1, 0);
var absolute = new CManualInspectionWorkspace();
absolute.Load(blankContext, [blankSource, blankTarget],
    new Dictionary<string, ST_MANUAL_OFFSET> { [key] = new(10, 0), [blankTarget.Key] = new(20, 0) },
    new(30, 200, 5), (_, _, _) => new(true, "synthetic Field"));
absolute.RecordMeasurementOperatorResults([key], 12, 0, "explicit absolute value, not a camera measurement");
Assert("absolute entry does not fabricate a measured deviation", !absolute.GetPoint(key).HasFiniteError);
Assert("absolute value without raw is not mistaken for empty-state copy", !absolute.CanCopyMeasurementReferenceState(key));
var copy = absolute.PreviewMeasurementReferenceCopy(key, [blankTarget.Key]);
absolute.ConfirmMeasurementReferenceCopy(copy, [blankTarget.Key], "explicit final value replacement");
Assert("absolute copy preserves absent target raw", absolute.GetPoint(blankTarget.Key).OriginalMeasurement is null && !absolute.GetPoint(blankTarget.Key).HasFiniteError);
absolute.BuildMeasurementReplacementCandidate([blankTarget.Key]);
Assert("absolute copy validates without pretending to be a measured residual", absolute.ValidateCandidate(true).Ok);
absolute.CommitApplied(absolute.CreateApplySnapshot(true), "ABSOLUTE_SAVED", "synthetic no-device write");
Assert("representative F12 replaces target20 with12, not32 or2", absolute.ActiveOffsets[blankTarget.Key].XUm == 12);
var output = args.Length == 2 && args[0] == "--report" ? args[1] : Path.Combine(AppContext.BaseDirectory, "closed-loop.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new {
    passed = true, count = checks.Count, checks, traces,
    scope = "Actual pure Core with assumed linear unit-response plant; 100%=1, fresh processed glass per iteration. No real equipment or physical precision validation."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Independent closed-loop checks: {checks.Count}/{checks.Count} passed.");
