using System.IO;
using System.Text.Json;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    // Actual production Controller/Core/native read+conversion+CAS CSV writer+reload.
    // Equipment-owned SaveCore/Guard and Vision responses use a synthetic no-device seam.
    private static async Task RunCellCsvWorkflowCsvChecks()
    {
        static Task Send(CManualInspectionController c,EN_MANUAL_INSPECTION_UI_ACTION action,IReadOnlyList<string> keys,
            Func<CManualInspectionUiRequest,CManualInspectionUiRequest>? edit=null)
        {var r=new CManualInspectionUiRequest{ExpectedVersion=c.Snapshot.Version,Keys=keys,CurrentCellNo=1};return c.ExecuteAsync(action,edit?.Invoke(r)??r);}
        static string Matrix(Fixture f,ST_MANUAL_POINT p)
        {var target=f.Review.CreateAllPlan(f.Recipe,p.BeamNo).Points.Single(x=>x.CellNo==p.CellNo&&x.ShotNo==p.ShotNo);return CReviewShotNameFormatter.ToMatrixName(p.ShotNo,target.PixelCountX);}
        static ST_REVIEW_OFFSET_ROW Row(Fixture f,ST_MANUAL_POINT p,double x,double y)=>new(p.CellNo,Matrix(f,p),x/1000,y/1000){IsDesignCoordinate=true};
        static ST_REVIEW_OFFSET_ROW SavedRow(Fixture f,ST_REVIEW_OFFSET_FILE_DATA saved,ST_MANUAL_POINT p)=>saved.Rows.Single(r=>r.CellId==p.CellNo&&r.ShotId==Matrix(f,p));
        var traces=new List<object>();
        foreach(var kind in new[]{"MEASURED","APPLIED_OFFSET","OPERATOR"})
        {
            await Check("csv_absolute_reference_"+kind.ToLowerInvariant()+"_replaces_nonzero_target_and_matches_displayed_review_f",async()=>
            {
                var f=new Fixture();var initial=await f.Backend.LoadAsync(null,default);var reference=initial.Points.First(p=>!p.Masked);
                var target=initial.Points.First(p=>!p.Masked&&p.ShotNo!=reference.ShotNo);
                var old=await f.Offsets.SaveNew("QA",[Row(f,reference,10,-2),Row(f,target,20,5)]);var bytes=System.IO.File.ReadAllBytes(old.FilePath);var directory=Path.GetDirectoryName(old.FilePath)!;var files=Directory.GetFiles(directory,"*.csv").Length;
                var writer=new CsvWriterSeam(f);var adapter=new CellCsvMeasurementSeam(f,writer);var c=new CManualInspectionController(adapter);await c.EnsureLoaded();
                if(kind=="MEASURED")await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[reference.Key]);
                if(kind=="OPERATOR")await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[reference.Key],r=>r with{ForcedXUm=3,ForcedYUm=1,Reason="synthetic typed representative"});
                var shown=c.Snapshot.Points.Single(p=>p.Key==reference.Key);
                if(kind=="APPLIED_OFFSET")
                {
                    True(shown.ReferenceFinalXUm is null&&shown.ReferenceFinalYUm is null&&shown.MeasurementXUm is null&&shown.OriginalXUm is null&&shown.CanCopyReferenceState);
                    await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],r=>r with{ReferenceKey=reference.Key,Reason="B-only is not a measured deviation"});
                    True(writer.SuccessfulSaves==0&&c.Snapshot.Points.Single(p=>p.Key==target.Key).MeasurementXUm is null);
                    await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));var unchanged=await f.Offsets.LoadLatest("QA");
                    True(writer.SuccessfulSaves==0&&writer.DeviceCalls==0&&unchanged.FilePath==old.FilePath&&SavedRow(f,unchanged,target).ReviewOffsetX==.020
                        &&SavedRow(f,unchanged,reference).ReviewOffsetX==.010&&Directory.GetFiles(directory,"*.csv").Length==files&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
                    traces.Add(new{sourceKind=kind,sourceBaseUm=new[]{10d,-2},sourceUnappliedDeltaUm=(double[]?)null,targetBeforeAndAfterUm=new[]{20d,5},saves=0,oldCsvBytesUnchanged=true,noDeviationInvented=true});
                    return;
                }
                var expected=kind=="MEASURED"?new CManualInspectionUiOffset(13,-1):new CManualInspectionUiOffset(3,1);
                True(shown.ReferenceFinalXUm==expected.XUm&&shown.ReferenceFinalYUm==expected.YUm&&shown.ReferenceValueKind==(kind=="MEASURED"?"CALIBRATED_MEASUREMENT":"ABSOLUTE_INPUT"));
                await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],r=>r with{ReferenceKey=reference.Key,Reason="stage original measurement deviation"});
                True(writer.SuccessfulSaves==0&&(await f.Offsets.LoadLatest("QA")).FilePath==old.FilePath&&c.Snapshot.Points.Single(p=>p.Key==target.Key).MeasurementXUm is null);
                await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]);var saved=await f.Offsets.LoadLatest("QA");var row=SavedRow(f,saved,target);
                True(writer.SuccessfulSaves==1&&writer.DeviceCalls==0&&row.IsDesignCoordinate&&Math.Abs(row.ReviewOffsetX*1000-expected.XUm)<1e-8&&Math.Abs(row.ReviewOffsetY*1000-expected.YUm)<1e-8);
                True(row.ReviewOffsetX*1000!=20+expected.XUm&&row.ReviewOffsetX*1000!=10+expected.XUm&&c.Snapshot.FinalOffsetPoints[target.Key]==expected&&c.Snapshot.LastSaveVerificationCode=="VERIFIED");
                True(Directory.GetFiles(directory,"*.csv").Length==files+1&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes)&&Math.Abs(SavedRow(f,saved,reference).ReviewOffsetX*1000-expected.XUm)<1e-8);
                await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],r=>r with{ReferenceKey=reference.Key,Reason="repeat same measurement deviation"});
                await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[]));True(writer.SuccessfulSaves==1&&Directory.GetFiles(directory,"*.csv").Length==files+1);
                traces.Add(new{sourceKind=kind,sourceBaseUm=new[]{10d,-2},sourceUnappliedDeltaUm=new[]{3d,1},displayedReferenceReviewFUm=new[]{expected.XUm,expected.YUm},targetBeforeUm=new[]{20d,5},targetCsvMm=new[]{row.ReviewOffsetX,row.ReviewOffsetY},saves=writer.SuccessfulSaves,repeatSaves=0,oldCsvBytesUnchanged=true,verification=c.Snapshot.LastSaveVerificationCode});
            });
        }
        await Check("csv_current_cell_bulk_updates_its_whole_population_but_preserves_other_cell_base_raw_and_new_measurement_eligibility",async()=>
        {
            var f=new Fixture(recipeOverrides:new(){["MAX_CELL_NUMBER"]="2",["CELL2_ALIGN_TO_1ST_PIXEL_X"]="15",["CELL2_ALIGN_TO_1ST_PIXEL_Y"]="1"});
            var source=await f.Backend.LoadAsync(null,default);var cell1=source.Points.Where(p=>!p.Masked&&p.CellNo==1).GroupBy(p=>p.ShotNo).Take(2).Select(g=>g.First()).ToArray();
            var other=source.Points.First(p=>!p.Masked&&p.CellNo==2);True(cell1.Length==2);
            var old=await f.Offsets.SaveNew("QA",[Row(f,cell1[0],10,-2),Row(f,other,20,5)]);var bytes=System.IO.File.ReadAllBytes(old.FilePath);
            var writer=new CsvWriterSeam(f);var c=new CManualInspectionController(writer);await c.EnsureLoaded();
            foreach(var p in cell1.Append(other))await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],r=>r with{ForcedXUm=2,ForcedYUm=1,Reason="synthetic current-cell input"});
            True(c.Snapshot.CellBulkEligibleCounts[1]==2&&c.Snapshot.CellBulkEligibleCounts[2]==1);
            var oldOther=c.Snapshot.Points.Single(p=>p.Key==other.Key);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[other.Key]);var saved=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==1&&writer.DeviceCalls==0&&SavedRow(f,saved,other).ReviewOffsetX==.020&&SavedRow(f,saved,other).ReviewOffsetY==.005);
            True(c.Snapshot.Points.Single(p=>p.Key==other.Key).BaseXUm==oldOther.BaseXUm&&c.Snapshot.Points.Single(p=>p.Key==other.Key).WorkingXUm==oldOther.WorkingXUm&&c.Snapshot.CellBulkEligibleCounts[2]==1);
            True(SavedRow(f,saved,cell1[0]).ReviewOffsetX==.002&&SavedRow(f,saved,cell1[1]).ReviewOffsetX==.002&&c.Snapshot.StatusText.Contains("Cell #1")&&c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
            await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=null}));True(writer.SuccessfulSaves==1&&c.Snapshot.CellBulkEligibleCounts[2]==1);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=2});var final=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==2&&SavedRow(f,final,other).ReviewOffsetX==.002&&SavedRow(f,final,cell1[0]).ReviewOffsetX==.002&&c.Snapshot.CellBulkEligibleCounts.Values.All(n=>n==0));
            traces.Add(new{sourceKind="CURRENT_CELL_BULK",cell1Eligible=2,cell2Eligible=1,firstScope=1,unrelatedCell2SavedBeforeAndAfterUm=20,cell2EligibleAfterFirst=1,secondScope=2,saves=writer.SuccessfulSaves,unscopedRequestSaved=0});
        });
        var output=Environment.GetEnvironmentVariable("A3_CELL_CSV_TRACE");if(!string.IsNullOrWhiteSpace(output)){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);await System.IO.File.WriteAllTextAsync(output,JsonSerializer.Serialize(traces,new JsonSerializerOptions{WriteIndented=true}));}
    }

    private sealed class CellCsvMeasurementSeam(Fixture fixture,CsvWriterSeam inner):IManualInspectionBackend
    {
        private readonly SyntheticCompletedMachiningAuthority _machining=new(fixture);
        public bool IsSimulation=>false;public string StateDirectory=>inner.StateDirectory;public IReadOnlyDictionary<string,ST_MANUAL_OFFSET> LastSavedValues=>inner.LastSavedValues;
        public async Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? keys=null)=>_machining.Bind(await inner.LoadAsync(path,token,keys));
        public Task EnsureCurrentAsync(CManualInspectionSource s,CancellationToken token)=>inner.EnsureCurrentAsync(s,token);
        public Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource s,CancellationToken token)=>inner.ReadSavedOffsetSnapshotAsync(s,token);
        public void GuardOperation()=>inner.GuardOperation();public void Stop() { }
        public Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource s,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)
        {token.ThrowIfCancellationRequested();var p=s.Points.Single(p=>p.Key==key);var m=new ST_MANUAL_MEASUREMENT("OK",3,1,"SYNTHETIC-CURRENT-RAW-"+key,s.Context.RecipeId,s.Context.RunId,s.Context.CoordinateSignature){RawData="Synthetic DESIGN micrometre response; no device"};return Task.FromResult(new CManualInspectionMeasurement(key,SyntheticCompletedMachiningAuthority.Packet(m,s,key),p.XMm,p.YMm));}
        public Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)=>throw new NotSupportedException("No equipment or flying simulation in this test.");
        public Task<string> SaveAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string id,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null)=>inner.SaveAsync(s,values,id,rows,token,consumed);
    }
}
