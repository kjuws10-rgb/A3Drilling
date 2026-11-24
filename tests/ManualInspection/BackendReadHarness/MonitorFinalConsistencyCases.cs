using System.Globalization;
using System.IO;
using System.Reflection;
using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    // Production planner/reader/CSV writer and actual Monitor projection only; no equipment manager.
    private static async Task RunMonitorFinalConsistencyChecks()
    {
        await Check("monitor_explicit_unknown_recipe_never_falls_back_to_first_model_csv", () => MonitorSta(async () =>
        {
            var f = new Fixture { Selected = "MISSING-MODEL" };
            await f.Offsets.SaveNew("QA", [new(1,"A1",.012,-.023) { IsDesignCoordinate=true }]);
            var monitor = ShotDetailMonitor(f);
            var data = await BuildMonitorConsistencyData(monitor);
            True(data.CalculationRows.Count == 0 && data.ShotCoordinateRows.Count == 0);
            True(data.ReviewSavedInfoText.Contains("MISSING-MODEL") && data.ReviewSavedInfoText.Contains("다른 모델"));
            True(!data.ReviewSavedInfoToolTip.Contains("reviewoffset.csv"));
        }));
        await Check("monitor_removed_selected_recipe_clears_previous_shot_once_instead_of_showing_wrong_model", () => MonitorSta(async () =>
        {
            var f=new Fixture(); var monitor=ShotDetailMonitor(f);
            await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
            await BuildMonitorConsistencyData(monitor); True(monitor.CoordinateCalculationRows.Count>0);
            var builds=0;
            SetDisplay(monitor,"_refreshCurrentScreen",(Func<Task>)(async()=>{builds++;await BuildMonitorConsistencyData(monitor);}));
            f.Selected="REMOVED"; await monitor.RefreshLiveData(); await monitor.RefreshLiveData();
            True(builds==1 && monitor.CoordinateCalculationRows.Count==0);
            True(monitor.CoordinateReviewSavedInfoText.Contains("REMOVED"));
        }));
        await Check("monitor_saved_design_microns_and_scanner_millimetres_are_separate_nonadditive_info", () => MonitorSta(async () =>
        {
            foreach(var scale in new[]{0,50,100,200})
            {
                var f=new Fixture(settingOverrides:new(){["REVIEW_OFFSET_SCALE"]=scale.ToString(CultureInfo.InvariantCulture)});
                var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
                var monitor=ShotDetailMonitor(f);var data=await BuildMonitorConsistencyData(monitor);
                True(data.ReviewSavedInfoText.Contains("저장 DESIGN µm: X 12.000 / Y -23.000"));
                True(data.ReviewSavedInfoText.Contains("적용 SCANNER mm: X "+(.012*scale/100).ToString("F6",CultureInfo.InvariantCulture)));
                True(data.ReviewSavedInfoText.Contains("Scale "+scale+"%"));
                True(data.ReviewSavedInfoText.Contains(saved.FileName) && data.ReviewSavedInfoToolTip.Contains(saved.ContentSha256));
                True(data.ReviewSavedInfoToolTip.Contains(saved.FilePath) && data.ReviewSavedInfoToolTip.Contains("모델 QA") && data.ReviewSavedInfoToolTip.Contains("Shot A1"));
                True(!data.CalculationRows.Any(row=>row.Key is "REVIEW_SAVED" or "CSV_REVIEW"));
                NearReview(data.CalculationRows.Single(row=>row.Key=="REVIEW"),.012*scale/100,-.023*scale/100);
                True((await f.Offsets.LoadLatest("QA")).Rows.Single().ReviewOffsetX==.012);
                True(monitor.CoordinateReviewSavedInfoText==data.ReviewSavedInfoText);
            }
        }));
        await Check("monitor_saved_info_same_csv_is_basis_independent_while_applied_row_uses_selected_axes", () => MonitorSta(async () =>
        {
            var f=new Fixture(settingOverrides:new(){["H01_SCANNER_XY_FLIP"]="ON",["H01_SCANNER_X_FLIP"]="ON",["H01_SCANNER_Y_FLIP"]="OFF"});
            await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
            var monitor=ShotDetailMonitor(f);string? original=null;
            foreach(var basis in new[]{"DESIGN","SCANNER","REVIEW_CAMERA"})
            {
                SetDisplay(monitor,"_coordinateBasis",basis);var data=await BuildMonitorConsistencyData(monitor);
                original??=data.ReviewSavedInfoText;True(data.ReviewSavedInfoText==original);
                var row=data.CalculationRows.Single(r=>r.Key=="REVIEW");
                var expected=basis=="SCANNER"?(-.023,.012):basis=="REVIEW_CAMERA"?(.012,.023):(.012,-.023);
                NearReview(row,expected.Item1,expected.Item2);
            }
        }));
        await Check("monitor_legacy_scanner_info_converts_stored_value_once_to_design_microns", () => MonitorSta(async () =>
        {
            var f=new Fixture(settingOverrides:new(){["H01_SCANNER_XY_FLIP"]="OFF",["H01_SCANNER_X_FLIP"]="OFF",["H01_SCANNER_Y_FLIP"]="OFF"});
            await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023)]);
            var data=await BuildMonitorConsistencyData(ShotDetailMonitor(f));
            True(data.ReviewSavedInfoText.Contains("저장 DESIGN µm: X 12.000 / Y 23.000"));
            True(data.ReviewSavedInfoToolTip.Contains("원본 좌표계 SCANNER / mm"));
            NearReview(data.CalculationRows.Single(r=>r.Key=="REVIEW"),.012,.023);
        }));
        await Check("monitor_info_distinguishes_no_file_init_and_real_measured_zero", () => MonitorSta(async () =>
        {
            var f=new Fixture();var monitor=ShotDetailMonitor(f);
            var none=await BuildMonitorConsistencyData(monitor);True(none.ReviewSavedInfoText.Contains("CSV 없음") && none.ReviewSavedInfoText.Contains("보정값 없음"));
            await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true}]);
            var measured=await BuildMonitorConsistencyData(monitor);True(measured.ReviewSavedInfoText.Contains("저장 DESIGN µm: X 0.000 / Y 0.000"));
            await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);
            var init=await BuildMonitorConsistencyData(monitor);True(init.ReviewSavedInfoText.Contains("보정값 없음") && !init.ReviewSavedInfoText.Contains("저장 DESIGN µm"));
        }));
        await Check("monitor_new_csv_updates_same_selected_shot_value_path_and_sha_together", () => MonitorSta(async () =>
        {
            var f=new Fixture();var first=await f.Offsets.SaveNew("QA",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
            var monitor=ShotDetailMonitor(f);await BuildMonitorConsistencyData(monitor);var key=GetDisplay(monitor,"_coordinateSelectedShotKey");
            SetDisplay(monitor,"_refreshCurrentScreen",(Func<Task>)(async()=>{await BuildMonitorConsistencyData(monitor);}));
            var next=await f.Offsets.SaveNew("QA",[new(1,"A1",.031,.042){IsDesignCoordinate=true}]);await monitor.RefreshLiveData();
            NearReview(monitor.CoordinateCalculationRows.Single(r=>r.Key=="REVIEW"),.031,.042);
            True(Equals(key,GetDisplay(monitor,"_coordinateSelectedShotKey")));
            True(monitor.CoordinateReviewSavedInfoText.Contains("X 31.000 / Y 42.000") && monitor.CoordinateReviewSavedInfoToolTip.Contains(next.ContentSha256));
            True(monitor.CoordinateReviewSavedInfoToolTip.Contains(next.FilePath) && !monitor.CoordinateReviewSavedInfoToolTip.Contains(first.FilePath));
        }));
        await Check("monitor_read_error_marks_previous_csv_stale_and_unchanged_success_clears_notice", async () =>
        {
            var f=new Fixture();var store=new DisplayOffsets();var monitor=DisplayMonitor(f,store,()=>Task.CompletedTask);
            SetDisplay(monitor,"_coordinateReviewSavedInfo","CSV LAST-GOOD");SetDisplay(monitor,"_coordinateReviewSavedToolTip","SHA last-good");
            SetDisplay(monitor,"_coordinateReviewOffsetVersion",DisplayVersion("QA",store.Saved));
            store.OnRead=()=>throw new IOException("synthetic locked CSV");await monitor.RefreshLiveData();
            True(monitor.CoordinateReviewSavedInfoText.Contains("갱신 실패") && monitor.CoordinateReviewSavedInfoText.Contains("LAST-GOOD"));
            True(monitor.CoordinateReviewSavedInfoToolTip.Contains("synthetic locked CSV"));
            store.OnRead=null;await monitor.RefreshLiveData();True(monitor.CoordinateReviewSavedInfoText=="CSV LAST-GOOD" && !monitor.CoordinateReviewSavedInfoToolTip.Contains("갱신 오류"));
        });
        await Check("monitor_delayed_old_model_read_error_does_not_label_new_model_stale", async () =>
        {
            var f=new Fixture();var store=new DisplayOffsets();var messages=new List<string>();var monitor=DisplayMonitor(f,store,()=>Task.CompletedTask);
            SetDisplay(monitor,"_setStatusMessage",(Action<string>)messages.Add);
            store.OnRead=()=>{f.Selected="NEXT";throw new IOException("old model read failed");};await monitor.RefreshLiveData();
            True(messages.Count==0 && !monitor.CoordinateReviewSavedInfoText.Contains("갱신 실패"));
        });
        await Check("monitor_delayed_old_model_refresh_success_cannot_clear_new_model_error", async () =>
        {
            var f=new Fixture();var store=new DisplayOffsets();CMenuMonitor? monitor=null;
            monitor=DisplayMonitor(f,store,()=>
            {
                f.Selected="NEXT";
                SetDisplay(monitor!,"_coordinateReviewOffsetError","new model read error");
                return Task.CompletedTask;
            });
            await monitor.RefreshLiveData();
            True(monitor.CoordinateReviewSavedInfoText.Contains("갱신 실패") && monitor.CoordinateReviewSavedInfoToolTip.Contains("new model read error"));
        });
        await Check("monitor_build_discards_old_model_csv_after_selection_changes_during_read_then_retries", () => MonitorSta(async () =>
        {
            var f=new Fixture();var store=new DisplayOffsets();var monitor=ShotDetailMonitor(f);
            SetDisplay(monitor,"_reviewOffsetFile",store);
            store.OnRead=()=>{f.Selected="NEXT";return Task.CompletedTask;};
            var discarded=await BuildMonitorConsistencyData(monitor);
            True(discarded.CalculationRows.Count==0 && discarded.ShotCoordinateRows.Count==0 && discarded.ReviewSavedInfoText.Contains("모델 전환 중"));
            True(!discarded.ReviewSavedInfoToolTip.Contains("synthetic/offset.csv") && (string)GetDisplay(monitor,"_coordinateReviewOffsetVersion")=="");
            f.Selected="QA";store.OnRead=null;
            SetDisplay(monitor,"_refreshCurrentScreen",(Func<Task>)(async()=>{await BuildMonitorConsistencyData(monitor);}));
            await monitor.RefreshLiveData();
            True(monitor.CoordinateCalculationRows.Count>0 && monitor.CoordinateReviewSavedInfoText.Contains("offset.csv"));
        }));
        await Check("monitor_build_discards_delayed_old_model_read_error_without_labeling_new_model_failed", () => MonitorSta(async () =>
        {
            var f=new Fixture();var store=new DisplayOffsets();var monitor=ShotDetailMonitor(f);
            SetDisplay(monitor,"_reviewOffsetFile",store);
            store.OnRead=()=>{f.Selected="NEXT";throw new IOException("delayed old model IO failure");};
            var discarded=await BuildMonitorConsistencyData(monitor);
            True(discarded.CalculationRows.Count==0 && discarded.ReviewSavedInfoText.Contains("모델 전환 중"));
            True(!monitor.CoordinateReviewSavedInfoToolTip.Contains("delayed old model IO failure") && !monitor.CoordinateReviewSavedInfoText.Contains("갱신 실패"));
        }));
        await Check("monitor_current_shot_row_is_not_borrowed_from_another_shot", () => MonitorSta(async () =>
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"B1",.012,-.023){IsDesignCoordinate=true}]);
            var data=await BuildMonitorConsistencyData(ShotDetailMonitor(f));
            True(data.ReviewSavedInfoToolTip.Contains("Shot A1") && data.ReviewSavedInfoText.Contains("보정값 없음"));
            True(data.CalculationRows.Single(r=>r.Key=="REVIEW").X=="—");
        }));
    }
    private static async Task<ST_MONITOR_COORDINATE_VIEWER_DATA> BuildMonitorConsistencyData(CMenuMonitor monitor)
    {
        var data=await (Task<ST_MONITOR_COORDINATE_VIEWER_DATA>)typeof(CMenuMonitor).GetMethod("BuildCoordinateViewerData",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(monitor,[CancellationToken.None])!;
        typeof(CMenuMonitor).GetMethod("ApplyCoordinateViewerData",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(monitor,[data]);
        return data;
    }
}
