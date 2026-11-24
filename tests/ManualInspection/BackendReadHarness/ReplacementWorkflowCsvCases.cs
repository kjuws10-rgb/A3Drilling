using System.IO;
using System.Globalization;
using System.Reflection;
using Drilling.Common.Station;
using Drilling.File.Script;
using System.Text.Json;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunReplacementWorkflowCsvChecks()
    {
        static Fixture TwoCells()=>new(recipeOverrides:new(){["MAX_CELL_NUMBER"]="2",["CELL2_ALIGN_TO_1ST_PIXEL_X"]="15",["CELL2_ALIGN_TO_1ST_PIXEL_Y"]="1"});
        static ST_MANUAL_POINT[] CellPoints(CManualInspectionSource s,int cell)=>s.Points.Where(p=>p.CellNo==cell&&!p.Masked).GroupBy(p=>p.ShotNo).Select(g=>g.First()).ToArray();
        static string Matrix(Fixture f,ST_MANUAL_POINT p)=>CReviewShotNameFormatter.ToMatrixName(p.ShotNo,f.Review.CreateAllPlan(f.Recipe,p.BeamNo).Points.Single(q=>q.CellNo==p.CellNo&&q.ShotNo==p.ShotNo).PixelCountX);
        static ST_REVIEW_OFFSET_ROW Row(Fixture f,ST_MANUAL_POINT p,double x,double y)=>new(p.CellNo,Matrix(f,p),x/1000,y/1000){IsDesignCoordinate=true};
        static ST_REVIEW_OFFSET_ROW Stored(Fixture f,ST_REVIEW_OFFSET_FILE_DATA file,ST_MANUAL_POINT p)=>file.Rows.Single(r=>r.CellId==p.CellNo&&r.ShotId==Matrix(f,p));
        static Task Send(CManualInspectionController c,EN_MANUAL_INSPECTION_UI_ACTION a,IReadOnlyList<string>? keys=null,int? cell=1,Func<CManualInspectionUiRequest,CManualInspectionUiRequest>? edit=null)
        {var r=new CManualInspectionUiRequest{ExpectedVersion=c.Snapshot.Version,Keys=keys??[],CurrentCellNo=cell};return c.ExecuteAsync(a,edit?.Invoke(r)??r);}
        static CManualInspectionUiPoint Ui(CManualInspectionController c,string key)=>c.Snapshot.Points.Single(p=>p.Key==key);
        static void SameObservation(CManualInspectionUiPoint a,CManualInspectionUiPoint b)
            =>True(a.WorkingJudge==b.WorkingJudge&&a.WorkingXUm==b.WorkingXUm&&a.WorkingYUm==b.WorkingYUm
                &&a.OriginalJudge==b.OriginalJudge&&a.OriginalXUm==b.OriginalXUm&&a.OriginalYUm==b.OriginalYUm
                &&a.BaseXUm==b.BaseXUm&&a.BaseYUm==b.BaseYUm&&a.IsMeasurementOffsetInitialized==b.IsMeasurementOffsetInitialized);
        static async Task Monitor(Fixture f,ST_MANUAL_POINT point,double? x,double? y)
        {
            await MonitorSta(async()=>
            {
                var monitor=ShotDetailMonitor(f);SetDisplay(monitor,"_coordinateSelectedCellNo",point.CellNo);
                var map=f.Recipe.Parameters.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
                foreach(var setting in f.SettingRows)map[setting.Key]=setting.Value;
                var shot=CShotCoordinatePlanBuilder.BuildDesignShots(map).Single(s=>s.CellNo==point.CellNo&&s.CellShotNo==point.ShotNo);
                SetDisplay(monitor,"_coordinateSelectedShotKey",shot.ShotKey);
                var data=await BuildMonitorConsistencyData(monitor);var row=data.CalculationRows.Single(r=>r.Key=="REVIEW");
                if(x is null||y is null)True(row.X=="—"&&row.Y=="—");else NearReview(row,x.Value/1000,y.Value/1000);
                var file=await f.Offsets.LoadLatest("QA");True(data.ReviewSavedInfoToolTip.Contains(file.ContentSha256));
            });
        }
        static async Task<ST_HEAD_SHOT_POINT> GeneratedScript(Fixture f,ST_MANUAL_POINT p,string phase)
        {
            var current=await f.Offsets.LoadLatest("QA");
            var parameters=f.Recipe.Parameters.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            foreach(var setting in f.SettingRows)parameters[setting.Key]=setting.Value;
            parameters["CELL77_Z99_REVIEW_OFFSET_X"]="999";parameters["CELL77_Z99_REVIEW_OFFSET_Y"]="999";
            CReviewOffsetData.ApplyToParameters(parameters,current);
            True(!parameters.ContainsKey("CELL77_Z99_REVIEW_OFFSET_X")&&!parameters.ContainsKey("CELL77_Z99_REVIEW_OFFSET_Y"));
            var build=typeof(CStationProcess).GetMethod("BuildProcessModel",BindingFlags.Static|BindingFlags.NonPublic)!;
            // Invoke only the actual pure model builder. No Station/Manager instance, upload or execution is created.
            var model=(ST_PROCESS_MODEL)build.Invoke(null,["CSV-TEXT-QA","QA","SYNTHETIC-"+phase,"SYNTHETIC",parameters])!;
            var point=model.HeadPlans.SelectMany(h=>h.Shots).Single(s=>s.CellNo==p.CellNo&&s.CellShotNo==p.ShotNo);
            var row=current.Rows.Single(r=>r.CellId==p.CellNo&&r.ShotId==Matrix(f,p));
            var review=CReviewOffsetData.ToScanner(parameters,point.HeadNo,row.ReviewOffsetX,row.ReviewOffsetY,row.IsDesignCoordinate);
            var scale=double.Parse(parameters.GetValueOrDefault("REVIEW_OFFSET_SCALE","100"),CultureInfo.InvariantCulture)/100;
            True(Math.Abs(point.ReviewOffsetX-review.X*scale)<1e-10&&Math.Abs(point.ReviewOffsetY-review.Y*scale)<1e-10);
            var writer=new CAutomation1ScriptFile(Path.Combine(f.Root,"GeneratedTextOnly",phase));
            foreach(var mode in new[]{"Standard","BufferedRun","ManualMOF/Standard","ManualMOF/Buffered"})
            {
                var output=writer.Build(model,mode);var directory=Path.GetDirectoryName(output.FilePath)!;
                var csv=System.IO.File.ReadAllLines(Path.Combine(directory,CAutomation1ScriptFile.ProcessShotCoordinateCsvFileName));
                var header=csv[0].TrimStart('\uFEFF').Split(',');var cols=header.Select((v,i)=>(v,i)).ToDictionary(t=>t.v,t=>t.i,StringComparer.Ordinal);
                var line=csv.Skip(1).Select(l=>l.Split(',')).Single(r=>r[cols["CellNo"]]==point.CellNo.ToString(CultureInfo.InvariantCulture)
                    &&r[cols["ShotRowNo"]]==point.GroupRow.ToString(CultureInfo.InvariantCulture)&&r[cols["ShotColNo"]]==point.GroupColumn.ToString(CultureInfo.InvariantCulture));
                double Number(string key)=>double.Parse(line[cols[key]],CultureInfo.InvariantCulture);
                True(Math.Abs(Number("ReviewOffsetX")-point.ReviewOffsetX)<.00000051&&Math.Abs(Number("ReviewOffsetY")-point.ReviewOffsetY)<.00000051
                    &&Math.Abs(Number("ScannerGx")-point.ScannerGx)<.00000051&&Math.Abs(Number("ScannerGy")-point.ScannerGy)<.00000051);
                var head=output.HeadScripts.Single(h=>h.HeadNo==point.HeadNo);var script=System.IO.File.ReadAllLines(head.FilePath);
                string Value(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
                True(script.Contains($"MoveRapid([GX,GY], [{Value(point.ScannerGx)},{Value(point.ScannerGy)}])"));
                True(output.TotalShots==model.HeadPlans.Sum(h=>h.Shots.Count)&&output.HeadScripts.All(h=>System.IO.File.Exists(h.FilePath)));
            }
            return point;
        }
        await Check("replacement_csv_copy_only_stages_original_delta_then_current_cell_bulk_adds_once_to_each_B",async()=>
        {
            var f=TwoCells();var s=await f.Backend.LoadAsync(null,default);var points=CellPoints(s,1);var reference=points[0];var target=points[1];var other=CellPoints(s,2)[0];
            var old=await f.Offsets.SaveNew("QA",[Row(f,reference,10,-2),Row(f,target,20,5),Row(f,other,37,-8)]);var bytes=System.IO.File.ReadAllBytes(old.FilePath);
            var writer=new CsvWriterSeam(f);var backend=new ReplacementCsvSeam(f,writer);backend.Responses[other.Key]=("OK",7,-2);
            var c=new CManualInspectionController(backend);await c.EnsureLoaded();
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[reference.Key]);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[other.Key]);var untouched=Ui(c,other.Key);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],edit:r=>r with{ReferenceKey=reference.Key,Reason="stage raw deviation only"});
            True(writer.SuccessfulSaves==0&&(await f.Offsets.LoadLatest("QA")).FilePath==old.FilePath&&Ui(c,target.Key).WorkingXUm==3&&Ui(c,target.Key).WorkingYUm==1);
            True(Ui(c,target.Key).OriginalXUm is null&&Ui(c,reference.Key).OriginalXUm==3);SameObservation(untouched,Ui(c,other.Key));
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var final=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==1&&Stored(f,final,reference).ReviewOffsetX==.013&&Stored(f,final,target).ReviewOffsetX==.023&&Stored(f,final,target).ReviewOffsetY==.006);
            True(Stored(f,final,other)==Stored(f,old,other)&&c.Snapshot.FinalOffsetPoints[target.Key]==new CManualInspectionUiOffset(23,6));
            SameObservation(untouched,Ui(c,other.Key));True(c.Snapshot.CellBulkEligibleCounts[2]==1&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
            await Monitor(f,target,23,6);await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(writer.SuccessfulSaves==1);
        });
        await Check("replacement_csv_operator_delta_cannot_bypass_total_limit_with_legacy_B500",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);var p=CellPoints(s,1)[0];
            var old=await f.Offsets.SaveNew("QA",[Row(f,p,500,-400)]);var writer=new CsvWriterSeam(f);var c=new CManualInspectionController(new ReplacementCsvSeam(f,writer));await c.EnsureLoaded();
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,[p.Key],edit:r=>r with{ForcedXUm=5,ForcedYUm=-4,Reason="explicit raw correction value"});
            True(Ui(c,p.Key).OriginalXUm is null&&Ui(c,p.Key).WorkingXUm==5&&writer.SuccessfulSaves==0);
            await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));var saved=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==0&&saved.FilePath==old.FilePath&&Stored(f,saved,p).ReviewOffsetX==.500&&Stored(f,saved,p).ReviewOffsetY==-.400);
            True(Ui(c,p.Key).BaseXUm==500&&Ui(c,p.Key).OriginalXUm is null&&Ui(c,p.Key).WorkingXUm==5&&c.Snapshot.FinalOffsetPoints[p.Key].XUm==500);await Monitor(f,p,500,-400);
        });
        await Check("replacement_csv_ng_missing_and_saved_B_only_do_not_become_fake_raw_or_new_corrections",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);var a=CellPoints(s,1);True(a.Length>=4);
            var old=await f.Offsets.SaveNew("QA",[Row(f,a[0],10,1),Row(f,a[1],9,2),Row(f,a[2],12,3)]);
            var writer=new CsvWriterSeam(f);var backend=new ReplacementCsvSeam(f,writer);backend.Responses[a[1].Key]=("NG",50,60);
            var c=new CManualInspectionController(backend);await c.EnsureLoaded();
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[a[0].Key]);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[a[1].Key]);
            True(Ui(c,a[2].Key).MeasurementXUm is null&&Ui(c,a[2].Key).OriginalXUm is null&&Ui(c,a[3].Key).MeasurementXUm is null);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var file=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==1&&Stored(f,file,a[0]).ReviewOffsetX==.013&&Stored(f,file,a[1])==Stored(f,old,a[1])&&Stored(f,file,a[2])==Stored(f,old,a[2]));
            True(!file.Rows.Any(r=>r.CellId==a[3].CellNo&&r.ShotId==Matrix(f,a[3]))&&Ui(c,a[1].Key).WorkingJudge=="NG"&&Ui(c,a[1].Key).OriginalXUm==50);
        });
        await Check("replacement_csv_import_stages_current_cell_only_and_bulk_preserves_other_cell_measured_zero_raw_ledger_and_hidden_rows",async()=>
        {
            var f=TwoCells();var s=await f.Backend.LoadAsync(null,default);var one=CellPoints(s,1);var two=CellPoints(s,2);
            var hiddenInside=new ST_REVIEW_OFFSET_ROW(1,"Z99",.123456,-.234567);var hiddenOutside=new ST_REVIEW_OFFSET_ROW(99,"Z99",.345678,-.456789);
            var old=await f.Offsets.SaveNew("QA",[Row(f,one[0],10,-2),Row(f,one[1],15,4),Row(f,two[0],22,5),Row(f,two[1],0,0),hiddenInside,hiddenOutside]);
            var portable=await f.Offsets.SaveNew("PORTABLE",[Row(f,one[0],7,-3),Row(f,two[0],99,33),Row(f,two[1],0,0) with{IsMeasurementOffsetInitialized=true},hiddenInside,hiddenOutside]);
            var path=Path.Combine(f.Root,"user-import.csv");System.IO.File.Copy(portable.FilePath,path);var input=System.IO.File.ReadAllBytes(path);
            var writer=new CsvWriterSeam(f);var backend=new ReplacementCsvSeam(f,writer);backend.Responses[two[0].Key]=("OK",7,-2);backend.Responses[two[1].Key]=("NG",99,88);
            var c=new CManualInspectionController(backend);await c.EnsureLoaded();
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[one[0].Key]);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[two[0].Key]);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[two[1].Key]);
            var outside=Ui(c,two[0].Key);var zeroOutside=Ui(c,two[1].Key);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle,edit:r=>r with{ResultPath=path});
            True(writer.SuccessfulSaves==0&&Ui(c,one[0].Key).WorkingXUm==7&&Ui(c,one[0].Key).Source=="OFFSET_FILE"&&Ui(c,one[0].Key).OriginalXUm==3);
            SameObservation(outside,Ui(c,two[0].Key));SameObservation(zeroOutside,Ui(c,two[1].Key));
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var file=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==1&&Stored(f,file,one[0]).ReviewOffsetX==.007&&Stored(f,file,one[1]).IsMeasurementOffsetInitialized);
            True(Stored(f,file,two[0])==Stored(f,old,two[0])&&Stored(f,file,two[1])==Stored(f,old,two[1])&&!Stored(f,file,two[1]).IsMeasurementOffsetInitialized);
            True(file.Rows.Single(r=>r.CellId==1&&r.ShotId=="Z99")==old.Rows.Single(r=>r.CellId==1&&r.ShotId=="Z99")&&file.Rows.Single(r=>r.CellId==99)==old.Rows.Single(r=>r.CellId==99));
            SameObservation(outside,Ui(c,two[0].Key));SameObservation(zeroOutside,Ui(c,two[1].Key));True(!c.Snapshot.IsOffsetEditPending&&c.Snapshot.CellBulkEligibleCounts[2]==1&&System.IO.File.ReadAllBytes(path).SequenceEqual(input));
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,cell:2);var next=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==2&&Stored(f,next,two[0]).ReviewOffsetX==.029&&Stored(f,next,two[1])==Stored(f,old,two[1])&&Stored(f,next,one[0]).ReviewOffsetX==.007);
            await Monitor(f,one[0],7,-3);
        });
        await Check("replacement_csv_current_cell_init_immediately_saves_without_resetting_other_cell_measured_zero_or_hidden_history",async()=>
        {
            var f=TwoCells();var s=await f.Backend.LoadAsync(null,default);var one=CellPoints(s,1);var two=CellPoints(s,2);
            var old=await f.Offsets.SaveNew("QA",[Row(f,one[0],500,-400),Row(f,two[0],222,5),Row(f,two[1],0,0),new(1,"Z99",.123,-.234),new(99,"Z99",.345,-.456)]);
            var writer=new CsvWriterSeam(f);var backend=new ReplacementCsvSeam(f,writer);backend.Responses[two[0].Key]=("OK",7,-2);backend.Responses[two[1].Key]=("NG",99,88);
            var c=new CManualInspectionController(backend);await c.EnsureLoaded();await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[two[0].Key]);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[two[1].Key]);
            var outside=Ui(c,two[0].Key);var zero=Ui(c,two[1].Key);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,edit:r=>r with{OffsetInitScope="CELL",OffsetInitCellNo=1});
            True(writer.SuccessfulSaves==1&&!c.Snapshot.IsOffsetEditPending);SameObservation(outside,Ui(c,two[0].Key));SameObservation(zero,Ui(c,two[1].Key));
            var file=await f.Offsets.LoadLatest("QA");
            True(writer.SuccessfulSaves==1&&file.Rows.Where(r=>r.CellId==1).All(r=>r.ReviewOffsetX==0&&r.ReviewOffsetY==0&&r.IsMeasurementOffsetInitialized));
            True(Stored(f,file,two[0])==Stored(f,old,two[0])&&Stored(f,file,two[1])==Stored(f,old,two[1])&&!Stored(f,file,two[1]).IsMeasurementOffsetInitialized&&file.Rows.Single(r=>r.CellId==99)==old.Rows.Single(r=>r.CellId==99));
            SameObservation(outside,Ui(c,two[0].Key));SameObservation(zero,Ui(c,two[1].Key));True(c.Snapshot.CellBulkEligibleCounts[2]==0&&Ui(c,one[0].Key).MeasurementXUm is null);
            await Monitor(f,one[0],null,null);await Monitor(f,two[1],0,0);
        });
        foreach(var operatorSource in new[]{false,true})
        {
            await Check("replacement_csv_legacy_generic_"+(operatorSource?"operator":"measured")+"_session_adds_captured_error_to_target_B_not_source_final_or_other_cell_candidate",async()=>
            {
                var f=TwoCells();var initial=await f.Backend.LoadAsync(null,default);var one=CellPoints(initial,1);var other=CellPoints(initial,2)[0];
                await f.Offsets.SaveNew("QA",[Row(f,one[0],10,-2),Row(f,one[1],20,5),Row(f,other,37,-8)]);var source=await f.Backend.LoadAsync(null,default);
                var legacy=new CManualInspectionWorkspace();legacy.Load(source.Context,source.Points,source.Values,source.Limits,source.FieldEvaluator,source.StoredInitializedOffsetKeys);
                if(operatorSource)legacy.RecordOperatorResults([one[0].Key],3,1,"generic legacy input");
                else legacy.RecordMeasurement(one[0].Key,new ST_MANUAL_MEASUREMENT("OK",3,1,"LEGACY-RAW",source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature));
                var preview=legacy.PreviewReferenceCopy(one[0].Key,[one[1].Key,other.Key]);legacy.ConfirmReferenceCopy(preview,[one[1].Key,other.Key],"legacy generic final 13");
                True(legacy.GetPoint(one[1].Key).ProxySource!.FinalOffset.XUm==13);legacy.ValidateCandidate(true);
                var bundle=new CManualInspectionController.SessionBundle(1,false,"recover",legacy.CaptureSession(),true,"MAX_AXIS",[5,10,20],["정상","관찰","주의","점검"]);
                var path=Path.Combine(f.Backend.StateDirectory,"Sessions",CManualInspectionBackend.Digest(source.Context.RecipeId+"|"+source.Context.RunId+"|"+source.Context.CoordinateSignature+"|"+source.Context.RevisionId)+".json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);await System.IO.File.WriteAllTextAsync(path,JsonSerializer.Serialize(bundle));
                var writer=new CsvWriterSeam(f);var c=new CManualInspectionController(new ReplacementCsvSeam(f,writer));await c.EnsureLoaded();
                True(Ui(c,one[1].Key).WorkingXUm==3&&writer.SuccessfulSaves==0);
                await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var saved=await f.Offsets.LoadLatest("QA");
                True(writer.SuccessfulSaves==1&&Stored(f,saved,one[0]).ReviewOffsetX==.013&&Stored(f,saved,one[1]).ReviewOffsetX==.023&&Stored(f,saved,other).ReviewOffsetX==.037);
                True(Ui(c,one[1].Key).OriginalXUm is null);await Monitor(f,one[1],23,6);
            });
        }
        await Check("replacement_csv_saved_bulk_init_undo_redo_feed_actual_station_model_and_all_four_A1_text_modes_without_execution",async()=>
        {
            var f=new Fixture(recipeOverrides:new(){["MAX_CELL_NUMBER"]="2",["CELL2_ALIGN_TO_1ST_PIXEL_X"]="15",["CELL2_ALIGN_TO_1ST_PIXEL_Y"]="1"},
                settingOverrides:new(){["REVIEW_OFFSET_SCALE"]="100",["AUTOMATION1_SIMULATOR_USE"]="OFF"});
            var initial=await f.Backend.LoadAsync(null,default);var one=CellPoints(initial,1);var other=CellPoints(initial,2)[0];
            var old=await f.Offsets.SaveNew("QA",[Row(f,one[0],10,-2),Row(f,one[1],20,5),Row(f,other,37,-8)]);var original=System.IO.File.ReadAllBytes(old.FilePath);
            var writer=new CsvWriterSeam(f);var c=new CManualInspectionController(new ReplacementCsvSeam(f,writer));await c.EnsureLoaded();
            var before=await GeneratedScript(f,one[1],"before");await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[one[0].Key]);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[one[1].Key],edit:r=>r with{ReferenceKey=one[0].Key,Reason="working stage only; no A1 execution"});
            var staged=await GeneratedScript(f,one[1],"copy-stage");True(staged.ScannerGx==before.ScannerGx&&staged.ScannerGy==before.ScannerGy&&writer.SuccessfulSaves==0);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var after=await GeneratedScript(f,one[1],"bulk-saved");
            True(Math.Abs(after.ScannerGx-before.ScannerGx-(after.ReviewOffsetX-before.ReviewOffsetX))<1e-10
                &&Math.Abs(after.ScannerGy-before.ScannerGy-(after.ReviewOffsetY-before.ReviewOffsetY))<1e-10
                &&(Math.Abs(after.ScannerGx-before.ScannerGx)>.0001||Math.Abs(after.ScannerGy-before.ScannerGy)>.0001));
            var saved=await f.Offsets.LoadLatest("QA");True(Stored(f,saved,one[1]).ReviewOffsetX==.023&&Stored(f,saved,other).ReviewOffsetX==.037);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);var init=await GeneratedScript(f,one[1],"init-immediately-saved");True(init.ReviewOffsetX==0&&init.ReviewOffsetY==0&&!c.Snapshot.IsOffsetEditPending&&writer.SuccessfulSaves==2);
            await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(writer.SuccessfulSaves==2);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);var undo=await GeneratedScript(f,one[1],"undo-saved");True(undo.ScannerGx==after.ScannerGx&&undo.ScannerGy==after.ScannerGy);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);var redo=await GeneratedScript(f,one[1],"redo-saved");True(redo.ScannerGx==init.ScannerGx&&redo.ScannerGy==init.ScannerGy);
            var final=await f.Offsets.LoadLatest("QA");True(Stored(f,final,other)==Stored(f,old,other)&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(original)&&writer.SuccessfulSaves==4);
        });
        await Check("increment_csv_restart_reads_both_target_raw_and_copy_driving_receipts_and_rejects_same_copy",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);var points=CellPoints(source,1);
            var reference=points[0];var target=points[1];
            var old=await f.Offsets.SaveNew("QA",[Row(f,reference,10,-2),Row(f,target,20,5)]);var oldBytes=System.IO.File.ReadAllBytes(old.FilePath);
            var writer=new CsvWriterSeam(f);var c=new CManualInspectionController(new ReplacementCsvSeam(f,writer));await c.EnsureLoaded();
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[reference.Key]);await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[target.Key]);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],edit:r=>r with{ReferenceKey=reference.Key,Reason="capture residual, retain target original"});
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);var saved=await f.Offsets.LoadLatest("QA");
            True(Stored(f,saved,reference).ReviewOffsetX==.013&&Stored(f,saved,target).ReviewOffsetX==.023&&writer.SuccessfulSaves==1);
            var driving=writer.LastDrivingRows.ToDictionary(r=>r.Key,r=>r.MeasurementToken);
            True(writer.LastCompanionTokens.TryGetValue(target.Key,out var rawToken)&&driving[target.Key]!=rawToken);
            var receipts=writer.LastCompanionTokens.Concat(driving).Distinct().ToArray();
            True(receipts.Count(p=>p.Key==target.Key)==2&&await f.Offsets.FindCommittedSourceApplication("QA",writer.LastReceiptPath) is not null);
            // Seed exact native reservation files after the real writer commit. Native SaveCore and its equipment guard are not executed.
            var context=writer.LastSource!.Context;var directory=Path.Combine(f.Backend.StateDirectory,"ApplySources",CManualInspectionBackend.Digest(context.RunId));Directory.CreateDirectory(directory);
            foreach(var pair in receipts)await System.IO.File.WriteAllTextAsync(Path.Combine(directory,"point-"+CManualInspectionBackend.Digest(context.RunId+pair.Key+pair.Value)+".json"),JsonSerializer.Serialize(new{SourcePath=writer.LastReceiptPath,BaseRevision=source.Context.RevisionId,RunId=context.RunId,PointKey=pair.Key,MeasurementToken=pair.Value}));
            await f.Backend.LoadAsync(null,default);True(receipts.All(pair=>f.Backend.ConsumedMeasurements.Contains(pair)));
            var nextWriter=new CsvWriterSeam(f);var reopened=new CManualInspectionController(new ReplacementCsvSeam(f,nextWriter));await reopened.EnsureLoaded();
            await Reject(()=>Send(reopened,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target.Key],edit:r=>r with{ReferenceKey=reference.Key,Reason="same source residual after restart"}));
            await Reject(()=>Send(reopened,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(nextWriter.SuccessfulSaves==0&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(oldBytes)&&(await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
        });
        await Check("replacement_csv_ng_reference_copy_is_working_state_only_and_never_writes_correction",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);var a=CellPoints(source,1);
            var old=await f.Offsets.SaveNew("QA",[Row(f,a[0],10,1),Row(f,a[1],20,2)]);var writer=new CsvWriterSeam(f);var backend=new ReplacementCsvSeam(f,writer);backend.Responses[a[0].Key]=("NG",8,6);
            var c=new CManualInspectionController(backend);await c.EnsureLoaded();await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,[a[0].Key]);
            await Send(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[a[1].Key],edit:r=>r with{ReferenceKey=a[0].Key,Reason="NG state follows without file write"});
            True(Ui(c,a[1].Key).WorkingJudge=="NG"&&Ui(c,a[1].Key).OriginalXUm is null&&Ui(c,a[0].Key).OriginalXUm==8&&writer.SuccessfulSaves==0);
            await Reject(()=>Send(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(writer.SuccessfulSaves==0&&(await f.Offsets.LoadLatest("QA")).FilePath==old.FilePath);
        });
    }

    private sealed class ReplacementCsvSeam(Fixture f,CsvWriterSeam inner):IManualInspectionBackend
    {
        public readonly Dictionary<string,(string Judge,double X,double Y)> Responses=new(StringComparer.Ordinal);
        public bool IsSimulation=>false;public string StateDirectory=>inner.StateDirectory;
        public IReadOnlyDictionary<string,ST_MANUAL_OFFSET> LastSavedValues=>inner.LastSavedValues;
        public IReadOnlyList<KeyValuePair<string,string>> ConsumedMeasurements=>inner.ConsumedMeasurements;
        public IReadOnlySet<string> ImportedInitializedOffsetKeys=>f.Backend.ImportedInitializedOffsetKeys;
        public string ImportedOffsetDescription=>f.Backend.ImportedOffsetDescription;
        public Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? keys=null)=>inner.LoadAsync(path,token,keys);
        public Task EnsureCurrentAsync(CManualInspectionSource s,CancellationToken token)=>inner.EnsureCurrentAsync(s,token);
        public Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource s,CancellationToken token)=>inner.ReadSavedOffsetSnapshotAsync(s,token);
        public Task<IReadOnlyDictionary<string,ST_MANUAL_OFFSET>> ImportOffsetBundleAsync(CManualInspectionSource s,string path,CancellationToken token)=>f.Backend.ImportOffsetBundleAsync(s,path,token);
        public void GuardOperation()=>inner.GuardOperation();public void Stop(){}
        public Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource s,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)
        {token.ThrowIfCancellationRequested();var p=s.Points.Single(p=>p.Key==key);var r=Responses.GetValueOrDefault(key,("OK",3d,1d));var raw=new ST_MANUAL_MEASUREMENT(r.Item1,r.Item2,r.Item3,"REPLACEMENT-RAW-"+key,s.Context.RecipeId,s.Context.RunId,s.Context.CoordinateSignature){RawData="Synthetic raw deviation; never device data"};return Task.FromResult(new CManualInspectionMeasurement(key,raw,p.XMm,p.YMm));}
        public Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)=>throw new NotSupportedException("No equipment in this CSV seam");
        public Task<string> SaveAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null)=>inner.SaveAsync(s,values,identity,rows,token,consumed);
    }
}
