using System.Globalization;
using System.IO;
using System.Reflection;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.File.ReviewOffset;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunFinalOffsetParityChecks()
    {
        static bool Close(double a,double b)=>Math.Abs(a-b)<1e-10;
        static Dictionary<string,string> Parameters(Fixture f)=>(Dictionary<string,string>)typeof(CManualInspectionBackend)
            .GetField("_parameters",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Backend)!;
        await Check("final_offset_stage_design_parity_256_direction_head_flip_scale_combinations",()=>
        {
            var tested=0;
            foreach(var direction in new[]{-1,1})foreach(var head in Enumerable.Range(1,8))foreach(var bits in Enumerable.Range(0,8))foreach(var scale in new[]{1d,.5d})
            {
                var flip=CReviewManager.CreateVisionFlipType((bits&1)!=0,(bits&2)!=0,(bits&4)!=0);
                var p=new Dictionary<string,string>{["STAGE_SCAN_DIRECTION_Y"]=direction.ToString(CultureInfo.InvariantCulture),
                    [$"H{head:00}_SCANNER_X_FLIP"]=(head&1)!=0?"ON":"OFF",[$"H{head:00}_SCANNER_Y_FLIP"]=(head&2)!=0?"ON":"OFF",[$"H{head:00}_SCANNER_XY_FLIP"]=(head&4)!=0?"ON":"OFF"};
                var anchor=new ST_REVIEW_PLAN_POINT(1,"CELL1_A1",head,1,1,1,1,true,1,2,0,0,.004,.006,EN_REVIEW_POINT_STATE.Ok,"OK");
                var stage=CReviewManager.CalculateFineOffsets([anchor],[anchor],flip).Single();
                var design=CReviewMeasurementOffset.StageToDesign(p,stage.X,stage.Y);
                var manual=CManualInspectionCoordinateBridge.FromVision(anchor.ErrorX,anchor.ErrorY,flip,p,head);
                True(Close(design.X,manual.XUm/1000)&&Close(design.Y,manual.YUm/1000));
                var scanner=CReviewOffsetData.ToScanner(p,head,design.X,design.Y,true);
                var expected=CReviewManager.VisionErrorToScannerOffset(anchor.ErrorX,anchor.ErrorY,flip,CScannerAxisTransform.Read(p,head));
                True(Close(scanner.X*scale,expected.X*scale)&&Close(scanner.Y*scale,expected.Y*scale));tested++;
            }
            True(tested==256);return Task.CompletedTask;
        });
        await Check("final_offset_review_data_all_result_types_add_design_delta_once_and_preserve_absolute_base",async()=>
        {
            foreach(var direction in new[]{-1,1})foreach(var type in new[]{"ALL_SHOT","ONE_SHOT","ONE_LINE","ROUGH","FINE","AUTO"})foreach(var designSpace in new[]{false,true})
            {
                var f=new Fixture(settingOverrides:new(){["STAGE_SCAN_DIRECTION_Y"]=direction.ToString(CultureInfo.InvariantCulture)});
                var initial=await f.Backend.LoadAsync(null,default);var parameters=Parameters(f);var point=f.Review.CreateAllPlan(f.Recipe,11).Points.First(p=>p.Use&&p.HeadNo>0);
                var shot=CReviewShotNameFormatter.ToMatrixName(point.ShotNo,point.PixelCountX);
                var stored=designSpace?(.012,-.021):CScannerAxisTransform.DesignOffsetToScanner(parameters,point.HeadNo,.012,-.021);
                var latest=await f.Offsets.SaveNew("QA",[new(point.CellNo,shot,stored.Item1,stored.Item2){IsDesignCoordinate=designSpace},new(99,"OUTSIDE",.08,.09){IsDesignCoordinate=true}]);
                var at=DateTimeOffset.UtcNow;var result=new ST_REVIEW_RESULT_FILE_DATA("synthetic.csv","synthetic.csv","QA",at,
                    [new(at,"QA",point.ShotKey,shot,point.HeadNo,point.CellNo,.004,.006,"OK"){BeamNo=11}]){ResultStatus="COMPLETED",BeamNo=11,RuleScope="HEAD_CELL",LineCount=1};
                var catalog=new ST_REVIEW_RESULT_CATALOG_ITEM(result.FilePath,result.FileName,"MANUAL",type,"QA",at,"COMPLETED","SYNTHETIC","MANUAL","P2P",1,"");
                var panel=new CMenuCorrection.CReviewDataPanel(f.Results,f.Offsets,f.Review,new(new RecipesFile(f)),f.Settings,()=>"QA",_=>{},()=>Task.CompletedTask,(_,_,_)=>throw new NotSupportedException());
                var apply=typeof(CMenuCorrection.CReviewDataPanel).GetMethod("BuildOffsetApplyPlan",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(panel,[catalog,result,f.Recipe,parameters,latest])!;
                var rows=(IReadOnlyList<ST_REVIEW_OFFSET_ROW>)apply.GetType().GetProperty("SavedRows")!.GetValue(apply)!;
                var target=rows.Single(r=>r.CellId==point.CellNo&&r.ShotId==shot);var delta=CReviewMeasurementOffset.StageToDesign(parameters,.004,.006);
                True(target.IsDesignCoordinate&&Close(target.ReviewOffsetX,CAlignShotOffsetData.RoundValue(.012+delta.X))&&Close(target.ReviewOffsetY,CAlignShotOffsetData.RoundValue(-.021+delta.Y)));
                True(rows.Single(r=>r.CellId==99)==latest.Rows.Single(r=>r.CellId==99));
                var display=(System.Collections.IEnumerable)apply.GetType().GetProperty("Rows")!.GetValue(apply)!;
                var targetDisplay=display.Cast<object>().First(r=>string.Equals((string?)r.GetType().GetProperty("ShotName")!.GetValue(r),shot,StringComparison.Ordinal));
                True(Close((double)targetDisplay.GetType().GetProperty("AddY")!.GetValue(targetDisplay)!,delta.Y));
            }
        });
        await Check("final_offset_auto_actual_save_converts_new_delta_once_and_preserves_other_rows",async()=>
        {
            foreach(var direction in new[]{-1,1})foreach(var bits in Enumerable.Range(0,8))
            {
                var f=new Fixture(settingOverrides:new(){["STAGE_SCAN_DIRECTION_Y"]=direction.ToString(CultureInfo.InvariantCulture)});await f.Backend.LoadAsync(null,default);var parameters=Parameters(f);
                var target=f.Review.CreateAllPlan(f.Recipe,11).Points.First(p=>p.Use&&p.HeadNo>0);var shot=CReviewShotNameFormatter.ToMatrixName(target.ShotNo,target.PixelCountX);
                await f.Offsets.SaveNew("QA",[new(target.CellNo,shot,.012,-.021){IsDesignCoordinate=true},new(99,"OUTSIDE",.08,.09){IsDesignCoordinate=true}]);
                var flip=CReviewManager.CreateVisionFlipType((bits&1)!=0,(bits&2)!=0,(bits&4)!=0);
                var measured=target with{State=EN_REVIEW_POINT_STATE.Ok,Judge="OK",ErrorX=.004,ErrorY=.006};
                var plan=new ST_REVIEW_PLAN("QA",1,1,1,flip,DateTimeOffset.UtcNow,[measured]){StageScanDirectionY=direction};
                var lines=new[]{new ST_FINE_REVIEW_LINE("SYNTHETIC-LINE",1,1,[measured],EN_FINE_REVIEW_LINE_STATE.Complete)};
                var request=new ST_FINE_REVIEW_REQUEST(plan,lines,EN_FINE_INSPECTION_METHOD.PtoP);
                var resultFile=Path.Combine(f.Root,"synthetic-auto-result.csv");await System.IO.File.WriteAllTextAsync(resultFile,"synthetic result identity only");
                var snapshot=new ST_FINE_REVIEW_SNAPSHOT("QA",lines,EN_REVIEW_SEQUENCE_STATE.Completed,"synthetic complete"){ResultFilePath=resultFile};
                var coordinator=new CAutoFineReviewCoordinator(f.Review,f.Offsets,_=>throw new NotSupportedException("No device prepare in save-boundary test"));
                typeof(CAutoFineReviewCoordinator).GetField("_prepared",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(coordinator,new ST_AUTO_FINE_PLAN(request,[target],parameters));
                typeof(CAutoFineReviewCoordinator).GetField("_result",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(coordinator,snapshot);
                await coordinator.Save(default);var saved=await f.Offsets.LoadLatest("QA");var row=saved.Rows.Single(r=>r.CellId==target.CellNo&&r.ShotId==shot);
                var manual=CManualInspectionCoordinateBridge.FromVision(.004,.006,flip,parameters,target.HeadNo);
                True(row.IsDesignCoordinate&&Close(row.ReviewOffsetX,CAlignShotOffsetData.RoundValue(.012+manual.XUm/1000))&&Close(row.ReviewOffsetY,CAlignShotOffsetData.RoundValue(-.021+manual.YUm/1000)));
                True(saved.Rows.Single(r=>r.CellId==99).ReviewOffsetX==.08);await coordinator.Save(default);True((await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
            }
        });
        await Check("final_offset_cas_rejects_new_revision_without_overwriting_new_map_or_recording_source",async()=>
        {
            var f=new Fixture();var basis=await f.Offsets.SaveNew("QA",[new(1,"A1",.001,.002)]);var source=Path.Combine(f.Root,"source.csv");await System.IO.File.WriteAllTextAsync(source,"synthetic");
            var next=await f.Offsets.SaveNew("QA",[new(1,"A1",.03,.04),new(2,"B1",.05,.06)]);
            await RejectCas(()=>f.Offsets.SaveNewIfLatestUnchanged("QA",basis,[new(1,"A1",.9,.9)]));
            await RejectCas(()=>f.Offsets.SaveNewFromSourceIfUnchanged("QA",basis,[new(1,"A1",.9,.9)],source,false));
            True((await f.Offsets.LoadLatest("QA")).FilePath==next.FilePath&&await f.Offsets.FindSourceApplication("QA",source) is null);
        });
        await Check("final_offset_cas_detects_same_path_same_timestamp_same_size_content_edit",async()=>
        {
            var f=new Fixture();var basis=await f.Offsets.SaveNew("QA",[new(1,"A1",.001,.002)]);var at=System.IO.File.GetLastWriteTimeUtc(basis.FilePath);var original=await System.IO.File.ReadAllTextAsync(basis.FilePath);
            var changed=original.Replace("0.001000","0.009000",StringComparison.Ordinal);True(changed!=original&&changed.Length==original.Length);await System.IO.File.WriteAllTextAsync(basis.FilePath,changed);System.IO.File.SetLastWriteTimeUtc(basis.FilePath,at);
            await RejectCas(()=>f.Offsets.SaveNewIfLatestUnchanged("QA",basis,[new(1,"A1",.9,.9)]));True(await System.IO.File.ReadAllTextAsync(basis.FilePath)==changed);
        });
        await Check("final_offset_cas_two_stores_one_basis_publish_exactly_one_source_snapshot",async()=>
        {
            var f=new Fixture();var basis=await f.Offsets.SaveNew("QA",[new(1,"A1",.001,.002)]);var other=new CReviewOffsetFile(Path.Combine(f.Root,"Config"));var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var sourcePaths=new[]{Path.Combine(f.Root,"source-a.csv"),Path.Combine(f.Root,"source-b.csv")};
            foreach(var path in sourcePaths)await System.IO.File.WriteAllTextAsync(path,"synthetic source");
            async Task<bool> Save(CReviewOffsetFile store,string path,double x){await start.Task;try{await store.SaveNewFromSourceIfUnchanged("QA",basis,[new(1,"A1",x,.002)],path,false);return true;}catch(InvalidOperationException){return false;}}
            var a=Save(f.Offsets,sourcePaths[0],.01);var b=Save(other,sourcePaths[1],.02);start.SetResult();var result=await Task.WhenAll(a,b);True(result.Count(x=>x)==1);
            var active=await f.Offsets.LoadLatest("QA");True(active.Rows.Single().ReviewOffsetX==(result[0] ? .01 : .02));True(await f.Offsets.FindSourceApplication("QA",sourcePaths[result[0]?1:0]) is null);
        });
        await Check("final_offset_cas_duplicate_source_policy_stays_explicit_with_current_basis",async()=>
        {
            var f=new Fixture();var source=Path.Combine(f.Root,"source.csv");await System.IO.File.WriteAllTextAsync(source,"synthetic");var basis=await f.Offsets.LoadLatest("QA");
            var saved=await f.Offsets.SaveNewFromSourceIfUnchanged("QA",basis,[new(1,"A1",.001,.002)],source,false);
            await RejectCas(()=>f.Offsets.SaveNewFromSourceIfUnchanged("QA",saved,[new(1,"A1",.003,.004)],source,false));True((await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
            var duplicate=await f.Offsets.SaveNewFromSourceIfUnchanged("QA",saved,[new(1,"A1",.003,.004)],source,true);True(duplicate.FilePath!=saved.FilePath&&duplicate.Rows.Single().ReviewOffsetX==.003);
        });
        await Check("final_offset_cas_unrelated_init_epoch_and_real_zero_remain_separate",async()=>
        {
            var f=new Fixture();var basis=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsMeasurementOffsetInitialized=true},new(2,"B1",.001,.002)]);var epoch=basis.Rows[0].MeasurementInitializedAt;
            var saved=await f.Offsets.SaveNewIfLatestUnchanged("QA",basis,basis.Rows.Select(r=>r.CellId==2?r with{ReviewOffsetX=0,ReviewOffsetY=0}:r).ToArray());
            True(saved.Rows.Single(r=>r.CellId==1).MeasurementInitializedAt==epoch&&saved.Rows.Single(r=>r.CellId==1).IsMeasurementOffsetInitialized&&!saved.Rows.Single(r=>r.CellId==2).IsMeasurementOffsetInitialized);
        });
        await Check("final_offset_cas_cancelled_or_foreign_basis_does_not_publish",async()=>
        {
            var f=new Fixture();var basis=await f.Offsets.SaveNew("QA",[new(1,"A1",.001,.002)]);using var cancel=new CancellationTokenSource();cancel.Cancel();
            await RejectCas(()=>f.Offsets.SaveNewIfLatestUnchanged("QA",basis,[new(1,"A1",.9,.9)],cancel.Token));await RejectCas(()=>f.Offsets.SaveNewIfLatestUnchanged("QA",basis with{RecipeId="OTHER"},[new(1,"A1",.9,.9)]));
            True((await f.Offsets.LoadLatest("QA")).FilePath==basis.FilePath);
        });
    }
    private static async Task RejectCas(Func<Task> action){var rejected=false;try{await action();}catch{rejected=true;}True(rejected);}
}
