using System.Globalization;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunInitMeasureMonitorChecks()
    {
        // Actual Controller + actual CSV conversion/writer/readback + existing Monitor
        // RefreshLiveData/Build/Apply. Only measurements and the equipment save guard are
        // substituted; no device Manager, stationary feedback, Vision, PLC or A1 executes.
        await Check("manual_init_fresh_fractional_measure_bulk_refreshes_existing_monitor_same_shot_and_csv_sha",()=>MonitorSta(async()=>
        {
            foreach(var scale in new[]{100,50})
            {
                var f=new Fixture(recipeOverrides:new(){["MAX_CELL_NUMBER"]="2",["CELL2_ALIGN_TO_1ST_PIXEL_X"]="15",["CELL2_ALIGN_TO_1ST_PIXEL_Y"]="1"},
                    settingOverrides:new(){["REVIEW_OFFSET_SCALE"]=scale.ToString(CultureInfo.InvariantCulture)});
                var first=await f.Backend.LoadAsync(null,default);var p=first.Points.First(p=>p.CellNo==1&&!p.Masked);var other=first.Points.First(p=>p.CellNo==2&&!p.Masked);
                string Matrix(Drilling.Common.Review.ManualInspection.ST_MANUAL_POINT point)
                {var q=f.Review.CreateAllPlan(f.Recipe,point.BeamNo).Points.Single(q=>q.CellNo==point.CellNo&&q.ShotNo==point.ShotNo);return CReviewShotNameFormatter.ToMatrixName(point.ShotNo,q.PixelCountX);}
                var old=await f.Offsets.SaveNew("QA",[new(1,Matrix(p),.088001,-.055999){IsDesignCoordinate=true},new(2,Matrix(other),.037123,-.008456){IsDesignCoordinate=true}]);
                var oldBytes=System.IO.File.ReadAllBytes(old.FilePath);var outside=old.Rows.Single(row=>row.CellId==2);
                var writer=new CsvWriterSeam(f);var backend=new ReplacementCsvSeam(f,writer);backend.Responses[p.Key]=("OK",1.234,-2.346);
                var c=new CManualInspectionController(backend);await c.EnsureLoaded();
                var monitor=ShotDetailMonitor(f);var parameters=f.Recipe.Parameters.ToDictionary(row=>row.Key,row=>row.Value,StringComparer.OrdinalIgnoreCase);
                foreach(var setting in f.SettingRows)parameters[setting.Key]=setting.Value;
                var shot=CShotCoordinatePlanBuilder.BuildDesignShots(parameters).Single(shot=>shot.CellNo==1&&shot.CellShotNo==p.ShotNo);
                SetDisplay(monitor,"_coordinateSelectedShotKey",shot.ShotKey);SetDisplay(monitor,"_coordinateSelectedCellNo",1);SetDisplay(monitor,"_coordinateBasis","DESIGN");
                var initial=await BuildMonitorConsistencyData(monitor);NearReview(initial.CalculationRows.Single(row=>row.Key=="REVIEW"),.088001*scale/100,-.055999*scale/100);
                var chosen=GetDisplay(monitor,"_coordinateSelectedShotKey");var refreshes=0;
                SetDisplay(monitor,"_refreshCurrentScreen",(Func<Task>)(async()=>{refreshes++;await BuildMonitorConsistencyData(monitor);}));
                Task Send(EN_MANUAL_INSPECTION_UI_ACTION action,IReadOnlyList<string>? keys=null)=>c.ExecuteAsync(action,new(){ExpectedVersion=c.Snapshot.Version,Keys=keys??[],CurrentCellNo=1});
                await Send(EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);var initialized=await f.Offsets.LoadLatest("QA");
                True(writer.SuccessfulSaves==1&&initialized.FilePath!=old.FilePath&&initialized.Rows.Where(row=>row.CellId==1).All(row=>row.IsMeasurementOffsetInitialized&&row.ReviewOffsetX==0&&row.ReviewOffsetY==0));
                await monitor.RefreshLiveData();True(refreshes==1&&monitor.CoordinateCalculationRows.Single(row=>row.Key=="REVIEW").X=="—"
                    &&monitor.CoordinateReviewSavedInfoToolTip.Contains(initialized.ContentSha256)&&Equals(chosen,GetDisplay(monitor,"_coordinateSelectedShotKey")));
                await Send(EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[p.Key]);var measured=c.Snapshot.Points.Single(point=>point.Key==p.Key);
                True(measured.BaseXUm==0&&measured.BaseYUm==0&&measured.MeasurementXUm==1.234&&measured.MeasurementYUm==-2.346&&measured.OriginalXUm==1.234&&!measured.IsMeasurementOffsetInitialized&&writer.SuccessfulSaves==1);
                True((await f.Offsets.LoadLatest("QA")).FilePath==initialized.FilePath&&monitor.CoordinateCalculationRows.Single(row=>row.Key=="REVIEW").X=="—");
                await Send(EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var final=await f.Offsets.LoadLatest("QA");var stored=final.Rows.Single(row=>row.CellId==1&&row.ShotId==Matrix(p));
                var after=c.Snapshot.Points.Single(point=>point.Key==p.Key);True(writer.SuccessfulSaves==2&&final.FilePath!=initialized.FilePath&&stored.IsDesignCoordinate&&!stored.IsMeasurementOffsetInitialized);
                True(stored.ReviewOffsetX==.001234&&stored.ReviewOffsetY==-.002346&&after.BaseXUm==1.234&&after.BaseYUm==-2.346&&after.MeasurementXUm==1.234&&after.OriginalXUm==1.234);
                True(c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.FinalOffsetContentSha256==final.ContentSha256&&c.Snapshot.FinalOffsetPoints[p.Key]==new CManualInspectionUiOffset(1.234,-2.346));
                await monitor.RefreshLiveData();var review=monitor.CoordinateCalculationRows.Single(row=>row.Key=="REVIEW");NearReview(review,.001234*scale/100,-.002346*scale/100);
                True(refreshes==2&&review.Unit=="mm"&&review.Description.Contains(scale+"%")&&monitor.CoordinateReviewSavedInfoToolTip.Contains(final.ContentSha256)
                    &&monitor.CoordinateReviewSavedInfoToolTip.Contains(final.FilePath)&&Equals(chosen,GetDisplay(monitor,"_coordinateSelectedShotKey"))&&(int)GetDisplay(monitor,"_coordinateSelectedCellNo")==1);
                True(final.Rows.Single(row=>row.CellId==2)==outside&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(oldBytes)&&writer.DeviceCalls==0);
            }
        }));
    }
}
