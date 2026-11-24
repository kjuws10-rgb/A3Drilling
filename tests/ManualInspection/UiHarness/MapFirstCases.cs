using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int MapFirst()
    {
        var observations = new List<object>();
        try
        {
            LoadResources("Dark");
            var snapshot = Snapshot(240) with { Version = 701, IsSimulation = true, RecipeId = "A3 6GH · 2D 측정 결과 중심",
                RunId = "QA-G001 · 설비 연결 없는 화면 검사", Message = "", StatusText = "좌표 선택 · 우클릭 동작 · 좌측 더블클릭 맞춤" };
            var fake = new FakeController(snapshot);
            var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_, _, _) => false };
            Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
            var window = new Window { Width = 1120, Height = 560, Content = view, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show(); Pump(); SelectMeasurementView(view);
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            var inspector = (Expander)view.FindName("InspectorSection");
            var workInfo = (Expander)view.FindName("WorkInfoSection");
            var workspace = (ScrollViewer)view.FindName("WorkspaceScroll");
            var first = snapshot.Points.First(p => !p.IsMasked);
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([first.Key], first.Key)); Pump();

            (double Width, double Height, double Scale, string Name)[] layouts = [(1120, 560, 1, "compact"), (1480, 900, 1, "wide"),
                (1400d / 1.25, 900d / 1.25, 1.25, "dpi125"), (1400d / 1.5, 900d / 1.5, 1.5, "dpi150")];
            foreach (var theme in new[] { "Light", "Dark" })
            {
                LoadResources(theme);
                foreach (var layout in layouts)
                {
                    window.Width = layout.Width; window.Height = layout.Height;
                    inspector.IsExpanded = workInfo.IsExpanded = false;
                    ((Expander)view.FindName("ResultsSection")).IsExpanded = false;
                    ((Expander)view.FindName("ScatterSection")).IsExpanded = false;
                    Set(view, "_cell", 1); Set(view, "_head", 0); Set(view, "_query", ""); Set(view, "_judge", "ALL");
                    Invoke(view, "Refresh"); Pump(); map.ResetView(); Pump();
                    var suffix = theme + "_" + layout.Name;
                    var mapFrame = (Border)map.Parent;
                    var mapBounds = map.TransformToAncestor(view).TransformBounds(new Rect(map.RenderSize));
                    Test("mapfirst_inspector_and_secondary_sections_closed_" + suffix, !inspector.IsExpanded && !workInfo.IsExpanded
                        && !((Expander)view.FindName("ResultsSection")).IsExpanded && !((Expander)view.FindName("ScatterSection")).IsExpanded);
                    Test("mapfirst_map_uses_almost_full_width_" + suffix, map.ActualWidth >= view.ActualWidth * .89,
                        new { map.ActualWidth, viewWidth = view.ActualWidth });
                    Test("mapfirst_map_uses_main_vertical_area_" + suffix, map.ActualHeight >= view.ActualHeight * .64,
                        new { map.ActualHeight, viewHeight = view.ActualHeight });
                    if (layout.Name == "compact") Test("mapfirst_compact_at_least_1000x360_" + theme, map.ActualWidth >= 1000 && map.ActualHeight >= 360,
                        new { map.ActualWidth, map.ActualHeight });
                    Test("mapfirst_default_no_scroll_or_parent_clip_" + suffix, workspace.ScrollableHeight <= 1 && workspace.ScrollableWidth <= 1
                        && mapFrame.ActualHeight - mapFrame.BorderThickness.Top - mapFrame.BorderThickness.Bottom - mapFrame.Padding.Top - mapFrame.Padding.Bottom + 1 >= map.ActualHeight
                        && mapBounds.Bottom <= view.ActualHeight + 1);
                    var slots = Slots(map);
                    Test("mapfirst_every_current_cell_slot_fits_" + suffix, slots.Count == snapshot.Points.Count(p => p.CellNo == 1)
                        && slots.Values.All(rect => rect.Left >= -.1 && rect.Top >= -.1 && rect.Right <= map.ActualWidth + .1 && rect.Bottom <= map.ActualHeight + .1));
                    Test("mapfirst_information_remains_reachable_without_overlay_" + suffix, view.FindName("PointDetailsText") is TextBlock
                        && view.FindName("ResultGrid") is DataGrid && view.FindName("ScatterPlot") is CManualInspectionScatterControl && fake.Calls.Count == 0);
                    RenderAtScale(view, "map-first-" + theme.ToLowerInvariant() + "-" + layout.Name + ".png", layout.Scale);
                    observations.Add(new { theme, layout.Name, layout.Scale, viewWidth = view.ActualWidth, viewHeight = view.ActualHeight,
                        mapWidth = map.ActualWidth, mapHeight = map.ActualHeight, workspace.ScrollableHeight, workspace.ScrollableWidth });
                }
            }
            window.Width = 1480; window.Height = 900; Pump();
            inspector.IsExpanded = true; Pump();
            Test("mapfirst_inspector_opens_only_on_demand", ((ScrollViewer)view.FindName("InspectorScroll")).IsVisible && map.ActualWidth > 800 && fake.Calls.Count == 0);
            ((Expander)view.FindName("CorrectionSection")).IsExpanded = true; Pump();
            Render(view, "map-first-dark-inspector.png");
            ((Expander)view.FindName("CorrectionSection")).IsExpanded = false;
            var selected = Field<HashSet<string>>(view, "_selected").ToHashSet();
            inspector.IsExpanded = false; workInfo.IsExpanded = true; Pump();
            Test("mapfirst_work_info_expands_without_changing_selection_or_source", workInfo.IsExpanded && Field<HashSet<string>>(view, "_selected").SetEquals(selected) && fake.Calls.Count == 0);
            Render(view, "map-first-dark-work-info.png");
            workInfo.IsExpanded = false; ((Expander)view.FindName("ResultsSection")).IsExpanded = true; Pump();
            Test("mapfirst_result_table_still_available", ((DataGrid)view.FindName("ResultGrid")).IsVisible && ((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Any());
            ((Expander)view.FindName("ResultsSection")).IsExpanded = false;
            ((Expander)view.FindName("ScatterSection")).IsExpanded = true; Pump();
            Test("mapfirst_scatter_still_available", ((CManualInspectionScatterControl)view.FindName("ScatterPlot")).IsVisible && ((CManualInspectionScatterControl)view.FindName("ScatterPlot")).Metrics.Count > 0);
            ((Expander)view.FindName("ScatterSection")).IsExpanded = false; Pump();
            ((Expander)view.FindName("LeftInspectorSection")).IsExpanded = false; Pump();
            Test("mapfirst_closing_secondary_panels_restores_large_map", map.ActualWidth >= view.ActualWidth * .89 && map.ActualHeight >= view.ActualHeight * .64 && workspace.ScrollableHeight <= 1);

            var geometryBefore = Slots(map); var selectionBefore = Field<HashSet<string>>(view, "_selected").ToHashSet();
            map.ZoomAt(2.2, new Point(120, 90)); map.PanBy(new Vector(81, -23)); Pump();
            Test("mapfirst_single_click_does_not_reset_zoom", !(bool)Invoke(map, "TryResetOnDoubleClick", 1)! && Math.Abs(map.Zoom - 2.2) < 1e-10);
            PrimeGesture(map);
            Test("mapfirst_doubleclick_resets_zoom_pan_and_cleans_gesture", (bool)Invoke(map, "TryResetOnDoubleClick", 2)!
                && map.Zoom == 1 && Field<Vector>(map, "_pan") == default && !Field<bool>(map, "_gesture") && !map.IsMouseCaptured);
            Pump();
            Test("mapfirst_doubleclick_preserves_selection_and_geometry", Field<HashSet<string>>(view, "_selected").SetEquals(selectionBefore)
                && Slots(map).All(entry => geometryBefore[entry.Key] == entry.Value));
            Test("mapfirst_doubleclick_never_dispatches_motion_measurement_or_correction", fake.Calls.Count == 0);
            fake.Replace(fake.Snapshot with { Version = 702, IsBusy = true, IsReadOnly = true }); Pump();
            map.ZoomAt(1.8, new Point(100, 100)); map.PanBy(new Vector(-40, 30));
            Invoke(map, "TryResetOnDoubleClick", 2); Pump();
            Test("mapfirst_doubleclick_remains_visual_in_busy_readonly_state", map.Zoom == 1 && Field<Vector>(map, "_pan") == default && fake.Calls.Count == 0);
            fake.Replace(fake.Snapshot with { Version = 703, IsBusy = false, IsReadOnly = false }); Pump();
            var pair = snapshot.Points.Take(2).Select(point => point.Key).ToArray(); var clicked = snapshot.Points[2].Key;
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs(pair, pair[1])); Pump();
            Invoke(map, "RememberSelectionBeforeClick");
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([clicked], clicked));
            Invoke(map, "CompleteClickSelectionTracking", false, false);
            Invoke(map, "TryResetOnDoubleClick", 2); Pump();
            Test("mapfirst_two_click_sequence_restores_preclick_multiselection_and_focus", Field<HashSet<string>>(view, "_selected").SetEquals(pair)
                && Field<string?>(view, "_focusKey") == pair[1] && map.Zoom == 1 && fake.Calls.Count == 0);
            Field<HashSet<string>>(view, "_selected").Clear(); Set(view, "_focusKey", null!); Invoke(view, "Refresh"); Pump();
            Invoke(map, "RememberSelectionBeforeClick");
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([clicked], clicked));
            Invoke(map, "CompleteClickSelectionTracking", false, false);
            Invoke(map, "TryResetOnDoubleClick", 2); Pump();
            Test("mapfirst_two_click_sequence_restores_empty_selection_and_null_focus", Field<HashSet<string>>(view, "_selected").Count == 0
                && Field<string?>(view, "_focusKey") is null && fake.Calls.Count == 0);
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs(pair, pair[1])); Pump();
            Invoke(map, "RememberSelectionBeforeClick");
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([clicked], clicked));
            Invoke(map, "CompleteClickSelectionTracking", false, false);
            fake.Replace(fake.Snapshot with { Version = 704 }); Pump(); Invoke(map, "TryResetOnDoubleClick", 2); Pump();
            Test("mapfirst_snapshot_change_prevents_old_click_selection_restore", Field<HashSet<string>>(view, "_selected").SetEquals([clicked]) && fake.Calls.Count == 0);
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs(pair, pair[1])); Pump();
            Invoke(map, "RememberSelectionBeforeClick");
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([clicked], clicked));
            Invoke(map, "CompleteClickSelectionTracking", false, true);
            Invoke(map, "TryResetOnDoubleClick", 2); Pump();
            Test("mapfirst_drag_result_is_not_replaced_by_prior_click_selection", Field<HashSet<string>>(view, "_selected").SetEquals([clicked]) && fake.Calls.Count == 0);

            var tileSnapshot = Snapshot(4) with { Version = 710, Points = Snapshot(4).Points.Select((point, index) => point with
                { CellNo = 1, HeadNo = 1, Row = 0, Column = index, HasAppliedOffset = true, BaseXUm = new[] { 1d, 7d, 14d, 25d }[index], BaseYUm = 0,
                    IsMasked = false, IsProxy = false, Source = "MEASURED" }).ToArray() };
            var tileMap = new CManualInspectionMapControl();
            var tileWindow = new Window { Width = 1100, Height = 420, Content = tileMap, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            tileMap.Configure(tileSnapshot, tileSnapshot.Points, tileSnapshot.Points, [], null, null, "applied", "tile-qa"); tileWindow.Show(); Pump();
            var frameData = Field<List<(Rect Bounds, Rect Grid, string Title, string Extents)>>(tileMap, "_frames");
            Test("mapfirst_dense_group_grid_uses_full_group_width", frameData.Count == 1 && frameData.All(frame => frame.Grid.Width >= frame.Bounds.Width * .95));
            var tileSlots = Slots(tileMap);
            Test("mapfirst_dense_group_slots_cover_grid_area", Math.Abs(tileSlots.Values.Sum(rect => rect.Width * rect.Height) / frameData.Sum(frame => frame.Grid.Width * frame.Grid.Height) - 1) < 1e-8);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tileMap.ActualWidth), (int)Math.Ceiling(tileMap.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(tileMap);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            foreach (var point in tileSnapshot.Points)
            {
                var expected = ((SolidColorBrush)CManualInspectionDrawing.PointBrush(tileMap, point, "applied", tileSnapshot)).Color;
                var bounds = tileSlots[point.Key]; var same = 0; var samples = 0;
                for (var y = (int)Math.Ceiling(bounds.Top); y < (int)Math.Floor(bounds.Bottom); y += 2)
                    for (var x = (int)Math.Ceiling(bounds.Left); x < (int)Math.Floor(bounds.Right); x += 2)
                    {
                        var pixel = (y * bitmap.PixelWidth + x) * 4; samples++;
                        if (Math.Abs(pixels[pixel] - expected.B) <= 2 && Math.Abs(pixels[pixel + 1] - expected.G) <= 2 && Math.Abs(pixels[pixel + 2] - expected.R) <= 2) same++;
                    }
                var ratio = (double)same / samples;
                Test("mapfirst_actual_level_fill_covers_ninety_percent_" + point.Key, ratio >= .9, new { ratio, samples });
                observations.Add(new { kind = "measured-color-fill", point.Key, ratio, samples });
            }
            Render(tileMap, "map-first-four-level-tiles.png"); tileWindow.Close();

            var large = Snapshot(32000) with { Version = 720, RunId = "mapfirst-32000" };
            var watch = Stopwatch.StartNew(); fake.Replace(large); Set(view, "_cell", 0); Set(view, "_head", 0); Invoke(view, "Refresh"); Pump(); map.ResetView(); Pump(); watch.Stop();
            var largeSlots = Slots(map);
            Test("mapfirst_32000_points_are_not_dropped", largeSlots.Count == large.Points.Count);
            Test("mapfirst_32000_fit_keeps_all_slots_inside_map", largeSlots.Values.All(rect => rect.Left >= -.1 && rect.Top >= -.1 && rect.Right <= map.ActualWidth + .1 && rect.Bottom <= map.ActualHeight + .1));
            Test("mapfirst_32000_render_does_not_dispatch_equipment_work", fake.Calls.Count == 0);
            observations.Add(new { kind = "synthetic-large-layout", points = large.Points.Count, milliseconds = watch.Elapsed.TotalMilliseconds });
            Render(view, "map-first-dark-32000.png"); window.Close(); _application.Shutdown();
        }
        catch (Exception error) { Test("mapfirst_unhandled", false, error.ToString()); }
        var report = new { schemaVersion = "a3-manual-inspection-map-first-ui/1", passed = _failures == 0, tests = Checks.Count, failures = _failures,
            scope = "Source-linked WPF and current app themes, immutable synthetic points/fake controller. DPI is equivalent DIP/render emulation. Double-click helper is called by the real mouse-down handler; no physical input injection or equipment App/SDK/Vision/motion.", checks = Checks, observations };
        File.WriteAllText(Path.Combine(_output, "ui-map-first-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.passed, report.tests, report.failures })); return _failures == 0 ? 0 : 1;
    }
}
