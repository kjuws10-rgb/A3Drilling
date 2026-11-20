using System.Text.Json;
using System.IO;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.Common.Recipe;
using Drilling.Common.Review.ManualInspection;
using Drilling.Common.Station;
using Drilling.File.ReviewOffset;
using Drilling.File.ReviewResult;
using Drilling.UI.Menu.Menus;

// Exercise the actual production load adapter, managers, planner, CSV reader and writer together.
// No equipment CManager, InterfaceManager, App/Root, vendor object or motion is constructed.
internal static class Program
{
    private static readonly List<object> Checks = [];
    private static int Failures;
    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); Checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { Failures++; Checks.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Expected rejection");
    }
    private static async Task<int> Main(string[] args)
    {
        await Check("reused_host_product_ids_still_separate_new_physical_runs", () =>
        {
            var method = typeof(CManualInspectionBackend).GetMethod("CreateRunIdentity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var at = DateTimeOffset.Parse("2026-11-20T10:00:00+09:00");
            string Run(DateTimeOffset created, double shift = 0, string recipe = "QA", string product = "HOST-1")
                => (string)method.Invoke(null, ["QA", product, "SAME-GLASS", recipe, created, at.AddMinutes(-1), shift, 0d])!;
            True(Run(at) != Run(at.AddSeconds(1))); True(Run(at) != Run(at, shift: 1));
            True(Run(at) != Run(at, recipe: "OTHER")); True(Run(at) != Run(at, product: "HOST-2"));
            True(Run(at) == Run(at)); return Task.CompletedTask;
        });
        await Check("unproven_other_recipe_completion_shift_never_authorizes_movement", () =>
        {
            var method=typeof(CManualInspectionBackend).GetMethod("ResolveCompletion",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
            var at=DateTimeOffset.Parse("2026-11-20T10:00:00+09:00");var c=new ST_MANUAL_MOF_COMPLETION(at,1,2);
            (ST_MANUAL_MOF_COMPLETION? Completion,bool Ambiguous) Resolve(string? product,DateTimeOffset? created,ST_MANUAL_MOF_COMPLETION? value)
                => ((ST_MANUAL_MOF_COMPLETION?,bool))method.Invoke(null,["QA",product,created,value])!;
            var mismatch=Resolve("OTHER",at.AddMinutes(-1),c);True(mismatch.Completion is null&&mismatch.Ambiguous);
            True(Resolve("QA",at.AddMinutes(-1),c)==(c,false));True(Resolve(null,null,c)==(c,false));
            True(Resolve("QA",at.AddMinutes(1),c)==(null,false));True(Resolve("OTHER",null,c with {ShiftOffsetX=0,ShiftOffsetY=0})==(null,false));
            return Task.CompletedTask;
        });
        await Check("actual_backend_loads_real_recipe_planner_and_explicit_zero_history", async () =>
        {
            var f = new Fixture(); await f.Offsets.SaveNew("QA", [new(1, "A1", 0, 0) { IsDesignCoordinate = true }]);
            var source = await f.Backend.LoadAsync(null, default);
            True(source.Points.Count > 1 && source.Points.Any(p => !p.Masked));
            True(source.AppliedOffsetKeys.Count == 1 && source.Values[source.AppliedOffsetKeys.Single()].XUm == 0);
            True(source.Context.RevisionId.Contains("|SHA256:") && source.ImportedResults.Count == 0);
        });
        await Check("old_version_committed_run_is_readonly_and_never_relabelled", async () =>
        {
            var f = new Fixture(); var legacy = "NATIVE:" + CManualInspectionBackend.Digest("QA|||");
            var markerSource = Path.Combine(f.Root, "source.json"); await System.IO.File.WriteAllTextAsync(markerSource, "legacy source");
            var saved = await f.Offsets.SaveNewFromSource("QA", [new(1,"A1",.002,.003) { IsDesignCoordinate = true }], markerSource, false);
            var dir = Path.Combine(f.Backend.StateDirectory,"ApplySources",CManualInspectionBackend.Digest(legacy)); Directory.CreateDirectory(dir);
            await System.IO.File.WriteAllTextAsync(Path.Combine(dir,"point-old.json"),JsonSerializer.Serialize(new { SourcePath=markerSource,BaseRevision="INITIAL_ZERO",RunId=legacy,PointKey="OLD-POINT",MeasurementToken="OLD-TOKEN" }));
            var source = await f.Backend.LoadAsync(null,default);
            True(source.ReadOnly && source.Description.Contains("이전 버전") && source.Context.RunId != legacy && f.Backend.ConsumedMeasurements.Count == 0);
            var method = typeof(CManualInspectionBackend).GetMethod("LegacyStateReason",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var reason = await (Task<string>)method.Invoke(f.Backend,[legacy,saved.SavedAt.AddSeconds(1),source.Context,CancellationToken.None])!;
            True(reason.Length == 0); // A provably newer Product is a distinct physical run.
            await f.Offsets.RollbackLatest("QA",saved.FilePath);
            True((await f.Backend.LoadAsync(null,default)).ReadOnly); // Existing-menu rollback must not erase upgrade protection.
        });
        await Check("old_uncommitted_reservation_does_not_invent_consumption", async () =>
        {
            var f = new Fixture(); var legacy = "NATIVE:" + CManualInspectionBackend.Digest("QA|||");
            var dir=Path.Combine(f.Backend.StateDirectory,"ApplySources",CManualInspectionBackend.Digest(legacy));Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,"point-old.json");
            await System.IO.File.WriteAllTextAsync(path,JsonSerializer.Serialize(new { SourcePath=Path.Combine(f.Root,"never-saved.json"),BaseRevision="INITIAL_ZERO",RunId=legacy,PointKey="OLD",MeasurementToken="TOKEN" }));
            True(!(await f.Backend.LoadAsync(null,default)).ReadOnly);
            await System.IO.File.WriteAllTextAsync(path,"{ broken");
            True((await f.Backend.LoadAsync(null,default)).ReadOnly);
        });
        await Check("old_incomplete_session_is_preserved_readonly_until_new_run", async () =>
        {
            var f = new Fixture();var source=await f.Backend.LoadAsync(null,default);
            var legacy="NATIVE:"+CManualInspectionBackend.Digest("QA|||");
            var oldContext=source.Context with { RunId=legacy,RevisionId="INITIAL_ZERO" };
            var engine=new CManualInspectionWorkspace();engine.Load(oldContext,source.Points,source.Values,source.Limits,source.FieldEvaluator);
            var point=source.Points.First(p=>!p.Masked);
            engine.BeginOperation(engine.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[point.Key]));
            var path=Path.Combine(f.Backend.StateDirectory,"Sessions",CManualInspectionBackend.Digest("QA|"+legacy+"|"+source.Context.CoordinateSignature+"|INITIAL_ZERO")+".json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var oldJson=JsonSerializer.Serialize(new {Session=engine.CaptureSession()});await System.IO.File.WriteAllTextAsync(path,oldJson);
            var loaded=await f.Backend.LoadAsync(null,default);
            True(loaded.ReadOnly&&await System.IO.File.ReadAllTextAsync(path)==oldJson);
        });
        await Check("same_path_offset_replacement_rejects_stale_backend_before_action", async () =>
        {
            var f = new Fixture(); var saved = await f.Offsets.SaveNew("QA", [new(1, "A1", .002, .003) { IsDesignCoordinate = true }]);
            var source = await f.Backend.LoadAsync(null, default);
            var text = await System.IO.File.ReadAllTextAsync(saved.FilePath);
            var changed = text.Replace("0.002000", "0.004000"); True(text != changed);
            var stamp = System.IO.File.GetLastWriteTimeUtc(saved.FilePath);
            await System.IO.File.WriteAllTextAsync(saved.FilePath, changed); System.IO.File.SetLastWriteTimeUtc(saved.FilePath, stamp);
            await Reject(() => f.Backend.EnsureCurrentAsync(source, default));
            var fresh = await f.Backend.LoadAsync(null, default);
            True(fresh.Context.RevisionId != source.Context.RevisionId && fresh.Values[fresh.AppliedOffsetKeys.Single()].XUm == 4);
        });
        await Check("selection_changed_while_catalog_is_loading_is_not_published", async () =>
        {
            var f = new Fixture(); f.Results.OnList = () => { f.Selected = "OTHER"; return Task.CompletedTask; };
            await Reject(() => f.Backend.LoadAsync(null, default));
        });
        await Check("coordinate_setting_changed_while_catalog_is_loading_is_not_published", async () =>
        {
            var f = new Fixture(); f.Results.OnList = () =>
            { f.SettingRows = f.SettingRows.Select(p => p.Key == "STAGE_SCAN_DIRECTION_Y" ? p with { Value = "1" } : p).ToArray(); f.Settings.ReloadSection(EN_SETTING_TAB.Option); return Task.CompletedTask; };
            await Reject(() => f.Backend.LoadAsync(null, default));
        });
        await Check("offset_changed_while_catalog_is_loading_is_not_published", async () =>
        {
            var f = new Fixture(); f.Results.OnList = async () => { await f.Offsets.SaveNew("QA", [new(1, "A1", .002, .003) { IsDesignCoordinate = true }]); };
            await Reject(() => f.Backend.LoadAsync(null, default));
        });
        await Check("explicit_other_recipe_result_is_rejected", async () =>
        {
            var f = new Fixture(); var plan = f.Review.CreateAllPlan(f.Recipe, 11);
            var path = Path.Combine(f.Root, "wrong.csv"); f.Results.Files[path] = new(path, "wrong.csv", "OTHER", DateTimeOffset.Now, []);
            await Reject(() => f.Backend.LoadAsync(path, default));
        });
        await Check("actual_explicit_result_is_readonly_without_current_glass_proof", async () =>
        {
            var f = new Fixture(); var plan = f.Review.CreateAllPlan(f.Recipe, 11);
            var measured = plan.Points.First(p => p.Use) with { State = EN_REVIEW_POINT_STATE.Ok, Judge = "OK", ErrorX = .002, ErrorY = -.003 };
            var actualFile = new CReviewResultFile(f.Root + "/Config");
            await actualFile.Save(new(plan, [measured], DateTimeOffset.Now, "G1") { IsStageSimulation = false, IsVisionSimulation = false });
            var path = actualFile.LastSavedPath!;
            f.Results.Files[path] = await actualFile.Load(path);
            var source = await f.Backend.LoadAsync(path, default);
            True(source.ReadOnly && !source.Context.LineageValid && source.ImportedResults.Count == 1);
            var point = source.Points.Single(p => p.Source == "IMPORTED");
            True(Math.Abs(point.ErrorXUm!.Value - 2) < 1e-8 && Math.Abs(point.ErrorYUm!.Value - 3) < 1e-8);
        });
        await Check("display_beam_slots_validate_duplicate_and_out_of_range_keys", async () =>
        {
            var f = new Fixture(); var source = await f.Backend.LoadAsync(null, default); var key = source.Points.First().Key;
            await Reject(() => f.Backend.LoadAsync(null, default, [key, key]));
            await Reject(() => f.Backend.LoadAsync(null, default, [key[..(key.LastIndexOf('/') + 1)] + "B17"]));
            var subset = await f.Backend.LoadAsync(null, default, [key]); True(subset.Points.Count == 1 && subset.Points[0].Key == key);
        });
        await Check("unknown_glass_does_not_auto_merge_unrelated_result_catalog", async () =>
        {
            var f = new Fixture(); var p = f.Review.CreateAllPlan(f.Recipe, 11).Points.First(p => p.Use);
            var file = new CReviewResultFile(f.Root + "/Config");
            await file.Save(new(f.Review.CreateAllPlan(f.Recipe, 11), [p with { Judge = "OK", State = EN_REVIEW_POINT_STATE.Ok }], DateTimeOffset.Now, "G1") { IsStageSimulation = false, IsVisionSimulation = false });
            f.Results.Catalog = await file.ListCorrectionResults();
            f.Results.Files[file.LastSavedPath!] = await file.Load(file.LastSavedPath!);
            var source = await f.Backend.LoadAsync(null, default);
            True(source.RecentResults.Count == 1 && source.ImportedResults.Count == 0 && source.Points.All(p => p.Source != "IMPORTED"));
        });
        await Check("backend_guard_fails_without_device_manager", async () =>
        {
            var f = new Fixture(); await f.Backend.LoadAsync(null, default);
            await Reject(() => Task.Run(f.Backend.GuardOperation));
        });
        await Check("vendor_assemblies_never_loaded", () =>
        {
            True(!AppDomain.CurrentDomain.GetAssemblies().Any(a => new[] { "Aerotech", "Automation1", "ACS.SPii", "Newport.XPS", "CmdLib", "DeviceIOLib" }.Any(n => (a.GetName().Name ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)))); return Task.CompletedTask;
        });
        var output = args.Length == 2 && args[0] == "--report" ? args[1] : "backend-read-results.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await System.IO.File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = Failures == 0, count = Checks.Count, failed = Failures, tests = Checks,
            scope = "Actual UI production adapter + actual pure Review/Recipe/Setting managers, parser/writer; synthetic temp data; no equipment Manager/App/Root or vendor objects." }, new JsonSerializerOptions { WriteIndented = true }));
        return Failures == 0 ? 0 : 1;
    }
    private sealed class Fixture
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "A3BackendRead", Guid.NewGuid().ToString("N"));
        public string Selected = "QA";
        public ST_SYSTEM_PARAMETER[] SettingRows;
        public readonly ST_RECIPE_DEFINITION Recipe;
        public readonly CSettingManager Settings;
        public readonly CReviewManager Review;
        public readonly Results Results;
        public readonly CReviewOffsetFile Offsets;
        public readonly CManualInspectionBackend Backend;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var values = new Dictionary<string, string> { ["MAX_CELL_NUMBER"]="1", ["GLASS_SIZE_X"]="100", ["GLASS_SIZE_Y"]="200", ["PIXEL_SIZE"]="0.1", ["SPLITED_BEAM_COUNT"]="4", ["MODEL1_NUM_OF_PIXEL_X"]="8", ["MODEL1_NUM_OF_PIXEL_Y"]="8", ["MODEL1_PITCH"]="1", ["CELL1_ALIGN_TO_1ST_PIXEL_X"]="1", ["CELL1_ALIGN_TO_1ST_PIXEL_Y"]="1" };
            Recipe = new("QA", "QA", values.Select(p => new ST_RECIPE_PARAMETER(p.Key,p.Value,"","",p.Value,Key:p.Key)).ToArray(), []);
            var settings = new Dictionary<string, string> { ["AK_MARGIN_X"]="0",["AK_MARGIN_Y"]="0",["H01_AK_POSITION_X"]="0",["HeadGapX"]="110",["STAGE_SCAN_DIRECTION_Y"]="-1" };
            for (var i=1;i<=8;i++) { settings[$"H{i:00}_USE"]= i==1 ? "ON" : "OFF"; settings[$"H{i:00}_SCAN_FIELD_WIDTH_X"]="110"; }
            SettingRows = settings.Select(p => new ST_SYSTEM_PARAMETER(EN_SETTING_TAB.Option,p.Key,p.Value,"","",Key:p.Key)).ToArray();
            Settings = new(new SettingsFile(this), null!, null!);
            Results = new(Root + "/Data/Review");
            Review = new(Results, null!, Settings);
            Offsets = new(Root + "/Config");
            Backend = new(Review, new(new RecipesFile(this)), Settings, Results, Offsets, () => Selected, null, () => { });
        }
    }
    private sealed class Results(string root) : CReviewResultFileBase
    {
        public override string RootPath => root;
        public Func<Task>? OnList;
        public IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM> Catalog = [];
        public readonly Dictionary<string, ST_REVIEW_RESULT_FILE_DATA> Files = [];
        public override async Task<IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM>> ListCorrectionResults(CancellationToken cancellationToken = default) { if(OnList is not null) await OnList(); return Catalog; }
        public override Task<ST_REVIEW_RESULT_FILE_DATA> Load(string path, CancellationToken cancellationToken = default) => Task.FromResult(Files[path]);
        public override Task Save(ST_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class SettingsFile(Fixture f) : CSettingFileBase
    {
        public override IReadOnlyList<ST_SYSTEM_PARAMETER> Load(EN_SETTING_TAB section, CancellationToken cancellationToken = default) => section == EN_SETTING_TAB.Option ? f.SettingRows : [];
        public override void Save(EN_SETTING_TAB section, IReadOnlyList<ST_SYSTEM_PARAMETER> parameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override IReadOnlyList<ST_SETTING_HISTORY> LoadHistory(EN_SETTING_TAB section, CancellationToken cancellationToken = default) => [];
    }
    private sealed class RecipesFile(Fixture f) : CRecipeFileBase
    {
        public override Task<IReadOnlyList<ST_RECIPE_DEFINITION>> LoadAll(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ST_RECIPE_DEFINITION>>([f.Recipe]);
        public override Task<ST_RECIPE_DEFINITION?> Find(string recipeId, CancellationToken cancellationToken = default) => Task.FromResult<ST_RECIPE_DEFINITION?>(f.Recipe);
        public override Task Save(ST_RECIPE_DEFINITION recipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task Rename(string oldRecipeId, string newRecipeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task Delete(string recipeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<IReadOnlyList<ST_RECIPE_HISTORY>> LoadHistory(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ST_RECIPE_HISTORY>>([]);
        public override Task RecordSelection(string previousRecipeId, string recipeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
