using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunRepresentativeApplyChecks()
    {
        static void ManualSave(CManualInspectionWorkspace w,string revision)
        {True(w.ValidateCandidate(true).Ok);var s=w.CreateApplySnapshot(true);w.CommitApplied(s,revision,"synthetic operator/proxy save");}
        Check("representative_saved_known_offset_copies_final_without_residual_and_remains_session_eligible",()=>
        {
            var source=Point(1,judge:"UNMEASURED") with{OriginalMeasurement=null,HasAppliedOffsetEvidence=true};var target=Point(2,judge:"UNMEASURED") with{OriginalMeasurement=null};
            var w=Workspace([source,target],new(){[source.Key]=new(7,-3)});var p=w.PreviewReferenceCopy(source.Key,[target.Key]);True(p.Judge=="OK"&&p.ErrorXUm is null&&p.ReferenceValueKind=="APPLIED_OFFSET");
            w.ConfirmReferenceCopy(p,[target.Key],"known saved F");True(w.PreviewBulkCorrection([target.Key]).Keys.SequenceEqual([target.Key]));
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);True(restored.Context!.LineageValid);
            ManualSave(w,"STORED-F");Offset(w.ActiveOffsets[target.Key],7,-3);True(w.GetPoint(target.Key).OriginalMeasurement is null&&!w.GetPoint(target.Key).HasFiniteError&&w.PreviewBulkCorrection([target.Key]).Keys.Count==0);
        });
        Check("representative_saved_known_but_ng_error_init_or_unassigned_never_auto_ok",()=>
        {
            foreach(var judge in new[]{"NG","ERROR"})
            {
                var p=Point(1,judge:judge) with{Source="KEPT",HasAppliedOffsetEvidence=true};var w=Workspace([p,Point(2)],new(){[p.Key]=new(7,0)});
                True(w.PreviewReferenceCopy(p.Key,[Key(2)]).Judge==judge);
            }
            var head0=Point(1,judge:"UNMEASURED") with{HeadNo=0,Masked=true,HasAppliedOffsetEvidence=true};var a=Workspace([head0,Point(2)]);True(a.ResolveReferenceValue(head0.Key).Judge=="UNMEASURED");
        });
        Check("representative_numeric_exclusion_is_per_point_and_does_not_remove_field_or_shot_validation",()=>
        {
            var a=Point(1,1,0);var b=Point(2,31,0);var c=Point(3,1,0);var w=Workspace([a,b,c],new(){[c.Key]=new(200,0)});var p=w.PreviewBulkCorrection();True(p.Keys.SequenceEqual([a.Key])&&p.ExcludedNumeric==2);
            w.BuildBulkCandidate();True(w.ValidateCandidate(false).Ok);Apply(w,"NUMERIC-SUBSET");Offset(w.ActiveOffsets[b.Key],0,0);Offset(w.ActiveOffsets[c.Key],200,0);
            w=Workspace([a],evaluator:(_,_,_)=>new(false,"field protected"));w.BuildBulkCandidate();True(!w.ValidateCandidate(false).Ok);
        });
        Check("operator_ng_completion_preserves_raw_and_creates_separate_current_manual_result",()=>
        {
            var p=Point(1,4,-2,"NG");var w=Workspace([p],new(){[p.Key]=new(5,1)});var raw=p.OriginalMeasurement;w.RecordOperatorResults([p.Key],null,null,"operator verified surface");var after=w.GetPoint(p.Key);
            True(after.Judge=="OK"&&after.Source=="OPERATOR"&&after.OperatorResult is not null&&after.OriginalMeasurement==raw&&raw!.Judge=="NG"&&!after.IsDirectMeasurement);Offset(after.OperatorResult!.FinalOffset,9,-1);
            w.BuildBulkCandidate();ManualSave(w,"FORCED-NG");Offset(w.ActiveOffsets[p.Key],9,-1);True(w.GetPoint(p.Key).OriginalMeasurement==raw&&w.PreviewBulkCorrection().Keys.Count==0);
        });
        Check("operator_missing_numbers_require_explicit_pair_and_real_manual_zero_does_not_create_camera_raw",()=>
        {
            var p=Point(1,judge:"UNMEASURED") with{OriginalMeasurement=null,IsMeasurementOffsetInitialized=true};var w=Workspace([p]);Throws(()=>w.RecordOperatorResults([p.Key],null,null,"missing"));Throws(()=>w.RecordOperatorResults([p.Key],0,null,"partial"));
            Throws(()=>w.RecordOperatorResults([p.Key],double.NaN,0,"nan"));True(w.GetPoint(p.Key)==p);
            w.RecordOperatorResults([p.Key],0,0,"explicit zero");True(w.GetPoint(p.Key).Judge=="OK"&&!w.GetPoint(p.Key).IsMeasurementOffsetInitialized&&w.GetPoint(p.Key).OriginalMeasurement is null);
            w.BuildBulkCandidate();ManualSave(w,"MANUAL-ZERO");True(w.GetPoint(p.Key).HasAppliedOffsetEvidence&&!w.GetPoint(p.Key).IsDirectMeasurement);
        });
        Check("operator_multiple_targets_preflight_is_atomic_and_masked_targets_are_rejected",()=>
        {
            var a=Point(1);var missing=Point(2,judge:"UNMEASURED") with{OriginalMeasurement=null};var w=Workspace([a,missing]);Throws(()=>w.RecordOperatorResults([a.Key,missing.Key],null,null,"not partial"));True(w.GetPoint(a.Key)==a&&w.History.Count==0);
            w=Workspace([a with{Masked=true}]);Throws(()=>w.RecordOperatorResults([a.Key],1,2,"masked"));
        });
        Check("operator_unadopted_foreign_import_requires_explicit_operator_action_and_preserves_origin",()=>
        {
            var raw=Measurement(2,-1) with{RunId="OLD-RUN",CoordinateSignature="OLD-FRAME"};var p=Point(1,2,-1) with{Source="IMPORTED",OriginalMeasurement=raw};var w=Workspace([p],new(){[p.Key]=new(5,0)});
            True(w.PreviewBulkCorrection().Keys.Count==0);Throws(()=>w.ResolveReferenceValue(p.Key));w.RecordOperatorResults([p.Key],null,null,"operator accepts displayed imported numbers");var op=w.GetPoint(p.Key).OperatorResult!;
            True(op.RunId==Initial.RunId&&op.ObservedCoordinateSignature=="OLD-FRAME"&&op.OriginalSource=="IMPORTED"&&w.GetPoint(p.Key).OriginalMeasurement==raw);Offset(op.FinalOffset,7,-1);
            var f=new CManualInspectionWorkspace();f.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);True(f.Context!.LineageValid);f.BuildBulkCandidate();ManualSave(f,"IMPORTED-OPERATOR");
        });
        Check("operator_saved_absolute_base_can_be_acknowledged_without_fabricating_residual",()=>
        {
            var p=Point(1,judge:"UNMEASURED") with{HasAppliedOffsetEvidence=true,OriginalMeasurement=null};var w=Workspace([p],new(){[p.Key]=new(7,-3)});w.RecordOperatorResults([p.Key],null,null,"known B approved");
            True(w.GetPoint(p.Key).Judge=="OK"&&!w.GetPoint(p.Key).HasFiniteError&&w.GetPoint(p.Key).OperatorResult?.FinalOffset==new ST_MANUAL_OFFSET(7,-3)&&w.PreviewBulkCorrection().Keys.Count==0);
            var f=new CManualInspectionWorkspace();f.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);True(f.Context!.LineageValid);
        });
        Check("operator_consumed_raw_and_repeated_explicit_input_never_readds_old_residual",()=>
        {
            var w=Workspace([Point(1,3,0)]);w.BuildDirectCandidate([Key(1)]);True(w.ValidateCandidate(false).Ok);Apply(w,"RAW-SAVED");w.RecordOperatorResults([Key(1)],null,null,"ack old consumed raw");Offset(w.GetPoint(Key(1)).OperatorResult!.FinalOffset,3,0);True(w.PreviewBulkCorrection().Keys.Count==0);
            w.RecordOperatorResults([Key(1)],2,0,"new manual deviation");w.BuildBulkCandidate();ManualSave(w,"MANUAL-NEW");Offset(w.ActiveOffsets[Key(1)],5,0);w.RecordOperatorResults([Key(1)],2,0,"same typed values repeated");True(w.PreviewBulkCorrection().Keys.Count==0);Offset(w.GetPoint(Key(1)).OperatorResult!.FinalOffset,5,0);
        });
        Check("operator_result_new_measurement_and_init_clear_manual_metadata_but_keep_original_history",()=>
        {
            var w=Workspace();w.RecordOperatorResults([Key(1)],2,1,"manual");var op=w.GetPoint(Key(1)).OperatorResult;var raw=Measurement(1,1,raw:"NEW-ACTUAL");w.RecordMeasurement(Key(1),raw);True(w.GetPoint(Key(1)).OperatorResult is null&&w.GetPoint(Key(1)).OriginalMeasurement?.RawToken==raw.RawToken&&w.History.Last().Before.OperatorResult==op);
            w.RecordOperatorResults([Key(1)],2,1,"manual again");w.InitializeMeasurementOffsets([Key(1)]);True(w.ValidateCandidate(false).Ok);Apply(w,"INIT-AFTER-OPERATOR");True(w.GetPoint(Key(1)).OperatorResult is null&&w.GetPoint(Key(1)).OriginalMeasurement?.RawToken==raw.RawToken);
        });
        Check("operator_result_is_copyable_frozen_and_cannot_be_laundered_after_session_tamper",()=>
        {
            var w=Workspace();w.RecordOperatorResults([Key(1)],2,1,"manual");var copy=w.PreviewReferenceCopy(Key(1),[Key(2)]);w.ConfirmReferenceCopy(copy,[Key(2)],"copy operator final");True(w.GetPoint(Key(2)).ProxySource!.ReferenceValueKind=="OPERATOR");
            var session=w.CaptureSession();var p=session.Points.Single(p=>p.Key==Key(1));var bad=session with{Points=session.Points.Select(x=>x.Key==p.Key?p with{OperatorResult=p.OperatorResult! with{FinalOffset=new(99,1)}}:x).ToArray()};
            var f=new CManualInspectionWorkspace();f.RestoreSession(bad,Initial,w.Points,w.ActiveOffsets,InsideField);True(!f.Context!.LineageValid);Throws(()=>f.BuildBulkCandidate());
        });
        Check("operator_shared_shot_conflict_remains_atomic_and_no_field_guard_is_bypassed",()=>
        {
            var a=Point(1);var b=Point(1,beam:12);var w=Workspace([a,b]);w.RecordOperatorResults([a.Key],1,0,"one");w.RecordOperatorResults([b.Key],2,0,"other");w.BuildBulkCandidate();True(!w.ValidateCandidate(true).Ok);Throws(()=>w.CreateApplySnapshot(true));
        });
        Check("operator_session_value_evidence_is_bound_to_the_exact_subject_point",()=>
        {
            var w=Workspace(values:new(){[Key(1)]=new(20,0)});w.RecordOperatorResults([Key(1)],2,0,"subject one");var session=w.CaptureSession();var source=w.GetPoint(Key(1));
            var moved=session with{Points=session.Points.Select(p=>p.Key==Key(2)?p with{Judge="OK",Source="OPERATOR",ErrorXUm=2,ErrorYUm=0,OperatorResult=source.OperatorResult}:p).ToArray()};
            var f=new CManualInspectionWorkspace();f.RestoreSession(moved,Initial,w.Points,w.ActiveOffsets,InsideField);True(!f.Context!.LineageValid);Throws(()=>f.BuildBulkCandidate());
        });
        Check("representative_numeric_exclusion_checks_both_original_and_file_rounded_limits",()=>
        {
            var a=Point(1,30.0004,0);var b=Point(2,30,0);var c=Point(3,1,0);var d=Point(4,1,0);
            var w=Workspace([a,b,c,d],new(){[b.Key]=new(-.0004,0),[c.Key]=new(199.0004,0)});var preview=w.PreviewBulkCorrection();
            True(preview.ExcludedNumeric==3&&preview.Keys.SequenceEqual([d.Key]));w.BuildBulkCandidate();True(w.ValidateCandidate(false).Ok);
        });
        Check("representative_finite_copy_after_empty_copy_preserves_foreign_original_provenance",()=>
        {
            var raw=Measurement(1,2) with{RunId="FOREIGN-RUN",CoordinateSignature="FOREIGN-FRAME"};
            var empty=Point(1,judge:"UNMEASURED") with{OriginalMeasurement=null};var target=Point(2,1,2) with{Source="IMPORTED",OriginalMeasurement=raw};var source=Point(3);
            var w=Workspace([empty,target,source]);w.ConfirmReferenceStateCopy(w.PreviewReferenceStateCopy(empty.Key,[target.Key]),[target.Key],"clear state");
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(source.Key,[target.Key]),[target.Key],"new finite representative");True(w.GetPoint(target.Key).ProxySource!.OriginalSource=="IMPORTED");
            var f=new CManualInspectionWorkspace();f.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);True(f.Context!.LineageValid&&f.GetPoint(target.Key).OriginalMeasurement==raw);ManualSave(f,"COPY-AFTER-STATE");
        });
    }
}
