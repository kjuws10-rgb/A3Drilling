using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunMultiCellBulkChecks()
    {
        static ST_MANUAL_BULK_PREVIEW Preview(CManualInspectionWorkspace w,params int[] cells)
            =>w.PreviewMeasurementReplacement(w.Points.Where(p=>cells.Contains(p.CellNo)).Select(p=>p.Key));
        static ST_MANUAL_APPLY_SNAPSHOT Save(CManualInspectionWorkspace w,string revision,bool manual=false)
        {
            True(w.ValidateCandidate(manual).Ok);var snapshot=w.CreateApplySnapshot(manual);
            w.CommitApplied(snapshot,revision,"one synthetic writer transaction for selected Cells");return snapshot;
        }
        Check("multiple_selected_cells_commit_once_and_undo_redo_together_without_changing_other_cells",()=>
        {
            var a=Point(1,3,1,cell:1);var b=Point(1,-2,4,cell:3);var outside=Point(1,7,2,cell:2);
            var w=Workspace([a,b,outside],new(){[a.Key]=new(10,5),[b.Key]=new(20,-5),[outside.Key]=new(222,-7)});
            var outsideBefore=w.GetPoint(outside.Key);var revisions=w.Revisions.Count;var undo=w.UndoCount;
            var preview=Preview(w,1,3);True(preview.Keys.ToHashSet().SetEquals([a.Key,b.Key]));
            var candidate=w.BuildMeasurementReplacementCandidate(preview.Keys);True(candidate.Rows.Count==2&&candidate.ReplacementCellNo is null);
            var snapshot=Save(w,"MULTI-CELL");True(snapshot.ValuesByPointKey.Count==3&&snapshot.Rows.Count==2);
            Offset(w.ActiveOffsets[a.Key],13,6);Offset(w.ActiveOffsets[b.Key],18,-1);Offset(w.ActiveOffsets[outside.Key],222,-7);
            True(w.GetPoint(outside.Key)==outsideBefore&&w.Revisions.Count==revisions+1&&w.UndoCount==undo+1);
            var previous=w.CreateRestorePreviousSnapshot();w.CommitRestored(previous,"MULTI-UNDO","one undo restores both Cells");
            Offset(w.ActiveOffsets[a.Key],10,5);Offset(w.ActiveOffsets[b.Key],20,-5);Offset(w.ActiveOffsets[outside.Key],222,-7);
            var next=w.CreateRestoreNextSnapshot();w.CommitRestored(next,"MULTI-REDO","one redo restores both Cells");
            Offset(w.ActiveOffsets[a.Key],13,6);Offset(w.ActiveOffsets[b.Key],18,-1);True(Preview(w,1,3).Keys.Count==0);
        });
        Check("multi_cell_population_excludes_ng_empty_masked_and_numeric_rows_without_expanding_to_other_cells",()=>
        {
            var a=Point(1,3,1,cell:1);var b=Point(1,2,-1,cell:2);var ng=Point(2,9,9,"NG",cell:1);
            var empty=Point(2,judge:"UNMEASURED",cell:2);var numeric=Point(3,31,0,cell:2);var masked=Point(3,cell:1) with{Masked=true};var outside=Point(1,5,5,cell:3);
            var w=Workspace([a,b,ng,empty,numeric,masked,outside]);var preview=Preview(w,1,2);
            True(preview.Total==6&&preview.Keys.ToHashSet().SetEquals([a.Key,b.Key])&&preview.ExcludedNg==1&&preview.ExcludedUnmeasured==1&&preview.ExcludedNumeric==1&&preview.ExcludedMasked==1);
            w.BuildMeasurementReplacementCandidate(preview.Keys);Save(w,"MULTI-ELIGIBLE");
            foreach(var point in new[]{ng,empty,numeric,masked,outside}){Offset(w.ActiveOffsets[point.Key],0,0);True(w.GetPoint(point.Key)==point);}
            True(Preview(w,1,2).Keys.Count==0&&Preview(w,3).Keys.SequenceEqual([outside.Key]));
        });
        Check("multi_cell_commit_preserves_unselected_initialized_and_real_zero_states_raw_and_consumed_ledger",()=>
        {
            var a=Point(1,3,0,cell:1);var b=Point(1,2,0,cell:2);
            var initialized=Point(1,judge:"UNMEASURED",cell:3) with{OriginalMeasurement=null,IsMeasurementOffsetInitialized=true};
            var realZero=Point(1,0,0,cell:4);var w=Workspace([a,b,initialized,realZero]);
            w.SeedConsumedMeasurements([new(realZero.Key,"PREVIOUS-DURABLE-ZERO")]);var original=w.GetPoint(realZero.Key).OriginalMeasurement;
            w.BuildMeasurementReplacementCandidate(Preview(w,1,2).Keys);Save(w,"MULTI-PRESERVE");
            True(w.ActiveInitializedOffsetKeys.Contains(initialized.OffsetKey)&&w.GetPoint(initialized.Key)==initialized);
            True(w.GetPoint(realZero.Key)==realZero&&w.GetPoint(realZero.Key).OriginalMeasurement==original&&!w.GetPoint(realZero.Key).IsMeasurementOffsetInitialized);
            True(w.WasConsumed(realZero.Key,"PREVIOUS-DURABLE-ZERO")&&w.CaptureSession().ConsumedMeasurementHistory.Count(x=>x.Key==realZero.Key)==1);
            True(Preview(w,4).Keys.SequenceEqual([realZero.Key]));Offset(w.ActiveOffsets[realZero.Key],0,0);
        });
        Check("multi_cell_shared_shot_conflict_or_field_error_rejects_the_whole_candidate_before_write",()=>
        {
            foreach(var fieldFailure in new[]{false,true})
            {
                var a=Point(1,3,0,cell:1);var sibling=Point(1,fieldFailure?3:4,0,cell:1,beam:12);var b=Point(1,2,0,cell:2);
                ManualInspectionFieldEvaluator field=(p,_,_)=>new(!fieldFailure||p.CellNo!=2,"synthetic Cell2 field failure");
                var w=Workspace([a,sibling,b],evaluator:field);var active=w.ActiveOffsets;var revisions=w.Revisions.Count;
                w.BuildMeasurementReplacementCandidate(Preview(w,1,2).Keys);True(!w.ValidateCandidate(false).Ok);Throws(()=>w.CreateApplySnapshot(false));
                True(!w.IsBusy&&w.Revisions.Count==revisions&&w.UndoCount==0&&w.CaptureSession().ConsumedMeasurementHistory.Count==0);
                foreach(var pair in active)True(w.ActiveOffsets[pair.Key]==pair.Value);
            }
        });
        Check("multi_cell_aborted_writer_keeps_both_cells_and_consumption_unchanged_until_one_successful_retry",()=>
        {
            var a=Point(1,3,1,cell:1);var b=Point(1,2,-1,cell:2);var w=Workspace([a,b],new(){[a.Key]=new(10,0),[b.Key]=new(20,0)});
            w.BuildMeasurementReplacementCandidate(Preview(w,1,2).Keys);True(w.ValidateCandidate(false).Ok);var failed=w.CreateApplySnapshot(false);
            True(w.IsBusy&&failed.Rows.Count==2);w.AbortApply(failed);True(!w.IsBusy&&w.UndoCount==0&&w.CaptureSession().ConsumedMeasurementHistory.Count==0);
            Offset(w.ActiveOffsets[a.Key],10,0);Offset(w.ActiveOffsets[b.Key],20,0);Throws(()=>w.CommitApplied(failed,"INVALID-LATE-COMMIT","aborted token"));
            Save(w,"MULTI-RETRY");Offset(w.ActiveOffsets[a.Key],13,1);Offset(w.ActiveOffsets[b.Key],22,-1);True(w.UndoCount==1&&Preview(w,1,2).ExcludedConsumed==2);
        });
        Check("copied_residuals_across_selected_cells_are_added_once_and_share_duplicate_guards_with_sibling_beams",()=>
        {
            var source=Point(1,3,1,cell:3);var a=Point(1,9,2,"NG",cell:1);var sibling=Point(1,cell:1,beam:12);var b=Point(1,cell:2);
            var w=Workspace([source,a,sibling,b],new(){[source.Key]=new(10,0),[a.Key]=new(20,5),[sibling.Key]=new(20,5),[b.Key]=new(30,-5)});
            var raw=a.OriginalMeasurement;var copy=w.PreviewMeasurementReferenceCopy(source.Key,[a.Key,b.Key]);w.ConfirmMeasurementReferenceCopy(copy,[a.Key,b.Key],"same residual for selected Cells");
            w.BuildMeasurementReplacementCandidate([a.Key,b.Key]);Save(w,"MULTI-COPY",true);
            Offset(w.ActiveOffsets[source.Key],10,0);Offset(w.ActiveOffsets[a.Key],23,6);Offset(w.ActiveOffsets[sibling.Key],23,6);Offset(w.ActiveOffsets[b.Key],33,-4);
            True(w.GetPoint(a.Key).OriginalMeasurement==raw&&raw!.Judge=="NG"&&Preview(w,1,2).Keys.Count==0);
            Throws(()=>w.PreviewMeasurementReferenceCopy(source.Key,[a.Key,b.Key]));Throws(()=>w.PreviewMeasurementReferenceCopy(source.Key,[sibling.Key]));
        });
    }
}
