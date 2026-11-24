using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.Common.Station;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunCalibrationBasisNativeChecks()
    {
        static Dictionary<string,string> Parameters(Fixture f)
        {var map=f.Recipe.Parameters.ToDictionary(v=>v.Key,v=>v.Value,StringComparer.OrdinalIgnoreCase);foreach(var v in f.SettingRows)map[v.Key]=v.Value;return map;}
        static ST_PROCESS_MODEL Model(Dictionary<string,string> p)=> (ST_PROCESS_MODEL)typeof(CStationProcess)
            .GetMethod("BuildProcessModel",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,["PROCESS-BASIS-QA","QA","GLASS-QA","SYNTHETIC",p])!;
        static ST_REVIEW_PROCESSING_BASIS Basis(Dictionary<string,string> p,string? signature=null)
            =>CReviewProcessingBasis.Capture(Model(p),signature??CReviewProcessingBasis.CoordinateSignature(p),"synthetic.csv",new string('A',64),DateTimeOffset.UtcNow,false,"SYNTHETIC_TEXT_ONLY");
        static string Temporary()=>Path.Combine(Path.GetTempPath(),"A3CalibrationBasisQA",Guid.NewGuid().ToString("N"),"LastBasis.json");
        static ST_MANUAL_POINT Point(ST_REVIEW_PROCESSING_OFFSET p)=>new($"C{p.CellNo}/S{p.ShotNo}/B11",p.CellNo,p.HeadNo,p.ShotNo,11,1,1){Row=0,Column=0,Masked=false};
        static void Set(object target,string field,object value)=>typeof(CStationProcess).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
        await Check("native_basis_signature_is_exact_legacy_algorithm_and_detects_shift_scale_static_changes_without_serializing_settings",()=>
        {
            var p=Parameters(new());p["PASSWORD"]="not-exported";p["AUTH_TOKEN"]="not-exported";p["CURRENT_PROCESS_COUNT"]="7";p["CELL1_A1_REVIEW_OFFSET_X"]=".123";p["SHIFT_OFFSET_X"]="5";
            var legacy=typeof(CManualInspectionBackend).GetMethod("Signature",BindingFlags.Static|BindingFlags.NonPublic)!;
            var oldAlgorithm=CManualInspectionBackend.Digest(string.Join("\n",p.Where(v=>!CReviewOffsetData.IsParameterKey(v.Key)&&v.Key is not ("CURRENT_PROCESS_COUNT" or "SHIFT_OFFSET_X" or "SHIFT_OFFSET_Y")
                &&!v.Key.Contains("PASSWORD",StringComparison.OrdinalIgnoreCase)&&!v.Key.Contains("SECRET",StringComparison.OrdinalIgnoreCase)&&!v.Key.Contains("TOKEN",StringComparison.OrdinalIgnoreCase))
                .OrderBy(v=>v.Key,StringComparer.OrdinalIgnoreCase).Select(v=>v.Key+"="+v.Value)));
            True((string)legacy.Invoke(null,[p])! == CReviewProcessingBasis.StaticSignature(p)&&oldAlgorithm==CReviewProcessingBasis.StaticSignature(p));var sig=CReviewProcessingBasis.CoordinateSignature(p);
            var secret=p.ToDictionary(k=>k.Key,k=>k.Value);secret["PASSWORD"]="other";secret["AUTH_TOKEN"]="other";secret["CURRENT_PROCESS_COUNT"]="99";secret["CELL1_A1_REVIEW_OFFSET_X"]=".999";
            True(CReviewProcessingBasis.CoordinateSignature(secret)==sig);secret["SHIFT_OFFSET_X"]="6";True(CReviewProcessingBasis.CoordinateSignature(secret)!=sig);
            secret=p.ToDictionary(k=>k.Key,k=>k.Value);secret["REVIEW_OFFSET_SCALE"]="50";True(CReviewProcessingBasis.CoordinateSignature(secret)!=sig);
            secret=p.ToDictionary(k=>k.Key,k=>k.Value);secret["PIXEL_SIZE"]=".2";True(CReviewProcessingBasis.CoordinateSignature(secret)!=sig);
            return Task.CompletedTask;
        });
        await Check("native_basis_backend_and_root_share_head_count_default_without_overwriting_explicit_count",async()=>
        {
            foreach(var explicitCount in new string?[]{null,"4"})
            {
                var f=new Fixture(settingOverrides:explicitCount is null?null:new(){["HEAD_COUNT"]=explicitCount});var source=await f.Backend.LoadAsync(null,default);
                var parameters=(Dictionary<string,string>)typeof(CManualInspectionBackend).GetField("_parameters",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Backend)!;
                True(parameters["HEAD_COUNT"]==(explicitCount??"8")&&source.Context.CoordinateSignature==CReviewProcessingBasis.CoordinateSignature(parameters));
                var factory=typeof(Drilling.UI.CRootView).GetMethod("CreateProcessParameters",BindingFlags.NonPublic|BindingFlags.Static)!;
                var request=(IReadOnlyDictionary<string,string>)factory.Invoke(null,[f.Recipe,"GLASS-QA","LOT-QA",0d,0d])!;
                var native=request.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);foreach(var row in f.SettingRows)native[row.Key]=row.Value;
                True(native["DIAMETER"]=="0.350"&&CReviewProcessingBasis.CoordinateSignature(native)==source.Context.CoordinateSignature);
                var otherIdentity=new Dictionary<string,string>(native,StringComparer.OrdinalIgnoreCase){["GlassId"]="OTHER",["LotId"]="OTHER"};
                True(CReviewProcessingBasis.CoordinateSignature(otherIdentity)==source.Context.CoordinateSignature);
                var completed=CReviewProcessingBasis.Complete(Basis(native),DateTimeOffset.UtcNow);
                var map=CManualInspectionBackend.CreateCalibrationBasisMap(completed,source.Context,source.Points,"GLASS-QA",null,false);True(map.Count>0);
            }
        });
        await Check("native_basis_captures_exact_actual_contribution_and_distinguishes_explicitly_quantized_frozen_plan",()=>
        {
            foreach(var scale in new[]{100,50})
            {
                var p=Parameters(new());p["REVIEW_OFFSET_SCALE"]=scale.ToString();p["CELL1_A1_REVIEW_OFFSET_X"]=".0014";p["CELL1_A1_REVIEW_OFFSET_Y"]="-.0014";p["CELL1_A1_REVIEW_OFFSET_SPACE"]="DESIGN";
                var b=Basis(p);var at=b.Offsets.Single(v=>v.CellNo==1&&v.ShotNo==1);
                // Current Review planner reads full CSV precision; only Align uses RoundValue(3).
                True(Math.Abs(at.BaseOffsetUm.XUm-1.4)<1e-7&&Math.Abs(at.BaseOffsetUm.YUm+1.4)<1e-7&&Math.Abs(at.AppliedDesignUm.XUm-1.4*scale/100d)<1e-7);
                True(b.ReviewOffsetScale==scale/100d);
                // Independently prove capture uses consumed frozen contribution, not the CSV:
                // this synthetic frozen command deliberately consumes 1um despite its 1.4um source.
                var model=Model(p);var head=model.HeadPlans.Single(h=>h.Shots.Any(v=>v.CellNo==1&&v.CellShotNo==1));var shot=head.Shots.Single(v=>v.CellNo==1&&v.CellShotNo==1);
                var command=CScannerAxisTransform.DesignOffsetToScanner(p,head.HeadNo,.001*scale/100d,-.001*scale/100d);
                var rounded=shot with{ReviewOffsetX=command.X,ReviewOffsetY=command.Y,ScannerGx=shot.ScannerGx-shot.ReviewOffsetX+command.X,ScannerGy=shot.ScannerGy-shot.ReviewOffsetY+command.Y,
                    FinalScannerOffsetX=shot.FinalScannerOffsetX-shot.ReviewOffsetX+command.X,FinalScannerOffsetY=shot.FinalScannerOffsetY-shot.ReviewOffsetY+command.Y};
                model=model with{HeadPlans=model.HeadPlans.Select(h=>h.HeadNo==head.HeadNo?h with{Shots=h.Shots.Select(v=>v.CellNo==1&&v.CellShotNo==1?rounded:v).ToArray()}:h).ToArray()};
                var consumed=CReviewProcessingBasis.Capture(model,b.CoordinateSignature,b.ReviewCsvPath,b.ReviewCsvSha256,b.ReviewCsvSavedAt,false,"SYNTHETIC_QUANTIZED_COMMAND_ONLY").Offsets.Single(v=>v.CellNo==1&&v.ShotNo==1);
                True(Math.Abs(consumed.BaseOffsetUm.XUm-1)<1e-7&&Math.Abs(consumed.BaseOffsetUm.YUm+1)<1e-7);
            }
            return Task.CompletedTask;
        });
        await Check("native_basis_initial_absence_is_explicit_consumed_zero_and_scale_zero_is_not_invented_positive_scale",()=>
        {
            var p=Parameters(new());var b=CReviewProcessingBasis.Capture(Model(p),CReviewProcessingBasis.CoordinateSignature(p),"",CReviewProcessingBasis.Hash("NO_REVIEW_OFFSET_CSV"),default,false,"TEST");
            True(b.Offsets.All(v=>v.BaseOffsetUm==ST_MANUAL_OFFSET.Zero)&&b.ReviewCsvSha256.Length==64);
            p["REVIEW_OFFSET_SCALE"]="0";p["CELL1_A1_REVIEW_OFFSET_X"]=".010";var zero=Basis(p);True(zero.ReviewOffsetScale==0&&zero.Offsets.All(v=>v.AppliedDesignUm==ST_MANUAL_OFFSET.Zero));
            return Task.CompletedTask;
        });
        await Check("native_basis_pending_completed_restart_and_new_pending_never_falls_back_to_old_completed_record",()=>
        {
            var b=Basis(Parameters(new()));var store=new CReviewProcessingBasisStore(Temporary());True(store.ReadCompleted() is null);
            store.Pending(b);True(new CReviewProcessingBasisStore(store.FilePath).ReadCompleted() is null);
            var completed=store.Completed(b,DateTimeOffset.UtcNow);var restored=new CReviewProcessingBasisStore(store.FilePath).ReadCompleted()!;
            True(restored.BasisId==completed.BasisId&&restored.ProofSha256==completed.ProofSha256&&restored.Offsets.SequenceEqual(completed.Offsets));
            store.Pending(Basis(Parameters(new())));True(store.ReadCompleted() is null&&store.ReadIdentity().StartsWith("PENDING|"));
            var text=System.IO.File.ReadAllText(store.FilePath);True(!text.Contains("PASSWORD")&&!text.Contains("SCANNER_MARK_SPEED"));return Task.CompletedTask;
        });
        await Check("native_basis_completed_publication_failure_retains_pending_and_cleans_temporary_file",async()=>
        {
            var b=Basis(Parameters(new()));var store=new CReviewProcessingBasisStore(Temporary());store.Pending(b);var old=System.IO.File.ReadAllBytes(store.FilePath);
            using(var locked=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var refused=false;try{store.Completed(b,DateTimeOffset.UtcNow);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){refused=true;}
                True(refused);
            }
            True(store.ReadCompleted() is null&&System.IO.File.ReadAllBytes(store.FilePath).SequenceEqual(old)&&!Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!,"*.tmp").Any());await Task.CompletedTask;
        });
        await Check("native_basis_tampering_and_wrong_frame_glass_head_or_simulation_cannot_authorize_current_measurements",()=>
        {
            var p=Parameters(new());var b=CReviewProcessingBasis.Complete(Basis(p),DateTimeOffset.UtcNow);var point=Point(b.Offsets.First());var context=new ST_MANUAL_CONTEXT("QA","RUN","CURRENT_OTHER_B",b.CoordinateSignature);
            var map=CManualInspectionBackend.CreateCalibrationBasisMap(b,context,[point],"GLASS-QA",b.CompletedAt.AddSeconds(-1),false);
            True(map.Count==1&&map[point.OffsetKey].BaseOffsetUm==b.Offsets.First().BaseOffsetUm&&map[point.OffsetKey].RunId=="RUN");
            True(CManualInspectionBackend.CreateCalibrationBasisMap(b,context,[point],"OTHER",null,false).Count==0);
            True(CManualInspectionBackend.CreateCalibrationBasisMap(b,context with{CoordinateSignature="OTHER"},[point],"GLASS-QA",null,false).Count==0);
            True(CManualInspectionBackend.CreateCalibrationBasisMap(b,context,[point with{HeadNo=point.HeadNo==1?2:1}],"GLASS-QA",null,false).Count==0);
            True(CManualInspectionBackend.CreateCalibrationBasisMap(b,context,[point],"GLASS-QA",null,true).Count==0);
            True(CManualInspectionBackend.CreateCalibrationBasisMap(b with{ReviewOffsetScale=.5},context,[point],"GLASS-QA",null,false).Count==0);
            True(CManualInspectionBackend.CreateCalibrationBasisMap(null,context,[point],"GLASS-QA",null,false).Count==0);return Task.CompletedTask;
        });
        await Check("native_measurement_mode_binding_preserves_raw_but_rejects_mixed_or_unknown_stage_vision_basis",()=>
        {
            var parameters=Parameters(new());var completed=CReviewProcessingBasis.Complete(Basis(parameters),DateTimeOffset.UtcNow);var point=Point(completed.Offsets.First());
            var context=new ST_MANUAL_CONTEXT("QA","RUN","CURRENT",completed.CoordinateSignature);var map=CManualInspectionBackend.CreateCalibrationBasisMap(completed,context,[point],"GLASS-QA",null,false);
            var source=new CManualInspectionSource(context,[point],new Dictionary<string,ST_MANUAL_OFFSET>{{point.Key,new(99,88)}},new(30,200,110),(_,_,_)=>new(true,""),5,5,"","synthetic"){CalibrationBasisByOffsetKey=map};
            var original=new ST_MANUAL_MEASUREMENT("NG",3,4,"ACTUAL-MODE-RAW","QA","RUN",completed.CoordinateSignature){RawData="SYNTHETIC immutable raw"};
            foreach(var stage in new bool?[]{false,true,null})foreach(var vision in new bool?[]{false,true,null})
            {
                var bound=CManualInspectionBackend.BindCalibrationMeasurement(original,source,point.OffsetKey,false,stage,vision);var valid=stage==false&&vision==false;
                True((bound.CalibrationBasis is not null)==valid&&string.IsNullOrEmpty(bound.CalibrationBasisUnavailableReason)==valid
                    &&bound.IsStageSimulation==stage&&bound.IsVisionSimulation==vision
                    &&bound.Judge==original.Judge&&bound.ErrorXUm==original.ErrorXUm&&bound.ErrorYUm==original.ErrorYUm&&bound.RawToken==original.RawToken&&bound.RawData==original.RawData);
            }
            return Task.CompletedTask;
        });
        await Check("native_basis_manual_head_scope_keeps_original_signature_but_only_actual_selected_shots_get_authority",()=>
        {
            var p=Parameters(new());var sig=CReviewProcessingBasis.CoordinateSignature(p);var model=Model(p);var head=model.HeadPlans.First(h=>h.Shots.Count>0);
            var scoped=model with{HeadPlans=[head with{Shots=head.Shots.Take(1).ToArray()}]};var b=CReviewProcessingBasis.Complete(CReviewProcessingBasis.Capture(scoped,sig,"",CReviewProcessingBasis.Hash("NO_REVIEW_OFFSET_CSV"),default,false,"MANUAL_MOF"),DateTimeOffset.UtcNow);
            var selected=Point(b.Offsets.Single());var unprocessed=selected with{Key="C1/S999/B11",ShotNo=999};var map=CManualInspectionBackend.CreateCalibrationBasisMap(b,new("QA","RUN","B",sig),[selected,unprocessed],"GLASS-QA",null,false);
            True(map.Count==1&&map.ContainsKey(selected.OffsetKey)&&!map.ContainsKey(unprocessed.OffsetKey)&&b.CoordinateSignature==sig);return Task.CompletedTask;
        });
        await Check("native_station_prepared_identity_rejects_same_path_same_saved_time_changed_sha_without_constructing_station",async()=>
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"A1",.001,.002){IsDesignCoordinate=true}]);var first=await f.Offsets.LoadLatest("QA");
            var station=(CStationProcess)RuntimeHelpers.GetUninitializedObject(typeof(CStationProcess));Set(station,"_sequenceLock",new object());Set(station,"_reviewOffsetFile",f.Offsets);
            Set(station,"_preparedReviewOffsetFilePath",first.FilePath);Set(station,"_preparedReviewOffsetSavedAt",first.SavedAt);Set(station,"_preparedReviewOffsetContentSha256",first.ContentSha256);
            var method=typeof(CStationProcess).GetMethod("EnsurePreparedReviewOffsetIsCurrent",BindingFlags.NonPublic|BindingFlags.Instance)!;var model=Model(Parameters(f));method.Invoke(station,[model,default(CancellationToken)]);
            await System.IO.File.AppendAllTextAsync(first.FilePath,"\n");var changed=await f.Offsets.LoadLatest("QA");True(changed.FilePath==first.FilePath&&changed.SavedAt==first.SavedAt&&changed.ContentSha256!=first.ContentSha256);
            var blocked=false;try{method.Invoke(station,[model,default(CancellationToken)]);}catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){blocked=true;}True(blocked);
        });
        await Check("native_basis_mode_ignores_unused_simulated_devices_and_heads",()=>
        {
            True(!CReviewProcessingBasis.IsSimulationOrUnknown(new[]{1},new Dictionary<int,bool?>{{1,false},{2,true}},new[]{7},new Dictionary<int,bool?>{{7,false},{8,true}}));return Task.CompletedTask;
        });
        await Check("native_basis_mode_marks_actual_simulated_head_only_when_used",()=>
        {
            var heads=new Dictionary<int,bool?>{{1,false},{2,true}};var stage=new Dictionary<int,bool?>{{7,false}};
            True(CReviewProcessingBasis.IsSimulationOrUnknown(new[]{2},heads,new[]{7},stage)&&!CReviewProcessingBasis.IsSimulationOrUnknown(new[]{1},heads,new[]{7},stage));return Task.CompletedTask;
        });
        await Check("native_basis_mode_marks_used_simulated_stage_or_process_handshake_route",()=>
        {
            True(CReviewProcessingBasis.IsSimulationOrUnknown(new[]{1},new Dictionary<int,bool?>{{1,false}},new[]{7,8},new Dictionary<int,bool?>{{7,false},{8,true}}));return Task.CompletedTask;
        });
        await Check("native_basis_mode_unknown_or_missing_required_route_never_proves_real_machining",()=>
        {
            var heads=new Dictionary<int,bool?>{{1,false}};var stage=new Dictionary<int,bool?>{{7,false},{8,null}};
            True(CReviewProcessingBasis.IsSimulationOrUnknown(new[]{1},heads,new[]{-1},stage)&&CReviewProcessingBasis.IsSimulationOrUnknown(new[]{1},heads,new[]{8},stage)
                &&CReviewProcessingBasis.IsSimulationOrUnknown(new[]{2},heads,new[]{7},stage)&&CReviewProcessingBasis.IsSimulationOrUnknown(Array.Empty<int>(),heads,new[]{7},stage));
            True(CReviewProcessingBasis.GetExecutionSimulationMode(new[]{1},heads,new[]{8},stage) is null);
            var unknown=Basis(Parameters(new())) with{IsExecutionModeKnown=false};unknown=unknown with{ProofSha256=CReviewProcessingBasis.Proof(unknown)};
            var blocked=false;try{CReviewProcessingBasis.Complete(unknown,DateTimeOffset.UtcNow);}catch(InvalidDataException){blocked=true;}True(blocked);return Task.CompletedTask;
        });
        foreach(var stage in new[]{"reservation","plan","invalidate","save"})
        {
            await Check("native_save_reservation_exception_boundary_cleans_uncommitted_"+stage,async()=>
            {
                var path=Temporary();Directory.CreateDirectory(Path.GetDirectoryName(path)!);var reservations=new List<string>();var failure=new IOException("synthetic "+stage);
                var seen=false;try{await CReviewSaveReservationTransaction.Run<int>(reservations,()=>{System.IO.File.WriteAllText(path,"SYNTHETIC-RESERVATION");reservations.Add(path);throw failure;},()=>Task.FromResult(false));}
                catch(IOException e){seen=ReferenceEquals(e,failure);}True(seen&&!System.IO.File.Exists(path));
            });
        }
        await Check("native_save_reservation_preserves_committed_or_unknown_receipt_and_successful_write",async()=>
        {
            foreach(var state in new[]{"committed","unknown","success"})
            {
                var path=Temporary();Directory.CreateDirectory(Path.GetDirectoryName(path)!);System.IO.File.WriteAllText(path,"SYNTHETIC-RESERVATION");var list=new[]{path};
                try{var result=await CReviewSaveReservationTransaction.Run<int>(list,()=>state=="success"?Task.FromResult(7):throw new IOException("post-write"),()=>state=="unknown"?throw new IOException("receipt unavailable"):Task.FromResult(true));True(result==7);}
                catch(IOException)when(state!="success"){}
                True(System.IO.File.Exists(path));
            }
        });
    }
}
