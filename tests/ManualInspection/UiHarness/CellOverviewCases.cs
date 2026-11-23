using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int CellOverview()
    {
        var observations=new List<object>();
        try
        {
            LoadResources("Dark");
            RunScatterAxisChecks(observations);
            RunCellOverviewViewChecks(observations);
            RunExpandedScatterMapChecks(observations);
            RunOverviewPointerGestureChecks(observations);
            RunEmptyReviewMenuChecks();
            RunMapKeyboardChecks();
            RunPersistentCellLabelChecks(observations);
            RunMeasurementBlinkChecks(observations);
        }
        catch(Exception e){Test("celloverview_unexpected_exception",false,e.ToString());}
        var files=new[]{"Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs","Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs"};
        var hashes=files.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output,"ui-cell-overview-results.json"),JsonSerializer.Serialize(new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,observations,sourceHashes=hashes,
            scope="Actual source-linked WPF View/Map/Scatter and current themes; synthetic immutable Recipe cell geometry and measurement values; fake Controller. No equipment App/Root/vendor object, OS input injection, Vision/motion or script execution. DIP125/150 is equivalent space/rendering only."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Cell overview UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }
    private static void RunScatterAxisChecks(List<object> observations)
    {
        var seed=Snapshot(4).Points[1] with{IsMasked=false,IsProxy=false,Source="MEASURED",WorkingJudge="OK",HasAppliedOffset=false};
        CManualInspectionUiPoint At(string key,double x,double y)=>seed with{Key=key,WorkingXUm=x,WorkingYUm=y};
        var points=new[]{At("small-X",.00002,0),At("small-Y",0,-.00003),At("center",0,0)};
        var original=JsonSerializer.Serialize(points);var scatter=new CManualInspectionScatterControl();
        var window=new Window{Width=680,Height=470,Content=scatter,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        var events=new List<CManualInspectionSelectionEventArgs>();scatter.SelectionRequested+=(_,e)=>events.Add(e);
        scatter.Configure(points,[],false,5,10,"");window.Show();Pump();
        Test("scatter_auto_small_errors_are_not_forced_to_tolerance_or_5um_floor",scatter.IsAutoScale && scatter.MaxXUm>.00003 && scatter.MaxXUm<.001);
        Test("scatter_auto_axes_are_equal_origin_symmetric",Math.Abs(scatter.MinXUm+scatter.MaxXUm)<1e-15 && Math.Abs(scatter.MinYUm+scatter.MaxYUm)<1e-15 && scatter.MinXUm==scatter.MinYUm && scatter.MaxXUm==scatter.MaxYUm);
        var hits=Field<List<(string Key,Point Point)>>(scatter,"_hits");
        var origin=hits.Single(p=>p.Key=="center").Point;var xhit=hits.Single(p=>p.Key=="small-X").Point;var yhit=hits.Single(p=>p.Key=="small-Y").Point;
        Test("scatter_small_errors_use_same_x_y_pixel_scale",Math.Abs((xhit.X-origin.X)/.00002-(yhit.Y-origin.Y)/.00003)<1e-5);
        scatter.SetFixedHalfRange(.00001);
        Test("scatter_scale_change_drops_stale_pixel_hits_before_next_render",!(bool)Invoke(scatter,"SelectHit",xhit,false)!&&events.Count==0);
        Pump();
        Test("scatter_fixed_range_clips_hits_but_keeps_population_statistics",!scatter.IsAutoScale && scatter.Metrics.Count==3 && Field<List<(string Key,Point Point)>>(scatter,"_hits").Select(h=>h.Key).SequenceEqual(["center"]));
        Test("scatter_fixed_one_value_sets_both_symmetric_axes",scatter.MinXUm==-.00001&&scatter.MaxXUm==.00001&&scatter.MinYUm==-.00001&&scatter.MaxYUm==.00001);
        var outlier=new[]{At("large",120,-90),At("zero",0,0)};scatter.Configure(outlier,[],false,5,5,"");Pump();
        Test("scatter_fixed_range_does_not_jump_after_population_changes",!scatter.IsAutoScale&&scatter.MaxXUm==.00001&&scatter.Metrics.Count==2);
        scatter.ResetView();Pump();Test("scatter_fit_returns_to_auto_and_current_population",scatter.IsAutoScale&&scatter.MaxXUm>=120&&scatter.MaxXUm<150&&scatter.Zoom==1);
        scatter.Configure(points,[],false,5,5,"");Pump();Test("scatter_auto_follows_smaller_population",scatter.IsAutoScale&&scatter.MaxXUm<.001);
        var plot=Field<Rect>(scatter,"_plotBounds");var at=new Point(plot.Left+plot.Width*.75,plot.Top+plot.Height*.25);
        double X()=>scatter.MinXUm+(at.X-plot.Left)/plot.Width*(scatter.MaxXUm-scatter.MinXUm);
        double Y()=>scatter.MaxYUm-(at.Y-plot.Top)/plot.Height*(scatter.MaxYUm-scatter.MinYUm);
        var beforeX=X();var beforeY=Y();scatter.ZoomAt(2,at);Pump();
        Test("scatter_cursor_centered_zoom_preserves_under_cursor_value",Math.Abs(X()-beforeX)<1e-15&&Math.Abs(Y()-beforeY)<1e-15&&!scatter.IsAutoScale&&Math.Abs(scatter.Zoom-2)<1e-12);
        var range=(scatter.MinXUm,scatter.MaxXUm,scatter.MinYUm,scatter.MaxYUm);scatter.Configure(points,["center"],false,5,5,"");Pump();
        Test("scatter_selection_refresh_retains_fixed_zoom",range==(scatter.MinXUm,scatter.MaxXUm,scatter.MinYUm,scatter.MaxYUm));
        foreach(var factor in new[]{double.NaN,double.PositiveInfinity,0,-1})scatter.ZoomAt(factor,at);
        scatter.ZoomAt(2,new Point(double.NaN,0));
        Test("scatter_invalid_zoom_cannot_corrupt_view_or_population",range==(scatter.MinXUm,scatter.MaxXUm,scatter.MinYUm,scatter.MaxYUm)&&scatter.Metrics.Count==3);
        foreach(var invalid in new[]{0d,-1,double.NaN,double.PositiveInfinity})
        {
            var rejected=false;try{scatter.SetFixedHalfRange(invalid);}catch(ArgumentOutOfRangeException){rejected=true;}
            Test("scatter_invalid_fixed_half_range_rejected_"+invalid,rejected&&range==(scatter.MinXUm,scatter.MaxXUm,scatter.MinYUm,scatter.MaxYUm));
        }
        scatter.ResetView();Pump();Test("scatter_reset_does_not_select_or_send_work",events.Count==0&&JsonSerializer.Serialize(points)==original);
        foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(scatter,"scatter-small-auto-"+theme.ToLowerInvariant()+".png");}
        scatter.Configure([At("allzero",0,0)],[],false,5,5,"");scatter.ResetView();Pump();
        Test("scatter_all_zero_auto_range_is_finite_and_nonzero",scatter.IsAutoScale&&double.IsFinite(scatter.MaxXUm)&&scatter.MaxXUm>0&&Field<List<(string Key,Point Point)>>(scatter,"_hits").Count==1);
        scatter.Configure([],[],false,5,5,"empty");scatter.ResetView();Pump();
        Test("scatter_empty_population_has_no_stale_hits",scatter.Metrics.Count==0&&Field<List<(string Key,Point Point)>>(scatter,"_hits").Count==0&&double.IsFinite(scatter.MaxXUm));
        observations.Add(new{kind="small-error-axis",maximumErrorUm=.00003,actualAutoHalfRange=points.Max(p=>Math.Max(Math.Abs(p.MeasurementXUm!.Value),Math.Abs(p.MeasurementYUm!.Value)))*1.12});
        window.Close();
    }
    private static void RunCellOverviewViewChecks(List<object> observations)
    {
        var state=OverviewSnapshot();var serialized=JsonSerializer.Serialize(state.Points);
        var fake=new FakeController(state);var descriptions=new List<string>();
        var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return true;}};
        Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
        var window=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        window.Show();Pump();var map=(CManualInspectionMapControl)view.FindName("PositionMap");var scatter=(CManualInspectionScatterControl)view.FindName("ScatterPlot");
        void Refresh(){Invoke(view,"Refresh");Pump();}
        void Choose(int cell){Invoke(view,"SetMapCellScope",cell);Pump();}
        void Click(string name){((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
        Test("overview_preserves_first_cell_landing",Field<int>(view,"_cell")==state.Points[0].CellNo);
        Choose(0);
        Test("overview_all_cells_scope_is_available",Field<int>(view,"_cell")==0 && Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all").Count==state.Points.Count);
        var slots=Slots(map);var centres=state.Points.GroupBy(p=>p.CellNo).ToDictionary(g=>g.Key,g=>new Point(g.Average(p=>slots[p.Key].X+slots[p.Key].Width/2),g.Average(p=>slots[p.Key].Y+slots[p.Key].Height/2)));
        Test("overview_uses_recipe_position_instead_of_cell_number_order",centres[3].X<centres[10].X && centres[8].X<centres[22].X && centres[3].Y<centres[8].Y && centres[10].Y<centres[22].Y);
        Test("overview_all_slots_fit_viewport",slots.Count==state.Points.Count && slots.Values.All(r=>r.Left>=-.1&&r.Top>=-.1&&r.Right<=map.ActualWidth+.1&&r.Bottom<=map.ActualHeight+.1));
        Test("overview_map_preserves_map_first_compact_space",map.ActualWidth>=1000&&map.ActualHeight>=360&&((ScrollViewer)view.FindName("WorkspaceScroll")).ScrollableHeight<=1);
        Point HeaderAt(int cell)
        {
            var rect=Field<List<(int CellNo,Rect Header,Rect AllCells)>>(map,"_cellHeaders").Single(h=>h.CellNo==cell).Header;
            return new((rect.Left+rect.Width/2)*map.Zoom+map.Pan.X,(rect.Top+rect.Height/2)*map.Zoom+map.Pan.Y);
        }
        var selected=Field<HashSet<string>>(view,"_selected");
        void DragCell(int cell)
        {
            var bounds=state.Points.Where(p=>p.CellNo==cell).Select(p=>Slots(map)[p.Key]).Aggregate(Rect.Union);
            Point Screen(Point p)=>new(p.X*map.Zoom+map.Pan.X,p.Y*map.Zoom+map.Pan.Y);
            Invoke(map,"BeginPointerGesture",Screen(bounds.TopLeft),ModifierKeys.Control,1);
            Invoke(map,"UpdatePointerGesture",Screen(bounds.BottomRight));
            Invoke(map,"CompletePointerGesture",Screen(bounds.BottomRight));Pump();
        }
        DragCell(3);
        Test("ctrl_cell_drag_selects_whole_cell_without_navigation_or_work",Field<int>(view,"_cell")==0&&selected.SetEquals(state.Points.Where(p=>p.CellNo==3).Select(p=>p.Key))&&fake.Calls.Count==0);
        DragCell(8);
        Test("ctrl_cell_drag_combines_multiple_cells",selected.Count==32&&selected.All(k=>state.Points.Single(p=>p.Key==k).CellNo is 3 or 8));
        Invoke(map,"ActivateCellHeader",HeaderAt(3),true);Pump();
        Test("ctrl_cell_header_navigates_without_toggling_existing_measurement_selection",Field<int>(view,"_cell")==3&&selected.Count==32&&fake.Calls.Count==0);Choose(0);
        var c10=state.Points.First(p=>p.CellNo==10);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([c10.Key],c10.Key,true));Pump();
        DragCell(10);
        Test("ctrl_partially_selected_cell_drag_adds_remainder_instead_of_xor",selected.Count==48&&state.Points.Where(p=>p.CellNo==10).All(p=>selected.Contains(p.Key)));
        var beforeDisabled=selected.ToHashSet();DragCell(22);
        Test("disabled_cell_drag_does_not_select_masked_targets",selected.SetEquals(beforeDisabled)&&Field<int>(view,"_cell")==0&&fake.Calls.Count==0);
        fake.Replace(state with{Version=902});Pump();map.RestoreView(1.7,new Vector(29,-11));Pump();
        Invoke(map,"ActivateCellHeader",HeaderAt(10),false);Pump();
        Test("normal_cell_header_hit_is_correct_after_zoom_pan",Field<int>(view,"_cell")==10&&fake.Calls.Count==0);
        var allRect=Field<List<(int CellNo,Rect Header,Rect AllCells)>>(map,"_cellHeaders").First(h=>!h.AllCells.IsEmpty).AllCells;
        Invoke(map,"ActivateCellHeader",new Point(allRect.Left+allRect.Width/2,allRect.Top+allRect.Height/2),false);Pump();
        Test("single_cell_header_all_cells_button_restores_overview",Field<int>(view,"_cell")==0&&Slots(map).Count==state.Points.Count);
        var selectedBeforeFit=selected.ToHashSet();map.RestoreView(2,new Vector(17,-8));Pump();Invoke(map,"TryResetOnDoubleClick",2);Pump();
        Test("overview_double_fit_preserves_selection_and_never_dispatches_work",map.Zoom==1&&map.Pan==default&&selected.SetEquals(selectedBeforeFit)&&fake.Calls.Count==0);
        Invoke(view,"ClearSelection_Click",view,new RoutedEventArgs());Pump();
        var before=Slots(map);Set(view,"_judge","NG");Refresh();
        Test("overview_judge_filter_preserves_real_cell_geometry",Slots(map).All(p=>before[p.Key]==p.Value)&&Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_visible").Count<state.Points.Count);
        Set(view,"_judge","ALL");Refresh();
        foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"cell-overview-"+theme.ToLowerInvariant()+"-compact.png");}
        foreach(var layout in new[]{(W:1480d,H:900d,Scale:1d,Name:"wide"),(W:1400/1.25,H:900/1.25,Scale:1.25,Name:"dpi125"),(W:1400/1.5,H:900/1.5,Scale:1.5,Name:"dpi150")})
        {
            window.Width=layout.W;window.Height=layout.H;Pump();map.ResetView();Pump();
            Test("overview_scaled_view_has_every_slot_and_no_default_scroll_"+layout.Name,Slots(map).Count==state.Points.Count&&Slots(map).Values.All(r=>r.Left>=-.1&&r.Top>=-.1&&r.Right<=map.ActualWidth+.1&&r.Bottom<=map.ActualHeight+.1)&&((ScrollViewer)view.FindName("WorkspaceScroll")).ScrollableHeight<=1);
            foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();RenderAtScale(view,"cell-overview-"+theme.ToLowerInvariant()+"-"+layout.Name+".png",layout.Scale);}
        }
        window.Width=1480;window.Height=1000;Pump();
        Choose(10);Test("cell_navigation_enters_single_cell_without_device_request",Field<int>(view,"_cell")==10&&Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all").All(p=>p.CellNo==10)&&fake.Calls.Count==0);
        var first=state.Points.First(p=>p.CellNo==3&&!p.IsMasked);var other=state.Points.First(p=>p.CellNo==8&&!p.IsMasked);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key,other.Key],first.Key));Pump();
        ((Button)view.FindName("ScatterCellsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        var scope=Field<HashSet<int>>(view,"_scatterCells");
        Test("selected_cells_scatter_initially_follows_selected_points_cells",Field<bool>(view,"_scatterFollowSelection")&&scope.SetEquals([3,8])&&scatter.Metrics.Total==32);
        ((Expander)view.FindName("ScatterSection")).IsExpanded=true;Pump();
        var scatterHit=Field<List<(string Key,Point Point)>>(scatter,"_hits").Single(h=>h.Key==first.Key).Point;
        Invoke(scatter,"SelectHit",scatterHit,false);Pump();
        Test("scatter_first_click_can_temporarily_focus_one_cell",selected.Count==1&&scatter.Metrics.Total==16);
        Invoke(scatter,"TryResetOnDoubleClick",2);Pump();
        Test("scatter_double_fit_restores_original_multi_cell_selection_and_population",selected.SetEquals([first.Key,other.Key])&&scope.SetEquals([3,8])&&scatter.Metrics.Total==32&&scatter.IsAutoScale&&scatter.Zoom==1&&fake.Calls.Count==0);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key,other.Key],null,clearFocus:true));Pump();
        scatterHit=Field<List<(string Key,Point Point)>>(scatter,"_hits").Single(h=>h.Key==first.Key).Point;Invoke(scatter,"SelectHit",scatterHit,false);Pump();Invoke(scatter,"TryResetOnDoubleClick",2);Pump();
        Test("scatter_double_fit_preserves_explicit_null_focus",Field<string?>(view,"_focusKey") is null&&selected.SetEquals([first.Key,other.Key])&&scatter.Metrics.Total==32);
        scatterHit=Field<List<(string Key,Point Point)>>(scatter,"_hits").Single(h=>h.Key==first.Key).Point;Invoke(scatter,"SelectHit",scatterHit,false);Pump();
        fake.Replace(fake.Snapshot with{Version=fake.Snapshot.Version+1});Pump();Invoke(scatter,"TryResetOnDoubleClick",2);Pump();
        Test("scatter_double_fit_cannot_restore_old_selection_after_external_source_version",selected.Count==1&&scatter.Metrics.Total==16&&scatter.IsAutoScale);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key,other.Key],first.Key));Pump();
        scatterHit=Field<List<(string Key,Point Point)>>(scatter,"_hits").Single(h=>h.Key==first.Key).Point;Invoke(scatter,"SelectHit",scatterHit,false);Pump();
        ((Button)view.FindName("ScatterHeadButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Invoke(scatter,"TryResetOnDoubleClick",2);Pump();
        Test("scatter_double_fit_cannot_restore_old_cells_after_manual_scope_change",Field<string>(view,"_scatterScope")=="HEAD"&&selected.Count==1&&scatter.IsAutoScale);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key,other.Key],first.Key));Pump();
        ((Button)view.FindName("ScatterCellsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        scatterHit=Field<List<(string Key,Point Point)>>(scatter,"_hits").Single(h=>h.Key==first.Key).Point;Invoke(scatter,"SelectHit",scatterHit,false);Pump();
        Invoke(scatter,"SelectHit",new Point(-100,-100),false);Invoke(scatter,"TryResetOnDoubleClick",2);Pump();
        Test("blank_scatter_double_fit_does_not_restore_a_stale_earlier_click",selected.Count==1&&scatter.Metrics.Total==16);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key,other.Key],first.Key));Pump();
        ((Expander)view.FindName("ScatterSection")).IsExpanded=false;Pump();
        var third=state.Points.First(p=>p.CellNo==10&&!p.IsMasked);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([third.Key],third.Key,true,true));Pump();
        Test("selected_cells_scatter_live_addition_uses_whole_cell_population",scope.SetEquals([3,8,10])&&scatter.Metrics.Total==48&&Field<HashSet<string>>(view,"_selected").Count==3);
        Set(view,"_judge","NG");Set(view,"_head",2);Set(view,"_query","unlikely-search");Refresh();
        Test("selected_cells_scatter_ignores_map_point_filters",scatter.Metrics.Total==48&&scope.SetEquals([3,8,10]));
        var checkBoxes=Descendants((FrameworkElement)view.FindName("ScatterScopeControls")).OfType<CheckBox>().ToArray();
        checkBoxes.Single(c=>c.Content.ToString()=="C10").IsChecked=false;Pump();
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([third.Key],third.Key));Pump();
        Test("manual_cell_checkboxes_take_priority_over_point_selection",!Field<bool>(view,"_scatterFollowSelection")&&scope.SetEquals([3,8])&&scatter.Metrics.Total==32);
        Descendants((FrameworkElement)view.FindName("ScatterScopeControls")).OfType<Button>().Single(b=>b.Content.ToString()!.Contains("따라가기")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("cell_follow_button_restores_selection_derived_scope",Field<bool>(view,"_scatterFollowSelection")&&scope.SetEquals([10])&&scatter.Metrics.Total==16);
        Invoke(view,"ClearSelection_Click",view,new RoutedEventArgs());Pump();
        Test("empty_point_selection_does_not_fall_back_to_all_cells_scatter",scope.Count==0&&scatter.Metrics.Total==0);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key,other.Key],first.Key));Pump();
        ((Expander)view.FindName("ScatterSection")).IsExpanded=true;Pump();
        ((TextBox)view.FindName("ScatterRangeInput")).Text="0.0002";Click("ScatterRangeApplyButton");
        Test("scatter_range_UI_fixes_equal_symmetric_axes",!scatter.IsAutoScale&&scatter.MinXUm==-.0002&&scatter.MaxXUm==.0002&&scatter.MinYUm==-.0002&&scatter.MaxYUm==.0002);
        Click("ScatterZoomInButton");Test("scatter_zoom_UI_changes_only_display",!scatter.IsAutoScale&&scatter.MaxXUm<.0002&&fake.Calls.Count==0);
        Click("ScatterFitButton");Test("scatter_fit_UI_returns_to_auto_population",scatter.IsAutoScale&&scatter.Zoom==1&&scatter.Metrics.Total==32);
        ((TextBox)view.FindName("ScatterRangeInput")).Text="NaN";var prior=scatter.MaxXUm;Click("ScatterRangeApplyButton");
        Test("scatter_invalid_UI_range_retains_view_and_describes_error",scatter.MaxXUm==prior&&((TextBlock)view.FindName("MessageText")).Text.Contains("유한"));
        ((TextBox)view.FindName("ScatterRangeInput")).Text="0.0002";Set(view,"_query","");Set(view,"_judge","ALL");Set(view,"_head",0);Choose(0);fake.Replace(fake.Snapshot with{Version=fake.Snapshot.Version+1});Pump();
        Test("overview_remains_recipe_geometry_while_scatter_is_open",Field<int>(view,"_cell")==0&&Field<bool>(map,"_showCellOverview")&&Field<bool>(map,"_hasRecipeLayout"));
        foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"cell-selected-scatter-"+theme.ToLowerInvariant()+".png");}
        ((Expander)view.FindName("ScatterSection")).IsExpanded=false;Choose(3);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([first.Key],first.Key));Pump();map.RestoreView(1.7,new Vector(31,-19));
        Set(view,"_query","C3");Set(view,"_sort","error");Set(view,"_color","level");Refresh();
        ((Expander)view.FindName("WorkInfoSection")).IsExpanded=true;((Expander)view.FindName("InspectorSection")).IsExpanded=true;Pump();
        ((Expander)view.FindName("CorrectionSection")).IsExpanded=true;((Expander)view.FindName("PointDetailsSection")).IsExpanded=true;Pump();
        scatter.SetFixedHalfRange(.0007);scatter.ZoomAt(1.25,new Point(scatter.ActualWidth/2,scatter.ActualHeight/2));var nativeRange=(scatter.MinXUm,scatter.MaxXUm,scatter.MinYUm,scatter.MaxYUm);
        fake.OnExecute=(action,_)=>
        {
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile)fake.Replace(state with{Version=state.Version+1,CanReturnToPreviousWorkspace=true,SourcePath="synthetic-file.csv"});
            else if(action==EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace)fake.Replace(state with{Version=state.Version+2});
        };
        ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,new CManualInspectionUiRequest{ExpectedVersion=fake.Snapshot.Version})!).GetAwaiter().GetResult();Pump();
        Choose(10);Invoke(view,"ClearSelection_Click",view,new RoutedEventArgs());map.RestoreView(3,new Vector(-22,43));Set(view,"_query","");Set(view,"_color","applied");Set(view,"_sort","shot");scatter.ResetView();
        ((Expander)view.FindName("WorkInfoSection")).IsExpanded=false;((Expander)view.FindName("InspectorSection")).IsExpanded=false;Refresh();
        ((Expander)view.FindName("CorrectionSection")).IsExpanded=false;((Expander)view.FindName("PointDetailsSection")).IsExpanded=false;Pump();
        Invoke(view,"Files_Click",view,new RoutedEventArgs());var menu=((Button)view.FindName("FilesButton")).ContextMenu!;
        var items=menu.Items.OfType<MenuItem>().ToArray();
        Test("file_menu_exposes_return_portable_offset_and_measurement_files_without_session_or_recent_list",items.Length==5&&items.Count(i=>i.Header.ToString()!.Contains("통합 Offset"))==2&&items.Any(i=>i.Header.ToString()!.Contains("돌아가기"))&&items.All(i=>!i.Header.ToString()!.Contains("세션")&&!i.Header.ToString()!.Contains("최근")));
        view.ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return false;};
        items.Single(i=>i.Header.ToString()!.Contains("돌아가기")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        Test("file_return_is_immediate_without_confirmation",fake.Calls.Count==2&&!fake.Snapshot.CanReturnToPreviousWorkspace&&Field<int>(view,"_cell")==3&&descriptions.Count==0);
        view.ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return true;};
        menu.IsOpen=false; // The first click already restored the native workspace; no second return is dispatched.
        Test("return_restores_native_cell_selection_filters_and_color",Field<int>(view,"_cell")==3&&Field<HashSet<string>>(view,"_selected").SetEquals([first.Key])&&Field<string>(view,"_query")=="C3"&&Field<string>(view,"_sort")=="error"&&Field<string>(view,"_color")=="level");
        Test("return_restores_native_map_zoom_pan_and_scatter_fixed_axes",Math.Abs(map.Zoom-1.7)<1e-12&&map.Pan==new Vector(31,-19)&&!scatter.IsAutoScale&&Math.Abs(scatter.Zoom-1.25)<1e-12&&nativeRange==(scatter.MinXUm,scatter.MaxXUm,scatter.MinYUm,scatter.MaxYUm));
        Test("return_restores_native_panel_fold_state",((Expander)view.FindName("WorkInfoSection")).IsExpanded&&((Expander)view.FindName("InspectorSection")).IsExpanded&&((Expander)view.FindName("CorrectionSection")).IsExpanded&&((Expander)view.FindName("PointDetailsSection")).IsExpanded);
        Test("file_return_dispatch_never_performs_implicit_move_measure_or_apply",fake.Calls.Select(c=>c.Action).SequenceEqual([EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace])&&descriptions.Count==0&&JsonSerializer.Serialize(state.Points)==serialized);
        fake.Replace(fake.Snapshot with{Version=fake.Snapshot.Version+1,HasIncomplete=true,CanReturnToPreviousWorkspace=true,RemainingCount=1});Pump();
        Invoke(view,"Files_Click",view,new RoutedEventArgs());menu=((Button)view.FindName("FilesButton")).ContextMenu!;items=menu.Items.OfType<MenuItem>().ToArray();
        Test("incomplete_measurement_disables_file_replacement_and_return_but_keeps_exports",items.Where(i=>!i.Header.ToString()!.Contains("내보내기")).All(i=>!i.IsEnabled)&&items.Where(i=>i.Header.ToString()!.Contains("내보내기")).All(i=>i.IsEnabled));menu.IsOpen=false;
        var calls=fake.Calls.Count;
        foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace})
            ((Task)Invoke(view,"Execute",action,new CManualInspectionUiRequest{ExpectedVersion=fake.Snapshot.Version})!).GetAwaiter().GetResult();
        Test("incomplete_file_actions_are_rejected_by_common_ui_gate",fake.Calls.Count==calls&&((TextBlock)view.FindName("MessageText")).Text.Contains("잔여"));
        var metadataCell=new CManualInspectionUiCellLayout{CellNo=99,Enabled=false,BoundaryMm=[new(600,780),new(750,780),new(750,910),new(600,910)]};
        fake.Replace(state with{Version=906,CellLayouts=state.CellLayouts.Append(metadataCell).ToArray()});Pump();Choose(0);
        Invoke(map,"ActivateCellHeader",HeaderAt(99),false);Pump();
        Test("geometry_only_disabled_cell_is_navigable_as_empty_view",Field<int>(view,"_cell")==99&&Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all").Count==0&&Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_visible").Count==0&&fake.Calls.Count==calls);
        observations.Add(new{kind="overview-layout",mapWidth=map.ActualWidth,mapHeight=map.ActualHeight,cells=state.CellLayouts.Count,points=state.Points.Count,centres});
        window.Close();
        RunRotatedCellChecks(state,observations);
    }
    private static void RunRotatedCellChecks(CManualInspectionUiSnapshot state,List<object> observations)
    {
        var point=state.Points.First() with{Key="ROTATED",CellNo=90,Row=0,Column=0,IsMasked=false};
        var rotated=state with{Points=[point],GlassWidthMm=0,GlassHeightMm=0,CellLayouts=[new(){CellNo=90,BoundaryMm=[new(0,0),new(300,120),new(240,420),new(-60,300)]}]};
        var map=new CManualInspectionMapControl();var requests=0;map.OperationRequested+=(_,_)=>requests++;
        var window=new Window{Width=700,Height=500,Content=map,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        map.Configure(rotated,rotated.Points,rotated.Points,[],null,null,"applied","rotated",showCellOverview:true);window.Show();Pump();
        var polygons=Field<Dictionary<string,Point[]>>(map,"_overviewPolygons");var polygon=polygons[point.Key];var centre=new Point(polygon.Average(p=>p.X),polygon.Average(p=>p.Y));
        Test("rotated_cell_uses_affine_polygon_instead_of_axis_aligned_fill",Math.Abs(polygon[1].Y-polygon[0].Y)>10&&Math.Abs(polygon[3].X-polygon[0].X)>10);
        Test("rotated_cell_hit_finds_real_polygon_center",((CManualInspectionUiPoint?)Invoke(map,"Hit",centre))?.Key==point.Key);
        var bounds=Slots(map)[point.Key];var outside=new Point(bounds.Left+1,bounds.Top+1);
        Test("rotated_cell_bbox_corner_does_not_hit_outside_polygon",Invoke(map,"Hit",outside) is null);
        var old=polygon.ToArray();var flipped=rotated with{Version=rotated.Version+1,CellLayoutYDown=false};
        map.Configure(flipped,flipped.Points,flipped.Points,[],null,null,"applied","rotated-flip",showCellOverview:true);Pump();polygon=Field<Dictionary<string,Point[]>>(map,"_overviewPolygons")[point.Key];
        Test("recipe_y_direction_metadata_flips_only_vertical_orientation",Math.Sign(old[1].Y-old[0].Y)==-Math.Sign(polygon[1].Y-polygon[0].Y)&&Math.Sign(old[1].X-old[0].X)==Math.Sign(polygon[1].X-polygon[0].X)&&requests==0);
        Render(map,"cell-rotated-y-up.png");observations.Add(new{kind="rotated-recipe-cell",boundary=rotated.CellLayouts[0].BoundaryMm,pointCount=rotated.Points.Count});window.Close();
    }
    private static CManualInspectionUiSnapshot OverviewSnapshot()
    {
        (int Cell,double X,double Y,double W,double H,bool Enabled)[] cells=[(3,100,150,400,550,true),(10,800,150,450,550,true),(8,100,950,400,700,true),(22,800,950,450,700,false)];
        var seed=Snapshot(4).Points[1];
        var points=cells.SelectMany(cell=>Enumerable.Range(0,16).Select(i=>seed with{Key=$"C{cell.Cell}/H{i%2+1}/S{i+1}/B11",CellNo=cell.Cell,HeadNo=i%2+1,ShotNo=i+1,Row=i/4,Column=i%4,
            DesignXmm=cell.X+cell.W*(i%4+.5)/4,DesignYmm=cell.Y+cell.H*(i/4+.5)/4,IsMasked=!cell.Enabled,IsProxy=false,Source="MEASURED",ReferenceKey="",WorkingJudge=i%5==0?"NG":"OK",
            WorkingXUm=(i-8)*.00001,WorkingYUm=(i%4-2)*.00002,OriginalXUm=(i-8)*.00001,OriginalYUm=(i%4-2)*.00002,HasAppliedOffset=true,BaseXUm=i%4*7,BaseYUm=0})).ToArray();
        return new(){Version=901,RecipeId="합성 Recipe 실제배치 계약 · 실물규격 아님",RunId="QA-OVERVIEW",ActiveRevisionId="QA-R",GlassWidthMm=1400,GlassHeightMm=1850,CellLayoutYDown=true,Points=points,
            CellLayouts=cells.Select(cell=>new CManualInspectionUiCellLayout{CellNo=cell.Cell,Enabled=cell.Enabled,BoundaryMm=[new(cell.X,cell.Y),new(cell.X+cell.W,cell.Y),new(cell.X+cell.W,cell.Y+cell.H),new(cell.X,cell.Y+cell.H)]}).ToArray(),Message="",StatusText="합성 전체 Cell 배치 · 설비 실행 없음"};
    }
    private static void RunOverviewPointerGestureChecks(List<object> observations)
    {
        var state=OverviewSnapshot();var map=new CManualInspectionMapControl();
        var navigation=new List<int>();var selection=new List<CManualInspectionSelectionEventArgs>();var work=0;
        map.CellRequested+=(_,cell)=>navigation.Add(cell);map.SelectionRequested+=(_,args)=>selection.Add(args);map.OperationRequested+=(_,_)=>work++;
        var window=new Window{Width=900,Height=700,Content=map,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        window.Show();Pump();
        void Configure(CManualInspectionUiSnapshot snapshot,IReadOnlyList<CManualInspectionUiPoint>? visible=null,bool overview=true)
        {
            map.ResetView();map.IsPanTool=false;
            map.Configure(snapshot,snapshot.Points,visible??snapshot.Points,[],null,null,"level","pointer-regression",overview);Pump();
            navigation.Clear();selection.Clear();
        }
        Point Screen(Point p)=>new(p.X*map.Zoom+map.Pan.X,p.Y*map.Zoom+map.Pan.Y);
        Point Centre(string key)
        {var r=Slots(map)[key];return Screen(new Point(r.Left+r.Width/2,r.Top+r.Height/2));}
        Point Header(int cell)
        {var r=Field<List<(int CellNo,Rect Header,Rect AllCells)>>(map,"_cellHeaders").First(h=>h.CellNo==cell).Header;return Screen(new(r.Left+r.Width/2,r.Top+r.Height/2));}
        void Down(Point at,ModifierKeys modifiers=ModifierKeys.None)=>Invoke(map,"BeginPointerGesture",at,modifiers,1);
        void Up(Point at)=>Invoke(map,"CompletePointerGesture",at);
        void Click(Point at,ModifierKeys modifiers=ModifierKeys.None){Down(at,modifiers);Up(at);Pump();}
        Configure(state);var target=state.Points.First(p=>p.CellNo==3&&p.Row==2&&p.Column==2);var at=Centre(target.Key);
        Down(at);Test("overview_point_press_defers_navigation_until_release",navigation.Count==0&&selection.Count==0);
        Up(at);Pump();Test("overview_point_single_click_opens_cell_instead_of_selecting_measurement",navigation.SequenceEqual([3])&&selection.Count==0&&work==0);
        Configure(state);at=Centre(target.Key);Click(at,ModifierKeys.Control);
        Test("overview_ctrl_point_click_also_opens_cell_without_measurement_selection",navigation.SequenceEqual([3])&&selection.Count==0&&work==0);
        Configure(state);Down(Header(3));Test("overview_header_press_can_still_become_drag",navigation.Count==0);Up(Header(3));Pump();
        Test("overview_header_release_opens_cell_once",navigation.SequenceEqual([3])&&selection.Count==0);
        Configure(state);Click(Header(3),ModifierKeys.Control);
        Test("overview_ctrl_header_click_opens_cell_without_whole_cell_toggle",navigation.SequenceEqual([3])&&selection.Count==0);

        Configure(state,[state.Points.First(p=>p.CellNo==10)]);at=Centre(target.Key);
        Test("overview_filtered_slot_is_not_a_measurement_hit",Invoke(map,"Hit",at) is null);
        Click(at);Test("overview_filtered_blank_inside_cell_still_navigates",navigation.SequenceEqual([3])&&selection.Count==0);
        Configure(state);var masked=state.Points.First(p=>p.CellNo==22&&p.Row==2&&p.Column==2);Click(Centre(masked.Key));
        Test("overview_masked_point_opens_its_cell_without_selecting_or_work",navigation.SequenceEqual([22])&&selection.Count==0&&work==0);
        Configure(state);at=Centre(target.Key);Down(at);Up(at+new Vector(2,1));
        Test("overview_small_pointer_jitter_remains_cell_click",navigation.SequenceEqual([3])&&selection.Count==0);
        Configure(state);at=Centre(target.Key);Down(at);Up(at+new Vector(4,0));
        Test("overview_four_dip_inclusive_jitter_remains_click",navigation.SequenceEqual([3])&&selection.Count==0);
        Configure(state);at=Centre(target.Key);Down(at);Up(at+new Vector(5,0));
        Test("overview_drag_threshold_prevents_navigation_even_without_move_event",navigation.Count==0&&selection.Count==1);
        Configure(state);at=Centre(target.Key);Down(at);Invoke(map,"UpdatePointerGesture",at+new Vector(20,20));Invoke(map,"UpdatePointerGesture",at);Up(at);
        Test("overview_drag_returned_to_origin_does_not_become_click",navigation.Count==0&&selection.Count==1);
        Configure(state);var cellRect=state.Points.Where(p=>p.CellNo==3).Select(p=>Slots(map)[p.Key]).Aggregate(Rect.Union);
        var dragStart=Screen(cellRect.TopLeft);var dragEnd=Screen(cellRect.BottomRight);Down(dragStart);Invoke(map,"UpdatePointerGesture",dragEnd);Up(dragEnd);Pump();
        Test("overview_header_started_drag_selects_cell_points_without_entering",navigation.Count==0&&selection.Count==1&&selection[0].Keys.Count==16&&!selection[0].Additive);
        Configure(state);Down(dragStart,ModifierKeys.Control);Up(dragEnd);Pump();
        Test("overview_ctrl_drag_preserves_additive_multi_selection",navigation.Count==0&&selection.Count==1&&selection[0].Keys.Count==16&&selection[0].Additive&&!selection[0].Toggle);

        Configure(state);var panStart=Header(3);Down(panStart,ModifierKeys.Alt);Invoke(map,"UpdatePointerGesture",panStart+new Vector(35,19));Up(panStart+new Vector(35,19));
        Test("overview_alt_drag_on_header_pans_without_entering_or_selecting",navigation.Count==0&&selection.Count==0&&map.Pan==new Vector(35,19));
        Configure(state);map.IsPanTool=true;panStart=Header(3);Down(panStart);Up(panStart+new Vector(-13,17));
        Test("overview_pan_tool_has_priority_over_cell_click",navigation.Count==0&&selection.Count==0&&map.Pan==new Vector(-13,17));
        Configure(state);at=Centre(target.Key);Down(at,ModifierKeys.Alt);Up(at);
        Test("overview_alt_click_does_not_navigate",navigation.Count==0&&selection.Count==0);
        Configure(state);at=Centre(target.Key);Down(at);Invoke(map,"OnLostMouseCapture",new MouseEventArgs(Mouse.PrimaryDevice,1));Up(at);
        Test("overview_capture_loss_discards_pending_navigation",navigation.Count==0&&selection.Count==0&&!Field<bool>(map,"_gesture")&&Private(map,"_overviewClickCell") is null);
        Click(at);Test("overview_new_click_after_capture_loss_can_navigate",navigation.SequenceEqual([3])&&selection.Count==0);
        Configure(state);at=Centre(target.Key);Down(at);map.Configure(state with{Version=state.Version+1},state.Points,state.Points,[],null,null,"level","pointer-regression",true);Up(at);
        Test("overview_source_refresh_cancels_pending_cell_click",navigation.Count==0&&selection.Count==0&&!Field<bool>(map,"_gesture"));
        Configure(state);at=Centre(target.Key);Down(at);map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));Up(at);
        Test("overview_unload_discards_pending_navigation",navigation.Count==0&&selection.Count==0&&!Field<bool>(map,"_gesture"));
        Configure(state);at=Centre(target.Key);Down(at);map.TryCancelGesture();Up(at);
        Test("overview_escape_cancel_discards_pending_navigation",navigation.Count==0&&selection.Count==0&&!Field<bool>(map,"_gesture"));
        Configure(state);at=Centre(target.Key);Down(at);Invoke(map,"OnMouseRightButtonDown",new MouseButtonEventArgs(Mouse.PrimaryDevice,1,MouseButton.Right));Up(at);
        Test("overview_right_press_cannot_complete_a_pending_left_navigation",navigation.Count==0&&!Field<bool>(map,"_gesture")&&work==0);
        if(map.ContextMenu is not null)map.ContextMenu.IsOpen=false;
        Configure(state);Click(new(-10,-10));
        Test("overview_outside_cell_click_does_not_select_or_navigate",navigation.Count==0&&selection.Count==0);
        Configure(state);Down(new(double.NaN,0));Up(new(1,1));
        Test("overview_invalid_pointer_cannot_create_navigation",navigation.Count==0&&selection.Count==0&&!Field<bool>(map,"_gesture"));

        Configure(state);map.RestoreView(1.7,new Vector(29,-11));Pump();at=Centre(target.Key);Click(at);
        Test("overview_body_navigation_uses_zoom_pan_transform",navigation.SequenceEqual([3])&&selection.Count==0);
        Configure(state);var direct=state with{Points=state.Points.Where(p=>p.CellNo==3).ToArray()};
        map.Configure(direct,direct.Points,direct.Points,[],null,null,"level","single-after-overview",false);Pump();map.RestoreView(2,new Vector(12,-8));Pump();
        Invoke(map,"BeginPointerGesture",new Point(100,100),ModifierKeys.None,2);Pump();
        Test("overview_second_double_click_down_fits_current_single_cell_without_work",map.Zoom==1&&map.Pan==default&&navigation.Count==0&&selection.Count==0&&work==0&&!Field<bool>(map,"_gesture"));

        var geometryOnly=state with{Points=[],CellLayouts=[new(){CellNo=99,Enabled=false,BoundaryMm=[new(0,0),new(200,0),new(200,300),new(0,300)]}],GlassWidthMm=0,GlassHeightMm=0};
        Configure(geometryOnly);var frame=Field<List<(Rect Bounds,Rect Grid,string Title,string Extents)>>(map,"_frames").Single().Bounds;
        Click(Screen(new(frame.Left+frame.Width/2,frame.Top+frame.Height/2)));
        Test("overview_geometry_only_cell_body_is_still_navigable",Field<bool>(map,"_hasRecipeLayout")&&navigation.SequenceEqual([99])&&selection.Count==0);
        var rotatedPoint=target with{Key="NAV-ROTATED",CellNo=90,Row=0,Column=0,IsMasked=false};
        var rotated=state with{Points=[rotatedPoint],CellLayouts=[new(){CellNo=90,BoundaryMm=[new(0,0),new(300,120),new(240,420),new(-60,300)]}],GlassWidthMm=0,GlassHeightMm=0};
        Configure(rotated);var polygon=Field<List<Point[]>>(map,"_overviewFrames").Single();var centre=new Point(polygon.Average(p=>p.X),polygon.Average(p=>p.Y));
        Click(Screen(centre));Test("overview_rotated_cell_interior_navigates",navigation.SequenceEqual([90])&&selection.Count==0);
        navigation.Clear();var rotatedBounds=Slots(map)[rotatedPoint.Key];var outside=Screen(new(rotatedBounds.Left+1,rotatedBounds.Bottom-1));
        Click(outside);Test("overview_rotated_cell_bbox_outside_shape_does_not_navigate",Invoke(map,"HitOverviewCell",outside) is null&&navigation.Count==0&&selection.Count==0);
        Configure(state with{CellLayouts=[]});var fallback=Slots(map)[target.Key];Click(Screen(new(fallback.Left+fallback.Width/2,fallback.Top+fallback.Height/2)));
        Test("overview_missing_recipe_geometry_keeps_logical_cell_navigation",!Field<bool>(map,"_hasRecipeLayout")&&navigation.SequenceEqual([3])&&selection.Count==0);
        observations.Add(new{kind="overview-pointer-policy",navigation="release-only, includes measurement and filtered slots",selection="rectangle drag only; Ctrl additive",thresholdDip=4,deviceRequests=work});
        window.Close();
    }
    private static void RunEmptyReviewMenuChecks()
    {
        var seed=Snapshot(1).Points[0] with{IsMasked=false,IsProxy=false,ReferenceKey="",WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,
            OriginalJudge="UNMEASURED",OriginalXUm=null,OriginalYUm=null,Source="KEPT",HasAppliedOffset=false,IsMeasurementOffsetInitialized=true};
        var map=new CManualInspectionMapControl();var work=0;map.OperationRequested+=(_,_)=>work++;
        var window=new Window{Width=700,Height=500,Content=map,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
        foreach(var entry in new[]{(Name:"fresh_empty",Applied:false,Pending:false),(Name:"saved_init",Applied:true,Pending:false),(Name:"unsaved_init_draft",Applied:true,Pending:true)})
        {
            var point=seed with{HasAppliedOffset=entry.Applied};var state=new CManualInspectionUiSnapshot{Version=701,RecipeId="EMPTY-STATE",RunId="NO-DEVICE",Points=[point],IsOffsetEditPending=entry.Pending,OffsetInitPending=entry.Pending};
            map.Configure(state,state.Points,state.Points,[point.Key],point.Key,null,"level","empty-menu");Pump();map.ShowOperationMenu();Pump();
            var menu=map.ContextMenu!;var action=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="이동 후 측정");
            Test("move_measure_menu_"+entry.Name+"_depends_on_pending_edit_not_empty_value",action.IsEnabled==!entry.Pending&&work==0);
            menu.IsOpen=false;
        }
        CManualInspectionSelectionEventArgs? maskedFocus=null;map.SelectionRequested+=(_,args)=>maskedFocus=args;
        var maskedPoint=seed with{IsMasked=true};var maskedState=new CManualInspectionUiSnapshot{Version=708,RecipeId="MASKED-REFERENCE",RunId="NO-DEVICE",Points=[maskedPoint]};
        map.Configure(maskedState,maskedState.Points,maskedState.Points,[],null,null,"level","masked-reference");Pump();
        var maskedRect=Slots(map)[maskedPoint.Key];var maskedAt=new Point(maskedRect.Left+maskedRect.Width/2,maskedRect.Top+maskedRect.Height/2);
        Invoke(map,"BeginPointerGesture",maskedAt,ModifierKeys.None,1);Invoke(map,"CompletePointerGesture",maskedAt);Pump();
        Test("masked_point_click_can_focus_reference_without_selecting_measurement_target",maskedFocus is{Additive:true,FocusKey:var maskedKey}&&maskedKey==maskedPoint.Key&&maskedFocus.Keys.Count==0&&work==0&&map.ContextMenu?.IsOpen!=true);
        window.Close();
        foreach(var entry in new[]{(Name:"initial_empty_model",Applied:false),(Name:"loaded_saved_init_model",Applied:true)})
        {
            var point=seed with{HasAppliedOffset=entry.Applied};var state=new CManualInspectionUiSnapshot{Version=711,RecipeId="INITIAL-MODEL",RunId="NO-DEVICE",Points=[point]};
            var fake=new FakeController(state);var confirms=0;
            var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>{confirms++;return false;}};
            var host=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();Pump();
            var initialMap=(CManualInspectionMapControl)view.FindName("PositionMap");
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([point.Key],point.Key));Pump();
            initialMap.ShowOperationMenu();Pump();var menu=initialMap.ContextMenu!;
            var moveMeasure=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="이동 후 측정");
            Test("view_"+entry.Name+"_offers_move_measure_before_any_edit",moveMeasure.IsEnabled&&!fake.Snapshot.IsOffsetEditPending&&fake.Calls.Count==0);
            moveMeasure.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
            Test("view_"+entry.Name+"_reaches_movement_confirmation_instead_of_empty_value_gate",confirms==1&&fake.Calls.Count==0);
            menu.IsOpen=false;host.Close();
        }
    }

    private static void RunMapKeyboardChecks()
    {
        var initial=OverviewSnapshot();var state=initial;var visible=state.Points;
        var selected=new HashSet<string>();string? focus=null;var selections=0;var navigation=0;var work=0;
        var map=new CManualInspectionMapControl();
        var window=new Window{Width=900,Height=700,Content=map,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        window.Show();Pump();
        void Publish()=>map.Configure(state,state.Points,visible,selected,focus,null,"level","keyboard-regression",true);
        map.SelectionRequested+=(_,args)=>
        {
            selections++;if(!args.Additive)selected.Clear();foreach(var key in args.Keys)selected.Add(key);
            if(args.FocusKey is not null)focus=args.FocusKey;Publish();
        };
        map.CellRequested+=(_,_)=>navigation++;map.OperationRequested+=(_,_)=>work++;
        void Configure(CManualInspectionUiSnapshot? next=null,IReadOnlyList<CManualInspectionUiPoint>? show=null,string? at=null)
        {state=next??initial;visible=show??state.Points;selected.Clear();focus=at;if(at is not null)selected.Add(at);map.ResetView();Publish();Pump();selections=0;navigation=0;}
        bool KeyPress(Key key,ModifierKeys mods=ModifierKeys.None,bool repeat=false)
        {var handled=(bool)Invoke(map,"HandleMapKey",key,mods,repeat)!;Pump();return handled;}
        string Point(int cell,int row,int col)=>state.Points.Single(p=>p.CellNo==cell&&p.Row==row&&p.Column==col).Key;
        Configure();
        Test("map_keyboard_without_focus_selects_first_visible_point_without_cell_navigation",KeyPress(Key.Right)&&focus==Point(3,0,0)&&selected.SetEquals([focus!])&&selections==1&&navigation==0&&work==0);
        KeyPress(Key.Right);
        Test("map_keyboard_right_follows_displayed_row_across_head_numbers",focus==Point(3,0,1)&&selected.Count==1&&navigation==0&&work==0);
        KeyPress(Key.Down,ModifierKeys.Control);
        Test("map_keyboard_ctrl_arrow_selects_column_end_as_one_point",focus==Point(8,3,1)&&selected.SetEquals([Point(8,3,1)]));
        Configure(at:Point(3,1,1));
        KeyPress(Key.Left);KeyPress(Key.Up);
        Test("map_keyboard_left_and_up_return_to_visual_neighbours",focus==Point(3,0,0)&&selected.Count==1);
        Configure(at:Point(3,1,1));KeyPress(Key.Right,ModifierKeys.Shift);
        Test("map_keyboard_shift_arrow_extends_from_fixed_anchor",focus==Point(3,1,2)&&selected.SetEquals([Point(3,1,1),Point(3,1,2)]));
        KeyPress(Key.Down,ModifierKeys.Shift);
        Test("map_keyboard_shift_two_axes_forms_rectangular_range",selected.SetEquals([Point(3,1,1),Point(3,1,2),Point(3,2,1),Point(3,2,2)]));
        KeyPress(Key.Left,ModifierKeys.Shift);KeyPress(Key.Up,ModifierKeys.Shift);
        Test("map_keyboard_reverse_shift_shrinks_back_to_anchor",focus==Point(3,1,1)&&selected.SetEquals([Point(3,1,1)]));
        KeyPress(Key.Left,ModifierKeys.Shift);KeyPress(Key.Right);KeyPress(Key.Down,ModifierKeys.Shift);
        Test("map_keyboard_plain_arrow_resets_range_anchor",focus==Point(3,2,1)&&selected.SetEquals([Point(3,1,1),Point(3,2,1)]));
        Configure(at:Point(3,1,1));KeyPress(Key.Right,ModifierKeys.Control|ModifierKeys.Shift);
        Test("map_keyboard_ctrl_shift_selects_rectangular_range_to_visual_row_end",focus==Point(10,1,3)&&selected.Count==7&&selected.All(key=>state.Points.Single(p=>p.Key==key).Row==1));
        KeyPress(Key.Left,ModifierKeys.Control|ModifierKeys.Shift);
        Test("map_keyboard_reverse_ctrl_shift_contracts_range_instead_of_union",focus==Point(3,1,0)&&selected.SetEquals([Point(3,1,0),Point(3,1,1)]));
        KeyPress(Key.Right,ModifierKeys.Control);
        Test("map_keyboard_ctrl_without_shift_collapses_previous_range",focus==Point(10,1,3)&&selected.SetEquals([Point(10,1,3)]));
        Configure(at:Point(3,1,1));KeyPress(Key.Right,ModifierKeys.Shift);
        selected.Clear();focus=Point(10,2,1);selected.Add(focus);Publish();Pump();KeyPress(Key.Down,ModifierKeys.Shift);
        Test("map_keyboard_external_selection_resets_old_anchor",focus==Point(10,3,1)&&selected.SetEquals([Point(10,2,1),Point(10,3,1)]));
        Configure(at:Point(3,1,1));KeyPress(Key.Right,ModifierKeys.Shift);
        state=state with{Version=state.Version+1,Points=state.Points.Select(p=>p with{WorkingJudge="OK"}).ToArray()};visible=state.Points;Publish();Pump();KeyPress(Key.Down,ModifierKeys.Shift);
        Test("map_keyboard_measurement_status_refresh_keeps_same_geometry_anchor",selected.SetEquals([Point(3,1,1),Point(3,1,2),Point(3,2,1),Point(3,2,2)]));
        var mask=initial.Points.Select(p=>p.CellNo==3&&p.Row==0&&p.Column==1?p with{IsMasked=true}:p).ToArray();
        var filtered=mask.Where(p=>!(p.CellNo==3&&p.Row==0&&p.Column==2)).ToArray();
        Configure(initial with{Points=mask},filtered,mask.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);KeyPress(Key.Right);
        Test("map_keyboard_skips_masked_and_filtered_positions",focus==Point(3,0,3)&&selected.Count==1,"Actual focus: "+focus);
        KeyPress(Key.Right);
        Test("map_keyboard_right_crosses_actual_cell_boundary_without_entering_single_cell",focus==Point(10,0,0)&&navigation==0&&work==0,"Actual focus: "+focus);
        Configure(initial with{Points=mask},filtered,mask.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);KeyPress(Key.Right,ModifierKeys.Shift);
        Test("map_keyboard_shift_range_excludes_masked_and_hidden_interior_points",focus==Point(3,0,3)&&selected.SetEquals([Point(3,0,0),Point(3,0,3)]));
        var rowOnly=initial.Points.Where(p=>p.CellNo==3&&!(p.Row==0&&p.Column>=2)).ToArray();
        Configure(show:rowOnly,at:initial.Points.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);KeyPress(Key.Right,ModifierKeys.Control);
        Test("map_keyboard_ctrl_end_stays_on_filtered_logical_row_without_diagonal_wrap",focus==Point(3,0,1)&&selected.Count==1);
        Configure(at:initial.Points.First(p=>p.CellNo==10&&p.Row==3&&p.Column==3).Key);KeyPress(Key.Right,ModifierKeys.Control);
        Test("map_keyboard_ctrl_at_line_edge_never_wraps_into_another_row",focus==Point(10,3,3)&&selected.Count==1);
        Configure(at:initial.Points.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);KeyPress(Key.Right,ModifierKeys.Shift);
        visible=state.Points.Where(p=>!(p.CellNo==3&&p.Column==0)).ToArray();Publish();Pump();KeyPress(Key.Down,ModifierKeys.Shift);
        Test("map_keyboard_filter_change_resets_old_anchor_to_current_visible_focus",selected.SetEquals([Point(3,0,1),Point(3,1,1)]));
        Configure(at:initial.Points.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);KeyPress(Key.Right,ModifierKeys.Shift);
        state=state with{RunId="NEW-COORDINATE-RUN"};Publish();Pump();KeyPress(Key.Down,ModifierKeys.Shift);
        Test("map_keyboard_source_frame_change_cannot_reuse_previous_anchor",selected.SetEquals([Point(3,0,1),Point(3,1,1)]));
        Configure(at:Point(3,3,0));KeyPress(Key.Down);
        Test("map_keyboard_down_crosses_to_cell_below_using_recipe_placement",focus==Point(8,0,0)&&navigation==0&&work==0);
        Configure(at:Point(8,0,3));KeyPress(Key.Right);
        Test("map_keyboard_never_selects_disabled_cell_positions",state.Points.Single(p=>p.Key==focus).CellNo!=22&&navigation==0&&work==0);
        Configure(show:initial.Points.Where(p=>p.CellNo==10).ToArray(),at:initial.Points.First(p=>p.CellNo==3).Key);KeyPress(Key.Left);
        Test("map_keyboard_hidden_focus_restarts_at_visible_point",focus==Point(10,0,0)&&selected.Count==1);
        Configure(show:[]);KeyPress(Key.Right);
        Test("map_keyboard_empty_filtered_map_does_not_select_or_dispatch",selections==0&&work==0&&navigation==0);
        Configure(at:initial.Points.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);
        KeyPress(Key.Left);
        Test("map_keyboard_left_edge_does_not_wrap_or_replace_selection",focus==Point(3,0,0)&&selections==0);
        map.RestoreView(3,new Vector(-500,-500));Pump();KeyPress(Key.Right);
        var rect=Slots(map)[focus!];var screen=new Rect(new Point(rect.Left*map.Zoom+map.Pan.X,rect.Top*map.Zoom+map.Pan.Y),new Size(rect.Width*map.Zoom,rect.Height*map.Zoom));
        Test("map_keyboard_selected_point_is_revealed_after_zoom_and_pan",screen.Left>=-.01&&screen.Top>=-.01&&screen.Right<=map.ActualWidth+.01&&screen.Bottom<=map.ActualHeight+.01);
        var beforeSelection=selected.ToHashSet();var beforeFocus=focus;var pan=map.Pan;
        KeyPress(Key.Down,ModifierKeys.Alt|ModifierKeys.Shift);
        Test("map_keyboard_alt_shift_arrow_still_pans_without_selection",map.Pan==pan+new Vector(0,100)&&selected.SetEquals(beforeSelection)&&focus==beforeFocus);
        KeyPress(Key.Space);var menu=map.ContextMenu!;
        Test("map_keyboard_space_opens_existing_action_context_menu_without_executing",menu.IsOpen&&work==0&&menu.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()=="이동 후 측정"));
        var handledArrow=KeyPress(Key.Down);KeyPress(Key.Space);
        Test("map_keyboard_open_menu_keeps_native_arrow_navigation_and_is_not_replaced",!handledArrow&&ReferenceEquals(menu,map.ContextMenu)&&menu.IsOpen&&selected.SetEquals(beforeSelection)&&focus==beforeFocus&&work==0);
        Test("map_keyboard_escape_closes_menu_and_preserves_selection",KeyPress(Key.Escape)&&!menu.IsOpen&&selected.SetEquals(beforeSelection)&&focus==beforeFocus&&work==0);
        KeyPress(Key.Space,repeat:true);
        Test("map_keyboard_held_space_does_not_reopen_context_menu",!menu.IsOpen&&work==0);
        KeyPress(Key.Space);var kept=selected.ToHashSet();
        Test("map_keyboard_parent_escape_cancel_closes_menu_before_other_actions",map.TryCancelGesture()&&!map.ContextMenu!.IsOpen&&selected.SetEquals(kept)&&work==0);
        Configure(at:initial.Points.First(p=>p.CellNo==3&&p.Row==0&&p.Column==0).Key);
        var r=Slots(map)[focus!];var start=new Point(r.Left+r.Width/2,r.Top+r.Height/2);
        Invoke(map,"BeginPointerGesture",start,ModifierKeys.None,1);KeyPress(Key.Right);Invoke(map,"CompletePointerGesture",start);Pump();
        Test("map_keyboard_selection_cancels_pending_cell_click_instead_of_navigating",focus==Point(3,0,1)&&navigation==0&&work==0);
        Configure(initial with{CellLayoutYDown=false});KeyPress(Key.Down);
        var topPoint=state.Points.Single(p=>p.Key==focus);var topY=Slots(map)[focus!].Top;
        Test("map_keyboard_first_point_follows_flipped_visual_y_direction",topPoint.CellNo==8&&topPoint.Row==3&&Slots(map).Where(p=>!state.Points.Single(s=>s.Key==p.Key).IsMasked).All(p=>p.Value.Top>=topY-.001));
        KeyPress(Key.Down);Test("map_keyboard_down_uses_visual_direction_after_y_flip",state.Points.Single(p=>p.Key==focus).Row==2&&navigation==0&&work==0);
        window.Close();
        // Route the native event through the real View as well; its shortcut handler must not
        // turn point navigation into a Controller operation or switch the Cell overview scope.
        var fake=new FakeController(initial);var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>false};
        var host=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();Pump();
        Invoke(view,"SetMapCellScope",0);Pump();var native=(CManualInspectionMapControl)view.FindName("PositionMap");
        var keyboardArgs=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(native)!,1,Key.Right){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        native.RaiseEvent(keyboardArgs);Pump();
        Test("map_keyboard_native_preview_event_selects_in_real_view_without_equipment_request",keyboardArgs.Handled&&Field<HashSet<string>>(view,"_selected").Count==1&&Field<int>(view,"_cell")==0&&fake.Calls.Count==0);
        host.Close();
    }

    private static void RunPersistentCellLabelChecks(List<object> observations)
    {
        var seed=OverviewSnapshot().Points[0];
        var cells=Enumerable.Range(1,48).Select(number=>
        {
            var x=10+(number-1)%8*28d;var y=10+(number-1)/8*24d;
            var dx=number%7==0?new Vector(10,3):new Vector(12,0);var dy=number%7==0?new Vector(-2,8):new Vector(0,10);
            var a=new Point(x,y);var b=a+dx;var d=a+dy;var c=a+dx+dy;
            return new CManualInspectionUiCellLayout{CellNo=number,Enabled=number!=7,BoundaryMm=[new(a.X,a.Y),new(b.X,b.Y),new(c.X,c.Y),new(d.X,d.Y)]};
        }).ToArray();
        var points=cells.Select(cell=>seed with{Key="LABEL-C"+cell.CellNo,CellNo=cell.CellNo,HeadNo=1,Row=0,Column=0,IsMasked=!cell.Enabled}).ToArray();
        var snapshot=new CManualInspectionUiSnapshot{Version=3001,RecipeId="SYNTHETIC-LABELS",RunId="NO-DEVICE",GlassWidthMm=240,GlassHeightMm=160,CellLayoutYDown=true,CellLayouts=cells,Points=points};
        var map=new CManualInspectionMapControl();var navigation=new List<int>();var selection=0;var work=0;
        map.CellRequested+=(_,cell)=>navigation.Add(cell);map.SelectionRequested+=(_,_)=>selection++;map.OperationRequested+=(_,_)=>work++;
        var host=new Window{Width=1000,Height=700,Content=map,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();
        map.Configure(snapshot,points,points,[],null,null,"level","cell-labels",true);Pump();
        (int Cell,Rect Rect,string Text,bool Enabled,double Font)[] Labels()=>Field<IList>(map,"_cellLabels").Cast<object>().Select(label=>
            ((int)label.GetType().GetProperty("CellNo")!.GetValue(label)!,
                (Rect)label.GetType().GetProperty("Bounds")!.GetValue(label)!,
                (string)label.GetType().GetProperty("Text")!.GetValue(label)!,
                (bool)label.GetType().GetProperty("Enabled")!.GetValue(label)!,
                (double)label.GetType().GetProperty("FontSize")!.GetValue(label)!)).ToArray();
        string GeometryJson()=>JsonSerializer.Serialize(new{snapshot.Points,snapshot.CellLayouts,snapshot.GlassWidthMm,snapshot.GlassHeightMm,snapshot.CellLayoutYDown});
        var fitted=Labels();var geometry=Slots(map);var original=GeometryJson();
        Test("overview_every_cell_has_persistent_number_without_hover",fitted.Length==48&&fitted.All(label=>label.Text.Contains("Cell "+label.Cell))&&Field<string?>(map,"_hoverKey") is null);
        Test("overview_cell_labels_distinguish_disabled_cells",fitted.Single(label=>label.Cell==7) is{Enabled:false,Text:var text}&&text.Contains("미사용"));
        map.RestoreView(.4,default);Pump();var small=Labels();
        Test("overview_small_cells_scale_label_dimensions_and_font_with_zoom",small.Length==48&&small.All(label=>Math.Abs(label.Rect.Width/fitted.Single(old=>old.Cell==label.Cell).Rect.Width-.4)<1e-8
            &&Math.Abs(label.Rect.Height/fitted.Single(old=>old.Cell==label.Cell).Rect.Height-.4)<1e-8&&Math.Abs(label.Font/fitted.Single(old=>old.Cell==label.Cell).Font-.4)<1e-8));
        Test("overview_tiny_cell_labels_have_no_fixed_badge_minimum",small.All(label=>label.Rect.Width<65&&label.Rect.Height<21&&label.Font>0));
        Test("overview_dense_cell_labels_do_not_overlap",small.SelectMany((label,i)=>small.Skip(i+1).Select(other=>!label.Rect.IntersectsWith(other.Rect))).All(value=>value));
        Test("overview_dense_cell_labels_stay_in_their_own_cell_bounds",small.All(label=>Slots(map)["LABEL-C"+label.Cell].Contains(new Rect(new Point(label.Rect.Left/.4,label.Rect.Top/.4),new Size(label.Rect.Width/.4,label.Rect.Height/.4)))));
        Test("overview_label_collision_avoidance_never_moves_real_cell_or_point_geometry",geometry.All(pair=>Slots(map)[pair.Key]==pair.Value)&&GeometryJson()==original);
        var clickedLabel=small.Single(item=>item.Cell==37);var at=new Point(clickedLabel.Rect.Left+clickedLabel.Rect.Width/2,clickedLabel.Rect.Top+clickedLabel.Rect.Height/2);
        Invoke(map,"BeginPointerGesture",at,ModifierKeys.None,1);Invoke(map,"CompletePointerGesture",at);Pump();
        Test("overview_scaled_label_still_enters_its_named_cell",navigation.SequenceEqual([37])&&selection==0&&work==0);
        foreach(var theme in new[]{"Dark","Light"}){LoadResources(theme);Pump();Render(map,"cell-labels-48-small-"+theme.ToLowerInvariant()+".png");}
        map.RestoreView(3,new Vector(-450,-400));Pump();var zoomed=Labels();
        var onscreen=zoomed.Where(item=>new Rect(0,0,map.ActualWidth,map.ActualHeight).Contains(new Point(item.Rect.Left+item.Rect.Width/2,item.Rect.Top+item.Rect.Height/2))).ToArray();
        var fullyShown=onscreen.Where(item=>{var r=geometry["LABEL-C"+item.Cell];return new Rect(0,0,map.ActualWidth,map.ActualHeight).Contains(new Rect(new Point(r.Left*3-450,r.Top*3-400),new Size(r.Width*3,r.Height*3)));}).ToArray();
        Test("overview_zoomed_cell_labels_expand_and_keep_click_identity",fullyShown.Length>0&&fullyShown.All(item=>item.Font>fitted.Single(old=>old.Cell==item.Cell).Font)
            &&onscreen.All(item=>item.Font>0&&item.Font<=22)
            &&onscreen.All(item=>(int?)Invoke(map,"HitOverviewCell",new Point(item.Rect.Left+item.Rect.Width/2,item.Rect.Top+item.Rect.Height/2))==item.Cell));
        Test("overview_partially_visible_cell_labels_fit_inside_viewport_without_floating_outside_cell",onscreen.Length>fullyShown.Length&&onscreen.All(item=>new Rect(0,0,map.ActualWidth,map.ActualHeight).Contains(item.Rect)));
        var rotatedLabel=onscreen.FirstOrDefault(item=>item.Cell%7==0);
        Test("overview_rotated_cell_label_hit_stays_in_its_actual_polygon",rotatedLabel.Cell>0&&(int?)Invoke(map,"HitOverviewCell",new Point(rotatedLabel.Rect.Left+rotatedLabel.Rect.Width/2,rotatedLabel.Rect.Top+rotatedLabel.Rect.Height/2))==rotatedLabel.Cell);
        map.Configure(snapshot,points,points,[],null,null,"level","cell-labels-fallback",true);Pump();
        map.Configure(snapshot with{CellLayouts=[]},points,points,[],null,null,"level","cell-labels-fallback",true);Pump();
        Test("overview_logical_fallback_also_keeps_all_cell_numbers",Labels().Length==48);
        observations.Add(new{kind="cell-relative-labels",cellCount=48,zoom=.4,fontRangeDip=new{Minimum=small.Min(label=>label.Font),Maximum=small.Max(label=>label.Font)},fixedBadgeMinimum=false,physicalGeometryUnchanged=true});
        host.Close();
    }
    private static void RunMeasurementBlinkChecks(List<object> observations)
    {
        LoadResources("Dark");Pump();
        var originalState=OverviewSnapshot();var points=originalState.Points.Where(p=>p.CellNo==3).Take(4).ToArray();
        var original=JsonSerializer.Serialize(points);var state=originalState with{Points=points,CellLayouts=originalState.CellLayouts.Where(cell=>cell.CellNo==3).ToArray()};
        var map=new CManualInspectionMapControl();var selections=0;var work=0;var navigation=0;
        map.SelectionRequested+=(_,_)=>selections++;map.OperationRequested+=(_,_)=>work++;map.CellRequested+=(_,_)=>navigation++;
        var host=new Window{Width=800,Height=550,Content=map,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();
        void Configure(CManualInspectionUiSnapshot value,IReadOnlyList<CManualInspectionUiPoint>? visible=null)
        {state=value;map.Configure(value,value.Points,visible??value.Points,[points[2].Key],points[2].Key,null,"level","blink",true);Pump();}
        var timer=Field<DispatcherTimer>(map,"_measurementBlinkTimer");
        bool Phase()=>Field<bool>(map,"_measurementBlinkVisible");
        HashSet<string> Keys()=>Field<HashSet<string>>(map,"_measurementBlinkKeys");
        Configure(state);
        Test("measurement_blink_idle_last_measured_point_is_not_an_active_target",!timer.IsEnabled&&!Phase()&&Keys().Count==0);
        var active=state with{IsBusy=true,IsMeasurementInProgress=true,ActiveMeasurementPointKey=points[0].Key};Configure(active);
        Test("measurement_blink_inflight_point_starts_immediately_on_half_second_timer",timer.IsEnabled&&Phase()&&timer.Interval==TimeSpan.FromMilliseconds(500)&&Keys().SetEquals([points[0].Key]));
        var ticks=0;timer.Tick+=(_,_)=>ticks++;var watch=System.Diagnostics.Stopwatch.StartNew();
        while(ticks==0&&watch.Elapsed<TimeSpan.FromSeconds(2)){Thread.Sleep(10);Pump();}
        Test("measurement_blink_actual_dispatcher_timer_advances_without_equipment_poll",ticks>0);
        Configure(active with{ActiveMeasurementPointKey=points[1].Key});
        Test("measurement_blink_new_target_resets_phase_to_on",Phase()&&Keys().SetEquals([points[1].Key]));
        Invoke(map,"AdvanceMeasurementBlink");Pump();
        Test("measurement_blink_tick_changes_only_overlay_phase",!Phase()&&timer.IsEnabled&&JsonSerializer.Serialize(points)==original&&work==0&&selections==0&&navigation==0);
        Configure(state with{Version=state.Version+1});
        Test("measurement_blink_same_target_progress_refresh_does_not_restart_phase",!Phase());
        Invoke(map,"AdvanceMeasurementBlink");Pump();Render(map,"measurement-blink-on-dark.png");
        Invoke(map,"AdvanceMeasurementBlink");Pump();Render(map,"measurement-blink-off-dark.png");
        Test("measurement_blink_on_off_render_differs_while_ng_and_raw_values_remain",!File.ReadAllBytes(Path.Combine(_output,"measurement-blink-on-dark.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(_output,"measurement-blink-off-dark.png")))
            &&points[0].WorkingJudge=="NG"&&JsonSerializer.Serialize(points)==original);
        Configure(state with{ActiveMeasurementPointKey=points[3].Key,ActiveMeasurementPointKeys=[points[0].Key,points[1].Key]});
        Test("measurement_blink_confirmed_batch_keys_take_precedence_over_single_key",Phase()&&Keys().SetEquals([points[0].Key,points[1].Key]));
        Invoke(map,"AdvanceMeasurementBlink");Configure(state with{ActiveMeasurementPointKeys=[points[1].Key,points[0].Key],Version=state.Version+1});
        Test("measurement_blink_batch_order_changes_preserve_phase",!Phase()&&Keys().SetEquals([points[0].Key,points[1].Key]));
        Configure(state with{IsMeasurementInProgress=false});
        Test("measurement_blink_move_or_save_busy_without_measurement_stops_timer",!timer.IsEnabled&&!Phase()&&Keys().Count==0);
        Configure(active with{IsBusy=false});
        Test("measurement_blink_finished_operation_never_keeps_old_target_lit",!timer.IsEnabled&&!Phase());
        Configure(active with{ActiveMeasurementPointKey="DOES-NOT-EXIST"});
        Test("measurement_blink_unknown_key_does_not_start_timer",!timer.IsEnabled&&Keys().Count==0);
        Configure(active,points.Where(p=>p.Key!=points[0].Key).ToArray());
        Test("measurement_blink_filtered_out_target_does_not_mark_another_point",!timer.IsEnabled&&Keys().Count==0);
        var masked=points.Select(p=>p.Key==points[0].Key?p with{IsMasked=true}:p).ToArray();Configure(active with{Points=masked});
        Test("measurement_blink_latest_masking_status_blocks_cached_slot_target",!timer.IsEnabled&&Keys().Count==0);
        Configure(active with{CellLayouts=active.CellLayouts.Select(cell=>cell with{Enabled=false}).ToArray()});
        Test("measurement_blink_disabled_cell_does_not_get_active_target_overlay",!timer.IsEnabled&&Keys().Count==0);
        Configure(active);map.Visibility=Visibility.Hidden;Pump();
        Test("measurement_blink_hidden_surface_stops_timer",!timer.IsEnabled&&!Phase());
        map.Visibility=Visibility.Visible;Pump();Test("measurement_blink_visible_surface_restarts_only_current_confirmed_target",timer.IsEnabled&&Phase());
        Invoke(map,"AdvanceMeasurementBlink");Configure(active with{RunId="OTHER-RUN"});
        Test("measurement_blink_new_frame_does_not_reuse_previous_phase",Phase());
        host.Content=null;Pump();Test("measurement_blink_unload_stops_dispatcher_timer",!timer.IsEnabled&&!Phase());
        host.Content=map;Pump();Test("measurement_blink_reload_restores_live_target_without_changing_selection",timer.IsEnabled&&Phase()&&work==0&&selections==0&&navigation==0);
        Configure(originalState with{Points=points,CellLayouts=state.CellLayouts,IsBusy=false,IsMeasurementInProgress=false});
        Test("measurement_blink_stop_or_completion_clears_active_keys_and_preserves_data",!timer.IsEnabled&&!Phase()&&Keys().Count==0&&JsonSerializer.Serialize(points)==original);
        observations.Add(new{kind="measurement-blink",timerMilliseconds=500,actualTickObserved=ticks>0,confirmedTargetsOnly=true,selectionOrEquipmentRequests=work+selections+navigation,rawUnchanged=true});
        host.Close();
    }

    private static void RunExpandedScatterMapChecks(List<object> observations)
    {
        var state=OverviewSnapshot();var fake=new FakeController(state);
        var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>false};
        Set(view, "_scatterOffsets", false); Set(view, "_scatterScope", "CELL"); // Explicit legacy working-data scope.
        var window=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        window.Show();Pump();Invoke(view,"SetMapCellScope",0);Pump();
        var map=(CManualInspectionMapControl)view.FindName("PositionMap");var scatter=(CManualInspectionScatterControl)view.FindName("ScatterPlot");
        var section=(Expander)view.FindName("ScatterSection");var scroll=(ScrollViewer)view.FindName("WorkspaceScroll");
        var collapsedRestored=true;
        foreach(var size in new[]{(Width:1120d,Height:560d,Name:"compact"),(Width:1480d,Height:900d,Name:"wide")})
        {
            window.Width=size.Width;window.Height=size.Height;Pump();
            section.IsExpanded=true;Pump();scroll.ScrollToTop();Pump();map.ResetView();Pump();
            var minimum=Math.Max(240,scroll.ViewportHeight*.45);
            Test("expanded_scatter_preserves_usable_map_height_"+size.Name,map.ActualHeight+.5>=minimum&&Slots(map).Count==state.Points.Count
                &&Slots(map).Values.All(r=>r.Left>=-.1&&r.Top>=-.1&&r.Right<=map.ActualWidth+.1&&r.Bottom<=map.ActualHeight+.1),
                new{map.ActualHeight,minimum,scroll.ViewportHeight});
            foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"expanded-map-"+theme.ToLowerInvariant()+"-"+size.Name+".png");}
            scatter.BringIntoView();Pump();var bounds=scatter.TransformToAncestor(scroll).TransformBounds(new Rect(scatter.RenderSize));
            Test("expanded_scatter_plot_remains_reachable_after_map_minimum_"+size.Name,scatter.IsVisible&&bounds.Top>=-1&&bounds.Bottom<=scroll.ViewportHeight+1
                &&scroll.VerticalOffset>=0&&scatter.Metrics.Count>0&&fake.Calls.Count==0,new{bounds.Top,bounds.Bottom,scroll.ViewportHeight,scroll.VerticalOffset});
            foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"expanded-scatter-plot-"+theme.ToLowerInvariant()+"-"+size.Name+".png");}
            section.IsExpanded=false;Pump();scroll.ScrollToTop();Pump();
            collapsedRestored&=scroll.ScrollableHeight<=1&&scroll.ScrollableWidth<=1&&map.ActualHeight>=view.ActualHeight*.64&&map.MinHeight==80;
            observations.Add(new{kind="expanded-map-access",size.Name,mapMinExpanded=minimum,collapsedMapHeight=map.ActualHeight,scroll.ScrollableHeight});
        }
        Test("closing_scatter_restores_map_first_no_scroll_at_both_sizes",collapsedRestored&&fake.Calls.Count==0);
        window.Close();
    }
}
