using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int VisualUsability()
    {
        var observations = new List<object>();
        try
        {
            LoadResources("Dark");
            var baseline = Snapshot(240) with { Version = 301, RecipeId = "A3_6GH_긴_레시피_설비별_설정_이름_자동화_PPID_검증용_1234567890",
                RunId = "NATIVE:" + new string('A', 64), ActiveRevisionId = "D:/Production/ReviewOffset/A3_6GH_긴_레시피_설비별_설정_이름/20261120_104550_123.csv|SHA256:" + new string('F', 64),
                StatusText = "현재 기판의 측정 결과와 적용 보정값을 확인했습니다.", Message = "" };
            var dataBefore = JsonSerializer.Serialize(baseline.Points);
            var fake = new FakeController(baseline);
            var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_, _, _) => false };
            var window = new Window { Width = 1120, Height = 560, Content = view, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show(); Pump();
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            var scatter = (CManualInspectionScatterControl)view.FindName("ScatterPlot");
            var search = (TextBox)view.FindName("SearchBox");
            var first = baseline.Points.First(p => p.CellNo == 1 && p.HeadNo == 1 && !p.IsMasked);
            var hidden = baseline.Points.First(p => p.CellNo == 2 && p.HeadNo == 2 && !p.IsMasked);
            void Refresh() { Invoke(view, "Refresh"); Pump(); }
            void CloseMenu() { if (map.ContextMenu is not null) map.ContextMenu.IsOpen = false; }

            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([first.Key, hidden.Key], first.Key));
            Set(view, "_cell", 1); Set(view, "_head", 1); Refresh();
            Test("visual_hidden_selection_is_preserved_across_cell_head_filter", Field<HashSet<string>>(view, "_selected").SetEquals([first.Key, hidden.Key]));
            var request = (CManualInspectionUiRequest)Invoke(view, "Request", null!, null!)!;
            Test("visual_hidden_selection_remains_explicit_operation_scope", request.Keys.ToHashSet().SetEquals([first.Key, hidden.Key]) && fake.Calls.Count == 0);
            Test("visual_hidden_selection_is_explained_in_selection_summary", ((TextBlock)view.FindName("SelectionCountText")).Text.Contains("숨김", StringComparison.Ordinal));
            map.ShowOperationMenu(); Pump();
            Test("visual_context_menu_explains_hidden_selection_scope", map.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header?.ToString()?.Contains("숨김", StringComparison.Ordinal) == true)
                || map.ContextMenu.ToolTip?.ToString()?.Contains("숨김", StringComparison.Ordinal) == true);
            CloseMenu();
            var operationEvents = new List<CManualInspectionOperationEventArgs>();
            EventHandler<CManualInspectionOperationEventArgs> recordOperation = (_, value) => operationEvents.Add(value);
            map.OperationRequested += recordOperation;
            map.ShowOperationMenu(); Pump(); var keyboardMenu = map.ContextMenu!;
            var keyboardItems = keyboardMenu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).ToArray();
            var flatFocusAccepted = keyboardItems[0].Focus(); Pump();
            var flatDownEvent = SendKey(keyboardItems[0], Key.Down); Pump();
            var flatDown = keyboardItems[1].IsKeyboardFocusWithin || keyboardItems[1].IsHighlighted;
            var flatUpEvent = SendKey(keyboardItems[1], Key.Up); Pump();
            var flatUp = keyboardItems[0].IsKeyboardFocusWithin || keyboardItems[0].IsHighlighted;
            SendKey(keyboardItems[0], Key.Enter); Pump();
            Test("visual_context_routed_enter_activates_only_source_action_and_cancel_keeps_host_unchanged", operationEvents.Count == 1
                && operationEvents[0].Action == EN_MANUAL_INSPECTION_UI_ACTION.Move && fake.Calls.Count == 0);
            map.ShowOperationMenu(); Pump(); SendKey(map.ContextMenu!, Key.Escape); Pump();
            Test("visual_context_escape_closes_without_an_operation", map.ContextMenu?.IsOpen != true && operationEvents.Count == 1 && fake.Calls.Count == 0);
            map.OperationRequested -= recordOperation;
            var systemMenu = new ContextMenu { PlacementTarget = map };
            systemMenu.Items.Add(new MenuItem { Header = "Native disabled heading", IsEnabled = false });
            var systemFirst = new MenuItem { Header = "Native first" }; var systemNext = new MenuItem { Header = "Native next" };
            systemMenu.Items.Add(systemFirst); systemMenu.Items.Add(systemNext); systemMenu.IsOpen = true; Pump();
            var systemFocusAccepted = systemFirst.Focus(); Pump();
            var systemDownEvent = SendKey(systemFirst, Key.Down); Pump();
            var systemDown = systemNext.IsKeyboardFocusWithin || systemNext.IsHighlighted;
            var systemUpEvent = SendKey(systemNext, Key.Up); Pump();
            var systemUp = systemFirst.IsKeyboardFocusWithin || systemFirst.IsHighlighted;
            systemMenu.IsOpen = false;
            observations.Add(new { kind = "directional-menu-keyboard-control-experiment", flatFocusAccepted, systemFocusAccepted, flatDown, flatUp, systemDown, systemUp,
                flatDownHandled = flatDownEvent.Handled, flatUpHandled = flatUpEvent.Handled, systemDownHandled = systemDownEvent.Handled, systemUpHandled = systemUpEvent.Handled,
                scope = "Off-screen nonactivated WPF window; no desktop focus activation and no injected highlight/focus state. If both native and flat controls cannot focus, real Up/Down traversal is inconclusive and excluded from passed check count." });
            if (systemDown || flatDown) Test("visual_context_down_key_matches_real_system_control", flatDown);
            if (systemUp || flatUp) Test("visual_context_up_key_matches_real_system_control", flatUp);

            foreach (var theme in new[] { "Dark", "Light" })
            {
                LoadResources(theme); fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsReadOnly = true }); Pump();
                map.ShowOperationMenu(); Pump(); var readonlyMenu = map.ContextMenu!;
                Test("visual_context_flat_theme_resource_" + theme, ((SolidColorBrush)readonlyMenu.Background).Color == ((SolidColorBrush)_application.FindResource("PanelBackgroundBrush")).Color
                    && readonlyMenu.Items.OfType<MenuItem>().All(item => item.OverridesDefaultStyle));
                Test("visual_context_readonly_disables_motion_but_keeps_details_" + theme, readonlyMenu.Items.OfType<MenuItem>().Count(item => item.IsEnabled) == 1
                    && readonlyMenu.Items.OfType<MenuItem>().Single(item => item.IsEnabled).Header.ToString()!.Contains("상세", StringComparison.Ordinal));
                Render(readonlyMenu, "visual-" + theme.ToLowerInvariant() + "-context-readonly.png"); CloseMenu();
            }
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsReadOnly = false }); Pump();
            var files = (Button)view.FindName("FilesButton"); Invoke(view, "Files_Click", files, new RoutedEventArgs()); Pump();
            Test("visual_file_menu_uses_same_flat_theme", files.ContextMenu!.Items.OfType<MenuItem>().All(item => item.OverridesDefaultStyle)); files.ContextMenu.IsOpen = false;
            var selectionTools = Descendants(view).OfType<Button>().Single(button => button.Content?.ToString()?.StartsWith("선택", StringComparison.Ordinal) == true
                && button.ToolTip?.ToString()?.Contains("NG만", StringComparison.Ordinal) == true);
            Invoke(view, "SelectionTools_Click", selectionTools, new RoutedEventArgs()); Pump();
            Test("visual_selection_menu_uses_same_flat_theme", selectionTools.ContextMenu!.Items.OfType<MenuItem>().All(item => item.OverridesDefaultStyle)); selectionTools.ContextMenu.IsOpen = false;

            var originalSlots = Slots(map);
            map.ZoomAt(1.8, new Point(125, 45)); map.PanBy(new Vector(23, -12)); Pump();
            var pan = Field<Vector>(map, "_pan");
            var examined = 0; var matched = true;
            foreach (var entry in originalSlots)
            {
                var screen = new Point((entry.Value.Left + entry.Value.Width / 2) * map.Zoom + pan.X,
                    (entry.Value.Top + entry.Value.Height / 2) * map.Zoom + pan.Y);
                if (screen.X < 0 || screen.Y < 0 || screen.X > map.ActualWidth || screen.Y > map.ActualHeight) continue;
                examined++; matched &= ((CManualInspectionUiPoint?)Invoke(map, "Hit", screen))?.Key == entry.Key;
            }
            Test("visual_zoom_pan_hit_matches_exact_transformed_point", examined > 0 && matched, new { examined });
            Test("visual_zoom_pan_does_not_change_source_or_dispatch_work", fake.Calls.Count == 0 && JsonSerializer.Serialize(fake.Snapshot.Points) == dataBefore);
            map.ResetView(); Pump();
            Set(view, "_judge", "NG"); Refresh();
            Test("visual_filter_does_not_repack_matrix_coordinates", Slots(map).All(entry => originalSlots[entry.Key] == entry.Value));
            Set(view, "_judge", "ALL"); Refresh();
            map.SelectRectangle(new Rect(0, 0, map.ActualWidth, map.ActualHeight), true); Pump();
            Test("visual_additive_drag_keeps_selected_hidden_points", Field<HashSet<string>>(view, "_selected").Contains(hidden.Key)
                && Field<HashSet<string>>(view, "_selected").Count == Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_visible").Count + 1);
            var selectedBeforeCancel = Field<HashSet<string>>(view, "_selected").ToHashSet();
            PrimeGesture(map);
            Test("visual_escape_cancels_pan_and_restores_previous_visual_position", map.TryCancelGesture() && !Field<bool>(map, "_gesture") && Field<Vector>(map, "_pan") == new Vector(7, 11));
            Test("visual_cancelled_gesture_never_changes_selection_or_work", Field<HashSet<string>>(view, "_selected").SetEquals(selectedBeforeCancel) && fake.Calls.Count == 0);
            PrimeGesture(map); Invoke(map, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 1)); Pump();
            Test("visual_capture_loss_cancels_gesture_and_restores_pan", !Field<bool>(map, "_gesture") && Field<Vector>(map, "_pan") == new Vector(7, 11));
            PrimeGesture(map); map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Pump();
            Test("visual_unload_cancels_gesture_without_selection_or_work", !Field<bool>(map, "_gesture") && Field<Vector>(map, "_pan") == new Vector(7, 11)
                && Field<HashSet<string>>(view, "_selected").SetEquals(selectedBeforeCancel) && fake.Calls.Count == 0);
            PrimeGesture(map); fake.Replace(fake.Snapshot with { Version = 302 }); Pump();
            Test("visual_external_snapshot_cancels_stale_drag", !Field<bool>(map, "_gesture") && fake.Calls.Count == 0);
            PrimeGesture(map); Set(view, "_head", 2); Refresh();
            Test("visual_scope_change_cancels_stale_drag", !Field<bool>(map, "_gesture") && fake.Calls.Count == 0);
            PrimeGesture(map); Set(view, "_judge", "NG"); Refresh();
            Test("visual_same_version_filter_change_cancels_drag_and_restores_pan", !Field<bool>(map, "_gesture") && Field<Vector>(map, "_pan") == new Vector(7, 11) && fake.Calls.Count == 0);
            Set(view, "_judge", "ALL"); Refresh(); PrimeGesture(map); Refresh();
            Test("visual_same_scope_selection_refresh_preserves_active_gesture", Field<bool>(map, "_gesture") && Field<Vector>(map, "_pan") == new Vector(37, 51));
            map.TryCancelGesture();
            map.ResetView(); Pump();
            var zoomBefore = map.Zoom;
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0d, -1d }) map.ZoomAt(invalid, new Point(20, 20));
            Test("visual_invalid_zoom_factor_cannot_poison_view_state", map.Zoom == zoomBefore && double.IsFinite(Field<Vector>(map, "_pan").X));
            map.ZoomAt(1.1, new Point(double.NaN, 10));
            Test("visual_invalid_zoom_origin_cannot_poison_pan", double.IsFinite(Field<Vector>(map, "_pan").X) && double.IsFinite(Field<Vector>(map, "_pan").Y));
            map.ResetView(); Pump();

            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var expander in Descendants(view).OfType<Expander>().ToArray()) expander.IsExpanded = true;
                Pump();
            }
            Pump(); search.ApplyTemplate(); Pump();
            var searchInner = Descendants(search).OfType<UIElement>().FirstOrDefault(element => element.GetType().Name.Contains("TextBoxView", StringComparison.Ordinal));
            Test("visual_text_input_uses_real_inner_editor_event_source", searchInner is not null);
            if (searchInner is not null)
            {
                foreach (var key in new[] { Key.Apps, Key.Enter, Key.Space, Key.Left, Key.Right })
                {
                    CloseMenu(); var calls = fake.Calls.Count; var beforePan = Field<Vector>(map, "_pan");
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view), 1, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    searchInner.RaiseEvent(args); Pump();
                    Test("visual_inner_textbox_key_" + key + "_does_not_open_map_or_execute_work", map.ContextMenu?.IsOpen != true && fake.Calls.Count == calls && Field<Vector>(map, "_pan") == beforePan);
                }
            }
            foreach (var expander in Descendants(view).OfType<Expander>()) expander.IsExpanded = false;
            Pump();
            foreach (var control in new FrameworkElement[] { map, scatter, search, (Button)view.FindName("StopButton") })
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement((UIElement)control);
                Test("visual_accessibility_peer_name_" + control.Name, peer is not null && !string.IsNullOrWhiteSpace(peer.GetName()));
            }
            Test("visual_map_scatter_accessibility_explains_visual_only_navigation", !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(map)) && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(scatter)));
            foreach (var symbol in new[] { "+", "−" })
            {
                var iconButton = Descendants(view).OfType<Button>().Single(button => button.Content?.ToString() == symbol);
                Test("visual_icon_button_has_descriptive_accessibility_name_" + symbol, AutomationProperties.GetName(iconButton).Length >= 4 && iconButton.ToolTip is not null);
            }

            var dictBefore = Private(view, "_points"); var metricsBefore = scatter.Metrics; var cachedSelection = Field<HashSet<string>>(view, "_selected").ToHashSet();
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, StatusText = "같은 원시값의 진행 상태만 갱신", IsBusy = true }); Pump();
            Test("visual_progress_only_snapshot_reuses_point_dictionary", ReferenceEquals(dictBefore, Private(view, "_points")));
            Test("visual_progress_only_snapshot_reuses_scatter_statistics", ReferenceEquals(metricsBefore, scatter.Metrics));
            Test("visual_progress_only_snapshot_preserves_selection_without_work", Field<HashSet<string>>(view, "_selected").SetEquals(cachedSelection) && fake.Calls.Count == 0);
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsBusy = false,
                Points = fake.Snapshot.Points.Select(p => p.Key == first.Key ? p with { WorkingXUm = 19 } : p).ToArray() }); Pump();
            Test("visual_new_measurement_array_invalidates_dictionary_and_scatter_cache", !ReferenceEquals(dictBefore, Private(view, "_points")) && !ReferenceEquals(metricsBefore, scatter.Metrics));
            var unassigned = fake.Snapshot.Points.Select((point, index) => index is 10 or 14
                ? point with { Key = index == 10 ? "UNASSIGNED-H00" : "INVALID-H09", HeadNo = index == 10 ? 0 : 9, IsMasked = true } : point).ToArray();
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, Points = unassigned }); Set(view, "_cell", 1); Set(view, "_head", 0); Refresh();
            string[] ChoiceValues(ComboBox combo) => ((IEnumerable)combo.ItemsSource).Cast<object>().Select(choice => choice.GetType().GetProperty("Value")!.GetValue(choice)!.ToString()!).ToArray();
            var headChoices = ChoiceValues((ComboBox)view.FindName("HeadFilter"));
            Test("visual_head_filter_has_single_all_and_only_real_scanners", headChoices.Count(value => value == "0") == 1 && headChoices.All(value => int.Parse(value) is >= 0 and <= 8));
            Test("visual_unassigned_masked_head_remains_in_matrix_geometry", Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_all").Any(point => point.HeadNo == 0 && point.IsMasked));
            Set(view, "_scatterScope", "HEAD"); Refresh();
            var headCombo = Descendants((FrameworkElement)view.FindName("ScatterScopeControls")).OfType<ComboBox>().Single();
            var scatterHeads = ChoiceValues(headCombo);
            Test("visual_scatter_head_options_do_not_duplicate_unassigned_head_as_all", scatterHeads.Count(value => value == "0") == 1 && scatterHeads.All(value => int.Parse(value) is >= 0 and <= 8));

            var duplicate = baseline.Points.Take(3).Select((p, index) => p with { WorkingJudge = "OK", WorkingXUm = index == 2 ? .08 : 0, WorkingYUm = 0,
                Source = "MEASURED", IsProxy = false, ReferenceKey = "" }).ToArray();
            var standaloneScatter = new CManualInspectionScatterControl();
            var scatterWindow = new Window { Width = 600, Height = 450, Content = standaloneScatter, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            var selections = new List<CManualInspectionSelectionEventArgs>(); standaloneScatter.SelectionRequested += (_, value) => selections.Add(value);
            standaloneScatter.Configure(duplicate, [], false, 5, 5, ""); scatterWindow.Show(); Pump();
            var scatterHits = Field<List<(string Key, Point Point)>>(standaloneScatter, "_hits");
            var coincident = scatterHits.Single(hit => hit.Key == duplicate[0].Key).Point;
            var overlap = (IEnumerable)Invoke(standaloneScatter, "HitGroup", coincident)!;
            Test("visual_scatter_hit_group_excludes_nearby_distinct_measurement", overlap.Cast<object>().Count() == 2 && scatterHits.Count == 3);
            for (var i = 0; i < 3; i++) Invoke(standaloneScatter, "SelectHit", coincident, false);
            Test("visual_coincident_scatter_clicks_cycle_individual_points", selections.Count == 3 && selections[0].Keys.Count == 1 && selections[1].Keys.Count == 1
                && selections[0].Keys[0] != selections[1].Keys[0] && selections[2].Keys[0] == selections[0].Keys[0]);
            Invoke(standaloneScatter, "SelectHit", coincident, true);
            Test("visual_ctrl_scatter_click_adds_only_coincident_group", selections[^1].Additive && !selections[^1].Toggle && selections[^1].Keys.ToHashSet().SetEquals(duplicate.Take(2).Select(p => p.Key)));
            standaloneScatter.Configure(duplicate, [duplicate[0].Key], false, 5, 5, ""); Pump();
            Invoke(standaloneScatter, "SelectHit", coincident, true);
            Test("visual_partial_coincident_selection_ctrl_adds_missing_points_without_inverting", selections[^1].Additive && !selections[^1].Toggle && selections[^1].Keys.Count == 2);
            standaloneScatter.Configure(duplicate, duplicate.Take(2).Select(p => p.Key), false, 5, 5, ""); Pump();
            Invoke(standaloneScatter, "SelectHit", coincident, true);
            Test("visual_fully_selected_coincident_group_ctrl_toggles_group_off", selections[^1].Additive && selections[^1].Toggle && selections[^1].Keys.Count == 2);
            var selectedCount = selections.Count;
            Test("visual_blank_scatter_click_does_not_select_remote_point", !(bool)Invoke(standaloneScatter, "SelectHit", new Point(-100, -100), false)! && selections.Count == selectedCount);
            var axisPoints = duplicate.Select((p, index) => p with { WorkingXUm = index == 1 ? 1 : 0, WorkingYUm = index == 2 ? 1 : 0 }).ToArray();
            standaloneScatter.Configure(axisPoints, [], false, 5, 5, ""); Pump();
            scatterHits = Field<List<(string Key, Point Point)>>(standaloneScatter, "_hits");
            var originPixel = scatterHits.Single(hit => hit.Key == axisPoints[0].Key).Point;
            var xPixel = scatterHits.Single(hit => hit.Key == axisPoints[1].Key).Point;
            var yPixel = scatterHits.Single(hit => hit.Key == axisPoints[2].Key).Point;
            Test("visual_scatter_uses_equal_xy_scale_and_positive_y_up", Math.Abs((xPixel - originPixel).Length - (yPixel - originPixel).Length) < 1e-8 && xPixel.X > originPixel.X && yPixel.Y < originPixel.Y);
            Render(standaloneScatter, "visual-scatter-overlap-and-scale.png"); scatterWindow.Close();

            (double Width, double Height, double Scale, string Label)[] layouts = [(1480, 900, 1, "wide"), (1120, 560, 1, "compact"),
                (1400d / 1.25, 900d / 1.25, 1.25, "dpi125"), (1400d / 1.5, 900d / 1.5, 1.5, "dpi150")];
            foreach (var theme in new[] { "Light", "Dark" })
            {
                LoadResources(theme);
                Test("visual_primary_text_contrast_" + theme, Contrast(((SolidColorBrush)_application.FindResource("TextPrimaryBrush")).Color,
                    ((SolidColorBrush)_application.FindResource("PanelBackgroundBrush")).Color) >= 4.5);
                foreach (var layout in layouts)
                {
                    window.Width = layout.Width; window.Height = layout.Height;
                    Set(view, "_cell", 1); Set(view, "_head", 0); Set(view, "_query", ""); Set(view, "_judge", "ALL");
                    fake.Replace(baseline with { Version = fake.Snapshot.Version + 1 }); Refresh();
                    foreach (var expander in Descendants(view).OfType<Expander>()) expander.IsExpanded = false;
                    Pump();
                    var workspace = (ScrollViewer)view.FindName("WorkspaceScroll"); var inspector = (ScrollViewer)view.FindName("InspectorScroll");
                    var suffix = theme + "_" + layout.Label;
                    Test("visual_default_sections_collapsed_" + suffix, !((Expander)view.FindName("ResultsSection")).IsExpanded && !((Expander)view.FindName("ScatterSection")).IsExpanded);
                    var actualMapFrame = (Border)map.Parent;
                    Test("visual_map_not_clipped_" + suffix, actualMapFrame.ActualHeight - actualMapFrame.BorderThickness.Top - actualMapFrame.BorderThickness.Bottom
                        - actualMapFrame.Padding.Top - actualMapFrame.Padding.Bottom + 1 >= map.ActualHeight && map.ActualHeight >= 79);
                    Test("visual_no_horizontal_overflow_" + suffix, workspace.ScrollableWidth <= 1 && (!inspector.IsVisible || inspector.ScrollableWidth <= 1) && view.ActualWidth <= layout.Width + 1);
                    if (layout.Label != "dpi150") Test("visual_no_default_vertical_scroll_" + suffix, workspace.ScrollableHeight <= 1 && (!inspector.IsVisible || inspector.ScrollableHeight <= 1),
                        new { workspace.ScrollableHeight, inspectorScroll = inspector.ScrollableHeight, inspectorVisible = inspector.IsVisible });
                    var stop = (Button)view.FindName("StopButton"); var stopBounds = stop.TransformToAncestor(view).TransformBounds(new Rect(stop.RenderSize));
                    Test("visual_stop_button_visible_" + suffix, stop.IsVisible && stop.ActualHeight >= 30 && stopBounds.Left >= 0 && stopBounds.Right <= view.ActualWidth + 1 && stopBounds.Bottom <= view.ActualHeight + 1);
                    var context = (TextBlock)view.FindName("ContextText");
                    Test("visual_long_context_is_available_without_expanding_layout_" + suffix, context.Text.Contains("Recipe", StringComparison.Ordinal) && context.ToolTip?.ToString()?.Contains(baseline.RunId, StringComparison.Ordinal) == true);
                    RenderAtScale(view, "visual-" + theme.ToLowerInvariant() + "-" + layout.Label + ".png", layout.Scale);
                    observations.Add(new { theme, layout.Label, layout.Scale, view.ActualWidth, view.ActualHeight, mapWidth = map.ActualWidth, mapHeight = map.ActualHeight,
                        workspaceScroll = workspace.ScrollableHeight, inspectorScroll = inspector.ScrollableHeight });
                }
            }
            LoadResources("Dark"); window.Width = 1480; window.Height = 900;
            var heroPoints = baseline.Points.Select((point, index) => point with
            {
                WorkingXUm = new[] { 1.8, 6.5, 13.2, 24.1 }[index % 80 / 20], WorkingYUm = (index % 5 - 2) * .6,
                WorkingJudge = index % 80 / 20 == 0 ? "OK" : "NG", OriginalJudge = index % 80 / 20 == 0 ? "OK" : "NG",
                OriginalXUm = new[] { 1.8, 6.5, 13.2, 24.1 }[index % 80 / 20], OriginalYUm = (index % 5 - 2) * .6,
                DisplayedMeasurementOrigin = point.IsProxy ? "대표값 복사 · 합성 예시" : index % 2 == 0 ? "Rough 측정 · 합성 예시" : "Fine 측정 · 합성 예시"
            }).ToArray();
            var hero = baseline with { Version = 450, IsSimulation = true, RecipeId = "A3 6GH · 리뷰 작업공간", RunId = "QA-G001 · 설비 연결 없는 화면 검증",
                ActiveRevisionId = "ReviewOffset_20261120_104550.csv · QA", Points = heroPoints,
                StatusText = "측정 결과를 확인하고 선택한 위치의 작업을 우클릭으로 시작하세요.",
                CandidateRows = heroPoints.Skip(1).Take(3).Select(point => new CManualInspectionUiCandidateRow { Key = point.Key, Source = "MEASURED",
                    BaseXUm = point.BaseXUm, BaseYUm = point.BaseYUm, DeltaXUm = point.WorkingXUm!.Value, DeltaYUm = point.WorkingYUm!.Value,
                    FinalXUm = point.BaseXUm + point.WorkingXUm.Value, FinalYUm = point.BaseYUm + point.WorkingYUm.Value }).ToArray(), CandidateStatus = "DRAFT" };
            fake.Replace(hero); Set(view, "_cell", 1); Set(view, "_head", 0); Set(view, "_color", "level"); Refresh();
            var heroSelection = heroPoints.Skip(1).Take(3).Select(point => point.Key).ToArray();
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs(heroSelection, heroSelection[0]));
            Set(view, "_representativeKey", heroPoints[5].Key); Refresh();
            if (view.FindName("InspectorSection") is Expander inspectorSection) inspectorSection.IsExpanded = true;
            ((Expander)view.FindName("ReferenceSection")).IsExpanded = true;
            ((Expander)view.FindName("CorrectionSection")).IsExpanded = true; Pump();
            Render(view, "visual-dark-review-hero.png");
            map.ShowOperationMenu(); Pump();
            Render(map.ContextMenu!, "visual-dark-context-menu.png"); CloseMenu();
            ((Expander)view.FindName("ReferenceSection")).IsExpanded = false;
            ((Expander)view.FindName("CorrectionSection")).IsExpanded = false;
            if (view.FindName("InspectorSection") is Expander inspectorForScatter) inspectorForScatter.IsExpanded = false;
            LoadResources("Light"); window.Height = 1050;
            ((Expander)view.FindName("ScatterSection")).IsExpanded = true;
            Set(view, "_scatterScope", "HEAD"); Set(view, "_scatterHead", 1); Refresh();
            ((FrameworkElement)view.FindName("ScatterPlot")).BringIntoView(); Pump();
            Render(view, "visual-light-head-scatter-hero.png");
            ((Expander)view.FindName("ScatterSection")).IsExpanded = false;
            Test("visual_preview_scenes_never_dispatch_controller_work", fake.Calls.Count == 0);
            LoadResources("Dark"); window.Width = 1480; window.Height = 900; Pump();
            var large = Snapshot(32000) with { Version = 501, Message = "", RunId = "large-visual-only" };
            var watch = Stopwatch.StartNew(); fake.Replace(large); Set(view, "_cell", 0); Set(view, "_head", 0); Refresh(); map.ResetView(); Pump(); watch.Stop();
            var largeSlots = Slots(map);
            Test("visual_large_dataset_has_no_missing_matrix_slots", largeSlots.Count == large.Points.Count && Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_all").Count == large.Points.Count);
            Test("visual_large_fit_keeps_all_slot_bounds_in_view", largeSlots.Values.All(rect => rect.Left >= -1 && rect.Top >= -1 && rect.Right <= map.ActualWidth + 1 && rect.Bottom <= map.ActualHeight + 1));
            Test("visual_large_data_presentation_has_no_equipment_request", fake.Calls.Count == 0);
            observations.Add(new { kind = "synthetic-large-layout", points = large.Points.Count, milliseconds = watch.Elapsed.TotalMilliseconds });
            Render(view, "visual-dark-32000-fit.png");
            window.Close(); _application.Shutdown();
        }
        catch (Exception error) { Test("visual_usability_unhandled", false, error.ToString()); }
        var report = new { schemaVersion = "a3-manual-inspection-ui-visual-usability/1", passed = _failures == 0, tests = Checks.Count, failures = _failures,
            scope = "Actual WPF sources/themes and synthetic points with request-recording fake Controller. DPI125/150 uses equivalent DIP space and high-resolution bitmap rendering, not an OS monitor DPI change. No equipment App/Root/SDK/Vision/motion.", checks = Checks, observations };
        File.WriteAllText(Path.Combine(_output, "ui-visual-usability-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.passed, report.tests, report.failures }));
        return _failures == 0 ? 0 : 1;
    }

    private static void PrimeGesture(CManualInspectionMapControl map)
    {
        Set(map, "_gesture", true); Set(map, "_panning", true); Set(map, "_moved", true);
        Set(map, "_startPan", new Vector(7, 11)); Set(map, "_pan", new Vector(37, 51));
    }
    private static KeyEventArgs SendKey(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 1, key) { RoutedEvent = Keyboard.KeyDownEvent };
        element.RaiseEvent(args); return args;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static IEnumerable<DependencyObject> AncestorsAndSelf(DependencyObject node)
    {
        for (var cursor = node; cursor is not null; cursor = VisualTreeHelper.GetParent(cursor)) yield return cursor;
    }
    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte value) { var s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double L(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var first = L(a); var second = L(b); return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static void RenderAtScale(FrameworkElement element, string filename, double scale)
    {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_output, filename)); encoder.Save(output);
    }
}
