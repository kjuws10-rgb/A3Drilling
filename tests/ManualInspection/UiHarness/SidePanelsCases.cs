using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int SidePanels()
    {
        var observations=new List<object>();
        try
        {
            RunSidePanelLayoutChecks(observations);
            RunSidePanelFirstLoadChecks();
            RunSidePanelSourceChecks();
        }
        catch(Exception error){Test("side_panels_unexpected_exception",false,error.ToString());}
        var files=new[]{"Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs","Views/CManualInspectionUiContract.cs"};
        var hashes=files.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output,"ui-side-panels-results.json"),JsonSerializer.Serialize(new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,observations,sourceHashes=hashes,
            scope="Real source-linked WPF View and actual theme resources with synthetic immutable points/fake Controller. No equipment App/Root/vendor object, OS input injection, motion, Vision, script or real offset file writes."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Side panels UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }

    private static void RunSidePanelLayoutChecks(List<object> observations)
    {
        LoadResources("Dark");
        var state=Snapshot(160) with{Revisions=Enumerable.Range(1,8).Select(i=>new CManualInspectionUiRevision{Id="R"+i,CreatedAt="11-24 12:00",Reason="합성 검증 적용 이력"}).ToArray(),
            CandidateRows=[new(){Key="C1:H2:S2:B11",Source="MEASURED",BaseXUm=1,BaseYUm=2,DeltaXUm=.2,DeltaYUm=-.3,FinalXUm=1.2,FinalYUm=1.7}],ValidationPassed=true};
        var original=JsonSerializer.Serialize(state.Points);var fake=new FakeController(state);
        var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>false};
        var host=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        host.Show();Pump();
        T Named<T>(string name)=>(T)view.FindName(name);
        void Choose(string name,int index){Named<TabControl>(name).SelectedIndex=index;Pump();}
        void OpenBoth(){Named<Expander>("LeftInspectorSection").IsExpanded=true;Named<Expander>("InspectorSection").IsExpanded=true;Pump();}
        bool Fits(string name,string scrollName)
        {
            var element=Named<FrameworkElement>(name);var parent=Named<FrameworkElement>(scrollName);
            if(!element.IsVisible||element.ActualHeight<=0||element.ActualWidth<=0)return false;
            var rect=element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
            return rect.Left>=-1&&rect.Top>=-1&&rect.Right<=parent.ActualWidth+1&&rect.Bottom<=parent.ActualHeight+1;
        }
        bool NoOuterScroll()=>new[]{"WorkspaceScroll","LeftInspectorScroll","InspectorScroll"}.All(name=>Named<ScrollViewer>(name).ScrollableHeight<=1&&Named<ScrollViewer>(name).ScrollableWidth<=1);
        Test("side_panels_default_folded_handles_keep_bottom_table_folded",!Named<Expander>("LeftInspectorSection").IsExpanded&&!Named<Expander>("InspectorSection").IsExpanded&&!Named<Expander>("ResultsSection").IsExpanded);
        foreach(var size in new[]{(Name:"notebook",W:1120d,H:560d,Scale:1d),(Name:"wide",W:1490d,H:900d,Scale:1d),(Name:"dpi125",W:1400/1.25,H:900/1.25,Scale:1.25),(Name:"dpi150",W:1490/1.5,H:900/1.5,Scale:1.5)})
        {
            host.Width=size.W;host.Height=size.H;OpenBoth();
            Choose("LeftPaneTabs",0);Choose("RightPaneTabs",0);
            var map=Named<CManualInspectionMapControl>("PositionMap");
            Test("side_panels_both_open_keep_central_map_readable_"+size.Name,map.ActualWidth>=350&&map.ActualHeight>=250&&NoOuterScroll());
            Test("side_panels_common_point_summary_stays_visible_"+size.Name,Fits("PointSummaryPanel","InspectorScroll")&&Fits("PointTitleText","InspectorScroll")&&Fits("MeasureCurrentButton","InspectorScroll"));
            Test("side_panels_filter_controls_fit_without_outer_scroll_"+size.Name,new[]{"CellFilter","MeasurementLevelLabel","JudgeFilter","HeadFilter","SourceFilter","SearchBox","SortFilter"}.All(name=>Fits(name,"LeftInspectorScroll"))&&NoOuterScroll());
            Named<ComboBox>("JudgeFilter").SelectedIndex=3;Named<TextBox>("SearchBox").Text="C1";Pump();
            var filterReset=Descendants(Named<ScrollViewer>("LeftInspectorScroll")).OfType<Button>().Single(button=>Equals(button.Content,"필터 초기화"));
            var resetBounds=filterReset.TransformToAncestor(Named<ScrollViewer>("LeftInspectorScroll")).TransformBounds(new Rect(filterReset.RenderSize));
            Test("side_panels_active_filter_notice_keeps_reset_and_fields_reachable_"+size.Name,!string.IsNullOrWhiteSpace(Named<TextBlock>("FilterNoticeText").Text)
                &&Fits("SortFilter","LeftInspectorScroll")&&resetBounds.Bottom<=Named<ScrollViewer>("LeftInspectorScroll").ActualHeight+1&&NoOuterScroll());
            filterReset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Choose("LeftPaneTabs",1);Test("side_panels_legend_is_on_left_"+size.Name,Fits("MapLegendItems","LeftInspectorScroll"));
            Choose("LeftPaneTabs",2);Test("side_panels_work_info_is_on_left_"+size.Name,Fits("StagePositionText","LeftInspectorScroll")&&Fits("PersistenceText","LeftInspectorScroll"));
            Choose("LeftPaneTabs",3);Choose("ScatterPaneTabs",0);
            Test("side_panels_scatter_graph_is_full_left_page_without_moving_map_"+size.Name,Fits("ScatterPlot","LeftInspectorScroll")&&Named<CManualInspectionScatterControl>("ScatterPlot").ActualWidth>=180&&NoOuterScroll());
            Choose("ScatterPaneTabs",1);Test("side_panels_scatter_statistics_is_separate_page_"+size.Name,Named<ScrollViewer>("ScatterStatisticsScroll").IsVisible&&!Named<CManualInspectionScatterControl>("ScatterPlot").IsVisible);
            Choose("ScatterPaneTabs",2);Test("side_panels_scatter_axis_controls_remain_reachable_"+size.Name,new[]{"ScatterRangeInput","ScatterRangeApplyButton","ScatterZoomInButton","ScatterZoomOutButton","ScatterAxisText"}.All(name=>Fits(name,"LeftInspectorScroll")));
            Choose("ScatterPaneTabs",3);Test("side_panels_scatter_scope_is_separate_page_"+size.Name,Named<StackPanel>("ScatterScopeControls").IsVisible&&NoOuterScroll());
            Choose("LeftPaneTabs",4);Choose("SettingsPaneTabs",0);Test("side_panels_measurement_settings_fit_"+size.Name,Fits("MeasurementModeCombo","LeftInspectorScroll")&&Fits("LevelsButton","LeftInspectorScroll"));
            Choose("SettingsPaneTabs",1);Test("side_panels_all_limit_settings_fit_"+size.Name,new[]{"DeltaLimitInput","TotalLimitInput","FieldLimitInput","SaveLimitsButton"}.All(name=>Fits(name,"LeftInspectorScroll")));
            Choose("SettingsPaneTabs",2);
            var levelPanel=Named<StackPanel>("LevelSettingsPanel");var levelSave=Descendants(levelPanel).OfType<Button>().LastOrDefault();
            var levelSaveBounds=levelSave?.TransformToAncestor(Named<ScrollViewer>("LeftInspectorScroll")).TransformBounds(new Rect(levelSave.RenderSize));
            Test("side_panels_level_settings_build_compact_reachable_page_"+size.Name,levelPanel.Children.Count>0&&Fits("LevelSettingsPanel","LeftInspectorScroll")&&NoOuterScroll());
            Test("side_panels_real_level_editor_last_apply_button_fits_"+size.Name,Descendants(levelPanel).OfType<TextBox>().Count()==7&&levelSave is{IsVisible:true}
                &&levelSaveBounds is{} box&&box.Top>=-1&&box.Bottom<=Named<ScrollViewer>("LeftInspectorScroll").ActualHeight+1);
            Choose("LeftPaneTabs",5);Test("side_panels_revision_history_is_on_left_"+size.Name,Named<ItemsControl>("RevisionItems").IsVisible&&Named<TextBlock>("OffsetHistoryText").IsVisible);
            Choose("RightPaneTabs",1);Test("side_panels_reference_copy_controls_fit_and_summary_remains_"+size.Name,new[]{"SetReferenceButton","CopyReasonInput","CopyReferenceButton","ClearReferenceButton","PointSummaryPanel"}.All(name=>Fits(name,"InspectorScroll"))&&NoOuterScroll());
            Choose("RightPaneTabs",2);
            Test("side_panels_correction_actions_fit_right_page_"+size.Name,new[]{"BulkApplyButton","OffsetInitButton","RollbackButton","RedoOffsetsButton","PointSummaryPanel"}.All(name=>Fits(name,"InspectorScroll"))&&NoOuterScroll());
            Test("side_panels_offset_init_controls_fit_right_page_"+size.Name,new[]{"OffsetInitButton"}.All(name=>Fits(name,"InspectorScroll"))&&NoOuterScroll());
            Test("side_panels_candidate_page_preserves_grid_and_validation_controls_"+size.Name,view.FindName("CandidateGrid") is null&&view.FindName("ValidationText") is null&&NoOuterScroll());
            Named<Expander>("ResultsSection").IsExpanded=true;Pump();
            Test("side_panels_bottom_table_expansion_never_scrolls_entire_workspace_"+size.Name,Named<DataGrid>("ResultGrid").IsVisible&&NoOuterScroll()&&map.ActualWidth>=350&&map.ActualHeight>=150);
            Named<Expander>("ResultsSection").IsExpanded=false;Choose("LeftPaneTabs",0);Choose("RightPaneTabs",1);
            foreach(var theme in new[]{"Dark","Light"}){LoadResources(theme);Pump();RenderAtScale(view,"side-panels-"+size.Name+"-"+theme.ToLowerInvariant()+".png",size.Scale);}
            observations.Add(new{kind="side-layout",name=size.Name,widthDip=size.W,heightDip=size.H,mapWidthDip=map.ActualWidth,mapHeightDip=map.ActualHeight,outerScroll=false,dpiIsEquivalentSpaceAndRenderOnly=true});
        }
        host.Width=1120;host.Height=560;OpenBoth();
        fake.Replace(state with{Version=state.Version+1,Revisions=Enumerable.Range(1,8).Select(i=>new CManualInspectionUiRevision{Id="LONG-R"+i,CreatedAt="11-24 12:00",Reason=string.Join(" · ",Enumerable.Repeat("합성 긴 적용 이력과 보정 근거",12))}).ToArray()});
        Choose("LeftPaneTabs",5);var history=Named<ScrollViewer>("HistoryItemsScroll");history.ScrollToBottom();Pump();
        var lastRevision=Named<ItemsControl>("RevisionItems").ItemContainerGenerator.ContainerFromIndex(7) as FrameworkElement;
        var lastBounds=lastRevision?.TransformToAncestor(history).TransformBounds(new Rect(lastRevision.RenderSize));
        var lastRevisionText=lastRevision is null?null:Descendants(lastRevision).OfType<TextBlock>().FirstOrDefault();
        Test("side_panels_long_eight_revision_rows_fit_without_scroll_and_keep_full_tooltip",history.ScrollableHeight<=1&&history.ViewportHeight>0&&NoOuterScroll()
            &&lastBounds is{} last&&last.Bottom<=history.ActualHeight+1&&lastRevisionText is{TextWrapping:TextWrapping.NoWrap,TextTrimming:TextTrimming.CharacterEllipsis}
            &&lastRevisionText.Text.Length>100&&Equals(lastRevisionText.ToolTip,lastRevisionText.Text));
        fake.Replace(state);Choose("LeftPaneTabs",3);Choose("ScatterPaneTabs",0);Choose("RightPaneTabs",2);LoadResources("Dark");Pump();Render(view,"side-panels-map-scatter-correction-dark.png");
        Choose("LeftPaneTabs",4);Choose("SettingsPaneTabs",2);Pump();Render(view,"side-panels-level-settings-dark.png");
        Choose("LeftPaneTabs",3);Named<Expander>("SearchSection").IsExpanded=false;Named<Expander>("SearchSection").IsExpanded=true;Pump();
        Test("side_panels_legacy_search_expansion_opens_left_filter_page",Named<Expander>("LeftInspectorSection").IsExpanded&&Named<TabControl>("LeftPaneTabs").SelectedIndex==0);
        Named<Expander>("ScatterSettingsSection").IsExpanded=false;Named<Expander>("ScatterSettingsSection").IsExpanded=true;Pump();
        Test("side_panels_legacy_scatter_settings_expansion_opens_left_axis_page",Named<TabControl>("LeftPaneTabs").SelectedIndex==3&&Named<TabControl>("ScatterPaneTabs").SelectedIndex==2);
        Named<Expander>("ReferenceSection").IsExpanded=false;Named<Expander>("ReferenceSection").IsExpanded=true;Pump();
        Test("side_panels_legacy_reference_expansion_opens_right_reference_page",Named<TabControl>("RightPaneTabs").SelectedIndex==1);
        Named<Expander>("CorrectionSection").IsExpanded=false;Named<Expander>("CorrectionSection").IsExpanded=true;Pump();
        Test("side_panels_legacy_correction_expansion_opens_right_correction_page",Named<TabControl>("RightPaneTabs").SelectedIndex==2);
        Choose("SettingsPaneTabs",2);Choose("LeftPaneTabs",3);Choose("ScatterPaneTabs",2);Choose("RightPaneTabs",2);
        var bookmark=Invoke(view,"CaptureWorkspaceView")!;
        Choose("ScatterPaneTabs",0);Choose("SettingsPaneTabs",0);Choose("LeftPaneTabs",0);Choose("RightPaneTabs",1);
        Named<Expander>("LeftInspectorSection").IsExpanded=false;Named<Expander>("InspectorSection").IsExpanded=false;Pump();
        Invoke(view,"RestoreWorkspaceView",bookmark);Pump();
        Test("side_panels_file_return_restores_both_panes_and_active_pages",Named<Expander>("LeftInspectorSection").IsExpanded&&Named<Expander>("InspectorSection").IsExpanded
            &&Named<TabControl>("LeftPaneTabs").SelectedIndex==3&&Named<TabControl>("RightPaneTabs").SelectedIndex==2
            &&Named<TabControl>("ScatterPaneTabs").SelectedIndex==2&&Named<TabControl>("SettingsPaneTabs").SelectedIndex==2);
        Test("side_panels_navigation_and_layout_never_mutate_measurements_or_dispatch_equipment",fake.Calls.Count==0&&JsonSerializer.Serialize(state.Points)==original);
        host.Close();
    }

    private static void RunSidePanelFirstLoadChecks()
    {
        var points=Snapshot(2).Points.Select(p=>p with{WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,OriginalJudge="UNMEASURED",OriginalXUm=null,OriginalYUm=null,
            HasAppliedOffset=false,IsProxy=false,ReferenceKey="",CanUseImportedForCorrection=false,ReferenceFinalXUm=null,ReferenceFinalYUm=null,CanCopyReferenceState=true}).ToArray();
        var state=new CManualInspectionUiSnapshot{Version=9001,RecipeId="FIRST-LOAD-EMPTY",RunId="NO-DEVICE",Points=points,CanBulkApply=false};
        var fake=new FakeController(state);var confirmations=0;
        var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>{confirmations++;return false;}};
        var host=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();Pump();
        ((Expander)view.FindName("InspectorSection")).IsExpanded=true;((TabControl)view.FindName("RightPaneTabs")).SelectedIndex=1;Pump();
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([points[0].Key],points[0].Key));
        ((Button)view.FindName("SetReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([points[1].Key],points[1].Key));Pump();
        Test("side_panels_first_empty_model_can_mark_and_copy_reference_state",((Button)view.FindName("SetReferenceButton")).IsEnabled&&((Button)view.FindName("CopyReferenceButton")).IsEnabled);
        ((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("side_panels_empty_state_copy_cancel_has_no_backend_action",fake.Calls.Count==0&&confirmations==1);
        view.ConfirmationForTesting=(_,_,_)=>{confirmations++;return true;};
        ((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("side_panels_empty_state_copy_accept_requests_only_copy_not_motion_or_apply",fake.Calls.Count==1&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.CopyReference&&fake.Calls[0].Request.ReferenceKey==points[0].Key&&confirmations==2);
        ((TabControl)view.FindName("RightPaneTabs")).SelectedIndex=2;Pump();
        var bulk=(Button)view.FindName("BulkApplyButton");Test("side_panels_first_empty_model_keeps_bulk_explanation_button_enabled",bulk.IsEnabled&&!fake.Snapshot.CanBulkApply);
        var before=fake.Calls.Count;bulk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("side_panels_empty_bulk_explains_without_extra_backend_or_confirmation",fake.Calls.Count==before&&confirmations==2&&!string.IsNullOrWhiteSpace(((TextBlock)view.FindName("MessageText")).Text));
        Pump();
        Test("side_panels_first_empty_model_offset_init_is_available",((Button)view.FindName("OffsetInitButton")).IsEnabled);
        Test("side_panels_initial_empty_values_do_not_become_measured_zero",fake.Snapshot.Points.All(p=>!p.HasWorkingMeasurement&&p.MeasurementXUm is null&&p.MeasurementYUm is null));
        host.Close();
    }

    private static void RunSidePanelSourceChecks()
    {
        var root=XDocument.Load(Path.Combine(_uiSource,"Views","CManualInspectionView.xaml"));
        XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        var names=root.Descendants().Select(element=>(string?)element.Attribute(x+"Name")).Where(name=>name is not null).ToArray();
        var required=new[]{"LeftInspectorSection","LeftInspectorScroll","LeftPaneTabs","InspectorSection","InspectorScroll","RightPaneTabs","PointSummaryPanel","WorkspaceScroll","PositionMap","ResultGrid","ScatterPlot","ReferenceSection","CorrectionSection","SettingsSection"};
        Test("side_panels_named_controls_remain_unique_for_real_codebehind",required.All(name=>names.Count(value=>value==name)==1));
        Test("side_panels_all_required_selection_handlers_are_connected",root.Descendants().Count(element=>((string?)element.Attribute("SelectionChanged")) is "LeftPane_SelectionChanged" or "RightPane_SelectionChanged" or "ScatterPane_SelectionChanged" or "SettingsPane_SelectionChanged")==4);
    }
}
