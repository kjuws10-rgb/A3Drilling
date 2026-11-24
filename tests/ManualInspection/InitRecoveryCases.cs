using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunInitRecoveryChecks()
    {
        static CManualInspectionWorkspace Large(ManualInspectionFieldEvaluator? field=null)
            => Workspace([Point(1),Point(2,cell:2,head:2)],new(){[Key(1)]=new(400,-700),[Key(2,2,2)]=new(250,-900)},field??InsideField);
        Check("init_recovery_scoped_large_legacy_keep_is_not_a_new_total_or_field_request",()=>
        {
            var checkedKeys=new List<string>();var w=Large((p,f,_)=>{checkedKeys.Add(p.Key);return new(p.CellNo==1&&f==ST_MANUAL_OFFSET.Zero,"unchanged legacy field");});
            w.InitializeMeasurementOffsets([Key(1)],[1]);var v=w.ValidateCandidate(false);True(v.Ok&&v.MaxTotalUm==0&&checkedKeys.SequenceEqual([Key(1)]));
            var write=w.CreateApplySnapshot(false);w.CommitApplied(write,"SCOPED-LARGE-INIT","synthetic reset");
            Offset(w.ActiveOffsets[Key(1)],0,0);Offset(w.ActiveOffsets[Key(2,2,2)],250,-900);True(w.GetPoint(Key(1)).IsMeasurementOffsetInitialized&&!w.GetPoint(Key(2,2,2)).IsMeasurementOffsetInitialized);
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);True(restored.ActiveOffsets[Key(2,2,2)]==w.ActiveOffsets[Key(2,2,2)]);
        });
        Check("init_recovery_all_removes_large_delta_without_increasing_limits",()=>
        {
            var w=Large();w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok);Apply(w,"ALL-LARGE-INIT");
            True(w.ActiveOffsets.Values.All(v=>v==ST_MANUAL_OFFSET.Zero)&&w.Points.All(p=>p.IsMeasurementOffsetInitialized)&&w.Limits==DefaultLimits);
        });
        Check("init_recovery_head_scope_includes_shared_shot_siblings_but_keeps_other_large_cells",()=>
        {
            var a=Point(1);var sibling=Point(1,head:2,beam:12);var other=Point(2,cell:2,head:2);
            var w=Workspace([a,sibling,other],new(){[a.Key]=new(400,700),[sibling.Key]=new(400,700),[other.Key]=new(250,900)});
            w.InitializeMeasurementOffsets([a.Key]);True(w.ValidateCandidate(false).Ok);Apply(w,"HEAD-SHARED-INIT");
            True(w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&w.GetPoint(sibling.Key).IsMeasurementOffsetInitialized);Offset(w.ActiveOffsets[other.Key],250,900);
        });
        Check("init_recovery_cancel_returns_original_candidate_raw_and_large_active_base",()=>
        {
            var w=Large();var candidate=w.BuildDirectCandidate([Key(1)]);var original=w.GetPoint(Key(1));w.InitializeMeasurementOffsets([Key(1)],[1]);True(w.ValidateCandidate(false).Ok);w.CancelOffsetReplacement();
            True(!w.HasPendingOffsetEdit&&w.Candidate!.Rows.SequenceEqual(candidate.Rows)&&w.GetPoint(Key(1))==original);Offset(w.ActiveOffsets[Key(1)],400,-700);
            var move=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]);True(move.Keys.SequenceEqual([Key(1)]));
        });
        Check("init_recovery_target_field_failure_still_blocks_and_can_cancel",()=>
        {
            var w=Large((_,_,_)=>new(false,"protected target field"));w.InitializeMeasurementOffsets([Key(1)],[1]);True(!w.ValidateCandidate(false).Ok);Throws(()=>w.CreateApplySnapshot(false));w.CancelOffsetReplacement();
            True(!w.HasPendingOffsetEdit);Offset(w.ActiveOffsets[Key(1)],400,-700);
        });
        Check("init_recovery_normal_measurement_and_absolute_file_do_not_inherit_keep_exemption",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(200,0)});w.SetLimits(DefaultLimits with{MaxDeltaUm=500});w.RecordMeasurement(Key(1),Measurement(1,0));w.BuildDirectCandidate([Key(1)]);
            True(!w.ValidateCandidate(false).Ok&&w.Candidate!.Validation!.Errors.Any(e=>e.Contains("누적 총량")));
            w=Large();w.BuildOffsetReplacementCandidate(w.ActiveOffsets,"absolute input not init",false);True(!w.ValidateCandidate(false).Ok);
        });
        Check("init_recovery_keep_forgery_or_metadata_change_is_rejected",()=>
        {
            var w=Large();w.InitializeMeasurementOffsets([Key(1)],[1]);var session=w.CaptureSession();var keep=session.Candidate!.Rows.Single(r=>r.Key==Key(2,2,2));
            foreach(var bad in new[]{session with{Candidate=session.Candidate with{Rows=session.Candidate.Rows.Select(r=>r.Key==keep.Key?r with{Final=new(251,-900),Delta=new(1,0)}:r).ToArray()}},
                session with{Candidate=session.Candidate with{Rows=session.Candidate.Rows.Select(r=>r.Key==Key(1)?r with{Final=new(1,0),Delta=new(-399,700)}:r).ToArray()}}})
            {
                var rejected=false;try{var f=new CManualInspectionWorkspace();f.RestoreSession(bad,Initial,w.Points,w.ActiveOffsets,InsideField);rejected=!f.ValidateCandidate(false).Ok;}catch(InvalidOperationException){rejected=true;}True(rejected);
            }
            w=Workspace(values:new(){[Key(1)]=new(400,0)});w.InitializeMeasurementOffsets([Key(1)]);session=w.CaptureSession();var outside=w.GetPoint(Key(2)).OffsetKey;
            var altered=session with{Candidate=session.Candidate! with{InitializedOffsetKeys=session.Candidate!.InitializedOffsetKeys.Append(outside).ToArray()}};
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(altered,Initial,w.Points,w.ActiveOffsets,InsideField);True(!restored.ValidateCandidate(false).Ok);
        });
        Check("init_recovery_commit_keeps_exact_or_file_rounded_legacy_precision_but_rejects_drift",()=>
        {
            foreach(var originalPrecision in new[]{true,false})
            {
                var w=Workspace([Point(1),Point(2)],new(){[Key(1)]=new(400,0),[Key(2)]=new(250.123456,-300.654321)});
                w.InitializeMeasurementOffsets([Key(1)]);True(w.ValidateCandidate(false).Ok);var write=w.CreateApplySnapshot(false);var actual=write.ValuesByPointKey.ToDictionary(p=>p.Key,p=>p.Value);
                if(originalPrecision)actual[Key(2)]=w.ActiveOffsets[Key(2)];w.CommitApplied(write,"PRECISION-INIT","synthetic writer",actual);True(w.ActiveOffsets[Key(2)]==actual[Key(2)]);
            }
            var bad=Large();bad.InitializeMeasurementOffsets([Key(1)],[1]);True(bad.ValidateCandidate(false).Ok);var snapshot=bad.CreateApplySnapshot(false);var values=snapshot.ValuesByPointKey.ToDictionary(p=>p.Key,p=>p.Value);values[Key(2,2,2)]=new(250.0001,-900);
            Throws(()=>bad.CommitApplied(snapshot,"BAD-KEEP","tampered writer",values));bad.AbortApply(snapshot);True(bad.HasPendingOffsetEdit);Offset(bad.ActiveOffsets[Key(2,2,2)],250,-900);
        });
        Check("init_recovery_verified_loaded_large_revision_can_undo_redo_but_field_guard_stays",()=>
        {
            var fieldOk=true;var w=Large((_,_,_)=>new(fieldOk,"protected field"));w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok);Apply(w,"INIT-UNDO-LARGE");
            fieldOk=false;Throws(()=>w.CreateRestorePreviousSnapshot());fieldOk=true;
            var undo=w.CreateRestorePreviousSnapshot();w.CommitRestored(undo,"UNDO-LARGE","known actual baseline");Offset(w.ActiveOffsets[Key(1)],400,-700);
            var redo=w.CreateRestoreNextSnapshot();w.CommitRestored(redo,"REDO-INIT","known committed init");True(w.Points.All(p=>p.IsMeasurementOffsetInitialized));
        });
        Check("init_recovery_external_or_tampered_large_history_cannot_forge_trusted_revision",()=>
        {
            var w=Large();w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok);Apply(w,"INIT-PROOF");var session=w.CaptureSession();
            var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(session,w.Context!,w.Points,w.ActiveOffsets,InsideField);Throws(()=>fresh.CreateRestorePreviousSnapshot());
            var first=session.Revisions[0];var altered=first with{ValuesByPointKey=first.ValuesByPointKey.ToDictionary(p=>p.Key,p=>new ST_MANUAL_OFFSET(p.Value.XUm+1,p.Value.YUm))};
            var forged=session with{Revisions=session.Revisions.Select(r=>r.Id==first.Id?altered:r).ToArray()};
            w.RestoreSession(forged,w.Context!,w.Points,w.ActiveOffsets,InsideField);Throws(()=>w.CreateRestorePreviousSnapshot());
            var ordinary=Workspace(values:new(){[Key(1)]=new(250,0)});ordinary.BuildOffsetReplacementCandidate(ordinary.ActiveOffsets,"arbitrary import",false);True(!ordinary.ValidateCandidate(false).Ok);
        });
        Check("init_recovery_same_native_load_keeps_only_process_known_exact_history",()=>
        {
            var w=Large();w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok);Apply(w,"INIT-REFRESH");var session=w.CaptureSession();
            w.Load(w.Context!,w.Points,w.ActiveOffsets,DefaultLimits,InsideField,w.ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal));
            w.RestoreSession(session,w.Context!,w.Points,w.ActiveOffsets,InsideField);var undo=w.CreateRestorePreviousSnapshot();w.CommitRestored(undo,"UNDO-REFRESH","same native values");Offset(w.ActiveOffsets[Key(1)],400,-700);
            // Same revision text with different actual contents cannot retain history permission.
            var f=Large();f.InitializeMeasurementOffsets();True(f.ValidateCandidate(false).Ok);Apply(f,"INIT-CHANGED");var captured=f.CaptureSession();var actual=f.ActiveOffsets.ToDictionary(p=>p.Key,p=>p.Value);actual[Key(1)]=new(1,0);
            var actualPoints=f.Points.Select(p=>p.Key==Key(1)?p with{IsMeasurementOffsetInitialized=false}:p).ToArray();
            f.Load(f.Context!,actualPoints,actual,DefaultLimits,InsideField);Throws(()=>f.RestoreSession(captured,f.Context!,actualPoints,actual,InsideField));
        });
        Check("init_recovery_limit_edit_revalidates_current_absolute_draft_but_never_a_stale_measurement_candidate",()=>
        {
            var w=Large();w.InitializeMeasurementOffsets([Key(1)],[1]);True(w.ValidateCandidate(false).Ok);w.SetLimits(DefaultLimits with{MaxTotalUm=50,MaxDeltaUm=1});True(w.Candidate!.Validation is null&&w.ValidateCandidate(false).Ok);
            var ordinary=Workspace();ordinary.BuildDirectCandidate([Key(1)]);ordinary.SetLimits(DefaultLimits with{MaxTotalUm=50});True(!ordinary.ValidateCandidate(false).Ok);
        });
    }
}
