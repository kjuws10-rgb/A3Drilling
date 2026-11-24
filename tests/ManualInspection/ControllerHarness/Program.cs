using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static readonly List<object> Results=[];
    private static int Failed;
    private static readonly string Root=Path.Combine(Path.GetTempPath(),"A3ManualInspectionControllerTests",Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var result=1;
        app.Dispatcher.BeginInvoke(async()=>{try{result=await RunAsync(args);}finally{app.Shutdown(result);}});
        app.Run();return result;
    }
    private static async Task<int> RunAsync(string[] args)
    {
        await Check("constructor_has_no_implicit_backend_operation",async()=>{var(f,c)=Fixture();True(f.Loads==0&&f.Saves==0&&f.Executed.Count==0);await Task.CompletedTask;});
        await Check("load_projects_current_native_source",async()=>{var(f,c)=await Loaded();True(c.Snapshot.Points.Count==3&&!c.IsBusy&&!c.Snapshot.IsReadOnly&&f.Loads==1);});
        await Check("stale_ui_version_rejects_before_side_effect",async()=>{var(f,c)=await Loaded();await Throws(()=>c.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.Move,new(){ExpectedVersion=c.Snapshot.Version-1,Keys=["P1"]}));True(f.Executed.Count==0);});
        await Check("calculate_and_validate_do_not_save",async()=>{var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(c.Snapshot.CanApply&&f.Saves==0);Near(c.Snapshot.CandidateRows[0].FinalXUm,3);});
        await Check("apply_requires_explicit_validation",async()=>{var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(f.Saves==0&&!c.IsBusy);});
        await Check("save_success_updates_active_and_revision",async()=>{var(f,c)=await Loaded();await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1&&c.Snapshot.ActiveRevisionId=="REV-1"&&!c.IsBusy&&!c.Snapshot.CanApply);Near(c.Snapshot.Points[0].BaseXUm,3);});
        await Check("failed_save_preserves_active_and_can_revalidate",async()=>{var(f,c)=await Loaded();await Prepared(c);f.FailSave=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));Near(c.Snapshot.Points[0].BaseXUm,0);True(c.Snapshot.ActiveRevisionId=="INITIAL"&&!c.IsBusy);f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(c.Snapshot.ActiveRevisionId=="REV-1");});
        await Check("changed_backend_context_prevents_save",async()=>{var(f,c)=await Loaded();await Prepared(c);f.Stale=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(f.Saves==0);Near(c.Snapshot.Points[0].BaseXUm,0);});
        await Check("legacy_readonly_source_reconnects_for_fresh_current_measurement_without_old_raw_promotion",async()=>{var(f,c)=Fixture(readOnly:true);f.Source=f.Source with{Context=f.Source.Context with{LineageValid=false}};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(c.Snapshot.Points.Count==3&&!c.Snapshot.IsReadOnly);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]));await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Prepared(c);True(f.Executed.Count==2&&f.Saves==0&&c.Snapshot.CanApply);});
        await Check("ng_reference_copies_working_and_preserves_original",async()=>{var(f,c)=await Loaded(ngReference:true);var old=c.Snapshot.Points[1].OriginalXUm;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="NG inherited"});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate,null,r=>r with{AllowManual=true});True(c.Snapshot.Points[1].WorkingJudge=="NG"&&c.Snapshot.Points[1].OriginalXUm==old&&!c.Snapshot.CanApply&&f.Saves==0);});
        await Check("confirmed_manual_copy_validates_and_saves_without_an_extra_checkbox",async()=>{var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="Use native representative"});True(f.Saves==1&&!c.Snapshot.CanApply);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(f.Saves==1);Near(c.Snapshot.Points[1].BaseXUm,3);});
        await Check("new_reference_measurement_changes_durable_manual_source_identity",async()=>{var(f,c)=await Loaded();async Task CopyApply(){await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="Same reason"});}await CopyApply();f.NewX=1.5;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await CopyApply();True(f.SourceIds.Count==2&&f.SourceIds[0]!=f.SourceIds[1]);});
        await Check("move_only_changes_position_not_measurement",async()=>{var(f,c)=await Loaded();var old=c.Snapshot.Points[0].WorkingXUm;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);True(f.Executed.SequenceEqual(["P1"])&&c.Snapshot.Points[0].WorkingXUm==old&&f.Saves==0);});
        await Check("current_position_measure_rejects_multiple_targets",async()=>{var(f,c)=await Loaded();await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1","P2"]));True(f.Executed.Count==0);});
        await Check("removed_move_measure_correct_rejects_before_any_motion_or_save",async()=>{var(f,c)=await Loaded();await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect,["P1","P2"]));True(f.Saves==0&&f.Executed.Count==0&&!c.Snapshot.HasIncomplete);});
        await Check("fresh_ng_move_measure_never_saves",async()=>{var(f,c)=await Loaded();f.NewJudge="NG";await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(f.Saves==0&&!c.Snapshot.HasIncomplete&&!c.Snapshot.CanApply&&c.Snapshot.Points[0].WorkingJudge=="NG");});
        await Check("partial_error_preserves_remaining_and_blocks_old_candidate",async()=>{var(f,c)=await Loaded();await Prepared(c);f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));True(c.Snapshot.CompletedCount==1&&c.Snapshot.RemainingCount==1&&c.Snapshot.HasIncomplete&&!c.IsBusy&&f.Saves==0);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));});
        await Check("retry_remaining_recovers_without_implicit_auto_correction",async()=>{var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));f.FailKey=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);True(!c.Snapshot.HasIncomplete&&f.Saves==0&&f.Executed.Count(x=>x=="P1")==1&&f.Executed.Count(x=>x=="P2")==2);});
        await Check("concurrent_command_rejected_and_stop_waits_for_cleanup",async()=>{var(f,c)=await Loaded();f.Hold=true;var running=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]);await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]));c.RequestStop();await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));True(c.IsBusy&&f.StopCalls==1);f.Release.TrySetResult();await running;True(!c.IsBusy&&c.Snapshot.HasIncomplete&&f.Saves==0);});
        await Check("flying_expands_only_same_cell_head_row_and_beam",async()=>{var(f,c)=Fixture();var other=f.Source.Points[0] with{Key="OTHER_BEAM",BeamNo=1};var values=f.Source.Values.ToDictionary(p=>p.Key,p=>p.Value);values[other.Key]=ST_MANUAL_OFFSET.Zero;f.Source=f.Source with{Points=f.Source.Points.Append(other).ToArray(),Values=values};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"],r=>r with{MeasurementMode="FLYING"});True(f.FlyingKeys.ToHashSet().SetEquals(["P1","P2"])&&f.Saves==0);});
        await Check("simulation_example_is_rejected_in_real_backend",async()=>{var(f,c)=await Loaded();await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation,null,r=>r with{Scenario="good"}));True(f.Executed.Count==0&&f.Saves==0&&c.Snapshot.RecipeId=="QA");});
        await Check("level_display_change_preserves_judge_and_offsets",async()=>{var(f,c)=await Loaded();var before=c.Snapshot.Points.ToArray();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,null,r=>r with{LevelMetric="RADIAL",LevelBoundsUm=[1,2,3],LevelNames=["A","B","C","D"]});True(c.Snapshot.LevelMetric=="RADIAL"&&c.Snapshot.Points.SequenceEqual(before)&&f.Saves==0);});
        await Check("level_duplicate_names_are_rejected",async()=>{var(f,c)=await Loaded();await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,null,r=>r with{LevelMetric="MAX_AXIS",LevelBoundsUm=[1,2,3],LevelNames=["A","A","C","D"]}));});
        await Check("level_zero_and_nonfinite_boundaries_are_rejected",async()=>{var(f,c)=await Loaded();foreach(var b in new[]{new[]{0d,2,3},new[]{double.NaN,2,3}})await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,null,r=>r with{LevelMetric="MAX_AXIS",LevelBoundsUm=b,LevelNames=["A","B","C","D"]}));});
        await Check("csv_export_distinguishes_original_working_and_active",async()=>{var(f,c)=await Loaded();var path=Path.Combine(f.StateDirectory,"export.csv");c.ChooseFile=(_,_)=>path;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv);var text=await File.ReadAllTextAsync(path);True(text.Contains("WORK_DX_DESIGN_UM")&&text.Contains("ORIGINAL_DX_DESIGN_UM")&&text.Contains("BASE_X_DESIGN_UM")&&text.Contains("ORIGINAL_RAW")&&f.Saves==0);});
        await Check("rollback_creates_new_revision_and_restores_base",async()=>{var(f,c)=await Loaded();await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Rollback);True(f.Saves==2&&c.Snapshot.ActiveRevisionId=="REV-2"&&c.Snapshot.Revisions.Count==3);Near(c.Snapshot.Points[0].BaseXUm,0);});
        await Check("export_import_incomplete_session_keeps_remaining_without_motion",async()=>{var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var path=Path.Combine(f.StateDirectory,"paused.json");c.ChooseFile=(_,_)=>path;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);var(fresh,g)=Fixture();g.ChooseFile=(_,_)=>path;await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);True(g.Snapshot.HasIncomplete&&g.Snapshot.CompletedCount==1&&g.Snapshot.RemainingCount==1&&fresh.Executed.Count==0&&fresh.Saves==0);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);True(!g.Snapshot.HasIncomplete&&fresh.Executed.SequenceEqual(["P2"])&&fresh.Saves==0);});
        await Check("roundtrip_session_retains_duplicate_raw_guard",async()=>{var(f,c)=await Loaded();await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);var path=Path.Combine(f.StateDirectory,"applied.json");c.ChooseFile=(_,_)=>path;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);var(fresh,g)=Fixture();fresh.Source=f.Source;g.ChooseFile=(_,_)=>path;await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(!g.Snapshot.CanApply&&fresh.Saves==0);});
        await Check("replay_application_does_not_remake_holes_in_same_run",async()=>{var(f,c)=await Loaded();f.Simulation=true;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation,null,r=>r with{Scenario="good"});var point=c.Snapshot.Points.First(p=>!p.IsMasked);var x=point.WorkingXUm;var y=point.WorkingYUm;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,[point.Key]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[point.Key]);var after=c.Snapshot.Points.Single(p=>p.Key==point.Key);True(after.WorkingXUm==x&&after.WorkingYUm==y&&f.Executed.Count==0&&f.Saves==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,[point.Key]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(!c.Snapshot.CanApply);});
        await Check("show_point_exposes_preserved_raw_and_full_history",async()=>{var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="History evidence"});string text="";c.ShowDetails=(_,value)=>text=value;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint,["P2"]);True(text.Contains("Synthetic raw 2")&&text.Contains("REFERENCE_COPY")&&text.Contains("History evidence")&&f.Saves==1);});
        await Check("native_load_defers_incomplete_disk_session_without_implicit_motion",async()=>{var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var fresh=new FakeBackend(f.StateDirectory,false,false);var g=new CManualInspectionController(fresh);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(!g.Snapshot.HasIncomplete&&g.Snapshot.RemainingCount==0&&fresh.Executed.Count==0&&!g.Snapshot.IsReadOnly&&Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"incomplete-operation-*.json").Length==1);});
        await Check("corrupt_local_session_allows_reconnect_and_fresh_current_measurement",async()=>{var(f,c)=await Loaded();var path=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();await File.WriteAllTextAsync(path,"{ invalid JSON");var fresh=new FakeBackend(f.StateDirectory,false,false);var g=new CManualInspectionController(fresh);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(!g.Snapshot.IsReadOnly&&g.Snapshot.Points.Count==3&&fresh.Executed.Count==0);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(fresh.Executed.Count==1&&!g.Snapshot.IsReadOnly);});
        await Check("saved_candidate_session_requires_new_validation",async()=>{var(f,c)=await Loaded();await Prepared(c);True(c.Snapshot.CanApply);var fresh=new FakeBackend(f.StateDirectory,false,false);var g=new CManualInspectionController(fresh);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(!g.Snapshot.CanApply&&g.Snapshot.CandidateRows.Count==1);await Throws(()=>Do(g,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(fresh.Saves==0);});
        await Check("fresh_controller_seeds_committed_receipts_without_session_file",async()=>{var(f,c)=await Loaded();await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);var(fresh,g)=Fixture();fresh.Source=f.Source;fresh.ReceiptSeeds.AddRange(f.SavedConsumption);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(!g.Snapshot.CanApply&&fresh.Saves==0);});
        await Check("initial_session_write_failure_releases_operation_and_keeps_retry",async()=>{var(f,c)=await Loaded();var path=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();File.Delete(path);Directory.CreateDirectory(path);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));True(f.Executed.Count==0&&c.Snapshot.HasIncomplete&&!c.IsBusy);Directory.Delete(path);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);True(!c.IsBusy&&!c.Snapshot.HasIncomplete&&f.Executed.Count==2&&f.Saves==0);});
        await Check("alarm_stop_does_not_wait_for_information_dialog",async()=>{var(f,c)=await Loaded();var returned=false;c.ShowDetails=(_,_)=>{var stop=c.StopAsync();True(stop.IsCompletedSuccessfully&&c.IsBusy&&f.StopCalls==0);returned=true;};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint,["P1"]);True(returned&&!c.IsBusy);});
