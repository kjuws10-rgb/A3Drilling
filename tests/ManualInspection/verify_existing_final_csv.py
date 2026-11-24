"""Verify existing-CSV startup/display and scoped Init recovery without equipment or data writes."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = "b9659de850691ff677a1e7caf2093c499cd42f28"
SOURCES = {
    "Drilling.UI/Drilling.UI.csproj",
    "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs",
    "Drilling.UI/Views/CManualInspectionView.xaml",
    "Drilling.UI/Views/CManualInspectionView.xaml.cs",
    "Drilling.UI/CRootView.cs",
    "Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionController.cs",
}


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def text(data):
    return data.decode("utf-8-sig").replace("\r\n", "\n")


def read(path):
    return text((ROOT / path).read_bytes())


def old(path):
    return text(git("show", f"{BASE}:{path}"))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--report", required=True)
    args = p.parse_args()
    checks = []

    def check(name, value):
        checks.append({"name": name, "passed": bool(value)})

    paths = set(text(git("diff", "--name-only", BASE)).splitlines()) | set(text(git("ls-files", "--others", "--exclude-standard")).splitlines())
    check("only_startup_display_init_sources_and_dedicated_tests_docs", all(path in SOURCES or path.startswith(("tests/ManualInspection/", "docs/ManualInspection/")) for path in paths))
    check("existing_csv_config_operating_data_vendor_themes_unchanged", not any(path.startswith(("Config/", "Data/", "Drilling.Common/Interface/", "Drilling.Common/Motion/", "Drilling.UI/Themes/")) or path.endswith((".dll", ".exe")) for path in paths))
    for path in ["Drilling.File/ReviewOffset/CReviewOffsetFile.cs", "Drilling.UI/CAppStartup.cs", "Drilling.UI/Menu/Menus/CManualInspectionBackend.cs", "Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs", "Drilling.UI/Views/CManualInspectionUiContract.cs", "Drilling.UI/Menu/Menus/CMenuCorrection.cs", "Drilling.UI/Menu/Menus/CMenuRecipe.cs", "Drilling.UI/Menu/Menus/CMenuMonitor.cs", "Drilling.Common/Station/CStationProcess.cs", "Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs", "Drilling.Common/Review/CManualInspectionCoordinateBridge.cs"]:
        check("unchanged:" + path, old(path) == read(path))
    for path in ["Drilling.UI/Drilling.UI.csproj", "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs"]:
        check("version_only:" + path, old(path).replace("11.24.3", "11.24.4") == read(path))
    check("program_version_11_24_4", ET.parse(ROOT / "Drilling.UI/Drilling.UI.csproj").findtext(".//Version") == "2026.11.24.4")
    view = read("Drilling.UI/Views/CManualInspectionView.xaml.cs")
    check("default_map_is_saved_csv", 'private string _color = "applied";' in view and 'new("applied", "최종 Review 보정 · Level")], "applied")' in view)
    check("default_scatter_is_saved_csv", 'private bool _scatterFinalOffsets = true;' in view and '"저장된 최종 Review 보정")], "FINAL_OFFSETS")' in view)
    check("model_reset_uses_saved_csv", '_color = "applied"; _scatterScope = "HEAD";' in view)
    check("valid_measurement_view_bookmarks_remain_available", 'bookmark.Color is "level" or "judge" or "applied" ? bookmark.Color : "applied"' in view)
    check("file_badge_has_name_absolute_path_hash_and_failure_state", all(t in view for t in ["FinalCsvSourceText", "FinalOffsetFileName", "FinalOffsetFilePath", "FinalOffsetContentSha256", "CSV 읽기 확인 필요"]))
    projection = lambda s: s.split("private CManualInspectionUiPoint FinalOffsetPoint", 1)[1].split("private void RefreshFinalOffsetInfo", 1)[0]
    check("saved_value_projection_and_unit_mapping_unchanged", projection(old("Drilling.UI/Views/CManualInspectionView.xaml.cs")) == projection(view))
    root = read("Drilling.UI/CRootView.cs")
    startup = root.split("private async Task InitializeProcessModel(", 1)[1].split("private async Task PrepareInitialProcess(", 1)[0]
    before = old("Drilling.UI/CRootView.cs")
    prefix, suffix = "                // Resolve the applied Recipe/Product first.", "            }\n            catch (OperationCanceledException)when"
    added = root.split(prefix, 1)[1].split(suffix, 1)[0]
    check("startup_host_only_adds_read_after_prepared_recipe", root.replace(prefix + added, "", 1) == before
          and startup.index("await PrepareInitialProcess(") < startup.index("await menu.ManualInspection.EnsureLoaded("))
    check("startup_read_failure_is_separate_and_cancellation_propagates", 'catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }' in startup
          and 'Manual Inspection saved offset load needs attention' in startup and 'Debug.WriteLine(' in startup)
    xaml = ET.parse(ROOT / "Drilling.UI/Views/CManualInspectionView.xaml")
    ns = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
    controls = [e for e in xaml.iter() if e.attrib.get(ns) == "FinalCsvSourceButton"]
    check("single_badge_uses_existing_display_only_handler", len(controls) == 1 and controls[0].attrib.get("Click") == "ShowFinalOffsets_Click")
    check("no_unresolved_merge_conflicts", not text(git("diff", "--name-only", "--diff-filter=U")).strip())
    check("diff_check", subprocess.run(["git", "diff", BASE, "--check"], cwd=ROOT, capture_output=True).returncode == 0)
    out = {"passed": all(c["passed"] for c in checks), "count": len(checks), "baseCommit": BASE, "programVersion": "11.24.4", "checks": checks, "changedPaths": sorted(paths), "productionSha256": {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in sorted(SOURCES)}, "scope": "Resolved startup recipe read, saved-value defaults/provenance and scoped Init validation/failure recovery. Existing CSV reader/writer, coordinate conversion and operating files are preserved. Init behavioral tests and independent source review are recorded separately."}
    report = Path(args.report)
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(out, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": out["passed"], "checks": len(checks), "changedPaths": len(paths)}))
    for c in checks:
        if not c["passed"]:
            print("FAIL", c["name"])
    return 0 if out["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
