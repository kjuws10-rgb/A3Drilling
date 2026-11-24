using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunOffsetAccumulationChecks()
    {
        static ST_MANUAL_APPLY_SNAPSHOT Save(CManualInspectionWorkspace w,string id,bool manual=false)
        {True(w.ValidateCandidate(manual).Ok);var s=w.CreateApplySnapshot(manual);w.CommitApplied(s,id,"synthetic current-B residual application");return s;}
        static void Copy(CManualInspectionWorkspace w,string source,params string[] targets)
        {var p=w.PreviewMeasurementReferenceCopy(source,targets);w.ConfirmMeasurementReferenceCopy(p,targets,"copy residual, not source final");}
        Check("both_paths_source_base10_error3_target_base20_copy_applies23_not13_or33",()=>
        {
            var a=Point(1,3,1);var b=Point(2,9,2,"NG");var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});var raw=b.OriginalMeasurement;
            Copy(w,a.Key,b.Key);True(w.Candidate is null&&w.GetPoint(b.Key).OriginalMeasurement==raw);Offset(w.GetPoint(b.Key).ProxySource!.FinalOffset,3,1);
            w.BuildMeasurementReplacementCandidate([a.Key,b.Key]);Save(w,"BOTH",true);Offset(w.ActiveOffsets[a.Key],13,-1);Offset(w.ActiveOffsets[b.Key],23,6);
            True(w.GetPoint(b.Key).OriginalMeasurement==raw&&w.GetPoint(b.Key).OriginalMeasurement!.Judge=="NG");
        });
        Check("copy_to_different_bases_and_cells_adds_same_residual_without_touching_source",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var c=Point(1,cell:2);var w=Workspace([a,b,c],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5),[c.Key]=new(-7,2)});
            Copy(w,a.Key,b.Key,c.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"CELL1",true);Offset(w.ActiveOffsets[b.Key],23,6);Offset(w.ActiveOffsets[c.Key],-7,2);Offset(w.ActiveOffsets[a.Key],10,-2);
            w.BuildMeasurementReplacementCandidate([c.Key]);Save(w,"CELL2",true);Offset(w.ActiveOffsets[c.Key],-4,3);
        });
        Check("consumed_source_raw_still_supplies_residual_to_a_new_target_not_its_saved_final",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});
            w.BuildMeasurementReplacementCandidate([a.Key]);Save(w,"SOURCE");Offset(w.ActiveOffsets[a.Key],13,-1);
            Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"TARGET",true);Offset(w.ActiveOffsets[b.Key],23,6);
        });
        Check("repeated_stage_and_same_representative_after_commit_never_readd_copied_residual",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[b.Key]=new(20,5)});Copy(w,a.Key,b.Key);Copy(w,a.Key,b.Key);
            w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"ONCE",true);Offset(w.ActiveOffsets[b.Key],23,6);True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);
            var before=w.ExportSessionJson();Throws(()=>w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]));True(w.ExportSessionJson()==before);
        });
        Check("copy_driving_and_original_target_camera_tokens_both_survive_commit_and_restore",()=>
        {
            var a=Point(1,3,1);var b=Point(2,9,2);var w=Workspace([a,b],new(){[b.Key]=new(20,5)});Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);
            var s=Save(w,"TWO-TOKENS",true);var driving=s.Rows.Single().MeasurementToken;var original=s.ConsumedMeasurements[b.Key];True(driving.Length>0&&driving!=original);
            var history=w.CaptureSession().ConsumedMeasurementHistory.Where(x=>x.Key==b.Key).Select(x=>x.Token).ToHashSet();True(history.Contains(driving)&&history.Contains(original));
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);
            True(restored.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);Throws(()=>restored.PreviewMeasurementReferenceCopy(a.Key,[b.Key]));
            restored.RecordMeasurement(b.Key,b.OriginalMeasurement!);True(restored.PreviewMeasurementReplacement([b.Key]).ExcludedConsumed==1);
        });
        Check("undo_redo_preserves_consumed_copy_and_never_turns_undo_into_new_apply_permission",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[b.Key]=new(20,5)});Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"COPY",true);
            var undo=w.CreateRestorePreviousSnapshot();w.CommitRestored(undo,"UNDO","undo");Offset(w.ActiveOffsets[b.Key],20,5);Throws(()=>w.PreviewMeasurementReferenceCopy(a.Key,[b.Key]));
            var redo=w.CreateRestoreNextSnapshot();w.CommitRestored(redo,"REDO","redo");Offset(w.ActiveOffsets[b.Key],23,6);True(w.PreviewMeasurementReplacement([b.Key]).Keys.Count==0);
        });
        Check("fresh_representative_raw_allows_one_new_increment_after_old_copy_was_consumed",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[b.Key]=new(20,5)});Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"FIRST",true);
            w.RecordMeasurement(a.Key,Measurement(2,-1,raw:"NEW_REPRESENTATIVE"));Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"SECOND",true);Offset(w.ActiveOffsets[b.Key],25,5);
        });
        Check("keypad_same_input_is_not_new_permission_but_changed_input_is_a_new_increment",()=>
        {
            var a=Point(1);var w=Workspace([a],new(){[a.Key]=new(10,0)});w.RecordMeasurementOperatorResults([a.Key],2,0,"manual two");var id=w.GetPoint(a.Key).OperatorResult!.OperatorId;
            w.BuildMeasurementReplacementCandidate([a.Key]);Save(w,"TWO",true);Offset(w.ActiveOffsets[a.Key],12,0);
            w.RecordMeasurementOperatorResults([a.Key],2,0,"same two");True(w.GetPoint(a.Key).OperatorResult!.OperatorId==id&&w.PreviewMeasurementReplacement().Keys.Count==0);
            foreach(var (input,final,revision) in new[]{(1d,13d,"ONE"),(2d,15d,"NEW-TWO")}){w.RecordMeasurementOperatorResults([a.Key],input,0,"changed input");w.BuildMeasurementReplacementCandidate([a.Key]);Save(w,revision,true);Offset(w.ActiveOffsets[a.Key],final,0);}
        });
        Check("raw_increment_and_total_limits_use_new_final_and_preserve_large_legacy_values_when_excluded",()=>
        {
            var a=Point(1,30,0);var w=Workspace([a],new(){[a.Key]=new(100,0)});w.BuildMeasurementReplacementCandidate([a.Key]);Save(w,"LIMIT30");Offset(w.ActiveOffsets[a.Key],130,0);
            foreach(var (baseX,error) in new[]{(100d,30.0006),(199d,2d),(500d,-5d)}){w=Workspace([Point(1,error,0)],new(){[Key(1)]=new(baseX,0)});True(w.PreviewMeasurementReplacement().ExcludedNumeric==1);Offset(w.ActiveOffsets[Key(1)],baseX,0);}
        });
        Check("copy_residual_limit_is_not_source_final_or_target_absolute_replacement_difference",()=>
        {
            var a=Point(1,3,0);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(100,0),[b.Key]=new(150,0)});Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"153",true);Offset(w.ActiveOffsets[b.Key],153,0);
        });
        Check("manual_copy_rounding_rechecks_increment_limit_before_reserving_file_write",()=>
        {
            var a=Point(1,30,0);var b=Point(2);var w=Workspace([a,b],new(){[b.Key]=new(-.0004,0)});Copy(w,a.Key,b.Key);True(w.PreviewMeasurementReplacement([b.Key]).ExcludedNumeric==1);
            w.BuildMeasurementReplacementCandidate([b.Key]);True(w.ValidateCandidate(true).Ok);Throws(()=>w.CreateApplySnapshot(true));True(!w.IsBusy);
        });
        Check("manual_driving_token_tamper_cannot_pass_candidate_validation",()=>
        {
            var w=Workspace();Copy(w,Key(1),Key(2));w.BuildMeasurementReplacementCandidate([Key(2)]);var session=w.CaptureSession();var modified=session with{Candidate=session.Candidate! with{Rows=session.Candidate.Rows.Select(r=>r with{MeasurementToken="FORGED"}).ToArray()}};
            var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(modified,Initial,w.Points,w.ActiveOffsets,InsideField);True(!fresh.ValidateCandidate(true).Ok);
        });
        Check("legacy_generic_absolute_copy_and_manual_target_addition_remain_separate",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b],new(){[a.Key]=new(10,-2),[b.Key]=new(20,5)});var generic=w.PreviewReferenceCopy(a.Key,[b.Key]);Offset(generic.Final,13,-1);
            var c=w.ConfirmReferenceCopy(generic,[b.Key],"unchanged generic absolute API");Offset(c.Rows.Single().Final,13,-1);True(w.ValidateCandidate(true).Ok);
        });
        Check("manual_acknowledgement_cannot_reauthorize_the_same_consumed_direct_or_copied_observation",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var w=Workspace([a,b]);w.BuildMeasurementReplacementCandidate([a.Key]);Save(w,"RAW");
            var before=w.GetPoint(a.Key);Throws(()=>w.RecordMeasurementOperatorResults([a.Key],null,null,"same measured result"));Throws(()=>w.RecordMeasurementOperatorResults([a.Key],3,1,"same typed result"));True(w.GetPoint(a.Key)==before);
            Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"COPY",true);before=w.GetPoint(b.Key);
            Throws(()=>w.RecordMeasurementOperatorResults([b.Key],null,null,"same copied result"));Throws(()=>w.RecordMeasurementOperatorResults([b.Key],3,1,"same copied typed result"));True(w.GetPoint(b.Key)==before);
            w.RecordMeasurementOperatorResults([a.Key],2,1,"different operator input");w.BuildMeasurementReplacementCandidate([a.Key]);Save(w,"CHANGED",true);Offset(w.ActiveOffsets[a.Key],5,2);
        });
        Check("same_representative_residual_cannot_be_reapplied_through_another_beam_of_shared_shot",()=>
        {
            var a=Point(1,3,1);var b=Point(2);var sibling=Point(2,beam:12);var w=Workspace([a,b,sibling],new(){[b.Key]=new(20,5),[sibling.Key]=new(20,5)});
            Copy(w,a.Key,b.Key);w.BuildMeasurementReplacementCandidate([b.Key]);Save(w,"FIRST-BEAM",true);Offset(w.ActiveOffsets[sibling.Key],23,6);
            Throws(()=>w.PreviewMeasurementReferenceCopy(a.Key,[sibling.Key]));
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);
            Throws(()=>restored.PreviewMeasurementReferenceCopy(a.Key,[sibling.Key]));True(restored.CaptureSession().ConsumedMeasurementHistory.Any(x=>x.Key==sibling.Key&&w.CaptureSession().ConsumedMeasurementHistory.Any(y=>y.Key==b.Key&&y.Token==x.Token)));
        });
    }
}
