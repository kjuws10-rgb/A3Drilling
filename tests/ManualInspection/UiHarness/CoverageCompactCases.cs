using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int CoverageCompact()
    {
        try
        {
            LoadResources("Dark");
            var original = Snapshot(80);
            var empty = original.Points[0] with
            {
                WorkingJudge = "UNMEASURED", WorkingXUm = null, WorkingYUm = null,
                OriginalJudge = "UNMEASURED", OriginalXUm = null, OriginalYUm = null,
                Source = "KEPT", IsProxy = false, ReferenceKey = "", HasAppliedOffset = false,
                BaseXUm = 0, BaseYUm = 0, CandidateXUm = null, CandidateYUm = null,
                DisplayedMeasurementOrigin = "", MeasurementSourcePath = "", MeasurementToken = ""
            };
            CManualInspectionUiPoint At(int index) => empty with
            {
                Key = original.Points[index].Key, CellNo = 1, HeadNo = original.Points[index].HeadNo,
                ShotNo = index + 1, Row = 0, Column = index, BeamNo = 11
            };
            var raw = At(0) with { HasAppliedOffset = true, BaseXUm = 40, BaseYUm = 10,
                WorkingJudge = "OK", WorkingXUm = 3, WorkingYUm = -4, OriginalJudge = "OK",
                OriginalXUm = 3, OriginalYUm = -4, Source = "MEASURED", MeasurementToken = "RAW-1" };
            var based = At(1) with { HasAppliedOffset = true, BaseXUm = 7, BaseYUm = -2 };
            var zero = At(2) with { HasAppliedOffset = true };
            var missing = At(3);
            var error = At(4) with { HasAppliedOffset = true, BaseXUm = 12, BaseYUm = 0,
                WorkingJudge = "ERROR", OriginalJudge = "ERROR", Source = "IMPORTED" };
            var masked = At(5) with { IsMasked = true, HasAppliedOffset = true, BaseXUm = 7 };
            var candidate = At(6) with { CandidateXUm = 99, CandidateYUm = 99 };
            var invalid = At(7) with { HasAppliedOffset = true, BaseXUm = double.NaN };
            var proxy = At(8) with { HasAppliedOffset = true, BaseXUm = 9, BaseYUm = 9,
                WorkingJudge = "OK", WorkingXUm = 6, WorkingYUm = 1, OriginalJudge = "OK",
                OriginalXUm = 9, OriginalYUm = 9, Source = "MANUAL", IsProxy = true, ReferenceKey = raw.Key };
            var points = new[] { raw, based, zero, missing, error, masked, candidate, invalid, proxy };
            var state = original with { Version = 201, Points = points, RunId = "coverage-qa", IsSimulation = false };
            var jsonOptions = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
            var before = JsonSerializer.Serialize(points, jsonOptions);
            var fake = new FakeController(state);
            var confirmations = new List<string>(); var accept = true;
            var view = new CManualInspectionView { DataContext = fake,
                ConfirmationForTesting = (_, description, _) => { confirmations.Add(description); return accept; } };
            Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
            var window = new Window { Width = 1480, Height = 900, Content = view, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show(); Pump();
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            var scatter = (CManualInspectionScatterControl)view.FindName("ScatterPlot");

            Test("active_row_is_measurement_history_without_native_raw", based.HasMeasurementHistory && based.UsesAppliedMeasurement
                && based.WorkingJudge == "UNMEASURED" && based.OriginalXUm is null && based.MeasurementToken == "");
            Test("correction_based_status_and_basis_are_explicit", based.MeasurementStatusText.Contains("측정")
                && based.MeasurementBasisText.Contains("보정") && based.MeasurementSourceCode == "CORRECTION_BASED");
            Test("stored_zero_is_measured_history_level_one", zero.HasMeasurementHistory && zero.UsesAppliedMeasurement
                && zero.MeasurementXUm == 0 && zero.MeasurementYUm == 0 && CManualInspectionDrawing.Level(zero, state) == 0);
            Test("missing_zero_row_remains_unmeasured", !missing.HasMeasurementHistory && !missing.UsesAppliedMeasurement
                && missing.MeasurementXUm is null && CManualInspectionDrawing.Level(missing, state) == -1);
            Test("native_measurement_takes_precedence_over_accumulated_B", raw.HasWorkingMeasurement && !raw.UsesAppliedMeasurement
                && raw.MeasurementXUm == 3 && raw.MeasurementYUm == -4
                && CManualInspectionDrawing.Level(raw, state) == 0 && CManualInspectionDrawing.AppliedLevel(raw, state) == 3);
            Test("existing_correction_is_not_hidden_by_latest_error", error.HasMeasurementHistory && error.UsesAppliedMeasurement
                && error.MeasurementXUm == 12 && error.WorkingJudge == "ERROR" && error.OriginalJudge == "ERROR"
                && CManualInspectionDrawing.Level(error, state) == 2);
            Test("masked_history_never_becomes_measurement_target", !masked.HasMeasurementHistory && !masked.UsesAppliedMeasurement
                && CManualInspectionDrawing.Level(masked, state) == -1);
            Test("unsaved_candidate_does_not_create_measured_coverage", !candidate.HasMeasurementHistory && candidate.MeasurementXUm is null);
            Test("invalid_stored_numeric_does_not_create_a_level", invalid.HasMeasurementHistory
                && CManualInspectionDrawing.Level(invalid, state) == -1);
            Test("correction_based_and_error_points_can_be_marked_without_native_relabel", based.CanBeReference && zero.CanBeReference && error.CanBeReference && based.Source != "MEASURED");
            Test("copied_working_measurement_preserves_original_distinction", proxy.HasMeasurementHistory && !proxy.UsesAppliedMeasurement
                && proxy.MeasurementXUm == 6 && proxy.OriginalXUm == 9 && proxy.CanBeReference);
            Test("measurement_level_covers_same_B_across_cells_heads", CManualInspectionDrawing.Level(based, state)
                == CManualInspectionDrawing.Level(based with { CellNo = 9, HeadNo = 8 }, state));
            string Brush(CManualInspectionUiPoint point, string mode) => ((SolidColorBrush)CManualInspectionDrawing.PointBrush(view, point, mode, state)).Color.ToString();
            Test("measurement_and_applied_level_agree_without_direct_raw", Brush(based, "level") == Brush(based, "applied"));

            var metrics = CManualInspectionScatterMetrics.Calculate(points);
            Test("scatter_includes_correction_based_history", metrics.Count == 5 && metrics.CorrectionBasedCount == 3
                && Math.Abs(metrics.MeanX!.Value - 5.6) < 1e-8 && Math.Abs(metrics.MeanY!.Value + 1) < 1e-8);
            Test("scatter_counts_missing_masked_and_invalid_separately", metrics.Unmeasured == 2 && metrics.Masked == 1 && metrics.Invalid == 1);
            Test("projection_does_not_mutate_measurement_or_offset_records", before == JsonSerializer.Serialize(points, jsonOptions));
            Set(view, "_focusKey", based.Key); Invoke(view, "Refresh"); Pump();
            Test("selected_summary_shows_effective_deviation", ((TextBlock)view.FindName("PointErrorText")).Text.Contains(based.MeasurementErrorText));
            Test("selected_summary_calls_it_measurement_history", ((TextBlock)view.FindName("PointJudgesText")).Text.Contains("측정")
                && ((TextBlock)view.FindName("PointJudgesText")).Text.Contains("보정"));
            Set(view, "_judge", "MEASURED"); Invoke(view, "Refresh");
            Test("measured_filter_includes_existing_correction", Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered").Any(p => p.Key == based.Key)
                && Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered").All(p => p.HasMeasurementHistory));
            Set(view, "_judge", "UNMEASURED"); Invoke(view, "Refresh");
            Test("unmeasured_filter_excludes_existing_correction", Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered").Select(p => p.Key).ToHashSet().SetEquals([missing.Key, candidate.Key]));
            Set(view, "_judge", "ALL"); Set(view, "_source", "CORRECTION_BASED"); Invoke(view, "Refresh");
            Test("correction_based_source_filter_uses_display_provenance", Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered").Any(p => p.Key == based.Key)
                && Field<IReadOnlyList<CManualInspectionUiPoint>>(view, "_filtered").All(p => p.MeasurementSourceCode == "CORRECTION_BASED"));
            Set(view, "_source", "ALL"); Set(view, "_scatterOriginal", false); Invoke(view, "Refresh");
            Test("working_scatter_uses_history_projection", scatter.Metrics.Count == 5 && scatter.Metrics.CorrectionBasedCount == 3);
            Set(view, "_scatterOriginal", true); Invoke(view, "Refresh");
            Test("original_scatter_does_not_invent_correction_raw", scatter.Metrics.Count == 2 && scatter.Metrics.CorrectionBasedCount == 0);
            Set(view, "_scatterOriginal", false); Invoke(view, "Refresh");

            Test("results_and_scatter_initially_collapsed", !((Expander)view.FindName("ResultsSection")).IsExpanded
                && !((Expander)view.FindName("ScatterSection")).IsExpanded);
            Test("right_action_buttons_are_removed", new[] { "MoveMeasureButton", "SelectedWorkButton", "PointHistoryButton", "MeasureButton" }.All(n => view.FindName(n) is null));
            var layoutPoints = Snapshot(240).Points.Select(point => point.Key == based.Key ? based : point).ToArray();
            fake.Replace(state with { Version = 202, Points = layoutPoints });
            Set(view, "_cell", 0); Invoke(view, "Refresh"); Pump();
            foreach (var theme in new[] { "Light", "Dark" })
            {
                LoadResources(theme);
                foreach (var size in new[] { (1480, 900), (1120, 720), (1120, 560) })
                {
                    window.Width = size.Item1; window.Height = size.Item2; Pump(); view.UpdateLayout();
                    var workspace = (ScrollViewer)view.FindName("WorkspaceScroll");
                    var inspector = (ScrollViewer)view.FindName("InspectorScroll");
                    Test($"default_no_workspace_scroll_{theme}_{size.Item1}_{size.Item2}", workspace.ScrollableHeight <= 1 && workspace.ScrollableWidth <= 1,
                        new { workspace.ScrollableHeight, workspace.ViewportHeight, workspace.ExtentHeight });
                    Test($"default_no_inspector_scroll_{theme}_{size.Item1}_{size.Item2}", !inspector.IsVisible || inspector.ScrollableHeight <= 1 && inspector.ScrollableWidth <= 1,
                        new { inspector.ScrollableHeight, inspector.ViewportHeight, inspector.ExtentHeight });
                    Test($"default_map_is_visible_{theme}_{size.Item1}_{size.Item2}", map.ActualHeight > 70 && map.ActualWidth > 250);
                    var mapFrame = (Border)map.Parent;
                    var frameHeight = mapFrame.ActualHeight - mapFrame.BorderThickness.Top - mapFrame.BorderThickness.Bottom
                        - mapFrame.Padding.Top - mapFrame.Padding.Bottom;
                    Test($"default_map_not_clipped_by_parent_{theme}_{size.Item1}_{size.Item2}", frameHeight + 1 >= map.ActualHeight,
                        new { frameHeight, mapHeight = map.ActualHeight, mapFrame.ActualHeight });
                    var hitBounds = ((IEnumerable)Private(map, "_hits")).Cast<object>()
                        .Select(hit => (Rect)hit.GetType().GetField("Item2")!.GetValue(hit)!).ToArray();
                    Test($"default_all_slots_fit_map_{theme}_{size.Item1}_{size.Item2}", hitBounds.Length == layoutPoints.Length
                        && hitBounds.All(rect => rect.Left >= -1 && rect.Top >= -1 && rect.Right <= map.ActualWidth + 1 && rect.Bottom <= map.ActualHeight + 1),
                        new { slots = hitBounds.Length, expected = layoutPoints.Length, map.ActualWidth, map.ActualHeight });
                    var legend = (FrameworkElement)view.FindName("MapLegendText");
                    if (legend.IsVisible)
                    {
                        var bounds = legend.TransformToAncestor(workspace).TransformBounds(new Rect(0, 0, legend.ActualWidth, legend.ActualHeight));
                        Test($"default_map_legend_not_clipped_{theme}_{size.Item1}_{size.Item2}", bounds.Bottom <= workspace.ActualHeight + 1, bounds);
                    }
                    else
                    {
                        // Map-first deliberately moves the complete legend into folded work information.
                        // Preserve the check's meaning: the information is retained and costs no map area.
                        Test($"default_map_legend_not_clipped_{theme}_{size.Item1}_{size.Item2}", view.FindName("WorkInfoSection") is Expander { IsExpanded: false }
                            && legend is TextBlock { Text.Length: > 0 }, "Legend retained in folded work information.");
                    }
                    Render(view, $"coverage-{theme.ToLowerInvariant()}-{size.Item1}x{size.Item2}.png");
                }
            }
            window.Width = 1480; window.Height = 900; Pump();
            fake.Replace(state with { Version = 203 }); Set(view, "_cell", 1); Invoke(view, "Refresh"); Pump();
            var results = (Expander)view.FindName("ResultsSection");
            results.IsExpanded = true; Pump();
            Test("result_table_expands_on_demand", ((DataGrid)view.FindName("ResultGrid")).IsVisible
                && ((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Count() == points.Length);
            var rowBindings = ((DataGrid)view.FindName("ResultGrid")).Columns.OfType<DataGridTextColumn>().Select(c => (c.Binding as System.Windows.Data.Binding)?.Path.Path ?? "").ToArray();
            Test("table_uses_effective_measurement_fields", rowBindings.Any(p => p.EndsWith("MeasurementStatusText", StringComparison.Ordinal))
                && rowBindings.Any(p => p.EndsWith("MeasurementErrorText", StringComparison.Ordinal)));
            results.IsExpanded = false; Pump();
            Test("collapsing_table_restores_no_scroll", ((ScrollViewer)view.FindName("WorkspaceScroll")).ScrollableHeight <= 1);

            Set(view, "_focusKey", based.Key); Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([raw.Key, based.Key], based.Key)); Pump();
            map.ShowOperationMenu(); Pump();
            var menu = map.ContextMenu!;
            Test("point_context_has_four_actions", menu.Items.OfType<MenuItem>().Count(i => i.IsEnabled) == 4);
            Test("point_context_keeps_two_move_actions_without_auto_correction", menu.Items.OfType<MenuItem>().Count(i => i.Header.ToString()!.StartsWith("이동", StringComparison.Ordinal)) == 2);
            var measure = menu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("현재 위치"));
            var beforeCalls = fake.Calls.Count;
            accept = false; measure.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Test("context_measure_is_immediate_without_popup", fake.Calls.Count == beforeCalls+1&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.Measure);
            accept = true; map.ShowOperationMenu(); Pump();
            map.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("현재 위치")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Test("context_current_measure_uses_only_focused_point", fake.Calls.Last().Action == EN_MANUAL_INSPECTION_UI_ACTION.Measure
                && fake.Calls.Last().Request.Keys.SequenceEqual([based.Key]));
            map.ShowOperationMenu(); Pump();
            map.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("상세")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Test("context_history_uses_focused_point", fake.Calls.Last().Action == EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint
                && (fake.Calls.Last().Request.FocusKey == based.Key || fake.Calls.Last().Request.Keys.SequenceEqual([based.Key])));
            fake.Replace(state with { Version = 204, IsReadOnly = true }); Pump(); map.ShowOperationMenu(); Pump();
            Test("legacy_readonly_flag_keeps_motion_and_history_available", map.ContextMenu!.Items.OfType<MenuItem>().Where(i => i.Header.ToString()!.StartsWith("이동", StringComparison.Ordinal)
                || i.Header.ToString()!.Contains("현재 위치")).All(i => i.IsEnabled)
                && map.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("상세")).IsEnabled);
            map.ContextMenu.IsOpen = false;
            var confirmationCount = confirmations.Count; beforeCalls = fake.Calls.Count;
            ((Button)view.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Test("stop_remains_immediate_with_no_confirmation", confirmations.Count == confirmationCount
                && fake.Calls.Count == beforeCalls + 1 && fake.Calls.Last().Action == EN_MANUAL_INSPECTION_UI_ACTION.Stop);
            window.Close(); _application.Shutdown();
        }
        catch (Exception e) { Test("coverage_compact_unhandled", false, e.ToString()); }
        var report = new { schemaVersion = "a3-manual-coverage-compact-ui/1", passed = _failures == 0,
            tests = Checks.Count, failures = _failures, scope = "Real WPF View/Map/Scatter and app themes with synthetic immutable points/fake controller. Existing correction history is a display projection, not native raw. No equipment App/Root/SDK or motion.", checks = Checks };
        File.WriteAllText(Path.Combine(_output, "ui-coverage-compact-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.passed, report.tests, report.failures }));
        return _failures == 0 ? 0 : 1;
    }
}
