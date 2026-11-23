using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Drilling.UI.Views;

internal sealed class FakeController(CManualInspectionUiSnapshot snapshot) : IManualInspectionController
{
    public CManualInspectionUiSnapshot Snapshot { get; private set; } = snapshot;
    public event EventHandler? Changed;
    public List<(EN_MANUAL_INSPECTION_UI_ACTION Action, CManualInspectionUiRequest Request)> Calls { get; } = [];
    public Action<EN_MANUAL_INSPECTION_UI_ACTION,CManualInspectionUiRequest>? OnExecute { get; set; }
    public Task ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest request, CancellationToken cancellationToken = default)
    { Calls.Add((action, request)); OnExecute?.Invoke(action,request); return Task.CompletedTask; }
    public void Replace(CManualInspectionUiSnapshot value) { Snapshot = value; Changed?.Invoke(this, EventArgs.Empty); }
}

internal static partial class Program
{
    private static readonly List<object> Checks = [];
    private static string _output = "";
    private static string _uiSource = "";
    private static Application _application = null!;
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] is not ("--integration-impact" or "--sync-levels" or "--coverage-compact" or "--hardening" or "--visual-usability" or "--map-first" or "--cell-overview" or "--offset-init" or "--measurement-workflow" or "--bulk-history")) { Console.Error.WriteLine("Usage: UiHarness <repository/Drilling.UI> <private-output> [--integration-impact|--sync-levels|--coverage-compact|--hardening|--visual-usability|--map-first|--cell-overview|--offset-init|--measurement-workflow|--bulk-history]"); return 2; }
        _uiSource = Path.GetFullPath(args[0]); _output = Path.GetFullPath(args[1]); Directory.CreateDirectory(_output);
        _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Length == 3) return args[2] switch { "--sync-levels" => SyncLevels(), "--coverage-compact" => CoverageCompact(), "--hardening" => Hardening(), "--visual-usability" => VisualUsability(), "--map-first" => MapFirst(), "--cell-overview" => CellOverview(), "--offset-init" => OffsetInit(), "--measurement-workflow" => MeasurementWorkflow(), "--bulk-history" => BulkHistory(), _ => IntegrationImpact() };
        try
        {
            LoadResources("Light");
            var fake = new FakeController(Snapshot(240));
            var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_,_,_) => true };
            Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
            var window = new Window { Width = 1490, Height = 1060, Content = view, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show(); Pump(); view.UpdateLayout();
            // This legacy per-Cell regression deliberately enters Cell 1. The new all-Cell
            // landing view and actual Recipe placement are tested independently below.
            Invoke(view, "SetMapCellScope", 1); Pump();
            Test("native_view_loaded", view.IsLoaded);
            Test("controller_attached", Field<IManualInspectionController>(view, "_controller") == fake);
            Test("loaded_recipe_context_is_visible", ((TextBlock)view.FindName("ContextText")).Text.Contains("Recipe A3_QA"));
            Test("combo_uses_visible_label", ((ComboBox)view.FindName("CellFilter")).SelectionBoxItem.ToString() == "Cell 1");
            Render(view, "native-light.png");
            Test("theme_light_background", ResourceColor("PanelBackgroundBrush") == "#FFFFFFFF");
            LoadResources("Dark"); Pump(); view.InvalidateVisual(); view.UpdateLayout(); Render(view, "native-dark.png");
            Test("theme_dark_background", ResourceColor("PanelBackgroundBrush") == "#FF20262D");
            ((Expander)view.FindName("CorrectionSection")).IsExpanded = true; Pump(); ((FrameworkElement)view.FindName("CorrectionSection")).BringIntoView(); Pump(); Render(view, "native-correction-dark.png");
            ((Expander)view.FindName("ScatterSection")).IsExpanded = true; Pump(); ((FrameworkElement)view.FindName("ScatterPlot")).BringIntoView(); Pump(); Render(view, "native-scatter-dark.png");
            ((Expander)view.FindName("ResultsSection")).IsExpanded = true; Pump(); ((FrameworkElement)view.FindName("ResultGrid")).BringIntoView(); Pump(); Render(view, "native-results-dark.png");
            ((ScrollViewer)view.FindName("WorkspaceScroll")).ScrollToTop(); Pump();
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            var scatter = (CManualInspectionScatterControl)view.FindName("ScatterPlot");
            Test("drawn_map_geometry", ((IList)Private(map, "_hits")).Count > 0);
            var originalSlots = Slots(map);
            Test("scatter_current_cell_independent", scatter.Metrics.Total == 80);
            Test("scatter_pop_sigma", Math.Abs(scatter.Metrics.SigmaX!.Value - Math.Sqrt(5.25)) < 1e-8);
            map.ZoomAt(1.25, new Point(170, 190)); Pump();
            Test("zoom_is_view_only", Math.Abs(map.Zoom - 1.25) < 1e-10 && fake.Calls.Count == 0);
            for (var n = 0; n < 40; n++) map.ZoomAt(1.25, new Point(100, 100));
            Test("zoom_upper_bound", map.Zoom == 12);
            for (var n = 0; n < 40; n++) map.ZoomAt(0.8, new Point(100, 100));
            Test("zoom_lower_bound", map.Zoom == 0.4); map.ResetView();
            Test("fit_reset", map.Zoom == 1);
            var callsBeforePan = fake.Calls.Count;
            map.PanBy(new Vector(40, -40)); Pump();
            Test("pan_is_visual_only", Field<Vector>(map, "_pan") == new Vector(40, -40) && fake.Calls.Count == callsBeforePan);
            map.ResetView(); Pump();
            var key1 = fake.Snapshot.Points[0].Key; var key2 = fake.Snapshot.Points[1].Key;
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([key1], key1));
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([key2], key2, true, true));
            Test("ctrl_multiple_selection", Field<HashSet<string>>(view, "_selected").SetEquals([key1, key2]));
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([key1], key1, true, true));
            Test("ctrl_toggle_selection", Field<HashSet<string>>(view, "_selected").SetEquals([key2]));
            map.SelectRectangle(new Rect(0, 0, map.ActualWidth, map.ActualHeight), false); Pump();
            Test("rectangle_selection_all_visible", Field<HashSet<string>>(view, "_selected").Count == 80);
            map.SelectRectangle(new Rect(-100, -100, 1, 1), false); Pump();
            Test("empty_rectangle_clears_selection", Field<HashSet<string>>(view, "_selected").Count == 0);
            var selected = Field<HashSet<string>>(view, "_selected"); selected.Add(key1); Invoke(view, "Refresh");
            map.ShowOperationMenu(); Pump();
            Test("context_retains_two_motion_operations_without_auto_correction", map.ContextMenu!.Items.OfType<MenuItem>().Count(x => x.IsEnabled && x.Header.ToString()!.StartsWith("이동", StringComparison.Ordinal)) == 2);
            map.ContextMenu.IsOpen = false;
            fake.Replace(fake.Snapshot with { Version = 8, IsBusy = true }); Pump(); map.ShowOperationMenu();
            Test("context_busy_disabled", map.ContextMenu!.Items.OfType<MenuItem>().All(x => !x.IsEnabled)); map.ContextMenu.IsOpen = false;
            fake.Replace(fake.Snapshot with { Version = 9, IsBusy = false, IsReadOnly = true }); Pump(); map.ShowOperationMenu();
            Test("context_legacy_readonly_flag_does_not_disable_motion", map.ContextMenu!.Items.OfType<MenuItem>().Where(x => x.Header.ToString()!.StartsWith("이동", StringComparison.Ordinal) || x.Header.ToString()!.Contains("현재 위치")).All(x => x.IsEnabled)); map.ContextMenu.IsOpen = false;
            fake.Replace(fake.Snapshot with { Version = 10, IsReadOnly = false }); Pump();
            Test("table_page_50", ((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Count() == 50);
            Invoke(view, "SelectNg_Click", view, new RoutedEventArgs());
            Test("visible_ng_selection_only", Field<HashSet<string>>(view, "_selected").All(k => fake.Snapshot.Points.Single(p => p.Key == k).WorkingJudge == "NG"));
            Invoke(view, "ClearSelection_Click", view, new RoutedEventArgs());
            Test("clear_selection", Field<HashSet<string>>(view, "_selected").Count == 0);
            Set(view, "_judge", "NG"); Invoke(view, "Refresh"); Pump();
            Test("ng_filter_does_not_shrink_scatter", scatter.Metrics.Total == 80);
            Test("ng_filter_leaves_slots", Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_all").Count == 80 && Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_visible").Count < 80);
            Test("filter_keeps_exact_point_coordinates", Slots(map).All(p => originalSlots[p.Key] == p.Value));
            Set(view, "_scatterScope", "HEAD"); Set(view, "_scatterHead", 1); Invoke(view, "Refresh");
            Test("head_scope_across_cells", scatter.Metrics.Total == 120);
            Set(view, "_scatterScope", "CELLS"); var cells = Field<HashSet<int>>(view, "_scatterCells"); cells.Clear(); cells.UnionWith([1, 3]); Invoke(view, "Refresh");
            Test("selected_cells_scope", scatter.Metrics.Total == 160);
            Set(view, "_scatterOriginal", true); Invoke(view, "Refresh");
            Test("original_scatter_drops_proxy_mark", scatter.Metrics.ProxyCount == 0);
            Set(view, "_judge", "ALL"); Set(view, "_cell", 0); Invoke(view, "Refresh");
            var before = fake.Calls.Count; ((Button)view.FindName("LoadRecipeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Test("refresh_request_uses_controller", fake.Calls.Count == before + 1 && fake.Calls[^1].Action == EN_MANUAL_INSPECTION_UI_ACTION.Refresh);
            Test("request_has_current_version", fake.Calls[^1].Request.ExpectedVersion == fake.Snapshot.Version);
            Test("real_mode_has_no_injection_ui", view.FindName("SimulationOptions") is null);
            var callsBeforeStale = fake.Calls.Count;
            ((Task)Invoke(view, "ConfirmOperation", EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, new[] { key1 }, fake.Snapshot.Version - 1, "P2P")!).GetAwaiter().GetResult();
            Test("stale_context_rejected_before_dialog", fake.Calls.Count == callsBeforeStale && ((TextBlock)view.FindName("MessageText")).Text.Contains("바뀌"));
            var masked = fake.Snapshot.Points.Select((p, index) => index == 0 ? p with { IsMasked = true } : p).ToArray();
            fake.Replace(fake.Snapshot with { Version = 11, Points = masked }); Pump();
            Set(view, "_cell", 1); Invoke(view, "Refresh"); Pump(); map.SelectRectangle(new Rect(0, 0, map.ActualWidth, map.ActualHeight), false); Pump();
            Test("rectangle_skips_masking", Field<HashSet<string>>(view, "_selected").Count == 79 && !Field<HashSet<string>>(view, "_selected").Contains(key1));
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([key1], key1));
            Test("masked_click_cannot_select", Field<HashSet<string>>(view, "_selected").Count == 0);
            Set(view, "_focusKey", fake.Snapshot.Points[23].Key); Invoke(view, "Refresh"); Invoke(view, "SetReference_Click", view, new RoutedEventArgs());
            Test("proxy_cannot_be_representative", Field<string?>(view, "_representativeKey") is null);
            Set(view, "_focusKey", fake.Snapshot.Points[11].Key); Invoke(view, "Refresh"); Invoke(view, "SetReference_Click", view, new RoutedEventArgs());
            Test("direct_ng_can_be_representative", Field<string?>(view, "_representativeKey") == fake.Snapshot.Points[11].Key);
            Test("representative_does_not_mutate_original", fake.Snapshot.Points[11].OriginalJudge == "OK" && fake.Snapshot.Points[11].WorkingJudge == "NG");
            var reference = fake.Snapshot.Points[11] with { BaseXUm = 4, BaseYUm = 5, WorkingXUm = 2, WorkingYUm = 3, CandidateXUm = 70, CandidateYUm = 80 };
            fake.Replace(fake.Snapshot with { Version = 12, CandidateStatus = "DRAFT" });
            var candidateF = ((double?, double?))Invoke(view, "ReferenceValues", reference)!;
            Test("representative_uses_pending_final_F", candidateF == (70.0, 80.0));
            fake.Replace(fake.Snapshot with { Version = 13, CandidateStatus = "APPLIED" });
            var appliedF = ((double?, double?))Invoke(view, "ReferenceValues", reference)!;
            Test("applied_candidate_not_reused_as_reference_F", appliedF == (6.0, 8.0));
            var originalChange = fake.Snapshot.Points.Select((p, index) => index == 1 ? p with { OriginalXUm = 100 } : p).ToArray();
            fake.Replace(fake.Snapshot with { Version = 14, Points = originalChange });
            Set(view, "_scatterScope", "CELL"); Set(view, "_scatterOriginal", false); Invoke(view, "Refresh"); var workingMean = scatter.Metrics.MeanX;
            Set(view, "_scatterOriginal", true); Invoke(view, "Refresh");
            Test("original_scatter_uses_preserved_values", scatter.Metrics.MeanX > workingMean + 1);
            Test("masked_scatter_exclusion_count", scatter.Metrics.Masked == 1 && scatter.Metrics.Count == 79);
            fake.Replace(Snapshot(10000) with { Version = 20, RunId = "large", IsSimulation = true });
            Test("new_run_resets_scatter_reference_selection", Field<string>(view, "_scatterScope") == "HEAD" && !Field<bool>(view, "_scatterOffsets") && !Field<bool>(view, "_scatterOriginal") && Field<string?>(view, "_representativeKey") is null && Field<HashSet<string>>(view, "_selected").Count == 0);
            var watch = Stopwatch.StartNew(); Pump(); Set(view, "_cell", 0); Invoke(view, "Refresh"); view.UpdateLayout(); Render(view, "native-10000-dark.png"); watch.Stop();
            Test("ten_thousand_points_retained", fake.Snapshot.Points.Count == 10000 && Field<Dictionary<string, CManualInspectionUiPoint>>(view, "_points").Count == 10000);
            Test("ten_thousand_fit_does_not_crop_groups", ((IList)Private(map, "_hits")).Count == 10000, new { hits = ((IList)Private(map, "_hits")).Count });
            Test("ten_thousand_native_layout_completes", watch.Elapsed.TotalSeconds < 12, new { milliseconds = watch.Elapsed.TotalMilliseconds });
            Test("sim_mode_has_no_injection_ui", view.FindName("SimulationOptions") is null);
            window.Width = 1120; window.Height = 820; Pump(); view.UpdateLayout(); Render(view, "native-1120-dark.png");
            Test("compact_native_layout_finite", double.IsFinite(map.ActualWidth) && map.ActualWidth > 200 && scatter.ActualWidth > 150);
            window.Close(); _application.Shutdown();
        }
        catch (Exception ex) { Test("unhandled_ui_failure", false, ex.ToString()); }
        var result = new { schemaVersion = "a3-manual-inspection-native-ui/1", passed = _failures == 0, tests = Checks.Count, failures = _failures,
            scope = "Standalone STA WPF view with fake controller and current application theme/styles. No equipment app, vendor DLL, controller, laser or source script execution.", checks = Checks };
        File.WriteAllText(Path.Combine(_output, "ui-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { result.passed, result.tests, result.failures })); return _failures == 0 ? 0 : 1;
    }
    private static int IntegrationImpact()
    {
        try
        {
            LoadResources("Dark");
            var initial = Snapshot(80);
            var points = initial.Points.Select((p, i) => i == 1 ? p with { Source = "IMPORTED", IsProxy = false } : p).ToArray();
            var fake = new FakeController(initial with { Version = 41, Points = points });
            var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_,_,_) => true };
            Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
            var window = new Window { Width = 1490, Height = 900, Content = view, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show(); Pump();
            Test("arrival_label_not_live_motor_feedback", ((TextBlock)view.FindName("StagePositionText")).Text.Contains("마지막 도착 확인"));
            Test("unknown_arrival_shown_as_dash", ((TextBlock)view.FindName("StagePositionText")).Text.Contains("—") && !((TextBlock)view.FindName("StagePositionText")).Text.Contains("NaN"));
            var filter = (ComboBox)view.FindName("SourceFilter");
            var importedChoice = filter.Items.Cast<object>().SingleOrDefault(x => (string?)x.GetType().GetProperty("Value")!.GetValue(x) == "IMPORTED");
            Test("imported_filter_choice_present", importedChoice is not null);
            filter.SelectedItem = importedChoice; Pump();
            Test("imported_filter_exact_source", Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered").Select(p => p.Key).SequenceEqual([points[1].Key]));
            Test("imported_not_a_native_reference", !points[1].CanBeReference && points[1].Source=="IMPORTED"&&points[1].SourceText.Contains("불러온"));
            var color = (ComboBox)view.FindName("ColorFilter");
            color.SelectedItem = color.Items.Cast<object>().Single(x => (string?)x.GetType().GetProperty("Value")!.GetValue(x) == "level"); Pump();
            Test("imported_result_does_not_invent_active_offset", ((SolidColorBrush)CManualInspectionDrawing.PointBrush(view, points[1] with { HasAppliedOffset = false }, "applied", fake.Snapshot)).Color.ToString() == "#FFDBE4EA");
            var legend = (WrapPanel)view.FindName("MapLegendItems");
            Test("measurement_legend_marks_unmeasured_and_initialized", legend.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).Any(t => t.Text.Contains("유효 측정 없음")||t.Text.Contains("초기화")));
            Test("imported_table_preserves_original", points[1].OriginalJudge == "OK" && points[1].OriginalXUm == initial.Points[1].OriginalXUm);
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            // Map-first has no implicit work target when focus/selection is empty.
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([points[1].Key], points[1].Key));
            map.ShowOperationMenu(); Pump();
            map.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("상세")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Test("raw_history_uses_controller_full_history", fake.Calls.Count == 1 && fake.Calls[0].Action == EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint && fake.Calls[0].Request.FocusKey == points[1].Key);
            Test("raw_history_request_current_version", fake.Calls[0].Request.ExpectedVersion == 41);
            fake.Replace(fake.Snapshot with { Version = 42, IsBusy = true }); Pump();
            map.ShowOperationMenu(); Pump();
            Test("busy_history_disabled_consistent_with_serial_controller", !map.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("상세")).IsEnabled); map.ContextMenu.IsOpen = false;
            fake.Replace(fake.Snapshot with { Version = 43, IsBusy = false }); Pump();
            var reference = points[2] with { BaseXUm = 13, BaseYUm = 4, WorkingXUm = 3, WorkingYUm = 2, CandidateXUm = 70, CandidateYUm = 80, ReferenceFinalXUm = 13, ReferenceFinalYUm = 4 };
            var resolved = ((double?, double?))Invoke(view, "ReferenceValues", reference)!;
            Test("consumed_raw_uses_engine_resolved_F", resolved == (13.0, 4.0));
            var partial = ((double?, double?))Invoke(view, "ReferenceValues", reference with { ReferenceFinalYUm = null })!;
            Test("partial_resolved_F_does_not_guess_other_axis", partial == (13.0, null));
            fake.Replace(fake.Snapshot with { Version = 44, CandidateStatus = "Applied" });
            var legacy = ((double?, double?))Invoke(view, "ReferenceValues", reference with { ReferenceFinalXUm = null, ReferenceFinalYUm = null })!;
            Test("candidate_status_case_insensitive", legacy == (16.0, 6.0));
            var extraBeam = points[1] with { Key = "IMPORTED-B12", BeamNo = 12 };
            var mixed = points.Select((p, i) => i == 3 ? p with { IsMasked = true } : p).Append(extraBeam).ToArray();
            fake.Replace(fake.Snapshot with { Version = 45, Points = mixed });
            var flying = (string[])Invoke(view, "ExpandFlyingKeys", (object)new[] { points[1].Key })!;
            Test("flying_keeps_selected_Beam_row", flying.Length == 4 && flying.All(k => mixed.Single(p => p.Key == k).BeamNo == 11));
            Test("flying_excludes_masked_in_same_row", !flying.Contains(points[3].Key));
            Test("level_names_trimmed", CManualInspectionView.ValidatedLevelNames([" A ", "B", "C", "D"]).SequenceEqual(["A", "B", "C", "D"]));
            Test("duplicate_trimmed_level_names_rejected", Rejects(() => CManualInspectionView.ValidatedLevelNames(["A", " A ", "C", "D"])));
            Test("long_level_names_rejected", Rejects(() => CManualInspectionView.ValidatedLevelNames([new string('A', 21), "B", "C", "D"])));
            Test("blank_level_names_rejected", Rejects(() => CManualInspectionView.ValidatedLevelNames([" ", "B", "C", "D"])));
            Test("impact_checks_send_no_motion_or_correction", fake.Calls.All(c => c.Action == EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint));
            Invoke(view, "Files_Click", view, new RoutedEventArgs());
            var files = ((Button)view.FindName("FilesButton")).ContextMenu!;
            Test("real_profile_has_no_SIM_fixture_action", files.Items.OfType<MenuItem>().All(item => !item.Header.ToString()!.Contains("SIM")));
            files.IsOpen = false;
            fake.Replace(fake.Snapshot with { Version = 46, IsSimulation = true });
            Invoke(view, "Files_Click", view, new RoutedEventArgs()); files = ((Button)view.FindName("FilesButton")).ContextMenu!;
            Test("SIM_profile_has_no_fixture_action", files.Items.OfType<MenuItem>().All(item => !item.Header.ToString()!.Contains("SIM")));
            files.IsOpen = false;
            Render(view, "native-imported-filter-dark.png");
            window.Close(); _application.Shutdown();
        }
        catch (Exception ex) { Test("integration_impact_unhandled", false, ex.ToString()); }
        var result = new { schemaVersion = "a3-manual-inspection-ui-integration-impact/1", passed = _failures == 0, tests = Checks.Count, failures = _failures,
            scope = "Targeted native UI changes only; the prior49 suite is not repeated. Imported source, same resolved F, Beam row expansion, history dispatch, level-name policy. Fake controller only.", checks = Checks };
        File.WriteAllText(Path.Combine(_output, "ui-integration-impact-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { result.passed, result.tests, result.failures })); return _failures == 0 ? 0 : 1;
    }
    private static bool Rejects(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    private static int SyncLevels()
    {
        try
        {
            LoadResources("Dark");
            var initial=Snapshot(160) with { Version=100, SynchronizationText="최근 Rough/Fine 결과를 자동 반영했습니다.", ImportedResultCount=2 };
            var fake=new FakeController(initial);
            var confirmations=new List<(string Title,string Description,string Button)>();
            var accept=false;
            var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(title,description,button)=>{confirmations.Add((title,description,button));return accept;}};
            Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
            var window=new Window{Width=1490,Height=1060,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
            window.Show();Pump();
            var map=(CManualInspectionMapControl)view.FindName("PositionMap");
            Test("default_color_is_measurement_deviation_level",Field<string>(view,"_color")=="level");
            Test("only_level_and_judge_color_concepts",((ComboBox)view.FindName("ColorFilter")).Items.Count==2);
            Test("advanced_sections_initially_closed",new[]{"ReferenceSection","CorrectionSection","SettingsSection","ScatterSection","ResultsSection","PointDetailsSection"}.All(n=>!((Expander)view.FindName(n)).IsExpanded));
            Test("simulation_and_duplicate_motion_controls_removed",new[]{"SimulationOptions","SimulationDemoButton","MoveButton","MoveMeasureCorrectButton","SideStopButton","PanToolButton","LoadResultButton"}.All(n=>view.FindName(n)is null));
            Test("automatic_synchronization_description_reachable",((TextBlock)view.FindName("StatusText")).ToolTip?.ToString()?.Contains(initial.SynchronizationText)==true);
            fake.Replace(initial with{Version=101,SyncPending=true});Pump();
            Test("deferred_synchronization_is_explicit",((TextBlock)view.FindName("StatusText")).Text.Contains("동기화 대기")
                &&((TextBlock)view.FindName("StatusText")).ToolTip?.ToString()?.Contains("새 결과 대기")==true);
            var sample=initial.Points[2] with{HasAppliedOffset=true,BaseXUm=7,BaseYUm=-2,WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null};
            string Brush(CManualInspectionUiPoint p,string mode="applied",CManualInspectionUiSnapshot? snapshot=null)=>((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,p,mode,snapshot??initial)).Color.ToString();
            var originSamples=new[]{sample with{CellNo=1,HeadNo=1,DisplayedMeasurementOrigin="ROUGH"},sample with{CellNo=2,HeadNo=8,DisplayedMeasurementOrigin="FINE"},sample with{CellNo=3,HeadNo=4,IsProxy=true,Source="MANUAL"}};
            Test("same_applied_level_across_cells_heads_and_sources",originSamples.Select(p=>Brush(p)).Distinct().Single()=="#FFB88B2A");
            Test("unmeasured_but_applied_point_has_level",CManualInspectionDrawing.AppliedLevel(sample,initial)==1);
            Test("applied_zero_is_level_one",Brush(sample with{BaseXUm=0,BaseYUm=0})=="#FF2E9076");
            Test("missing_offset_is_gray_not_false_zero",Brush(sample with{HasAppliedOffset=false,BaseXUm=0,BaseYUm=0})=="#FFDBE4EA");
            Test("draft_final_never_colors_active_map",Brush(sample with{BaseXUm=1,CandidateXUm=99,CandidateYUm=99})=="#FF2E9076");
            Test("active_level_keeps_ng_red_before_applying_level_palette",Brush(sample with{WorkingJudge="NG"})=="#FFD16776"&&new[]{"OK","UNMEASURED"}.Select(j=>Brush(sample with{WorkingJudge=j})).Distinct().Count()==1);
            Test("measurement_error_remains_distinct_from_successful_history",Brush(sample with{WorkingJudge="ERROR",WorkingXUm=1,WorkingYUm=1},"level")=="#FFD39A45");
            Test("measurement_and_applied_share_inclusive_bounds",Brush(sample with{WorkingJudge="OK",WorkingXUm=5,WorkingYUm=0},"level")==Brush(sample with{BaseXUm=5,BaseYUm=0}));
            Test("level_upper_ranges_distinct",new[]{5d,10d,20d,20.001}.Select(x=>Brush(sample with{BaseXUm=x,BaseYUm=0})).Distinct().Count()==4);
            Test("radial_metric_uses_common_policy",Brush(sample with{BaseXUm=4,BaseYUm=4},snapshot:initial with{LevelMetric="RADIAL"})=="#FFB88B2A"&&Brush(sample with{BaseXUm=4,BaseYUm=4})=="#FF2E9076");
            Test("invalid_level_policy_fails_to_gray",Brush(sample,snapshot:initial with{LevelBoundsUm=[5,5,20]})=="#FFDBE4EA");
            var snapshot=initial with{Version=102,Points=initial.Points.Select((p,i)=>i==0?p with{DisplayedMeasurementOrigin="FINE · 기타 메뉴",MeasurementSourcePath="synthetic/Fine.csv",HasAppliedOffset=true,AppliedLevel=1,BaseXUm=7}:p).ToArray()};
            fake.Replace(snapshot);Pump();Set(view,"_focusKey",snapshot.Points[0].Key);Invoke(view,"Refresh");
            Test("point_details_show_external_measurement_origin",((TextBlock)view.FindName("PointDetailsText")).Text.Contains("FINE · 기타 메뉴")&&((TextBlock)view.FindName("PointDetailsText")).Text.Contains("synthetic/Fine.csv"));
            var appliedDetails = (TextBlock)view.FindName("PointAppliedText");
            Test("point_details_prioritize_measurement_deviation_and_judge",((TextBlock)view.FindName("PointJudgesText")).Text.Contains("NG")&&((TextBlock)view.FindName("PointErrorText")).Text.Contains(snapshot.Points[0].MeasurementErrorText)&&appliedDetails.Visibility==Visibility.Collapsed);
            var before=fake.Calls.Count;
            ((Button)view.FindName("LoadRecipeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("refresh_dispatches_immediately_without_confirmation",fake.Calls.Count==before+1&&confirmations.Count==0);
            Test("refresh_description_still_explains_no_machine_effect",CManualInspectionView.DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION.Refresh,snapshot,0).Contains("이동하지"));
            accept=true;((Button)view.FindName("LoadRecipeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("refresh_immediate_keeps_expected_version",fake.Calls.Count==before+2&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.Refresh&&fake.Calls.Last().Request.ExpectedVersion==102);
            accept=false;var key=snapshot.Points[1].Key;Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([key],key));
            ((Task)Invoke(view,"ConfirmOperation",EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect,new[]{key},102L,"P2P")!).GetAwaiter().GetResult();
            Test("removed_measurement_correction_explains_separate_bulk_without_dialog",confirmations.Count==0&&((TextBlock)view.FindName("MessageText")).Text.Contains("일괄"));
            Test("operation_cancel_sends_no_device_request",fake.Calls.Count==before+2);
            accept=true;map.ShowOperationMenu();Pump();map.ContextMenu!.Items.OfType<MenuItem>().Single(i=>i.Header.ToString()=="이동 후 측정").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
            Test("context_measurement_uses_selected_keys",fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure&&fake.Calls.Last().Request.Keys.SequenceEqual([key]));
            var callCount=fake.Calls.Count;((Button)view.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("stop_is_immediate_without_confirmation",fake.Calls.Count==callCount+1&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.Stop);
            Test("calculate_description_does_not_claim_saved",CManualInspectionView.DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION.Calculate,snapshot,3).Contains("저장하지"));
            Test("retry_description_no_implicit_correction",CManualInspectionView.DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining,snapshot with{RemainingCount=3},0).Contains("재개하지"));
            var recent=new CManualInspectionUiResultFile("synthetic/other-fine.csv","Fine · synthetic", "G",DateTimeOffset.UtcNow,"FINE");
            fake.Replace(snapshot with{Version=103,RecentResults=[recent]});accept=true;
            Invoke(view,"Files_Click",view,new RoutedEventArgs());var menu=((Button)view.FindName("FilesButton")).ContextMenu!;
            menu.Items.OfType<MenuItem>().Single(x=>x.Header.ToString()!.Contains("파일 불러오기")&&!x.Header.ToString()!.Contains("전 작업")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
            Test("file_load_delegates_picker_and_keeps_current_version",fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile&&fake.Calls.Last().Request.ResultPath is null&&fake.Calls.Last().Request.ExpectedVersion==103);
            Test("file_load_remains_immediate_and_does_not_request_motion_confirmation",fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile&&confirmations.Last().Title.Contains("이동"));
            Test("more_menu_has_return_portable_offsets_and_results_without_session_or_recent_list",menu.Items.OfType<MenuItem>().Count()==5&&menu.Items.OfType<MenuItem>().Count(x=>x.Header.ToString()!.Contains("통합 Offset"))==2&&menu.Items.OfType<MenuItem>().All(x=>!x.Header.ToString()!.Contains("세션")&&!x.Header.ToString()!.Contains("최근")&&!x.Header.ToString()!.Contains("SIM")));menu.IsOpen=false;
            var oldLevel=CManualInspectionDrawing.AppliedLevel(sample,initial);var changed=initial with{LevelBoundsUm=[10,20,30]};
            Test("new_common_bounds_recolor_existing_offsets",oldLevel==1&&CManualInspectionDrawing.AppliedLevel(sample,changed)==0);
            fake.Replace(snapshot with{Version=104,LevelBoundsUm=[10,20,30]});Pump();Set(view,"_focusKey",snapshot.Points[0].Key);Invoke(view,"Refresh");
            var firstRow=((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().First();
            Test("table_and_detail_use_same_current_level_scale",firstRow.GetType().GetProperty("AppliedLevelText")!.GetValue(firstRow)?.ToString()=="L1"&&((TextBlock)view.FindName("PointJudgesText")).Text.Contains("NG"));
            Set(view,"_focusKey",snapshot.Points[1].Key);Invoke(view,"Refresh");
            var confirmsBeforeReference=confirmations.Count;view.ConfirmationForTesting=(_,_,_)=>throw new InvalidOperationException("Representative selection must not open confirmation");
            Invoke(view,"SetReference_Click",view,new RoutedEventArgs());
            Test("representative_selection_is_immediate_without_confirmation",Field<string?>(view,"_representativeKey")==snapshot.Points[1].Key&&confirmations.Count==confirmsBeforeReference);
            view.ConfirmationForTesting=(_,_,_)=>false;
            ((ScrollViewer)view.FindName("WorkspaceScroll")).ScrollToTop();Pump();Render(view,"simplified-dark.png");
            LoadResources("Light");Pump();Render(view,"simplified-light.png");
            ((Expander)view.FindName("ReferenceSection")).IsExpanded=true;((Expander)view.FindName("CorrectionSection")).IsExpanded=true;Pump();Render(view,"simplified-expanded-light.png");
            window.Close();_application.Shutdown();
        }
        catch(Exception e){Test("sync_levels_unhandled",false,e.ToString());}
        var result=new{schemaVersion="a3-manual-inspection-ui-sync-levels/1",passed=_failures==0,tests=Checks.Count,failures=_failures,scope="Native WPF with fake controller, applied/measurement Level policy, simplified controls and real description/confirmation dispatch. No equipment app or SDK.",checks=Checks};
        File.WriteAllText(Path.Combine(_output,"ui-sync-levels-results.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(new{result.passed,result.tests,result.failures}));return _failures==0?0:1;
    }
    private static void LoadResources(string theme)
    {
        _application.Resources.Clear(); _application.Resources.MergedDictionaries.Clear();
        using (var stream = File.OpenRead(Path.Combine(_uiSource, "Themes", "Theme" + theme + ".xaml")))
            _application.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var app = XDocument.Load(Path.Combine(_uiSource, "App.xaml"));
        var source = app.Descendants(presentation + "ResourceDictionary").First();
        var needed = new HashSet<string> { "PrimaryButton", "SecondaryButton", "PanelBorder", "PanelTitle", "PlainDataGridCell", "PlainDataGrid" };
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x),
            source.Elements().Where(el => needed.Contains(el.Attribute(x + "Key")?.Value ?? "")).Select(el => new XElement(el)));
        _application.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(dictionary.ToString()));
    }
    private static CManualInspectionUiSnapshot Snapshot(int count)
    {
        var points = Enumerable.Range(0, count).Select(i => new CManualInspectionUiPoint
        {
            Key = $"C{i / 80 + 1}:H{i % 2 + 1}:S{i % 80 + 1}:B11", CellNo = i / 80 + 1, HeadNo = i % 2 + 1,
            ShotNo = i % 80 + 1, BeamNo = 11, Row = i % 80 / 10, Column = i % 10,
            DesignXmm = i % 10 * 0.1, DesignYmm = i % 80 / 10 * 0.1,
            WorkingJudge = i % 11 == 0 ? "NG" : "OK", OriginalJudge = "OK", WorkingXUm = i % 8 - 3.5,
            WorkingYUm = (i % 5 - 2) * 1.2, OriginalXUm = i % 8 - 3.5, OriginalYUm = (i % 5 - 2) * 1.2,
            Source = i % 23 == 0 ? "MANUAL" : "MEASURED", IsProxy = i % 23 == 0, ReferenceKey = i % 23 == 0 ? "C1:H1:S2:B11" : "",
            BaseXUm = i%4*7, BaseYUm = -1, HasAppliedOffset=i%7!=0, AppliedLevel=i%4, DisplayedMeasurementOrigin=i%2==0?"ROUGH · 합성 시험":"FINE · 합성 시험",MeasurementSourcePath="synthetic/measurement.csv",Details = "Synthetic test measurement only; DESIGN µm."
        }).ToArray();
        return new CManualInspectionUiSnapshot { Version = 7, RecipeId = "A3_QA", RunId = "native-qa", ActiveRevisionId = "QA-R0001", Points = points,
            StatusText = "독립 WPF 테스트 · 실제 설비 연결 없음", PersistenceText = "QA · 임시 출력만 사용" };
    }
    private static string ResourceColor(string key) => ((SolidColorBrush)_application.FindResource(key)).Color.ToString();
    private static void Render(FrameworkElement view, string name)
    {
        view.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(_output, name)); encoder.Save(stream);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static object Private(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static Dictionary<string, Rect> Slots(object map) => ((IEnumerable)Private(map, "_slots")).Cast<object>().ToDictionary(
        item => ((CManualInspectionUiPoint)item.GetType().GetProperty("Point")!.GetValue(item)!).Key,
        item => (Rect)item.GetType().GetProperty("Bounds")!.GetValue(item)!);
    private static T Field<T>(object owner, string name) => (T)Private(owner, name);
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Test(string name, bool passed, object? details = null) { Checks.Add(new { name, passed, details }); if (!passed) _failures++; }
}
