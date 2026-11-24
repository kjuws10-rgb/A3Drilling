using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunReplacementWorkflowChecks()
    {
        static void Commit(CManualInspectionWorkspace w,string id,bool manual=false)
        {True(w.ValidateCandidate(manual).Ok);var snapshot=w.CreateApplySnapshot(manual);w.CommitApplied(snapshot,id,"synthetic Manual replacement");}
        Check("manual_replacement_stores_current_measurement_not_base_plus_measurement",()=>
        {
            var p=Point(1,3,1);var w=Workspace([p],new(){[p.Key]=new(20,5)});var c=w.BuildMeasurementReplacementCandidate([p.Key]);
            True(c.IsMeasurementReplacement&&!c.IsOffsetReplacement);Offset(c.Rows.Single().Final,3,1);Offset(c.Rows.Single().Delta,-17,-4);
            Commit(w,"REPLACE-1");Offset(w.ActiveOffsets[p.Key],3,1);True(w.GetPoint(p.Key).OriginalMeasurement==p.OriginalMeasurement&&w.PreviewMeasurementReplacement().Keys.Count==0);
        });
        Check("manual_replacement_discards_large_old_base_but_keeps_input_limit",()=>
        {
            var p=Point(1,5,1);var w=Workspace([p],new(){[p.Key]=new(500,-700)});True(w.PreviewMeasurementReplacement().Keys.SequenceEqual([p.Key]));
            w.BuildMeasurementReplacementCandidate([p.Key]);Commit(w,"LARGE-BASE");Offset(w.ActiveOffsets[p.Key],5,1);
            foreach(var x in new[]{30.0006,201d}){var bad=Workspace([Point(1,x,0)]);True(bad.PreviewMeasurementReplacement().ExcludedNumeric==1);bad.BuildMeasurementReplacementCandidate([Key(1)]);True(!bad.ValidateCandidate(false).Ok);}
        });
        Check("manual_measurement_reference_stages_error_xy_only_and_defers_file_commit",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});
            var copy=w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]);Offset(copy.Final,3,1);w.ConfirmMeasurementReferenceCopy(copy,[b.Key],"measurement XY only");
            True(w.Candidate is null&&w.CanUndoReferenceCopy&&w.UndoCount==0);Offset(w.ActiveOffsets[b.Key],20,5);Offset(w.GetPoint(b.Key).ProxySource!.FinalOffset,3,1);
            w.BuildMeasurementReplacementCandidate([b.Key]);Commit(w,"COPY-BULK",true);Offset(w.ActiveOffsets[b.Key],3,1);True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);
        });
        Check("legacy_proxy_and_operator_final_do_not_leak_old_base_into_measurement_policy",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2)});
            var old=w.PreviewReferenceCopy(a.Key,[b.Key]);w.ConfirmReferenceCopy(old,[b.Key],"legacy final");Offset(w.GetPoint(b.Key).ProxySource!.FinalOffset,13,-1);
            Throws(()=>w.ResolveMeasurementReferenceValue(b.Key));
            w.RecordOperatorResults([a.Key],3,1,"legacy operator");Offset(w.GetPoint(a.Key).OperatorResult!.FinalOffset,13,-1);Throws(()=>w.ResolveMeasurementReferenceValue(a.Key));
        });
        Check("saved_base_only_has_no_current_measurement_reference_and_copies_empty_state",()=>
        {
            var a=Point(1,judge:"UNMEASURED") with{OriginalMeasurement=null,HasAppliedOffsetEvidence=true};var b=Point(2);
            var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});Throws(()=>w.ResolveMeasurementReferenceValue(a.Key));True(w.CanCopyMeasurementReferenceState(a.Key));
            var copy=w.PreviewMeasurementReferenceStateCopy(a.Key,[b.Key]);w.ConfirmReferenceStateCopy(copy,[b.Key],"no residual invented");
            True(w.GetPoint(b.Key).IsMeasurementStateCleared&&w.GetPoint(b.Key).ErrorXUm is null&&w.GetPoint(b.Key).Judge=="UNMEASURED");Offset(w.ActiveOffsets[b.Key],20,5);True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);
        });
        Check("replacement_policy_flag_cannot_reapprove_legacy_accumulation_candidate",()=>
        {
            var p=Point(1,3,1);var w=Workspace([p],new(){[p.Key]=new(10,-2)});w.BuildDirectCandidate([p.Key]);
            var s=w.CaptureSession();s=s with{Candidate=s.Candidate! with{IsMeasurementReplacement=true}};var loaded=Workspace([p],new(){[p.Key]=new(10,-2)});
            loaded.RestoreSession(s,Initial,loaded.Points,loaded.ActiveOffsets,InsideField);True(!loaded.ValidateCandidate(false).Ok);
            w.BuildMeasurementReplacementCandidate([p.Key]);s=w.CaptureSession();s=s with{Candidate=s.Candidate! with{IsMeasurementReplacement=false}};
            loaded.RestoreSession(s,Initial,loaded.Points,loaded.ActiveOffsets,InsideField);True(!loaded.ValidateCandidate(false).Ok);
        });
        Check("replacement_roundtrip_preserves_policy_and_zero_raw_duplicate_guard",()=>
        {
            var p=Point(1,0,0);var w=Workspace([p]);w.BuildMeasurementReplacementCandidate([p.Key]);var fresh=Workspace([p]);
            fresh.RestoreSession(w.CaptureSession(),Initial,fresh.Points,fresh.ActiveOffsets,InsideField,calibrationBases:fresh.CalibrationBases);True(fresh.Candidate!.IsMeasurementReplacement);Commit(fresh,"ZERO-REPLACE");
            True(fresh.PreviewMeasurementReplacement().Keys.Count==0);fresh.BuildMeasurementReplacementCandidate([p.Key]);True(!fresh.ValidateCandidate(false).Ok);
        });
        Check("scoped_import_retains_other_cell_values_init_state_raw_and_bad_existing_field",()=>
        {
            var a=Point(1,3,1);var b=Point(1,cell:2);var w=Workspace([a,b],new(){[a.Key]=new(5,0),[b.Key]=new(500,-700)},evaluator:(p,v,_)=>new(p.CellNo==1,"outside legacy field"));
            var imported=w.ActiveOffsets.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(7,2));var c=w.BuildScopedOffsetReplacementCandidate(1,imported,new HashSet<string>(),"current Cell import");
            True(c.ReplacementCellNo==1&&c.Rows.Single(r=>r.Key==b.Key).Source=="OFFSET_KEEP");Commit(w,"CELL-IMPORT");
            Offset(w.ActiveOffsets[a.Key],7,2);Offset(w.ActiveOffsets[b.Key],500,-700);True(w.GetPoint(b.Key)==b&&w.PreviewMeasurementReplacement([b.Key]).Keys.Count==1);
        });
        Check("scoped_init_is_value_absence_and_outside_measured_zero_stays_measured",()=>
        {
            var a=Point(1);var b=Point(1,cell:2,x:0,y:0) with{HasAppliedOffsetEvidence=true};var w=Workspace([a,b],new(){[a.Key]=new(500,-700),[b.Key]=ST_MANUAL_OFFSET.Zero});
            w.BuildScopedOffsetReplacementCandidate(1,w.ActiveOffsets,new HashSet<string>(),"current Cell Init",true);Commit(w,"CELL-INIT");
            True(w.GetPoint(a.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(b.Key).IsMeasurementOffsetInitialized&&w.GetPoint(b.Key).HasFiniteError&&w.GetPoint(b.Key).Judge=="OK");
            True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0&&w.PreviewMeasurementReplacement([b.Key]).ExcludedUnchanged==1);Offset(w.ActiveOffsets[b.Key],0,0);
        });
        Check("scoped_import_cannot_drop_its_scope_flag_or_forge_outside_keep",()=>
        {
            var a=Point(1);var b=Point(1,cell:2);var w=Workspace([a,b]);w.BuildScopedOffsetReplacementCandidate(1,w.ActiveOffsets,new HashSet<string>(),"scoped");
            var session=w.CaptureSession();var fresh=Workspace([a,b]);
            fresh.RestoreSession(session with{Candidate=session.Candidate! with{ReplacementCellNo=null}},Initial,fresh.Points,fresh.ActiveOffsets,InsideField);True(!fresh.ValidateCandidate(false).Ok);
            var corrupt=session.Candidate! with{Rows=session.Candidate!.Rows.Select(r=>r.CellNo==2?r with{Final=new(1,1),Delta=new(1,1)}:r).ToArray()};
            fresh.RestoreSession(session with{Candidate=corrupt},Initial,fresh.Points,fresh.ActiveOffsets,InsideField);True(!fresh.ValidateCandidate(false).Ok);
        });
        Check("scoped_absolute_file_replaces_large_old_base_without_using_removal_as_a_new_measurement_delta",()=>
        {
            var a=Point(1);var w=Workspace([a],new(){[a.Key]=new(500,-700)});var values=new Dictionary<string,ST_MANUAL_OFFSET>{{a.Key,new(5,1)}};
            w.BuildScopedOffsetReplacementCandidate(1,values,new HashSet<string>(),"explicit absolute file");Commit(w,"FILE-SMALL");Offset(w.ActiveOffsets[a.Key],5,1);
            w.BuildScopedOffsetReplacementCandidate(1,new Dictionary<string,ST_MANUAL_OFFSET>{{a.Key,new(201,0)}},new HashSet<string>(),"file total guard");True(!w.ValidateCandidate(false).Ok);
        });
        Check("manual_replacement_field_and_shared_shot_conflicts_remain_atomic_guards",()=>
        {
            var a=Point(1);var sibling=Point(1,beam:12,x:9);var w=Workspace([a,sibling]);w.BuildMeasurementReplacementCandidate([a.Key,sibling.Key]);True(!w.ValidateCandidate(false).Ok);
            w=Workspace([a,sibling],evaluator:(p,_,_)=>new(p.BeamNo!=12,"sibling outside field"));w.BuildMeasurementReplacementCandidate([a.Key]);True(!w.ValidateCandidate(false).Ok&&w.UndoCount==0);
        });
        Check("manual_replacement_undo_redo_restores_exact_before_and_after_values",()=>
        {
            var p=Point(1,3,1);var w=Workspace([p],new(){[p.Key]=new(20,5)});w.BuildMeasurementReplacementCandidate([p.Key]);Commit(w,"FIRST");
            var undo=w.CreateRestorePreviousSnapshot();w.CommitRestored(undo,"UNDO","undo");Offset(w.ActiveOffsets[p.Key],20,5);
            var redo=w.CreateRestoreNextSnapshot();w.CommitRestored(redo,"REDO","redo");Offset(w.ActiveOffsets[p.Key],3,1);
        });
        Check("copied_measurement_in_other_cell_remains_staged_after_first_cell_commit",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var other=Point(1,cell:2);var w=Workspace([a,b,other]);
            var copy=w.PreviewMeasurementReferenceCopy(a.Key,[b.Key,other.Key]);w.ConfirmMeasurementReferenceCopy(copy,[b.Key,other.Key],"two Cell stage");
            w.BuildMeasurementReplacementCandidate([b.Key]);Commit(w,"CELL1",true);Offset(w.ActiveOffsets[other.Key],0,0);
            True(w.GetPoint(other.Key).ProxySource is not null&&w.PreviewMeasurementReplacement([other.Key]).Keys.SequenceEqual([other.Key]));
            w.BuildMeasurementReplacementCandidate([other.Key]);Commit(w,"CELL2",true);Offset(w.ActiveOffsets[other.Key],3,1);
        });
        Check("already_applied_legacy_operator_requires_explicit_reacknowledgement_before_measurement_replacement",()=>
        {
            var p=Point(1);var w=Workspace([p],new(){[p.Key]=new(10,-2)});w.RecordOperatorResults([p.Key],3,1,"legacy value");
            w.BuildDirectCandidate([p.Key]);Commit(w,"LEGACY-APPLIED",true);Offset(w.ActiveOffsets[p.Key],13,-1);
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField,calibrationBases:w.CalibrationBases);w=restored;
            True(w.PreviewMeasurementReplacement().Keys.Count==0);
            Throws(()=>w.BuildMeasurementReplacementCandidate([p.Key]));
            w.RecordMeasurementOperatorResults([p.Key],3,1,"explicit new measurement policy");Offset(w.GetPoint(p.Key).OperatorResult!.FinalOffset,3,1);
            w.BuildMeasurementReplacementCandidate([p.Key]);Commit(w,"EXPLICIT-REPLACE",true);Offset(w.ActiveOffsets[p.Key],3,1);
        });
        Check("already_applied_legacy_proxy_is_excluded_but_a_new_copy_is_not_blocked_by_same_numeric_old_final",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});
            var old=w.PreviewReferenceCopy(a.Key,[b.Key]);w.ConfirmReferenceCopy(old,[b.Key],"legacy proxy");Commit(w,"LEGACY-COPY",true);
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField,calibrationBases:w.CalibrationBases);w=restored;
            True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);Offset(w.ActiveOffsets[b.Key],13,-1);
            var copy=w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]);w.ConfirmMeasurementReferenceCopy(copy,[b.Key],"explicit new copy");
            True(w.PreviewMeasurementReplacement([b.Key]).Keys.SequenceEqual([b.Key]));w.BuildMeasurementReplacementCandidate([b.Key]);Commit(w,"NEW-COPY",true);Offset(w.ActiveOffsets[b.Key],3,1);
        });
        Check("pending_legacy_same_final_without_applied_transition_remains_a_valid_measurement_stage",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(13,-1)});
            var copy=w.PreviewReferenceCopy(a.Key,[b.Key]);w.ConfirmReferenceCopy(copy,[b.Key],"pending same old F");
            True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);Throws(()=>w.ResolveMeasurementReferenceValue(b.Key));
        });
    }
}
