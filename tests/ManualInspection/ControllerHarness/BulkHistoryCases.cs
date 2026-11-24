using System.Text.Json;
using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunBulkHistoryChecks()
    {
        static async Task SaveFresh(FakeBackend f,CManualInspectionController c,int n)
        { f.NewX=n;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply); }
        await Check("bulk_apply_uses_entire_model_not_selected_or_filtered_subset",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,["P1"]);
            True(f.Saves==1&&f.Executed.Count==0&&c.Snapshot.Points.All(p=>p.BaseXUm!=0)&&c.Snapshot.UndoOffsetCount==1);
            Near(c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm,3);Near(c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm,1);
        });
        await Check("bulk_apply_skips_ng_and_unmeasured_without_saving_them_as_zero_results",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P2"?p with{Judge="NG"}:p.Key=="P3"?p with{Judge="UNMEASURED",ErrorXUm=null,ErrorYUm=null,Source="KEPT",OriginalMeasurement=null}:p).ToArray()};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(c.Snapshot.BulkEligibleCount==1);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            Near(c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm,3);Near(c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm,0);True(f.Saves==1&&!c.Snapshot.CanBulkApply);
        });
        await Check("bulk_failed_save_keeps_original_baseline_and_history_then_allows_retry",async()=>
        {
            var(f,c)=await Loaded();f.FailSave=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(f.Saves==0&&c.Snapshot.UndoOffsetCount==0&&c.Snapshot.RedoOffsetCount==0&&c.Snapshot.Points.All(p=>p.BaseXUm==0)&&!c.IsBusy);
            f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&c.Snapshot.UndoOffsetCount==1);
        });
        await Check("bulk_already_consumed_measurements_do_not_create_another_revision",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var revision=c.Snapshot.ActiveRevisionId;
            True(!c.Snapshot.CanBulkApply&&c.Snapshot.BulkEligibleCount==0);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(f.Saves==1&&c.Snapshot.ActiveRevisionId==revision&&c.Snapshot.UndoOffsetCount==1);
        });
        await Check("bulk_numeric_exclusion_keeps_invalid_rows_and_saves_valid_points",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P2"?p with{ErrorXUm=999,OriginalMeasurement=p.OriginalMeasurement! with{ErrorXUm=999}}:p).ToArray()};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==0&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==3&&c.Snapshot.UndoOffsetCount==1);
        });
        await Check("undo_and_redo_restore_actual_written_values_without_motion",async()=>
        {
            var(f,c)=await Loaded();for(var n=1;n<=3;n++)await SaveFresh(f,c,n);var moves=f.Executed.Count;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);Near(c.Snapshot.Points[0].BaseXUm,3);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);Near(c.Snapshot.Points[0].BaseXUm,1);
            True(c.Snapshot.UndoOffsetCount==1&&c.Snapshot.RedoOffsetCount==2);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);Near(c.Snapshot.Points[0].BaseXUm,3);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);Near(c.Snapshot.Points[0].BaseXUm,6);True(f.Executed.Count==moves&&f.Saves==7&&!c.Snapshot.CanRedoOffsets);
        });
        await Check("undo_redo_are_bounded_at_ten_and_never_pass_either_end",async()=>
        {
            var(f,c)=await Loaded();for(var n=1;n<=14;n++)await SaveFresh(f,c,n);True(c.Snapshot.UndoOffsetCount==10);
            for(var n=0;n<10;n++)await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);Near(c.Snapshot.Points[0].BaseXUm,10);
            True(!c.Snapshot.CanUndoOffsets&&c.Snapshot.RedoOffsetCount==10);var saves=f.Saves;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets));True(f.Saves==saves);
            for(var n=0;n<10;n++)await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);Near(c.Snapshot.Points[0].BaseXUm,105);True(!c.Snapshot.CanRedoOffsets);
        });
        await Check("failed_undo_or_redo_save_preserves_cursor_and_active_value",async()=>
        {
            var(f,c)=await Loaded();await SaveFresh(f,c,1);await SaveFresh(f,c,2);f.FailSave=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets));
            Near(c.Snapshot.Points[0].BaseXUm,3);True(c.Snapshot.UndoOffsetCount==2&&c.Snapshot.RedoOffsetCount==0);f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);
            f.FailSave=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets));Near(c.Snapshot.Points[0].BaseXUm,1);True(c.Snapshot.UndoOffsetCount==1&&c.Snapshot.RedoOffsetCount==1&&!c.IsBusy);
        });
        await Check("new_bulk_apply_after_undo_truncates_redo_and_uses_restored_base",async()=>
        {
            var(f,c)=await Loaded();await SaveFresh(f,c,1);await SaveFresh(f,c,2);await SaveFresh(f,c,3);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);
            await SaveFresh(f,c,7);Near(c.Snapshot.Points[0].BaseXUm,8);True(c.Snapshot.UndoOffsetCount==2&&!c.Snapshot.CanRedoOffsets);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets));
        });
        await Check("undo_persists_consumption_of_unsaved_results_from_the_old_coordinate_basis",async()=>
        {
            var(f,c)=await Loaded();await SaveFresh(f,c,1);f.NewX=4;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);
            True(f.SavedConsumption.Any(x=>x.Key=="P1")&&!c.Snapshot.Points[0].HasWorkingMeasurement&&!c.Snapshot.CanBulkApply);
            var identity=JsonSerializer.Deserialize<JsonElement>(f.SourceIds.Last());True(identity.GetProperty("Kind").GetString()=="UNDO");
        });
        await Check("pending_offset_init_blocks_bulk_undo_and_redo_until_save_or_cancel",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);var saves=f.Saves;
            foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets})await Throws(()=>Do(c,action));
            True(f.Saves==saves&&c.Snapshot.IsOffsetEditPending);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);True(c.Snapshot.CanUndoOffsets);
        });
        await Check("bulk_history_roundtrip_restores_undo_cursor_and_redo_without_automatic_write",async()=>
        {
            var(f,c)=await Loaded();await SaveFresh(f,c,1);await SaveFresh(f,c,2);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);
            var path=Path.Combine(f.StateDirectory,"bulk-history.json");c.ChooseFile=(_,_)=>path;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            var(fresh,g)=Fixture();fresh.Source=f.Source;g.ChooseFile=(_,_)=>path;await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);
            True(g.Snapshot.UndoOffsetCount==1&&g.Snapshot.RedoOffsetCount==1&&fresh.Saves==0&&fresh.Executed.Count==0);await Do(g,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);Near(g.Snapshot.Points[0].BaseXUm,3);
        });
        await Check("fresh_model_first_bulk_then_undo_restores_absent_measurement_state",async()=>
        {
            var(f,c)=Fixture();var keys=f.Source.Points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
            f.Source=f.Source with{StoredInitializedOffsetKeys=keys,Points=f.Source.Points.Select(p=>p with{Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null,IsMeasurementOffsetInitialized=true}).ToArray()};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(c.Snapshot.Points.All(p=>!p.HasMeasurementHistory)&&!c.Snapshot.CanBulkApply);
            f.NewX=0;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(c.Snapshot.Points[0].HasMeasurementHistory);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(c.Snapshot.Points[0].IsMeasurementOffsetInitialized&&!c.Snapshot.Points[0].HasMeasurementHistory&&!c.Snapshot.Points[0].HasWorkingMeasurement);
        });
        await Check("return_from_file_after_first_bulk_reconnects_new_nonzero_base_instead_of_stale_init_flag",async()=>
        {
            var(f,c)=Fixture();var empty=f.Source.Points.Select(p=>p with{Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null,IsMeasurementOffsetInitialized=true}).ToArray();
            f.Source=f.Source with{Points=empty,StoredInitializedOffsetKeys=empty.Select(p=>p.OffsetKey).ToHashSet()};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var file=Archive(f,"first-bulk-return",3);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            f.NewX=2;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);True(!c.Snapshot.CanReturnToPreviousWorkspace&&!c.Snapshot.IsReadOnly);
            var point=c.Snapshot.Points.Single(p=>p.Key=="P1");Near(point.BaseXUm,2);True(!point.IsMeasurementOffsetInitialized);
        });
        await Check("file_open_bulk_and_return_keep_native_and_file_correction_history_in_one_timeline",async()=>
        {
            var(f,c)=await Loaded();await SaveFresh(f,c,1);await SaveFresh(f,c,2);var file=Archive(f,"shared-correction-history",4);f.FileSources[file.SourcePath]=file;
            await OpenFile(c,file.SourcePath);True(c.Snapshot.UndoOffsetCount==2);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);Near(c.Snapshot.Points[0].BaseXUm,7);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);True(c.Snapshot.UndoOffsetCount==3&&!c.Snapshot.CanReturnToPreviousWorkspace);
            foreach(var value in new[]{3d,1d,0d}){await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);Near(c.Snapshot.Points[0].BaseXUm,value);}
            True(c.Snapshot.UndoOffsetCount==0&&c.Snapshot.RedoOffsetCount==3);
        });
    }
}
