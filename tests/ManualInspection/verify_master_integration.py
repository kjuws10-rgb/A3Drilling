"""Verify the 11.24.2 integration without starting the equipment application.

Keep three different baselines: the current master, the imported Manual Inspection
tip, and their common ancestor. Comparing whole trees against the feature alone
would incorrectly treat master's operational files as deletions to import.
"""
import argparse
import hashlib
import json
import subprocess
import tempfile
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
MASTER = "0219666f9053b907d96d7e0aa7d0c6c1be3a73d4"
FEATURE = "fb869deb2e02b695c8fd39d57bbd01f80932e5c7"
VERSION = "11.24.2"
VERSION_FILES = {
    "Drilling.UI/Drilling.UI.csproj",
    "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs",
}
MONITOR = "Drilling.UI/Menu/Menus/CMenuMonitor.cs"


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def text(data):
    return data.decode("utf-8-sig").replace("\r\n", "\n")


def at(commit, path):
    return text(git("show", f"{commit}:{path}"))


def current(path):
    return text((ROOT / path).read_bytes())


def names(*args):
    return set(text(git(*args)).splitlines())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    checks = []

    def check(name, value):
        checks.append({"name": name, "passed": bool(value)})

    ancestor = text(git("merge-base", MASTER, FEATURE)).strip()
    master_changes = names("diff", "--name-only", ancestor, MASTER)
    feature_changes = names("diff", "--name-only", ancestor, FEATURE)
    changed = names("diff", "--name-only", MASTER, "--") | names("ls-files", "--others", "--exclude-standard")
    production = sorted(p for p in feature_changes if p.startswith(("Drilling.Common/", "Drilling.File/", "Drilling.UI/")))
    check("master_and_feature_have_expected_common_ancestor", ancestor == "334b3599651b2e8e0181fd4459bafaf3cddafff3")
    check("all_changes_are_manual_sources_or_dedicated_tests_and_docs", all(p in production or p.startswith(("tests/ManualInspection/", "docs/ManualInspection/")) for p in changed))
    check("master_only_files_not_modified_or_removed", not ((master_changes - feature_changes) & changed))
    check("master_configuration_data_vendor_and_global_theme_unchanged", not any(p.startswith(("Config/", "Data/", "Drilling.Common/Interface/", "Drilling.Common/Motion/", "Drilling.UI/Themes/")) or p.endswith((".dll", ".exe")) for p in changed))
    check("every_imported_feature_file_is_present", all((ROOT / p).is_file() for p in feature_changes))
    check("daily_program_version", ET.parse(ROOT / "Drilling.UI/Drilling.UI.csproj").findtext(".//Version") == "2026." + VERSION)
    for p in production:
        if p not in VERSION_FILES and p != MONITOR:
            check("imported_implementation_exact:" + p, current(p) == at(FEATURE, p))
    check("portable_schema_only_version_changed", current("Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs") == at(FEATURE, "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs").replace('ProgramVersion = "11.24.1"', 'ProgramVersion = "11.24.2"'))
    check("project_only_version_changed", current("Drilling.UI/Drilling.UI.csproj") == at(FEATURE, "Drilling.UI/Drilling.UI.csproj").replace("2026.11.24.1", "2026.11.24.2"))
    # Independently reproduce the textual three-way result outside the repository.
    # merge-file reads C# as text; it never executes or compiles the application.
    with tempfile.TemporaryDirectory(prefix="A3ManualMerge-") as directory:
        files = [Path(directory) / s for s in ("master.cs", "base.cs", "feature.cs")]
        for p, commit in zip(files, (MASTER, ancestor, FEATURE)):
            p.write_bytes(at(commit, MONITOR).encode("utf-8"))
        merged = subprocess.run(["git", "merge-file", "-p", *(str(p) for p in files)], cwd=ROOT, capture_output=True)
        check("monitor_reproduces_conflict_free_three_way_merge", merged.returncode == 0 and text(merged.stdout) == current(MONITOR))
    monitor = current(MONITOR)
    check("monitor_master_melsec_product_and_manual_review_paths_coexist", all(s in monitor for s in ("SelectedMelsecGroupOption", "RefreshMelsecNow", "ShowGlassDataSourceChoice", "EN_GLASS_DATA_SOURCE_CHOICE.StagePcData", "RefreshCoordinateReviewOffsets", "CReviewOffsetDisplaySnapshot.Create", "BuildCoordinateReviewOffsetDescription")))
    check("manual_host_and_navigation_included", "new CManualInspectionController" in current("Drilling.UI/Menu/Menus/CMenuCorrection.cs") and "ManualInspection" in current("Drilling.UI/Views/CCorrectionView.xaml"))
    check("all_review_init_missing_value_semantics_included", current("Drilling.UI/Menu/Menus/CMenuCorrection.ReviewData.Offset.cs") == at(FEATURE, "Drilling.UI/Menu/Menus/CMenuCorrection.ReviewData.Offset.cs"))
    check("no_unresolved_merge_conflicts", not names("diff", "--name-only", "--diff-filter=U"))
    check("diff_check", subprocess.run(["git", "diff", MASTER, "--check"], cwd=ROOT, capture_output=True).returncode == 0)
    result = {
        "passed": all(c["passed"] for c in checks), "count": len(checks),
        "programVersion": VERSION, "masterCommit": MASTER, "featureCommit": FEATURE,
        "commonAncestor": ancestor, "masterChangedFiles": len(master_changes),
        "masterOnlyFilesPreserved": len(master_changes - feature_changes),
        "featureFilesImported": len(feature_changes), "importedProductionFileCount": len(production),
        "changedPaths": sorted(changed), "checks": checks,
        "productionSha256": {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in production},
        "scope": "Git/source/layout contracts only. No equipment execution, vendor loading or operating-record publication.",
    }
    path = Path(args.report)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("passed", "count", "masterOnlyFilesPreserved", "featureFilesImported", "importedProductionFileCount")}, ensure_ascii=False))
    for c in checks:
        if not c["passed"]:
            print("FAIL", c["name"])
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