#if ACTUAL_ASSEMBLIES
        await Check("actual_single_line_flying_requests_pass_existing_manager_contract",async()=>{
            var builder=typeof(CManualInspectionBackend).GetMethod("CreateSingleFlyingRequest",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            var validate=typeof(Drilling.Common.Review.CReviewManager).GetMethod("ValidateFineRequest",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            foreach(var number in new[]{1,2,19}){
                var point=new Drilling.Common.Review.ST_REVIEW_PLAN_POINT(number,"SHOT-"+number,1,1,number,20,20,true,1,2,3,4,0,0,Drilling.Common.Review.EN_REVIEW_POINT_STATE.Ready,"WAIT");
                var plan=new Drilling.Common.Review.ST_REVIEW_PLAN("QA",1,.005,.005,default,DateTimeOffset.UtcNow,[point]);
                var line=new Drilling.Common.Review.ST_FINE_REVIEW_LINE("MANUAL_"+number,number,1,[point]);
                var context=new Drilling.Common.Review.ST_FINE_REVIEW_CONTEXT("QA","G","MANUAL_INSPECTION",1,new Dictionary<string,string>());
                var request=(Drilling.Common.Review.ST_FINE_REVIEW_REQUEST)builder.Invoke(null,[plan,line,context])!;
                True(request.StartLineNo==1&&request.Lines.Single().Number==1&&request.Lines.Single().Key==line.Key&&!request.IsResume);
                validate.Invoke(null,[request]);
            }
            await Task.CompletedTask;
        });
#endif
        await RunSynchronizationChecks();
        await RunFinalOffsetSyncChecks();
        await RunCoverageChecks();
        await RunHardeningChecks();
        await RunPresentationChecks();
        await RunCellOverviewChecks();
        await RunOffsetInitChecks();
        await RunMeasurementWorkflowChecks();
        await RunBulkHistoryChecks();
        await RunEmptyOffsetMeasurementChecks();
        await RunBulkReentryChecks();
        await RunAutomaticOffsetDraftRecoveryChecks();
        await RunSelectedCorrectionChecks();
        await RunRepresentativeChecks();
        await RunRetryVisibilityChecks();
        await RunFirstLoadStateChecks();
        await RunInitRecoveryChecks();
        await RunStartupFinalOffsetsCases();
        await RunRepresentativeApplyChecks();
        await Check("generic_sta_application_and_no_loaded_vendor_assemblies",async()=>{True(Application.Current.GetType()==typeof(Application));True(!VendorAssemblies().Any());await Task.CompletedTask;});
        var path=args.Length==2&&args[0]=="--report"?args[1]:Path.Combine(AppContext.BaseDirectory,"controller-results.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var assembly=typeof(CManualInspectionController).Assembly;
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{schemaVersion="a3-manual-inspection-controller-tests/1",passed=Failed==0,count=Results.Count,failed=Failed,tests=Results,presentation=PresentationObservations,controllerAssembly=assembly.GetName().Name,controllerAssemblySha256=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant(),loadedVendorAssemblies=VendorAssemblies(),scope="Actual new production Controller+Common+UI contract; synthetic async fake Backend. Generic WPF Application/STA only; Drilling.UI.App/Root/device Manager not constructed. No SDK/motion/PLC/Vision executed."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Controller tests: {Results.Count-Failed}/{Results.Count} passed. Report: {path}");return Failed==0?0:1;
    }
    private static string[] VendorAssemblies()=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetName().Name??"").Where(n=>new[]{"Aerotech","Automation1","ACS.SPii","Newport.XPS","CmdLib","DeviceIOLib","NpEthernet","NpSerial","UsbDllWrap"}.Any(s=>n.Contains(s,StringComparison.OrdinalIgnoreCase))).ToArray();
    private static async Task RunSelectedCorrectionChecks()
    {
        await Check("selected_correction_calculates_validates_and_saves_only_selection_with_one_context_check",async()=>
        {
            var(f,c)=await Loaded();var checks=f.Ensures;var guards=f.Guards;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1","P3"]);
            True(f.Saves==1&&f.Executed.Count==0&&f.Ensures==checks+1&&f.Guards==guards+1&&c.Snapshot.UndoOffsetCount==1);
            Near(c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm,3);Near(c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm,0);Near(c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm,1);
        });
        await Check("selected_correction_excludes_ng_missing_but_masked_or_empty_selection_still_rejects",async()=>
        {
            foreach(var kind in new[]{"NG","MASKED","MISSING","EMPTY"})
            {
                var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key!="P2"?p:kind switch
                    { "NG"=>p with { Judge="NG" },"MASKED"=>p with { Masked=true },"MISSING"=>p with { Judge="UNMEASURED",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null },_=>p }).ToArray() };
                await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
                if(kind is "MASKED" or "EMPTY")
                {await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,kind=="EMPTY"?[]:["P1","P2"]));True(f.Saves==0&&c.Snapshot.Points.All(p=>p.BaseXUm==0)&&c.Snapshot.UndoOffsetCount==0);}
                else {await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1","P2"]);True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==3&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==0);}
                True(f.Executed.Count==0);
            }
        });
        await Check("selected_correction_numeric_exclusion_saves_only_valid_companion_points",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key=="P2"?p with
                { ErrorXUm=999,OriginalMeasurement=p.OriginalMeasurement! with { ErrorXUm=999 } }:p).ToArray() };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1","P2"]);
            True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==3&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==0&&!c.IsBusy);
        });
        await Check("selected_real_zero_first_apply_then_repeat_is_one_save_and_undo_redo_preserve_empty_semantics",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p with { Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null,IsMeasurementOffsetInitialized=true }).ToArray(),
                StoredInitializedOffsetKeys=f.Source.Points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal) };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);f.NewX=0;f.NewY=0;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);
            True(f.Saves==1&&!c.Snapshot.Points[0].IsMeasurementOffsetInitialized&&c.Snapshot.Points[0].HasMeasurementHistory);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(c.Snapshot.Points[0].IsMeasurementOffsetInitialized&&!c.Snapshot.Points[0].HasMeasurementHistory);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);True(!c.Snapshot.Points[0].IsMeasurementOffsetInitialized&&c.Snapshot.Points[0].HasMeasurementHistory&&f.Saves==3);
        });
        await Check("selected_representative_copy_applies_captured_final_once_without_extra_manual_checkbox",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P1",Reason="Synthetic verified copy" });
            Near(c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm,3);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]));True(f.Saves==1&&f.Executed.Count==0);
        });
        await Check("selected_correction_failed_writer_can_retry_without_consumption_or_partial_save",async()=>
        {
            var(f,c)=await Loaded();f.FailSave=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));
            True(f.Saves==0&&f.SavedConsumption.Count==0&&c.Snapshot.Points.All(p=>p.BaseXUm==0)&&!c.IsBusy);
            f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==1&&c.Snapshot.UndoOffsetCount==1);
        });
        await Check("selected_correction_rapid_repeat_during_write_never_creates_second_save",async()=>
        {
            var(f,c)=await Loaded();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.SaveHook=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            var first=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try { await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==0); }
            finally { release.TrySetResult(); }
            await first;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==1&&!c.IsBusy);
        });
        await Check("pending_init_save_revalidates_after_limits_change_without_separate_validate_action",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits,null,r=>r with { DeltaLimitUm=30,TotalLimitUm=200,ScannerFieldMm=5 });
            True(c.Snapshot.IsOffsetEditPending&&!c.Snapshot.ValidationPassed);var checks=f.Ensures;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Saves==1&&f.Ensures==checks+1&&!c.Snapshot.IsOffsetEditPending&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized));
        });
        await Check("pending_import_save_runs_validation_and_rejects_unsafe_values_without_write",async()=>
        {
            var(f,c)=await Loaded();f.ImportedBundleValues=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(42,-9));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle,null,r=>r with { ResultPath="synthetic-offsets.json" });
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits,null,r=>r with { DeltaLimitUm=5,TotalLimitUm=10,ScannerFieldMm=5 });
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));
            True(f.Saves==0&&c.Snapshot.IsOffsetEditPending&&!c.Snapshot.ValidationPassed&&c.Snapshot.Points.All(p=>p.BaseXUm==42));
            True(f.Source.Values.Values.All(value=>value==ST_MANUAL_OFFSET.Zero));
        });
    }
    private static async Task RunAutomaticOffsetDraftRecoveryChecks()
    {
        await Check("first_model_load_archives_unsaved_offset_init_exact_bytes_without_relocking_measurement",async()=>
        {
            var(f,previous)=await Loaded();await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            var sessionPath=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var bytes=await File.ReadAllBytesAsync(sessionPath);
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var backup=Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"offset-draft-*.json").Single();
            True(File.ReadAllBytes(backup).SequenceEqual(bytes)&&!File.ReadAllBytes(sessionPath).SequenceEqual(bytes)
                &&!reopened.Snapshot.IsOffsetEditPending&&!reopened.IsBusy&&f.Saves==0&&f.Executed.Count==0);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            True(f.Executed.SequenceEqual(["P1"])&&f.Saves==0&&reopened.Snapshot.Points.Single(p=>p.Key=="P1").HasWorkingMeasurement);
        });
        await Check("automatic_import_draft_recovery_keeps_saved_base_raw_ledger_and_undo_redo_history",async()=>
        {
            var(f,previous)=await Loaded();
            for(var n=1;n<=2;n++){f.NewX=n;await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);}
            var before=previous.Snapshot;f.ImportedBundleValues=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(42,-9));
            await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle,null,r=>r with { ResultPath="synthetic-portable.json" });
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(!reopened.Snapshot.IsOffsetEditPending&&reopened.Snapshot.ActiveRevisionId==before.ActiveRevisionId
                &&reopened.Snapshot.Points.Select(p=>p.BaseXUm).SequenceEqual(before.Points.Select(p=>p.BaseXUm))
                &&reopened.Snapshot.Points.Select(p=>p.OriginalXUm).SequenceEqual(before.Points.Select(p=>p.OriginalXUm))
                &&reopened.Snapshot.UndoOffsetCount==2&&reopened.Snapshot.RedoOffsetCount==0&&f.Saves==2&&f.Executed.Count==2);
            await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==2);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(f.Saves==3&&reopened.Snapshot.RedoOffsetCount==1);
        });
        await Check("explicit_session_import_keeps_archived_unsaved_offset_draft_and_live_workspace_edit_stays_pending",async()=>
        {
            var(f,previous)=await Loaded();await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            var sourcePath=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var explicitPath=Path.Combine(f.StateDirectory,"explicit-draft.json");
            await File.WriteAllBytesAsync(explicitPath,await File.ReadAllBytesAsync(sourcePath));
            await Throws(()=>Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.Refresh));True(previous.Snapshot.IsOffsetEditPending&&f.Saves==0);
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            reopened.ChooseFile=(_,_)=>explicitPath;await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);
            True(reopened.Snapshot.IsOffsetEditPending&&reopened.Snapshot.OffsetInitPending&&f.Saves==0&&f.Executed.Count==0);
            await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]));
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            True(f.Executed.SequenceEqual(["P1"])&&f.Saves==0);
        });
        await Check("draft_recovery_backup_failure_never_overwrites_original_checkpoint_and_retry_is_safe",async()=>
        {
            var(f,previous)=await Loaded();await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            var sessionPath=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var bytes=await File.ReadAllBytesAsync(sessionPath);
            var recovery=Path.Combine(f.StateDirectory,"Recovery");await File.WriteAllTextAsync(recovery,"synthetic file blocks Recovery directory");
            var reopened=new CManualInspectionController(f);await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe));
            True(File.ReadAllBytes(sessionPath).SequenceEqual(bytes)&&f.Saves==0&&f.Executed.Count==0&&!reopened.IsBusy);
            await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe));True(File.ReadAllBytes(sessionPath).SequenceEqual(bytes));
            File.Delete(recovery);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(Directory.GetFiles(recovery,"offset-draft-*.json").Any(path=>File.ReadAllBytes(path).SequenceEqual(bytes))
                &&!reopened.Snapshot.IsOffsetEditPending&&f.Saves==0&&f.Executed.Count==0);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(f.Executed.SequenceEqual(["P1"]));
        });
        await Check("failed_draft_backup_only_blocks_its_checkpoint_and_other_recipe_still_saves_measurements",async()=>
        {
            var(f,previous)=await Loaded();await Do(previous,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);var sourceA=f.Source;
            var pathA=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var bytesA=await File.ReadAllBytesAsync(pathA);
            var recovery=Path.Combine(f.StateDirectory,"Recovery");await File.WriteAllTextAsync(recovery,"synthetic Recovery directory blocker");
            var reopened=new CManualInspectionController(f);await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe));
            var context=f.Source.Context with { RecipeId="QB",RunId="RUN-QB",CoordinateSignature="COORD-QB" };
            f.Source=f.Source with { Context=context,Points=f.Source.Points.Select(p=>p with
                { OriginalMeasurement=p.OriginalMeasurement! with { RecipeId=context.RecipeId,RunId=context.RunId,CoordinateSignature=context.CoordinateSignature } }).ToArray() };
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            var pathB=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single(path=>path!=pathA);
            var bytesB=await File.ReadAllBytesAsync(pathB);
            True(reopened.Snapshot.RecipeId=="QB"&&System.Text.Encoding.UTF8.GetString(bytesB).Contains("NEW-RAW-")
                &&File.ReadAllBytes(pathA).SequenceEqual(bytesA)&&f.Saves==0);
            f.Source=sourceA;await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe));
            True(File.ReadAllBytes(pathA).SequenceEqual(bytesA)&&File.ReadAllBytes(pathB).SequenceEqual(bytesB));
            File.Delete(recovery);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(!reopened.Snapshot.IsOffsetEditPending&&reopened.Snapshot.RecipeId=="QA"
                &&Directory.GetFiles(recovery,"offset-draft-*.json").Any(path=>File.ReadAllBytes(path).SequenceEqual(bytesA)));
        });
    }
    private static async Task RunBulkReentryChecks()
    {
        await Check("bulk_apply_pending_writer_rejects_rapid_repeat_and_same_raw_after_success",async()=>
        {
            var(f,c)=await Loaded();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.SaveHook=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            var first=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                True(c.IsBusy&&!c.Snapshot.CanBulkApply&&f.Saves==0);
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==0&&f.Executed.Count==0);
            }
            finally { release.TrySetResult(); }
            await first;var revision=c.Snapshot.ActiveRevisionId;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(f.Saves==1&&f.Executed.Count==0&&c.Snapshot.ActiveRevisionId==revision&&c.Snapshot.UndoOffsetCount==1&&!c.IsBusy);
        });
        await Check("bulk_apply_cancelled_writer_does_not_consume_measurement_or_leave_command_locked",async()=>
        {
            var(f,c)=await Loaded();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.SaveHook=async token=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);};
            using var cancellation=new CancellationTokenSource();
            var first=c.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,new(){ExpectedVersion=c.Snapshot.Version},cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));cancellation.Cancel();await first;
            True(f.Saves==0&&f.SavedConsumption.Count==0&&f.Executed.Count==0&&!c.IsBusy&&c.Snapshot.Points.All(p=>p.BaseXUm==0)&&c.Snapshot.UndoOffsetCount==0);
            f.SaveHook=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            True(f.Saves==1&&c.Snapshot.UndoOffsetCount==1&&!c.IsBusy);
        });
        await Check("bulk_apply_rechecks_equipment_guard_before_any_write",async()=>
        {
            var(f,c)=await Loaded();f.RejectGuard=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(f.Saves==0&&f.SavedConsumption.Count==0&&f.Executed.Count==0&&!c.IsBusy);
            f.RejectGuard=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.Executed.Count==0);
        });
    }
    private static async Task RunEmptyOffsetMeasurementChecks()
    {
        static void Empty(FakeBackend backend)=>backend.Source=backend.Source with
        {
            Points=backend.Source.Points.Select(p=>p with { Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,
                OriginalMeasurement=null,MeasurementVersion=0,IsMeasurementOffsetInitialized=true }).ToArray(),
            StoredInitializedOffsetKeys=backend.Source.Points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal)
        };
        await Check("fresh_model_without_measured_offset_accepts_move_then_measure_without_implicit_save",async()=>
        {
            var(f,c)=Fixture();Empty(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(!c.Snapshot.IsOffsetEditPending&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized&&!p.HasMeasurementHistory&&p.WorkingXUm is null));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            var measured=c.Snapshot.Points.Single(p=>p.Key=="P1");
            True(f.Executed.SequenceEqual(["P1"])&&f.Saves==0&&measured.WorkingJudge=="OK"&&!measured.IsMeasurementOffsetInitialized&&measured.HasWorkingMeasurement&&c.Snapshot.CanBulkApply);
            True(c.Snapshot.Points.Where(p=>p.Key!="P1").All(p=>p.IsMeasurementOffsetInitialized&&!p.HasMeasurementHistory));
        });
        await Check("saved_offset_init_accepts_new_real_zero_measurement_and_retains_unsaved_model_empty_state",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);var revision=c.Snapshot.ActiveRevisionId;f.NewX=0;f.NewY=0;
            True(!c.Snapshot.IsOffsetEditPending&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            var measured=c.Snapshot.Points.Single(p=>p.Key=="P1");
            True(f.Executed.SequenceEqual(["P1"])&&f.Saves==1&&c.Snapshot.ActiveRevisionId==revision
                &&measured.WorkingXUm==0&&measured.WorkingJudge=="OK"&&!measured.IsMeasurementOffsetInitialized
                &&measured.HasMeasurementHistory&&f.Source.StoredInitializedOffsetKeys!.Contains(f.Source.Points[0].OffsetKey));
        });
        await Check("saved_offset_init_keeps_masked_point_and_equipment_guard_restrictions",async()=>
        {
            var(f,c)=Fixture();Empty(f);f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key=="P2"?p with { Masked=true }:p).ToArray() };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P2"]));True(f.Executed.Count==0&&f.Saves==0);
            f.RejectGuard=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]));
            True(f.Executed.Count==0&&f.Saves==0&&!c.IsBusy&&!c.Snapshot.HasIncomplete&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized));
        });
    }
    private static Task Do(CManualInspectionController c,EN_MANUAL_INSPECTION_UI_ACTION action,IReadOnlyList<string>?keys=null,Func<CManualInspectionUiRequest,CManualInspectionUiRequest>?edit=null){var r=new CManualInspectionUiRequest{ExpectedVersion=c.Snapshot.Version,Keys=keys??[]};return c.ExecuteAsync(action,edit?.Invoke(r)??r);}
    private static async Task Prepared(CManualInspectionController c){await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);}
    private static (FakeBackend,CManualInspectionController) Fixture(bool readOnly=false,bool ngReference=false){var f=new FakeBackend(Path.Combine(Root,Guid.NewGuid().ToString("N")),readOnly,ngReference);return(f,new(f));}
    private static async Task<(FakeBackend,CManualInspectionController)> Loaded(bool ngReference=false){var(f,c)=Fixture(ngReference:ngReference);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);return(f,c);}
    private static void True(bool x){if(!x)throw new InvalidOperationException("Assertion failed");}private static void Near(double a,double b){if(Math.Abs(a-b)>1e-8)throw new InvalidOperationException($"Expected {b}, actual {a}");}
    private static async Task Throws(Func<Task>a){var thrown=false;try{await a();}catch{thrown=true;}True(thrown);}
    private static async Task Check(string name,Func<Task>a){try{await a();Results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){Failed++;Results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e.Message);}}
    private sealed class FakeBackend:IManualInspectionBackend
    {
        public bool IsSimulation=>Simulation;public string StateDirectory{get;}public CManualInspectionSource Source;public int Loads,Saves,StopCalls,Ensures,Guards;public bool FailSave,Stale,Hold,Simulation;public string? FailKey;public double NewX=.5,NewY=-.25;public string NewJudge="OK";public readonly List<string> Executed=[],SourceIds=[],FlyingKeys=[];
        public readonly List<KeyValuePair<string,string>> ReceiptSeeds=[],SavedConsumption=[];
        public Func<CManualInspectionSource,CancellationToken,Task>? LoadHook;
        public readonly Dictionary<string,CManualInspectionSource> FileSources=new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string?> LoadedPaths=[];
        public readonly List<bool> CumulativeInitializations=[];
        public readonly List<(string Path,bool Draft,bool Initialize,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> Values)> ExportedBundles=[];
        public IReadOnlyDictionary<string,ST_MANUAL_OFFSET>? ImportedBundleValues;
        public IReadOnlySet<string> ImportedInitializedOffsetKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public Func<CancellationToken,Task>? SaveHook;
        public Action? StopHook;
        public Action<string>? PointHook;
        public Action<Action<IReadOnlyList<string>>>? FlyingActiveHook;
        public bool CancellationCallbackThrows,RejectGuard;
        public IReadOnlyList<string> WatchRoots { get; set; } = [];
        public IReadOnlyList<KeyValuePair<string,string>> ConsumedMeasurements=>ReceiptSeeds.Concat(SavedConsumption).Distinct().ToArray();
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),CancelObserved=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeBackend(string path,bool readOnly,bool ng)
        {
            StateDirectory=path;var context=new ST_MANUAL_CONTEXT("QA","RUN-1","INITIAL","COORD-1");
            var points=Enumerable.Range(1,3).Select(i=>{var judge=i==1&&ng?"NG":"OK";var m=new ST_MANUAL_MEASUREMENT(judge,i==1?3:1,-1,"RAW-"+i,context.RecipeId,context.RunId,context.CoordinateSignature){RawData="Synthetic raw "+i};return new ST_MANUAL_POINT("P"+i,1,1,i,11,.1*i,.2*i){Row=i==3?1:0,Column=i-1,Judge=judge,Source="MEASURED",ErrorXUm=m.ErrorXUm,ErrorYUm=m.ErrorYUm,OriginalMeasurement=m,MeasurementVersion=1};}).ToArray();
            Source=new(context,points,points.ToDictionary(p=>p.Key,_=>ST_MANUAL_OFFSET.Zero),new(30,200,5),(_,_,_)=>new(true,"synthetic field"),5,5,"","Synthetic current source"){ReadOnly=readOnly};
        }
        public async Task<CManualInspectionSource> LoadAsync(string? p,CancellationToken t,IReadOnlyList<string>?displayKeys=null){Loads++;LoadedPaths.Add(p);var captured=p is not null&&FileSources.TryGetValue(p,out var file)?file:Source;if(LoadHook is not null)await LoadHook(captured,t);t.ThrowIfCancellationRequested();return captured with { Points=captured.Points.Select(point=>point with { HasAppliedOffsetEvidence=captured.AppliedOffsetKeys.Contains(point.Key)&&captured.StoredInitializedOffsetKeys?.Contains(point.OffsetKey)!=true&&!point.IsMeasurementOffsetInitialized }).ToArray() };}
        public void GuardOperation(){Guards++;if(RejectGuard)throw new InvalidOperationException("Synthetic equipment safety interlock");}
        public Task EnsureCurrentAsync(CManualInspectionSource source,CancellationToken t){Ensures++;t.ThrowIfCancellationRequested();if(Stale)throw new InvalidOperationException("Changed native context");return Task.CompletedTask;}
        public async Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource s,string key,EN_MANUAL_OPERATION_KIND kind,Action<string>progress,CancellationToken token)
        {
            Executed.Add(key);PointHook?.Invoke(key);Entered.TrySetResult();if(Hold){using var registration=CancellationCallbackThrows?token.Register(()=>throw new IOException("Synthetic cancellation callback failure")):default;try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){CancelObserved.TrySetResult();await Release.Task;throw;}}
            token.ThrowIfCancellationRequested();if(key==FailKey)throw new TimeoutException("Synthetic missing response");var p=s.Points.First(p=>p.Key==key);var m=new ST_MANUAL_MEASUREMENT(NewJudge,NewX,NewY,"NEW-RAW-"+NewX,s.Context.RecipeId,s.Context.RunId,s.Context.CoordinateSignature){RawData="Synthetic new raw"};return new(key,m,p.XMm,p.YMm);
        }
        public async Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string>keys,Action<CManualInspectionMeasurement>measured,Action<string>progress,CancellationToken token){FlyingKeys.AddRange(keys);foreach(var k in keys)measured(await ExecutePointAsync(s,k,EN_MANUAL_OPERATION_KIND.Flying,progress,token));}
        public async Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string>keys,Action<CManualInspectionMeasurement>measured,Action<string>progress,Action<IReadOnlyList<string>>activeTargets,CancellationToken token)
        {
            FlyingActiveHook?.Invoke(activeTargets);var points=s.Points.ToDictionary(p=>p.Key,StringComparer.Ordinal);
            foreach(var line in keys.GroupBy(key=>(points[key].CellNo,points[key].HeadNo,points[key].Row,points[key].BeamNo)))
                try { var current=line.ToArray();activeTargets(current);await ExecuteFlyingAsync(s,current,measured,progress,token); }
                finally { activeTargets([]); }
        }
        public async Task<string> SaveAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET>values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW>rows,CancellationToken token,IReadOnlyDictionary<string,string>?consumedMeasurements=null)
        {
            if(SaveHook is not null)await SaveHook(token);token.ThrowIfCancellationRequested();if(FailSave)throw new IOException("Synthetic writer error");
            SourceIds.Add(identity);if(consumedMeasurements is not null)SavedConsumption.AddRange(consumedMeasurements);Saves++;
            var previousNumber=Source.Context.RevisionId.StartsWith("REV-",StringComparison.Ordinal)&&int.TryParse(Source.Context.RevisionId.AsSpan(4),out var parsed)?parsed:0;
            var id="REV-"+Math.Max(Saves,previousNumber+1); // Real timestamped writer never reuses an imported revision name.
            Source=Source with{Context=Source.Context with{RevisionId=id},Values=new Dictionary<string,ST_MANUAL_OFFSET>(values)};
            using var json=JsonDocument.Parse(identity);
            if(json.RootElement.TryGetProperty("InitializedOffsetKeys",out var state))
            {
                var initialized=state.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
                var applied=Source.AppliedOffsetKeys.Concat(Source.Points.Where(p=>!initialized.Contains(p.OffsetKey)).Select(p=>p.Key)).ToHashSet(StringComparer.Ordinal);
                Source=Source with{StoredInitializedOffsetKeys=initialized,AppliedOffsetKeys=applied,Points=Source.Points.Select(p=>initialized.Contains(p.OffsetKey)
                    ?p with{IsMeasurementOffsetInitialized=true,HasAppliedOffsetEvidence=false,Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,ProxySource=null,AdoptedFileMeasurement=null}
                    :p with{IsMeasurementOffsetInitialized=false,HasAppliedOffsetEvidence=applied.Contains(p.Key)}).ToArray()};
            }
            return id;
        }
        public async Task<string> SaveCumulativeAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET>values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW>rows,bool initializeAllReviewRows,CancellationToken token,IReadOnlyDictionary<string,string>?consumedMeasurements=null)
        {
            CumulativeInitializations.Add(initializeAllReviewRows);var revision=await SaveAsync(s,values,identity,rows,token,consumedMeasurements);
            // Mirror the native writer's state metadata, not a physical measurement or device action.
            using var json=JsonDocument.Parse(identity);var initialized=json.RootElement.TryGetProperty("InitializedOffsetKeys",out var state)
                ?state.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal)
                :initializeAllReviewRows?s.Points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal):new HashSet<string>(StringComparer.Ordinal);
            Source=Source with{AppliedOffsetKeys=s.Points.Select(p=>p.Key).ToHashSet(),Points=Source.Points.Select(p=>initialized.Contains(p.OffsetKey)
                ?p with{IsMeasurementOffsetInitialized=true,Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,ProxySource=null,AdoptedFileMeasurement=null}
                :p with{IsMeasurementOffsetInitialized=false}).ToArray()};return revision;
        }
        public Task ExportOffsetBundleAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET>values,string path,bool isDraft,bool initializeAllReviewRows,CancellationToken token){token.ThrowIfCancellationRequested();ExportedBundles.Add((path,isDraft,initializeAllReviewRows,new Dictionary<string,ST_MANUAL_OFFSET>(values)));return Task.CompletedTask;}
        public Task<IReadOnlyDictionary<string,ST_MANUAL_OFFSET>> ImportOffsetBundleAsync(CManualInspectionSource s,string path,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(ImportedBundleValues??s.Values);}
        public void Stop(){StopCalls++;StopHook?.Invoke();}
    }
}
