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
internal static partial class Program
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
        try { await action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; } catch (CReviewOffsetValidationException) { return; }
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
        await Check("old_version_committed_history_is_preserved_without_blocking_fresh_current_work", async () =>
        {
            var f = new Fixture(); var legacy = "NATIVE:" + CManualInspectionBackend.Digest("QA|||");
            var markerSource = Path.Combine(f.Root, "source.json"); await System.IO.File.WriteAllTextAsync(markerSource, "legacy source");
            var saved = await f.Offsets.SaveNewFromSource("QA", [new(1,"A1",.002,.003) { IsDesignCoordinate = true }], markerSource, false);
            var dir = Path.Combine(f.Backend.StateDirectory,"ApplySources",CManualInspectionBackend.Digest(legacy)); Directory.CreateDirectory(dir);
            await System.IO.File.WriteAllTextAsync(Path.Combine(dir,"point-old.json"),JsonSerializer.Serialize(new { SourcePath=markerSource,BaseRevision="INITIAL_ZERO",RunId=legacy,PointKey="OLD-POINT",MeasurementToken="OLD-TOKEN" }));
            var source = await f.Backend.LoadAsync(null,default);
            if(source.ReadOnly || !source.Description.Contains("이전 작업 이력") || source.Context.RunId==legacy || f.Backend.ConsumedMeasurements.Count!=0 || source.Points.Any(p=>p.IsDirectMeasurement))
                throw new InvalidOperationException($"Legacy source: readonly={source.ReadOnly}, legacy-description={source.Description.Contains("이전 작업 이력")}, changed-run={source.Context.RunId!=legacy}, receipts={f.Backend.ConsumedMeasurements.Count}, direct={source.Points.Count(p=>p.IsDirectMeasurement)}");
            var method = typeof(CManualInspectionBackend).GetMethod("LegacyStateReason",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var reason = await (Task<string>)method.Invoke(f.Backend,[legacy,saved.SavedAt.AddSeconds(1),source.Context,CancellationToken.None])!;
            True(reason.Length == 0); // A provably newer Product is a distinct physical run.
            await f.Offsets.RollbackLatest("QA",saved.FilePath);
            True(!(await f.Backend.LoadAsync(null,default)).ReadOnly); // Old receipt history remains; current Recipe may be measured again.
        });
        await Check("old_uncommitted_reservation_does_not_invent_consumption", async () =>
        {
            var f = new Fixture(); var legacy = "NATIVE:" + CManualInspectionBackend.Digest("QA|||");
            var dir=Path.Combine(f.Backend.StateDirectory,"ApplySources",CManualInspectionBackend.Digest(legacy));Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,"point-old.json");
            await System.IO.File.WriteAllTextAsync(path,JsonSerializer.Serialize(new { SourcePath=Path.Combine(f.Root,"never-saved.json"),BaseRevision="INITIAL_ZERO",RunId=legacy,PointKey="OLD",MeasurementToken="TOKEN" }));
            True(!(await f.Backend.LoadAsync(null,default)).ReadOnly);
            await System.IO.File.WriteAllTextAsync(path,"{ broken");
            True(!(await f.Backend.LoadAsync(null,default)).ReadOnly && f.Backend.ConsumedMeasurements.Count==0);
        });
        await Check("old_incomplete_session_is_preserved_without_blocking_current_recipe_measurement", async () =>
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
            True(!loaded.ReadOnly&&loaded.Points.All(p=>!p.IsDirectMeasurement)&&await System.IO.File.ReadAllTextAsync(path)==oldJson);
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
        await Check("actual_explicit_result_uses_current_recipe_geometry_without_file_readonly_gate", async () =>
        {
            var f = new Fixture(); var plan = f.Review.CreateAllPlan(f.Recipe, 11);
            var measured = plan.Points.First(p => p.Use) with { State = EN_REVIEW_POINT_STATE.Ok, Judge = "OK", ErrorX = .002, ErrorY = -.003 };
            var actualFile = new CReviewResultFile(f.Root + "/Config");
            await actualFile.Save(new(plan, [measured], DateTimeOffset.Now, "G1") { IsStageSimulation = false, IsVisionSimulation = false });
            var path = actualFile.LastSavedPath!;
            f.Results.Files[path] = await actualFile.Load(path);
            var source = await f.Backend.LoadAsync(path, default);
            True(!source.ReadOnly && source.Context.LineageValid && source.ImportedResults.Count == 1);
            var point = source.Points.Single(p => p.Source == "IMPORTED");
            True(Math.Abs(point.ErrorXUm!.Value - 2) < 1e-8 && Math.Abs(point.ErrorYUm!.Value - 3) < 1e-8);
        });
        await Check("explicit_file_still_rejects_head_or_design_coordinate_mismatch", async () =>
        {
            foreach(var change in new[]{"head","x","y"})
            {
                var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var p=plan.Points.First(p=>p.Use);
                var measured=p with{State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=.002,ErrorY=-.003};
                var actual=new CReviewResultFile(f.Root+"/Config");await actual.Save(new(plan,[measured],DateTimeOffset.Now,"PAST-GLASS"){IsStageSimulation=false,IsVisionSimulation=false});
                var file=await actual.Load(actual.LastSavedPath!);var row=file.Rows.Single();
                row=change switch{"head"=>row with{HeadNo=row.HeadNo+1},"x"=>row with{DesignX=row.DesignX+.1},_=>row with{DesignY=row.DesignY+.1}};
                f.Results.Files[file.FilePath]=file with{Rows=[row]};await Reject(()=>f.Backend.LoadAsync(file.FilePath,default));
            }
        });
        await Check("explicit_file_still_rejects_unmatched_shot_and_out_of_range_beam", async () =>
        {
            foreach(var change in new[]{"shot","beam"})
            {
                var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var p=plan.Points.First(p=>p.Use);
                var actual=new CReviewResultFile(f.Root+"/Config");await actual.Save(new(plan,[p with{State=EN_REVIEW_POINT_STATE.Ok,Judge="OK"}],DateTimeOffset.Now,"PAST-GLASS"){IsStageSimulation=false,IsVisionSimulation=false});
                var file=await actual.Load(actual.LastSavedPath!);var row=file.Rows.Single();
                row=change=="shot"?row with{ShotName="NO-SUCH-SHOT"}:row with{BeamNo=17};
                f.Results.Files[file.FilePath]=file with{Rows=[row]};await Reject(()=>f.Backend.LoadAsync(file.FilePath,default));
            }
        });
        await Check("actual_loader_adopts_edited_and_manual_file_values_without_inventing_original_raw", async () =>
        {
            foreach(var kind in new[]{"MEASURED","EDITED","MANUAL"})
            {
                var(f,path)=await ExplicitFile(kind);var source=await f.Backend.LoadAsync(path,default);var point=source.Points.Single(p=>p.Source=="IMPORTED");
                True(point.AdoptedFileMeasurement is not null && point.IsDirectMeasurement && (point.OriginalMeasurement is not null)==(kind=="MEASURED"));
                var engine=Engine(source);engine.BuildDirectCandidate([point.Key]);True(engine.ValidateCandidate(false).Ok);
                var write=engine.CreateApplySnapshot(false);True(write.Rows.Single().MeasurementToken.Length>0 && write.ConsumedMeasurements.ContainsKey(point.Key));
                engine.CommitApplied(write,"SYNTHETIC-COMMIT","Pure engine commit only; no device Backend Save called");
                True(engine.GetPoint(point.Key).OriginalMeasurement==point.OriginalMeasurement && engine.WasConsumed(point.Key,write.ConsumedMeasurements[point.Key]));
            }
        });
        await Check("fresh_native_measurement_clears_adopted_file_receipt", async () =>
        {
            var(f,path)=await ExplicitFile("EDITED");var source=await f.Backend.LoadAsync(path,default);var point=source.Points.Single(p=>p.Source=="IMPORTED");
            var engine=Engine(source);var fresh=new ST_MANUAL_MEASUREMENT("OK",1.25,-.5,"SYNTHETIC-NEW-NATIVE",source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature){RawData="Synthetic native response"};
            engine.RecordMeasurement(point.Key,fresh);var current=engine.GetPoint(point.Key);
            True(current.Source=="MEASURED"&&current.AdoptedFileMeasurement is null&&current.OriginalMeasurement==fresh);
            engine.BuildDirectCandidate([point.Key]);True(engine.ValidateCandidate(false).Ok);
        });
        await Check("mode_mismatched_file_is_navigable_but_cannot_promote_simulation_values_to_correction", async () =>
        {
            var(f,path)=await ExplicitFile("MEASURED",simulation:true);var source=await f.Backend.LoadAsync(path,default);var point=source.Points.Single(p=>p.Source=="IMPORTED");
            True(!source.ReadOnly&&source.Context.LineageValid&&point.AdoptedFileMeasurement is null&&point.OriginalMeasurement is not null&&point.ErrorXUm.HasValue);
            var engine=Engine(source);await Reject(()=>Task.Run(()=>engine.BuildDirectCandidate([point.Key])));
            var fresh=new ST_MANUAL_MEASUREMENT("OK",1,-1,"SYNTHETIC-REAL-MODE-REMEASURE",source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature){RawData="Synthetic current-mode response"};
            engine.RecordMeasurement(point.Key,fresh);engine.BuildDirectCandidate([point.Key]);True(engine.ValidateCandidate(false).Ok);
        });
        await Check("automatic_import_without_adoption_receipt_is_not_promoted_to_correction_anchor", async () =>
        {
            var(f,path)=await ExplicitFile("MEASURED");var source=await f.Backend.LoadAsync(path,default);var point=source.Points.Single(p=>p.Source=="IMPORTED");
            var automatic=source with{Points=source.Points.Select(p=>p with{AdoptedFileMeasurement=null}).ToArray()};
            var engine=Engine(automatic);True(!engine.GetPoint(point.Key).IsDirectMeasurement);
            await Reject(()=>Task.Run(()=>engine.BuildDirectCandidate([point.Key])));
        });
        await Check("actual_committed_receipt_blocks_same_explicit_file_after_offset_revision_change", async () =>
        {
            var(f,path)=await ExplicitFile("MEASURED");var source=await f.Backend.LoadAsync(path,default);var point=source.Points.Single(p=>p.Source=="IMPORTED");
            var engine=Engine(source);engine.BuildDirectCandidate([point.Key]);True(engine.ValidateCandidate(false).Ok);var write=engine.CreateApplySnapshot(false);
            var token=write.ConsumedMeasurements[point.Key];var dir=Path.Combine(f.Backend.StateDirectory,"ApplySources",CManualInspectionBackend.Digest(source.Context.RunId));Directory.CreateDirectory(dir);
            var sourcePath=Path.Combine(dir,"synthetic-file-application.json");await System.IO.File.WriteAllTextAsync(sourcePath,"{\"synthetic\":true}");
            var target=f.Review.CreateAllPlan(f.Recipe,11).Points.Single(p=>p.CellNo==point.CellNo&&p.ShotNo==point.ShotNo);
            var row=CManualInspectionCoordinateBridge.ToFile(point,target.PixelCountX,write.ValuesByPointKey[point.Key]);
            await f.Offsets.SaveNewFromSource("QA",[row],sourcePath,false);
            await System.IO.File.WriteAllTextAsync(Path.Combine(dir,"point-qa-explicit.json"),JsonSerializer.Serialize(new{SourcePath=sourcePath,BaseRevision=source.Context.RevisionId,RunId=source.Context.RunId,PointKey=point.Key,MeasurementToken=token}));
            var reopened=await f.Backend.LoadAsync(path,default);True(reopened.Context.RevisionId!=source.Context.RevisionId&&f.Backend.ConsumedMeasurements.Any(p=>p.Key==point.Key&&p.Value==token));
            var next=Engine(reopened);next.SeedConsumedMeasurements(f.Backend.ConsumedMeasurements);next.BuildDirectCandidate([point.Key]);True(!next.ValidateCandidate(false).Ok);
            True(next.Candidate!.Rows.Single().MeasurementToken==token);
        });
        await Check("actual_loader_publishes_recipe_cell_boundary_and_glass_dimensions", async () =>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);
            True(source.CellLayouts.Count==1&&source.CellLayouts[0].CellNo==1&&source.CellLayouts[0].BoundaryMm.Count==4);
            True(source.CellLayouts.SelectMany(c=>c.BoundaryMm).All(v=>double.IsFinite(v.Xmm)&&double.IsFinite(v.Ymm))&&source.GlassWidthMm==100&&source.GlassHeightMm==200);
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
        await RunPortableOffsetChecks();
        await RunMeasurementStateChecks();
        await RunBulkHistoryChecks();
        await Check("vendor_assemblies_never_loaded", () =>
        {
            True(!AppDomain.CurrentDomain.GetAssemblies().Any(a => new[] { "Aerotech", "Automation1", "ACS.SPii", "Newport.XPS", "CmdLib", "DeviceIOLib" }.Any(n => (a.GetName().Name ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)))); return Task.CompletedTask;
        });
        var output = args.Length == 2 && args[0] == "--report" ? args[1] : "backend-read-results.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var productionAssemblies=new[]{typeof(CManualInspectionBackend).Assembly,typeof(CManualInspectionWorkspace).Assembly}.Distinct()
            .ToDictionary(a=>a.GetName().Name!,a=>new{path=a.Location,sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(a.Location))).ToLowerInvariant()});
        await System.IO.File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = Failures == 0, count = Checks.Count, failed = Failures, tests = Checks,
            productionAssemblies,
            scope = "Actual UI production adapter + actual pure Review/Recipe/Setting managers, parser/writer; synthetic temp data; no equipment Manager/App/Root or vendor objects." }, new JsonSerializerOptions { WriteIndented = true }));
        return Failures == 0 ? 0 : 1;
    }
    private static CManualInspectionWorkspace Engine(CManualInspectionSource source)
    {var engine=new CManualInspectionWorkspace();engine.Load(source.Context,source.Points,source.Values,source.Limits,source.FieldEvaluator,source.StoredInitializedOffsetKeys);return engine;}
    private static async Task<(Fixture Fixture,string Path)> ExplicitFile(string kind,bool simulation=false)
    {
        var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var point=plan.Points.First(p=>p.Use);
        var file=new CReviewResultFile(f.Root+"/Config");await file.Save(new(plan,[point with{State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=.002,ErrorY=-.003}],DateTimeOffset.Now,"PAST-GLASS"){IsStageSimulation=simulation,IsVisionSimulation=simulation});
        var path=file.LastSavedPath!;var loaded=await file.Load(path);
        if(kind is "EDITED" or "MANUAL")
        {
            var row=loaded.Rows.Single();path=await file.SaveEditedRevision(loaded,[new(row.CellNo,row.ShotKey,row.ShotName,row.BeamNo,.002,-.003,"OK")],DateTimeOffset.Now.AddSeconds(1));
            if(kind=="MANUAL")
            {var text=await System.IO.File.ReadAllTextAsync(path);True(text.Contains("EDITED",StringComparison.Ordinal));await System.IO.File.WriteAllTextAsync(path,text.Replace("EDITED","MANUAL",StringComparison.Ordinal));}
            loaded=await file.Load(path);True(loaded.Rows.Single().ResultSource==kind);
        }
        f.Results.Files[path]=loaded;return(f,path);
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
        public Fixture(Dictionary<string,string>? recipeOverrides=null,Dictionary<string,string>? settingOverrides=null)
        {
            Directory.CreateDirectory(Root);
            var values = new Dictionary<string, string> { ["MAX_CELL_NUMBER"]="1", ["GLASS_SIZE_X"]="100", ["GLASS_SIZE_Y"]="200", ["PIXEL_SIZE"]="0.1", ["SPLITED_BEAM_COUNT"]="4", ["MODEL1_NUM_OF_PIXEL_X"]="8", ["MODEL1_NUM_OF_PIXEL_Y"]="8", ["MODEL1_PITCH"]="1", ["CELL1_ALIGN_TO_1ST_PIXEL_X"]="1", ["CELL1_ALIGN_TO_1ST_PIXEL_Y"]="1" };
            if(recipeOverrides is not null)foreach(var p in recipeOverrides)values[p.Key]=p.Value;
            Recipe = new("QA", "QA", values.Select(p => new ST_RECIPE_PARAMETER(p.Key,p.Value,"","",p.Value,Key:p.Key)).ToArray(), []);
            var settings = new Dictionary<string, string> { ["AK_MARGIN_X"]="0",["AK_MARGIN_Y"]="0",["H01_AK_POSITION_X"]="0",["HeadGapX"]="110",["STAGE_SCAN_DIRECTION_Y"]="-1" };
            for (var i=1;i<=8;i++) { settings[$"H{i:00}_USE"]= i==1 ? "ON" : "OFF"; settings[$"H{i:00}_SCAN_FIELD_WIDTH_X"]="110"; }
            if(settingOverrides is not null)foreach(var p in settingOverrides)settings[p.Key]=p.Value;
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
