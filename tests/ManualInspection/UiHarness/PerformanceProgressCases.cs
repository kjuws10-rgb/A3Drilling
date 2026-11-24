using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int PerformanceProgress()
    {
        var observations = new List<object>();
        try { RunProgressProjectionChecks(observations); RunDelayedCurrentPointChecks(observations); RunProgressLayouts(observations); RunExistingMapLayoutChecks(); }
        catch (Exception error) { Test("performance_progress_unexpected_exception", false, error.ToString()); }
        var sourceHashes = new[] { "Views/CManualInspectionView.xaml", "Views/CManualInspectionView.xaml.cs", "Views/CManualInspectionUiContract.cs", "Views/CManualInspectionMapControl.cs", "Views/CManualInspectionScatterControl.cs" }
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource, path)))).ToLowerInvariant());
        var result = new { passed = _failures == 0, tests = Checks.Count, failures = _failures, checks = Checks, observations, sourceHashes,
            scope = "Actual source-linked standalone WPF controls/current themes; immutable synthetic 8k-point records and delayed fake Controller only. Timing observations are local presentation samples, not device latency or hardware measurements. Identity and invalidation checks prove cached status updates, while changed data/filter/levels and input still redraw. No equipment App/SDK/PLC/Vision/motion/operating CSV executes." };
        File.WriteAllText(Path.Combine(_output, "ui-performance-progress-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Performance progress UI: {Checks.Count - _failures}/{Checks.Count}"); return _failures == 0 ? 0 : 1;
    }

    private static void RunProgressProjectionChecks(List<object> observations)
    {
        LoadResources("Dark");
        var state = Snapshot(8000) with { StageXmm = 0, StageYmm = 0 };
        var fake = new FakeController(state); var view = new CManualInspectionView { DataContext = fake }; var window = HostRepresentativeView(view);
        try
        {
            Invoke(view, "SetMapCellScope", 0); Pump();
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            var scatter = (CManualInspectionScatterControl)view.FindName("ScatterPlot");
            var grid = (DataGrid)view.FindName("ResultGrid");
            var beforeFiltered = Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered");
            var beforeScoped = Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_all");
            var rows = grid.ItemsSource; var metrics = scatter.Metrics;
            var idleMapSize = map.RenderSize;
            var idleMapSlot = ((System.Collections.IList)Private(map,"_slots"))[0];
            var legend = ((WrapPanel)view.FindName("MapLegendItems")).Children[0];
            var ng = state.Points.First(point => point.WorkingJudge == "NG");
            var raw = JsonSerializer.Serialize(state.Points);
            var samples = new List<double>();
            for (var i = 0; i < 80; i++)
            {
                var timer = Stopwatch.StartNew();
                fake.Replace(state with { Version = 100 + i, IsBusy = true, ProgressStage = "MEASURING", ProgressText = "측정 응답 대기", IsProgressIndeterminate = true });
                Pump(); samples.Add(timer.Elapsed.TotalMilliseconds);
            }
            Test("performance_status_reuses_filtered_projection", ReferenceEquals(beforeFiltered, Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered")));
            Test("performance_status_reuses_map_scope", ReferenceEquals(beforeScoped, Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_all")));
            Test("performance_status_does_not_replace_result_rows", ReferenceEquals(rows, grid.ItemsSource));
            Test("performance_status_retains_scatter_statistics", ReferenceEquals(metrics, scatter.Metrics));
            Test("performance_status_retains_legend_children", ReferenceEquals(legend, ((WrapPanel)view.FindName("MapLegendItems")).Children[0]));
            Test("performance_first_busy_transition_keeps_map_size", map.RenderSize == idleMapSize);
            Test("performance_first_busy_transition_does_not_rebuild_layout_slots",ReferenceEquals(idleMapSlot,((System.Collections.IList)Private(map,"_slots"))[0]));
            Test("performance_status_keeps_ng_fill_and_raw", CManualInspectionDrawing.Level(ng, fake.Snapshot) >= 0 && JsonSerializer.Serialize(fake.Snapshot.Points) == raw
                && ((SolidColorBrush)CManualInspectionDrawing.PointBrush(view, ng, "level", fake.Snapshot)).Color.ToString() == "#FFD16776");
            fake.Replace(state with { Version = 200 }); Pump();
            var selectionMetrics = scatter.Metrics; var groups = ((DataGrid)view.FindName("ScatterGroupsGrid")).ItemsSource;
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([state.Points[1].Key], state.Points[1].Key)); Pump();
            Test("performance_selection_reuses_same_scatter_population_metrics", ReferenceEquals(selectionMetrics, scatter.Metrics));
            Test("performance_selection_retains_scatter_group_rows", ReferenceEquals(groups, ((DataGrid)view.FindName("ScatterGroupsGrid")).ItemsSource));
            Test("performance_selection_still_updates_map_and_table", Field<HashSet<string>>(map, "_selected").SetEquals([state.Points[1].Key]) && !ReferenceEquals(rows, grid.ItemsSource));
            SelectViewChoice(view, "ScatterDataCombo", "ORIGINAL"); Pump(); metrics = scatter.Metrics;
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([state.Points[2].Key], state.Points[2].Key)); Pump();
            Test("performance_original_scatter_selection_does_not_reclone_population", ReferenceEquals(metrics, scatter.Metrics) && scatter.DataKind == "ORIGINAL");
            var changed = state.Points.Select((point, i) => i == 0 ? point with { WorkingXUm = 42, WorkingYUm = -7, WorkingJudge = "NG", IsProxy = false, Source = "MEASURED" } : point).ToArray();
            fake.Replace(state with { Version = 201, Points = changed }); Pump();
            Test("performance_real_data_change_invalidates_filter_and_rows", !ReferenceEquals(beforeFiltered, Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered")) && !ReferenceEquals(rows, grid.ItemsSource));
            Test("performance_real_data_change_reaches_latest_map_point", Field<Dictionary<string,CManualInspectionUiPoint>>(map,"_pointByKey")[changed[0].Key].WorkingXUm == 42);
            var all = Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered"); SelectViewChoice(view,"JudgeFilter","NG"); Pump();
            Test("performance_filter_invalidates_only_visible_population", !ReferenceEquals(all, Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered")) && Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered").All(point=>point.WorkingJudge=="NG") && Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all").Count==8000);
            var beforeLevelRows = grid.ItemsSource;
            fake.Replace(fake.Snapshot with { Version=202, LevelBoundsUm = [1,2,3] }); Pump();
            Test("performance_level_configuration_refreshes_cached_rows", !ReferenceEquals(beforeLevelRows,grid.ItemsSource));
            SelectViewChoice(view,"JudgeFilter","ALL"); fake.Replace(state with {Version=203,IsBusy=true,IsMeasurementInProgress=true,ActiveMeasurementPointKeys=[state.Points[1].Key],ProgressStage="MEASURING",IsProgressIndeterminate=true}); Pump();
            var slot=((System.Collections.IList)Private(map,"_slots"))[0];
            Rect FirstHitBounds(){var hit=((System.Collections.IList)Private(map,"_hits"))[0]!;return (Rect)hit.GetType().GetField("Item2")!.GetValue(hit)!;}
            var beforeHit=FirstHitBounds();
            var themeBrush=(Freezable)_application.FindResource("AccentBrush");var themeWasFrozen=themeBrush.IsFrozen;
            Test("performance_progress_keeps_all_visible_hit_targets",((System.Collections.IList)Private(map,"_hits")).Count==8000);
            Test("performance_original_surface_hit_routes_to_map_control",ReferenceEquals(map.InputHitTest(new Point(map.ActualWidth/2,map.ActualHeight/2)),map));
            Test("performance_original_surface_keeps_viewport_clipping",map.ClipToBounds&&map.InputHitTest(new Point(-5,-5)) is null);
            Invoke(map,"AdvanceMeasurementBlink"); Pump();
            Test("performance_blink_preserves_original_layout_slots",ReferenceEquals(slot,((System.Collections.IList)Private(map,"_slots"))[0]));
            map.ZoomAt(1.2,new Point(120,120)); Pump();
            Test("performance_zoom_updates_hit_bounds",beforeHit!=FirstHitBounds());
            Test("performance_rendering_does_not_freeze_shared_theme_brush",ReferenceEquals(themeBrush,_application.FindResource("AccentBrush"))&&themeBrush.IsFrozen==themeWasFrozen);
            LoadResources("Light"); Pump(); map.InvalidateVisual(); Pump();
            Test("performance_theme_updates_existing_map_surface",((SolidColorBrush)map.Background).Color.ToString()==ResourceColor("GridBackgroundBrush"));
            var firstTransitionMs=samples[0];samples.Sort(); observations.Add(new { type="status-only-presentation-8000",samples=samples.Count,firstTransitionMs,p50Ms=samples[samples.Count/2],p95Ms=samples[(int)(samples.Count*.95)],maximumMs=samples[^1],hardwareLatencyMeasured=false });
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class DelayedPresentationController(CManualInspectionUiSnapshot initial) : IManualInspectionController
    {
        public CManualInspectionUiSnapshot Snapshot {get;private set;}=initial;
        public event EventHandler? Changed;
        public readonly TaskCompletionSource MeasurementFinish=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupFinish=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<EN_MANUAL_INSPECTION_UI_ACTION> Calls=[];
        private bool _stopping;
        private void State(bool busy,string stage,string text) {Snapshot=Snapshot with{Version=Snapshot.Version+1,IsBusy=busy,ProgressStage=stage,ProgressText=text,IsProgressIndeterminate=busy,IsMeasurementInProgress=busy&&!_stopping};Changed?.Invoke(this,EventArgs.Empty);}
        public async Task ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION action,CManualInspectionUiRequest request,CancellationToken cancellationToken=default)
        {
            Calls.Add(action);
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.Stop)
            {_stopping=true;State(true,"STOPPING","중지 정리 중");await CleanupFinish.Task;MeasurementFinish.TrySetResult();State(false,"IDLE","");return;}
            State(true,"MEASURING","측정 응답 대기");await MeasurementFinish.Task;
            if(_stopping)await CleanupFinish.Task;
            else State(false,"IDLE","");
        }
    }
    private static void RunDelayedCurrentPointChecks(List<object> observations)
    {
        LoadResources("Dark");var state=Snapshot(80) with{StageXmm=0,StageYmm=0};var fake=new DelayedPresentationController(state);var view=new CManualInspectionView{DataContext=fake};var window=HostRepresentativeView(view,1120,560);
        try
        {
            var confirms=0;view.ConfirmationForTesting=(_,_,_)=>{confirms++;return true;};Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[1].Key],state.Points[1].Key));
            ((Button)view.FindName("MeasureCurrentButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            ((Expander)view.FindName("InspectorSection")).IsExpanded=false;((Expander)view.FindName("LeftInspectorSection")).IsExpanded=false;Pump();
            var progress=(ProgressBar)view.FindName("JobProgressBar");var panel=(FrameworkElement)view.FindName("JobProgressPanel");
            Test("performance_current_point_wait_is_visible_with_panels_closed",panel.IsVisible&&progress.IsVisible&&progress.IsIndeterminate&&((TextBlock)view.FindName("JobProgressText")).Text.Contains("측정 응답 대기"));
            Test("performance_current_point_has_no_motion_confirmation_or_extra_command",confirms==0&&fake.Calls.SequenceEqual([EN_MANUAL_INSPECTION_UI_ACTION.Measure]));
            Test("performance_wait_disables_reentry_and_keeps_stop_accessible",!((Button)view.FindName("MeasureCurrentButton")).IsEnabled&&((Button)view.FindName("StopButton")).IsEnabled);
            var pulses=0;var heartbeat=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(10)};heartbeat.Tick+=(_,_)=>pulses++;heartbeat.Start();var timer=Stopwatch.StartNew();
            while(timer.ElapsedMilliseconds<140){Pump();Thread.Sleep(2);}heartbeat.Stop();
            Test("performance_ui_dispatcher_heartbeat_runs_during_fake_async_wait",pulses>=3,pulses);
            ((Button)view.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("performance_stop_cleanup_remains_busy_and_indeterminate",panel.IsVisible&&progress.IsIndeterminate&&((TextBlock)view.FindName("JobProgressText")).Text.Contains("정리")&&!((Button)view.FindName("MeasureCurrentButton")).IsEnabled);
            fake.CleanupFinish.TrySetResult();var cleanup=Stopwatch.StartNew();while((fake.Snapshot.IsBusy||Field<int>(view,"_requests")>0)&&cleanup.ElapsedMilliseconds<1500){Pump();Thread.Sleep(2);}Pump();
            Test("performance_progress_clears_only_after_cleanup",!panel.IsVisible&&!progress.IsIndeterminate&&!fake.Snapshot.IsBusy&&Field<int>(view,"_requests")==0);
            Test("performance_stop_uses_existing_single_stop_action",fake.Calls.Count(action=>action==EN_MANUAL_INSPECTION_UI_ACTION.Stop)==1);
            observations.Add(new{type="fake-asynchronous-current-measure",heartbeatPulses=pulses,confirmedDialogs=confirms,sourceCount=80,noDeviceLatencyClaim=true});
        }
        finally{fake.CleanupFinish.TrySetResult();fake.MeasurementFinish.TrySetResult();Pump();window.Close();Pump();}
    }

    private static void RunProgressLayouts(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})
        {
            LoadResources(theme);var state=Snapshot(80) with{IsSimulation=true,IsBusy=true,ProgressStage="MEASURING",ProgressText="측정 응답 대기",IsProgressIndeterminate=true,CompletedCount=0,TotalCount=1};var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var window=HostRepresentativeView(view,theme=="Dark"?1120:1490,theme=="Dark"?560:900);
            try
            {
                ((Expander)view.FindName("InspectorSection")).IsExpanded=false;((Expander)view.FindName("LeftInspectorSection")).IsExpanded=false;Pump();
                var bar=(ProgressBar)view.FindName("JobProgressBar");var panel=(FrameworkElement)view.FindName("JobProgressPanel");var map=(CManualInspectionMapControl)view.FindName("PositionMap");var toolbar=(FrameworkElement)view.FindName("ToolbarPanel");
                var bounds=panel.TransformToAncestor(toolbar).TransformBounds(new Rect(panel.RenderSize));
                Test("performance_toolbar_progress_fits_without_overlay_"+theme,panel.IsVisible&&bounds.Top>=-1&&bounds.Bottom<=toolbar.ActualHeight+1&&bounds.Left>=-1&&bounds.Right<=toolbar.ActualWidth+1&&map.ActualWidth>=350&&map.ActualHeight>=150);
                Test("performance_theme_and_no_external_scroll_preserved_"+theme,ResourceColor("PanelBackgroundBrush")== (theme=="Dark"?"#FF20262D":"#FFFFFFFF")&&((ScrollViewer)view.FindName("WorkspaceScroll")).ComputedVerticalScrollBarVisibility==Visibility.Collapsed);
                Render(view,"progress-wait-"+theme.ToLowerInvariant()+".png");
                fake.Replace(state with{Version=8,TotalCount=10,CompletedCount=4,IsProgressIndeterminate=false,ProgressText="측정 중"});Pump();
                Test("performance_batch_uses_real_completed_count_"+theme,!bar.IsIndeterminate&&bar.Value==40&&((TextBlock)view.FindName("JobProgressText")).Text.Contains("4/10"));
                fake.Replace(state with{Version=9,ProgressStage="SAVING",ProgressText="CSV 저장 중",IsProgressIndeterminate=true});Pump();
                Test("performance_file_wait_does_not_reuse_old_operation_counts_"+theme,bar.IsIndeterminate&&((TextBlock)view.FindName("JobProgressText")).Text.Contains("CSV 저장 중")&&!((TextBlock)view.FindName("JobProgressText")).Text.Contains("/"));
                fake.Replace(state with{Version=10,IsBusy=false,ProgressStage="IDLE",ProgressText="",IsProgressIndeterminate=false});Pump();
                Test("performance_idle_hides_busy_indicator_and_restores_status_"+theme,!panel.IsVisible&&((TextBlock)view.FindName("StatusText")).IsVisible&&!bar.IsIndeterminate);
                fake.Replace(state with{Version=11,ProgressStage="IDLE",ProgressText="",TotalCount=0,IsProgressIndeterminate=false});Pump();
                Test("performance_legacy_busy_snapshot_uses_indeterminate_fallback_"+theme,bar.IsIndeterminate&&((TextBlock)view.FindName("JobProgressText")).Text.Contains("처리 중"));
                observations.Add(new{theme,map.ActualWidth,map.ActualHeight,toolbarHeight=toolbar.ActualHeight});
            }finally{window.Close();Pump();}
        }
    }

    private static void RunExistingMapLayoutChecks()
    {
        var map=new CManualInspectionMapControl();
        map.Measure(new Size(300,200));map.Arrange(new Rect(0,0,300,200));
        Test("performance_original_map_preserves_minheight_and_stretch",map.DesiredSize.Height>=map.MinHeight&&map.RenderSize==new Size(300,200));
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(FrameworkElement.WidthProperty,73d);border.SetValue(FrameworkElement.HeightProperty,42d);
        map.Template=new ControlTemplate(typeof(CManualInspectionMapControl)){VisualTree=border};map.ApplyTemplate();map.Measure(new Size(300,200));map.Arrange(new Rect(0,0,300,200));
        Test("performance_original_map_preserves_optional_template_measure_and_child",VisualTreeHelper.GetChildrenCount(map)==1&&VisualTreeHelper.GetChild(map,0) is Border template&&template.DesiredSize==new Size(73,42)&&map.DesiredSize.Width==73&&map.DesiredSize.Height>=map.MinHeight&&map.RenderSize==new Size(300,200));
    }
}
