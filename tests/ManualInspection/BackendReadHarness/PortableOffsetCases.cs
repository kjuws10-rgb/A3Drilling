using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunPortableOffsetChecks()
    {
        static Fixture ProtectedFixture()=>new(new(){["CELL1_A1_ALIGN_OFFSET_X"]="0.0015",["CELL1_A1_ALIGN_OFFSET_Y"]="-0.0025",["CELL1_A1_RECIPE_OFFSET_X"]="0.0005",["CELL1_A1_RECIPE_OFFSET_Y"]="0.0007"},
            new(){["H01_DEFAULT_OFFSET_X"]="0.025",["H01_DEFAULT_OFFSET_Y"]="-0.015",["H01_FIRST_BEAM_OFFSET_X"]="0.012",["H01_FIRST_BEAM_OFFSET_Y"]="-0.009",["REVIEW_TO_HEAD1_GAP_Y"]="1",["H01_SCANNER_XY_FLIP"]="OFF",["TEST_PASSWORD"]="synthetic-placeholder-not-a-credential"});
        static IReadOnlyDictionary<string,ST_MANUAL_OFFSET> All(CManualInspectionSource s,double x,double y)=>s.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(x,y));
        static byte[] Bytes(string path)=>System.IO.File.ReadAllBytes(path);
        static Dictionary<string,string> Params(Fixture f)=>f.Recipe.Parameters.Where(p=>p.Key is not null).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        static Dictionary<string,ST_REVIEW_OFFSET_ROW> WorkingRows(Fixture f,CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,bool initialize)
            =>(Dictionary<string,ST_REVIEW_OFFSET_ROW>)typeof(CManualInspectionBackend).GetMethod("BuildWorkingRows",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(f.Backend,[s,values,initialize])!;
        async Task<(Fixture F,CManualInspectionSource Source,string Path,ST_MANUAL_OFFSET_BUNDLE Bundle)> Export(bool init=false)
        {
            var f=ProtectedFixture();await f.Offsets.SaveNew("QA",[new(1,"A1",.004,-.005){IsDesignCoordinate=true}]);var s=await f.Backend.LoadAsync(null,default);var path=Path.Combine(f.Root,"portable.a3offset.json");
            await f.Backend.ExportOffsetBundleAsync(s,init?All(s,0,0):s.Values,path,init,init,default);var b=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));return(f,s,path,b);
        }
        await Check("portable_offset_exports_latest_absolute_map_once_instead_of_summing_history",async()=>
        {
            var f=ProtectedFixture();var one=await f.Offsets.SaveNew("QA",[new(1,"A1",.001,.002){IsDesignCoordinate=true}]);await Task.Delay(2);
            var two=await f.Offsets.SaveNew("QA",[new(1,"A1",.004,-.005){IsDesignCoordinate=true}]);var a=Bytes(one.FilePath);var b=Bytes(two.FilePath);var s=await f.Backend.LoadAsync(null,default);var path=Path.Combine(f.Root,"portable.json");
            await f.Backend.ExportOffsetBundleAsync(s,s.Values,path,false,false,default);var package=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));var row=package.ReviewRows.Single(r=>r.CellNo==1&&r.ShotId=="A1");
            True(row.XMm==.004&&row.YMm==-.005&&Bytes(one.FilePath).SequenceEqual(a)&&Bytes(two.FilePath).SequenceEqual(b));True((await f.Offsets.LoadLatest("QA")).FilePath==two.FilePath);
        });
        await Check("portable_offset_includes_protected_reference_only_and_exact_planner_coordinates",async()=>
        {
            var(f,s,_,b)=await Export();True(b.SchemaVersion==CManualInspectionOffsetBundle.Schema&&b.ProgramVersion=="11.24.3"&&b.ReviewCoordinateSpace=="DESIGN"&&b.ReviewUnit=="mm"&&b.FinalCoordinateSpace=="SCANNER"&&b.FinalCoordinateUnit=="mm");
            True(b.ProtectedSnapshotPolicy==CManualInspectionOffsetBundle.ReferenceOnly&&b.ProtectedParameters["H01_DEFAULT_OFFSET_X"]=="0.025"&&b.ProtectedParameters["H01_FIRST_BEAM_OFFSET_X"]=="0.012"&&b.ProtectedParameters["CELL1_A1_ALIGN_OFFSET_X"]=="0.0015"&&b.ProtectedParameters["HeadGapX"]=="110"&&!b.ProtectedParameters.ContainsKey("TEST_PASSWORD"));
            var p=Params(f);foreach(var setting in f.SettingRows)p[setting.Key]=setting.Value;CReviewOffsetData.ApplyToParameters(p,await f.Offsets.LoadLatest("QA"));var plan=CShotCoordinatePlanBuilder.Build(p);
            True(b.FinalCoordinates.Count==plan.Shots.Count&&b.Points.Count==s.Points.Count);
            foreach(var shot in plan.Shots){var x=b.FinalCoordinates.Single(c=>c.CellNo==shot.CellNo&&c.ShotNo==shot.CellShotNo);True(x.ScannerGxMm==shot.ScannerGx&&x.ScannerGyMm==shot.ScannerGy&&x.CombinedScannerOffsetXmm==shot.FinalScannerOffsetX&&x.CombinedScannerOffsetYmm==shot.FinalScannerOffsetY&&x.EncoderWaitPosition==shot.EncoderWaitPosition);}
        });
        await Check("portable_init_zeroes_measurement_map_but_preserves_protected_contributions_and_source_files",async()=>
        {
            var(f,s,_,before)=await Export();var saved=await f.Offsets.LoadLatest("QA");var bytes=Bytes(saved.FilePath);var recipe=JsonSerializer.Serialize(f.Recipe);var settings=JsonSerializer.Serialize(f.SettingRows);var path=Path.Combine(f.Root,"init.json");
            await f.Backend.ExportOffsetBundleAsync(s,All(s,0,0),path,true,true,default);var after=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));
            True(after.IsDraft&&after.InitializeAllReviewRows&&after.ReviewRows.All(r=>r.XMm==0&&r.YMm==0));True(after.ProtectedParameters.OrderBy(p=>p.Key).SequenceEqual(before.ProtectedParameters.OrderBy(p=>p.Key)));
            True(JsonSerializer.Serialize(f.Recipe)==recipe&&JsonSerializer.Serialize(f.SettingRows)==settings&&Bytes(saved.FilePath).SequenceEqual(bytes));
            var old=before.FinalCoordinates.First(c=>c.CellNo==1&&c.ShotNo==1);var now=after.FinalCoordinates.Single(c=>c.CellNo==1&&c.ShotNo==1);
            True(now.DefaultRecipeOffsetXmm==old.DefaultRecipeOffsetXmm&&now.FirstBeamRecipeOffsetXmm==old.FirstBeamRecipeOffsetXmm&&now.DistortionDesignOffsetXmm==old.DistortionDesignOffsetXmm&&now.RecipeScannerOffsetXmm==old.RecipeScannerOffsetXmm);
            True(now.ReviewScannerOffsetXmm==0&&now.ReviewScannerOffsetYmm==0&&Math.Abs(now.CombinedScannerOffsetXmm)>0);
        });
        await Check("portable_init_clears_hidden_historical_rows_and_writer_keeps_old_csv_bytes",async()=>
        {
            var f=ProtectedFixture();var old=await f.Offsets.SaveNew("QA",[new(1,"A1",.004,-.005){IsDesignCoordinate=true},new(99,"Z99",.13,-.17){IsDesignCoordinate=false}]);var bytes=Bytes(old.FilePath);var s=await f.Backend.LoadAsync(null,default);var rows=WorkingRows(f,s,All(s,0,0),true);
            True(rows.Values.All(r=>r.ReviewOffsetX==0&&r.ReviewOffsetY==0)&&rows.Values.Any(r=>r.CellId==99&&r.ShotId=="Z99"&&!r.IsDesignCoordinate));
            var identity=Path.Combine(f.Root,"synthetic-init-save.json");await System.IO.File.WriteAllTextAsync(identity,"{\"operation\":\"synthetic init\"}");
            var next=await f.Offsets.SaveNewIfUnchanged("QA",old.FilePath,rows.Values.ToArray(),identity,expectedLatestContentSha256:old.ContentSha256);
            True(next.FilePath!=old.FilePath&&Bytes(old.FilePath).SequenceEqual(bytes)&&next.Rows.All(r=>r.ReviewOffsetX==0&&r.ReviewOffsetY==0));
        });
        await Check("portable_import_returns_absolute_values_without_saving_or_overwriting_protected_reference",async()=>
        {
            var(f,s,path,b)=await Export();var active=await f.Offsets.LoadLatest("QA");var bytes=Bytes(active.FilePath);var settings=JsonSerializer.Serialize(f.SettingRows);var recipe=JsonSerializer.Serialize(f.Recipe);
            var changed=b with{ReviewRows=b.ReviewRows.Select(r=>r with{XMm=.012,YMm=-.007,IsMeasurementOffsetInitialized=false,MeasurementInitializedAt=null}).ToArray(),ProtectedParameters=new Dictionary<string,string>{{"H01_DEFAULT_OFFSET_X","999"}}};await System.IO.File.WriteAllTextAsync(path,CManualInspectionOffsetBundle.Serialize(changed));
            var imported=await f.Backend.ImportOffsetBundleAsync(s,path,default);True(imported.Count==s.Points.Count&&imported.Values.All(v=>v==new ST_MANUAL_OFFSET(12,-7)));
            True(JsonSerializer.Serialize(f.SettingRows)==settings&&JsonSerializer.Serialize(f.Recipe)==recipe&&Bytes(active.FilePath).SequenceEqual(bytes)&&(await f.Offsets.LoadLatest("QA")).FilePath==active.FilePath);
        });
        await Check("portable_import_rejects_recipe_coordinate_geometry_and_mask_mismatch",async()=>
        {
            var(f,s,path,b)=await Export();foreach(var changed in new[]{b with{RecipeId="OTHER"},b with{CoordinateSignature="OTHER"},b with{Points=b.Points.Skip(1).ToArray()},b with{Points=b.Points.Select((p,i)=>i==0?p with{XMm=p.XMm+.001}:p).ToArray()},b with{Points=b.Points.Select((p,i)=>i==0?p with{Masked=!p.Masked}:p).ToArray()}})
            {await System.IO.File.WriteAllTextAsync(path,JsonSerializer.Serialize(changed,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,path,default));}
        });
        await Check("portable_import_rejects_partial_duplicate_unknown_and_nonfinite_rows",async()=>
        {
            var(_,_,_,b)=await Export();foreach(var changed in new[]{b with{ReviewRows=b.ReviewRows.Skip(1).ToArray()},b with{ReviewRows=b.ReviewRows.Concat([b.ReviewRows[0]]).ToArray()},b with{ReviewRows=b.ReviewRows.Concat([new ST_MANUAL_OFFSET_BUNDLE_ROW(99,"Z99",0,0)]).ToArray()},b with{ReviewRows=b.ReviewRows.Select((r,i)=>i==0?r with{XMm=double.NaN}:r).ToArray()}})
                await Reject(()=>Task.Run(()=>CManualInspectionOffsetBundle.Validate(changed)));
        });
        await Check("portable_json_rejects_duplicate_unknown_fields_and_unit_changes",async()=>
        {
            var(_,_,_,b)=await Export();var json=CManualInspectionOffsetBundle.Serialize(b);
            foreach(var text in new[]{json.Replace("\"recipeId\": \"QA\"","\"recipeId\": \"QA\", \"recipeId\": \"QA\""),json.Replace("{","{\"unexpected\":1,",StringComparison.Ordinal).Split('\n')[0],json.Replace("\"reviewUnit\": \"mm\"","\"reviewUnit\": \"um\"")})
            {try{CManualInspectionOffsetBundle.Deserialize(text);throw new Exception("Unexpected valid malformed portable document");}catch(InvalidDataException){}catch(JsonException){}}
        });
        await Check("portable_export_refuses_overwrite_inside_operating_folders_and_non_json_target",async()=>
        {
            var(f,s,_,_)=await Export();foreach(var path in new[]{Path.Combine(f.Results.RootPath,"replace.json"),Path.Combine(f.Offsets.RootPath,"replace.json"),Path.Combine(f.Backend.StateDirectory,"replace.json"),Path.Combine(f.Root,"replace.csv")})
                await Reject(()=>f.Backend.ExportOffsetBundleAsync(s,s.Values,path,false,false,default));
        });
        await Check("portable_roundtrip_never_promotes_history_rows_or_scanner_commands_to_active_offsets",async()=>
        {
            var(f,s,path,b)=await Export();var changed=b with{RetainedHistoricalRows=[new(99,"Z99",777,-888,false)],FinalCoordinates=b.FinalCoordinates.Select(r=>r with{ScannerGxMm=100000,CombinedScannerOffsetXmm=999999}).ToArray()};
            await System.IO.File.WriteAllTextAsync(path,CManualInspectionOffsetBundle.Serialize(changed));var imported=await f.Backend.ImportOffsetBundleAsync(s,path,default);
            True(imported.Count==s.Points.Count&&imported.All(p=>p.Value==s.Values[p.Key])&&!(await f.Offsets.LoadLatest("QA")).Rows.Any(r=>r.CellId==99));
        });
        await Check("portable_source_revision_change_is_rechecked_before_import_and_export",async()=>
        {
            var(f,s,path,_)=await Export();await f.Offsets.SaveNew("QA",[new(1,"A1",.008,.009){IsDesignCoordinate=true}]);
            await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,path,default));await Reject(()=>f.Backend.ExportOffsetBundleAsync(s,s.Values,Path.Combine(f.Root,"stale.json"),false,false,default));
        });
        await Check("portable_whole_model_rows_expand_and_shrink_displayed_beam_siblings_without_double_accumulation",async()=>
        {
            var(f,s,path,_)=await Export();var extra=s.Points.Select(p=>p.Key[..(p.Key.LastIndexOf('/')+1)]+"B1");var keys=s.Points.Select(p=>p.Key).Concat(extra).ToArray();var grown=await f.Backend.LoadAsync(null,default,keys);
            True(grown.Points.Count==s.Points.Count*2);var loaded=await f.Backend.ImportOffsetBundleAsync(grown,path,default);True(loaded.Count==grown.Points.Count&&grown.Points.GroupBy(p=>p.OffsetKey).All(g=>g.Select(p=>loaded[p.Key]).Distinct().Count()==1));
            var expandedPath=Path.Combine(f.Root,"expanded.json");await f.Backend.ExportOffsetBundleAsync(grown,loaded,expandedPath,true,false,default);var current=await f.Backend.LoadAsync(null,default);
            var beforePlans=((System.Collections.IDictionary)typeof(CManualInspectionBackend).GetField("_plans",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Backend)!).Count;
            var shrunk=await f.Backend.ImportOffsetBundleAsync(current,expandedPath,default);var afterPlans=((System.Collections.IDictionary)typeof(CManualInspectionBackend).GetField("_plans",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Backend)!).Count;
            True(shrunk.Count==s.Points.Count&&shrunk.All(p=>p.Value==s.Values[p.Key])&&beforePlans==afterPlans);
        });
        await Check("portable_extra_beam_must_match_real_recipe_beam_range_key_head_and_geometry",async()=>
        {
            var(f,s,path,b)=await Export();var first=b.Points[0];foreach(var forged in new[]{first with{Key=first.Key[..(first.Key.LastIndexOf('/')+1)]+"B17",BeamNo=17},first with{Key=first.Key+"FORGED"},first with{HeadNo=first.HeadNo==1?2:1},first with{XMm=first.XMm+.01}})
            {var altered=b with{Points=b.Points.Select((p,i)=>i==0?forged:p).ToArray()};await System.IO.File.WriteAllTextAsync(path,CManualInspectionOffsetBundle.Serialize(altered));await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,path,default));}
        });
        await Check("portable_import_requires_all_model_shot_rows_even_when_display_beam_changes",async()=>
        {
            var(f,s,path,b)=await Export();var row=b.ReviewRows[0];var reduced=b with{ReviewRows=b.ReviewRows.Skip(1).ToArray(),Points=b.Points.Where(p=>p.CellNo!=row.CellNo||p.ShotId!=row.ShotId).ToArray()};
            await System.IO.File.WriteAllTextAsync(path,CManualInspectionOffsetBundle.Serialize(reduced));await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,path,default));
        });
    }
}
