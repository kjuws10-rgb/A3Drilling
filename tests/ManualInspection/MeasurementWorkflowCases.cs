using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunMeasurementWorkflowChecks()
    {
        static (CManualInspectionWorkspace W, ST_MANUAL_POINT A, ST_MANUAL_POINT Sibling, ST_MANUAL_POINT Other, ST_MANUAL_POINT OtherCell) Scoped()
        {
            var a=Point(1); var sibling=Point(1,beam:1,head:2); var other=Point(2,head:2); var otherCell=Point(1,cell:2);
            return (Workspace([a,sibling,other,otherCell],new(){[a.Key]=new(8,-3),[sibling.Key]=new(8,-3),[other.Key]=new(4,5),[otherCell.Key]=new(-7,6)}),a,sibling,other,otherCell);
        }
        Check("scoped_init_stages_shared_offset_siblings_and_keeps_every_other_base",()=>
        {
            var(w,a,sibling,other,otherCell)=Scoped();var original=w.Points.ToArray();w.InitializeMeasurementOffsets([a.Key]);
            True(w.Candidate is {IsOffsetReplacement:true,InitializeAllReviewRows:false,IsMeasurementOffsetInitialization:true});
            True(w.Candidate!.InitializationOffsetKeys.ToHashSet().SetEquals([a.OffsetKey]));
            foreach(var p in new[]{a,sibling}) Offset(w.Candidate.Rows.Single(r=>r.Key==p.Key).Final,0,0);
            Offset(w.Candidate.Rows.Single(r=>r.Key==other.Key).Final,4,5);Offset(w.Candidate.Rows.Single(r=>r.Key==otherCell.Key).Final,-7,6);
            True(w.Points.SequenceEqual(original));Offset(w.ActiveOffsets[a.Key],8,-3);
        });
        Check("scoped_init_validates_every_final_field_and_retains_outside_raw_eligibility",()=>
        {
            var(w,a,_,other,otherCell)=Scoped();var otherBefore=w.GetPoint(other.Key);var cellBefore=w.GetPoint(otherCell.Key);
            w.InitializeMeasurementOffsets([a.Key]);True(w.ValidateCandidate(false).Ok);Apply(w,"SCOPED_INIT");
            True(w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(a.Key).HasFiniteError);
            True(w.GetPoint(other.Key)==otherBefore&&w.GetPoint(otherCell.Key)==cellBefore);
            True(w.GetPoint(other.Key).IsDirectMeasurement);w.BuildDirectCandidate([other.Key]);True(w.ValidateCandidate(false).Ok);
        });
        Check("scoped_init_commit_gray_metadata_propagates_across_head_siblings",()=>
        {
            var(w,a,sibling,other,otherCell)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);var s=w.CreateApplySnapshot(false);
            True(!s.InitializeAllReviewRows&&s.InitializationOffsetKeys.ToHashSet().SetEquals([a.OffsetKey]));w.CommitApplied(s,"SCOPED","scope QA");
            True(w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&w.GetPoint(sibling.Key).IsMeasurementOffsetInitialized);
            True(!w.GetPoint(other.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(otherCell.Key).IsMeasurementOffsetInitialized);
        });
        Check("scoped_init_rejects_unknown_empty_scope_and_deduplicates_selected_keys",()=>
        {
            var(w,a,_,_,_)=Scoped();Throws(()=>w.InitializeMeasurementOffsets(["NO_SUCH_POINT"]));True(w.Candidate is null);Throws(()=>w.InitializeMeasurementOffsets([]));
            w.InitializeMeasurementOffsets([a.Key,a.Key]);True(w.Candidate!.InitializationOffsetKeys.Count==1);Offset(w.ActiveOffsets[a.Key],8,-3);
        });
        Check("scoped_init_cancel_restores_candidate_and_gray_provenance_exactly",()=>
        {
            var(w,a,_,other,_)=Scoped();w.BuildDirectCandidate([other.Key]);var c=w.Candidate;var p=w.Points.ToArray();
            w.InitializeMeasurementOffsets([a.Key]);w.CancelOffsetReplacement();True(w.Candidate==c&&w.Points.SequenceEqual(p));
        });
        Check("scoped_init_abort_never_changes_active_values_or_initialized_flags",()=>
        {
            var(w,a,_,_,_)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);var s=w.CreateApplySnapshot(false);w.AbortApply(s);
            True(w.Points.All(p=>!p.IsMeasurementOffsetInitialized));Offset(w.ActiveOffsets[a.Key],8,-3);True(w.Candidate!.IsMeasurementOffsetInitialization);
        });
        Check("scoped_init_large_legacy_delta_exemption_does_not_exempt_untouched_fields",()=>
        {
            var a=Point(1);var b=Point(2);var seen=new HashSet<string>();var w=Workspace([a,b],new(){[a.Key]=new(700,-800),[b.Key]=new(2,3)},
                (p,offset,_)=>{seen.Add(p.Key);return new(p.Key!=b.Key,"synthetic preserved location outside field");});
            w.InitializeMeasurementOffsets([a.Key]);True(!w.ValidateCandidate(false).Ok&&seen.Contains(b.Key));
        });
        Check("real_zero_measurement_clears_initialized_state_but_is_still_direct_valid_measurement",()=>
        {
            var(w,a,sibling,other,_)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"INIT");
            w.RecordMeasurement(a.Key,Measurement(0,0,raw:"NEW_REAL_ZERO"));var p=w.GetPoint(a.Key);
            True(!p.IsMeasurementOffsetInitialized&&p.HasFiniteError&&p.IsDirectMeasurement&&p.Judge=="OK");
            True(w.GetPoint(sibling.Key).IsMeasurementOffsetInitialized);w.BuildDirectCandidate([a.Key]);True(w.ValidateCandidate(false).Ok);Apply(w,"ZERO_APPLY");
            True(!w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(sibling.Key).IsMeasurementOffsetInitialized);Offset(w.ActiveOffsets[other.Key],4,5);
        });
        Check("copy_to_initialized_target_restores_proxy_state_without_fabricating_original",()=>
        {
            var(w,a,_,other,_)=Scoped();var original=w.GetPoint(a.Key).OriginalMeasurement;w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"INIT");
            var copy=w.PreviewReferenceCopy(other.Key,[a.Key]);w.ConfirmReferenceCopy(copy,[a.Key],"synthetic explicit replacement");
            True(!w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&w.GetPoint(a.Key).ProxySource is not null&&w.GetPoint(a.Key).OriginalMeasurement==original);
        });
        Check("scoped_init_session_roundtrip_preserves_scope_without_write_approval",()=>
        {
            var(w,a,_,other,_)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);var json=w.ExportSessionJson();
            var restored=Scoped().W;restored.RestoreSessionJson(json,Initial,w.Points,restored.ActiveOffsets,InsideField);
            True(restored.Candidate is {IsMeasurementOffsetInitialization:true,InitializeAllReviewRows:false,Validation:null});
            True(restored.Candidate!.InitializationOffsetKeys.ToHashSet().SetEquals([a.OffsetKey]));Offset(restored.Candidate.Rows.Single(r=>r.Key==other.Key).Final,4,5);Throws(()=>restored.CreateApplySnapshot(false));
        });
        Check("scoped_init_after_a_prior_init_keeps_preexisting_gray_scope",()=>
        {
            var(w,a,_,other,otherCell)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"FIRST");w.InitializeMeasurementOffsets([other.Key]);w.ValidateCandidate(false);Apply(w,"SECOND");
            True(w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&w.GetPoint(other.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(otherCell.Key).IsMeasurementOffsetInitialized);
        });
        Check("valid_measured_zero_initial_load_is_not_classified_as_explicit_offset_init",()=>
        {
            var p=Point(1,0,0);var w=Workspace([p],new(){[p.Key]=ST_MANUAL_OFFSET.Zero});True(!w.GetPoint(p.Key).IsMeasurementOffsetInitialized&&w.GetPoint(p.Key).HasFiniteError);
            w.BuildDirectCandidate([p.Key]);True(w.ValidateCandidate(false).Ok);
        });
        Check("rollback_of_init_restores_previous_nonzero_base_and_noninit_state",()=>
        {
            var(w,a,_,_,_)=Scoped();var raw=w.GetPoint(a.Key).OriginalMeasurement;w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"INIT");
            var restore=w.CreateRestorePreviousSnapshot();True(!restore.InitializedOffsetKeys.Contains(a.OffsetKey));w.CommitRestored(restore,"BACK","synthetic rollback");
            Offset(w.ActiveOffsets[a.Key],8,-3);True(!w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&w.GetPoint(a.Key).OriginalMeasurement==raw);
        });
        Check("rollback_from_real_zero_measurement_to_init_zero_restores_gray_metadata",()=>
        {
            var(w,a,_,_,_)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"INIT");w.RecordMeasurement(a.Key,Measurement(0,0,raw:"FRESH_ZERO"));w.BuildDirectCandidate([a.Key]);w.ValidateCandidate(false);Apply(w,"REAL_ZERO");
            var raw=w.GetPoint(a.Key).OriginalMeasurement;var restore=w.CreateRestorePreviousSnapshot();True(restore.InitializedOffsetKeys.Contains(a.OffsetKey));w.CommitRestored(restore,"BACK_TO_INIT","synthetic rollback");
            True(w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(a.Key).HasFiniteError&&w.GetPoint(a.Key).OriginalMeasurement==raw);Offset(w.ActiveOffsets[a.Key],0,0);
        });
        Check("session_rejects_null_foreign_duplicate_and_nonzero_initialized_revision_metadata",()=>
        {
            var(w,a,_,_,_)=Scoped();var s=w.CaptureSession();
            foreach(var keys in new IReadOnlyList<string>?[]{null,["C99/S999"],[a.OffsetKey,a.OffsetKey],[a.OffsetKey]})
            {
                var bad=s with{Revisions=s.Revisions.Select((r,i)=>i==0?r with{InitializedOffsetKeys=keys!}:r).ToArray()};var fresh=Scoped().W;
                Throws(()=>fresh.RestoreSession(bad,Initial,s.Points,s.ActiveValuesByPointKey,InsideField));
            }
        });
        Check("tampered_init_scope_snapshot_is_rejected_and_untampered_snapshot_can_abort",()=>
        {
            var(w,a,_,other,_)=Scoped();w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);var snapshot=w.CreateApplySnapshot(false);
            Throws(()=>w.CommitApplied(snapshot with{InitializationOffsetKeys=[other.OffsetKey]},"BAD","scope tampering"));w.AbortApply(snapshot);Offset(w.ActiveOffsets[a.Key],8,-3);True(!w.IsBusy);
        });
        Check("session_initialized_point_with_nonzero_active_value_is_rejected",()=>
        {
            var(w,a,_,_,_)=Scoped();var s=w.CaptureSession();var bad=s with{Points=s.Points.Select(p=>p.Key==a.Key?p with{IsMeasurementOffsetInitialized=true}:p).ToArray()};var fresh=Scoped().W;
            Throws(()=>fresh.RestoreSession(bad,Initial,s.Points,s.ActiveValuesByPointKey,InsideField));
        });
        Check("unsaved_fresh_measurement_does_not_clear_saved_init_state_when_other_shot_is_applied",()=>
        {
            var a=Point(1);var b=Point(2);var w=Workspace([a,b]);w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"INIT");
            w.RecordMeasurement(a.Key,Measurement(0,0,raw:"UNSAVED_REAL_ZERO"));True(!w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&w.ActiveInitializedOffsetKeys.Contains(a.OffsetKey));
            w.BuildDirectCandidate([b.Key]);w.ValidateCandidate(false);var s=w.CreateApplySnapshot(false);True(s.InitializedOffsetKeys.Contains(a.OffsetKey));w.CommitApplied(s,"OTHER_SHOT","synthetic other correction");
            True(w.ActiveInitializedOffsetKeys.Contains(a.OffsetKey)&&!w.GetPoint(a.Key).IsMeasurementOffsetInitialized);Offset(w.ActiveOffsets[a.Key],0,0);
        });
        Check("legacy_session_without_init_metadata_cannot_erase_proven_native_init_state",()=>
        {
            var raw=Point(1,0,0);var native=raw with{IsMeasurementOffsetInitialized=true,Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null};
            var values=new Dictionary<string,ST_MANUAL_OFFSET>{{raw.Key,ST_MANUAL_OFFSET.Zero}};var current=Workspace([native],values);var before=current.ExportSessionJson();
            var old=Workspace([raw],values).CaptureSession();True(old.Revisions.Single().InitializedOffsetKeys.Count==0&&!old.Points.Single().IsMeasurementOffsetInitialized);
            Throws(()=>current.RestoreSession(old,Initial,[native],values,InsideField));True(current.ExportSessionJson()==before&&current.GetPoint(raw.Key).IsMeasurementOffsetInitialized);
        });
        Check("native_init_revision_can_restore_new_unsaved_measurement_with_clear_point_flag",()=>
        {
            var raw=Point(1,0,0);var native=raw with{IsMeasurementOffsetInitialized=true,Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null};var values=new Dictionary<string,ST_MANUAL_OFFSET>{{raw.Key,ST_MANUAL_OFFSET.Zero}};
            var measured=Workspace([native],values);measured.RecordMeasurement(raw.Key,Measurement(0,0,raw:"POST_INIT_UNSAVED_ZERO"));var session=measured.CaptureSession();
            True(session.Revisions.Single().InitializedOffsetKeys.Contains(raw.OffsetKey)&&!session.Points.Single().IsMeasurementOffsetInitialized);
            var restored=Workspace([native],values);restored.RestoreSession(session,Initial,[native],values,InsideField);
            True(!restored.GetPoint(raw.Key).IsMeasurementOffsetInitialized&&restored.GetPoint(raw.Key).HasFiniteError&&restored.ActiveInitializedOffsetKeys.Contains(raw.OffsetKey));
        });
        Check("legacy_measured_zero_session_without_init_metadata_remains_valid_measured_zero",()=>
        {
            var raw=Point(1,0,0);var values=new Dictionary<string,ST_MANUAL_OFFSET>{{raw.Key,ST_MANUAL_OFFSET.Zero}};var old=Workspace([raw],values).CaptureSession();var current=Workspace([raw],values);
            current.RestoreSession(old,Initial,[raw],values,InsideField);True(!current.GetPoint(raw.Key).IsMeasurementOffsetInitialized&&current.ActiveInitializedOffsetKeys.Count==0&&current.GetPoint(raw.Key).HasFiniteError);
            current.BuildDirectCandidate([raw.Key]);True(current.ValidateCandidate(false).Ok);
        });
    }
}
