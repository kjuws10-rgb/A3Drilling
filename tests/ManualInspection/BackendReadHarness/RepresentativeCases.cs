using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static async Task RunRepresentativeChecks()
    {
        await Check("native_reference_base_evidence_distinguishes_missing_zero_stored_zero_and_init",async()=>
        {
            var f=new Fixture();var missing=await f.Backend.LoadAsync(null,default);var first=missing.Points.First(p=>!p.Masked);
            True(missing.Points.All(p=>!p.HasAppliedOffsetEvidence));await Reject(()=>Task.Run(()=>Engine(missing).ResolveReferenceFinal(first.Key)));
            var plan=f.Review.CreateAllPlan(f.Recipe,11);var target=plan.Points.Single(p=>p.CellNo==first.CellNo&&p.ShotNo==first.ShotNo);
            var name=CReviewShotNameFormatter.ToMatrixName(target.ShotNo,target.PixelCountX);
            await f.Offsets.SaveNew("QA",[new(first.CellNo,name,0,0){IsDesignCoordinate=true}]);
            var stored=await f.Backend.LoadAsync(null,default);var known=stored.Points.Single(p=>p.Key==first.Key);
            True(known.HasAppliedOffsetEvidence&&!known.IsMeasurementOffsetInitialized&&known.Judge=="UNMEASURED");
            var value=Engine(stored).ResolveReferenceValue(known.Key);True(value.Final==ST_MANUAL_OFFSET.Zero&&value.Judge=="UNMEASURED"&&value.ErrorXUm is null);
            await f.Offsets.SaveNew("QA",[new(first.CellNo,name,0,0){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=true}]);
            var initialized=await f.Backend.LoadAsync(null,default);True(initialized.Points.All(p=>!p.HasAppliedOffsetEvidence));
            await Reject(()=>Task.Run(()=>Engine(initialized).ResolveReferenceFinal(first.Key)));
        });
        await Check("native_unadopted_sim_file_cannot_be_laundered_through_representative_copy",async()=>
        {
            var(f,path)=await ExplicitFile("MEASURED",simulation:true);var source=await f.Backend.LoadAsync(path,default);
            var imported=source.Points.First(p=>p.Source=="IMPORTED");True(imported.HasFiniteError&&imported.AdoptedFileMeasurement is null&&!imported.HasAppliedOffsetEvidence);
            var engine=Engine(source);await Reject(()=>Task.Run(()=>engine.ResolveReferenceFinal(imported.Key)));
            var fresh=new ST_MANUAL_MEASUREMENT("OK",0,0,"SYNTHETIC-CURRENT-RAW",source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature);
            engine.RecordMeasurement(imported.Key,fresh);True(engine.ResolveReferenceFinal(imported.Key)==ST_MANUAL_OFFSET.Zero);
        });
        await Check("native_adopted_file_can_supply_value_without_prior_candidate_validation",async()=>
        {
            var(f,path)=await ExplicitFile("MEASURED",simulation:false);var source=await f.Backend.LoadAsync(path,default);
            var imported=source.Points.First(p=>p.Source=="IMPORTED");True(imported.AdoptedFileMeasurement is not null);
            var value=Engine(source).ResolveReferenceValue(imported.Key);True(value.Kind=="MEASURED"&&value.Final.IsFinite);
            await Reject(()=>f.Backend.ExecutePointAsync(source,imported.Key,EN_MANUAL_OPERATION_KIND.MoveMeasure,_=>{},default));
        });
        await Check("native_unassigned_head_legacy_scanner_row_does_not_claim_authoritative_design_evidence",async()=>
        {
            // Keep the real planner's minimum enabled-Head prerequisite. This enabled Head is
            // deliberately outside the synthetic Cell, so its points are truthfully unassigned.
            var f=new Fixture(settingOverrides:new(){["H01_AK_POSITION_X"]="1000",["H01_SCAN_FIELD_WIDTH_X"]="1"});var plan=f.Review.CreateAllPlan(f.Recipe,11);
            var target=plan.Points.First(p=>p.HeadNo==0);var name=CReviewShotNameFormatter.ToMatrixName(target.ShotNo,target.PixelCountX);
            await f.Offsets.SaveNew("QA",[new(target.CellNo,name,.004,.002){IsDesignCoordinate=false}]);
            var source=await f.Backend.LoadAsync(null,default);var point=source.Points.Single(p=>p.CellNo==target.CellNo&&p.ShotNo==target.ShotNo);
            True(point.HeadNo==0&&point.Masked&&!point.HasAppliedOffsetEvidence);
            await Reject(()=>Task.Run(()=>Engine(source).ResolveReferenceFinal(point.Key)));
        });
        await Check("native_assigned_head4_legacy_scanner_row_keeps_its_actual_axis_conversion",async()=>
        {
            var f=new Fixture(settingOverrides:new(){["H01_USE"]="OFF",["H04_USE"]="ON",["H01_AK_POSITION_X"]="-330",["H04_SCANNER_X_FLIP"]="ON"});
            var plan=f.Review.CreateAllPlan(f.Recipe,11);var target=plan.Points.First(p=>p.HeadNo==4);
            var name=CReviewShotNameFormatter.ToMatrixName(target.ShotNo,target.PixelCountX);
            await f.Offsets.SaveNew("QA",[new(target.CellNo,name,.004,.002){IsDesignCoordinate=false}]);
            var source=await f.Backend.LoadAsync(null,default);var point=source.Points.Single(p=>p.CellNo==target.CellNo&&p.ShotNo==target.ShotNo);
            var value=Engine(source).ResolveReferenceValue(point.Key);
            True(point.HeadNo==4&&point.HasAppliedOffsetEvidence&&Math.Abs(value.Final.XUm+4)<1e-9&&Math.Abs(value.Final.YUm+2)<1e-9);
        });
    }
}
