using System.IO;
using System.Text.Json;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunRetryVisibilityChecks()
    {
        static async Task FailSecond(FakeBackend f,CManualInspectionController c)
        {f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));f.FailKey=null;}
        await Check("retry_failed_queue_does_not_force_old_remaining_when_new_point_is_explicitly_measured",async()=>
        {
            var(f,c)=await Loaded();await FailSecond(f,c);var original=c.Snapshot.Points[1].OriginalXUm;var completed=c.Snapshot.Points[0].WorkingXUm;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);
            True(f.Executed.SequenceEqual(["P1","P2","P3"])&&!c.Snapshot.HasIncomplete&&f.Saves==0&&c.Snapshot.Points[0].WorkingXUm==completed);
            True(c.Snapshot.Points[1].WorkingJudge=="UNMEASURED"&&c.Snapshot.Points[1].WorkingXUm is null&&c.Snapshot.Points[1].OriginalXUm==original);
            var path=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var saved=JsonSerializer.Deserialize<CManualInspectionController.SessionBundle>(await File.ReadAllTextAsync(path))!;
            var old=JsonSerializer.Deserialize<ST_MANUAL_OPERATION>(saved.Session.Audit.Single(a=>a.Kind=="OP_SUPERSEDED").Message)!;
            True(old.RemainingKeys.SequenceEqual(["P2"])&&old.CompletedKeys.SequenceEqual(["P1"]));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.SavedConsumption.All(pair=>pair.Key!="P2"));
        });
        await Check("retry_remaining_is_still_optional_and_unchanged_until_new_operation_begins",async()=>
        {
            var(f,c)=await Loaded();await FailSecond(f,c);var request=new CManualInspectionUiRequest{ExpectedVersion=c.Snapshot.Version,Keys=["P3"]};
            True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1&&f.Executed.Count==2&&request.Keys.SequenceEqual(["P3"]));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);True(f.Executed.SequenceEqual(["P1","P2","P2"])&&!c.Snapshot.HasIncomplete&&f.Saves==0);
        });
        await Check("retry_stale_or_all_masked_new_request_does_not_erase_existing_queue",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P3"?p with{Masked=true}:p).ToArray()};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await FailSecond(f,c);
            await Throws(()=>c.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,new(){ExpectedVersion=c.Snapshot.Version-1,Keys=["P1"]}));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]));True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1&&f.Executed.Count==2&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);True(f.Executed.Last()=="P2"&&!c.Snapshot.HasIncomplete);
        });
        await Check("retry_new_current_measure_or_move_can_leave_stopped_queue_without_applying_old_residual",async()=>
        {
            foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.Measure,EN_MANUAL_INSPECTION_UI_ACTION.Move})
            {var(f,c)=await Loaded();await FailSecond(f,c);await Do(c,action,["P3"]);True(f.Executed.SequenceEqual(["P1","P2","P3"])&&!c.Snapshot.HasIncomplete&&c.Snapshot.Points[1].WorkingJudge=="UNMEASURED"&&f.Saves==0);}
        });
        await Check("retry_new_ng_response_stays_ng_and_selected_correction_is_rejected",async()=>
        {
            var(f,c)=await Loaded();await FailSecond(f,c);f.NewJudge="NG";await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);
            True(c.Snapshot.Points[2].WorkingJudge=="NG"&&!c.Snapshot.HasIncomplete);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P3"]));True(f.Saves==0);
        });
        await Check("active_p2p_target_is_published_before_response_and_is_separate_from_last_measured",async()=>
        {
            var(f,c)=await Loaded();var observed=new List<(string Key,string? Active,string? Last,string[] Batch,bool Measuring)>();
            f.PointHook=key=>observed.Add((key,c.Snapshot.ActiveMeasurementPointKey,c.Snapshot.LastMeasuredPointKey,c.Snapshot.ActiveMeasurementPointKeys.ToArray(),c.Snapshot.IsMeasurementInProgress));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]);
            True(observed.Count==2&&observed.All(o=>o.Measuring&&o.Active==o.Key&&o.Batch.SequenceEqual([o.Key]))&&observed[0].Last is null&&observed[1].Last=="P1");
            True(!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKey is null&&c.Snapshot.ActiveMeasurementPointKeys.Count==0);
        });
        await Check("active_target_stays_inflight_until_stop_cleanup_and_clears_after_stop_or_error",async()=>
        {
            var(f,c)=await Loaded();f.Hold=true;var run=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            True(c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKey=="P1");await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]));
            c.RequestStop();await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));True(c.IsBusy&&c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKey=="P1");
            f.Release.TrySetResult();await run;True(!c.IsBusy&&!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKey is null&&c.Snapshot.ActiveMeasurementPointKeys.Count==0);
            f.Hold=false;f.FailKey="P3";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]));True(!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0&&f.Saves==0);
        });
        await Check("active_target_is_never_reported_for_move_only_or_offset_file_write",async()=>
        {
            var(f,c)=await Loaded();var bad=false;c.Changed+=(_,_)=>bad|=c.Snapshot.IsMeasurementInProgress||c.Snapshot.ActiveMeasurementPointKey is not null||c.Snapshot.ActiveMeasurementPointKeys.Count>0;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(!bad&&f.Saves==1&&f.Executed.SequenceEqual(["P1"]));
        });
        await Check("active_flying_batch_contains_only_current_typed_line_and_never_guesses_a_single_point",async()=>
        {
            var(f,c)=await Loaded();var batches=new List<string>();var bad=false;f.PointHook=key=>bad|=!c.Snapshot.IsMeasurementInProgress||c.Snapshot.ActiveMeasurementPointKey is not null||!c.Snapshot.ActiveMeasurementPointKeys.Contains(key);
            c.Changed+=(_,_)=>{if(c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count>0){var current=string.Join(",",c.Snapshot.ActiveMeasurementPointKeys);if(batches.LastOrDefault()!=current)batches.Add(current);}};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P3"],r=>r with{MeasurementMode="FLYING"});
            True(!bad&&batches.SequenceEqual(["P1,P2","P3"])&&!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0&&f.Saves==0);
        });
        await Check("active_flying_failure_clears_line_then_new_p2p_selection_is_not_blocked",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P3";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P3"],r=>r with{MeasurementMode="FLYING"}));
            True(c.Snapshot.HasIncomplete&&!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0&&c.Snapshot.RemainingCount==1);
            f.FailKey=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(!c.Snapshot.HasIncomplete&&f.Executed.SequenceEqual(["P1","P2","P3","P1"])&&c.Snapshot.Points[2].WorkingJudge=="UNMEASURED");
        });
        await Check("active_late_flying_callback_cannot_restart_or_clear_another_operations_marker",async()=>
        {
            var(f,c)=await Loaded();Action<IReadOnlyList<string>>? old=null;f.FlyingActiveHook=callback=>old=callback;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"],r=>r with{MeasurementMode="FLYING"});old!(["P1"]);
            True(!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0);f.Hold=true;var run=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);
            True(c.Snapshot.ActiveMeasurementPointKey=="P3");old([]);True(c.Snapshot.ActiveMeasurementPointKey=="P3"&&c.Snapshot.IsMeasurementInProgress);
            c.RequestStop();await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Release.TrySetResult();await run;True(!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0);
        });
        await Check("active_invalid_flying_target_is_rejected_before_backend_measurement",async()=>
        {
            var(f,c)=await Loaded();f.FlyingActiveHook=callback=>callback(["OUTSIDE-OPERATION"]);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"],r=>r with{MeasurementMode="FLYING"}));
            True(f.Executed.Count==0&&f.Saves==0&&!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0);
        });
        await Check("active_replay_backend_flying_remains_compatible_without_device_calls",async()=>
        {
            var(f,c)=await Loaded();f.Simulation=true;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation,null,r=>r with{Scenario="good"});
            var key=c.Snapshot.Points.First(p=>!p.IsMasked).Key;var seen=false;c.Changed+=(_,_)=>seen|=c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKey is null&&c.Snapshot.ActiveMeasurementPointKeys.Count>0;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[key],r=>r with{MeasurementMode="FLYING"});True(seen&&!c.Snapshot.IsMeasurementInProgress&&c.Snapshot.ActiveMeasurementPointKeys.Count==0&&f.Executed.Count==0&&f.Saves==0);
        });
        await Check("actual_replay_flying_across_rows_publishes_only_the_current_synthetic_point",async()=>
        {
            var replay=new CManualInspectionReplayBackend("good",Path.Combine(Root,Guid.NewGuid().ToString("N")));
            var source=await replay.LoadAsync(null,default);var keys=source.Points.Where(p=>!p.Masked).GroupBy(p=>p.Row).Take(2).Select(g=>g.First().Key).ToArray();
            True(keys.Length==2);replay.Configure(new(){Keys=keys,Speed=16,Fault="NONE"});
            var active=new List<string[]>();var measured=new List<string>();
            await replay.ExecuteFlyingAsync(source,keys,value=>
                {True(active.Last().SequenceEqual([value.Key]));measured.Add(value.Key);},_=>{},current=>active.Add(current.ToArray()),default);
            True(measured.SequenceEqual(keys)&&active.Count==3&&active[0].SequenceEqual([keys[0]])&&active[1].SequenceEqual([keys[1]])&&active[2].Length==0);
        });
        await Check("actual_replay_flying_failure_and_cancellation_always_clear_the_current_target",async()=>
        {
            foreach(var cancel in new[]{false,true})
            {
                var replay=new CManualInspectionReplayBackend("good",Path.Combine(Root,Guid.NewGuid().ToString("N")));
                var source=await replay.LoadAsync(null,default);var keys=source.Points.Where(p=>!p.Masked).GroupBy(p=>p.Row).Take(3).Select(g=>g.First().Key).ToArray();
                True(keys.Length==3);replay.Configure(new(){Keys=keys,Speed=16,Fault=cancel?"NONE":"VISION_TIMEOUT"});
                using var cancellation=new CancellationTokenSource();var active=new List<string[]>();var measured=new List<string>();
                await Throws(()=>replay.ExecuteFlyingAsync(source,keys,value=>measured.Add(value.Key),_=>{},current=>
                    {active.Add(current.ToArray());if(cancel&&current.Count>0)cancellation.Cancel();},cancellation.Token));
                True(active.Count>=2&&active[^1].Length==0&&active.All(batch=>batch.Length<=1)&&measured.Count==(cancel?0:1));
                True(active.Where(batch=>batch.Length>0).SelectMany(batch=>batch).All(key=>key!=keys[2]));
            }
        });
    }
}
