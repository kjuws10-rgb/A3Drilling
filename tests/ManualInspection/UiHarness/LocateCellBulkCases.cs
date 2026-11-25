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
    private static int LocateCellBulk()
    {
        try { RunLocatedPointChecks(); RunOverviewBulkChecks(); RunSavedFinalReadoutChecks(); }
        catch (Exception error) { Test("locate_bulk_unexpected_exception",false,error.ToString()); }
        var hashes=new[]{"Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs","Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs"}
            .ToDictionary(path=>path,path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,path)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output,"ui-locate-cell-bulk-results.json"),JsonSerializer.Serialize(new
        {
            passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,sourceHashes=hashes,
            scope="Actual source-linked standalone WPF and current themes with immutable synthetic data and fake fresh-position responses. No equipment App, vendor, motion, Vision, physical position read or operating CSV executes. Feedback validation and multi-Cell CSV persistence are separate production Backend/Controller/Core checks."
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Locate/Cell bulk UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }

    private static void RunLocatedPointChecks()
    {
        foreach(var theme in new[]{"Dark","Light"})
        {
            LoadResources(theme);var state=Snapshot(240) with {CoordinateSignature="LOCATE_FRAME",StageXmm=0,StageYmm=0};
            var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};
            var window=HostRepresentativeView(view,theme=="Dark"?1120:1490,theme=="Dark"?560:900);
            try
            {
                ((Expander)view.FindName("InspectorSection")).IsExpanded=true;Pump();
                var map=(CManualInspectionMapControl)view.FindName("PositionMap");var locate=(Button)view.FindName("LocateCurrentButton");
                var point=state.Points[165];var raw=JsonSerializer.Serialize(state.Points);
                Test($"locate_{theme}_button_beside_measure",locate.IsVisible&&view.FindName("MeasureCurrentButton") is Button measure&&ReferenceEquals(locate.Parent,measure.Parent));
                SelectViewChoice(view,"JudgeFilter","NG");
                fake.OnExecute=(action,request)=>{if(action==EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition)fake.Replace(state with{Version=8,CurrentPositionPointKey=point.Key,StageXmm=12,StageYmm=34});};
                locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Test($"locate_{theme}_fresh_display_only_action",fake.Calls.Count==1&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition);
                Test($"locate_{theme}_opens_exact_cell_focus_and_reveals_filtered_point",Field<int>(view,"_cell")==point.CellNo&&Field<string?>(view,"_focusKey")==point.Key&&Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered").Any(p=>p.Key==point.Key));
                Test($"locate_{theme}_starts_distinct_500ms_locator",Field<string?>(map,"_locatedPointKey")==point.Key&&Field<bool>(map,"_locationBlinkVisible")&&Field<DispatcherTimer>(map,"_locationBlinkTimer").Interval==TimeSpan.FromMilliseconds(500));
                Test($"locate_{theme}_leaves_measurements_unchanged",JsonSerializer.Serialize(fake.Snapshot.Points)==raw&&fake.Snapshot.MeasurementSequence==state.MeasurementSequence);
                var mapped=Slots(map)[point.Key];
                var rendered=new Rect(new Point(mapped.Left*map.Zoom+map.Pan.X,mapped.Top*map.Zoom+map.Pan.Y),new Size(mapped.Width*map.Zoom,mapped.Height*map.Zoom));
                Test($"locate_{theme}_actual_slot_inside_viewport",rendered.IntersectsWith(new Rect(map.RenderSize)));
                Render(view,$"locate-{theme.ToLowerInvariant()}.png");
                Test($"locate_{theme}_large_map_and_no_workspace_scroll",map.ActualWidth>=350&&((ScrollViewer)view.FindName("WorkspaceScroll")).ScrollableHeight<1);
                Invoke(map,"AdvanceLocationBlink");Test($"locate_{theme}_blink_toggles_without_equipment",!Field<bool>(map,"_locationBlinkVisible")&&fake.Calls.Count==1);
                for(var i=0;i<8;i++)Invoke(map,"AdvanceLocationBlink");
                Test($"locate_{theme}_blink_expires_and_stops_timer",Field<string?>(map,"_locatedPointKey") is null&&!Field<DispatcherTimer>(map,"_locationBlinkTimer").IsEnabled);

                fake.OnExecute=(action,request)=>{if(action==EN_MANUAL_INSPECTION_UI_ACTION.LocateCurrentPosition)fake.Replace(state with{Version=9,CurrentPositionPointKey=null,LastMeasuredPointKey=point.Key});};
                locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Test($"locate_{theme}_no_match_never_falls_back_to_recent",Field<string?>(map,"_locatedPointKey") is null&&Field<string>(view,"_localMessage").Contains("일치하는 측정점이 없습니다"));
                fake.OnExecute=(action,request)=>fake.Replace(state with{Version=10,RunId="OTHER_RUN",CurrentPositionPointKey=point.Key});
                locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Test($"locate_{theme}_frame_change_during_read_blocks_highlight",Field<string?>(map,"_locatedPointKey") is null&&Field<string>(view,"_localMessage").Contains("기준이 바뀌었습니다"));
                fake.Replace(state with{Version=20,CurrentPositionPointKey=point.Key});Pump();
                // Simulate a newer Controller snapshot whose queued UI Changed event has not run yet.
                Set(fake,"<Snapshot>k__BackingField",state with{Version=21,CurrentPositionPointKey=point.Key});
                var rejectedCalls=fake.Calls.Count;locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Test($"locate_{theme}_stale_preflight_never_blinks_old_acknowledged_key",fake.Calls.Count==rejectedCalls&&Field<string?>(map,"_locatedPointKey") is null);
                fake.Replace(state with{Version=22,CurrentPositionPointKey=point.Key});Pump();
                fake.OnExecute=(_,_)=>throw new IOException("synthetic read failed; old response deliberately retained");
                locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Test($"locate_{theme}_failed_dispatch_never_blinks_old_key",fake.Calls.Count==rejectedCalls+1&&Field<string?>(map,"_locatedPointKey") is null);
                var busy=state with{Version=11,IsBusy=true,IsMeasurementInProgress=true,ActiveMeasurementPointKeys=[point.Key],CurrentPositionPointKey=point.Key};
                fake.Replace(busy);Pump();var count=fake.Calls.Count;
                Test($"locate_{theme}_busy_button_disabled",!locate.IsEnabled);
                Invoke(view,"LocateCurrent_Click",locate,new RoutedEventArgs());Pump();
                Test($"locate_{theme}_busy_cannot_dispatch_second_read",fake.Calls.Count==count);
                var measured=Field<bool>(map,"_measurementBlinkVisible");Invoke(map,"AdvanceLocationBlink");
                Test($"locate_{theme}_independent_locator_never_changes_measurement_blink",Field<bool>(map,"_measurementBlinkVisible")==measured);
                fake.Replace(state with{Version=12,CurrentPositionPointKey=point.Key});Pump();map.LocatePoint(point.Key);
                fake.Replace(state with{Version=13,RunId="NEW_FRAME",CurrentPositionPointKey=point.Key});Pump();
                Test($"locate_{theme}_new_frame_clears_old_locator_even_same_key",Field<string?>(map,"_locatedPointKey") is null);
                Test($"locate_{theme}_unknown_map_key_rejected",!map.LocatePoint("missing"));
            }
            finally{window.Close();Pump();}
        }
    }

    private static void RunOverviewBulkChecks()
    {
        LoadResources("Dark");var original=Snapshot(240);var points=original.Points.Select(p=>p with{IsMasked=false}).ToArray();
        var state=original with{Version=300,Points=points,CanBulkApply=true,CellBulkEligibleCounts=new Dictionary<int,int>{{1,5},{2,3},{3,2}},CellBulkSummaryTexts=new Dictionary<int,string>{{1,"Cell 1 · 새 OK 5"},{2,"Cell 2 · 새 OK 3"},{3,"Cell 3 · 새 OK 2"}}};
        var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var window=HostRepresentativeView(view);
        try
        {
            var bulk=(Button)view.FindName("BulkApplyButton");var confirmations=0;string text="";
            view.ConfirmationForTesting=(_,message,_)=>{confirmations++;text=message;return true;};
            void Choose(params string[] keys){Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(keys,keys.FirstOrDefault()));Pump();}
            void Save(){bulk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
            Invoke(view,"SetMapCellScope",0);Choose();
            Test("overview_bulk_no_selection_disabled",!bulk.IsEnabled);
            Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,null!);Pump();
            Test("overview_bulk_empty_never_means_whole_model",fake.Calls.Count==0&&confirmations==0);
            Choose(points[1].Key,points[161].Key);SelectViewChoice(view,"JudgeFilter","NG");Save();
            Test("overview_bulk_correction_status_and_section_describe_selected_cells",((TextBlock)view.FindName("SaveStatusText")).Text.Contains("선택 Cell들")&&((Expander)view.FindName("CorrectionSection")).Header.ToString()!.Contains("선택 Cell들"));
            Test("overview_bulk_uses_only_selected_cell_set_not_focus_or_filter",fake.Calls.Count==1&&fake.Calls[0].Request.CurrentCellNo is null&&fake.Calls[0].Request.CellNos.SequenceEqual([1,3]));
            Test("overview_bulk_confirmation_lists_cells_and_combined_eligibility",text.Contains("Cell 1, 3")&&text.Contains("새 OK 7")&&text.Contains("다른 Cell은 유지"));
            Test("overview_bulk_button_and_summary_describe_explicit_scope",bulk.Content.ToString()!.Contains("선택 2 Cell")&&((TextBlock)view.FindName("BulkSummaryText")).Text.Contains("새 OK 7"));
            var before=fake.Calls.Count;view.ConfirmationForTesting=(_,_,_)=>{Choose(points[81].Key);return true;};Save();
            Test("overview_bulk_selection_cell_change_in_modal_blocks_dispatch",fake.Calls.Count==before);
            Choose(points[1].Key,points[2].Key);view.ConfirmationForTesting=(_,_,_)=>{Choose(points[3].Key);return true;};Save();
            Test("overview_bulk_same_cell_point_change_does_not_change_scope",fake.Calls.Count==before+1&&fake.Calls[^1].Request.CellNos.SequenceEqual([1]));
            Invoke(view,"SetMapCellScope",2);Choose(points[1].Key);view.ConfirmationForTesting=(_,_,_)=>true;Save();
            Test("single_cell_bulk_ignores_other_cell_point_selection",fake.Calls[^1].Request.CurrentCellNo==2&&fake.Calls[^1].Request.CellNos.SequenceEqual([2]));
            fake.Replace(state with{Version=301,IsOffsetEditPending=true,PendingOffsetCellNo=1});Invoke(view,"SetMapCellScope",0);Choose(points[1].Key,points[81].Key);
            Test("overview_bulk_pending_cell_cannot_mix_other_cells",!bulk.IsEnabled);
            Choose(points[1].Key);Test("overview_bulk_pending_exact_single_cell_allowed",bulk.IsEnabled);
            before=fake.Calls.Count;Save();Test("overview_bulk_pending_request_is_explicit_singleton",fake.Calls.Count==before+1&&fake.Calls[^1].Request.CellNos.SequenceEqual([1]));
            fake.Replace(state with{Version=302,CellBulkEligibleCounts=new Dictionary<int,int>{{1,5},{2,0},{3,2}}});Choose(points[81].Key);before=fake.Calls.Count;confirmations=0;Save();
            Test("overview_bulk_zero_eligible_does_not_use_other_cells",fake.Calls.Count==before&&confirmations==0);
            Choose(points[1].Key,points[161].Key);view.ConfirmationForTesting=(_,_,_)=>false;Save();
            Test("overview_bulk_cancel_never_dispatches",fake.Calls.Count==before);
        }
        finally{window.Close();Pump();}
    }

    private static void RunSavedFinalReadoutChecks()
    {
        foreach(var theme in new[]{"Dark","Light"})
        {
            LoadResources(theme);var first=Snapshot(80);var target=first.Points[2] with
            {
                WorkingJudge="OK",OriginalJudge="OK",WorkingXUm=2,WorkingYUm=-3,OriginalXUm=2,OriginalYUm=-3,
                IsMasked=false,IsProxy=false,IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false,
                BaseXUm=100,BaseYUm=200,CandidateXUm=999,CandidateYUm=-999,ReferenceFinalXUm=90,ReferenceFinalYUm=80
            };
            var state=first with{Version=900,Points=[target],HasFinalOffsetSnapshot=true,
                FinalOffsetFileName="QA_ONLY_reviewoffset.csv",FinalOffsetFilePath=@"C:\SyntheticOnly\QA_ONLY_reviewoffset.csv",FinalOffsetRowCount=1,
                FinalOffsetPoints=new Dictionary<string,CManualInspectionUiOffset>{{target.Key,new(-12,34)}},
                FinalAppliedPointKeys=new HashSet<string>{target.Key},FinalInitializedPointKeys=new HashSet<string>()};
            var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var window=HostRepresentativeView(view,theme=="Dark"?1120:1490,theme=="Dark"?560:900);
            try
            {
                ((Expander)view.FindName("InspectorSection")).IsExpanded=true;
                Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([target.Key],target.Key));Pump();
                var measured=(TextBlock)view.FindName("PointErrorText");var final=(TextBlock)view.FindName("PointFinalOffsetText");
                Test($"saved_readout_{theme}_separate_deviation_and_canonical_saved_final",measured.Text.Contains("ΔX/Y")&&measured.Text.Contains("+2.000 / -3.000")&&final.Text.Contains("F X/Y -12.000 / +34.000")&&final.Text.Contains("µm")&&!final.Text.Contains("999")&&!final.Text.Contains("100.000"));
                Test($"saved_readout_{theme}_both_lines_visible_full_width",measured.IsVisible&&final.IsVisible&&measured.ActualWidth>180&&final.ActualWidth>180&&final.ActualHeight>=final.FontSize);
                Test($"saved_readout_{theme}_map_level_keeps_actual_deviation_not_large_saved_F",CManualInspectionDrawing.Level(target,state)==0&&((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,target,"level",state)).Color.ToString()=="#FF2E9076");
                Render(view,$"dual-readout-{theme.ToLowerInvariant()}.png");
                fake.Replace(state with{Version=901,FinalOffsetPoints=new Dictionary<string,CManualInspectionUiOffset>{{target.Key,new(-50,-60)}}});Pump();
                Test($"saved_readout_{theme}_latest_saved_map_updates_without_point_array_change",ReferenceEquals(fake.Snapshot.Points,state.Points)&&final.Text.Contains("-50.000 / -60.000")&&measured.Text.Contains("+2.000 / -3.000"));
                fake.Replace(state with{Version=902,HasFinalOffsetSnapshot=false});Pump();
                Test($"saved_readout_{theme}_unknown_final_never_falls_back_to_Base_candidate_or_raw",final.Text.Contains("확인 불가")&&!final.Text.Contains("100.000")&&!final.Text.Contains("2.000")&&!final.Text.Contains("999"));
                fake.Replace(state with{Version=903,FinalInitializedPointKeys=new HashSet<string>{target.Key}});Pump();
                Test($"saved_readout_{theme}_initialized_saved_row_is_absence_not_numeric_zero",final.Text.Contains("값 없음")&&final.Text.Contains("초기화")&&!final.Text.Contains("0.000"));
                fake.Replace(state with{Version=904,FinalAppliedPointKeys=new HashSet<string>()});Pump();
                Test($"saved_readout_{theme}_saved_value_requires_applied_row_evidence",final.Text.Contains("저장값 없음")&&!final.Text.Contains("12.000"));
                fake.Replace(state with{Version=905,FinalOffsetPoints=new Dictionary<string,CManualInspectionUiOffset>{{target.Key,new(0,0)}}});Pump();
                Test($"saved_readout_{theme}_actual_saved_zero_remains_numeric_zero",final.Text.Contains("0.000 / 0.000")&&!final.Text.Contains("없음"));
                var ng=target with{WorkingJudge="NG"};
                Test($"saved_readout_{theme}_NG_stays_red_independent_of_saved_F",((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,ng,"level",state)).Color.ToString()=="#FFD16776");
                Test($"saved_readout_{theme}_readout_never_dispatches_controller_or_mutates_raw",fake.Calls.Count==0&&fake.Snapshot.Points.Single().OriginalXUm==2&&fake.Snapshot.Points.Single().WorkingYUm==-3);
            }
            finally{window.Close();Pump();}
        }
    }
}
