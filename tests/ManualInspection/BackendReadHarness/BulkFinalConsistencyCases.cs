using System.IO;
using Drilling.Common.Recipe;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static readonly List<object> BulkFinalTrace=[];
    private static async Task RunBulkFinalConsistencyCsvChecks()
    {
        static Task Dispatch(CManualInspectionController c,EN_MANUAL_INSPECTION_UI_ACTION action,IReadOnlyList<string> keys,
            Func<CManualInspectionUiRequest,CManualInspectionUiRequest>? change=null)
        {var r=new CManualInspectionUiRequest{ExpectedVersion=c.Snapshot.Version,Keys=keys};return c.ExecuteAsync(action,change?.Invoke(r)??r);}
        static string Matrix(Fixture f,ST_MANUAL_POINT point)
        {var p=f.Review.CreateAllPlan(f.Recipe,point.BeamNo).Points.Single(p=>p.CellNo==point.CellNo&&p.ShotNo==point.ShotNo);return CReviewShotNameFormatter.ToMatrixName(point.ShotNo,p.PixelCountX);}
        static Fixture Scaled()=>new(settingOverrides:new(){["REVIEW_OFFSET_SCALE"]="100"});
        static (double X,double Y) MonitorReview(Fixture f,ST_REVIEW_OFFSET_FILE_DATA file,ST_MANUAL_POINT p)
        {
            var parameters=(Dictionary<string,string>)typeof(CManualInspectionBackend).GetField("_parameters",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Backend)!;
            var map=CReviewOffsetDisplaySnapshot.Create("QA",file).ApplyToCopy(parameters);var shot=CShotCoordinatePlanBuilder.Build(map).Shots.Single(s=>s.CellNo==p.CellNo&&s.CellShotNo==p.ShotNo);
            var review=CScannerAxisTransform.ScannerOffsetToDesign(map,shot.HeadNo,shot.ReviewOffsetX,shot.ReviewOffsetY);return(review.X*1000,review.Y*1000);
        }
        await Check("bulk_csv_operator_edit_then_bulk_matches_actual_csv_final_and_monitor_review_component",async()=>
        {
            var f=Scaled();var initial=await f.Backend.LoadAsync(null,default);var p=initial.Points.First(p=>!p.Masked);
            var old=await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.018,-.003){IsDesignCoordinate=true}]);var original=System.IO.File.ReadAllBytes(old.FilePath);
            var b=new CsvWriterSeam(f);var c=new CManualInspectionController(b);await c.EnsureLoaded();
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],r=>r with{ForcedXUm=5,ForcedYUm=1,Reason="synthetic edit then bulk"});
            var working=c.Snapshot.Points.Single(x=>x.Key==p.Key);var workingLevel=CManualInspectionLevels.Evaluate(working.MeasurementXUm,working.MeasurementYUm,c.Snapshot.LevelMetric,c.Snapshot.LevelBoundsUm);
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]);var saved=await f.Offsets.LoadLatest("QA");var row=saved.Rows.Single();var final=c.Snapshot.FinalOffsetPoints[p.Key];var monitor=MonitorReview(f,saved,p);
            True(b.SuccessfulSaves==1&&b.DeviceCalls==0&&row.ReviewOffsetX==.023&&row.ReviewOffsetY==-.002&&final==new CManualInspectionUiOffset(23,-2));
            True(c.Snapshot.Points.Single(x=>x.Key==p.Key).BaseXUm==23&&Math.Abs(monitor.X-23)<1e-7&&Math.Abs(monitor.Y+2)<1e-7&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(original));
            True(workingLevel==0&&CManualInspectionLevels.Evaluate(final.XUm,final.YUm,c.Snapshot.LevelMetric,c.Snapshot.LevelBoundsUm)==3&&c.Snapshot.LastSaveVerificationCode=="VERIFIED");
            BulkFinalTrace.Add(new{scenario="normal_B_plus_delta",baseUm=new[]{18d,-3},deltaUm=new[]{5d,1},workingLevel=workingLevel+1,finalUm=new[]{final.XUm,final.YUm},csvMm=new[]{row.ReviewOffsetX,row.ReviewOffsetY},monitorDesignUm=new[]{monitor.X,monitor.Y},finalLevel=4,status=c.Snapshot.StatusText});
        });
        await Check("bulk_csv_existing_over_limit_base_explains_zero_eligible_without_changing_saved_final",async()=>
        {
            var f=Scaled();var initial=await f.Backend.LoadAsync(null,default);var p=initial.Points.First(p=>!p.Masked);
            var old=await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.500,-.700){IsDesignCoordinate=true}]);var bytes=System.IO.File.ReadAllBytes(old.FilePath);
            var b=new CsvWriterSeam(f);var c=new CManualInspectionController(b);await c.EnsureLoaded();await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],r=>r with{ForcedXUm=5,ForcedYUm=1,Reason="small residual on large B"});
            True(c.Snapshot.BulkEligibleCount==0&&c.Snapshot.TotalLimitUm==200);await Reject(()=>Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));
            True(b.SuccessfulSaves==0&&c.Snapshot.StatusText.Contains("수치한계 1")&&c.Snapshot.FinalOffsetPoints[p.Key]==new CManualInspectionUiOffset(500,-700)&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
            BulkFinalTrace.Add(new{scenario="legacy_B_over_200_excluded",baseUm=new[]{500d,-700},deltaUm=new[]{5d,1},eligible=c.Snapshot.BulkEligibleCount,saves=b.SuccessfulSaves,status=c.Snapshot.StatusText});
        });
        await Check("bulk_csv_stale_current_reconnect_reads_actual_latest_final_before_rejecting_old_operator",async()=>
        {
            var f=Scaled();var initial=await f.Backend.LoadAsync(null,default);var p=initial.Points.First(p=>!p.Masked);
            await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.018,0){IsDesignCoordinate=true}]);var b=new CsvWriterSeam(f);var c=new CManualInspectionController(b);await c.EnsureLoaded();
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],r=>r with{ForcedXUm=5,ForcedYUm=0,Reason="old basis edit"});var newer=await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.030,0){IsDesignCoordinate=true}]);
            await Reject(()=>Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));
            True(b.SuccessfulSaves==0&&c.Snapshot.FinalOffsetFileName==newer.FileName&&c.Snapshot.FinalOffsetPoints[p.Key].XUm==30&&c.Snapshot.Points.Single(x=>x.Key==p.Key).BaseXUm==30);
            True(!c.Snapshot.Points.Single(x=>x.Key==p.Key).IsOperatorResult&&c.Snapshot.StatusText.Contains("바뀌어"));
            BulkFinalTrace.Add(new{scenario="stale_reconnect_rejected",saves=b.SuccessfulSaves,finalUm=c.Snapshot.FinalOffsetPoints[p.Key].XUm,activeUm=c.Snapshot.Points.Single(x=>x.Key==p.Key).BaseXUm,status=c.Snapshot.StatusText});
        });
        await Check("bulk_csv_readback_failure_keeps_committed_values_and_reports_unverified_without_second_save",async()=>
        {
            var f=Scaled();var initial=await f.Backend.LoadAsync(null,default);var p=initial.Points.First(p=>!p.Masked);
            await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.010,0){IsDesignCoordinate=true}]);var inner=new CsvWriterSeam(f);var b=new BulkSavedReadSeam(inner){FailAfterSave=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],r=>r with{ForcedXUm=2,ForcedYUm=0,Reason="saved but read failed"});await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]);
            var saved=await f.Offsets.LoadLatest("QA");True(inner.SuccessfulSaves==1&&saved.Rows.Single().ReviewOffsetX==.012&&c.Snapshot.Points.Single(x=>x.Key==p.Key).BaseXUm==12&&c.Snapshot.UndoOffsetCount==1);
            True(c.Snapshot.LastSaveVerificationCode=="READ_FAILED"&&!c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.StatusText.Contains("재확인 실패"));await Reject(()=>Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));True(inner.SuccessfulSaves==1);
            b.FailAfterSave=false;await c.EnsureLoaded();True(c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.FinalOffsetPoints[p.Key].XUm==12);
            BulkFinalTrace.Add(new{scenario="saved_readback_failed_then_recovered",saves=inner.SuccessfulSaves,finalUm=c.Snapshot.FinalOffsetPoints[p.Key].XUm,verification=c.Snapshot.LastSaveVerificationCode});
        });
        await Check("bulk_csv_followup_external_write_is_superseded_not_an_unsaved_retry",async()=>
        {
            var f=Scaled();var initial=await f.Backend.LoadAsync(null,default);var p=initial.Points.First(p=>!p.Masked);
            await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.010,0){IsDesignCoordinate=true}]);var inner=new CsvWriterSeam(f);var b=new BulkSavedReadSeam(inner);var c=new CManualInspectionController(b);await c.EnsureLoaded();
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],r=>r with{ForcedXUm=2,ForcedYUm=0,Reason="our save before another write"});
            b.AfterSave=async()=>{await f.Offsets.SaveNew("QA",[new(p.CellNo,Matrix(f,p),.025,0){IsDesignCoordinate=true}]);};await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]);
            True(inner.SuccessfulSaves==1&&c.Snapshot.LastSaveVerificationCode=="SUPERSEDED"&&c.Snapshot.FinalOffsetPoints[p.Key].XUm==25&&c.Snapshot.Points.Single(x=>x.Key==p.Key).BaseXUm==12&&c.Snapshot.UndoOffsetCount==1);
            True(c.Snapshot.StatusText.Contains("最新")||c.Snapshot.StatusText.Contains("최신값 변경"));
            BulkFinalTrace.Add(new{scenario="saved_then_other_csv",saves=inner.SuccessfulSaves,ourCommittedUm=12,latestFinalUm=c.Snapshot.FinalOffsetPoints[p.Key].XUm,verification=c.Snapshot.LastSaveVerificationCode});
        });
        var tracePath=Environment.GetEnvironmentVariable("A3_BULK_FINAL_TRACE");
        if(!string.IsNullOrWhiteSpace(tracePath)){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tracePath))!);await System.IO.File.WriteAllTextAsync(tracePath,System.Text.Json.JsonSerializer.Serialize(BulkFinalTrace,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));}
    }
    private sealed class BulkSavedReadSeam(CsvWriterSeam inner):IManualInspectionBackend
    {
        public bool FailAfterSave;
        public Func<Task>? AfterSave;
        public bool IsSimulation=>false;public string StateDirectory=>inner.StateDirectory;
        public IReadOnlyDictionary<string,ST_MANUAL_OFFSET> LastSavedValues=>inner.LastSavedValues;
        public Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? displayKeys=null)=>inner.LoadAsync(path,token,displayKeys);
        public Task EnsureCurrentAsync(CManualInspectionSource source,CancellationToken token)=>inner.EnsureCurrentAsync(source,token);
        public Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource source,CancellationToken token)
            =>FailAfterSave&&inner.SuccessfulSaves>0?throw new IOException("synthetic final CSV read failed"):inner.ReadSavedOffsetSnapshotAsync(source,token);
        public void GuardOperation()=>inner.GuardOperation();public void Stop()=>inner.Stop();
        public Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource s,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)=>inner.ExecutePointAsync(s,key,kind,progress,token);
        public Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)=>inner.ExecuteFlyingAsync(s,keys,measured,progress,token);
        public async Task<string> SaveAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null)
        {var revision=await inner.SaveAsync(s,values,identity,rows,token,consumed);if(AfterSave is { } after){AfterSave=null;await after();}return revision;}
    }
}
