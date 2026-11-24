using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunMeasurementWorkflowChecks()
    {
        static (FakeBackend F,CManualInspectionController C) ScopedFixture()
        {
            var(f,c)=Fixture();var p1=f.Source.Points[0];var p2=f.Source.Points[1] with{HeadNo=2};var p3=f.Source.Points[2] with{CellNo=2,HeadNo=2};
            var sibling=p1 with{Key="P1B",BeamNo=1,HeadNo=2};var p4=p3 with{Key="P4",HeadNo=1,ShotNo=4};
            var points=new[]{p1,sibling,p2,p3,p4};f.Source=f.Source with{Points=points,Values=points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(8,-5)),AppliedOffsetKeys=points.Select(p=>p.Key).ToHashSet()};return(f,c);
        }
        await Check("cell_init_stages_only_cell_and_all_shared_shot_beams",async()=>
        {
            var(f,c)=ScopedFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);var originals=c.Snapshot.Points.ToArray();
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});
            True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.OffsetInitPending&&c.Snapshot.CandidateRows.Where(r=>f.Source.Points.Single(p=>p.Key==r.Key).CellNo==1).All(r=>r.FinalXUm==0&&r.FinalYUm==0));
            True(c.Snapshot.CandidateRows.Where(r=>f.Source.Points.Single(p=>p.Key==r.Key).CellNo==2).All(r=>r.FinalXUm==8&&r.FinalYUm==-5)&&f.Saves==0&&f.Executed.Count==0);
            True(f.Source.Points.All(p=>!p.IsMeasurementOffsetInitialized)&&f.Source.Values.Values.All(v=>v==new ST_MANUAL_OFFSET(8,-5)));
        });
        await Check("manual_init_uses_current_cell_including_all_heads_and_shared_beams",async()=>
        {
            var(f,c)=ScopedFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="HEAD",OffsetInitHeadNo=1});
            var affected=new HashSet<string>{"P1","P1B","P2"};True(c.Snapshot.CandidateRows.All(r=>affected.Contains(r.Key)?r.FinalXUm==0:r.FinalXUm==8)&&f.Saves==0);
        });
        await Check("scoped_init_missing_unknown_or_unsupported_scope_rejects_without_draft",async()=>
        {
            var(f,c)=ScopedFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            foreach(var request in new[]{new CManualInspectionUiRequest{OffsetInitScope="CELL"},new(){OffsetInitScope="HEAD"},new(){OffsetInitScope="CELL",OffsetInitCellNo=99},new(){OffsetInitScope="HEAD",OffsetInitHeadNo=99},new(){OffsetInitScope="INVALID"}})
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>request with{ExpectedVersion=r.ExpectedVersion}));
            True(!c.Snapshot.IsOffsetEditPending&&f.Saves==0&&f.Executed.Count==0);
        });
        await Check("scoped_init_save_carries_metadata_and_keeps_outside_values",async()=>
        {
            var(f,c)=ScopedFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Saves==1&&c.Snapshot.Points.Where(p=>p.CellNo==1).All(p=>p.IsMeasurementOffsetInitialized&&p.BaseXUm==0)&&c.Snapshot.Points.Where(p=>p.CellNo==2).All(p=>!p.IsMeasurementOffsetInitialized&&p.BaseXUm==8));
            True(f.SourceIds.Single().Contains("InitializationOffsetKeys")||f.SourceIds.Single().Contains("initializationOffsetKeys"));
        });
        await Check("scoped_init_save_failure_preserves_native_baseline_and_draft_scope",async()=>
        {
            var(f,c)=ScopedFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="HEAD",OffsetInitHeadNo=1});f.FailSave=true;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.OffsetInitPending&&f.Saves==0&&f.Source.Values.Values.All(v=>v.XUm==8));
        });
        await Check("scoped_init_saved_reload_retains_initialized_gray_state_without_clearing_other_raw",async()=>
        {
            var(f,c)=ScopedFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,null,r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Refresh);
            True(c.Snapshot.Points.Where(p=>p.CellNo==1).All(p=>p.IsMeasurementOffsetInitialized&&!p.HasMeasurementHistory)&&c.Snapshot.Points.Where(p=>p.CellNo==2).All(p=>!p.IsMeasurementOffsetInitialized&&p.HasMeasurementHistory));
        });
        await Check("initial_load_and_move_only_never_create_latest_measurement_notice",async()=>
        {
            var(f,c)=await Loaded();True(c.Snapshot.MeasurementSequence==0&&c.Snapshot.LastMeasuredPointKey is null&&c.Snapshot.LastMeasurementText.Length==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);
            True(c.Snapshot.MeasurementSequence==0&&c.Snapshot.LastMeasuredPointKey is null&&f.Saves==0);
        });
        await Check("move_then_measure_immediately_publishes_zero_ok_result_without_saving",async()=>
        {
            var(f,c)=await Loaded();f.NewX=0;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            True(c.Snapshot.MeasurementSequence==1&&c.Snapshot.LastMeasuredPointKey=="P1"&&c.Snapshot.LastMeasurementText.Contains("OK")&&c.Snapshot.LastMeasurementText.Contains("0")&&f.Saves==0);
            Near(c.Snapshot.Points.Single(p=>p.Key=="P1").WorkingXUm!.Value,0);
        });
        await Check("multi_target_measure_publishes_each_result_and_last_target",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]);
            True(c.Snapshot.MeasurementSequence==2&&c.Snapshot.LastMeasuredPointKey=="P2"&&c.Snapshot.LastMeasurementText.Contains("2")&&f.Saves==0);
        });
        await Check("ng_response_updates_latest_notice_without_automatic_correction",async()=>
        {
            var(f,c)=await Loaded();f.NewJudge="NG";await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            True(c.Snapshot.MeasurementSequence==1&&c.Snapshot.LastMeasuredPointKey=="P1"&&c.Snapshot.LastMeasurementText.Contains("NG")&&c.Snapshot.Points[0].WorkingJudge=="NG"&&f.Saves==0);
        });
        await Check("failed_response_does_not_publish_fabricated_latest_measurement",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P1";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]));True(c.Snapshot.MeasurementSequence==0&&c.Snapshot.LastMeasuredPointKey is null&&f.Saves==0);
        });
        await Check("partial_batch_latest_notice_is_only_successful_completed_point",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));
            True(c.Snapshot.MeasurementSequence==1&&c.Snapshot.LastMeasuredPointKey=="P1"&&c.Snapshot.HasIncomplete&&f.Saves==0);
        });
        await Check("flying_callbacks_publish_latest_measurement_for_each_completed_point",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"],r=>r with{MeasurementMode="FLYING"});
            True(c.Snapshot.MeasurementSequence==2&&c.Snapshot.LastMeasuredPointKey=="P2"&&f.FlyingKeys.SequenceEqual(["P1","P2"])&&f.Saves==0);
        });
        await Check("refresh_and_existing_file_import_do_not_advance_actual_measurement_sequence",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);var sequence=c.Snapshot.MeasurementSequence;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Refresh);True(c.Snapshot.MeasurementSequence==sequence);
            var file=Archive(f,"latest-notice-file",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);True(c.Snapshot.MeasurementSequence==sequence&&f.Executed.Count==1);
        });
        await Check("current_position_measure_publishes_result_after_existing_arrival_without_moving_again",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]);
            True(c.Snapshot.MeasurementSequence==1&&c.Snapshot.LastMeasuredPointKey=="P1"&&f.Executed.SequenceEqual(["P1","P1"])&&f.Saves==0);
        });
        await Check("rollback_to_initialized_zero_renews_reset_boundary_in_persisted_identity",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Rollback);
            using var json=System.Text.Json.JsonDocument.Parse(f.SourceIds.Last());var keys=json.RootElement.GetProperty("InitializationOffsetKeys").EnumerateArray().Select(x=>x.GetString()).ToHashSet();
            True(keys.Contains("C1/S1")&&c.Snapshot.Points.Single(p=>p.Key=="P1").IsMeasurementOffsetInitialized&&f.Saves==3);
        });
    }
}
