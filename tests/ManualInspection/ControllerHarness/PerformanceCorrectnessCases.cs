using System.IO;
using System.Reflection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunPerformanceCorrectnessChecks()
    {
        await Check("performance_idle_stop_does_not_leave_stopping_stage_on_the_next_measurement",async()=>
        {
            var(f,c)=await Loaded();c.RequestStop();True(!c.IsBusy&&c.Snapshot.ProgressStage=="IDLE");
            var stages=new List<string>();c.Changed+=(_,_)=>stages.Add(c.Snapshot.ProgressStage);f.Hold=true;
            var measuring=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]);await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(c.IsBusy&&stages.Contains("PREPARING")&&stages.Contains("MEASURING")&&!stages.Contains("STOPPING"));
            c.RequestStop();await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Release.TrySetResult();await measuring;
            True(!c.IsBusy&&c.Snapshot.ProgressStage=="IDLE"&&f.Saves==0);
        });
        await Check("performance_measure_reuses_unchanged_point_records_without_implicit_offset_save",async()=>
        {
            var(f,c)=await Loaded();var before=c.Snapshot.Points.Single(p=>p.Key=="P2");var beforeRaw=c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm;
            var progressOnlyLists=new List<IReadOnlyList<CManualInspectionUiPoint>>();var starting=c.Snapshot.Points;
            c.Changed+=(_,_)=>{if(c.Snapshot.IsBusy&&c.Snapshot.MeasurementSequence==0)progressOnlyLists.Add(c.Snapshot.Points);};
            f.NewX=7;f.NewY=-2;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]);
            var point=c.Snapshot.Points.Single(p=>p.Key=="P1");
            if(!(progressOnlyLists.Count>0&&progressOnlyLists.All(p=>ReferenceEquals(p,starting))&&ReferenceEquals(before,c.Snapshot.Points.Single(p=>p.Key=="P2"))))
                throw new InvalidOperationException($"point publication identity: progress={progressOnlyLists.Count}, initial-list-matches={progressOnlyLists.Count(p=>ReferenceEquals(p,starting))}, unchanged-P2-record={ReferenceEquals(before,c.Snapshot.Points.Single(p=>p.Key=="P2"))}");
            True(point.OriginalXUm==7&&point.OriginalYUm==-2&&point.WorkingXUm==7&&point.OriginalXUm!=beforeRaw&&point.BaseXUm==0&&f.Saves==0&&f.Executed.SequenceEqual(["P1"])&&!c.IsBusy);
        });
        await Check("performance_due_flying_checkpoint_error_preserves_received_raw_and_stops_before_next_line",async()=>
        {
            var(f,c)=await Loaded();var checkpoint=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();var changed=false;
            f.PointHook=key=>
            {
                if(changed)return;changed=true;
                typeof(CManualInspectionController).GetField("_lastCheckpoint",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(c,DateTimeOffset.UtcNow.AddSeconds(-3));
                File.Delete(checkpoint);Directory.CreateDirectory(checkpoint);
            };
            try
            {
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P3"],r=>r with{MeasurementMode="FLYING"}));
                True(f.Executed.SequenceEqual(["P1","P2"])&&c.Snapshot.Points.Take(2).All(p=>p.OriginalXUm==f.NewX)&&c.Snapshot.CompletedCount==2&&c.Snapshot.RemainingCount==1&&c.Snapshot.HasIncomplete&&!c.IsBusy&&f.Saves==0);
            }
            finally {if(Directory.Exists(checkpoint))Directory.Delete(checkpoint);}
            f.PointHook=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);True(f.Executed.SequenceEqual(["P1","P2","P3"])&&!c.Snapshot.HasIncomplete&&f.Saves==0);
        });
        await Check("performance_stop_waits_for_line_fence_cleanup_and_does_not_start_another_line",async()=>
        {
            var(f,c)=await Loaded();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.AfterLineHook=async token=>{entered.TrySetResult();await release.Task;token.ThrowIfCancellationRequested();};
            var run=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P3"],r=>r with{MeasurementMode="FLYING"});await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(c.IsBusy&&f.Executed.SequenceEqual(["P1","P2"]));var stopping=c.StopAsync();await Task.Yield();
            True(!stopping.IsCompleted&&!run.IsCompleted&&c.IsBusy&&f.Executed.SequenceEqual(["P1","P2"]));
            release.TrySetResult();await stopping;await run;
            True(!c.IsBusy&&f.Executed.SequenceEqual(["P1","P2"])&&c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1&&c.Snapshot.Points.Take(2).All(p=>p.OriginalXUm==f.NewX)&&f.Saves==0);
        });
    }
}
