using System.Collections;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Drilling.Common.Managers;
using Drilling.UI.Popup;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int RepresentativeApply()
    {
        var observations=new List<object>();
        try { RunRepresentativeConfirmationChecks(observations);RunActualCopyConfirmationThemes();RunCopyPendingWriteChecks();RunManualInputChecks(observations);RunManualContextChecks(observations);RunRealNumericKeypadChecks(observations); }
        catch(Exception error){Test("representative_apply_unexpected_exception",false,error.ToString());}
        var files=new[]{"Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs","Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs","Popup/CValueInputDialog.xaml","Popup/CValueInputDialog.xaml.cs"};
        var hashes=files.ToDictionary(file=>file,file=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        var common=Path.GetFullPath(Path.Combine(_uiSource,"..","Drilling.Common","Managers","CRecipeManager.cs"));
        hashes["../Drilling.Common/Managers/CRecipeManager.cs"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(common))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(_output,"ui-representative-apply-results.json"),JsonSerializer.Serialize(new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,observations,sourceHashes=hashes,
            scope="Source-linked actual WPF Manual controls and actual numeric keypad; actual Common data-type enum is extracted at build time. Immutable synthetic points, fake Controller only: no production App/Root/Manager, equipment, vendor, Vision, motion, CSV save or actual correction executes. Core/Controller saving is tested separately."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Representative apply UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }

    private static CManualInspectionUiSnapshot RepresentativeApplyState()
    {
        var source=FinalOffsetFixture();var first=source.Points[0];
        return source with{Version=500,Points=source.Points.Select(point=>point.Key==first.Key?point with{WorkingJudge="OK",WorkingXUm=3,WorkingYUm=-2,OriginalJudge="OK",OriginalXUm=3,OriginalYUm=-2,
            ReferenceFinalXUm=3,ReferenceFinalYUm=-2,ReferenceValueKind="MEASURED",ReferenceValueJudge="OK",ReferenceValueUnavailableReason=""}:point).ToArray()};
    }

    private static Window HostRepresentativeView(CManualInspectionView view,double width=1490,double height=900)
    {
        var host=new Window{Width=width,Height=height,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();Pump();return host;
    }
    private static void RunRepresentativeConfirmationChecks(List<object> observations)
    {
        LoadResources("Dark");var state=RepresentativeApplyState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        T Find<T>(string name)=>(T)view.FindName(name);
        void ClickCopy(){Find<Button>("CopyReferenceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
        void SelectCopy()
        {
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[0].Key],state.Points[0].Key));
            Invoke(view,"SetReference_Click",view,new RoutedEventArgs());
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[1].Key,state.Points[2].Key],state.Points[1].Key));Pump();
        }
        SelectCopy();var confirmations=new List<(string Title,string Text,string Button)>();var raw=JsonSerializer.Serialize(state.Points);
        view.ConfirmationForTesting=(title,text,button)=>{confirmations.Add((title,text,button));return false;};ClickCopy();
        Test("copy_popup_cancel_is_inert_before_dispatch",confirmations.Count==1&&fake.Calls.Count==0&&JsonSerializer.Serialize(fake.Snapshot.Points)==raw);
        Test("copy_popup_names_reference_target_count_and_storage_effect",confirmations[0].Text.Contains("추천 Review")&&confirmations[0].Text.Contains("2")&&confirmations[0].Text.Contains("저장")&&confirmations[0].Title.Contains("대표"));
        Test("copy_popup_explains_non_motion_and_original_measurement_preservation",confirmations[0].Text.Contains("현재 Cell")&&confirmations[0].Text.Contains("원측정"));
        var before=fake.Calls.Count;view.ConfirmationForTesting=(title,text,button)=>{confirmations.Add((title,text,button));ClickCopy();return true;};ClickCopy();
        Test("copy_popup_nested_second_click_does_not_dispatch_twice",fake.Calls.Count==before+1&&confirmations.Count==2&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.CopyReference);
        Test("copy_accepted_request_freezes_reference_targets_and_current_version",fake.Calls[^1].Request.ReferenceKey==state.Points[0].Key&&fake.Calls[^1].Request.Keys.ToHashSet().SetEquals([state.Points[1].Key,state.Points[2].Key])&&fake.Calls[^1].Request.ExpectedVersion==state.Version);
        before=fake.Calls.Count;view.ConfirmationForTesting=(_,_,_)=>{fake.Replace(state with{Version=501});return true;};ClickCopy();
        Test("copy_popup_external_version_change_rejects_stale_accept",fake.Calls.Count==before&&Find<TextBlock>("MessageText").Text.Contains("기준"));
        state=state with{Version=502};fake.Replace(state);Pump();SelectCopy();before=fake.Calls.Count;
        var other=new FakeController(state);view.ConfirmationForTesting=(_,_,_)=>{view.DataContext=other;return true;};ClickCopy();
        Test("copy_popup_controller_replaced_at_same_version_is_rejected",fake.Calls.Count==before&&other.Calls.Count==0);
        view.DataContext=fake;Pump();SelectCopy();view.ConfirmationForTesting=(_,_,_)=>throw new InvalidOperationException("synthetic confirm failure");before=fake.Calls.Count;ClickCopy();
        Test("copy_popup_exception_restores_idle_controls_without_dispatch",fake.Calls.Count==before&&!Field<bool>(view,"_actionConfirmationPending")&&Find<Button>("CopyReferenceButton").IsEnabled);
        view.ConfirmationForTesting=(_,_,_)=>true;fake.OnExecute=(action,request)=>
        {
            if(action!=EN_MANUAL_INSPECTION_UI_ACTION.CopyReference)return;
            fake.Replace(fake.Snapshot with{Version=503,
                Points=fake.Snapshot.Points.Select(point=>request.Keys.Contains(point.Key)?point with{WorkingJudge="OK",WorkingXUm=3,WorkingYUm=-2,ReferenceFinalXUm=3,ReferenceFinalYUm=-2,IsProxy=true,ReferenceKey=state.Points[0].Key,IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false}:point).ToArray()});
        };
        ClickCopy();
        var map=Find<CManualInspectionMapControl>("PositionMap");var projected=Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all");
        Test("copy_working_reply_updates_measurement_map_without_saving_csv",projected.Single(point=>point.Key==state.Points[1].Key).WorkingXUm==3&&projected.Single(point=>point.Key==state.Points[1].Key).WorkingYUm==-2&&fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
        Test("copy_view_never_fabricates_original_raw_from_saved_reply",fake.Snapshot.Points[1].OriginalJudge==state.Points[1].OriginalJudge&&fake.Snapshot.Points[1].OriginalXUm==state.Points[1].OriginalXUm);
        state=state with{Version=504,Points=state.Points.Select((point,index)=>index==0?point with{WorkingJudge="NG",ReferenceValueJudge="NG"}:point).ToArray()};fake.Replace(state);Pump();SelectCopy();
        fake.OnExecute=null;string ngDescription="";view.ConfirmationForTesting=(_,text,_)=>{ngDescription=text;return true;};before=fake.Calls.Count;ClickCopy();
        Test("copy_ng_reference_still_requires_confirm_and_describes_state_only",fake.Calls.Count==before+1&&ngDescription.Contains("NG")&&ngDescription.Contains("NG·빈 상태 제외")&&fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
        state=state with{Version=505,Points=state.Points.Select((point,index)=>index==0?point with{WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,ReferenceValueJudge="UNMEASURED",
            ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="빈 측정 상태",CanCopyReferenceState=true,HasAppliedOffset=false,IsMeasurementOffsetInitialized=true}:point).ToArray()};
        fake.Replace(state);Pump();SelectCopy();string emptyDescription="";view.ConfirmationForTesting=(_,text,_)=>{emptyDescription=text;return true;};before=fake.Calls.Count;ClickCopy();
        Test("copy_unmeasured_reference_confirms_state_only_without_inventing_numeric_value",fake.Calls.Count==before+1&&emptyDescription.Contains("빈 상태")&&fake.Snapshot.Points[0].WorkingXUm is null&&fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
        observations.Add(new{copyConfirmations=confirmations.Count,copyCalls=fake.Calls.Count});host.Close();Pump();
    }

    private static void RunManualInputChecks(List<object> observations)
    {
        LoadResources("Light");var state=RepresentativeApplyState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        var point=state.Points[1];var prompts=new List<(string Prompt,string Initial)>();var entered=new Queue<string?>();
        void Input(){((Task)Invoke(view,"InputMeasurementValues",point.Key,fake.Snapshot.Version)!).GetAwaiter().GetResult();Pump();}
        view.NumericInputForTesting=(prompt,initial)=>{prompts.Add((prompt,initial));return entered.Dequeue();};
        entered.Enqueue(null);Input();Test("keypad_cancel_x_does_not_ask_y_or_change_work",prompts.Count==1&&fake.Calls.Count==0);
        prompts.Clear();entered.Enqueue("12.5");entered.Enqueue(null);Input();Test("keypad_cancel_y_does_not_commit_partial_x",prompts.Count==2&&fake.Calls.Count==0);
        prompts.Clear();entered.Enqueue("12.5");entered.Enqueue("-7.25");Input();
        Test("keypad_confirm_dispatches_one_force_action_with_explicit_design_um",fake.Calls.Count==1&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement
            &&fake.Calls[0].Request.Keys.SequenceEqual([point.Key])&&fake.Calls[0].Request.ForcedXUm==12.5&&fake.Calls[0].Request.ForcedYUm==-7.25&&prompts.All(entry=>entry.Prompt.Contains("µm")));
        Test("keypad_confirm_never_dispatches_motion_measurement_or_apply",fake.Calls.All(call=>call.Action==EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement));
        foreach(var invalid in new[]{"","NaN","Infinity","not-a-number"})
        {
            var before=fake.Calls.Count;entered.Enqueue(invalid);entered.Enqueue("1");Input();Test("keypad_invalid_input_is_not_invented_zero_"+(invalid.Length==0?"blank":invalid),fake.Calls.Count==before);
        }
        var emptyPoint=point with{WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=false,OriginalXUm=null,OriginalYUm=null,ReferenceFinalXUm=null,ReferenceFinalYUm=null};
        fake.Replace(state with{Version=505,Points=state.Points.Select(p=>p.Key==point.Key?emptyPoint:p).ToArray()});Pump();prompts.Clear();entered.Enqueue("0");entered.Enqueue("0");Input();
        Test("keypad_missing_values_start_blank_and_explicit_typed_zero_is_allowed",prompts.All(entry=>entry.Initial=="")&&fake.Calls[^1].Request.ForcedXUm==0&&fake.Calls[^1].Request.ForcedYUm==0);
        Test("keypad_explicit_zero_does_not_create_original_camera_raw",fake.Snapshot.Points.Single(p=>p.Key==point.Key).OriginalXUm is null);
        var beforeStale=fake.Calls.Count;var axis=0;view.NumericInputForTesting=(_,_)=>{axis++;if(axis==1)fake.Replace(fake.Snapshot with{Version=506});return "1";};Input();
        Test("keypad_external_source_change_rejects_pair_before_dispatch",fake.Calls.Count==beforeStale);
        axis=0;var changedController=new FakeController(fake.Snapshot);view.NumericInputForTesting=(_,_)=>{axis++;if(axis==1)view.DataContext=changedController;return "1";};Input();
        Test("keypad_controller_swap_at_same_version_cannot_dispatch_into_new_controller",fake.Calls.Count==beforeStale&&changedController.Calls.Count==0);
        view.DataContext=fake;Pump();var numericCalls=0;view.NumericInputForTesting=(_,_)=>{numericCalls++;if(numericCalls==1)((Task)Invoke(view,"InputMeasurementValues",point.Key,fake.Snapshot.Version)!).GetAwaiter().GetResult();return "2";};Input();
        Test("keypad_nested_double_open_is_blocked_during_modal_pair",numericCalls==2&&fake.Calls.Count==beforeStale+1&&!Field<bool>(view,"_actionConfirmationPending"));
        var beforeError=fake.Calls.Count;view.NumericInputForTesting=(_,_)=>throw new InvalidOperationException("synthetic keypad failure");Input();
        Test("keypad_exception_restores_idle_without_partial_dispatch",fake.Calls.Count==beforeError&&!Field<bool>(view,"_actionConfirmationPending"));
        observations.Add(new{typedCalls=fake.Calls.Count,typedAction="ForceMeasurement",sourceRawUntouched=fake.Snapshot.Points.Single(p=>p.Key==point.Key).OriginalXUm is null});host.Close();Pump();
    }

    private static void RunActualCopyConfirmationThemes()
    {
        foreach(var theme in new[]{"Dark","Light"})
        {
            LoadResources(theme);var state=RepresentativeApplyState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[0].Key],state.Points[0].Key));Invoke(view,"SetReference_Click",view,new RoutedEventArgs());
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[1].Key,state.Points[2].Key],state.Points[1].Key));Pump();var observed=false;
            _application.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>
            {
                var popup=_application.Windows.Cast<Window>().Single(window=>window.Title=="대표값 복사 확인");observed=true;
                Test("real_copy_confirmation_inherits_current_theme_"+theme,((SolidColorBrush)popup.Background).Color.ToString()==ResourceColor("PanelBackgroundBrush"));
                Render(popup,"representative-confirm-"+theme.ToLowerInvariant()+".png");popup.DialogResult=false;
            }));
            ((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("real_copy_confirmation_cancel_never_dispatches_or_edits_"+theme,observed&&fake.Calls.Count==0&&fake.Snapshot==state);host.Close();Pump();
        }
    }

    private static void RunCopyPendingWriteChecks()
    {
        LoadResources("Dark");var state=RepresentativeApplyState();var fake=new HoldingCopyController(state);var confirmations=0;
        var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>{confirmations++;return true;}};var host=HostRepresentativeView(view);
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[0].Key],state.Points[0].Key));Invoke(view,"SetReference_Click",view,new RoutedEventArgs());
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[1].Key],state.Points[1].Key));Pump();var copy=(Button)view.FindName("CopyReferenceButton");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("copy_write_pending_blocks_second_confirmation_and_second_dispatch",fake.Calls==1&&confirmations==1&&!copy.IsEnabled);
        fake.Complete();Pump();Test("copy_write_completion_releases_ui_command_gate",copy.IsEnabled&&Field<int>(view,"_requests")==0);host.Close();Pump();
    }

    private sealed class HoldingCopyController(CManualInspectionUiSnapshot state):IManualInspectionController
    {
        private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CManualInspectionUiSnapshot Snapshot{get;private set;}=state;
        public event EventHandler? Changed;
        public int Calls;
        public Task ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION action,CManualInspectionUiRequest request,CancellationToken cancellationToken=default)
        {Calls++;return _release.Task;}
        public void Complete(){Snapshot=Snapshot with{Version=Snapshot.Version+1};Changed?.Invoke(this,EventArgs.Empty);_release.TrySetResult();}
    }

    private static void RunManualContextChecks(List<object> observations)
    {
        LoadResources("Dark");var state=RepresentativeApplyState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>throw new InvalidOperationException("Manual OK is not a motion operation")};var host=HostRepresentativeView(view);
        var map=(CManualInspectionMapControl)view.FindName("PositionMap");var key=state.Points[0].Key;var other=state.Points[1].Key;
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([key,other],key));Pump();map.ShowOperationMenu();Pump();
        var menu=map.ContextMenu!;var force=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()?.StartsWith("추천 보정값 확정",StringComparison.Ordinal)==true);
        var edit=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()?.StartsWith("보정값 직접 입력",StringComparison.Ordinal)==true);
        force.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();menu.IsOpen=false;
        Test("context_force_ok_dispatches_selected_keys_without_motion_or_implicit_save",fake.Calls.Count==1&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement
            &&fake.Calls[0].Request.Keys.ToHashSet().SetEquals([key,other])&&fake.Calls[0].Request.ForcedXUm is null&&fake.Calls[0].Request.ForcedYUm is null);
        var numeric=0;view.NumericInputForTesting=(_,_)=>{numeric++;return numeric%2==1?"4":"-6";};edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        Test("context_direct_input_edits_focus_only_and_passes_explicit_pair",numeric==2&&fake.Calls.Count==2&&fake.Calls[^1].Request.Keys.SequenceEqual([key])&&fake.Calls[^1].Request.ForcedXUm==4&&fake.Calls[^1].Request.ForcedYUm==-6);
        var originalNg=state.Points[1];var manual=originalNg with{WorkingJudge="OK",IsOperatorResult=true,OperatorReason="사용자 우클릭 확정",Source="OPERATOR",IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false,CandidateXUm=null,CandidateYUm=null,
            DisplayedMeasurementOrigin="사용자 수동 확정 · 원측정 NG 보존"};
        fake.Replace(state with{Version=506,Points=state.Points.Select(point=>point.Key==other?manual:point).ToArray()});Pump();
        Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([other],other));Pump();
        Test("manual_ok_reply_is_labeled_operator_while_original_ng_stays_preserved",((TextBlock)view.FindName("PointJudgesText")).Text.Contains("수동")
            &&!((TextBlock)view.FindName("PointErrorText")).Text.Contains("실측")&&fake.Snapshot.Points.Single(point=>point.Key==other).OriginalJudge=="NG");
        SelectViewChoice(view,"SourceFilter","OPERATOR");var rows=((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().ToArray();
        Test("manual_ok_source_filter_lists_operator_result_without_promoting_raw",rows.Length==1&&manual.MeasurementSourceCode=="OPERATOR"&&manual.OriginalXUm==originalNg.OriginalXUm);
        Test("manual_ok_working_reply_does_not_change_saved_csv_identity",fake.Snapshot.FinalOffsetFilePath==state.FinalOffsetFilePath&&fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
        ((Expander)view.FindName("InspectorSection")).IsExpanded=true;((TabControl)view.FindName("RightPaneTabs")).SelectedIndex=0;Pump();
        foreach(var theme in new[]{"Dark","Light"}){LoadResources(theme);Pump();Render(view,"manual-ok-"+theme.ToLowerInvariant()+".png");}
        var finalOnly=manual with{WorkingValueKind="ABSOLUTE_REVIEW",WorkingXUm=null,WorkingYUm=null,OriginalXUm=null,OriginalYUm=null,OriginalJudge="UNMEASURED",BaseXUm=7,BaseYUm=-9,ReferenceFinalXUm=0,ReferenceFinalYUm=0,ReferenceValueKind="OPERATOR_FINAL"};
        fake.Replace(state with{Version=507,Points=[finalOnly]});Pump();Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([other],other));Pump();
        var errorText=((TextBlock)view.FindName("PointErrorText")).Text;
        Test("operator_final_only_never_invents_base_or_zero_as_residual",!finalOnly.UsesAppliedMeasurement&&finalOnly.MeasurementXUm is null&&finalOnly.MeasurementYUm is null
            &&errorText.Contains("유효 측정 편차 없음")&&!errorText.Contains("실측")&&!errorText.Contains("L1"));
        Render(view,"manual-final-only-light.png");
        SelectViewChoice(view,"ScatterDataCombo","ORIGINAL");var plot=(CManualInspectionScatterControl)view.FindName("ScatterPlot");var residualCount=plot.Metrics.Count;
        SelectViewChoice(view,"ScatterDataCombo","WORKING");
        Test("operator_F_only_is_in_working_review_but_not_original_scatter",residualCount==0&&plot.Metrics.Count==1&&plot.DataKind=="WORKING_REVIEW"&&plot.Metrics.MeanX==0);
        var capturedFinal=finalOnly with{BaseXUm=3,BaseYUm=-2,ReferenceFinalXUm=12,ReferenceFinalYUm=-8};
        fake.Replace(state with{Version=508,Points=[capturedFinal]});Pump();
        Test("operator_old_final_without_current_deviation_is_not_new_measurement",((TextBlock)view.FindName("PointErrorText")).Text.Contains("유효 측정 편차 없음")&&capturedFinal.MeasurementXUm is null&&capturedFinal.MeasurementYUm is null);
        var pendingFinal=capturedFinal with{HasAppliedOffset=false,ReferenceValueUnavailableReason=""};
        var incompleteFinals=new[]{pendingFinal with{ReferenceFinalYUm=null},pendingFinal with{ReferenceFinalXUm=double.NaN},pendingFinal with{ReferenceValueUnavailableReason="출처 미확인"},
            pendingFinal with{IsMasked=true},pendingFinal with{IsMeasurementOffsetInitialized=true},pendingFinal with{IsMeasurementStateCleared=true}};
        Test("pending_operator_final_is_completed_history_without_fabricated_residual",!pendingFinal.HasMeasurementHistory&&pendingFinal.MeasurementStatusText.Contains("실측 없음")
            &&pendingFinal.MeasurementXUm is null&&pendingFinal.MeasurementYUm is null&&!pendingFinal.UsesAppliedMeasurement&&incompleteFinals.All(point=>!point.HasMeasurementHistory));
        var pendingState=state with{Version=509,Points=[pendingFinal],FinalOffsetPoints=new Dictionary<string,CManualInspectionUiOffset>{{other,new(0,0)}},
            FinalAppliedPointKeys=new HashSet<string>(),FinalInitializedPointKeys=new HashSet<string>{other}};fake.Replace(pendingState);Pump();
        SelectViewChoice(view,"JudgeFilter","MEASURED");
        var measuredRows=((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Count();
        SelectViewChoice(view,"JudgeFilter","OK");var okRows=((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Count();
        SelectViewChoice(view,"JudgeFilter","UNMEASURED");var unmeasuredRows=((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Count();
        SelectViewChoice(view,"JudgeFilter","ALL");SelectViewChoice(view,"ScatterDataCombo","ORIGINAL");var unknownResidualCount=plot.Metrics.Count;
        SelectViewChoice(view,"ScatterDataCombo","WORKING");
        Test("pending_operator_final_filters_are_consistent_and_both_unsaved_scatter_populations_stay_empty",measuredRows==0&&okRows==0&&unmeasuredRows==1&&unknownResidualCount==0&&plot.Metrics.Count==1);
        SelectViewChoice(view,"SourceFilter","ALL");fake.Replace(state with{Version=509});Pump();Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([key,other],key));Pump();
        map.ShowOperationMenu();Pump();menu=map.ContextMenu!;force=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()?.StartsWith("추천 보정값 확정",StringComparison.Ordinal)==true);fake.Replace(state with{Version=510});Pump();var before=fake.Calls.Count;
        force.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();menu.IsOpen=false;Test("context_force_opened_before_source_change_does_not_dispatch",fake.Calls.Count==before);
        view.NumericInputForTesting=(_,_)=>null;
        Point Centre(){var rect=Slots(map)[key];return new Point((rect.Left+rect.Width/2)*map.Zoom+map.Pan.X,(rect.Top+rect.Height/2)*map.Zoom+map.Pan.Y);}
        var focusRect=Slots(map)[key];var inputPan=new Vector(map.ActualWidth/2-(focusRect.Left+focusRect.Width/2)*1.8,map.ActualHeight/2-(focusRect.Top+focusRect.Height/2)*1.8);
        map.RestoreView(1.8,inputPan);Pump();var at=Centre();var requests=0;map.ValueInputRequested+=(_,_)=>requests++;
        Invoke(map,"BeginPointerGesture",at,ModifierKeys.None,2);Pump();Test("point_double_click_opens_numeric_input_instead_of_fitting_or_executing",requests==1&&map.Zoom==1.8&&map.Pan==inputPan&&fake.Calls.Count==before);
        map.RestoreView(1.7,new Vector(9,-4));Pump();Invoke(map,"BeginPointerGesture",new Point(2,2),ModifierKeys.None,2);Pump();
        Test("blank_double_click_retains_fit_without_manual_or_device_action",requests==1&&map.Zoom==1&&map.Pan==default&&fake.Calls.Count==before);
        foreach(var modifiers in new[]{ModifierKeys.Control,ModifierKeys.Alt})
        {var count=requests;Invoke(map,"BeginPointerGesture",Centre(),modifiers,2);Pump();Test("modified_double_click_does_not_open_numeric_editor_"+modifiers,requests==count);}
        fake.Replace(state with{Version=511,IsBusy=true});Pump();var blocked=requests;Invoke(map,"BeginPointerGesture",Centre(),ModifierKeys.None,2);Pump();
        Test("busy_point_double_click_cannot_open_another_editor",requests==blocked&&fake.Calls.Count==before);
        fake.Replace(state with{Version=512,IsOffsetEditPending=true});Pump();Invoke(map,"BeginPointerGesture",Centre(),ModifierKeys.None,2);Pump();
        Test("pending_offset_edit_point_double_click_is_blocked",requests==blocked&&fake.Calls.Count==before);
        fake.Replace(state with{Version=513});Pump();Invoke(view,"SetMapCellScope",0);Pump();
        var countBeforeOverview=requests;Invoke(map,"BeginPointerGesture",Centre(),ModifierKeys.None,2);Pump();
        Test("overview_double_click_keeps_fit_without_numeric_input",requests==countBeforeOverview&&map.Zoom==1);
        // Exercise the real two-click scope transition, not merely a synthetic second-down in overview.
        var first=Centre();Invoke(map,"BeginPointerGesture",first,ModifierKeys.None,1);Invoke(map,"CompletePointerGesture",first);Pump();
        var afterNavigation=requests;Invoke(map,"BeginPointerGesture",Centre(),ModifierKeys.None,2);Pump();
        Test("overview_first_release_then_second_down_does_not_accidentally_edit",requests==afterNavigation&&fake.Calls.Count==before);
        observations.Add(new{manualContextCalls=fake.Calls.Count,numericRequests=requests});host.Close();Pump();
    }

    private static void RunRealNumericKeypadChecks(List<object> observations)
    {
        var text=File.ReadAllText(Path.GetFullPath(Path.Combine(_uiSource,"..","Drilling.Common","Managers","CRecipeManager.cs")));
        var start=text.IndexOf("public enum EN_RECIPE_DATA_TYPE",StringComparison.Ordinal);var end=text.IndexOf('}',start);
        var enumBody=text[(text.IndexOf('{',start)+1)..end];var actualNames=enumBody.Split(',').Select(name=>name.Trim()).Where(name=>name.Length>0).ToArray();
        Test("keypad_uses_exact_production_recipe_data_type_enum",Enum.GetNames<EN_RECIPE_DATA_TYPE>().SequenceEqual(actualNames));
        foreach(var theme in new[]{"Dark","Light"})
        {
            LoadResources(theme);var dialog=new CValueInputDialog("X 편차 · µm · 실제 키패드 검증","",EN_RECIPE_DATA_TYPE.Double,-1000,1000)
            {Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};
            dialog.Loaded+=(_,_)=>
            {
                var box=(TextBox)dialog.FindName("ValueTextBox");Test("real_keypad_missing_initial_value_stays_blank_"+theme,box.Text=="");box.Text="-12.5";
                Render(dialog,"manual-keypad-"+theme.ToLowerInvariant()+".png");
                var ok=((Grid)((Border)dialog.Content).Child).Children.OfType<Grid>().Single().Children.OfType<Button>().Single(button=>button.Content?.ToString()=="OK");
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            var accepted=dialog.ShowDialog();Test("real_keypad_accepts_signed_decimal_with_existing_ui_"+theme,accepted==true&&dialog.ResultValue=="-12.5");
            var cancelled=new CValueInputDialog("Y 편차 · µm","",EN_RECIPE_DATA_TYPE.Double,-1000,1000)
            {Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};
            cancelled.Loaded+=(_,_)=>{var button=((Grid)((Border)cancelled.Content).Child).Children.OfType<Grid>().Single().Children.OfType<Button>().Single(item=>item.Content?.ToString()=="CANCEL");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
            Test("real_keypad_cancel_keeps_missing_value_empty_"+theme,cancelled.ShowDialog()==false&&cancelled.ResultValue=="");observations.Add(new{theme,keypadAssembly=typeof(CValueInputDialog).Assembly.GetName().Name});
        }
    }
}
