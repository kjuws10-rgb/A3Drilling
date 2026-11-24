using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunCellCsvWorkflowChecks()
    {
        Check("scoped_bulk_builds_all_eligible_cell_points_without_other_cell_raw_or_base_changes",()=>
        {
            var a=Point(1);var b=Point(2,head:2);var other=Point(1,cell:2);var w=Workspace([a,b,other]);
            var c=w.BuildBulkCandidate(w.Points.Where(p=>p.CellNo==1).Select(p=>p.Key));
            True(c.Rows.Select(r=>r.Key).ToHashSet().SetEquals([a.Key,b.Key])&&w.ValidateCandidate(false).Ok);
            Apply(w,"CELL1-SAVED");Offset(w.ActiveOffsets[other.Key],0,0);
            True(w.PreviewBulkCorrection([other.Key]).Keys.SequenceEqual([other.Key])&&w.GetPoint(other.Key).OriginalMeasurement==other.OriginalMeasurement);
        });
        Check("scoped_bulk_empty_or_unknown_scope_cannot_expand_to_model",()=>
        {
            var w=Workspace();Throws(()=>w.BuildBulkCandidate(Array.Empty<string>()));
            Throws(()=>w.BuildBulkCandidate(["UNKNOWN"]));True(w.Candidate is null&&w.UndoCount==0);
        });
        Check("measured_representative_review_final_replaces_nonzero_target_instead_of_adding",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});
            var preview=w.PreviewReferenceCopy(a.Key,[b.Key]);Offset(preview.Final,13,-1);
            var c=w.ConfirmReferenceCopy(preview,[b.Key],"absolute Review F");var row=c.Rows.Single();
            Offset(row.Base,20,5);Offset(row.Delta,-7,-6);Offset(row.Final,13,-1);
            True(w.ValidateCandidate(true).Ok);var write=w.CreateApplySnapshot(true);w.CommitApplied(write,"ABSOLUTE-SAVED","synthetic writer");
            Offset(w.ActiveOffsets[b.Key],13,-1);True(w.PreviewBulkCorrection([b.Key]).Keys.Count==0);
        });
        Check("saved_representative_review_final_is_not_a_residual_or_invented_measurement",()=>
        {
            var a=Point(1,judge:"UNMEASURED") with{OriginalMeasurement=null,HasAppliedOffsetEvidence=true};var b=Point(2);
            var w=Workspace([a,b],new(){[a.Key]=new(12,-3),[b.Key]=new(20,5)});var value=w.ResolveReferenceValue(a.Key);
            Offset(value.Final,12,-3);True(value.Kind=="APPLIED_OFFSET"&&value.Judge=="OK"&&value.ErrorXUm is null&&w.GetPoint(a.Key).Judge=="UNMEASURED");
            var copy=w.PreviewReferenceCopy(a.Key,[b.Key]);w.ConfirmReferenceCopy(copy,[b.Key],"known saved Review F");
            True(w.ValidateCandidate(true).Ok);Offset(w.Candidate!.Rows.Single().Final,12,-3);
        });
        Check("scoped_bulk_preserves_other_cell_guard_exclusions_and_frozen_proxy_final",()=>
        {
            var a=Point(1,2,1);var b=Point(2);var other=Point(1,cell:2,judge:"NG");var w=Workspace([a,b,other]);
            var copy=w.PreviewReferenceCopy(a.Key,[b.Key]);w.ConfirmReferenceCopy(copy,[b.Key],"frozen F");
            w.RecordMeasurement(a.Key,Measurement(9,9,raw:"NEWER-SOURCE"));
            w.BuildBulkCandidate([b.Key]);True(w.ValidateCandidate(true).Ok);Offset(w.Candidate!.Rows.Single().Final,2,1);
            True(w.GetPoint(other.Key).Judge=="NG"&&w.GetPoint(other.Key).OriginalMeasurement==other.OriginalMeasurement);
        });
        Check("cell_bulk_one_pass_summaries_equal_whole_preview_and_each_explicit_cell",()=>
        {
            var points=new[]{Point(1),Point(2,judge:"NG"),Point(3,cell:2),Point(4,cell:2) with{Masked=true},Point(5,cell:3,judge:"UNMEASURED")};
            var w=Workspace(points);var cells=w.PreviewBulkCorrectionByCell();var all=w.PreviewBulkCorrection();
            True(cells.Count==3&&all.Keys.ToHashSet().SetEquals(cells.Values.SelectMany(p=>p.Keys))&&all.Total==cells.Values.Sum(p=>p.Total)
                &&all.ExcludedNg==cells.Values.Sum(p=>p.ExcludedNg)&&all.ExcludedMasked==cells.Values.Sum(p=>p.ExcludedMasked)
                &&all.ExcludedUnmeasured==cells.Values.Sum(p=>p.ExcludedUnmeasured));
            foreach(var (cellNo,cell) in cells)
            {var exact=w.PreviewBulkCorrection(points.Where(p=>p.CellNo==cellNo).Select(p=>p.Key));True(exact.Keys.SequenceEqual(cell.Keys)&&exact.Total==cell.Total);}
        });
    }
}
