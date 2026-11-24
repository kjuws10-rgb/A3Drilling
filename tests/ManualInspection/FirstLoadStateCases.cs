using System.Text.Json;
using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunFirstLoadStateChecks()
    {
        static ST_MANUAL_POINT Blank(int shot,bool init=false)=>Point(shot) with {Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null,IsMeasurementOffsetInitialized=init};
        static void Copy(CManualInspectionWorkspace w,string source,string target)=>w.ConfirmReferenceStateCopy(w.PreviewReferenceStateCopy(source,[target]),[target],"synthetic captured absence");
        Check("state_copy_initial_absence_never_creates_numeric_zero_ok_raw_or_candidate",()=>
        {
            var w=Workspace([Blank(1,true),Blank(2,true)]);Copy(w,Key(1),Key(2));var p=w.GetPoint(Key(2));
            True(p.IsMeasurementStateCleared&&p.IsMeasurementOffsetInitialized&&p.Judge=="UNMEASURED"&&p.ErrorXUm is null&&p.ErrorYUm is null&&p.OriginalMeasurement is null&&p.ProxySource is null&&w.Candidate is null);
            True(w.CanCopyReferenceState(Key(2))&&w.PreviewBulkCorrection().Keys.Count==0);Throws(()=>w.ResolveReferenceValue(Key(2)));Throws(()=>w.BuildDirectCandidate([Key(2)]));
        });
        Check("state_copy_preserves_nonzero_active_base_and_ng_original_and_ledger_then_undo",()=>
        {
            var target=Point(2,8,-4,"NG") with { HasAppliedOffsetEvidence=true };var w=Workspace([Blank(1),target],new(){[Key(2)]=new(17,-9)});
            w.SeedConsumedMeasurements([new(Key(2),"OLD-CONSUMED")]);var before=w.GetPoint(Key(2));Copy(w,Key(1),Key(2));var p=w.GetPoint(Key(2));
            Offset(w.ActiveOffsets[Key(2)],17,-9);True(!p.IsMeasurementOffsetInitialized&&p.IsMeasurementStateCleared&&p.OriginalMeasurement==before.OriginalMeasurement&&p.HasAppliedOffsetEvidence&&w.WasConsumed(Key(2),"OLD-CONSUMED"));
            True(!w.PreviewBulkCorrection().Keys.Contains(Key(2)));w.UndoReferenceCopy();True(w.GetPoint(Key(2))==before&&w.WasConsumed(Key(2),"OLD-CONSUMED"));
        });
        Check("state_copy_only_removes_affected_numeric_candidates_and_keeps_other_rows",()=>
        {
            var w=Workspace([Blank(1),Point(2),Point(3)]);var old=w.BuildDirectCandidate([Key(2),Key(3)]);Copy(w,Key(1),Key(2));
            True(w.Candidate!.Rows.Count==1&&w.Candidate.Rows[0].Key==Key(3)&&w.Candidate.Rows[0].Final==old.Rows[1].Final);
            w.UndoReferenceCopy();True(w.Candidate==old&&!w.GetPoint(Key(2)).IsMeasurementStateCleared);
        });
        Check("state_copy_error_status_and_masked_source_preserve_no_correction_eligibility",()=>
        {
            var w=Workspace([Blank(1) with {Judge="ERROR",Masked=true},Blank(2),Blank(3) with{Masked=true}]);var preview=w.PreviewReferenceStateCopy(Key(1),[Key(2),Key(3)]);
            True(preview.Targets.SequenceEqual([Key(2)])&&preview.ExcludedMaskedKeys.SequenceEqual([Key(3)]));w.ConfirmReferenceStateCopy(preview,[Key(2),Key(3)],"copy error state");
            True(w.GetPoint(Key(2)).Judge=="ERROR"&&w.GetPoint(Key(2)).ErrorXUm is null&&!w.GetPoint(Key(3)).IsMeasurementStateCleared);Throws(()=>w.BuildBulkCandidate());
        });
        Check("state_copy_is_captured_and_reference_later_measurement_does_not_resurrect_target",()=>
        {
            var w=Workspace([Blank(1),Point(2)]);Copy(w,Key(1),Key(2));var captured=w.GetPoint(Key(2));w.RecordMeasurement(Key(1),Measurement(4,5,raw:"FRESH-REFERENCE"));
            True(w.GetPoint(Key(2))==captured&&w.CanCopyReferenceState(Key(2))&&!w.PreviewBulkCorrection().Keys.Contains(Key(2)));
        });
        Check("state_copy_stale_preview_and_blank_reason_are_non_mutating",()=>
        {
            var w=Workspace([Blank(1),Point(2)]);var preview=w.PreviewReferenceStateCopy(Key(1),[Key(2)]);var target=w.GetPoint(Key(2));
            Throws(()=>w.ConfirmReferenceStateCopy(preview,[Key(2)]," "));True(w.GetPoint(Key(2))==target);
            w.RecordMeasurement(Key(1),Measurement(1,1));Throws(()=>w.ConfirmReferenceStateCopy(preview,[Key(2)],"stale"));True(w.GetPoint(Key(2))==target);
        });
        Check("state_copy_session_roundtrip_keeps_nonzero_base_and_empty_metadata_without_fake_raw",()=>
        {
            var w=Workspace([Blank(1),Point(2)],new(){[Key(2)]=new(17,-9)});Copy(w,Key(1),Key(2));var session=JsonSerializer.Deserialize<ST_MANUAL_SESSION>(JsonSerializer.Serialize(w.CaptureSession()))!;
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(session,Initial,[Blank(1),Point(2)],w.ActiveOffsets,InsideField);
            True(restored.GetPoint(Key(2)).IsMeasurementStateCleared&&restored.GetPoint(Key(2)).StateCopySource==w.GetPoint(Key(2)).StateCopySource&&restored.GetPoint(Key(2)).OriginalMeasurement==w.GetPoint(Key(2)).OriginalMeasurement);
            Offset(restored.ActiveOffsets[Key(2)],17,-9);Throws(()=>restored.ResolveReferenceValue(Key(2)));
        });
        Check("state_copy_tampered_ok_self_missing_reference_and_foreign_frame_are_rejected",()=>
        {
            var w=Workspace([Blank(1),Point(2)]);Copy(w,Key(1),Key(2));var saved=w.CaptureSession();var p=w.GetPoint(Key(2));
            foreach(var bad in new[]{p with{Judge="OK",StateCopySource=p.StateCopySource! with{Judge="OK"}},p with{StateCopySource=p.StateCopySource! with{ReferenceKey=p.Key}},p with{StateCopySource=p.StateCopySource! with{ReferenceKey="MISSING"}},p with{StateCopySource=p.StateCopySource! with{CoordinateSignature="OTHER"}},p with{StateCopySource=p.StateCopySource! with{RecipeId="OTHER"}}})
            {
                var session=saved with{Points=saved.Points.Select(value=>value.Key==p.Key?bad:value).ToArray()};var restored=new CManualInspectionWorkspace();
                Throws(()=>restored.RestoreSession(session,Initial,[Blank(1),Point(2)],w.ActiveOffsets,InsideField));
            }
        });
        Check("state_copy_old_and_equal_time_external_sync_cannot_resurrect_cleared_result",()=>
        {
            var w=Workspace([Blank(1),Point(2)]);Copy(w,Key(1),Key(2));var p=w.GetPoint(Key(2));var at=p.StateCopySource!.CopiedAt;
            var incoming=Point(2,9,9) with{Source="IMPORTED"};foreach(var t in new[]{at.AddTicks(-1),at})True(w.MergeImportedObservations([incoming],new Dictionary<string,DateTimeOffset>{{Key(2),t}})==0);
            True(w.GetPoint(Key(2))==p);True(w.MergeImportedObservations([incoming],new Dictionary<string,DateTimeOffset>{{Key(2),at.AddTicks(1)}})==1);
            True(!w.GetPoint(Key(2)).IsMeasurementStateCleared&&w.GetPoint(Key(2)).StateCopySource is null&&w.GetPoint(Key(2)).Source=="IMPORTED");
        });
        Check("state_copy_clock_rollback_and_real_new_zero_measurement_clear_only_working_state",()=>
        {
            var future=DateTimeOffset.UtcNow.AddDays(1);var w=Workspace([Blank(1),Point(2) with{ExternalObservedAt=future}]);Copy(w,Key(1),Key(2));var copied=w.GetPoint(Key(2)).StateCopySource!.CopiedAt;True(copied>future);
            w.RecordMeasurement(Key(2),Measurement(0,0,raw:"ACTUAL-ZERO") with{MeasuredAt=DateTimeOffset.UtcNow.AddDays(-2)});var p=w.GetPoint(Key(2));
            True(!p.IsMeasurementStateCleared&&p.StateCopySource is null&&p.IsDirectMeasurement&&p.Judge=="OK"&&p.HasFiniteError&&p.MeasurementStateChangedAt>copied);
            var old=Point(2,5,5) with{Source="IMPORTED"};True(w.MergeImportedObservations([old],new Dictionary<string,DateTimeOffset>{{Key(2),copied}})==0);True(w.GetPoint(Key(2)).ErrorXUm==0);
        });
        Check("state_copy_numeric_copy_and_saved_offset_init_clear_logical_metadata",()=>
        {
            var w=Workspace([Blank(1),Point(2),Point(3)]);Copy(w,Key(1),Key(2));w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(3),[Key(2)]),[Key(2)],"replace absence with finite proof");
            True(!w.GetPoint(Key(2)).IsMeasurementStateCleared&&w.GetPoint(Key(2)).StateCopySource is null&&w.GetPoint(Key(2)).ProxySource is not null);
            Copy(w,Key(1),Key(2));w.InitializeMeasurementOffsets();True(w.ValidateCandidate(false).Ok);Apply(w,"INIT-AFTER-STATE");
            True(w.GetPoint(Key(2)).IsMeasurementOffsetInitialized&&!w.GetPoint(Key(2)).IsMeasurementStateCleared&&w.GetPoint(Key(2)).StateCopySource is null);
        });
        Check("state_copy_reconnect_does_not_invalidate_captured_metadata_or_promote_raw",()=>
        {
            var w=Workspace([Blank(1),Point(2)]);Copy(w,Key(1),Key(2));var points=w.Points.ToArray();var uncertain=Initial with{LineageValid=false};
            var restored=new CManualInspectionWorkspace();restored.Load(uncertain,points,w.ActiveOffsets,DefaultLimits,InsideField);restored.ConfirmEditableMeasurementContext(Initial);
            True(restored.GetPoint(Key(2)).Source=="MANUAL_STATE"&&restored.GetPoint(Key(2)).IsMeasurementStateCleared&&!restored.GetPoint(Key(2)).IsDirectMeasurement);
            var session=restored.CaptureSession();var other=new CManualInspectionWorkspace();other.RestoreSession(session,Initial,points,w.ActiveOffsets,InsideField);True(other.CanCopyReferenceState(Key(2)));
        });
        Check("state_copy_failed_measurement_retirement_never_revives_prior_applied_base_or_raw",()=>
        {
            foreach(var nextOperation in new[]{false,true})
            {
                var w=Workspace([Blank(1),Point(2) with{HasAppliedOffsetEvidence=true},Point(3)],new(){[Key(2)]=new(13,-8)});Copy(w,Key(1),Key(2));var captured=w.GetPoint(Key(2));
                var op=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(2)]));w.CompleteOperation(op.Id,EN_MANUAL_OPERATION_STATE.Error,"no camera response");
                if(nextOperation){var next=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Move,[Key(3)]));w.MarkOperationPointCompleted(next.Id,Key(3));w.CompleteOperation(next.Id);}else w.ReleaseIncompleteOperation("explicit idle recovery");
                var p=w.GetPoint(Key(2));True(p.IsMeasurementStateCleared&&p.StateCopySource==captured.StateCopySource&&p.Judge=="UNMEASURED"&&p.OriginalMeasurement==captured.OriginalMeasurement&&p.MeasurementStateChangedAt>captured.MeasurementStateChangedAt);
                Offset(w.ActiveOffsets[Key(2)],13,-8);True(w.CanCopyReferenceState(Key(2)));Throws(()=>w.ResolveReferenceValue(Key(2)));Copy(w,Key(2),Key(3));True(w.GetPoint(Key(3)).IsMeasurementStateCleared);
            }
        });
        Check("state_copy_foreign_import_original_provenance_survives_init_absolute_save_and_undo",()=>
        {
            foreach(var initialize in new[]{false,true})
            {
                var foreign=Point(2) with{Source="IMPORTED",AdoptedFileMeasurement=null,OriginalMeasurement=Measurement(5,6) with{RunId="FOREIGN",CoordinateSignature="FOREIGN"}};
                var w=Workspace([Blank(1),foreign],new(){[Key(2)]=new(13,-8)});Copy(w,Key(1),Key(2));
                if(initialize)w.InitializeMeasurementOffsets();else w.BuildOffsetReplacementCandidate(w.ActiveOffsets.ToDictionary(pair=>pair.Key,_=>new ST_MANUAL_OFFSET(4,5)),"synthetic absolute baseline",false);
                True(w.ValidateCandidate(false).Ok);Apply(w,"STATE-REPLACED");var current=w.Context!;
                var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),current,[Blank(1),foreign],w.ActiveOffsets,InsideField);
                True(restored.Context!.LineageValid&&restored.GetPoint(Key(2)).Source=="IMPORTED"&&restored.GetPoint(Key(2)).OriginalMeasurement==foreign.OriginalMeasurement);
                var undo=w.CreateRestorePreviousSnapshot();w.CommitRestored(undo,"STATE-UNDO","restore original baseline");
                restored=new();restored.RestoreSession(w.CaptureSession(),w.Context!,[Blank(1),foreign],w.ActiveOffsets,InsideField);True(restored.Context!.LineageValid&&restored.GetPoint(Key(2)).Source=="IMPORTED");
            }
        });
        Check("state_copy_absence_shared_shot_save_preserves_captured_state_and_actual_ng_raw",()=>
        {
            foreach(var judge in new[]{"UNMEASURED","ERROR","NG"})
            {
                var source=Blank(1) with{Judge=judge,Source="MANUAL_STATE",IsMeasurementStateCleared=true,
                    StateCopySource=new(Key(3),judge,"captured status fixture",Initial.RunId,Initial.CoordinateSignature,DateTimeOffset.UtcNow){RecipeId=Initial.RecipeId}};
                var target=Point(2,8,9,"NG");var sibling=Point(2,2,3,beam:12);var origin=Blank(3);var w=Workspace([source,target,sibling,origin]);Copy(w,Key(1),Key(2));var captured=w.GetPoint(Key(2));
                w.BuildDirectCandidate([sibling.Key]);True(w.ValidateCandidate(false).Ok);Apply(w,"SHARED-SHOT-STATE");
                var p=w.GetPoint(Key(2));True(p.IsMeasurementStateCleared&&p.StateCopySource==captured.StateCopySource&&p.Judge==judge&&p.Source=="MANUAL_STATE"&&p.HasAppliedOffsetEvidence&&p.OriginalMeasurement==target.OriginalMeasurement);
                Offset(w.ActiveOffsets[p.Key],2,3);var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,[source,target,sibling,origin],w.ActiveOffsets,InsideField);True(restored.Context!.LineageValid&&restored.GetPoint(p.Key).IsMeasurementStateCleared);
            }
        });
    }
}
