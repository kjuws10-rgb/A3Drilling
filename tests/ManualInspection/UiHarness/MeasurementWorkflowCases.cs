using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int MeasurementWorkflow()
    {
        try
        {
            LoadResources("Light");var state=Snapshot(240) with{StageXmm=0,StageYmm=0};var fake=new FakeController(state);var confirmations=0;
            var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>{confirmations++;return false;}};
            var window=new Window{Width=1490,Height=900,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
            T Find<T>(string name)=>(T)view.FindName(name);
            void Click(string name){Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
            bool Under(DependencyObject child,DependencyObject ancestor)
            { for(DependencyObject? p=child;p is not null;p=VisualTreeHelper.GetParent(p)??LogicalTreeHelper.GetParent(p))if(ReferenceEquals(p,ancestor))return true;return false; }
            var map=Find<CManualInspectionMapControl>("PositionMap");var plot=Find<CManualInspectionScatterControl>("ScatterPlot");
            var p=state.Points[1] with{WorkingJudge="OK",WorkingXUm=0,WorkingYUm=0,BaseXUm=0,BaseYUm=0,HasAppliedOffset=true,IsProxy=false,Source="MEASURED"};
            string Color(CManualInspectionUiPoint point,string mode="level")=>((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,point,mode,state)).Color.ToString();
            Test("workflow_color_mode_is_fixed_measurement_level",view.FindName("ColorFilter") is null&&MeasurementMapMode(view)=="level");
            Test("workflow_scatter_defaults_to_original_measurement_and_all_heads",Field<bool>(view,"_scatterOriginal")&&Field<string>(view,"_scatterScope")=="HEAD"&&Field<int>(view,"_scatterHead")==0&&plot.DataKind=="ORIGINAL");
            SelectMeasurementView(view); // The remaining workflow assertions exercise the retained raw/measurement view.
            foreach(var theme in new[]{"Light","Dark"})
            {
                LoadResources(theme);Pump();
                foreach(var level in new[]{0d,7d,15d,25d})
                    Test("workflow_ng_always_red_"+theme+"_"+level,Color(p with{WorkingJudge="NG",WorkingXUm=level,BaseXUm=level})=="#FFD16776");
                Test("workflow_ng_red_in_judge_mode_"+theme,Color(p with{WorkingJudge="NG"},"judge")=="#FFD16776");
                Test("workflow_initialized_zero_is_gray_level_and_judge_"+theme,Color(p with{IsMeasurementOffsetInitialized=true,WorkingJudge="UNMEASURED"})=="#FFDBE4EA"&&Color(p with{IsMeasurementOffsetInitialized=true,WorkingJudge="UNMEASURED"},"judge")=="#FFDBE4EA");
                Test("workflow_actual_zero_measurement_retains_normal_level_"+theme,Color(p)=="#FF2E9076"&&CManualInspectionDrawing.Level(p,state)==0&&p.HasMeasurementHistory);
                Test("workflow_initialized_old_raw_does_not_reenable_history_"+theme,!(p with{IsMeasurementOffsetInitialized=true}).HasMeasurementHistory&&CManualInspectionDrawing.Level(p with{IsMeasurementOffsetInitialized=true},state)==-1);
            }
            Test("workflow_measurement_table_is_compact",Find<DataGrid>("ResultGrid").MaxHeight<=150);
            Test("workflow_scatter_plot_is_compact",plot.Height<=190);
            Test("workflow_filter_and_legend_are_in_left_inspector",Under(Find<Expander>("WorkInfoSection"),Find<ScrollViewer>("LeftInspectorScroll"))&&Under(Find<TextBlock>("FilterNoticeText"),Find<ScrollViewer>("LeftInspectorScroll"))&&Under(Find<WrapPanel>("MapLegendItems"),Find<ScrollViewer>("LeftInspectorScroll")));
            Test("workflow_source_markers_have_plain_language_explanation",Find<TextBlock>("MapLegendText").Text.Contains("초기화")||Find<Expander>("WorkInfoSection").Header.ToString()!.Contains("범례"));
            Test("workflow_removed_applied_columns_are_not_visible",view.FindName("ResultOffsetColumn") is null);
            Find<Expander>("InspectorSection").IsExpanded=true;Pump();Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([p.Key],p.Key));Pump();
            var before=fake.Calls.Count;Click("MeasureCurrentButton");
            Test("workflow_current_position_measure_button_dispatches_one_point_without_popup",fake.Calls.Count==before+1&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.Measure&&fake.Calls[^1].Request.Keys.SequenceEqual([p.Key])&&confirmations==0);
            var zeroPoint=p with{IsMeasurementOffsetInitialized=false,WorkingXUm=0,WorkingYUm=0};
            fake.Replace(state with{Version=8,MeasurementSequence=1,LastMeasuredPointKey=p.Key,LastMeasurementText="Cell 1 / Head 2 / Shot 2 · OK · X +0.000 / Y +0.000 µm",Points=state.Points.Select(x=>x.Key==p.Key?zeroPoint:x).ToArray()});Pump();
            Test("workflow_new_measurement_opens_inspector_and_focuses_measured_point",Find<Expander>("InspectorSection").IsExpanded&&Field<string?>(view,"_focusKey")==p.Key);
            Test("workflow_right_side_shows_fresh_measurement_result_without_correction",Find<TextBlock>("LastMeasurementText").Text==fake.Snapshot.LastMeasurementText&&Find<TextBlock>("LastMeasurementText").Text.Contains("+0.000")&&fake.Calls.All(x=>x.Action!=EN_MANUAL_INSPECTION_UI_ACTION.Apply));
            Find<Expander>("InspectorSection").IsExpanded=false;fake.Replace(fake.Snapshot with{Version=9,Message="파일 갱신 관찰"});Pump();
            Test("workflow_refresh_with_same_measurement_sequence_does_not_reopen_inspector",!Find<Expander>("InspectorSection").IsExpanded);
            fake.Replace(fake.Snapshot with{Version=10,MeasurementSequence=2,LastMeasurementText="Cell 1 / Head 2 · NG · X +25 / Y -8 µm",Points=fake.Snapshot.Points.Select(x=>x.Key==p.Key?x with{WorkingJudge="NG",WorkingXUm=25,WorkingYUm=-8}:x).ToArray()});Pump();
            Test("workflow_next_ng_measurement_reopens_inspector_and_updates_right_result",Find<Expander>("InspectorSection").IsExpanded&&Find<TextBlock>("LastMeasurementText").Text.Contains("NG"));
            Find<Expander>("CorrectionSection").IsExpanded=true;Pump();
            Invoke(view,"SetMapCellScope",2);before=fake.Calls.Count;Click("OffsetInitButton");
            Test("workflow_cell_init_dispatches_current_cell_without_popup",fake.Calls.Count==before+1&&fake.Calls[^1].Request.CurrentCellNo==2&&fake.Calls[^1].Request.OffsetInitScope=="CELL"&&fake.Calls[^1].Request.OffsetInitCellNo==2&&confirmations==0);
            Test("workflow_init_has_no_ambiguous_model_or_head_controls",new[]{"InitScopeCombo","InitCellCombo","InitHeadCombo"}.All(name=>view.FindName(name)is null));
            Invoke(view,"SetMapCellScope",1);
            var movingBefore=fake.Calls.Count;((Task)Invoke(view,"ConfirmOperation",EN_MANUAL_INSPECTION_UI_ACTION.Move,new[]{p.Key},fake.Snapshot.Version,"P2P")!).GetAwaiter().GetResult();Pump();
            Test("workflow_motion_still_requires_confirmation_and_cancel_prevents_dispatch",confirmations==1&&fake.Calls.Count==movingBefore);
            fake.Replace(fake.Snapshot with{CandidateRows=[new CManualInspectionUiCandidateRow{Key=p.Key,Source="MEASURED"}],CandidateStatus="DRAFT"});Pump();
            var cb=0;var beforeValidation=fake.Calls.Count;view.ConfirmationForTesting=(_,_,_)=>{cb++;return true;};
            ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,new CManualInspectionUiRequest{ExpectedVersion=fake.Snapshot.Version,CurrentCellNo=1,Keys=[p.Key]})!).GetAwaiter().GetResult();
            Test("workflow_removed_selected_correction_cannot_bypass_cell_save",fake.Calls.Count==beforeValidation&&cb==0&&view.FindName("CalculateButton") is null);
            var savedState=fake.Snapshot;
            fake.Replace(savedState with{Version=savedState.Version+1,HasIncomplete=true,RemainingCount=2,IsOffsetEditPending=false,StageXmm=0,StageYmm=0});Pump();
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([p.Key],p.Key));Pump();
            Test("workflow_failed_queue_keeps_retry_optional_and_allows_new_current_measure",Find<Button>("RetryButton").IsEnabled&&Find<Button>("MeasureCurrentButton").IsEnabled);
            Test("workflow_failed_queue_keeps_apply_guard_but_allows_init_and_explanation",view.FindName("CalculateButton") is null&&Find<Button>("BulkApplyButton").IsEnabled&&Find<Button>("OffsetInitButton").IsEnabled);
            var count=fake.Calls.Count;var popups=cb;Click("MeasureCurrentButton");
            Test("workflow_new_current_measure_supersedes_failed_queue_without_motion_popup",fake.Calls.Count==count+1&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.Measure&&cb==popups);
            var confirmedText="";view.ConfirmationForTesting=(_,text,_)=>{confirmedText=text;return false;};count=fake.Calls.Count;
            Invoke(view,"ConfirmOperation",EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,new[]{p.Key},fake.Snapshot.Version,"P2P");Pump();
            Test("workflow_new_measure_confirmation_describes_replacing_failed_queue",confirmedText.Contains("이전 잔여 목록")&&confirmedText.Contains("완료한 측정")&&fake.Calls.Count==count);
            view.ConfirmationForTesting=(_,_,_)=>true;
            Invoke(view,"ConfirmOperation",EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,new[]{p.Key},fake.Snapshot.Version,"P2P");Pump();
            Test("workflow_new_measure_after_failure_dispatches_only_current_selection",fake.Calls.Count==count+1&&fake.Calls[^1].Request.Keys.SequenceEqual([p.Key]));
            fake.Replace(savedState with{Version=fake.Snapshot.Version+1});Pump();view.ConfirmationForTesting=(_,_,_)=>{cb++;return true;};
            var beforeSettingsConfirmation=cb;Find<Expander>("SettingsSection").IsExpanded=true;Pump();Click("LevelsButton");
            Test("workflow_level_settings_open_inline_without_popup",cb==beforeSettingsConfirmation&&Find<StackPanel>("LevelSettingsPanel").Visibility==Visibility.Visible);
            foreach(var size in new[]{(1490,900),(1120,560)})
            {
                window.Width=size.Item1;window.Height=size.Item2;Find<Expander>("InspectorSection").IsExpanded=false;Find<Expander>("LeftInspectorSection").IsExpanded=false;Find<Expander>("ResultsSection").IsExpanded=false;Find<Expander>("ScatterSection").IsExpanded=false;Pump();
                Test("workflow_map_keeps_scroll_free_priority_"+size.Item1+"x"+size.Item2,Find<ScrollViewer>("WorkspaceScroll").ScrollableHeight<=1&&map.ActualHeight>=350);
                foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"measurement-workflow-"+size.Item1+"x"+size.Item2+"-"+theme.ToLowerInvariant()+".png");}
                Find<Expander>("ScatterSection").IsExpanded=true;Find<TabControl>("ScatterPaneTabs").SelectedIndex=0;Pump();var chartViewport=Find<ScrollViewer>("ScatterSectionContent");
                var chartBounds=plot.TransformToAncestor(chartViewport).TransformBounds(new Rect(plot.RenderSize));var visibleChart=Rect.Intersect(chartBounds,new Rect(0,0,chartViewport.ViewportWidth,chartViewport.ViewportHeight));
                Test("workflow_left_scatter_graph_page_shows_plot_"+size.Item1+"x"+size.Item2,Find<TabControl>("LeftPaneTabs").SelectedIndex==3&&visibleChart.Height>=150&&plot.ActualHeight<=190,
                    new{chartBounds,visibleHeight=visibleChart.Height,chartViewport.ViewportHeight});
                Find<Expander>("ScatterSection").IsExpanded=false;Pump();
            }
            window.Width=1490;window.Height=900;Find<Expander>("InspectorSection").IsExpanded=true;Find<Expander>("WorkInfoSection").IsExpanded=true;Find<Expander>("ResultsSection").IsExpanded=true;Find<Expander>("ScatterSection").IsExpanded=true;Pump();
            foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"measurement-workflow-expanded-"+theme.ToLowerInvariant()+".png");}window.Close();
        }
        catch(Exception e){Test("measurement_workflow_unhandled",false,e.ToString());}
        var files=new[]{"Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs","Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs"};
        var hashes=files.ToDictionary(f=>f,f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,f)))).ToLowerInvariant());
        var report=new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,sourceHashes=hashes,scope="Source-linked native WPF, synthetic immutable snapshots and fake controller only. No equipment App/Root/Manager/SDK, motion/Vision/PLC or scripts executed."};
        File.WriteAllText(Path.Combine(_output,"ui-measurement-workflow-results.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Measurement UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }
}
