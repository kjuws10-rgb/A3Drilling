using System.IO;
using System.Reflection;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.File.ReviewResult;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunBulkHistoryChecks()
    {
        static Dictionary<string,ST_REVIEW_OFFSET_ROW> Working(Fixture f,CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values)
            =>(Dictionary<string,ST_REVIEW_OFFSET_ROW>)typeof(CManualInspectionBackend).GetMethod("BuildWorkingRows",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(f.Backend,[s,values,false])!;
        static CManualInspectionSource Desired(CManualInspectionSource s,IReadOnlySet<string> initialized)=>s with
        {HasExplicitMeasurementState=true,Points=s.Points.Select(p=>p with{IsMeasurementOffsetInitialized=initialized.Contains(p.OffsetKey)}).ToArray()};
        await Check("empty_review_metadata_distinguishes_missing_initialized_and_real_measured_zero",()=>
        {
            const string prefix="CELL1_A1_REVIEW_OFFSET_";var p=new Dictionary<string,string>{{"H01_DEFAULT_OFFSET_X","12"}};
            CReviewOffsetData.ApplyToParameters(p,ST_REVIEW_OFFSET_FILE_DATA.Empty("QA"));True(!CReviewOffsetData.HasMeasurementValue(p,prefix)&&p["H01_DEFAULT_OFFSET_X"]=="12");
            var f=new ST_REVIEW_OFFSET_FILE_DATA("QA","","",DateTimeOffset.Now,[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);
            CReviewOffsetData.ApplyToParameters(p,f);True(p[prefix+"STATE"]=="INITIALIZED"&&!CReviewOffsetData.HasMeasurementValue(p,prefix));
            CReviewOffsetData.ApplyToParameters(p,f with{Rows=[new(1,"A1",0,0){IsDesignCoordinate=true}]});True(CReviewOffsetData.HasMeasurementValue(p,prefix)&&p[prefix+"STATE"]=="MEASURED");
            CReviewOffsetData.RemoveFromParameters(p);True(!p.Keys.Any(k=>k.StartsWith(prefix,StringComparison.Ordinal))&&p["H01_DEFAULT_OFFSET_X"]=="12");return Task.CompletedTask;
        });
        await Check("empty_review_legacy_pair_is_explicit_value_but_default_global_zero_is_not",()=>
        {
            const string prefix="CELL1_A1_REVIEW_OFFSET_";var p=new Dictionary<string,string>{{"REVIEW_OFFSET_X","0"},{"REVIEW_OFFSET_Y","0"}};True(!CReviewOffsetData.HasMeasurementValue(p,prefix));
            p[prefix+"X"]="0";True(!CReviewOffsetData.HasMeasurementValue(p,prefix));p[prefix+"Y"]="0";True(CReviewOffsetData.HasMeasurementValue(p,prefix));
            p[prefix+"STATE"]="INVALID";var rejected=false;try{CReviewOffsetData.HasMeasurementValue(p,prefix);}catch(InvalidOperationException){rejected=true;}True(rejected);return Task.CompletedTask;
        });
        await Check("empty_simple_current_display_is_absent_and_real_zero_remains_numeric",()=>
        {
            var input=new CSimpleCorrectionInput(1,1,"S1","A1");input.UpdateCurrent(0,0,false);True(input.CurrentX=="—"&&input.CurrentY=="—");
            input.UpdateCurrent(0,0,true);True(input.CurrentX=="0.000"&&input.CurrentY=="0.000");return Task.CompletedTask;
        });
        await Check("empty_rough_current_display_is_absent_and_real_zero_remains_numeric",()=>
        {
            var f=new Fixture();var point=f.Review.CreateAllPlan(f.Recipe,11).Points.First();var row=new CRoughCorrectionRow(point,"A1",_ =>(0d,0d));
            row.SetCurrent((0,0),false);True(row.CurrentX=="—"&&row.CurrentY=="—");row.SetCurrent((0,0),true);True(row.CurrentX=="0.000"&&row.CurrentY=="0.000");return Task.CompletedTask;
        });
        await Check("empty_review_viewer_and_cell_rows_never_print_zero_as_precision",()=>
        {
            var row=new ST_ALIGN_OFFSET_VIEW_ROW("A1","H01",0,0,0,0,""){HasMeasurementValue=false};True(row.DesignOffsetX=="—"&&row.ScannerOffsetGx=="—");
            var empty=new ST_CORRECTION_REVIEW_OFFSET_ROW("A1","H01",0,0,"EMPTY");var real=empty with{State="MEASURED"};True(empty.OffsetX=="—"&&empty.OffsetY=="—"&&real.OffsetX.Contains("0.000"));return Task.CompletedTask;
        });
        await Check("empty_review_apply_preview_distinguishes_missing_current_value_from_real_zero",()=>
        {
            var row=new CMenuCorrection.ST_REVIEW_OFFSET_APPLY_GRID_ROW(1,1,"A1","S1",0,0,1,2,1,2){HasCurrentMeasurementValue=false};
            True(row.CurrentXText=="—"&&row.CurrentYText=="—"&&row.AddXText=="1.000"&&(row with{HasCurrentMeasurementValue=true}).CurrentXText=="0.000");return Task.CompletedTask;
        });
        foreach(var initialize in new[]{true,false})await Check("empty_fine_init_clears_raw_eligibility_but_real_zero_apply_keeps_measurement_"+initialize,()=>
        {
            var f=new Fixture();var p=f.Review.CreateAllPlan(f.Recipe,11).Points.First() with{State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=0,ErrorY=0};
            var line=new CFineInspectionLine("L1",1,1,1,0,true,[p]);
            typeof(CFineInspectionLine).GetMethod("ApplyResults",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(line,[new[]{new ST_FINE_SHOT_RESULT(p.ShotKey,0,0,"OK")},EN_VISION_FLIP_TYPE.Normal]);
            line.Update(EN_FINE_LINE_STATE.Complete);var panel=new CFineCorrectionPanel();typeof(CFineCorrectionPanel).GetProperty("Lines")!.SetValue(panel,new[]{line});
            True(line.MeasuredCount==1&&line.Statistics.All(s=>s.Average=="0.000"));panel.AcceptOffsetSave(initialize,"synthetic.csv");
            if(initialize)True(line.State==EN_FINE_LINE_STATE.Wait&&line.Points.All(x=>x.State==EN_REVIEW_POINT_STATE.Ready&&x.Judge=="WAIT")&&line.MeasuredCount==0&&line.ShotResults.All(x=>x.ReceivedX is null&&x.ReceivedY is null)&&line.Statistics.All(s=>s.Average=="-")&&!panel.CanApplyOffset);
            else True(line.State==EN_FINE_LINE_STATE.Complete&&line.MeasuredCount==1&&line.ShotResults.All(x=>x.ReceivedX==0)&&line.Statistics.All(s=>s.Average=="0.000"));return Task.CompletedTask;
        });
        await Check("empty_native_model_has_no_measured_offsets_and_retains_missing_keys_for_undo",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);True(s.AppliedOffsetKeys.Count==0&&s.StoredInitializedOffsetKeys is not null&&s.StoredInitializedOffsetKeys.Count==s.Points.Select(p=>p.OffsetKey).Distinct().Count());
            True(s.Points.All(p=>p.IsMeasurementOffsetInitialized&&!p.HasFiniteError));
        });
        await Check("empty_native_missing_zero_rows_export_as_absent_not_measured_precision",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);var path=Path.Combine(f.Root,"missing-zero.json");await f.Backend.ExportOffsetBundleAsync(s,s.Values,path,false,false,default);
            var bundle=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));True(bundle.ReviewRows.All(r=>r.IsMeasurementOffsetInitialized&&r.XMm==0&&r.YMm==0));
        });
        await Check("empty_native_first_real_zero_save_creates_measured_row_without_untouched_missing_rows",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);var first=s.Points.First(p=>!p.Masked);var initialized=s.Points.Select(p=>p.OffsetKey).ToHashSet();initialized.Remove(first.OffsetKey);
            var rows=Working(f,Desired(s,initialized),s.Values);var key=$"{first.CellNo}:{CReviewShotNameFormatter.ToMatrixName(first.ShotNo,8)}";
            True(rows.Count==1&&rows.ContainsKey(key)&&rows[key].ReviewOffsetX==0&&!rows[key].IsMeasurementOffsetInitialized);
            var saved=await f.Offsets.SaveNew("QA",rows.Values.ToArray());True(saved.Rows.Count==1&&!saved.Rows[0].IsMeasurementOffsetInitialized);
        });
        await Check("empty_native_partial_bulk_does_not_synthesize_init_rows_that_erase_unsaved_ng",async()=>
        {
            var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var selected=plan.Points.Where(p=>p.Use).Take(2).ToArray();True(selected.Length==2);
            var result=new CReviewResultFile(f.Root+"/Config");await result.Save(new(plan,[selected[0] with{State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=.002,ErrorY=-.003},selected[1] with{State=EN_REVIEW_POINT_STATE.Ng,Judge="NG",ErrorX=.009,ErrorY=-.008}],DateTimeOffset.Now,"QA-GLASS"));
            var path=result.LastSavedPath!;f.Results.Files[path]=await result.Load(path);var s=await f.Backend.LoadAsync(path,default);var ok=s.Points.Single(p=>p.ShotNo==selected[0].ShotNo);var ng=s.Points.Single(p=>p.ShotNo==selected[1].ShotNo);
            var initialized=s.Points.Select(p=>p.OffsetKey).ToHashSet();initialized.Remove(ok.OffsetKey);var values=s.Values.ToDictionary(p=>p.Key,p=>p.Value);values[ok.Key]=new(2,3);
            var rows=Working(f,Desired(s,initialized),values);var ngName=CReviewShotNameFormatter.ToMatrixName(ng.ShotNo,8);True(!rows.ContainsKey($"{ng.CellNo}:{ngName}"));
            await f.Offsets.SaveNew("QA",rows.Values.ToArray());var loaded=await f.Backend.LoadAsync(path,default);var kept=loaded.Points.Single(p=>p.Key==ng.Key);
            True(kept.Judge=="NG"&&kept.HasFiniteError&&!kept.IsMeasurementOffsetInitialized&&!loaded.AppliedOffsetKeys.Contains(ng.Key));
        });
        await Check("empty_fine_external_init_invalidates_whole_column_including_unmeasured_target_shot_only",()=>
        {
            var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var seed=plan.Points.First();
            var a=seed with{ShotKey="FINE-A",ShotNo=1,DesignX=1,State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=.001,ErrorY=.002};
            var b=seed with{ShotKey="FINE-B",ShotNo=2,DesignX=2,State=EN_REVIEW_POINT_STATE.Ready,Judge="ERROR",ErrorX=0,ErrorY=0};
            var hidden=seed with{ShotKey="FINE-HIDDEN",ShotNo=17,DesignX=1,State=EN_REVIEW_POINT_STATE.Ready,Judge="WAIT"};
            var line=new CFineInspectionLine("FINE-L",1,1,1,0,true,[a,b]);var panel=new CFineCorrectionPanel();
            typeof(CFineCorrectionPanel).GetField("_source",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(panel,plan with{Points=[a,b,hidden]});
            typeof(CFineCorrectionPanel).GetProperty("RecipeId")!.SetValue(panel,"QA");typeof(CFineCorrectionPanel).GetProperty("Lines")!.SetValue(panel,new[]{line});
            panel.ApplyProgress(new("QA",[new("FINE-L",1,1,[a,b],EN_FINE_REVIEW_LINE_STATE.Complete)],EN_REVIEW_SEQUENCE_STATE.Completed,"synthetic measured"),panel.SourceVersion);
            var epoch=DateTimeOffset.UtcNow.AddSeconds(1);var name=CReviewShotNameFormatter.ToMatrixName(hidden.ShotNo,hidden.PixelCountX);var file=new ST_REVIEW_OFFSET_FILE_DATA("QA","synthetic.csv","synthetic.csv",epoch,[new(1,name,0,0){IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=epoch}]);
            panel.ObserveOffsetInitializations(file);True(line.Points.Single(x=>x.ShotKey==a.ShotKey).Judge=="WAIT"&&line.Points.Single(x=>x.ShotKey==b.ShotKey).Judge=="ERROR"&&line.MeasuredCount==0&&line.ShotResults.Single(x=>x.Shot==CReviewShotNameFormatter.ToMatrixName(b.ShotNo,b.PixelCountX)).Judge=="ERROR");
            return Task.CompletedTask;
        });
        await Check("empty_fine_repeated_old_init_epoch_does_not_erase_newly_measured_zero",()=>
        {
            var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var a=plan.Points.First() with{ShotKey="FINE-NEW",State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=0,ErrorY=0};
            var line=new CFineInspectionLine("FINE-L",1,1,1,0,true,[a]);var panel=new CFineCorrectionPanel();
            typeof(CFineCorrectionPanel).GetField("_source",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(panel,plan with{Points=[a]});typeof(CFineCorrectionPanel).GetProperty("RecipeId")!.SetValue(panel,"QA");typeof(CFineCorrectionPanel).GetProperty("Lines")!.SetValue(panel,new[]{line});
            var old=DateTimeOffset.UtcNow.AddMinutes(-1);var file=new ST_REVIEW_OFFSET_FILE_DATA("QA","old.csv","old.csv",old,[new(a.CellNo,CReviewShotNameFormatter.ToMatrixName(a.ShotNo,a.PixelCountX),0,0){IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=old}]);
            panel.ApplyProgress(new("QA",[new("FINE-L",1,1,[a],EN_FINE_REVIEW_LINE_STATE.Complete)],EN_REVIEW_SEQUENCE_STATE.Completed,"fresh real zero"),panel.SourceVersion);
            panel.ObserveOffsetInitializations(file);panel.ObserveOffsetInitializations(file with{FilePath="later.csv",SavedAt=DateTimeOffset.UtcNow.AddMinutes(1)});
            True(line.Points.Single().Judge=="OK"&&line.MeasuredCount==1&&line.Statistics.All(s=>s.Average=="0.000"));return Task.CompletedTask;
        });
        await Check("empty_rough_external_init_clears_representative_scope_from_another_assigned_shot",()=>
        {
            var f=new Fixture();var plan=f.Review.CreateAllPlan(f.Recipe,11);var a=plan.Points.First() with{ShotKey="ROUGH-A",State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=.001,ErrorY=.002};
            var owner=new CSimpleCorrectionPanel();var panel=new CRoughCorrectionPanel(owner);var row=new CRoughCorrectionRow(a,"A1",_ =>(0d,0d));
            typeof(CRoughCorrectionPanel).GetField("_recipe",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,f.Recipe);typeof(CRoughCorrectionPanel).GetProperty("Rows")!.SetValue(panel,new[]{row});
            var target=new ST_SIMPLE_CORRECTION_SHOT("UNMEASURED-TARGET","B1",a.HeadNo,1,0,true,"READY",false,false){CellNo=a.CellNo};
            typeof(CRoughCorrectionPanel).GetField("_assigned",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,new[]{target});
            panel.ApplyProgress(new("QA",[a],EN_REVIEW_SEQUENCE_STATE.Completed,"synthetic rough measurement"),panel.SourceVersion);
            var epoch=DateTimeOffset.UtcNow.AddSeconds(1);panel.ObserveOffsetInitializations(new("QA","future.csv","future.csv",epoch,[new(a.CellNo,"B1",0,0){IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=epoch}]));
            True(row.Point.Judge=="WAIT"&&row.Point.State==EN_REVIEW_POINT_STATE.Ready&&!panel.HasApplicableMeasurement(a.HeadNo,a.CellNo));return Task.CompletedTask;
        });
        await Check("empty_rough_first_seen_old_init_and_repeated_epoch_keep_fresh_zero_and_error",()=>
        {
            var f=new Fixture();var seed=f.Review.CreateAllPlan(f.Recipe,11).Points.First();var a=seed with{ShotKey="ROUGH-REALZERO",State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=0,ErrorY=0};var owner=new CSimpleCorrectionPanel();var panel=new CRoughCorrectionPanel(owner);var row=new CRoughCorrectionRow(a,"A1",_ =>(0d,0d));
            typeof(CRoughCorrectionPanel).GetField("_recipe",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,f.Recipe);typeof(CRoughCorrectionPanel).GetProperty("Rows")!.SetValue(panel,new[]{row});
            typeof(CRoughCorrectionPanel).GetField("_assigned",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,new[]{new ST_SIMPLE_CORRECTION_SHOT(a.ShotKey,"A1",a.HeadNo,0,0,true,"READY",false,false){CellNo=a.CellNo}});
            var old=DateTimeOffset.UtcNow.AddMinutes(-1);var file=new ST_REVIEW_OFFSET_FILE_DATA("QA","old.csv","old.csv",old,[new(a.CellNo,"A1",0,0){IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=old}]);
            panel.ApplyProgress(new("QA",[a],EN_REVIEW_SEQUENCE_STATE.Completed,"fresh zero"),panel.SourceVersion);panel.ObserveOffsetInitializations(file);True(row.Point.Judge=="OK");
            panel.ApplyProgress(new("QA",[a with{State=EN_REVIEW_POINT_STATE.Ready,Judge="ERROR"}],EN_REVIEW_SEQUENCE_STATE.Completed,"fresh ERROR"),panel.SourceVersion);panel.ObserveOffsetInitializations(file with{FilePath="later.csv",SavedAt=DateTimeOffset.UtcNow.AddMinutes(1)});True(row.Point.Judge=="ERROR");return Task.CompletedTask;
        });
    }
}
