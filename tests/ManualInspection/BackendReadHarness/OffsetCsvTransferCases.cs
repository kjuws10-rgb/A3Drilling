using System.IO;
using System.Diagnostics;
using System.Text;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.File.ReviewOffset;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunOffsetCsvTransferChecks()
    {
        static async Task<(Fixture F,CManualInspectionSource Source,ST_REVIEW_OFFSET_FILE_DATA Saved)> Saved()
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[
                new(1,"A1",.012,-.023){IsDesignCoordinate=true},
                new(1,"B1",0,0){IsDesignCoordinate=true},
                new(1,"A2",0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true},
                new(99,"Z99",.234567,-.345678){IsDesignCoordinate=false}]);
            return (f,await f.Backend.LoadAsync(null,default),saved);
        }
        static async Task ExportCopy(Fixture f,CManualInspectionSource source,string target,bool overwrite=false)
            => _=await f.Backend.ExportCurrentOffsetCsvAsync(source,target,default,overwrite);
        static ST_MANUAL_POINT At(CManualInspectionSource source,string shot)
            => source.Points.First(p=>CReviewShotNameFormatter.ToMatrixName(p.ShotNo,2)==shot);
        await Check("offset_csv_export_preserves_original_bytes_sha_headers_space_state_and_hidden_rows",async()=>
        {
            var(f,s,saved)=await Saved();var target=Path.Combine(f.Root,"user files","renamed.csv");
            var result=await f.Backend.ExportCurrentOffsetCsvAsync(s,target,default);
            True(System.IO.File.ReadAllBytes(target).SequenceEqual(System.IO.File.ReadAllBytes(saved.FilePath)));
            var copy=CReviewOffsetFile.ReadSnapshotFile("QA",target);
            True(result!.ContentSha256==copy.ContentSha256&&copy.Rows.Count==4&&copy.Rows.Single(r=>r.CellId==99).ReviewOffsetX==.234567);
            True(!copy.Rows.Single(r=>r.CellId==99).IsDesignCoordinate&&copy.Rows.Single(r=>r.ShotId=="A2").IsMeasurementOffsetInitialized);
            True(!copy.Rows.Single(r=>r.ShotId=="B1").IsMeasurementOffsetInitialized&&copy.Rows.Single(r=>r.ShotId=="B1").ReviewOffsetX==0);
            True(copy.HasMeasurementStateMetadata&&copy.Rows.Single(r=>r.ShotId=="A2").MeasurementInitializedAt==saved.Rows.Single(r=>r.ShotId=="A2").MeasurementInitializedAt);
        });
        await Check("offset_csv_safe_export_name_in_current_folder_never_activates_and_future_writer_still_succeeds",async()=>
        {
            var(f,s,saved)=await Saved();var target=Path.Combine(Path.GetDirectoryName(saved.FilePath)!,Path.GetFileNameWithoutExtension(saved.FileName)+"_export.csv");
            await ExportCopy(f,s,target);True((await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
            var next=await f.Offsets.SaveNew("QA",saved.Rows);True(next.FilePath!=saved.FilePath&&(await f.Offsets.LoadLatest("QA")).FilePath==next.FilePath);
            True(System.IO.File.ReadAllBytes(target).SequenceEqual(System.IO.File.ReadAllBytes(saved.FilePath)));
        });
        await Check("offset_csv_same_active_path_is_verified_no_write_and_other_live_candidate_names_are_refused",async()=>
        {
            var(f,s,saved)=await Saved();var bytes=System.IO.File.ReadAllBytes(saved.FilePath);var at=System.IO.File.GetLastWriteTimeUtc(saved.FilePath);
            await ExportCopy(f,s,saved.FilePath);True(System.IO.File.ReadAllBytes(saved.FilePath).SequenceEqual(bytes)&&System.IO.File.GetLastWriteTimeUtc(saved.FilePath)==at);
            foreach(var name in new[]{"20991231_235959_999_reviewoffset.csv","custom_reviewoffset.csv"})
                await Reject(()=>ExportCopy(f,s,Path.Combine(Path.GetDirectoryName(saved.FilePath)!,name),true));
            True((await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
        });
        await Check("offset_csv_export_refuses_other_timestamp_history_even_with_overwrite_confirmation",async()=>
        {
            var(f,_,old)=await Saved();var newer=await f.Offsets.SaveNew("QA",old.Rows);var s=await f.Backend.LoadAsync(null,default);var bytes=System.IO.File.ReadAllBytes(old.FilePath);
            await Reject(()=>ExportCopy(f,s,old.FilePath,true));
            True(System.IO.File.ReadAllBytes(old.FilePath).SequenceEqual(bytes)&&(await f.Offsets.LoadLatest("QA")).FilePath==newer.FilePath);
        });
        await Check("offset_csv_export_external_timestamp_name_is_allowed_without_modifying_live_latest",async()=>
        {
            var(f,s,saved)=await Saved();var target=Path.Combine(f.Root,"user files",saved.FileName);await ExportCopy(f,s,target);
            True(System.IO.File.Exists(target)&&(await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
        });
        await Check("offset_csv_custom_overwrite_requires_explicit_confirmation_and_replaces_only_copy",async()=>
        {
            var(f,s,saved)=await Saved();var target=Path.Combine(f.Root,"custom.csv");await System.IO.File.WriteAllTextAsync(target,"previous user copy");
            await Reject(()=>ExportCopy(f,s,target));True(await System.IO.File.ReadAllTextAsync(target)=="previous user copy");
            await ExportCopy(f,s,target,true);True(System.IO.File.ReadAllBytes(target).SequenceEqual(System.IO.File.ReadAllBytes(saved.FilePath)));
        });
        await Check("offset_csv_locked_custom_overwrite_fails_atomically_and_cleans_temporary_copy",async()=>
        {
            var(f,s,saved)=await Saved();var target=Path.Combine(f.Root,"locked.csv");await System.IO.File.WriteAllTextAsync(target,"previous retained copy");
            bool failed=false;
            using(var held=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.None))
                try{await ExportCopy(f,s,target,true);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){failed=true;}
            True(failed&&await System.IO.File.ReadAllTextAsync(target)=="previous retained copy"&&!Directory.GetFiles(f.Root,"*.tmp").Any());
            True((await f.Offsets.LoadLatest("QA")).ContentSha256==saved.ContentSha256);
        });
        await Check("offset_csv_export_protects_config_data_state_and_other_project_destinations",async()=>
        {
            var(f,s,saved)=await Saved();var other=Path.Combine(f.Root,"other project");Directory.CreateDirectory(Path.Combine(other,".git"));
            foreach(var target in new[]{Path.Combine(f.Root,"Config","replace.csv"),Path.Combine(f.Offsets.RootPath,"OTHER_reviewoffset","replace.csv"),
                Path.Combine(f.Results.RootPath,"replace.csv"),Path.Combine(f.Backend.StateDirectory,"replace.csv"),Path.Combine(other,"replace.csv")})
            {await Reject(()=>ExportCopy(f,s,target,true));True(!System.IO.File.Exists(target));}
            await Reject(()=>ExportCopy(f,s,Path.Combine(f.Root,"wrong.json")));
        });
        await Check("offset_csv_export_parent_junction_cannot_bypass_operating_folder_guards",async()=>
        {
            var(f,s,_)=await Saved();var link=Path.Combine(f.Root,"linked folder");var data=Path.Combine(f.Root,"Data");Directory.CreateDirectory(data);
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
            {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var value in new[]{"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:A3_CSV_LINK -Target $env:A3_CSV_TARGET | Out-Null"})start.ArgumentList.Add(value);
            start.Environment["A3_CSV_LINK"]=link;start.Environment["A3_CSV_TARGET"]=data;
            using var process=Process.Start(start)!;await process.WaitForExitAsync();True(process.ExitCode==0);
            await Reject(()=>ExportCopy(f,s,Path.Combine(link,"escaped.csv"),true));True(!System.IO.File.Exists(Path.Combine(data,"escaped.csv")));
        });
        await Check("offset_csv_export_cancel_or_model_switch_leaves_no_new_or_half_written_file",async()=>
        {
            var(f,s,_)=await Saved();var target=Path.Combine(f.Root,"cancel.csv");using var cancel=new CancellationTokenSource();cancel.Cancel();
            bool cancelled=false;try{await f.Backend.ExportCurrentOffsetCsvAsync(s,target,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            True(cancelled&&!System.IO.File.Exists(target));
            f.Selected="OTHER";await Reject(()=>ExportCopy(f,s,target));True(!System.IO.File.Exists(target)&&!Directory.GetFiles(f.Root,"*.tmp").Any());
        });
        await Check("offset_csv_mid_copy_model_switch_or_cancel_preserves_existing_copy_and_removes_temporary",async()=>
        {
            foreach(var cancelDuringCopy in new[]{false,true})
            {
                var(f,_,saved)=await Saved();var directory=Path.Combine(f.Root,"during copy");Directory.CreateDirectory(directory);
                var destination=Path.Combine(directory,"chosen.csv");await System.IO.File.WriteAllTextAsync(destination,"existing user copy retained");
                var original=System.IO.File.ReadAllBytes(saved.FilePath);using var cancellation=new CancellationTokenSource();var triggered=false;
                string Selected()
                {
                    // Use the real final EnsureCurrent callback. No production hook or timing race:
                    // the source stays stable until the exporter has created its temporary copy.
                    if(!triggered && Directory.GetFiles(directory,"*.tmp").Length>0)
                    {
                        triggered=true;
                        if(cancelDuringCopy)cancellation.Cancel();else f.Selected="OTHER";
                    }
                    return f.Selected;
                }
                var backend=new CManualInspectionBackend(f.Review,new Drilling.Common.Managers.CRecipeManager(new RecipesFile(f)),
                    f.Settings,f.Results,f.Offsets,Selected,null,()=>{});
                var source=await backend.LoadAsync(null,default);var refused=false;
                try{await backend.ExportCurrentOffsetCsvAsync(source,destination,cancellation.Token,overwriteConfirmed:true);}
                catch(OperationCanceledException) when(cancelDuringCopy){refused=true;}
                catch(InvalidOperationException) when(!cancelDuringCopy){refused=true;}
                True(triggered&&refused&&await System.IO.File.ReadAllTextAsync(destination)=="existing user copy retained");
                True(Directory.GetFiles(directory,"*.tmp").Length==0&&System.IO.File.ReadAllBytes(saved.FilePath).SequenceEqual(original));
                True((await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
            }
        });
        await Check("offset_csv_export_without_saved_file_does_not_invent_measured_zero_csv",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);var target=Path.Combine(f.Root,"empty.csv");
            await Reject(()=>ExportCopy(f,s,target));True(!System.IO.File.Exists(target));
        });
        await Check("offset_csv_roundtrip_import_is_absolute_sparse_and_preserves_measured_zero_vs_init",async()=>
        {
            var(f,s,saved)=await Saved();var target=Path.Combine(f.Root,"renamed.csv");await ExportCopy(f,s,target);
            var imported=await f.Backend.ImportOffsetBundleAsync(s,target,default);
            var a=At(s,"A1");var zero=At(s,"B1");var init=At(s,"A2");var missing=At(s,"B2");
            True(imported[a.Key]==new ST_MANUAL_OFFSET(12,-23)&&imported[zero.Key]==ST_MANUAL_OFFSET.Zero);
            True(!f.Backend.ImportedInitializedOffsetKeys.Contains(zero.OffsetKey)&&f.Backend.ImportedInitializedOffsetKeys.Contains(init.OffsetKey)&&f.Backend.ImportedInitializedOffsetKeys.Contains(missing.OffsetKey));
            True(f.Backend.ImportedOffsetDescription.Contains("모델 정보 없음")&&f.Backend.ImportedOffsetDescription.Contains("모델 QA")&&f.Backend.ImportedOffsetDescription.Contains("초안"));
            True((await f.Offsets.LoadLatest("QA")).ContentSha256==saved.ContentSha256);
        });
        await Check("offset_csv_legacy_scanner_rows_convert_once_without_reapplying_scale_or_current_B",async()=>
        {
            var f=new Fixture(settingOverrides:new(){["REVIEW_OFFSET_SCALE"]="200"});var s=await f.Backend.LoadAsync(null,default);
            var input=Path.Combine(f.Root,"legacy.csv");await System.IO.File.WriteAllTextAsync(input,"CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY,COORDINATE_SPACE\n1,A1,0.012,-0.023,SCANNER\n");
            var values=await f.Backend.ImportOffsetBundleAsync(s,input,default);var a=At(s,"A1");True(values[a.Key]==new ST_MANUAL_OFFSET(12,23));
            True((await f.Offsets.LoadLatest("QA")).Rows.Count==0);
        });
        await Check("offset_csv_import_rejects_named_foreign_model_and_unknown_or_changed_hidden_rows",async()=>
        {
            var(f,s,saved)=await Saved();var foreign=await f.Offsets.SaveNew("OTHER",[new(1,"A1",.012,-.023){IsDesignCoordinate=true}]);
            await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,foreign.FilePath,default));
            foreach(var tail in new[]{"100,Z99,0.001,0.002,DESIGN,MEASURED,","99,Z99,0.999,-0.345678,SCANNER,MEASURED,"})
            {
                var target=Path.Combine(f.Root,"foreign.csv");await System.IO.File.WriteAllTextAsync(target,"CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY,COORDINATE_SPACE,MEASUREMENT_STATE,MEASUREMENT_INITIALIZED_AT\n1,A1,0.012,-0.023,DESIGN,MEASURED,\n"+tail+"\n");
                await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,target,default));
            }
            True((await f.Offsets.LoadLatest("QA")).ContentSha256==saved.ContentSha256);
        });
        await Check("offset_csv_import_rejects_invalid_header_duplicates_nan_space_and_init_nonzero",async()=>
        {
            var f=new Fixture();var s=await f.Backend.LoadAsync(null,default);var target=Path.Combine(f.Root,"bad.csv");
            foreach(var text in new[]{"wrong,header\n1,2\n",
                "CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY\n1,A1,0,0\n1,A1,1,2\n",
                "CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY\n1,A1,0,0\n1,A-1,1,2\n",
                "CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY\n1,A1,NaN,0\n",
                "CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY,COORDINATE_SPACE\n1,A1,1,2,STAGE\n",
                "CELLID,SHOTID,REVIEWOFFSETX,REVIEWOFFSETY,MEASUREMENT_STATE\n1,A1,1,0,INITIALIZED\n"})
            {await System.IO.File.WriteAllTextAsync(target,text);await Reject(()=>f.Backend.ImportOffsetBundleAsync(s,target,default));}
        });
        await Check("offset_csv_reader_wrapper_matches_actual_latest_reader_without_publishing_or_reformatting",async()=>
        {
            var(f,_,saved)=await Saved();var actual=await f.Offsets.LoadLatest("QA");var raw=CReviewOffsetFile.ReadSnapshotFile("QA",saved.FilePath);
            True(raw.ContentSha256==actual.ContentSha256&&raw.Rows.SequenceEqual(actual.Rows)&&raw.HasMeasurementStateMetadata==actual.HasMeasurementStateMetadata);
            True((await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
        });
        await Check("offset_csv_json_bundle_import_remains_compatible_and_validates_original_identity",async()=>
        {
            var(f,s,_)=await Saved();var path=Path.Combine(f.Root,"old compatible.json");await f.Backend.ExportOffsetBundleAsync(s,s.Values,path,false,false,default);
            var imported=await f.Backend.ImportOffsetBundleAsync(s,path,default);True(imported.All(pair=>pair.Value==s.Values[pair.Key])&&f.Backend.ImportedOffsetDescription.Contains("JSON"));
        });
    }
}
