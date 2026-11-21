using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drilling.Common.Review;
using Drilling.Common.Recipe;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.File.ReviewOffset;

internal static partial class Program
{
    private static async Task RunMeasurementStateChecks()
    {
        static Dictionary<string,ST_REVIEW_OFFSET_ROW> Working(Fixture f,CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,bool resetAll=false)
            =>(Dictionary<string,ST_REVIEW_OFFSET_ROW>)typeof(CManualInspectionBackend).GetMethod("BuildWorkingRows",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(f.Backend,[s,values,resetAll])!;
        static CManualInspectionSource Reset(CManualInspectionSource s,IReadOnlySet<string> keys,IReadOnlyList<int>? cells=null)=>s with
        {Points=s.Points.Select(p=>p with{IsMeasurementOffsetInitialized=p.IsMeasurementOffsetInitialized||keys.Contains(p.OffsetKey)}).ToArray(),InitializationOffsetKeys=keys,InitializationCellNos=cells??[]};
        static Dictionary<string,ST_MANUAL_OFFSET> Values(CManualInspectionSource s,IReadOnlySet<string> keys)=>s.Points.ToDictionary(p=>p.Key,p=>keys.Contains(p.OffsetKey)?ST_MANUAL_OFFSET.Zero:s.Values[p.Key]);
        async Task<(Fixture F,CManualInspectionSource S,string Path,ST_MANUAL_OFFSET_BUNDLE Bundle)> ExportZero(bool init)
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=init}]);var s=await f.Backend.LoadAsync(null,default);var path=Path.Combine(f.Root,"state.json");
            await f.Backend.ExportOffsetBundleAsync(s,s.Values,path,false,false,default);return(f,s,path,CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path)));
        }
        await Check("measurement_state_csv_preserves_explicit_init_zero_and_origin_time",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);var csv=await System.IO.File.ReadAllTextAsync(saved.FilePath);var row=(await f.Offsets.LoadLatest("QA")).Rows.Single();
            True(csv.Contains("MEASUREMENT_STATE")&&csv.Contains("INITIALIZED")&&row.IsMeasurementOffsetInitialized&&row.MeasurementInitializedAt.HasValue);
            var s=await f.Backend.LoadAsync(null,default);True(s.Points.Single(p=>p.ShotNo==1).IsMeasurementOffsetInitialized&&s.Values.Values.All(v=>v.IsFinite));
        });
        await Check("measurement_state_csv_keeps_real_measured_zero_distinct_from_init",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true}]);var row=(await f.Offsets.LoadLatest("QA")).Rows.Single();var s=await f.Backend.LoadAsync(null,default);
            True(!row.IsMeasurementOffsetInitialized&&row.MeasurementInitializedAt is null&&s.Points.All(p=>!p.IsMeasurementOffsetInitialized)&&s.AppliedOffsetKeys.Count==1);
            True((await System.IO.File.ReadAllTextAsync(saved.FilePath)).Contains("MEASURED"));
        });
        await Check("measurement_state_csv_legacy_header_without_optional_columns_still_loads_as_measured",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.001,-.002){IsDesignCoordinate=true}]);var lines=await System.IO.File.ReadAllLinesAsync(saved.FilePath);
            var header=Array.FindIndex(lines,l=>l.StartsWith("CELLID,",StringComparison.Ordinal));True(header>=0);for(var i=header;i<lines.Length;i++)if(!string.IsNullOrWhiteSpace(lines[i]))lines[i]=string.Join(',',lines[i].Split(',').Take(5));
            await System.IO.File.WriteAllLinesAsync(saved.FilePath,lines);var old=await new CReviewOffsetFile(f.Root+"/Config").LoadLatest("QA");True(old.Rows.Single().ReviewOffsetX==.001&&!old.Rows.Single().IsMeasurementOffsetInitialized&&!old.HasMeasurementStateMetadata);
        });
        await Check("measurement_state_csv_state_only_transition_creates_new_file_and_preserves_old_bytes",async()=>
        {
            var f=new Fixture();var a=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true}]);var bytes=await System.IO.File.ReadAllBytesAsync(a.FilePath);var b=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);
            True(a.FilePath!=b.FilePath&&System.IO.File.ReadAllBytes(a.FilePath).SequenceEqual(bytes)&&b.Rows.Single().IsMeasurementOffsetInitialized);
        });
        await Check("measurement_state_csv_rejects_initialized_nonzero_instead_of_hiding_real_offset",async()=>
        {
            var f=new Fixture();await Reject(()=>f.Offsets.SaveNew("QA",[new(1,"A1",.001,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]));
        });
        await Check("measurement_state_scoped_cell_init_resets_hidden_same_cell_legacy_but_preserves_other_cells",async()=>
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"A1",.004,-.005){IsDesignCoordinate=true},new(1,"ZZ999",.7,.8){IsDesignCoordinate=true},new(99,"ZZ999",.9,1){IsDesignCoordinate=true}]);var s=await f.Backend.LoadAsync(null,default);var keys=s.Points.Select(p=>p.OffsetKey).ToHashSet();
            var next=Working(f,Reset(s,keys,[1]),Values(s,keys));True(next["1:ZZ999"].ReviewOffsetX==0&&next["1:ZZ999"].IsMeasurementOffsetInitialized&&next["99:ZZ999"].ReviewOffsetX==.9&&!next["99:ZZ999"].IsMeasurementOffsetInitialized);
        });
        await Check("measurement_state_scoped_head_init_keeps_unknown_head_legacy_rows",async()=>
        {
            var f=new Fixture();await f.Offsets.SaveNew("QA",[new(1,"A1",.004,-.005){IsDesignCoordinate=true},new(1,"ZZ999",.7,.8){IsDesignCoordinate=true}]);var s=await f.Backend.LoadAsync(null,default);var target=s.Points.First(p=>p.ShotNo==1);var keys=new HashSet<string>{target.OffsetKey};var next=Working(f,Reset(s,keys),Values(s,keys));
            True(next["1:A1"].IsMeasurementOffsetInitialized&&next["1:A1"].ReviewOffsetX==0&&next["1:ZZ999"].ReviewOffsetX==.7&&!next["1:ZZ999"].IsMeasurementOffsetInitialized);
        });
        await Check("measurement_state_unrelated_partial_save_does_not_renew_old_init_timestamp",async()=>
        {
            var f=new Fixture();var oldTime=DateTimeOffset.Parse("2026-11-01T03:00:00+09:00");await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=oldTime}]);var s=await f.Backend.LoadAsync(null,default);var other=s.Points.First(p=>p.ShotNo!=1);var values=s.Values.ToDictionary(p=>p.Key,p=>p.Value);values[other.Key]=new(1,2);
            var next=Working(f,s,values);True(next["1:A1"].IsMeasurementOffsetInitialized&&next["1:A1"].MeasurementInitializedAt==oldTime);
        });
        await Check("measurement_state_reinit_same_zero_explicitly_renews_only_its_scope_timestamp",async()=>
        {
            var f=new Fixture();var oldTime=DateTimeOffset.Parse("2026-11-01T03:00:00+09:00");await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true,MeasurementInitializedAt=oldTime}]);var s=await f.Backend.LoadAsync(null,default);var target=s.Points.Single(p=>p.ShotNo==1);var keys=new HashSet<string>{target.OffsetKey};var next=Working(f,Reset(s,keys),Values(s,keys));
            True(next["1:A1"].IsMeasurementOffsetInitialized&&next["1:A1"].MeasurementInitializedAt is null);
        });
        await Check("measurement_state_portable_schema_two_preserves_init_and_import_metadata",async()=>
        {
            var(f,s,path,b)=await ExportZero(true);True(b.SchemaVersion.EndsWith("/2")&&b.ReviewRows.Single(r=>r.ShotId=="A1").IsMeasurementOffsetInitialized);var imported=await f.Backend.ImportOffsetBundleAsync(s,path,default);
            var target=s.Points.Single(p=>p.ShotNo==1);True(imported[target.Key]==ST_MANUAL_OFFSET.Zero&&f.Backend.ImportedInitializedOffsetKeys.Contains(target.OffsetKey));
        });
        await Check("measurement_state_portable_measured_zero_has_no_initialized_import_metadata",async()=>
        {
            var(f,s,path,b)=await ExportZero(false);await f.Backend.ImportOffsetBundleAsync(s,path,default);True(b.ReviewRows.All(r=>!r.IsMeasurementOffsetInitialized)&&f.Backend.ImportedInitializedOffsetKeys.Count==0);
        });
        await Check("measurement_state_portable_schema_one_without_new_fields_remains_supported",async()=>
        {
            var(f,s,path,b)=await ExportZero(false);var node=JsonNode.Parse(CManualInspectionOffsetBundle.Serialize(b))!.AsObject();node["schemaVersion"]=CManualInspectionOffsetBundle.LegacySchema;
            foreach(var row in node["reviewRows"]!.AsArray()){row!.AsObject().Remove("isMeasurementOffsetInitialized");row.AsObject().Remove("measurementInitializedAt");}
            var json=node.ToJsonString();await System.IO.File.WriteAllTextAsync(path,json);var old=CManualInspectionOffsetBundle.Deserialize(json);var imported=await f.Backend.ImportOffsetBundleAsync(s,path,default);
            True(old.SchemaVersion==CManualInspectionOffsetBundle.LegacySchema&&old.ReviewRows.All(r=>!r.IsMeasurementOffsetInitialized)&&imported.Count==s.Points.Count);
        });
        await Check("measurement_state_portable_schema_one_cannot_inject_new_init_state",async()=>
        {
            var(_,_,_,b)=await ExportZero(true);await Reject(()=>Task.Run(()=>CManualInspectionOffsetBundle.Validate(b with{SchemaVersion=CManualInspectionOffsetBundle.LegacySchema})));
        });
        await Check("measurement_state_portable_full_init_export_carries_gray_metadata_for_known_and_legacy_rows",async()=>
        {
            var(f,s,_,_)=await ExportZero(false);var path=Path.Combine(f.Root,"all-init.json");var zero=s.Points.ToDictionary(p=>p.Key,_=>ST_MANUAL_OFFSET.Zero);await f.Backend.ExportOffsetBundleAsync(s,zero,path,true,true,default);var b=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));
            True(b.InitializeAllReviewRows&&b.ReviewRows.All(r=>r.IsMeasurementOffsetInitialized&&r.XMm==0&&r.YMm==0));
        });
        await Check("measurement_state_csv_invalid_state_and_timestamp_are_not_silently_treated_as_measured",async()=>
        {
            foreach(var column in new[]{"MEASUREMENT_STATE","MEASUREMENT_INITIALIZED_AT"})
            {
                var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);var lines=await System.IO.File.ReadAllLinesAsync(saved.FilePath);var header=Array.FindIndex(lines,l=>l.StartsWith("CELLID,",StringComparison.Ordinal));var index=Array.IndexOf(lines[header].Split(','),column);True(index>=0);
                var row=lines[header+1].Split(',');row[index]="INVALID_STATE_OR_TIMESTAMP";lines[header+1]=string.Join(',',row);await System.IO.File.WriteAllLinesAsync(saved.FilePath,lines);
                var rejected=false;try{await new CReviewOffsetFile(f.Root+"/Config").LoadLatest("QA");}catch(CReviewOffsetValidationException){rejected=true;}catch(InvalidDataException){rejected=true;}catch(InvalidOperationException){rejected=true;}True(rejected);
            }
        });
        await Check("measurement_state_active_export_keeps_saved_init_even_after_unsaved_new_measurement",async()=>
        {
            var(f,s,_,_)=await ExportZero(true);var fresh=s with{Points=s.Points.Select(p=>p with{IsMeasurementOffsetInitialized=false}).ToArray()};var path=Path.Combine(f.Root,"active-after-unsaved-measurement.json");
            await f.Backend.ExportOffsetBundleAsync(fresh,fresh.Values,path,false,false,default);var bundle=CManualInspectionOffsetBundle.Deserialize(await System.IO.File.ReadAllTextAsync(path));
            True(bundle.ReviewRows.Single(r=>r.ShotId=="A1").IsMeasurementOffsetInitialized&&!bundle.IsDraft);
        });
    }
}
