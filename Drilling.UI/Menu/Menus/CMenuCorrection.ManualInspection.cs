using Drilling.Common.Review;

namespace Drilling.UI.Menu.Menus;

public sealed partial class CMenuCorrection
{
    /// <summary>Only the new tab uses this controller; the existing SIMPLE/INSPECTION panels remain independent.</summary>
    public CManualInspectionController ManualInspection { get; }
    public Func<string?> ManualInspectionBlockReason { get; set; } = () => "MANUAL/PM 운전 조건이 연결되지 않았습니다.";

    private void GuardManualInspection()
    {
        var reason = ManualInspectionBlockReason();
        if (!string.IsNullOrEmpty(reason)) throw new InvalidOperationException(reason);
        if (!CheckOperationAlarm("MANUAL INSPECTION")) throw new InvalidOperationException("현재 알람으로 Manual Inspection을 실행할 수 없습니다.");
        if (Review?.BlocksMenuNavigation == true || _isReviewOperationRunning || DefaultOffset.IsBusy
            || _roughCancellation is { IsCancellationRequested: false }
            || _fineCancellation is { IsCancellationRequested: false }
            || _defaultCancellation is { IsCancellationRequested: false })
            throw new InvalidOperationException("다른 Correction/Review 동작을 완료하거나 정지 확인 후 실행하세요.");
        if (_defaultManager?.PowerMeasure.GetSnapshot().IsRunning == true)
            throw new InvalidOperationException("Power Measure 진행 중에는 Manual Inspection을 시작할 수 없습니다.");
    }
}
