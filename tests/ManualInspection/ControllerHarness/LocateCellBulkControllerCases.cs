using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunLocateAndMultiCellChecks()
    {
        await Check("locate_reads_fresh_feedback_each_time_without_catalog_device_save_or_checkpoint",async()=>
        {
            var(f,c)=await Loaded();var loads=f.Loads;var checks=f.Ensures;var raw=c.Snapshot.Points.ToArray();
            var files=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions")).ToDictionary(p=>p,File.ReadAllBytes);
            f.Position=new("P2",.234,1.25);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
            True(c.Snapshot.CurrentPositionPointKey=="P2");Near(c.Snapshot.StageXmm,.234);Near(c.Snapshot.StageYmm,1.25);
            f.Position=new("P3",.45,3.25);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
            True(c.Snapshot.CurrentPositionPointKey=="P3"&&f.PositionReads==2&&f.Ensures==checks+2&&f.Loads==loads&&f.Executed.Count==0&&f.Saves==0);
            True(c.Snapshot.Points.SequenceEqual(raw)&&files.Count==Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions")).Length&&files.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
        });
        await Check("locate_unknown_feedback_clears_recent_point_without_fabricating_nearest_match",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);True(c.Snapshot.CurrentPositionPointKey=="P1");
            f.Position=new(null,50,60);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
            True(c.Snapshot.CurrentPositionPointKey is null&&c.Snapshot.StageXmm==50&&c.Snapshot.StageYmm==60&&f.Executed.Count==1&&f.Saves==0);
            f.Position=new(null,double.NaN,double.NaN);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
            True(c.Snapshot.CurrentPositionPointKey is null&&double.IsNaN(c.Snapshot.StageXmm)&&double.IsNaN(c.Snapshot.StageYmm)&&f.PositionReads==2);
        });
        await Check("locate_unknown_masked_and_nonfinite_point_responses_never_publish_old_highlight",async()=>
        {
            foreach(var kind in new[]{"unknown","masked","nan","infinity"})
            {
                var(f,c)=Fixture();if(kind=="masked")f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P2"?p with{Masked=true}:p).ToArray()};await c.EnsureLoaded();
                f.Position=new("P1",1,2);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
                f.Position=kind switch{"unknown"=>new("absent",1,2),"masked"=>new("P2",1,2),"nan"=>new("P1",double.NaN,2),_=>new("P1",1,double.PositiveInfinity)};
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition));
                True(c.Snapshot.CurrentPositionPointKey is null&&double.IsNaN(c.Snapshot.StageXmm)&&double.IsNaN(c.Snapshot.StageYmm)&&!c.IsBusy&&f.Saves==0&&f.Executed.Count==0);
            }
        });
        await Check("locate_stale_authority_or_equipment_interlock_rejects_before_position_reader",async()=>
        {
            foreach(var interlock in new[]{false,true})
            {
                var(f,c)=await Loaded();f.Position=new("P1",1,2);if(interlock)f.RejectGuard=true;else f.Stale=true;
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition));
                True(f.PositionReads==0&&f.Executed.Count==0&&f.Saves==0&&c.Snapshot.CurrentPositionPointKey is null&&!c.IsBusy);
            }
        });
        await Check("locate_cancellation_waits_for_fresh_reader_cleanup_and_does_not_restore_previous_point",async()=>
        {
            var(f,c)=await Loaded();f.Position=new("P1",1,2);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.PositionHook=async token=>{entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){cancelled.TrySetResult();await cleanup.Task;throw;}};
            var read=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);await entered.Task;var stop=c.StopAsync();await cancelled.Task;
            True(c.IsBusy&&!stop.IsCompleted&&c.Snapshot.CurrentPositionPointKey is null);cleanup.TrySetResult();await stop;await read;
            True(!c.IsBusy&&f.PositionReads==2&&f.StopCalls==0&&f.Executed.Count==0&&f.Saves==0&&c.Snapshot.CurrentPositionPointKey is null&&double.IsNaN(c.Snapshot.StageXmm));
        });
        await Check("locate_delayed_old_frame_feedback_is_rejected_without_recent_result_fallback",async()=>
        {
            var(f,c)=await Loaded();f.Position=new("P1",1,2);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.PositionHook=async _=>{entered.TrySetResult();await release.Task;};var read=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);await entered.Task;
            f.Source=f.Source with{Context=f.Source.Context with{RunId="NEXT-RUN"}};release.TrySetResult();await Throws(()=>read);
            True(c.Snapshot.CurrentPositionPointKey is null&&double.IsNaN(c.Snapshot.StageXmm)&&f.Executed.Count==0&&f.Saves==0&&!c.IsBusy);
        });
        await Check("overview_selected_cells_use_one_combined_csv_and_one_undo_redo_with_outside_large_raw_preserved",async()=>
        {
            var(f,c)=MultiCellControllerFixture();await c.EnsureLoaded();var outside=c.Snapshot.Points.Single(p=>p.Key=="P3");
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,["P1"],r=>r with{CurrentCellNo=null,CellNos=[2,1,2]});
            True(f.Saves==1&&c.Snapshot.UndoOffsetCount==1&&c.Snapshot.RedoOffsetCount==0&&f.Source.Values["P1"]==new ST_MANUAL_OFFSET(13,-3)&&f.Source.Values["P2"]==new ST_MANUAL_OFFSET(21,4));
            True(c.Snapshot.Points.Single(p=>p.Key=="P3")==outside&&f.Source.Values["P3"]==new ST_MANUAL_OFFSET(500,-700));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=[1,2]}));True(f.Saves==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(f.Saves==2&&f.Source.Values["P1"]==new ST_MANUAL_OFFSET(10,-2)&&f.Source.Values["P2"]==new ST_MANUAL_OFFSET(20,5)&&c.Snapshot.UndoOffsetCount==0&&c.Snapshot.RedoOffsetCount==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);True(f.Saves==3&&f.Source.Values["P1"].XUm==13&&f.Source.Values["P2"].XUm==21&&f.Source.Values["P3"].XUm==500&&f.Executed.Count==0);
        });
        await Check("overview_bulk_requires_explicit_positive_existing_cells_and_rejects_open_cell_conflict",async()=>
        {
            foreach(var cells in new int[][]{[],[0],[-1],[99],[1,99],[1,-2]})
            {
                var(f,c)=MultiCellControllerFixture();await c.EnsureLoaded();await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=cells}));
                True(f.Saves==0&&f.Source.Values["P1"].XUm==10&&f.Source.Values["P2"].XUm==20&&!c.Snapshot.IsOffsetEditPending&&!c.IsBusy);
            }
            var(g,d)=MultiCellControllerFixture();await d.EnsureLoaded();await Throws(()=>Do(d,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=1,CellNos=[1,2]}));True(g.Saves==0);
            await Throws(()=>Do(d,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=-1,CellNos=[1]}));True(g.Saves==0);
        });
        await Check("overview_single_explicit_cell_does_not_expand_to_selected_keys_or_other_cells",async()=>
        {
            var(f,c)=MultiCellControllerFixture();await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,["P1","P3"],r=>r with{CurrentCellNo=0,CellNos=[2]});
            True(f.Saves==1&&f.Source.Values["P1"].XUm==10&&f.Source.Values["P2"]==new ST_MANUAL_OFFSET(21,4)&&f.Source.Values["P3"].XUm==500&&c.Snapshot.UndoOffsetCount==1);
        });
        await Check("overview_multi_cell_excludes_ng_without_changing_raw_or_unselected_cells",async()=>
        {
            var(f,c)=MultiCellControllerFixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P2"?p with{Judge="NG",OriginalMeasurement=p.OriginalMeasurement! with{Judge="NG"}}:p).ToArray()};
            await c.EnsureLoaded();var raw=c.Snapshot.Points.Single(p=>p.Key=="P2");await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=[1,2]});
            var ng=c.Snapshot.Points.Single(p=>p.Key=="P2");True(f.Saves==1&&f.Source.Values["P1"].XUm==13&&f.Source.Values["P2"].XUm==20&&ng.OriginalJudge=="NG"&&ng.OriginalXUm==raw.OriginalXUm&&ng.WorkingJudge=="NG"&&f.Source.Values["P3"].XUm==500);
        });
        await Check("overview_multi_cell_field_or_csv_failure_is_atomic_and_can_retry_without_consuming_raw",async()=>
        {
            foreach(var invalidField in new[]{false,true})
            {
                var(f,c)=MultiCellControllerFixture();if(invalidField)f.Source=f.Source with{FieldEvaluator=(point,_,_)=>new(point.CellNo!=2,"synthetic cell2 field failure")};else f.FailSave=true;
                await c.EnsureLoaded();await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=[1,2]}));
                True(f.Saves==0&&c.Snapshot.UndoOffsetCount==0&&f.Source.Values["P1"].XUm==10&&f.Source.Values["P2"].XUm==20&&c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm==3&&!c.IsBusy);
                if(!invalidField){f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=[1,2]});True(f.Saves==1&&f.Source.Values["P1"].XUm==13&&f.Source.Values["P2"].XUm==21);}
            }
        });
        await Check("overview_pending_csv_is_exactly_its_single_cell_and_never_expands_to_multiple_cells",async()=>
        {
            var(f,c)=MultiCellControllerFixture();await c.EnsureLoaded();f.ImportedBundleValues=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(7,-1));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle,[],r=>r with{CurrentCellNo=1,ResultPath="synthetic.csv"});
            foreach(var cells in new int[][]{[1,2],[2]}){await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=cells}));True(f.Saves==0&&c.Snapshot.IsOffsetEditPending&&c.Snapshot.PendingOffsetCellNo==1);}
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null,CellNos=[1,1]});
            True(f.Saves==1&&!c.Snapshot.IsOffsetEditPending&&f.Source.Values["P1"].XUm==7&&f.Source.Values["P2"].XUm==20&&f.Source.Values["P3"].XUm==500&&c.Snapshot.UndoOffsetCount==1&&f.CumulativeInitializations.Single()==false);
        });
    }
    private static (FakeBackend,CManualInspectionController) MultiCellControllerFixture()
    {
        var(f,c)=Fixture();f.Source=f.Source with
        {
            Points=f.Source.Points.Select((p,index)=>p with{CellNo=index+1}).ToArray(),
            Values=new Dictionary<string,ST_MANUAL_OFFSET>{{"P1",new(10,-2)},{"P2",new(20,5)},{"P3",new(500,-700)}}
        };return(f,c);
    }
}
