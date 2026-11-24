using System.IO;
using System.Reflection;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    // Controller + actual native read/conversion/field + actual CAS CSV writer. The sealed
    // SaveCoreAsync cannot run without its hardware Manager, so only that equipment-owned
    // transaction is replaced by a synthetic no-device writer seam. Never claim its guard ran.
    private static async Task RunRepresentativeApplyCsvChecks()
    {
        static Task Dispatch(CManualInspectionController controller,EN_MANUAL_INSPECTION_UI_ACTION action,IReadOnlyList<string> keys,
            Func<CManualInspectionUiRequest,CManualInspectionUiRequest>? change=null)
        {
            var request=new CManualInspectionUiRequest{ExpectedVersion=controller.Snapshot.Version,Keys=keys,CurrentCellNo=1};
            return controller.ExecuteAsync(action,change?.Invoke(request)??request);
        }
        static string Matrix(Fixture fixture,ST_MANUAL_POINT point)
        {var p=fixture.Review.CreateAllPlan(fixture.Recipe,point.BeamNo).Points.Single(p=>p.CellNo==point.CellNo&&p.ShotNo==point.ShotNo);return CReviewShotNameFormatter.ToMatrixName(point.ShotNo,p.PixelCountX);}
        await Check("csv_seam_controller_saved_B_without_deviation_copies_only_empty_state_and_never_writes_fake_measurement",async()=>
        {
            var f=new Fixture();var initial=await f.Backend.LoadAsync(null,default);var reference=initial.Points.First(p=>!p.Masked);
            var old=await f.Offsets.SaveNew("QA",[new(reference.CellNo,Matrix(f,reference),.007,-.003){IsDesignCoordinate=true}]);
            var oldBytes=await System.IO.File.ReadAllBytesAsync(old.FilePath);var directory=Path.GetDirectoryName(old.FilePath)!;var count=Directory.GetFiles(directory,"*.csv").Length;
            var backend=new CsvWriterSeam(f);var c=new CManualInspectionController(backend);await c.EnsureLoaded();
            var targets=c.Snapshot.Points.Where(p=>!p.IsMasked&&p.ShotNo!=reference.ShotNo).Take(2).ToArray();True(targets.Length==2);
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,targets.Select(p=>p.Key).ToArray(),r=>r with{ReferenceKey=reference.Key,Reason="saved B is not a residual"});
            var saved=await f.Offsets.LoadLatest("QA");True(backend.SuccessfulSaves==0&&backend.DeviceCalls==0&&saved.FilePath==old.FilePath&&Directory.GetFiles(directory,"*.csv").Length==count);
            True(System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(oldBytes)&&saved.Rows.Count==1&&saved.Rows.Single().IsDesignCoordinate&&saved.Rows.Single().ReviewOffsetX==.007&&saved.Rows.Single().ReviewOffsetY==-.003);
            True(c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.FinalOffsetFileName==saved.FileName&&c.Snapshot.FinalOffsetRevisionId==backend.LastSource!.Context.RevisionId);
            foreach(var p in targets)True(c.Snapshot.Points.Single(x=>x.Key==p.Key).MeasurementXUm is null&&c.Snapshot.Points.Single(x=>x.Key==p.Key).WorkingJudge=="UNMEASURED"&&c.Snapshot.Points.Single(x=>x.Key==p.Key).BaseXUm==0);
            await Reject(()=>Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,targets.Select(p=>p.Key).ToArray(),r=>r with{ReferenceKey=reference.Key,Reason="repeat empty measurement state"});
            True(backend.SuccessfulSaves==0&&backend.DeviceCalls==0&&Directory.GetFiles(directory,"*.csv").Length==count&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(oldBytes));
        });
        await Check("csv_seam_controller_keypad_manual_result_roundtrips_design_values_and_preserves_old_csv",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);var first=source.Points.First(p=>!p.Masked);
            var old=await f.Offsets.SaveNew("QA",[new(first.CellNo,Matrix(f,first),.003,.001){IsDesignCoordinate=true}]);var bytes=await System.IO.File.ReadAllBytesAsync(old.FilePath);
            var backend=new CsvWriterSeam(f);var c=new CManualInspectionController(backend);await c.EnsureLoaded();var target=c.Snapshot.Points.First(p=>!p.IsMasked&&p.ShotNo!=first.ShotNo);
            var original=target.OriginalXUm;await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[target.Key],r=>r with{ForcedXUm=2.5,ForcedYUm=-1.25,Reason="synthetic keypad values"});
            True(backend.SuccessfulSaves==0&&c.Snapshot.Points.Single(p=>p.Key==target.Key).IsOperatorResult);
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,[target.Key]);var saved=await f.Offsets.LoadLatest("QA");
            var actualTarget=backend.LastSource!.Points.Single(p=>p.Key==target.Key);var row=saved.Rows.Single(r=>r.CellId==actualTarget.CellNo&&r.ShotId==Matrix(f,actualTarget));
            True(backend.SuccessfulSaves==1&&backend.DeviceCalls==0&&row.IsDesignCoordinate&&!row.IsMeasurementOffsetInitialized&&row.ReviewOffsetX==.0025&&row.ReviewOffsetY==-.00125);
            True(saved.Rows.Single(r=>r.ShotId==Matrix(f,first)).ReviewOffsetX==.003&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
            True(c.Snapshot.FinalOffsetFileName==saved.FileName&&c.Snapshot.FinalOffsetPoints[target.Key]==new CManualInspectionUiOffset(2.5,-1.25)&&c.Snapshot.Points.Single(p=>p.Key==target.Key).OriginalXUm==original);
            await c.EnsureLoaded();True(c.Snapshot.Points.Single(p=>p.Key==target.Key).IsOperatorResult);
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[target.Key],r=>r with{ForcedXUm=2.5,ForcedYUm=-1.25,Reason="repeat keypad values"});
            await Reject(()=>Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,[target.Key]));True(backend.SuccessfulSaves==1&&backend.DeviceCalls==0);
        });
        await Check("csv_seam_actual_cas_refuses_concurrent_file_change_without_overwriting_or_claiming_device_guard",async()=>
        {
            var f=new Fixture();var initial=await f.Backend.LoadAsync(null,default);var reference=initial.Points.First(p=>!p.Masked);
            var old=await f.Offsets.SaveNew("QA",[new(reference.CellNo,Matrix(f,reference),.004,.002){IsDesignCoordinate=true}]);var bytes=await System.IO.File.ReadAllBytesAsync(old.FilePath);
            var backend=new CsvWriterSeam(f);var c=new CManualInspectionController(backend);await c.EnsureLoaded();var target=c.Snapshot.Points.First(p=>!p.IsMasked&&p.ShotNo!=reference.ShotNo);
            string? externalPath=null;backend.BeforeWrite=async()=>{var external=await f.Offsets.SaveNew("QA",[new(reference.CellNo,Matrix(f,reference),.011,.009){IsDesignCoordinate=true}]);externalPath=external.FilePath;};
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[reference.Key],r=>r with{ForcedXUm=3,ForcedYUm=1,Reason="synthetic numeric source for CAS race"});
            await Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],r=>r with{ReferenceKey=reference.Key,Reason="synthetic CAS stage"});
            True(backend.SuccessfulSaves==0&&externalPath is null);
            await Reject(()=>Dispatch(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));
            var latest=await f.Offsets.LoadLatest("QA");True(backend.SuccessfulSaves==0&&backend.DeviceCalls==0&&latest.FilePath==externalPath&&latest.Rows.Single().ReviewOffsetX==.011&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
        });
    }

    private sealed class CsvWriterSeam(Fixture fixture):IManualInspectionBackend
    {
        public bool IsSimulation=>false;
        public string StateDirectory=>fixture.Backend.StateDirectory;
        public IReadOnlyList<string> WatchRoots=>[];
        public IReadOnlyDictionary<string,ST_MANUAL_OFFSET> LastSavedValues {get;private set;}=new Dictionary<string,ST_MANUAL_OFFSET>();
        public CManualInspectionSource? LastSource {get;private set;}
        public int SuccessfulSaves,DeviceCalls,SyntheticGuards;
        public Func<Task>? BeforeWrite;
        public async Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? displayKeys=null)
            =>LastSource=await fixture.Backend.LoadAsync(path,token,displayKeys);
        public Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource source,CancellationToken token)
            =>fixture.Backend.ReadSavedOffsetSnapshotAsync(source,token);
        public Task EnsureCurrentAsync(CManualInspectionSource source,CancellationToken token)=>fixture.Backend.EnsureCurrentAsync(source,token);
        public void GuardOperation()=>SyntheticGuards++;
        public Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)
        {DeviceCalls++;throw new InvalidOperationException("No-device CSV seam does not execute equipment.");}
        public Task ExecuteFlyingAsync(CManualInspectionSource source,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)
        {DeviceCalls++;throw new InvalidOperationException("No-device CSV seam does not execute equipment.");}
        public void Stop(){DeviceCalls++;throw new InvalidOperationException("No-device CSV seam does not stop equipment.");}
        public async Task<string> SaveAsync(CManualInspectionSource source,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumedMeasurements=null)
        {
            await fixture.Backend.EnsureCurrentAsync(source,token);
            var type=typeof(CManualInspectionBackend);var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            type.GetMethod("ValidateWorkingValues",flags)!.Invoke(fixture.Backend,[source,values]);
            var state=(CManualInspectionSource)type.GetMethod("SourceWithSaveState",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[source,identity])!;
            var converted=(Dictionary<string,ST_REVIEW_OFFSET_ROW>)type.GetMethod("BuildWorkingRows",flags)!.Invoke(fixture.Backend,[state,values,false])!;
            var written=type.GetMethod("WrittenRow",BindingFlags.NonPublic|BindingFlags.Static)!;
            var finalRows=converted.Values.Select(row=>(ST_REVIEW_OFFSET_ROW)written.Invoke(null,[row])!).ToArray();
            var previous=await fixture.Offsets.LoadLatest(source.Context.RecipeId,token);
            var receipt=Path.Combine(StateDirectory,"SyntheticCsvSeam",Guid.NewGuid().ToString("N")+".json");Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);await System.IO.File.WriteAllTextAsync(receipt,identity,token);
            if(BeforeWrite is { } before){BeforeWrite=null;await before();}
            var saved=await fixture.Offsets.SaveNewIfUnchanged(source.Context.RecipeId,previous.FilePath,finalRows,receipt,token,expectedLatestContentSha256:previous.ContentSha256);
            LastSource=await fixture.Backend.LoadAsync(null,token);LastSavedValues=LastSource.Values;SuccessfulSaves++;
            True(saved.FilePath==LastSource.SavedOffsets!.FilePath);return LastSource.Context.RevisionId;
        }
    }
}
