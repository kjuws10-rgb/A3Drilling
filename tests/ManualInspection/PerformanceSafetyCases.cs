using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunPerformanceSafetyChecks()
    {
        Check("operation_preview_is_local_to_workspace_even_for_identical_loaded_data",()=>
        {
            var a=Workspace();var b=Workspace();var preview=a.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]);
            Throws(()=>b.BeginOperation(preview));
            _=a.Points;_=a.ActiveOffsets;_=a.CaptureSession(); // Reads must not invalidate a legitimate preview.
            var started=a.BeginOperation(preview);True(started.Keys.SequenceEqual([Key(1)]));a.CompleteOperation(started.Id,EN_MANUAL_OPERATION_STATE.Stopped);
        });
        Check("same_frame_reload_never_resurrects_an_earlier_operation_preview",()=>
        {
            var w=Workspace();var preview=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Measure,[Key(1)]);
            w.Load(Initial,w.Points,w.ActiveOffsets,DefaultLimits,InsideField);Throws(()=>w.BeginOperation(preview));
            True(w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Measure,[Key(1)])).Keys.Single()==Key(1));
        });
        Check("same_frame_session_restore_never_resurrects_an_earlier_operation_preview",()=>
        {
            var w=Workspace();var preview=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]);var session=w.CaptureSession();
            w.RestoreSession(session,Initial,w.Points,w.ActiveOffsets,InsideField);Throws(()=>w.BeginOperation(preview));
        });
        Check("measurement_and_limit_changes_reject_old_operation_previews",()=>
        {
            foreach(var change in new Action<CManualInspectionWorkspace>[]
                {w=>w.RecordMeasurement(Key(1),Measurement(2,1,raw:"FRESH_FOR_STAMP")),w=>w.SetLimits(DefaultLimits with{MaxDeltaUm=29})})
            {var w=Workspace();var preview=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]);change(w);Throws(()=>w.BeginOperation(preview));}
        });
        Check("stopped_operation_and_new_candidate_changes_reject_old_operation_previews",()=>
        {
            var w=Workspace();var old=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]);
            var active=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(2)]));w.CompleteOperation(active.Id,EN_MANUAL_OPERATION_STATE.Stopped);
            Throws(()=>w.BeginOperation(old));
            w=Workspace();old=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]);w.BuildDirectCandidate([Key(2)]);Throws(()=>w.BeginOperation(old));
        });
        Check("cached_point_and_offset_snapshots_are_readonly_and_previous_values_do_not_change",()=>
        {
            var w=Workspace();var points=w.Points;var values=w.ActiveOffsets;var point=points.Single(p=>p.Key==Key(1));
            Throws(()=>((IList<ST_MANUAL_POINT>)points)[0]=Point(4));Throws(()=>((IDictionary<string,ST_MANUAL_OFFSET>)values)[Key(1)]=new(99,99));
            w.RecordMeasurement(Key(1),Measurement(7,1,raw:"NEW_CACHE_POINT"));
            True(points.Single(p=>p.Key==Key(1))==point&&point.ErrorXUm==3&&w.Points.Single(p=>p.Key==Key(1)).ErrorXUm==7);Offset(values[Key(1)],0,0);
        });
        Check("offset_save_init_and_undo_refresh_caches_without_mutating_previous_snapshots",()=>
        {
            static void Save(CManualInspectionWorkspace w,string id)
            {True(w.ValidateCandidate(false).Ok);var snapshot=w.CreateApplySnapshot(false);w.CommitApplied(snapshot,id,"cache public state verification");}
            var w=Workspace();var original=w.ActiveOffsets;var originalPoints=w.Points;w.BuildDirectCandidate([Key(1)]);Save(w,"CACHE-SAVED");
            var saved=w.ActiveOffsets;var savedPoints=w.Points;Offset(saved[Key(1)],3,-2);Offset(original[Key(1)],0,0);True(!originalPoints.Single(p=>p.Key==Key(1)).HasAppliedOffsetEvidence);
            w.BuildScopedOffsetReplacementCandidate(1,w.ActiveOffsets,w.ActiveInitializedOffsetKeys.ToHashSet(),"current Cell Init",true);Save(w,"CACHE-INIT");
            Offset(w.ActiveOffsets[Key(1)],0,0);Offset(saved[Key(1)],3,-2);True(w.Points.Single(p=>p.Key==Key(1)).IsMeasurementOffsetInitialized&&!savedPoints.Single(p=>p.Key==Key(1)).IsMeasurementOffsetInitialized);
            var initialized=w.ActiveOffsets;var restore=w.CreateRestorePreviousSnapshot();w.CommitRestored(restore,"CACHE-UNDO","undo");
            Offset(w.ActiveOffsets[Key(1)],3,-2);Offset(initialized[Key(1)],0,0);True(!w.Points.Single(p=>p.Key==Key(1)).IsMeasurementOffsetInitialized);
        });
        Check("persisted_candidate_source_fingerprint_still_rejects_changed_raw_and_limits",()=>
        {
            foreach(var change in new Action<CManualInspectionWorkspace>[]
                {w=>w.RecordMeasurement(Key(1),Measurement(2,1,raw:"CHANGED_PERSISTED_PROOF")),w=>w.SetLimits(DefaultLimits with{MaxTotalUm=199})})
            {var w=Workspace();w.BuildDirectCandidate([Key(1)]);True(w.ValidateCandidate(false).Ok);var fingerprint=w.Candidate!.SourceFingerprint;change(w);
                True(!w.ValidateCandidate(false).Ok&&w.Candidate!.SourceFingerprint==fingerprint);Throws(()=>w.CreateApplySnapshot(false));}
        });
    }
}
