using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunOffsetInitChecks()
    {
        Check("offset_init_stages_zero_for_all_cells_without_changing_active_values",()=>
        {
            var a=Point(1);var b=Point(2,cell:2);var sibling=Point(1,beam:1);
            var w=Workspace([a,b,sibling],new(){[a.Key]=new(8,-3),[b.Key]=new(-6,4),[sibling.Key]=new(8,-3)});
            var originals=w.Points.ToDictionary(p=>p.Key);w.InitializeMeasurementOffsets();
            True(w.Candidate is {IsOffsetReplacement:true,InitializeAllReviewRows:true});
            True(w.Candidate!.Rows.Select(r=>r.Key).ToHashSet().SetEquals([a.Key,b.Key,sibling.Key]));
            True(w.Candidate.Rows.All(r=>r.Source=="OFFSET_INIT"&&r.Final==ST_MANUAL_OFFSET.Zero));
            Offset(w.ActiveOffsets[a.Key],8,-3);Offset(w.ActiveOffsets[b.Key],-6,4);
            True(w.Points.All(p=>p==originals[p.Key]));
        });
        Check("offset_init_needs_no_successful_measurement_and_can_clear_ng_or_unmeasured_values",()=>
        {
            var a=Point(1,judge:"NG");var b=Point(2,judge:"UNMEASURED") with{OriginalMeasurement=null};
            var w=Workspace([a,b],new(){[a.Key]=new(4,5),[b.Key]=new(7,-2)});
            w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok);
        });
        Check("offset_init_can_remove_large_legacy_base_but_still_uses_total_and_field_validation",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(700,-800)});w.InitializeMeasurementOffsets();
            var v=w.ValidateCandidate(false);True(v.Ok&&v.MaxTotalUm==0);Offset(w.ActiveOffsets[Key(1)],700,-800);
            var bad=Workspace(evaluator:(_,_,_)=>new(false,"synthetic protected hardware field limit"));bad.InitializeMeasurementOffsets();True(!bad.ValidateCandidate(false).Ok);
        });
        Check("offset_init_checks_entire_workspace_field_even_outside_selection",()=>
        {
            var visited=new HashSet<string>();var w=Workspace(evaluator:(p,f,_)=>{visited.Add(p.Key);True(f==ST_MANUAL_OFFSET.Zero);return new(true,"");});
            w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok&&visited.SetEquals(w.Points.Select(p=>p.Key)));
        });
        Check("offset_init_cancel_restores_previous_candidate_and_working_provenance",()=>
        {
            var w=Workspace();var old=w.BuildDirectCandidate([Key(2)]);var p=w.Points.ToArray();w.InitializeMeasurementOffsets();w.CancelOffsetReplacement();
            True(w.Candidate==old&&w.Points.SequenceEqual(p)&&w.ActiveOffsets.Values.All(v=>v==ST_MANUAL_OFFSET.Zero));
        });
        Check("offset_init_requires_validation_before_write_and_flags_full_replacement",()=>
        {
            var w=Workspace();w.InitializeMeasurementOffsets();Throws(()=>w.CreateApplySnapshot(false));True(w.ValidateCandidate(false).Ok);
            var s=w.CreateApplySnapshot(false);True(s.IsOffsetReplacement&&s.InitializeAllReviewRows&&s.ValuesByPointKey.Values.All(v=>v==ST_MANUAL_OFFSET.Zero));w.AbortApply(s);
        });
        Check("offset_init_abort_keeps_active_base_and_retryable_draft",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(9,10)});w.InitializeMeasurementOffsets();w.ValidateCandidate(false);var s=w.CreateApplySnapshot(false);w.AbortApply(s);
            Offset(w.ActiveOffsets[Key(1)],9,10);True(w.Candidate is {IsOffsetReplacement:true}&&!w.IsBusy);True(w.ValidateCandidate(false).Ok);
        });
        Check("offset_init_commit_clears_working_values_but_preserves_original_and_history",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(9,10)});var raw=w.GetPoint(Key(1)).OriginalMeasurement;
            w.InitializeMeasurementOffsets();w.ValidateCandidate(false);Apply(w,"INIT_REV");
            True(w.ActiveOffsets.Values.All(v=>v==ST_MANUAL_OFFSET.Zero));var point=w.GetPoint(Key(1));
            True(point.OriginalMeasurement==raw&&!point.IsDirectMeasurement&&point.ProxySource is null&&!point.HasFiniteError&&w.History.Any(h=>h.Key==point.Key));
            Throws(()=>w.BuildDirectCandidate([point.Key]));
        });
        Check("offset_init_cannot_reenable_previously_consumed_camera_raw",()=>
        {
            var w=Workspace();w.BuildDirectCandidate([Key(1)]);w.ValidateCandidate(false);var old=w.CreateApplySnapshot(false);w.CommitApplied(old,"OLD_APPLY","synthetic apply");
            var token=old.Rows.Single().MeasurementToken;w.InitializeMeasurementOffsets();w.ValidateCandidate(false);Apply(w,"INIT_REV");True(w.WasConsumed(Key(1),token));
            var oldPoint=Point(1);w.RecordMeasurement(Key(1),oldPoint.OriginalMeasurement!);w.BuildDirectCandidate([Key(1)]);True(!w.ValidateCandidate(false).Ok);
        });
        Check("offset_init_after_commit_fresh_camera_raw_accumulates_from_zero",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(9,10)});w.InitializeMeasurementOffsets();w.ValidateCandidate(false);Apply(w,"INIT_REV");
            w.RecordMeasurement(Key(1),Measurement(.5,-.25,raw:"AFTER_INIT_NEW_HOLE"));var c=w.BuildDirectCandidate([Key(1)]);
            Offset(c.Rows.Single().Base,0,0);Offset(c.Rows.Single().Final,.5,-.25);True(w.ValidateCandidate(false).Ok);
        });
        Check("offset_replacement_import_is_absolute_not_another_accumulation",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(7,8)});var values=w.ActiveOffsets.ToDictionary(p=>p.Key,p=>p.Key==Key(1)?new ST_MANUAL_OFFSET(10,12):p.Value);
            var c=w.BuildOffsetReplacementCandidate(values,"Synthetic portable cumulative result",false);
            True(c.IsOffsetReplacement&&c.Rows.All(r=>r.Source=="OFFSET_FILE"));var row=c.Rows.Single(r=>r.Key==Key(1));Offset(row.Base,7,8);Offset(row.Delta,3,4);Offset(row.Final,10,12);
            True(w.ValidateCandidate(false).Ok);Apply(w,"FILE_REV");Offset(w.ActiveOffsets[Key(1)],10,12);
        });
        Check("offset_replacement_import_still_enforces_delta_limit",()=>
        {
            var w=Workspace();var values=w.ActiveOffsets.ToDictionary(p=>p.Key,p=>p.Key==Key(1)?new ST_MANUAL_OFFSET(31,0):p.Value);
            w.BuildOffsetReplacementCandidate(values,"Synthetic too large imported change",false);True(!w.ValidateCandidate(false).Ok);
        });
        Check("offset_replacement_rejects_partial_unknown_nonfinite_or_conflicting_beam_values",()=>
        {
            var a=Point(1);var sibling=Point(1,beam:1);var w=Workspace([a,sibling]);
            Throws(()=>w.BuildOffsetReplacementCandidate(new Dictionary<string,ST_MANUAL_OFFSET>{{a.Key,new(1,2)}},"partial",false));
            Throws(()=>w.BuildOffsetReplacementCandidate(new Dictionary<string,ST_MANUAL_OFFSET>{{a.Key,new(1,2)},{sibling.Key,new(1,3)}},"conflict",false));
            Throws(()=>w.BuildOffsetReplacementCandidate(new Dictionary<string,ST_MANUAL_OFFSET>{{a.Key,new(double.NaN,0)},{sibling.Key,new(double.NaN,0)}},"invalid",false));
            Throws(()=>w.BuildOffsetReplacementCandidate(w.ActiveOffsets,"",false));
        });
        Check("offset_init_draft_blocks_measurement_and_motion_until_save_or_cancel",()=>
        {
            var w=Workspace();w.InitializeMeasurementOffsets();
            Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Move,[Key(1)]));Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]));
            Throws(()=>w.PreviewReferenceCopy(Key(1),[Key(2)]));Throws(()=>w.BuildDirectCandidate([Key(1)]));
            w.CancelOffsetReplacement();True(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Move,[Key(1)]).Keys.Count==1);
        });
        Check("offset_init_cannot_erase_an_incomplete_measurement_batch",()=>
        {
            var w=Workspace();var p=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1),Key(2)]);var operation=w.BeginOperation(p);w.CompleteOperation(operation.Id,EN_MANUAL_OPERATION_STATE.Stopped,"synthetic interrupted");
            True(w.HasIncompleteMeasurements);Throws(()=>w.InitializeMeasurementOffsets());
        });
        Check("offset_init_session_restore_keeps_draft_and_demands_validation",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(5,-4)});w.InitializeMeasurementOffsets();w.ValidateCandidate(false);var session=w.CaptureSession();var next=new CManualInspectionWorkspace();
            next.RestoreSession(session,w.Context!,w.Points,w.ActiveOffsets,InsideField);
            True(next.Candidate is {IsOffsetReplacement:true,InitializeAllReviewRows:true});Offset(next.ActiveOffsets[Key(1)],5,-4);Throws(()=>next.CreateApplySnapshot(false));True(next.ValidateCandidate(false).Ok);
        });
        Check("offset_init_same_run_reload_cannot_silently_erase_draft",()=>
        {
            var w=Workspace();w.InitializeMeasurementOffsets();Throws(()=>w.Load(w.Context!,w.Points,w.ActiveOffsets,DefaultLimits,InsideField));True(w.Candidate is {IsOffsetReplacement:true});
        });
        Check("offset_init_cancel_after_session_restore_restores_prior_candidate_without_old_approval",()=>
        {
            var w=Workspace();var prior=w.BuildDirectCandidate([Key(2)]);w.ValidateCandidate(false);w.InitializeMeasurementOffsets();var next=new CManualInspectionWorkspace();
            next.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);next.CancelOffsetReplacement();
            True(next.Candidate is {IsOffsetReplacement:false}&&next.Candidate.Rows.SequenceEqual(prior.Rows)&&next.Candidate.Validation is null);Throws(()=>next.CreateApplySnapshot(false));True(next.ValidateCandidate(false).Ok);
        });
    }
}
