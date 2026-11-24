using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int Representative()
    {
        try
        {
            LoadResources("Dark");
            var basis=Snapshot(240) with { CoordinateSignature="geometry-1" };
            var seed=basis.Points.First(p=>!p.IsProxy&&p.WorkingJudge=="OK");
            var cases=new (string Name,CManualInspectionUiPoint Point,bool Copy)[]
            {
                ("measured",seed with{ReferenceFinalXUm=12,ReferenceFinalYUm=-3,ReferenceValueKind="MEASURED"},true),
                ("ng",seed with{WorkingJudge="NG",ReferenceFinalXUm=12,ReferenceFinalYUm=-3,ReferenceValueKind="MEASURED"},true),
                ("proxy",seed with{Source="MANUAL",IsProxy=true,ReferenceFinalXUm=12,ReferenceFinalYUm=-3,ReferenceValueKind="PROXY"},true),
                ("saved",seed with{Source="KEPT",WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=true,ReferenceFinalXUm=12,ReferenceFinalYUm=-3,ReferenceValueKind="APPLIED_OFFSET"},false),
                ("unadopted_file",seed with{Source="IMPORTED",CanUseImportedForCorrection=false,HasAppliedOffset=false,ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="파일 기준 확인 필요"},false),
                ("no_value",seed with{Source="KEPT",WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=false,ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="복사할 값 없음"},false),
                ("initialized",seed with{IsMeasurementOffsetInitialized=true,Source="OFFSET_INIT",WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=true,BaseXUm=0,BaseYUm=0,ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="초기화 · 값 없음"},false),
                ("error",seed with{Source="KEPT",WorkingJudge="ERROR",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=false,ReferenceValueUnavailableReason="유효한 값 없음"},false),
                ("nonfinite",seed with{WorkingXUm=double.NaN,WorkingYUm=double.PositiveInfinity,HasAppliedOffset=false,ReferenceValueUnavailableReason="유효한 값 없음"},false),
                ("masked",seed with{IsMasked=true,ReferenceFinalXUm=12,ReferenceFinalYUm=-3,ReferenceValueKind="MEASURED"},true),
                ("partial_final",seed with{ReferenceFinalXUm=12,ReferenceFinalYUm=null},false),
                ("nonfinite_final",seed with{ReferenceFinalXUm=double.NaN,ReferenceFinalYUm=-3},false)
            };
            var fake=new FakeController(basis);
            var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>true};
            var window=new Window{Width=1490,Height=900,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
            window.Show();Pump();SelectMeasurementView(view);
            T Find<T>(string name)=>(T)view.FindName(name);
            Find<Expander>("InspectorSection").IsExpanded=true;
            var version=basis.Version;
            foreach(var item in cases)
            {
                var points=basis.Points.Select(p=>p.Key==seed.Key?item.Point:p).ToArray();
                fake.Replace(basis with{Version=++version,Points=points});Pump();
                Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([seed.Key],seed.Key));Pump();
                Test("any_point_reference_button_enabled_"+item.Name,Find<Button>("SetReferenceButton").IsEnabled);
                Invoke(view,"SetReference_Click",view,new RoutedEventArgs());Pump();
                Test("any_point_reference_marker_set_without_backend_"+item.Name,Field<string?>(view,"_representativeKey")==seed.Key&&fake.Calls.Count==0);
                Test("reference_copy_value_eligibility_separate_"+item.Name,item.Point.CanCopyAsReference==item.Copy);
                if(!item.Copy)
                    Test("unavailable_reference_disables_copy_without_invented_value_"+item.Name,!Find<Button>("CopyReferenceButton").IsEnabled);
                if(item.Name=="masked")
                    Test("masked_reference_focus_never_enters_measurement_selection",!Field<HashSet<string>>(view,"_selected").Contains(seed.Key));
            }
            fake.Replace(basis with{Version=++version});Pump();
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([seed.Key],seed.Key));
            Invoke(view,"SetReference_Click",view,new RoutedEventArgs());Pump();
            var changed=basis.Points.Select(p=>p.Key==seed.Key?p with{Source="IMPORTED",WorkingJudge="ERROR",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=false,ReferenceValueUnavailableReason="값 없음"}:p).ToArray();
            fake.Replace(basis with{Version=++version,Points=changed});Pump();
            Test("representative_marker_survives_source_judge_value_and_validation_changes",Field<string?>(view,"_representativeKey")==seed.Key);
            fake.Replace(basis with{Version=++version,Points=changed,IsOffsetEditPending=true,OffsetInitPending=true});Pump();
            Test("representative_marking_allowed_during_unsaved_offset_edit",Find<Button>("SetReferenceButton").IsEnabled);
            Invoke(view,"SetReference_Click",view,new RoutedEventArgs());Pump();
            Test("representative_marking_never_saves_or_cancels_pending_edit",fake.Calls.Count==0&&fake.Snapshot.IsOffsetEditPending&&Field<string?>(view,"_representativeKey")==seed.Key);
            fake.Replace(basis with{Version=++version,CoordinateSignature="geometry-2"});Pump();
            Test("coordinate_frame_change_clears_old_representative",Field<string?>(view,"_representativeKey") is null);
            Invoke(view,"SetReference_Click",view,new RoutedEventArgs());Pump();
            fake.Replace(basis with{Version=++version,RecipeId="OTHER"});Pump();
            Test("model_change_clears_old_representative",Field<string?>(view,"_representativeKey") is null);
            foreach(var theme in new[]{"Light","Dark"})
            {
                LoadResources(theme);fake.Replace(basis with{Version=++version});Pump();
                Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([seed.Key],seed.Key));
                Invoke(view,"SetReference_Click",view,new RoutedEventArgs());Pump();
                Render(view,"representative-any-point-"+theme.ToLowerInvariant()+".png");
            }
            var cleared=seed with{Source="MANUAL_STATE",WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,
                OriginalJudge="OK",OriginalXUm=2,OriginalYUm=-3,BaseXUm=7,BaseYUm=-9,HasAppliedOffset=true,
                IsProxy=false,IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=true,CanCopyReferenceState=true,
                ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="빈 상태"};
            fake.Replace(basis with{Version=++version,Points=[cleared]});Pump();
            Set(view,"_scatterScope","HEAD");Set(view,"_scatterHead",0);SelectViewChoice(view,"ScatterDataCombo","WORKING");Invoke(view,"Refresh");Pump();
            var scatter=Find<CManualInspectionScatterControl>("ScatterPlot");
            Test("state_copy_working_scatter_has_no_fabricated_zero_or_old_B_sample",scatter.Metrics.Count==0&&cleared.MeasurementXUm is null&&cleared.MeasurementYUm is null);
            SelectViewChoice(view,"ScatterDataCombo","ORIGINAL");Invoke(view,"Refresh");Pump();
            Test("state_copy_original_scatter_keeps_preserved_raw",scatter.Metrics.Count==1&&scatter.Metrics.MeanX==2&&scatter.Metrics.MeanY==-3);
            SelectViewChoice(view,"ScatterDataCombo","WORKING");Invoke(view,"Refresh");Pump();
            Test("state_copy_offset_scatter_keeps_actual_saved_B",scatter.Metrics.Count==0&&cleared.MeasurementXUm is null);
            Test("state_copy_scatter_projections_do_not_modify_source_state_or_model_values",fake.Snapshot.Points.Single()==cleared&&fake.Calls.Count==0);
            window.Close();
        }
        catch(Exception error){Test("representative_unexpected_exception",false,error.ToString());}
        var files=new[]{"Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs","Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs"};
        var hashes=files.ToDictionary(f=>f,f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,f)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output,"ui-representative-results.json"),JsonSerializer.Serialize(new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,sourceHashes=hashes,
            scope="Source-linked real WPF controls; synthetic immutable snapshots and inert controller. No equipment App/Root/Manager/SDK, motion, PLC, Vision or scripts."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Representative UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }
}
