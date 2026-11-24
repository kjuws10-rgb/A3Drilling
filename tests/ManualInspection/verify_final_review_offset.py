"""Check 11.24.3 scope and coordinate/storage/display boundaries without equipment."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = "7de760ccfe57ed75de3b8c9484c0b14114857be0"
ALLOWED = {
    "Drilling.Common/Review/CReviewMeasurementOffset.cs",
    "Drilling.Common/Review/CAutoFineReviewCoordinator.cs",
    "Drilling.Common/Review/CReviewManager.cs",
    "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs",
    "Drilling.File/ReviewOffset/CReviewOffsetFile.cs",
    "Drilling.UI/Drilling.UI.csproj",
    "Drilling.UI/Menu/Menus/CManualInspectionBackend.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionController.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs",
    "Drilling.UI/Menu/Menus/CMenuCorrection.cs",
    "Drilling.UI/Menu/Menus/CMenuCorrection.ReviewData.cs",
    "Drilling.UI/Menu/Menus/CMenuCorrection.ReviewData.Offset.cs",
    "Drilling.UI/Views/CManualInspectionUiContract.cs",
    "Drilling.UI/Views/CManualInspectionView.xaml",
    "Drilling.UI/Views/CManualInspectionView.xaml.cs",
}


def git(*a):
    return subprocess.check_output(["git", *a], cwd=ROOT)


def txt(b):
    return b.decode("utf-8-sig").replace("\r\n", "\n")


def read(p):
    return txt((ROOT / p).read_bytes())


def old(p):
    return txt(git("show", f"{BASE}:{p}"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    checks = []

    def check(name, value):
        checks.append({"name": name, "passed": bool(value)})

    paths = set(txt(git("diff", "--name-only", BASE)).splitlines()) | set(txt(git("ls-files", "--others", "--exclude-standard")).splitlines())
    check("only_authorized_review_integration_sources_tests_and_docs", all(p in ALLOWED or p.startswith(("tests/ManualInspection/", "docs/ManualInspection/")) for p in paths))
    check("configuration_data_vendor_hardware_and_global_themes_unchanged", not any(p.startswith(("Config/", "Data/", "Drilling.Common/Interface/", "Drilling.Common/Motion/", "Drilling.UI/Themes/")) or p.endswith((".dll", ".exe")) for p in paths))
    check("program_version_11_24_3", ET.parse(ROOT / "Drilling.UI/Drilling.UI.csproj").findtext(".//Version") == "2026.11.24.3")
    check("portable_version_11_24_3", 'ProgramVersion = "11.24.3"' in read("Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs"))
    for p in ["Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs", "Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs", "Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs", "Drilling.Common/Review/CManualInspectionCoordinateBridge.cs", "Drilling.Common/Review/CManualInspectionResultProjection.cs", "Drilling.File/ReviewResult/CReviewResultFile.cs", "Drilling.UI/CRootView.cs", "Drilling.UI/Menu/Menus/CMenuRecipe.cs", "Drilling.UI/Menu/Menus/CMenuMonitor.cs", "Drilling.UI/Views/CManualInspectionMapControl.cs", "Drilling.UI/Views/CManualInspectionScatterControl.cs", "Drilling.Common/Station/CStationProcess.cs"]:
        check("unchanged:" + p, old(p) == read(p))
    # Fine still returns Stage-mm increments; conversion occurs once at each save boundary.
    manager = "Drilling.Common/Review/CReviewManager.cs"
    def fine_body(s):
        return s.split("public static IReadOnlyList<ST_FINE_OFFSET_DELTA> CalculateFineOffsets", 1)[1].split("private static double InterpolateFineOffsetColumn", 1)[0]
    check("fine_stage_delta_contract_unchanged", fine_body(old(manager)) == fine_body(read(manager)))
    helper = read("Drilling.Common/Review/CReviewMeasurementOffset.cs")
    check("single_stage_to_design_direction_boundary", "stageYmm/CScannerAxisTransform.ReadStageDirection(parameters)" in helper and "double.IsFinite" in helper)
    for p in ["Drilling.Common/Review/CAutoFineReviewCoordinator.cs", "Drilling.UI/Menu/Menus/CMenuCorrection.ReviewData.Offset.cs", "Drilling.UI/Menu/Menus/CMenuCorrection.cs"]:
        check("shared_design_conversion:" + p, "CReviewMeasurementOffset.StageToDesign" in read(p))
    store = read("Drilling.File/ReviewOffset/CReviewOffsetFile.cs")
    check("active_store_location_and_order_unchanged", 'return Path.Combine(_recipeRoot, $"{SanitizeFileName(recipeId)}_reviewoffset")' in store and '"*_reviewoffset.csv", SearchOption.TopDirectoryOnly' in store)
    check("conditional_save_checks_expected_basis_inside_store_lock", all(x in store for x in ["SaveNewIfLatestUnchanged", "SaveNewFromSourceIfUnchanged", "VerifyExpectedBasis", "AcquireProcessLease"]))
    host = read("Drilling.UI/Menu/Menus/CMenuCorrection.cs")
    check("completion_notify_is_read_only_and_exception_isolated", "RefreshManualInspectionAfterReview" in host and "NotifyExternalReviewChangedAsync" in host and "Trace.TraceWarning" in host)
    check("fine_and_rough_confirm_physical_residual_prerequisite", "같은 가공 흔적" in host and "새로 가공한 결과의 잔차" in host)
    controller = read("Drilling.UI/Menu/Menus/CManualInspectionController.cs")
    check("saved_projection_and_export_are_distinct_from_working_candidate", all(x in controller for x in ["RefreshFinalOffsets", "ExportFinalOffsets", "FinalOffsetPoints", "FinalOffsetRevisionId"]))
    backend = read("Drilling.UI/Menu/Menus/CManualInspectionBackend.cs")
    check("all_declared_result_roots_watched", "SearchRootPaths" in backend and "WatchRoots" in backend)
    check("stateless_saved_reader_and_current_basis_export", "ReadSavedState" in backend and "ExportFinalOffsetBundleAsync" in backend and "await reader.EnsureCurrentAsync(current,token)" in backend)
    view = read("Drilling.UI/Views/CManualInspectionView.xaml.cs")
    check("final_map_table_and_distribution_are_explicit", all(x in view for x in ["FinalOffsetPoint", 'new("applied",', '"FINAL_OFFSETS"', "AppliedOffsetText", "최종 보정 파일 내보내기"]))
    check("final_view_has_filename_path_time_and_hash", all(x in view for x in ["FinalOffsetFileName", "FinalOffsetFilePath", "FinalOffsetSavedAt", "FinalOffsetContentSha256"]))
    check("no_merge_conflicts", not txt(git("diff", "--name-only", "--diff-filter=U")).strip())
    check("diff_check", subprocess.run(["git", "diff", BASE, "--check"], cwd=ROOT, capture_output=True).returncode == 0)
    result = {"passed": all(c["passed"] for c in checks), "count": len(checks), "baseCommit": BASE, "programVersion": "11.24.3", "checks": checks, "changedPaths": sorted(paths), "productionSha256": {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in sorted(ALLOWED) if (ROOT / p).is_file()}, "scope": "Source/storage/layout contracts. No machine, vendor SDK or real processing execution."}
    out = Path(args.report)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": result["passed"], "checks": len(checks), "changedPaths": len(paths)}))
    for c in checks:
        if not c["passed"]:
            print("FAIL", c["name"])
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
