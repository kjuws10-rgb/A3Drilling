using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static readonly List<object> PresentationObservations = [];
    private sealed class PresentationClock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
    private static (FakeBackend Backend, CManualInspectionController Controller, PresentationClock Clock) PresentationFixture()
    {
        var f = new FakeBackend(Path.Combine(Root,Guid.NewGuid().ToString("N")),false,false);
        var context=f.Source.Context;
        var points=Enumerable.Range(1,4000).Select(i=>new ST_MANUAL_POINT("V"+i,1,1,i,11,i*.001,.1)
        { Row=(i-1)/120,Column=(i-1)%120,Source="MEASURED",Judge="OK",ErrorXUm=.1,ErrorYUm=.2,
          OriginalMeasurement=new("OK",.1,.2,"INITIAL-V"+i,context.RecipeId,context.RunId,context.CoordinateSignature),MeasurementVersion=1 }).ToArray();
        f.Source=f.Source with {Points=points,Values=points.ToDictionary(p=>p.Key,_=>ST_MANUAL_OFFSET.Zero)};
        f.NewX=7;var clock=new PresentationClock();return(f,new CManualInspectionController(f,presentationTime:clock),clock);
    }
    private static async Task RunPresentationChecks()
    {
        await Check("presentation_flying_burst_coalesces_large_matrix_without_dropping_any_measurement",async()=>
        {
            var(f,c,clock)=PresentationFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var rebuilds=0;IReadOnlyList<CManualInspectionUiPoint> prior=c.Snapshot.Points;
            c.Changed+=(_,_)=>{if(!ReferenceEquals(prior,c.Snapshot.Points)){rebuilds++;prior=c.Snapshot.Points;}};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["V1"],r=>r with{MeasurementMode="FLYING"});
            True(f.FlyingKeys.Count==120&&c.Snapshot.CompletedCount==120&&!c.Snapshot.HasIncomplete&&!c.IsBusy);
            True(c.Snapshot.Points.Take(120).All(p=>p.WorkingXUm==7)&&c.Snapshot.Points.Skip(120).All(p=>p.WorkingXUm==.1));
            True(rebuilds<=6&&f.Saves==0&&clock.Ticks==0);
            PresentationObservations.Add(new { matrixPoints=4000,receivedPoints=120,fullMatrixPublications=rebuilds,
                clock="deterministic fixed presentation clock",allMeasurementsRetained=true,deviceTimingChanged=false });
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["V120"]);
            Near(c.Snapshot.CandidateRows.Single().FinalXUm,7);
        });
        await Check("presentation_error_flushes_all_recorded_points_and_preserves_remaining",async()=>
        {
            var(f,c,clock)=PresentationFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);f.FailKey="V4";
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["V1"],r=>r with{MeasurementMode="FLYING"}));
            True(!c.IsBusy&&c.Snapshot.CompletedCount==3&&c.Snapshot.RemainingCount==117);
            True(c.Snapshot.Points.Take(3).All(p=>p.WorkingXUm==7)&&c.Snapshot.Points[3].WorkingXUm==.1&&f.Saves==0);
            f.FailKey=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);
            True(!c.Snapshot.HasIncomplete&&c.Snapshot.CompletedCount==117&&c.Snapshot.Points.Take(120).All(p=>p.WorkingXUm==7));
        });
        await Check("presentation_elapsed_budget_exposes_intermediate_progress_and_immediate_last_result",async()=>
        {
            var(f,c,clock)=PresentationFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var observed=new List<int>();
            c.Changed+=(_,_)=>{observed.Add(c.Snapshot.CompletedCount);clock.Ticks+=80;};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["V1"],r=>r with{MeasurementMode="FLYING"});
            True(observed.Contains(1)&&observed.Contains(120)&&observed.Any(n=>n>1&&n<120));
            True(c.Snapshot.Points.Take(120).All(p=>p.WorkingXUm==7)&&c.Snapshot.CompletedCount==120&&!c.IsBusy);
        });
        await Check("presentation_stop_flushes_pending_points_before_delayed_device_cleanup",async()=>
        {
            var(f,c,clock)=PresentationFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            f.PointHook=key=>{if(key=="V4"){f.Hold=true;c.RequestStop();}};
            var running=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["V1"],r=>r with{MeasurementMode="FLYING"});
            await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            True(c.IsBusy&&c.Snapshot.CompletedCount==3&&c.Snapshot.Points.Take(3).All(p=>p.WorkingXUm==7));
            var stopping=c.StopAsync();True(!stopping.IsCompleted&&f.StopCalls>=1&&f.Saves==0);
            f.Release.TrySetResult();await Task.WhenAll(running,stopping).WaitAsync(TimeSpan.FromSeconds(3));
            True(!c.IsBusy&&c.Snapshot.HasIncomplete&&c.Snapshot.CompletedCount==3&&c.Snapshot.RemainingCount==117);
            True(c.Snapshot.Points.Take(3).All(p=>p.WorkingXUm==7)&&c.Snapshot.Points[3].WorkingXUm==.1&&f.Saves==0);
        });
    }
}
