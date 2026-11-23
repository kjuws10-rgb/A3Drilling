using System.Text.Json;
using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunBulkHistoryChecks()
    {
        static void FreshApply(CManualInspectionWorkspace w,int n,double x=1,double y=0)
        {
            var p=w.Points.First();w.RecordMeasurement(p.Key,Measurement(x,y,raw:"HISTORY-"+n));
            w.BuildBulkCandidate();True(w.ValidateCandidate(true).Ok);var s=w.CreateApplySnapshot(true);w.CommitApplied(s,"HISTORY-REV-"+n,"synthetic bulk save");
        }
        static void Undo(CManualInspectionWorkspace w,string id)
        {var s=w.CreateRestorePreviousSnapshot();w.CommitRestored(s,id,"synthetic undo");}
        static void Redo(CManualInspectionWorkspace w,string id)
        {var s=w.CreateRestoreNextSnapshot();w.CommitRestored(s,id,"synthetic redo");}

        Check("bulk_whole_model_collects_valid_points_from_all_cells_and_heads",()=>
        {
            var a=Point(1,x:1,y:2);var b=Point(2,x:-1,y:3,cell:2,head:8);var w=Workspace([a,b]);
            var preview=w.PreviewBulkCorrection();True(preview.Total==2&&preview.Keys.ToHashSet().SetEquals([a.Key,b.Key]));
            var c=w.BuildBulkCandidate();True(c.Rows.Select(r=>r.Key).ToHashSet().SetEquals([a.Key,b.Key]));
            True(w.ValidateCandidate(false).Ok);Apply(w,"ALL-CELLS");Offset(w.ActiveOffsets[a.Key],1,2);Offset(w.ActiveOffsets[b.Key],-1,3);
        });
        Check("bulk_excludes_ng_masked_unmeasured_but_keeps_real_measured_zero",()=>
        {
            var zero=Point(1,0,0);var ng=Point(2,1,1,"NG");var masked=Point(3) with{Masked=true};var empty=Point(4,judge:"UNMEASURED");
            var w=Workspace([zero,ng,masked,empty]);var p=w.PreviewBulkCorrection();
            True(p.Keys.SequenceEqual([zero.Key])&&p.ExcludedNg==1&&p.ExcludedMasked==1&&p.ExcludedUnmeasured==1);
            w.BuildBulkCandidate();True(w.ValidateCandidate(false).Ok);Apply(w,"ZERO-REAL");
            True(!w.GetPoint(zero.Key).IsMeasurementOffsetInitialized&&w.GetPoint(zero.Key).Judge=="OK");Offset(w.ActiveOffsets[ng.Key],0,0);
        });
        Check("bulk_excludes_initialization_even_with_preserved_historic_raw",()=>
        {
            var p=Point(1) with{IsMeasurementOffsetInitialized=true,Judge="UNMEASURED",ErrorXUm=null,ErrorYUm=null};var w=Workspace([p]);
            True(w.PreviewBulkCorrection().Keys.Count==0);Throws(()=>w.BuildBulkCandidate());True(w.Candidate is null);
        });
        Check("bulk_has_no_fake_targets_for_fresh_unmeasured_model",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED"),Point(2,judge:"UNMEASURED")]);
            True(w.PreviewBulkCorrection().Keys.Count==0&&w.PreviewBulkCorrection().ExcludedUnmeasured==2);Throws(()=>w.BuildBulkCandidate());
            True(w.Revisions.Count==1&&w.UndoCount==0&&!w.IsBusy);
        });
        Check("bulk_never_readds_consumed_raw_and_preserves_applied_baseline",()=>
        {
            var a=Point(1,1,0);var b=Point(2,2,0);var w=Workspace([a,b]);w.BuildDirectCandidate([a.Key]);w.ValidateCandidate(false);Apply(w,"A-ONLY");
            var p=w.PreviewBulkCorrection();True(p.Keys.SequenceEqual([b.Key])&&p.ExcludedConsumed>=1);
            w.BuildBulkCandidate();True(w.ValidateCandidate(false).Ok);Apply(w,"B-ONLY");Offset(w.ActiveOffsets[a.Key],1,0);Offset(w.ActiveOffsets[b.Key],2,0);
            True(w.PreviewBulkCorrection().Keys.Count==0);Throws(()=>w.BuildBulkCandidate());
        });
        Check("bulk_adopted_file_measurement_is_eligible_but_unproven_import_is_not",()=>
        {
            var a=Point(1) with{Source="IMPORTED",OriginalMeasurement=null,AdoptedFileMeasurement=Measurement(2,1,raw:"FILE-ADOPTED")};
            var b=Point(2) with{Source="IMPORTED",OriginalMeasurement=null};var w=Workspace([a with{ErrorXUm=2,ErrorYUm=1},b]);
            True(w.PreviewBulkCorrection().Keys.SequenceEqual([a.Key]));w.BuildBulkCandidate();True(w.ValidateCandidate(false).Ok);
        });
        Check("bulk_includes_explicit_representative_copy_with_original_judge_and_manual_gate",()=>
        {
            var w=Workspace([Point(1,2,-1),Point(2,judge:"ERROR")]);var copy=w.PreviewReferenceCopy(Key(1),[Key(2)]);
            w.ConfirmReferenceCopy(copy,[Key(2)],"synthetic explicit representative");var p=w.PreviewBulkCorrection();
            True(p.Keys.ToHashSet().SetEquals([Key(1),Key(2)])&&p.ManualCount==1);w.BuildBulkCandidate();True(!w.ValidateCandidate(false).Ok&&w.ValidateCandidate(true).Ok);
        });
        Check("bulk_conflicting_fresh_beams_at_one_shot_block_atomic_save",()=>
        {
            var a=Point(1,1,0);var b=Point(1,2,0,beam:1);var w=Workspace([a,b]);var before=w.CaptureSession();
            w.BuildBulkCandidate();True(!w.ValidateCandidate(true).Ok);Throws(()=>w.CreateApplySnapshot(true));
            True(w.Context==before.Context&&w.ActiveOffsets.Values.All(v=>v==ST_MANUAL_OFFSET.Zero)&&w.UndoCount==0);
        });
        Check("bulk_consistent_beams_share_one_written_offset",()=>
        {
            var a=Point(1,1,0);var b=Point(1,1,0,beam:1);var w=Workspace([a,b]);w.BuildBulkCandidate();True(w.ValidateCandidate(false).Ok);Apply(w,"SIBLINGS");
            Offset(w.ActiveOffsets[a.Key],1,0);True(w.ActiveOffsets[a.Key]==w.ActiveOffsets[b.Key]&&w.UndoCount==1);
        });
        Check("bulk_real_zero_proxy_marks_previously_empty_target_measured_and_undo_redo_restore_state",()=>
        {
            var reference=Point(1,0,0);var target=Point(2,judge:"UNMEASURED") with{OriginalMeasurement=null,IsMeasurementOffsetInitialized=true};
            var w=new CManualInspectionWorkspace();w.Load(Initial,[reference,target],new Dictionary<string,ST_MANUAL_OFFSET>(),DefaultLimits,InsideField,new HashSet<string>{target.OffsetKey});
            var copy=w.PreviewReferenceCopy(reference.Key,[target.Key]);w.ConfirmReferenceCopy(copy,[target.Key],"explicit measured zero representative");
            True(w.PreviewBulkCorrection().Keys.Contains(target.Key));w.BuildBulkCandidate();True(w.ValidateCandidate(true).Ok);var write=w.CreateApplySnapshot(true);w.CommitApplied(write,"ZERO-PROXY-APPLY","save copied real zero");
            True(!w.GetPoint(target.Key).IsMeasurementOffsetInitialized&&!w.ActiveInitializedOffsetKeys.Contains(target.OffsetKey));Undo(w,"ZERO-PROXY-UNDO");
            True(w.GetPoint(target.Key).IsMeasurementOffsetInitialized);Redo(w,"ZERO-PROXY-REDO");True(!w.GetPoint(target.Key).IsMeasurementOffsetInitialized);Offset(w.ActiveOffsets[target.Key],0,0);
        });
        Check("bulk_proxy_to_never_measured_target_keeps_valid_lineage_through_session_restore",()=>
        {
            var reference=Point(1,1,0);var target=Point(2,judge:"UNMEASURED") with{OriginalMeasurement=null};var w=Workspace([reference,target]);
            var copy=w.PreviewReferenceCopy(reference.Key,[target.Key]);w.ConfirmReferenceCopy(copy,[target.Key],"explicit proxy with no target raw");var saved=w.CaptureSession();
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(saved,w.Context!,w.Points,w.ActiveOffsets,InsideField);
            True(restored.Context!.LineageValid&&restored.GetPoint(target.Key).ProxySource is not null&&restored.GetPoint(target.Key).OriginalMeasurement is {Judge:"UNMEASURED",ErrorXUm:null,ErrorYUm:null});
            restored.BuildBulkCandidate();True(restored.ValidateCandidate(true).Ok);
        });
        Check("bulk_proxy_over_edited_import_without_original_raw_does_not_fabricate_raw_or_lose_lineage_on_restore",()=>
        {
            var reference=Point(1,1,0);var target=Point(2,2,1) with{Source="IMPORTED",OriginalMeasurement=null,AdoptedFileMeasurement=null};var w=Workspace([reference,target]);
            var copy=w.PreviewReferenceCopy(reference.Key,[target.Key]);w.ConfirmReferenceCopy(copy,[target.Key],"replace edited legacy value with explicit representative");
            True(w.GetPoint(target.Key).OriginalMeasurement is null&&w.GetPoint(target.Key).ProxySource?.OriginalSource=="IMPORTED");
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);
            True(restored.Context!.LineageValid&&restored.GetPoint(target.Key).OriginalMeasurement is null&&restored.GetPoint(target.Key).ProxySource is not null);
            restored.BuildBulkCandidate();True(restored.ValidateCandidate(true).Ok);
        });
        Check("history_undo_twice_redo_returns_exact_saved_states",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);for(var n=1;n<=3;n++)FreshApply(w,n);
            True(w.UndoCount==3&&w.RedoCount==0);Undo(w,"U1");Offset(w.ActiveOffsets[Key(1)],2,0);Undo(w,"U2");Offset(w.ActiveOffsets[Key(1)],1,0);
            True(w.UndoCount==1&&w.RedoCount==2);Redo(w,"R1");Offset(w.ActiveOffsets[Key(1)],2,0);Redo(w,"R2");Offset(w.ActiveOffsets[Key(1)],3,0);
            True(w.UndoCount==3&&w.RedoCount==0);
        });
        Check("history_retains_exactly_ten_preceding_applied_states",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);for(var n=1;n<=14;n++)FreshApply(w,n);
            True(w.UndoCount==10&&w.RedoCount==0);for(var n=1;n<=10;n++)Undo(w,"CAP-U"+n);
            Offset(w.ActiveOffsets[Key(1)],4,0);True(!w.CanUndoOffsets&&w.RedoCount==10);
            Throws(()=>w.CreateRestorePreviousSnapshot());for(var n=1;n<=10;n++)Redo(w,"CAP-R"+n);
            Offset(w.ActiveOffsets[Key(1)],14,0);True(w.UndoCount==10&&!w.CanRedoOffsets);Throws(()=>w.CreateRestoreNextSnapshot());
        });
        Check("history_aborted_undo_and_redo_do_not_move_cursor_or_change_values",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);FreshApply(w,1);FreshApply(w,2);var before=w.ActiveOffsets[Key(1)];
            var u=w.CreateRestorePreviousSnapshot();True(w.IsBusy);w.AbortRestore(u);True(!w.IsBusy&&w.UndoCount==2&&w.RedoCount==0&&w.ActiveOffsets[Key(1)]==before);
            Undo(w,"ABORT-U");var r=w.CreateRestoreNextSnapshot();w.AbortRestore(r);True(w.UndoCount==1&&w.RedoCount==1);Offset(w.ActiveOffsets[Key(1)],1,0);
        });
        Check("history_new_apply_after_undo_discards_only_redo_branch",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);for(var n=1;n<=3;n++)FreshApply(w,n);Undo(w,"FORK-U1");Undo(w,"FORK-U2");
            FreshApply(w,9,4,0);Offset(w.ActiveOffsets[Key(1)],5,0);True(w.UndoCount==2&&w.RedoCount==0);Throws(()=>w.CreateRestoreNextSnapshot());
            Undo(w,"FORK-U3");Offset(w.ActiveOffsets[Key(1)],1,0);Redo(w,"FORK-R1");Offset(w.ActiveOffsets[Key(1)],5,0);
        });
        Check("history_undo_does_not_authorize_same_measurement_again",()=>
        {
            var w=Workspace([Point(1,1,0)]);var old=w.GetPoint(Key(1)).OriginalMeasurement!;w.BuildBulkCandidate();w.ValidateCandidate(false);Apply(w,"RAW-APPLIED");Undo(w,"RAW-UNDO");
            w.RecordMeasurement(Key(1),old);True(w.PreviewBulkCorrection().Keys.Count==0);w.BuildDirectCandidate([Key(1)]);True(!w.ValidateCandidate(false).Ok);
        });
        Check("history_changed_basis_clears_unsaved_residual_and_reserves_its_consumption",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);FreshApply(w,1);w.RecordMeasurement(Key(1),Measurement(4,0,raw:"UNSAVED-OLD-BASIS"));
            var u=w.CreateRestorePreviousSnapshot();True(u.ConsumedMeasurements.ContainsKey(Key(1)));w.CommitRestored(u,"BASIS-U","test residual basis");
            True(!w.GetPoint(Key(1)).HasFiniteError&&w.PreviewBulkCorrection().Keys.Count==0&&w.GetPoint(Key(1)).OriginalMeasurement?.RawToken=="UNSAVED-OLD-BASIS");
            w.RecordMeasurement(Key(1),Measurement(4,0,raw:"UNSAVED-OLD-BASIS"));True(w.PreviewBulkCorrection().Keys.Count==0);
        });
        Check("history_roundtrip_session_preserves_undone_cursor_and_redo_target",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);for(var n=1;n<=3;n++)FreshApply(w,n);Undo(w,"SESSION-U");
            var json=JsonSerializer.Serialize(w.CaptureSession());var saved=JsonSerializer.Deserialize<ST_MANUAL_SESSION>(json)!;var copy=new CManualInspectionWorkspace();
            copy.RestoreSession(saved,w.Context!,w.Points,w.ActiveOffsets,InsideField);True(copy.UndoCount==2&&copy.RedoCount==1);Redo(copy,"SESSION-R");Offset(copy.ActiveOffsets[Key(1)],3,0);
        });
        Check("history_init_zero_and_real_zero_remain_distinct_during_undo_redo",()=>
        {
            var w=Workspace([Point(1,1,0)]);w.InitializeMeasurementOffsets();w.ValidateCandidate(false);Apply(w,"INIT-SAVE");
            True(w.GetPoint(Key(1)).IsMeasurementOffsetInitialized);FreshApply(w,2,0,0);True(!w.GetPoint(Key(1)).IsMeasurementOffsetInitialized);
            Undo(w,"INIT-U");True(w.GetPoint(Key(1)).IsMeasurementOffsetInitialized&&!w.GetPoint(Key(1)).HasFiniteError);Redo(w,"INIT-R");
            True(!w.GetPoint(Key(1)).IsMeasurementOffsetInitialized);Offset(w.ActiveOffsets[Key(1)],0,0);
        });
        Check("history_unrelated_undo_keeps_fresh_measurement_and_reset_time_boundary_of_unchanged_init_shot",()=>
        {
            var a=Point(1);var b=Point(2);var w=Workspace([a,b]);w.InitializeMeasurementOffsets([a.Key]);w.ValidateCandidate(false);Apply(w,"UNCHANGED-INIT");
            w.RecordMeasurement(a.Key,Measurement(1,0,raw:"FRESH-UNCHANGED-SHOT"));var fresh=w.GetPoint(a.Key);
            w.BuildDirectCandidate([b.Key]);w.ValidateCandidate(false);Apply(w,"OTHER-SHOT-APPLIED");var undo=w.CreateRestorePreviousSnapshot();
            True(undo.InitializedOffsetKeys.Contains(a.OffsetKey)&&!undo.InitializationOffsetKeys.Contains(a.OffsetKey)&&!undo.ConsumedMeasurements.ContainsKey(a.Key));
            w.CommitRestored(undo,"OTHER-SHOT-UNDO","keep unrelated raw and original reset boundary");
            True(w.GetPoint(a.Key)==fresh&&w.PreviewBulkCorrection().Keys.Contains(a.Key));
        });
        Check("history_external_revision_load_cannot_reuse_old_timeline",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);FreshApply(w,1);var old=w.CaptureSession();var values=w.ActiveOffsets;
            var external=w.Context! with{RevisionId="EXTERNAL-REV"};w.Load(external,w.Points,values,DefaultLimits,InsideField);
            True(w.UndoCount==0&&w.RedoCount==0);Throws(()=>w.RestoreSession(old,external,w.Points,values,InsideField));True(w.Context==external);
        });
        Check("history_missing_native_offset_baseline_stays_absent_after_first_bulk_and_undo",()=>
        {
            var point=Point(1,1,0);var w=new CManualInspectionWorkspace();var zero=new Dictionary<string,ST_MANUAL_OFFSET>{{point.Key,ST_MANUAL_OFFSET.Zero}};
            w.Load(Initial,[point],zero,DefaultLimits,InsideField,new HashSet<string>{point.OffsetKey});
            True(!w.GetPoint(point.Key).IsMeasurementOffsetInitialized&&w.ActiveInitializedOffsetKeys.Contains(point.OffsetKey));
            w.BuildBulkCandidate();w.ValidateCandidate(false);Apply(w,"FIRST-NATIVE-BULK");Undo(w,"FIRST-NATIVE-UNDO");
            True(w.GetPoint(point.Key).IsMeasurementOffsetInitialized&&!w.GetPoint(point.Key).HasFiniteError);Offset(w.ActiveOffsets[point.Key],0,0);
        });
        Check("history_undo_quantized_legacy_baseline_roundtrips_and_redoes",()=>
        {
            var a=Point(1,1,0);var w=Workspace([a],new(){{a.Key,new(.1234567,-.7654321)}});
            w.BuildBulkCandidate();w.ValidateCandidate(false);Apply(w,"PRECISION-APPLY");Undo(w,"PRECISION-UNDO");
            var actual=w.ActiveOffsets[a.Key];Offset(actual,.123,-.765);
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);
            True(restored.RedoCount==1);Redo(restored,"PRECISION-REDO");Offset(restored.ActiveOffsets[a.Key],1.123,-.765);
        });
        Check("history_coordinate_frame_change_resets_undo_and_redo_baseline",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);FreshApply(w,1);FreshApply(w,2);Undo(w,"FRAME-U");
            var value=w.ActiveOffsets[Key(1)];w.UpdateContext(w.Context! with{CoordinateSignature="NEW-PHYSICAL-FRAME"});
            True(w.UndoCount==0&&w.RedoCount==0&&w.ActiveOffsets[Key(1)]==value);Throws(()=>w.CreateRestorePreviousSnapshot());Throws(()=>w.CreateRestoreNextSnapshot());
        });
        Check("history_restore_rejects_saved_state_missing_authoritative_native_init_keys",()=>
        {
            var w=Workspace([Point(1,0,0)]);var saved=w.CaptureSession();var before=JsonSerializer.Serialize(saved);
            Throws(()=>w.RestoreSession(saved,Initial,w.Points,w.ActiveOffsets,InsideField,new HashSet<string>{w.Points.First().OffsetKey}));
            True(JsonSerializer.Serialize(w.CaptureSession())==before);
        });
        Check("history_reserved_write_blocks_other_undo_redo_or_measurement",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);FreshApply(w,1);var u=w.CreateRestorePreviousSnapshot();
            Throws(()=>w.CreateRestorePreviousSnapshot());Throws(()=>w.CreateRestoreNextSnapshot());Throws(()=>w.RecordMeasurement(Key(1),Measurement(1,0,raw:"DURING-WRITE")));
            w.AbortRestore(u);True(!w.IsBusy&&w.UndoCount==1);
        });
        Check("history_session_foreign_duplicate_and_out_of_range_timeline_rejected_atomically",()=>
        {
            var w=Workspace([Point(1,judge:"UNMEASURED")]);FreshApply(w,1);var good=w.CaptureSession();var text=JsonSerializer.Serialize(good);
            foreach(var replacement in new[]{"[\"FOREIGN\"]","[\"HISTORY-REV-1\",\"HISTORY-REV-1\"]","null"})
            {
                var node=System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();node["OffsetTimelineRevisionIds"]=System.Text.Json.Nodes.JsonNode.Parse(replacement);
                var bad=JsonSerializer.Deserialize<ST_MANUAL_SESSION>(node.ToJsonString())!;var before=JsonSerializer.Serialize(w.CaptureSession());
                Throws(()=>w.RestoreSession(bad,w.Context!,w.Points,w.ActiveOffsets,InsideField));True(JsonSerializer.Serialize(w.CaptureSession())==before);
            }
            var root=System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();root["OffsetHistoryCursor"]=99;
            Throws(()=>w.RestoreSession(JsonSerializer.Deserialize<ST_MANUAL_SESSION>(root.ToJsonString())!,w.Context!,w.Points,w.ActiveOffsets,InsideField));
        });
    }
}
