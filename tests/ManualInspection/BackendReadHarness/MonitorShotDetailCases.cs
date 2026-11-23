using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunMonitorShotDetailChecks()
    {
        await Check("monitor_actual_shot_detail_saved_design_offsets_match_each_basis_and_head_transform", () => MonitorSta(async () =>
        {
            foreach (var flipped in new[] { false, true })
            {
                var f = new Fixture(settingOverrides: new()
                { ["H01_SCANNER_XY_FLIP"] = flipped ? "ON" : "OFF", ["H01_SCANNER_X_FLIP"] = flipped ? "ON" : "OFF", ["H01_SCANNER_Y_FLIP"] = "OFF" });
                await f.Offsets.SaveNew("QA", [new(1,"A1",.012,-.023) { IsDesignCoordinate=true }]);
                var monitor = ShotDetailMonitor(f);
                foreach (var basis in new[] { "DESIGN", "SCANNER", "STAGE", "REVIEW_CAMERA" })
                {
                    SetDisplay(monitor,"_coordinateBasis",basis);
                    var data = await BuildActualShotDetail(monitor);
                    var row = data.CalculationRows.Single(r=>r.Key=="REVIEW");
                    var expected = basis=="SCANNER" ? flipped ? (-.023,.012) : (.012,.023)
                        : basis=="REVIEW_CAMERA" ? (.012,.023) : (.012,-.023);
                    NearReview(row,expected.Item1,expected.Item2);
                    True(row.Unit=="mm" && row.Description.Contains("DESIGN") && row.Description.Contains("100%"));
                    True(monitor.CoordinateCalculationRows.Single(r=>r.Key=="REVIEW")==row);
                    if (basis=="DESIGN")
                    {
                        var selected = data.ValueRows.Where(r=>r.Key is "CALC_DESIGN_X" or "CALC_DESIGN_Y").ToArray();
                        // Selected-position summary and Shot Detail must use the same DESIGN axes.
                        var result=data.CalculationRows.Single(r=>r.Key=="RESULT");
                        True(selected.Any(r=>r.Value==result.X) && selected.Any(r=>r.Value==result.Y));
                    }
                }
            }
        }));
        await Check("monitor_actual_saved_offset_refresh_rebuilds_selected_shot_without_reselection", () => MonitorSta(async () =>
        {
            var f=new Fixture(); var monitor=ShotDetailMonitor(f);
            var empty=await BuildActualShotDetail(monitor);True(empty.CalculationRows.Single(r=>r.Key=="REVIEW").X=="—");
            SetDisplay(monitor,"_refreshCurrentScreen",(Func<Task>)(async()=>{await BuildActualShotDetail(monitor);}));
            await f.Offsets.SaveNew("QA",[new(1,"A1",.015,-.027){IsDesignCoordinate=true}]);
            await monitor.RefreshLiveData();NearReview(monitor.CoordinateCalculationRows.Single(r=>r.Key=="REVIEW"),.015,-.027);
            var saved=monitor.CoordinateCalculationRows;
            var shot=GetDisplay(monitor,"_coordinateSelectedShotKey");
            await f.Offsets.SaveNew("QA",[new(1,"A1",.031,.042){IsDesignCoordinate=true}]);
            await monitor.RefreshLiveData();NearReview(monitor.CoordinateCalculationRows.Single(r=>r.Key=="REVIEW"),.031,.042);
            True(Equals(shot,GetDisplay(monitor,"_coordinateSelectedShotKey")));
            True(!ReferenceEquals(saved,monitor.CoordinateCalculationRows));
        }));
        await Check("monitor_review_scale_zero_hundred_twohundred_preserves_saved_value_and_shows_contribution", () => MonitorSta(async () =>
        {
            foreach(var scale in new[]{0,100,200})
            {
                var f=new Fixture(settingOverrides:new(){["REVIEW_OFFSET_SCALE"]=scale.ToString(CultureInfo.InvariantCulture)});
                await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
                var data=await BuildActualShotDetail(ShotDetailMonitor(f));var row=data.CalculationRows.Single(r=>r.Key=="REVIEW");
                NearReview(row,.012*scale/100,-.023*scale/100);
                True(row.Description.Contains("X 0.012000 / Y -0.023000") && row.Description.Contains(scale+"%"));
                var stored=await f.Offsets.LoadLatest("QA");True(stored.Rows.Single().ReviewOffsetX==.012);
            }
        }));
        await Check("monitor_review_init_and_absence_are_empty_but_measured_zero_is_valid", () => MonitorSta(async () =>
        {
            var f=new Fixture();var monitor=ShotDetailMonitor(f);
            var none=await BuildActualShotDetail(monitor);True(none.CalculationRows.Single(r=>r.Key=="REVIEW").X=="—");
            await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true}]);
            var measured=await BuildActualShotDetail(monitor);NearReview(measured.CalculationRows.Single(r=>r.Key=="REVIEW"),0,0);
            await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);
            var init=await BuildActualShotDetail(monitor);var row=init.CalculationRows.Single(r=>r.Key=="REVIEW");
            True(row.X=="—" && row.Y=="—" && row.Description.Contains("Offset Init"));
        }));
        await Check("monitor_review_subtenth_micron_value_remains_visible_in_shot_detail", () => MonitorSta(async () =>
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"A1",.000012,-.000023){IsDesignCoordinate=true}]);
            var data=await BuildActualShotDetail(ShotDetailMonitor(f));var row=data.CalculationRows.Single(r=>r.Key=="REVIEW");
            True(row.X=="0.000012" && row.Y=="-0.000023");
        }));
        await Check("monitor_latest_file_overrides_stale_recipe_setting_values_without_mutating_them", () => MonitorSta(async () =>
        {
            var f=new Fixture(recipeOverrides:new(){["CELL1_A1_REVIEW_OFFSET_X"]="9",["CELL1_A1_REVIEW_OFFSET_Y"]="8"},
                settingOverrides:new(){["CELL1_A1_REVIEW_OFFSET_X"]="7",["CELL1_A1_REVIEW_OFFSET_Y"]="6"});
            await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
            var data=await BuildActualShotDetail(ShotDetailMonitor(f));NearReview(data.CalculationRows.Single(r=>r.Key=="REVIEW"),.012,-.023);
            True(f.Recipe.Parameters.Single(p=>p.Key=="CELL1_A1_REVIEW_OFFSET_X").Value=="9");
            True(f.SettingRows.Single(p=>p.Key=="CELL1_A1_REVIEW_OFFSET_X").Value=="7");
        }));
        await Check("monitor_restored_review_rows_roundtrip_through_writer_and_actual_detail", () => MonitorSta(async () =>
        {
            var f=new Fixture();var monitor=ShotDetailMonitor(f);
            foreach(var pair in new[]{(.012,-.023),(.031,.042),(.012,-.023),(.031,.042)})
            {
                await f.Offsets.SaveNew("QA",[new(1,"A1",pair.Item1,pair.Item2){IsDesignCoordinate=true}]);
                var data=await BuildActualShotDetail(monitor);NearReview(data.CalculationRows.Single(r=>r.Key=="REVIEW"),pair.Item1,pair.Item2);
            }
        }));
        await Check("monitor_legacy_scanner_saved_row_is_converted_once_not_as_design_twice", () => MonitorSta(async () =>
        {
            var f=new Fixture(settingOverrides:new(){["H01_SCANNER_XY_FLIP"]="OFF",["H01_SCANNER_X_FLIP"]="OFF",["H01_SCANNER_Y_FLIP"]="OFF"});
            await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023)]);
            var data=await BuildActualShotDetail(ShotDetailMonitor(f));var row=data.CalculationRows.Single(r=>r.Key=="REVIEW");
            NearReview(row,.012,.023);True(row.Description.Contains("SCANNER"));
        }));
    }
    private static CMenuMonitor ShotDetailMonitor(Fixture f)
    {
        var monitor=DisplayMonitor(f,new DisplayOffsets(),()=>Task.CompletedTask);
        SetDisplay(monitor,"_reviewOffsetFile",f.Offsets);SetDisplay(monitor,"_settingManager",f.Settings);
        SetDisplay(monitor,"_coordinateBasis","DESIGN");SetDisplay(monitor,"_coordinateSelectedCellNo",1);
        SetDisplay(monitor,"_coordinateIsCellDetailVisible",true);
        SetDisplay(monitor,"_coordinateSelectedShotKey","CELL1_SHOT1");
        // Use the actual planner's stable key; no hardware objects are created.
        var p=f.Recipe.Parameters.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        foreach(var s in f.SettingRows)p[s.Key]=s.Value;
        SetDisplay(monitor,"_coordinateSelectedShotKey",CShotCoordinatePlanBuilder.BuildDesignShots(p).First().ShotKey);
        SetDisplay(monitor,"_coordinateSelectedOffsetKeys",new HashSet<string>(["RECIPE","HEAD_DEFAULT","FIRST_BEAM","REVIEW","ALIGN","SHIFT"],StringComparer.OrdinalIgnoreCase));
        SetDisplay(monitor,"_coordinateOffsetPreviewMinimum",-5.0);SetDisplay(monitor,"_coordinateOffsetPreviewMaximum",5.0);
        SetDisplay(monitor,"_lastNotifiedLiveValues",new Dictionary<string,object?>(StringComparer.Ordinal));
        return monitor;
    }
    private static async Task<ST_MONITOR_COORDINATE_VIEWER_DATA> BuildActualShotDetail(CMenuMonitor monitor)
    {
        var data=await (Task<ST_MONITOR_COORDINATE_VIEWER_DATA>)typeof(CMenuMonitor).GetMethod("BuildCoordinateViewerData",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(monitor,[CancellationToken.None])!;
        typeof(CMenuMonitor).GetMethod("ApplyCoordinateViewerData",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(monitor,[data]);
        True(data.CalculationRows.Count>0 && monitor.CoordinateCalculationRows.Count>0);
        return data;
    }
    private static void NearReview(ST_MONITOR_COORDINATE_CALCULATION_ROW row,double x,double y)
    {True(Math.Abs(double.Parse(row.X,CultureInfo.InvariantCulture)-x)<.00000051 && Math.Abs(double.Parse(row.Y,CultureInfo.InvariantCulture)-y)<.00000051);}
    private static Task MonitorSta(Func<Task> body)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async()=>{try{await body();done.SetResult();}catch(Exception e){done.SetException(e);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();return done.Task;
    }
}
