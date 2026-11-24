using System.Globalization;
using System.IO;
using System.Reflection;
using Drilling.Common.Interface;
using Drilling.Common.Product;
using Drilling.File.Product;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task MasterCompatibilityCases()
    {
        static Dictionary<string,string> StageFields() => CStageGlassDataMap.ProductFieldNames.Select((name,index)=>(name,index))
            .ToDictionary(pair=>pair.name,pair=>pair.name switch
            {
                "H_GLASSID"=>"SYNTHETIC-GLASS", "LOTID"=>"SYNTHETIC-LOT", "PPID"=>"QA", "PRODUCTID"=>"PRODUCT-1",
                "JUDGEMENT"=>"NG", _=>"V"+pair.index.ToString(CultureInfo.InvariantCulture)
            },StringComparer.OrdinalIgnoreCase);
        // Invoke only the existing pure static mapper. No ProductManager, CMelsec, equipment
        // Manager, Root, App or device object is constructed by these compatibility checks.
        static ST_PRODUCT_INFO Info(IReadOnlyDictionary<string,string> fields,DateTimeOffset created,string ppid="")
            => (ST_PRODUCT_INFO)typeof(CProductManager).GetMethod("CreateProductInfo",BindingFlags.NonPublic|BindingFlags.Static)!
                .Invoke(null,[fields["H_GLASSID"],fields["LOTID"],"SYNTHETIC-PROCESS",ppid,fields,created])!;
        static ST_PRODUCT_DATA Product(ST_PRODUCT_INFO info) => new(info,
            new(new Dictionary<string,string>{{"CUSTOM_PROCESS_FACTOR","1.25"}}),
            new(EN_PRODUCT_PROCESS_STATE.NotStarted,"PENDING",null,null,null),
            new(EN_PRODUCT_PROCESS_STATE.NotStarted,"PENDING",null),
            new(EN_PRODUCT_PROCESS_STATE.NotStarted,null,[]){RecipeId="QA",BeamNo=11},new([]));
        static (CProductFile File,string Root) Store()
        {
            var root=Path.Combine(Path.GetTempPath(),"A3MasterCompatibilityTests",Guid.NewGuid().ToString("N"));
            return(new CProductFile(Path.Combine(root,"Config")),root);
        }
        var created=DateTimeOffset.Parse("2026-11-24T10:20:30+09:00",CultureInfo.InvariantCulture);
        await Check("master_stage_product_fields_roundtrip_preserves_ppid_and_separate_recipe_parameters",async()=>
        {
            var stage=StageFields();stage["UNTRUSTED_EXTRA"]="not-a-host-field";stage["PRODUCT_RESULT"]="OK";
            var snapshot=new ST_STAGE_GLASS_DATA_SNAPSHOT(stage,created);var info=Info(snapshot.Values,created);
            True(snapshot.GlassId=="SYNTHETIC-GLASS"&&snapshot.LotId=="SYNTHETIC-LOT"&&snapshot.Ppid=="QA");
            True(info.Get("PPID")==snapshot.Ppid&&info.Get("PRODUCT_RESULT")=="PENDING"&&info.Get("UNTRUSTED_EXTRA")=="");
            var(store,_)=Store();await store.SaveActive(Product(info));var loaded=(await store.LoadActive())!;
            True(CStageGlassDataMap.ProductFieldNames.All(name=>loaded.Product.Get(name)==stage[name]));
            True(loaded.GlassId==snapshot.GlassId&&loaded.RecipeId==snapshot.Ppid&&loaded.CreatedAt==created
                &&loaded.Parameters["CUSTOM_PROCESS_FACTOR"]=="1.25"&&!loaded.Parameters.ContainsKey("JUDGEMENT")&&!loaded.Parameters.ContainsKey("PPID"));
            True(Info(snapshot.Values,created,"SELECTED-OVERRIDE").Get("PPID")=="SELECTED-OVERRIDE");
        });
        await Check("master_product_result_and_stage_judgement_remain_distinct_after_actual_file_roundtrip",async()=>
        {
            foreach(var values in new[]{(Stage:"NG",Process:"OK",Expected:EN_PRODUCT_RESULT.OK),(Stage:"OK",Process:"NG",Expected:EN_PRODUCT_RESULT.NG)})
            {
                var stage=StageFields();stage["JUDGEMENT"]=values.Stage;
                var product=Product(Info(stage,created).Set("PRODUCT_RESULT",values.Process).Set("PRODUCT_STATE","COMPLETED"));
                var(store,_)=Store();await store.SaveActive(product);var loaded=(await store.LoadActive())!;
                True(loaded.Result==values.Expected&&loaded.Product.Get("JUDGEMENT")==values.Stage&&loaded.Product.Get("PRODUCT_RESULT")==values.Process&&loaded.State==EN_PRODUCT_STATE.Completed);
                await store.SaveActive(loaded);True((await store.LoadActive())!.Result==values.Expected);
            }
        });
        await Check("master_legacy_judgement_only_active_product_fallback_survives_new_writer",async()=>
        {
            var(store,root)=Store();var directory=Path.Combine(root,"Data","Product");Directory.CreateDirectory(directory);
            await System.IO.File.WriteAllTextAsync(Path.Combine(directory,"ActiveProduct.csv"),
                "TYPE,NAME,VALUE\r\nPRODUCT,H_GLASSID,LEGACY-SYNTHETIC\r\nPRODUCT,PPID,QA\r\nPRODUCT,JUDGEMENT,NG\r\nPRODUCT,PRODUCT_STATE,COMPLETED\r\nRECIPE,CUSTOM_PROCESS_FACTOR,1.25\r\n");
            var old=(await store.LoadActive())!;True(old.Result==EN_PRODUCT_RESULT.NG&&old.Product.Get("PRODUCT_RESULT")==""&&old.RecipeId=="QA");
            await store.SaveActive(old);var loaded=(await store.LoadActive())!;
            True(loaded.Result==EN_PRODUCT_RESULT.NG&&loaded.Product.Get("JUDGEMENT")=="NG"&&loaded.Parameters["CUSTOM_PROCESS_FACTOR"]=="1.25");
        });
        await Check("master_stage_product_roundtrip_retains_review_run_identity_and_separates_repeated_glass_runs",async()=>
        {
            var mapper=typeof(CManualInspectionBackend).GetMethod("CreateRunIdentity",BindingFlags.NonPublic|BindingFlags.Static)!;
            string Run(ST_PRODUCT_DATA p,string selected="QA") => (string)mapper.Invoke(null,
                [selected,p.ProductId,p.GlassId,p.RecipeId,p.CreatedAt,null,0d,0d])!;
            var first=Product(Info(StageFields(),created));var second=Product(Info(StageFields(),created.AddSeconds(1)));
            var(store,_)=Store();await store.SaveActive(first);var loadedFirst=(await store.LoadActive())!;
            True(Run(first)==Run(loadedFirst)&&loadedFirst.ProductId=="PRODUCT-1"&&loadedFirst.CreatedAt==created);
            await store.SaveActive(second);var loadedSecond=(await store.LoadActive())!;
            True(loadedFirst.GlassId==loadedSecond.GlassId&&loadedFirst.ProductId==loadedSecond.ProductId&&Run(loadedFirst)!=Run(loadedSecond));
            True(Run(loadedSecond,"OTHER-SELECTED-RECIPE")!=Run(loadedSecond));
        });
    }
}
