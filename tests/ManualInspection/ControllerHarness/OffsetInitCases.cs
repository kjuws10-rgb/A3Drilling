using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunOffsetInitChecks()
    {
        static void WithBase(FakeBackend f,double x=8,double y=-5)=>f.Source=f.Source with{Values=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(x,y)),AppliedOffsetKeys=f.Source.Points.Select(p=>p.Key).ToHashSet()};
        await Check("offset_init_is_draft_without_file_save_move_or_active_baseline_change",async()=>
        {
            var(f,c)=Fixture();WithBase(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await StageLegacyInitDraft(c);
            True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.OffsetInitPending&&f.Saves==0&&f.Executed.Count==0&&f.Source.Values.Values.All(v=>v==new ST_MANUAL_OFFSET(8,-5)));
            True(c.Snapshot.CandidateRows.Count==f.Source.Points.Count&&c.Snapshot.CandidateRows.All(r=>r.FinalXUm==0&&r.FinalYUm==0));
        });
        await Check("offset_init_save_writes_whole_cumulative_replacement_and_clears_working_results",async()=>
        {
            var(f,c)=Fixture();WithBase(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await StageLegacyInitDraft(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Saves==1&&f.CumulativeInitializations.SequenceEqual([false])&&f.Source.Values.Values.All(v=>v==ST_MANUAL_OFFSET.Zero));
            True(!c.Snapshot.IsOffsetEditPending&&c.Snapshot.Points.All(p=>p.WorkingXUm is null&&!p.IsProxy));
        });
        await Check("offset_init_cancel_restores_previous_candidate_and_no_backend_save",async()=>
        {
            var(f,c)=await Loaded();await Prepared(c);var before=c.Snapshot.CandidateRows.ToArray();await StageLegacyInitDraft(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);
            True(!c.Snapshot.IsOffsetEditPending&&c.Snapshot.CandidateRows.SequenceEqual(before)&&f.Saves==0&&f.Executed.Count==0);
        });
        await Check("offset_init_failed_save_preserves_real_baseline_and_retryable_draft",async()=>
        {
            var(f,c)=Fixture();WithBase(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await StageLegacyInitDraft(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);
            f.FailSave=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(c.Snapshot.IsOffsetEditPending&&f.Saves==0&&f.Source.Values.Values.All(v=>v.XUm==8));
            f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1&&!c.Snapshot.IsOffsetEditPending);
        });
        await Check("offset_init_stale_backend_blocks_save_and_keeps_draft",async()=>
        {
            var(f,c)=await Loaded();await StageLegacyInitDraft(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);f.Stale=true;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(f.Saves==0&&c.Snapshot.IsOffsetEditPending&&f.Executed.Count==0);
        });
        await Check("offset_init_pending_blocks_movement_new_measurement_copy_calculation_and_file_switch",async()=>
        {
            var(f,c)=await Loaded();await StageLegacyInitDraft(c);
            foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.Move,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,EN_MANUAL_INSPECTION_UI_ACTION.Refresh})
                await Throws(()=>Do(c,action,["P1"],r=>r with{ResultPath="synthetic.csv",ReferenceKey="P1",Reason="blocked pending replacement"}));
            True(c.Snapshot.IsOffsetEditPending&&!c.Snapshot.IsReadOnly&&f.Executed.Count==0&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(c.Snapshot.CanApply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);True(!c.Snapshot.IsOffsetEditPending&&!c.Snapshot.IsReadOnly);
        });
        await Check("offset_init_pending_file_workspace_cannot_return_until_save_or_cancel",async()=>
        {
            var(f,c)=await Loaded();var file=Archive(f,"init-pending-return",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);await StageLegacyInitDraft(c);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace));True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.CanReturnToPreviousWorkspace&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);True(!c.Snapshot.CanReturnToPreviousWorkspace);
        });
        await Check("offset_init_explicitly_retires_incomplete_measurement_but_can_reconnect_current_source",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));await StageLegacyInitDraft(c);True(!c.Snapshot.HasIncomplete&&c.Snapshot.IsOffsetEditPending&&f.Saves==0);
            var(readonlyBackend,readonlyController)=Fixture(readOnly:true);await Do(readonlyController,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await StageLegacyInitDraft(readonlyController);True(readonlyBackend.Saves==0&&readonlyController.Snapshot.IsOffsetEditPending&&!readonlyController.Snapshot.IsReadOnly);
        });
        await Check("offset_bundle_export_of_init_is_draft_zero_and_never_saves_model",async()=>
        {
            var(f,c)=Fixture();WithBase(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await StageLegacyInitDraft(c);c.ChooseFile=(_,_)=>"synthetic-offset.a3offset.json";
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle);var e=f.ExportedBundles.Single();True(e.Draft&&!e.Initialize&&e.Values.Values.All(v=>v==ST_MANUAL_OFFSET.Zero)&&f.Saves==0&&c.Snapshot.IsOffsetEditPending);
        });
        await Check("offset_bundle_export_of_active_values_does_not_readd_measurement_delta",async()=>
        {
            var(f,c)=Fixture();WithBase(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);c.ChooseFile=(_,_)=>"synthetic-offset.a3offset.json";await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle);
            var e=f.ExportedBundles.Single();True(!e.Draft&&!e.Initialize&&e.Values.Values.All(v=>v==new ST_MANUAL_OFFSET(8,-5))&&f.Saves==0);
        });
        await Check("offset_bundle_import_stages_absolute_values_and_waits_for_explicit_save",async()=>
        {
            var(f,c)=Fixture();WithBase(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);f.ImportedBundleValues=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(12,-7));c.ChooseFile=(_,_)=>"synthetic-offset.a3offset.json";
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle);True(c.Snapshot.IsOffsetEditPending&&!c.Snapshot.OffsetInitPending&&f.Saves==0&&f.Source.Values.Values.All(v=>v.XUm==8));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1&&f.Source.Values.Values.All(v=>v==new ST_MANUAL_OFFSET(12,-7))&&!c.Snapshot.IsOffsetEditPending);
        });
        await Check("offset_bundle_cancelled_picker_never_stages_or_saves",async()=>
        {
            var(f,c)=await Loaded();c.ChooseFile=(_,_)=>null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle);
            True(!c.Snapshot.IsOffsetEditPending&&f.ExportedBundles.Count==0&&f.Saves==0&&f.Executed.Count==0);
        });
        await Check("offset_init_zero_to_zero_saved_rows_are_explicit_initialized_gray_before_and_after_refresh",async()=>
        {
            var(f,c)=await Loaded();True(c.Snapshot.Points.All(p=>!p.HasAppliedOffset));await StageLegacyInitDraft(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(c.Snapshot.Points.All(p=>p.HasAppliedOffset&&p.IsMeasurementOffsetInitialized&&!p.HasMeasurementHistory&&p.AppliedLevel is null&&p.BaseXUm==0&&p.BaseYUm==0));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Refresh);True(c.Snapshot.Points.All(p=>p.HasAppliedOffset&&p.IsMeasurementOffsetInitialized&&!p.HasMeasurementHistory&&p.AppliedLevel is null&&p.BaseXUm==0&&p.BaseYUm==0)&&f.Saves==1&&f.Executed.Count==0);
        });
        await Check("corrupt_checkpoint_is_archived_as_exact_bytes_before_reconnect_and_fresh_measurement",async()=>
        {
            var(f,_)=await Loaded();var path=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var bytes=System.Text.Encoding.UTF8.GetBytes("{ invalid checkpoint: synthetic exact bytes\r\n");await File.WriteAllBytesAsync(path,bytes);
            var fresh=new FakeBackend(f.StateDirectory,false,false);var c=new CManualInspectionController(fresh);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            var recovered=Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"checkpoint-*.json");True(recovered.Any(p=>File.ReadAllBytes(p).SequenceEqual(bytes))&&fresh.Executed.SequenceEqual(["P1"])&&!c.Snapshot.IsReadOnly);
        });
        await Check("reconnect_preserves_and_restores_target_frame_pending_candidate_checkpoint",async()=>
        {
            var(f,a)=await Loaded();f.Source=WithRun(f.Source,"RUN-TARGET");var b=new CManualInspectionController(f);await Do(b,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Prepared(b);
            var target=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single(p=>File.ReadAllText(p).Contains("RUN-TARGET"));var bytes=await File.ReadAllBytesAsync(target);var rows=b.Snapshot.CandidateRows.ToArray();
            f.Stale=true;await Throws(()=>Do(a,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]));
            True(a.Snapshot.RunId=="RUN-TARGET"&&a.Snapshot.CandidateRows.SequenceEqual(rows)&&!a.Snapshot.CanApply&&f.Executed.Count==0);
            True(Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"checkpoint-*.json").Any(p=>File.ReadAllBytes(p).SequenceEqual(bytes)));
            f.Stale=false;await Do(a,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(a.Snapshot.CanApply&&f.Saves==0);
        });
        await Check("reconnect_preserves_and_restores_target_frame_partial_measurement_without_auto_resume",async()=>
        {
            var(f,a)=await Loaded();f.Source=WithRun(f.Source,"RUN-PARTIAL-TARGET");var b=new CManualInspectionController(f);await Do(b,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);f.FailKey="P2";await Throws(()=>Do(b,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));
            var target=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single(p=>File.ReadAllText(p).Contains("RUN-PARTIAL-TARGET"));var bytes=await File.ReadAllBytesAsync(target);var executed=f.Executed.Count;
            f.Stale=true;await Throws(()=>Do(a,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]));
            True(a.Snapshot.RunId=="RUN-PARTIAL-TARGET"&&a.Snapshot.HasIncomplete&&a.Snapshot.CompletedCount==1&&a.Snapshot.RemainingCount==1&&f.Executed.Count==executed&&f.Saves==0);
            True(Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"checkpoint-*.json").Any(p=>File.ReadAllBytes(p).SequenceEqual(bytes)));
        });
        await Check("missing_selected_result_file_reconnects_current_geometry_and_accepts_fresh_measurement",async()=>
        {
            var(f,c)=await Loaded();var file=Archive(f,"removed-before-action",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);f.Stale=true;
            f.LoadHook=(loaded,_)=>{if(loaded.SourcePath==file.SourcePath){f.Stale=false;throw new FileNotFoundException("Synthetic selected result disappeared.");}return Task.CompletedTask;};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(f.Executed.SequenceEqual(["P1"])&&!c.Snapshot.IsReadOnly&&c.Snapshot.SourcePath==file.SourcePath&&c.Snapshot.Points.Single(p=>p.Key=="P1").Source=="MEASURED");
        });
        await Check("old_recipe_selected_file_falls_back_to_new_model_with_reconfirmation_before_motion",async()=>
        {
            var(f,c)=await Loaded();var file=Archive(f,"previous-model-file",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            var context=f.Source.Context with{RecipeId="QB",RunId="RUN-QB",CoordinateSignature="COORD-QB"};f.Source=f.Source with{Context=context,Points=f.Source.Points.Select(p=>p with{XMm=p.XMm+1,OriginalMeasurement=p.OriginalMeasurement! with{RecipeId="QB",RunId="RUN-QB",CoordinateSignature="COORD-QB"}}).ToArray()};f.Stale=true;
            f.LoadHook=(loaded,_)=>{if(loaded.SourcePath==file.SourcePath){f.Stale=false;throw new InvalidOperationException("Synthetic old file belongs to previous Recipe.");}return Task.CompletedTask;};
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]));True(c.Snapshot.RecipeId=="QB"&&c.Snapshot.SourcePath.Length==0&&f.Executed.Count==0&&!c.Snapshot.IsReadOnly);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(f.Executed.SequenceEqual(["P1"])&&c.Snapshot.Points.Single(p=>p.Key=="P1").Source=="MEASURED"&&f.Saves==0);
        });
        await Check("failed_file_to_model_reconnect_keeps_file_mode_and_native_checkpoint_until_recovery_succeeds",async()=>
        {
            var(f,c)=await Loaded();var file=Archive(f,"recovery-write-failure",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            var n=f.Source.Context;var native=Path.Combine(f.StateDirectory,"Sessions",CManualInspectionBackend.Digest($"{n.RecipeId}|{n.RunId}|{n.CoordinateSignature}|{n.RevisionId}")+".json");var nativeBytes=await File.ReadAllBytesAsync(native);
            var context=f.Source.Context with{RecipeId="QB",RunId="RUN-QB",CoordinateSignature="COORD-QB"};f.Source=f.Source with{Context=context,Points=f.Source.Points.Select(p=>p with{XMm=p.XMm+1,OriginalMeasurement=p.OriginalMeasurement! with{RecipeId="QB",RunId="RUN-QB",CoordinateSignature="COORD-QB"}}).ToArray()};
            var target=new CManualInspectionController(f);await Do(target,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Prepared(target);
            var recovery=Path.Combine(f.StateDirectory,"Recovery");await File.WriteAllTextAsync(recovery,"synthetic file prevents creating Recovery directory");f.Stale=true;
            f.LoadHook=(loaded,_)=>{if(loaded.SourcePath==file.SourcePath){f.Stale=false;throw new InvalidOperationException("Synthetic file belongs to former model.");}return Task.CompletedTask;};
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]));True(c.Snapshot.RecipeId=="QA"&&c.Snapshot.SourcePath==file.SourcePath&&c.Snapshot.CanReturnToPreviousWorkspace&&f.Executed.Count==0&&File.ReadAllBytes(native).SequenceEqual(nativeBytes));
            File.Delete(recovery);f.Stale=true; // The real adapter compares its prepared B plan against the still-visible A context.
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]));await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(c.Snapshot.RecipeId=="QB"&&f.Executed.SequenceEqual(["P1"])&&f.Saves==0);
        });
    }
}
