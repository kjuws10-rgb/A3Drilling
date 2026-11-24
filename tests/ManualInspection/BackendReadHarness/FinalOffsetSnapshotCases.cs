using System.IO;
using System.Reflection;
using System.Text.Json;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunFinalOffsetSnapshotChecks()
    {
        await Check("final_native_empty_model_is_no_file_and_no_measured_zero",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);var final=(await f.Backend.ReadSavedOffsetSnapshotAsync(source,default))!;
            True(final.FilePath==""&&final.FileName==""&&final.SavedAt is null&&final.RowCount==0&&final.AppliedPointKeys.Count==0);
            True(final.ValuesByPointKey.Count==source.Points.Count&&final.ValuesByPointKey.Values.All(value=>value==ST_MANUAL_OFFSET.Zero)
                &&final.InitializedOffsetKeys.SetEquals(source.Points.Select(point=>point.OffsetKey)));
        });
        await Check("final_native_saved_map_has_actual_byte_identity_and_ignores_old_raw",async()=>
        {
            var f=new Fixture();var old=await f.Offsets.SaveNew("QA",[new(1,"A1",.002,.003){IsDesignCoordinate=true}]);var source=await f.Backend.LoadAsync(null,default);
            var bytes=System.IO.File.ReadAllBytes(old.FilePath);await Task.Delay(2);var latest=await f.Offsets.SaveNew("QA",[new(1,"A1",.014,-.009){IsDesignCoordinate=true}]);
            latest=await f.Offsets.LoadLatest("QA"); // Compare persisted filename/CSV millisecond precision, not the writer's pre-serialization ticks.
            var final=(await f.Backend.ReadSavedOffsetSnapshotAsync(source,default))!;var key=source.Points.First(point=>point.ShotNo==1).Key;
            True(final.FilePath==latest.FilePath&&final.FileName==latest.FileName&&final.SavedAt==latest.SavedAt&&final.ContentSha256==latest.ContentSha256&&final.RowCount==1);
            True(final.ValuesByPointKey[key]==new ST_MANUAL_OFFSET(14,-9)&&source.Values[key]==new ST_MANUAL_OFFSET(2,3)&&System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes));
        });
        await Check("final_native_read_does_not_mutate_live_adapter_or_authorize_old_revision",async()=>
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"A1",.002,.003){IsDesignCoordinate=true}]);var source=await f.Backend.LoadAsync(null,default);
            var names=new[]{"_recipe","_latest","_parameters","_targets","_plans","_shots","_baseline","_resultCache"};
            var fields=names.Select(name=>typeof(CManualInspectionBackend).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!).ToArray();
            var before=fields.Select(field=>field.GetValue(f.Backend)).ToArray();
            await f.Offsets.SaveNew("QA",[new(1,"A1",.011,.012){IsDesignCoordinate=true}]);await f.Backend.ReadSavedOffsetSnapshotAsync(source,default);
            True(fields.Select((field,index)=>ReferenceEquals(before[index],field.GetValue(f.Backend))).All(value=>value));
            await Reject(()=>f.Backend.EnsureCurrentAsync(source,default));
        });
        await Check("final_native_read_distinguishes_real_zero_init_and_missing_row",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true},new(1,"A2",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);
            var source=await f.Backend.LoadAsync(null,default);var final=(await f.Backend.ReadSavedOffsetSnapshotAsync(source,default))!;
            var real=source.Points.Single(point=>point.Row==0&&point.Column==0);var init=source.Points.Single(point=>point.Row==1&&point.Column==0);
            True(final.RowCount==2&&final.InitializedRowCount==1&&final.AppliedPointKeys.Contains(real.Key)&&!final.InitializedOffsetKeys.Contains(real.OffsetKey)
                &&final.InitializedOffsetKeys.Contains(init.OffsetKey)&&final.ValuesByPointKey.Values.All(value=>value==ST_MANUAL_OFFSET.Zero));
            True(final.FilePath==saved.FilePath);
        });
        await Check("final_export_reloads_latest_full_model_from_filtered_old_workspace",async()=>
        {
            var f=new Fixture(settingOverrides:new(){["H01_DEFAULT_OFFSET_X"]="0.025",["H01_FIRST_BEAM_OFFSET_X"]="0.012"});
            await f.Offsets.SaveNew("QA",[new(1,"A1",.002,.003){IsDesignCoordinate=true}]);var source=await f.Backend.LoadAsync(null,default);
            var subset=source with { Points=source.Points.Take(1).ToArray(),Values=source.Values.Take(1).ToDictionary(pair=>pair.Key,pair=>pair.Value),SourcePath="historical-work-file.csv" };
            var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.021,-.008){IsDesignCoordinate=true}]);var path=Path.Combine(f.Root,"final.json");
            var final=(await f.Backend.ExportFinalOffsetBundleAsync(subset,path,default))!;var bundle=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));
            True(!bundle.IsDraft&&bundle.Points.Count==source.Points.Count&&bundle.ReviewRows.Count==source.Points.Select(point=>point.OffsetKey).Distinct().Count()
                &&bundle.ActiveOffsetFileName==saved.FileName&&bundle.ActiveOffsetContentSha256==saved.ContentSha256);
            True(bundle.ReviewRows.Single(row=>row.ShotId=="A1").XMm==.021&&bundle.ReviewRows.Single(row=>row.ShotId=="A1").YMm==-.008
                &&bundle.ProtectedParameters["H01_DEFAULT_OFFSET_X"]=="0.025"&&bundle.ProtectedParameters["H01_FIRST_BEAM_OFFSET_X"]=="0.012");
            True(final.ValuesByPointKey.Count==source.Points.Count&&(await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
        });
        await Check("final_export_preserves_hidden_historical_rows_and_original_csv",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.005,.006){IsDesignCoordinate=true},new(99,"Z99",.017,-.019){IsDesignCoordinate=false}]);
            var bytes=System.IO.File.ReadAllBytes(saved.FilePath);var source=await f.Backend.LoadAsync(null,default);var path=Path.Combine(f.Root,"final-history.json");
            await f.Backend.ExportFinalOffsetBundleAsync(source,path,default);var bundle=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));
            True(bundle.RetainedHistoricalRows.Any(row=>row.CellNo==99&&row.ShotId=="Z99"&&row.XMm==.017&&row.YMm==-.019&&!row.IsDesignCoordinate));
            True(System.IO.File.ReadAllBytes(saved.FilePath).SequenceEqual(bytes));
        });
        await Check("final_native_same_path_same_stamp_replacement_refreshes_sha_and_value",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.002,.003){IsDesignCoordinate=true}]);var source=await f.Backend.LoadAsync(null,default);
            var stamp=System.IO.File.GetLastWriteTimeUtc(saved.FilePath);var text=await System.IO.File.ReadAllTextAsync(saved.FilePath);
            await System.IO.File.WriteAllTextAsync(saved.FilePath,text.Replace("0.002000","0.007000",StringComparison.Ordinal));System.IO.File.SetLastWriteTimeUtc(saved.FilePath,stamp);
            var final=(await f.Backend.ReadSavedOffsetSnapshotAsync(source,default))!;
            True(final.ContentSha256!=saved.ContentSha256&&final.FilePath==saved.FilePath&&final.ValuesByPointKey[source.Points.Single(point=>point.ShotNo==1).Key].XUm==7);
        });
        await Check("final_native_model_coordinate_run_and_geometry_mismatch_are_rejected",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);
            foreach(var bad in new[]{source with { Context=source.Context with { RecipeId="OTHER" } },source with { Context=source.Context with { RunId="OTHER" } },
                source with { Context=source.Context with { CoordinateSignature="OTHER" } },source with { ShiftOffsetXmm=1 },
                source with { Points=source.Points.Select((point,index)=>index==0?point with { XMm=point.XMm+1 }:point).ToArray() }})
                await Reject(()=>f.Backend.ReadSavedOffsetSnapshotAsync(bad,default));
            f.Selected="OTHER";var path=Path.Combine(f.Root,"must-not-export.json");await Reject(()=>f.Backend.ExportFinalOffsetBundleAsync(source,path,default));True(!System.IO.File.Exists(path));
        });
        await Check("final_native_reads_do_not_call_operation_guard_or_write_recipe",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);var guards=0;
            var backend=new CManualInspectionBackend(f.Review,new(new RecipesFile(f)),f.Settings,f.Results,f.Offsets,()=>f.Selected,null,()=>{guards++;throw new Exception("device guard must not execute");});
            var recipe=JsonSerializer.Serialize(f.Recipe);var settings=JsonSerializer.Serialize(f.SettingRows);var path=Path.Combine(f.Root,"final-readonly.json");
            await backend.ReadSavedOffsetSnapshotAsync(source,default);await backend.ExportFinalOffsetBundleAsync(source,path,default);
            True(guards==0&&JsonSerializer.Serialize(f.Recipe)==recipe&&JsonSerializer.Serialize(f.SettingRows)==settings&&(await f.Offsets.LoadLatest("QA")).FilePath=="");
        });
        await Check("final_native_watch_roots_include_all_declared_result_roots_and_offset_store",async()=>
        {
            var f=new Fixture();var results=new ExtraResultRoots(f.Root);var backend=new CManualInspectionBackend(f.Review,new(new RecipesFile(f)),f.Settings,results,f.Offsets,()=>f.Selected,null,()=>{});
            True(backend.WatchRoots.Count==4&&results.SearchRootPaths.All(path=>backend.WatchRoots.Contains(path,StringComparer.OrdinalIgnoreCase))&&backend.WatchRoots.Contains(f.Offsets.RootPath,StringComparer.OrdinalIgnoreCase));
        });
        await Check("final_native_unassigned_legacy_scanner_row_is_never_guessed_as_head1_design",async()=>
        {
            var f=new Fixture(settingOverrides:new(){["H01_AK_POSITION_X"]="1000",["H01_SCAN_FIELD_WIDTH_X"]="1"});
            var target=f.Review.CreateAllPlan(f.Recipe,11).Points.First(point=>point.HeadNo==0);
            var name=CReviewShotNameFormatter.ToMatrixName(target.ShotNo,target.PixelCountX);
            var saved=await f.Offsets.SaveNew("QA",[new(target.CellNo,name,.004,.002){IsDesignCoordinate=false}]);var bytes=System.IO.File.ReadAllBytes(saved.FilePath);
            var source=await f.Backend.LoadAsync(null,default);var path=Path.Combine(f.Root,"invalid-head-final.json");
            await Reject(()=>f.Backend.ReadSavedOffsetSnapshotAsync(source,default));await Reject(()=>f.Backend.ExportFinalOffsetBundleAsync(source,path,default));
            True(!System.IO.File.Exists(path)&&System.IO.File.ReadAllBytes(saved.FilePath).SequenceEqual(bytes));
            var design=await f.Offsets.SaveNew("QA",[new(target.CellNo,name,.004,.002){IsDesignCoordinate=true}]);
            var final=(await f.Backend.ReadSavedOffsetSnapshotAsync(source,default))!;
            True(final.FilePath==design.FilePath&&final.ValuesByPointKey[source.Points.Single(point=>point.ShotNo==target.ShotNo).Key]==new ST_MANUAL_OFFSET(4,2));
            True(final.InitializedOffsetKeys.Count==source.Points.Select(point=>point.OffsetKey).Distinct().Count()-1);
            await f.Backend.ExportFinalOffsetBundleAsync(source,path,default);True(System.IO.File.Exists(path));
        });
    }

    private sealed class ExtraResultRoots(string root):CReviewResultFileBase
    {
        public override string RootPath=>Path.Combine(root,"Review");
        public override IReadOnlyList<string> SearchRootPaths=>[RootPath,Path.Combine(root,"ReviewResult"),Path.Combine(root,"InspectionArchive")];
        public override Task<ST_REVIEW_RESULT_FILE_DATA> Load(string path,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public override Task Save(ST_REVIEW_RESULT_DATA result,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
