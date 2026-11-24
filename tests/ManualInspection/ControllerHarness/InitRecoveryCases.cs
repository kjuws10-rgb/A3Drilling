using System.IO;
using System.Text.Json;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunInitRecoveryChecks()
    {
        static void Large(FakeBackend f,bool rejectOutside=false)
        {
            f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P3"?p with{CellNo=2,HeadNo=2}:p).ToArray(),
                Values=f.Source.Points.ToDictionary(p=>p.Key,p=>p.Key=="P3"?new ST_MANUAL_OFFSET(250,-900):new ST_MANUAL_OFFSET(400,-700)),
                AppliedOffsetKeys=f.Source.Points.Select(p=>p.Key).ToHashSet(StringComparer.Ordinal),
                FieldEvaluator=(p,value,_)=>new(!rejectOutside || p.CellNo==1 || value==ST_MANUAL_OFFSET.Zero,"synthetic unchanged outside field")};
        }
        await Check("init_recovery_controller_cell_and_head_init_can_save_with_unchanged_legacy_over_limit",async()=>
        {
            foreach(var scope in new[]{"CELL","HEAD"})
            {
                var(f,c)=Fixture();Large(f,true);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
                await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope=scope,OffsetInitCellNo=1,OffsetInitHeadNo=1});
                True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.CanApply&&c.Snapshot.TotalLimitUm==200&&f.Saves==0);
                await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);var outside=c.Snapshot.Points.Single(p=>p.Key=="P3");
                True(!c.Snapshot.IsOffsetEditPending&&f.Saves==1&&outside.BaseXUm==250&&outside.BaseYUm==-900&&!outside.IsMeasurementOffsetInitialized
                    &&c.Snapshot.Points.Where(p=>p.Key!="P3").All(p=>p.IsMeasurementOffsetInitialized&&p.MeasurementXUm is null));
                await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);True(f.Executed.SequenceEqual(["P3"])&&f.Saves==1);
            }
        });
        await Check("init_recovery_controller_all_init_saves_large_negative_base_without_raising_limits",async()=>
        {
            var(f,c)=Fixture();Large(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(c.Snapshot.Points.All(p=>p.BaseXUm==0&&p.BaseYUm==0&&p.IsMeasurementOffsetInitialized)&&c.Snapshot.TotalLimitUm==200&&f.Saves==1);
            f.NewX=f.NewY=0;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(c.Snapshot.Points.Single(p=>p.Key=="P1").HasWorkingMeasurement&&f.Saves==1);
        });
        await Check("init_recovery_controller_failed_field_preflight_preserves_previous_candidate_and_measurement_access",async()=>
        {
            var(f,c)=Fixture();var fieldOk=true;f.Source=f.Source with{FieldEvaluator=(_,_,_)=>new(fieldOk,"protected target field")};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Prepared(c);
            var old=c.Snapshot.CandidateRows.ToArray();var raw=c.Snapshot.Points.Select(p=>p.OriginalXUm).ToArray();fieldOk=false;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets));True(!c.Snapshot.IsOffsetEditPending&&c.Snapshot.CandidateRows.SequenceEqual(old)&&raw.SequenceEqual(c.Snapshot.Points.Select(p=>p.OriginalXUm))&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P2"]);True(f.Executed.SequenceEqual(["P2"]));
        });
        await Check("init_recovery_controller_invalid_scope_and_field_failure_do_not_retire_remaining_queue",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{FieldEvaluator=(_,_,_)=>new(false,"protected target field")};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var before=c.Snapshot.Points.Select(p=>p.OriginalXUm).ToArray();
            foreach(var edit in new Func<CManualInspectionUiRequest,CManualInspectionUiRequest>[] {r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=99},r=>r})
                {await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,edit));True(c.Snapshot.HasIncomplete&&c.Snapshot.CompletedCount==1&&c.Snapshot.RemainingCount==1&&!c.Snapshot.IsOffsetEditPending);}
            True(before.SequenceEqual(c.Snapshot.Points.Select(p=>p.OriginalXUm))&&f.Saves==0&&f.Executed.Count==2);
            f.FailKey=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);True(!c.Snapshot.HasIncomplete&&f.Executed.Last()=="P3");
        });
        await Check("init_recovery_controller_failed_save_keeps_retry_cancel_and_original_source_bytes",async()=>
        {
            var(f,c)=Fixture();Large(f,true);Directory.CreateDirectory(f.StateDirectory);var path=Path.Combine(f.StateDirectory,"synthetic-active-offset.csv");await File.WriteAllTextAsync(path,"synthetic source bytes");var bytes=await File.ReadAllBytesAsync(path);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});f.FailSave=true;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm==250&&f.Saves==0&&File.ReadAllBytes(path).SequenceEqual(bytes));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);True(!c.Snapshot.IsOffsetEditPending&&f.Executed.Last()=="P3");
            f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1&&!c.Snapshot.IsOffsetEditPending);
        });
        await Check("init_recovery_controller_limit_edits_do_not_reject_unchanged_keep_but_new_correction_is_checked",async()=>
        {
            var(f,c)=Fixture();Large(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits,null,r=>r with{DeltaLimitUm=1,TotalLimitUm=50,ScannerFieldMm=5});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P3"]));True(f.Saves==1&&!c.Snapshot.IsOffsetEditPending);
        });
        await Check("init_recovery_controller_known_large_history_undo_redo_is_exact_not_a_new_measurement",async()=>
        {
            var(f,c)=Fixture();Large(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(f.Saves==2&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==400&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseYUm==-700);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);True(f.Saves==3&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets));True(f.Saves==3&&f.Executed.Count==0);
        });
        await Check("init_recovery_controller_same_frame_ensureloaded_preserves_verified_large_undo_redo",async()=>
        {
            var(f,c)=Fixture();Large(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            // Different observation fingerprint forces the real Load -> RestoreSession refresh,
            // rather than the identical-observation fast return. No JSON carries trusted proof.
            f.Source=f.Source with{ObservationFingerprint="fresh-native-observation-after-init"};
            await c.EnsureLoaded();True(f.Loads>=2&&c.Snapshot.UndoOffsetCount==1&&!c.Snapshot.IsOffsetEditPending);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(f.Saves==2&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==400);
            f.Source=f.Source with{ObservationFingerprint="fresh-native-observation-after-undo"};
            await c.SynchronizeExternalAsync();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);
            True(f.Saves==3&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized)&&f.Executed.Count==0);
        });
        await Check("init_recovery_controller_old_scoped_draft_startup_archives_but_explicit_import_can_save_after_revalidation",async()=>
        {
            var(f,c)=Fixture();Large(f,true);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});
            var checkpoint=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var bytes=await File.ReadAllBytesAsync(checkpoint);
            var imported=Path.Combine(f.StateDirectory,"explicit-old-scoped-draft.json");await File.WriteAllBytesAsync(imported,bytes);
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(!reopened.Snapshot.IsOffsetEditPending&&Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"offset-draft-*.json").Any(p=>File.ReadAllBytes(p).SequenceEqual(bytes)));
            reopened.ChooseFile=(_,_)=>imported;await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);True(reopened.Snapshot.IsOffsetEditPending&&!reopened.Snapshot.ValidationPassed);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1&&reopened.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm==250);
        });
        await Check("init_recovery_controller_explicit_old_field_invalid_draft_requires_cancel_and_never_writes",async()=>
        {
            var(f,c)=Fixture();Large(f);var fieldOk=true;f.Source=f.Source with{FieldEvaluator=(_,_,_)=>new(fieldOk,"target field")};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);var checkpoint=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var bytes=await File.ReadAllBytesAsync(checkpoint);
            var imported=Path.Combine(f.StateDirectory,"explicit-old-field-draft.json");await File.WriteAllBytesAsync(imported,bytes);fieldOk=false;
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(!reopened.Snapshot.IsOffsetEditPending);
            reopened.ChooseFile=(_,_)=>imported;await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.Apply));
            True(reopened.Snapshot.IsOffsetEditPending&&f.Saves==0);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(!reopened.Snapshot.IsOffsetEditPending&&f.Executed.SequenceEqual(["P1"])&&f.Saves==0&&File.ReadAllBytes(imported).SequenceEqual(bytes));
        });
    }
}
