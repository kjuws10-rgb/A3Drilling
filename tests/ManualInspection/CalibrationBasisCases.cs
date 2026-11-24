using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunCalibrationBasisChecks()
    {
        static CManualInspectionWorkspace Known(double processed,double current,double residual,double scale=1,bool raw=true)
        {
            var p=Point(1,residual,0);var basis=ZeroBasis(1,1,1) with{BaseOffsetUm=new(processed,0),ReviewOffsetScale=scale};
            p=p with{OriginalMeasurement=raw?p.OriginalMeasurement! with{CalibrationBasis=basis}:null,
                Judge=raw?"OK":"UNMEASURED",Source=raw?"MEASURED":"KEPT",ErrorXUm=raw?residual:null,ErrorYUm=raw?0:null};
            var w=new CManualInspectionWorkspace();w.Load(Initial,[p],new Dictionary<string,ST_MANUAL_OFFSET>{{p.Key,new(current,0)}},DefaultLimits,InsideField,
                calibrationBases:new Dictionary<string,ST_MANUAL_CALIBRATION_BASIS>{{p.OffsetKey,basis}});return w;
        }
        static void Save(CManualInspectionWorkspace w,string id,bool manual=false)
        {True(w.ValidateCandidate(manual).Ok);var s=w.CreateApplySnapshot(manual);w.CommitApplied(s,id,"synthetic basis calibration");}
        Check("calibration_uses_actual_processing_base_not_latest_saved_csv",()=>{var w=Known(100,20,2);Offset(w.ResolveMeasurementReferenceValue(Key(1)).Final,102,0);w.BuildMeasurementReplacementCandidate([Key(1)]);Save(w,"CAL-102");Offset(w.ActiveOffsets[Key(1)],102,0);});
        Check("calibration_scale_half_one_double_inverse_residual",()=>{foreach(var scale in new[]{.5,1d,2d}){var w=Known(10,20,2,scale);Offset(w.ResolveMeasurementReferenceValue(Key(1)).Final,10+2/scale,0);}});
        Check("calibration_known_initial_zero_is_valid_first_process_evidence",()=>{var w=Known(0,0,2);w.BuildMeasurementReplacementCandidate([Key(1)]);Save(w,"FIRST-CAL");Offset(w.ActiveOffsets[Key(1)],2,0);});
        Check("calibration_unknown_or_serialized_only_basis_never_authorizes_raw",()=>
        {
            var w=Known(10,20,2);var unknown=new CManualInspectionWorkspace();unknown.Load(Initial,w.Points,w.ActiveOffsets,DefaultLimits,InsideField);
            Throws(()=>unknown.ResolveMeasurementReferenceValue(Key(1)));unknown.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);
            True(unknown.PreviewMeasurementReplacement().Keys.Count==0);unknown.RecordMeasurement(Key(1),Measurement(1,0));True(unknown.GetPoint(Key(1)).ErrorXUm==1&&unknown.PreviewMeasurementReplacement().Keys.Count==0);
        });
        Check("calibration_zero_or_changed_scale_blocks_only_automatic_correction",()=>
        {
            var zero=Known(10,20,2,0);Throws(()=>zero.ResolveMeasurementReferenceValue(Key(1)));zero.RecordMeasurementOperatorResults([Key(1)],12,0,"explicit final");Offset(zero.ResolveMeasurementReferenceValue(Key(1)).Final,12,0);
            var w=Known(10,20,2);var changed=w.CalibrationBases.ToDictionary(x=>x.Key,x=>x.Value with{ReviewOffsetScale=.5});
            w.ConfirmEditableMeasurementContext(Initial,calibrationBases:changed);Throws(()=>w.ResolveMeasurementReferenceValue(Key(1)));
        });
        Check("calibration_packet_point_head_date_or_basis_tamper_is_rejected",()=>
        {
            foreach(var mutate in new Func<ST_MANUAL_CALIBRATION_BASIS,ST_MANUAL_CALIBRATION_BASIS>[] {b=>b with{BaseOffsetUm=new(11,0)},b=>b with{HeadNo=2},b=>b with{CompletedAt=DateTimeOffset.MaxValue}})
            {var w=Known(10,20,2);var session=w.CaptureSession();var p=session.Points.Single();var changed=p with{OriginalMeasurement=p.OriginalMeasurement! with{CalibrationBasis=mutate(p.OriginalMeasurement.CalibrationBasis!)}};
                var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(session with{Points=[changed]},Initial,w.Points,w.ActiveOffsets,InsideField,calibrationBases:w.CalibrationBases);Throws(()=>fresh.ResolveMeasurementReferenceValue(Key(1)));}
        });
        Check("calibration_residual_limit_uses_inverse_scale_not_old_csv_difference",()=>
        {var w=Known(100,-500,15,.5);w.BuildMeasurementReplacementCandidate([Key(1)]);Save(w,"LIMIT30");Offset(w.ActiveOffsets[Key(1)],130,0);w=Known(100,0,15.001,.5);True(w.PreviewMeasurementReplacement().ExcludedNumeric==1);});
        Check("calibration_final_total_and_field_limits_still_fail_closed",()=>
        {var w=Known(199,0,2);True(w.PreviewMeasurementReplacement().ExcludedNumeric==1);w=Known(10,0,2);w.Load(Initial,w.Points,w.ActiveOffsets,DefaultLimits,(_,_,_)=>new(false,"field"),calibrationBases:w.CalibrationBases);w.BuildMeasurementReplacementCandidate([Key(1)]);True(!w.ValidateCandidate(false).Ok);});
        Check("absolute_review_input_preserves_own_residual_and_accepts_large_absolute_not_delta",()=>
        {var w=Known(100,20,2);var raw=w.GetPoint(Key(1)).OriginalMeasurement;w.RecordMeasurementOperatorResults([Key(1)],102,0,"absolute F");True(w.GetPoint(Key(1)).ErrorXUm==2&&w.GetPoint(Key(1)).OriginalMeasurement==raw);w.BuildMeasurementReplacementCandidate([Key(1)]);Save(w,"ABS102",true);Offset(w.ActiveOffsets[Key(1)],102,0);True(w.PreviewMeasurementReplacement().Keys.Count==0);});
        Check("absolute_review_without_raw_never_fabricates_measured_zero",()=>
        {var w=Known(0,0,0,raw:false);w.RecordMeasurementOperatorResults([Key(1)],12,0,"manual absolute");True(w.GetPoint(Key(1)).OriginalMeasurement is null&&!w.GetPoint(Key(1)).HasFiniteError);w.BuildMeasurementReplacementCandidate([Key(1)]);Save(w,"ABS12",true);Offset(w.ActiveOffsets[Key(1)],12,0);});
        Check("absolute_reference_copy_preserves_target_raw_and_replaces_target_base_once",()=>
        {
            var source=Known(100,20,2);var a=source.GetPoint(Key(1));var b=Point(2,7,1);var w=new CManualInspectionWorkspace();
            w.Load(Initial,[a,b],new Dictionary<string,ST_MANUAL_OFFSET>{{a.Key,new(20,0)},{b.Key,new(50,0)}},DefaultLimits,InsideField,calibrationBases:source.CalibrationBases);
            var preview=w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]);Offset(preview.Final,102,0);w.ConfirmMeasurementReferenceCopy(preview,[b.Key],"absolute recommendation");
            True(w.GetPoint(b.Key).ErrorXUm==7&&w.GetPoint(b.Key).OriginalMeasurement==b.OriginalMeasurement&&w.Candidate is null);
            var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField,calibrationBases:w.CalibrationBases);w=fresh;
            w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"COPY102",true);Offset(w.ActiveOffsets[b.Key],102,0);True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);
        });
        Check("legacy_replacement_candidate_is_not_reapproved_under_new_physics",()=>
        {var w=Known(100,20,2);w.BuildMeasurementReplacementCandidate([Key(1)]);var s=w.CaptureSession();var old=s.Candidate! with{CalibrationPolicyVersion=0};w.RestoreSession(s with{Candidate=old},Initial,w.Points,w.ActiveOffsets,InsideField,calibrationBases:w.CalibrationBases);True(!w.ValidateCandidate(false).Ok);});
        Check("calibration_ng_stays_excluded_and_explicit_absolute_preserves_original_ng",()=>
        {var w=Known(10,0,2);w.RecordMeasurement(Key(1),Measurement(2,0,"NG"));True(w.PreviewMeasurementReplacement().ExcludedNg==1);w.RecordMeasurementOperatorResults([Key(1)],12,0,"operator final decision");True(w.GetPoint(Key(1)).OriginalMeasurement!.Judge=="NG");w.BuildMeasurementReplacementCandidate([Key(1)]);Save(w,"NG-EXPLICIT",true);});
        Check("calibration_mixed_or_unknown_mode_cannot_backfill_trusted_process_basis",()=>
        {
            foreach(var modes in new (bool?,bool?)[]{(false,true),(true,false),(null,false),(false,null)})
            {var w=Known(10,0,2);var raw=Measurement(2,0) with{IsStageSimulation=modes.Item1,IsVisionSimulation=modes.Item2};w.RecordMeasurement(Key(1),raw);True(w.GetPoint(Key(1)).OriginalMeasurement!.CalibrationBasis is null);Throws(()=>w.ResolveMeasurementReferenceValue(Key(1)));}
            var blocked=Known(10,0,2);blocked.RecordMeasurement(Key(1),Measurement(2,0) with{CalibrationBasisUnavailableReason="actual-mode-mismatch"});True(blocked.GetPoint(Key(1)).OriginalMeasurement!.CalibrationBasis is null);Throws(()=>blocked.ResolveMeasurementReferenceValue(Key(1)));
        });
        Check("absolute_proxy_proof_is_bound_to_target_point_not_reusable_on_another_point",()=>
        {
            var a=Point(1);var b=Point(2);var c=Point(3);var w=Workspace([a,b,c]);w.ConfirmMeasurementReferenceCopy(w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]),[b.Key],"target binding");
            var session=w.CaptureSession();var copied=w.GetPoint(b.Key);var moved=session with{Points=session.Points.Select(p=>p.Key==c.Key?p with{Source="MANUAL",Judge=copied.Judge,ProxySource=copied.ProxySource}:p).ToArray()};
            var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(moved,Initial,w.Points,w.ActiveOffsets,InsideField,calibrationBases:w.CalibrationBases);True(!fresh.Context!.LineageValid);Throws(()=>fresh.ResolveMeasurementReferenceValue(c.Key));
        });
        Check("absolute_operator_and_proxy_without_camera_residual_are_numeric_not_empty_state_references",()=>
        {
            var a=Point(1,judge:"UNMEASURED") with{OriginalMeasurement=null};var b=Point(2,judge:"UNMEASURED") with{OriginalMeasurement=null};var c=Point(3,judge:"UNMEASURED") with{OriginalMeasurement=null};var w=Workspace([a,b,c]);
            w.RecordMeasurementOperatorResults([a.Key],12,0,"absolute no raw");True(!w.CanCopyMeasurementReferenceState(a.Key));w.ConfirmMeasurementReferenceCopy(w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]),[b.Key],"copy abs");
            True(!w.CanCopyMeasurementReferenceState(b.Key)&&w.GetPoint(b.Key).ErrorXUm is null);w.ConfirmMeasurementReferenceCopy(w.PreviewMeasurementReferenceCopy(b.Key,[c.Key]),[c.Key],"proxy abs");Offset(w.ResolveMeasurementReferenceValue(c.Key).Final,12,0);
        });
    }
}
