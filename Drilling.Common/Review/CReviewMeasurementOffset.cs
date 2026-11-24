using Drilling.Common.Recipe;

namespace Drilling.Common.Review;

/// <summary>Coordinate boundary for measurement-derived Review increments, always in mm.
/// Vision Flip has already been applied to the Stage error. Stored DESIGN Y follows the
/// recipe direction, so remove StageScanDirectionY here; scanner/Head transforms and
/// REVIEW_OFFSET_SCALE are applied once later when the saved value is consumed.
/// This conversion never changes a previously saved absolute B or a raw Vision packet.</summary>
public static class CReviewMeasurementOffset
{
    public static ST_REVIEW_COORDINATE_OFFSET StageToDesign(
        IReadOnlyDictionary<string,string> parameters,double stageXmm,double stageYmm)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if(!double.IsFinite(stageXmm)||!double.IsFinite(stageYmm))
            throw new InvalidOperationException("측정 Review 보정량이 유효한 mm 값이 아닙니다.");
        return new(stageXmm,stageYmm/CScannerAxisTransform.ReadStageDirection(parameters));
    }
}
