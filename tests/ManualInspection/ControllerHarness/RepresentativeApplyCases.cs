using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunRepresentativeApplyChecks()
    {
        await Check("copy_confirmation_dispatch_saves_known_final_and_refreshes_final_snapshot_without_motion",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="explicit confirmed representative"});
            True(f.Saves==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").WorkingXUm==3);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            True(f.Saves==1&&f.Executed.Count==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==3&&c.Snapshot.UndoOffsetCount==1);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="same residual again"}));True(f.Saves==1);
        });
        await Check("copy_saved_known_offset_with_no_residual_is_applied_as_absolute_f_not_fake_raw",async()=>
        {
            var(f,c)=CorrectionFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="known persisted F"});
            var target=c.Snapshot.Points.Single(p=>p.Key=="P2");True(f.Saves==0&&target.WorkingJudge=="UNMEASURED"&&target.OriginalXUm is null&&target.WorkingXUm is null&&target.BaseXUm==0&&target.IsMeasurementStateCleared);
            await c.EnsureLoaded();True(c.Snapshot.Points.Single(p=>p.Key=="P2").ReferenceFinalXUm is null&&f.Saves==0);
        });
        await Check("copy_ng_and_empty_states_never_save_until_a_separate_operator_acceptance",async()=>
        {
            var(f,c)=await Loaded(ngReference:true);var original=c.Snapshot.Points.Single(p=>p.Key=="P2").OriginalXUm;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="NG copy"});True(f.Saves==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").WorkingJudge=="NG");
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P2"],r=>r with{Reason="operator visually accepted"});True(f.Saves==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").IsOperatorResult);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]);True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P2").OriginalXUm==original&&c.Snapshot.Points.Single(p=>p.Key=="P2").OriginalJudge=="OK");
        });
        await Check("copy_numeric_invalid_target_is_excluded_while_valid_target_is_saved",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Values=new Dictionary<string,ST_MANUAL_OFFSET>{{"P1",new(0,0)},{"P2",new(-50,0)},{"P3",new(0,0)}}};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2","P3"],r=>r with{ReferenceKey="P1",Reason="per-target numeric exclusion"});
            True(f.Saves==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==-47&&c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm==3);
        });
        await Check("copy_write_failure_keeps_working_proxy_and_allows_one_explicit_retry_without_motion",async()=>
        {
            var(f,c)=await Loaded();f.FailSave=true;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="write failure"});await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(f.Saves==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").IsProxy&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==0&&!c.IsBusy);
            f.FailSave=false;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]);True(f.Saves==1&&f.Executed.Count==0);
        });
        await Check("operator_ng_raw_is_preserved_while_manual_ok_is_separately_saved",async()=>
        {
            var(f,c)=await Loaded(ngReference:true);var raw=c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{Reason="surface verified"});
            var p=c.Snapshot.Points.Single(p=>p.Key=="P1");True(f.Saves==0&&p.IsOperatorResult&&p.OriginalJudge=="NG"&&p.WorkingJudge=="OK"&&p.OriginalXUm==raw&&p.OperatorReason=="surface verified");
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalJudge=="NG");
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==1);
        });
        await Check("operator_empty_requires_keypad_pair_and_explicit_zero_is_manual_not_camera_measurement",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P1"?p with{Judge="UNMEASURED",Source="KEPT",OriginalMeasurement=null,ErrorXUm=null,ErrorYUm=null,IsMeasurementOffsetInitialized=true}:p).ToArray()};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            foreach(var pair in new (double?,double?)[]{(null,null),(1,null),(double.NaN,0)})await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=pair.Item1,ForcedYUm=pair.Item2,Reason="input"}));
            True(!c.Snapshot.Points.Single(p=>p.Key=="P1").IsOperatorResult&&f.Saves==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=0,ForcedYUm=0,Reason="explicit zero"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm is null&&!c.Snapshot.Points.Single(p=>p.Key=="P1").IsMeasurementOffsetInitialized);
        });
        await Check("operator_unadopted_import_acceptance_keeps_foreign_raw_and_current_context_metadata",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P1"?p with{Source="IMPORTED",OriginalMeasurement=p.OriginalMeasurement! with{RunId="FOREIGN",CoordinateSignature="OLD"}}:p).ToArray()};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{Reason="operator accepts current displayed values"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==2&&c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm==3);
            await c.EnsureLoaded();True(!c.Snapshot.IsReadOnly&&c.Snapshot.Points.Single(p=>p.Key=="P1").IsOperatorResult);
        });
        await Check("operator_initial_invalid_multi_request_preserves_remaining_queue_then_explicit_input_can_supersede",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P2"],r=>r with{Reason="missing current response"}));True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P2"],r=>r with{ForcedXUm=1,ForcedYUm=-1,Reason="explicit operator numbers"});True(!c.Snapshot.HasIncomplete&&f.Saves==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").IsOperatorResult);
        });
        await Check("operator_numeric_limits_are_excluded_per_row_but_field_and_shared_shot_stay_atomic",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=31,ForcedYUm=0,Reason="large observed deviation"});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1","P2"]);
            True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==0&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==1);
            var(g,d)=Fixture();g.Source=g.Source with{FieldEvaluator=(_,_,_)=>new(false,"protected actual field")};await Do(d,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(d,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=1,ForcedYUm=0,Reason="field remains protected"});await Throws(()=>Do(d,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(g.Saves==0);
        });
        await Check("operator_explicit_repeated_values_after_save_do_not_accumulate_twice_and_fresh_camera_replaces_manual",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=2,ForcedYUm=0,Reason="manual two"});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=2,ForcedYUm=0,Reason="same two"});await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==2);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(!c.Snapshot.Points.Single(p=>p.Key=="P1").IsOperatorResult&&f.Executed.SequenceEqual(["P1"]));
        });
        await Check("operator_file_return_after_external_base_change_preserves_info_but_requires_new_acceptance",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=2,ForcedYUm=0,Reason="old native operator value"});
            var oldRaw=c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm;var file=Archive(f,"operator-file",8);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P3"]);True(f.Saves==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);var returned=c.Snapshot.Points.Single(p=>p.Key=="P1");
            True(!returned.IsOperatorResult&&returned.Source=="IMPORTED"&&returned.OriginalXUm==oldRaw&&returned.WorkingXUm==2&&returned.Details.Contains("old native operator value"));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{Reason="accepted against returned current base"});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==2);
        });
    }
}
