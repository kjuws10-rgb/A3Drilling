using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunReferenceValueChecks()
    {
        Check("reference_missing_zero_and_initialized_historic_raw_have_no_copy_value",()=>
        {
            var missing=Point(1,judge:"UNMEASURED") with { OriginalMeasurement=null };
            var w=Workspace([missing,Point(2)]);Throws(()=>w.ResolveReferenceFinal(missing.Key));
            var initialized=Point(1) with { IsMeasurementOffsetInitialized=true,HasAppliedOffsetEvidence=true };
            w=Workspace([initialized,Point(2)]);Throws(()=>w.PreviewReferenceCopy(initialized.Key,[Key(2)]));
        });
        Check("reference_numeric_base_requires_authoritative_stored_row_evidence",()=>
        {
            var source=Point(1,judge:"UNMEASURED") with { OriginalMeasurement=null };
            var w=Workspace([source,Point(2)],new(){[source.Key]=new(7,-2)});Throws(()=>w.ResolveReferenceFinal(source.Key));
            w=Workspace([source with { HasAppliedOffsetEvidence=true },Point(2)],new(){[source.Key]=new(7,-2)});
            var preview=w.PreviewReferenceCopy(source.Key,[Key(2)]);Offset(preview.Final,7,-2);
            True(preview.Judge=="UNMEASURED"&&preview.ErrorXUm is null&&preview.ReferenceValueKind=="APPLIED_OFFSET");
        });
        Check("reference_stored_zero_is_copyable_without_inventing_ok_raw_or_zero_precision",()=>
        {
            var source=Point(1,judge:"UNMEASURED") with { OriginalMeasurement=null,HasAppliedOffsetEvidence=true };
            var target=Point(2,judge:"UNMEASURED") with { OriginalMeasurement=null };var w=Workspace([source,target]);
            var preview=w.PreviewReferenceCopy(source.Key,[target.Key]);w.ConfirmReferenceCopy(preview,[target.Key],"stored zero snapshot");
            True(w.GetPoint(target.Key).Judge=="UNMEASURED"&&w.GetPoint(target.Key).ErrorXUm is null
                &&w.GetPoint(target.Key).OriginalMeasurement is null&&w.GetPoint(target.Key).ProxySource?.ReferenceValueKind=="APPLIED_OFFSET");
            True(!w.ValidateCandidate(true).Ok);Offset(w.Candidate!.Rows.Single().Final,0,0);
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);
            True(restored.Context!.LineageValid&&restored.GetPoint(target.Key).Judge=="UNMEASURED");
        });
        Check("reference_invalid_candidate_validation_does_not_disqualify_valid_raw_value",()=>
        {
            var w=Workspace([Point(1,35,0),Point(2)]);w.BuildDirectCandidate([Key(1)]);True(!w.ValidateCandidate(false).Ok);
            Offset(w.PreviewReferenceCopy(Key(1),[Key(2)]).Final,35,0);True(!w.ValidateCandidate(false).Ok);
        });
        Check("reference_unadopted_import_mode_or_frame_never_becomes_manual_ok_anchor",()=>
        {
            foreach(var imported in new[]{Point(1) with { Source="IMPORTED",AdoptedFileMeasurement=null },
                Point(1) with { Source="IMPORTED",OriginalMeasurement=null },
                Point(1) with { OriginalMeasurement=Point(1).OriginalMeasurement! with { CoordinateSignature="OTHER-FRAME" } }})
            {
                var w=Workspace([imported,Point(2)]);Throws(()=>w.PreviewReferenceCopy(imported.Key,[Key(2)]));
                True(w.GetPoint(Key(2)).Source=="MEASURED"&&w.Candidate is null);
            }
        });
        Check("reference_masked_point_can_supply_valid_value_but_cannot_be_a_device_target",()=>
        {
            var masked=Point(1) with { Masked=true };var w=Workspace([masked,Point(2)]);
            Offset(w.PreviewReferenceCopy(masked.Key,[Key(2)]).Final,3,-2);
            Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[masked.Key]));
            Throws(()=>w.RecordMeasurement(masked.Key,Measurement(1,1)));
        });
        Check("unassigned_head_has_no_camera_reference_but_explicit_design_base_remains_unknown",()=>
        {
            var unassigned=Point(1) with { HeadNo=0,Masked=true };var w=Workspace([unassigned,Point(2)]);
            Throws(()=>w.PreviewReferenceCopy(unassigned.Key,[Key(2)]));
            w=Workspace([unassigned with { HasAppliedOffsetEvidence=true },Point(2)],new(){[unassigned.Key]=new(7,-2)});
            var preview=w.PreviewReferenceCopy(unassigned.Key,[Key(2)]);Offset(preview.Final,7,-2);
            True(preview.Judge=="UNMEASURED"&&preview.ErrorXUm is null);w.ConfirmReferenceCopy(preview,[Key(2)],"known design base only");
            True(!w.ValidateCandidate(true).Ok);
        });
        Check("proxy_reference_is_flattened_and_keeps_frozen_final_after_root_remeasurement",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"first copy");
            var captured=w.GetPoint(Key(2)).ProxySource!;w.RecordMeasurement(Key(1),Measurement(9,7,raw:"ROOT-NEW"));
            var second=w.PreviewReferenceCopy(Key(2),[Key(3)]);w.ConfirmReferenceCopy(second,[Key(3)],"copy snapshot again");
            var copied=w.GetPoint(Key(3)).ProxySource!;
            Offset(copied.FinalOffset,3,-2);True(copied.ReferenceKey==Key(1)&&copied.CopiedFromKey==Key(2)
                &&copied.ReferenceMeasurementToken==captured.ReferenceMeasurementToken&&copied.ReferenceValueProof.Length>0);
        });
        Check("proxy_reference_ng_state_and_nullable_target_original_are_preserved",()=>
        {
            var w=Workspace([Point(1,2,-3,"NG"),Point(2),Point(3,judge:"UNMEASURED") with { OriginalMeasurement=null }]);
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"NG copy");
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(2),[Key(3)]),[Key(3)],"NG snapshot copy");
            True(w.GetPoint(Key(3)).Judge=="NG"&&w.GetPoint(Key(3)).ErrorXUm==2&&w.GetPoint(Key(3)).OriginalMeasurement is null);
            True(!w.ValidateCandidate(true).Ok);
        });
        Check("proxy_root_target_or_self_rejects_before_any_other_target_is_changed",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"first copy");
            var before=w.GetPoint(Key(3));var candidate=w.Candidate;
            Throws(()=>w.PreviewReferenceCopy(Key(2),[Key(1),Key(3)]));Throws(()=>w.PreviewReferenceCopy(Key(2),[Key(2)]));
            True(w.GetPoint(Key(3))==before&&w.Candidate==candidate);
        });
        Check("proxy_reverse_historical_copy_is_allowed_and_undo_restores_frozen_metadata",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"A to B");
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(2),[Key(3)]),[Key(3)],"B to C");var before=w.GetPoint(Key(2));
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(3),[Key(2)]),[Key(2)],"C back to B snapshot");
            var after=w.GetPoint(Key(2));Offset(after.ProxySource!.FinalOffset,3,-2);
            True(after.ProxySource.ReferenceKey==Key(1)&&after.ProxySource.CopiedFromKey==Key(3));
            w.UndoReferenceCopy();True(w.GetPoint(Key(2))==before);
        });
        Check("proxy_reverse_copy_session_and_apply_do_not_add_frozen_final_twice",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"A B");
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(2),[Key(3)]),[Key(3)],"B C");
            w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(3),[Key(2)]),[Key(2)],"C B");
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),Initial,w.Points,w.ActiveOffsets,InsideField);
            True(restored.Context!.LineageValid);restored.BuildDirectCandidate([Key(2)]);True(restored.ValidateCandidate(true).Ok);
            var snapshot=restored.CreateApplySnapshot(true);restored.CommitApplied(snapshot,"REF-APPLY","apply frozen F");
            Offset(restored.ActiveOffsets[Key(2)],3,-2);True(!restored.PreviewBulkCorrection().Keys.Contains(Key(2)));
        });
        Check("proxy_session_final_tampering_cannot_be_laundered_as_a_new_reference",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"copy");var saved=w.CaptureSession();
            saved=saved with { Points=saved.Points.Select(p=>p.Key==Key(2)?p with { ProxySource=p.ProxySource! with { FinalOffset=new(80,90) } }:p).ToArray(),
                Candidate=saved.Candidate! with { Rows=saved.Candidate.Rows.Select(r=>r.Key==Key(2)?r with { Final=new(80,90),Delta=new(80,90) }:r).ToArray() } };
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(saved,Initial,w.Points,w.ActiveOffsets,InsideField);
            True(!restored.Context!.LineageValid);Throws(()=>restored.PreviewReferenceCopy(Key(2),[Key(3)]));
        });
        Check("proxy_session_unknown_value_kind_or_root_cycle_invalidates_lineage",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"copy");var saved=w.CaptureSession();
            foreach(var change in new Func<ST_MANUAL_PROXY,ST_MANUAL_PROXY>[] {p=>p with { ReferenceValueKind="UNPROVEN" },p=>p with { ReferenceKey=Key(2) }})
            {
                var tampered=saved with { Points=saved.Points.Select(p=>p.Key==Key(2)?p with { ProxySource=change(p.ProxySource!) }:p).ToArray() };
                var restored=new CManualInspectionWorkspace();restored.RestoreSession(tampered,Initial,w.Points,w.ActiveOffsets,InsideField);True(!restored.Context!.LineageValid);
            }
        });
        Check("session_claimed_base_evidence_is_replaced_by_the_real_expected_source",()=>
        {
            var points=new[]{Point(1,judge:"UNMEASURED") with { OriginalMeasurement=null },Point(2)};var w=Workspace(points);
            var saved=w.CaptureSession() with { Points=w.Points.Select(p=>p with { HasAppliedOffsetEvidence=true }).ToArray() };
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(saved,Initial,points,w.ActiveOffsets,InsideField);
            True(!restored.GetPoint(Key(1)).HasAppliedOffsetEvidence);Throws(()=>restored.ResolveReferenceFinal(Key(1)));
            var actual=points.Select(p=>p with { HasAppliedOffsetEvidence=true }).ToArray();
            restored.RestoreSession(w.CaptureSession(),Initial,actual,w.ActiveOffsets,InsideField);Offset(restored.ResolveReferenceFinal(Key(1)),0,0);
        });
        Check("legacy_proxy_metadata_can_be_referenced_only_with_matching_root_or_real_stored_base",()=>
        {
            var w=Workspace();w.ConfirmReferenceCopy(w.PreviewReferenceCopy(Key(1),[Key(2)]),[Key(2)],"old format");var saved=w.CaptureSession();
            saved=saved with { Points=saved.Points.Select(p=>p.ProxySource is null?p:p with { ProxySource=p.ProxySource with
                { CopiedFromKey="",ReferenceValueKind="",ReferenceValueSource="",BasisRevisionId="",ReferenceValueProof="" } }).ToArray() };
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(saved,Initial,w.Points,w.ActiveOffsets,InsideField);
            True(restored.Context!.LineageValid);Offset(restored.PreviewReferenceCopy(Key(2),[Key(3)]).Final,3,-2);
            restored.RecordMeasurement(Key(1),Measurement(12,9,raw:"CHANGED"));Throws(()=>restored.PreviewReferenceCopy(Key(2),[Key(3)]));
        });
        Check("saved_measurement_init_undo_redo_keep_authoritative_base_evidence_separate",()=>
        {
            var point=Point(1) with { IsMeasurementOffsetInitialized=true,Judge="UNMEASURED",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null };
            var w=Workspace([point]);w.RecordMeasurement(point.Key,Measurement(0,0,raw:"TRUE-ZERO"));w.BuildDirectCandidate([point.Key]);True(w.ValidateCandidate(false).Ok);Apply(w,"REF-ZERO");
            True(w.GetPoint(point.Key).HasAppliedOffsetEvidence);var undo=w.CreateRestorePreviousSnapshot();w.CommitRestored(undo,"REF-UNDO","restore empty");
            True(!w.GetPoint(point.Key).HasAppliedOffsetEvidence);var redo=w.CreateRestoreNextSnapshot();w.CommitRestored(redo,"REF-REDO","restore real zero");
            True(w.GetPoint(point.Key).HasAppliedOffsetEvidence&&!w.GetPoint(point.Key).IsMeasurementOffsetInitialized);
        });
    }
}
