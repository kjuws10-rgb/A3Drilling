"""Verify measurement-focused Manual Inspection scope and preservation. No equipment execution."""
import argparse
import hashlib
import json
import re
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

BASE = "d9c2779f888f1cc925c1515724a00038fe633bca"
ROOT = Path(__file__).resolve().parents[2]
EXISTING = {
    "Drilling.Common/Station/CStageReviewSequences.cs",
    "Drilling.File/ReviewResult/CReviewResultFile.cs",
    "Drilling.UI/CRootView.cs",
    "Drilling.UI/Views/CCorrectionView.xaml",
    "Drilling.UI/Views/CCorrectionView.xaml.cs",
    "Drilling.Common/Review/CManualInspectionCoordinateBridge.cs",
    "Drilling.Common/Review/CManualInspectionResultProjection.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs",
    "Drilling.UI/Menu/Menus/CMenuCorrection.ManualInspection.cs",
}
NEW = {
    "Drilling.Common/Review/CReviewManager.cs",
    "Drilling.File/ReviewOffset/CReviewOffsetFile.cs",
    "Drilling.UI/Menu/Menus/CMenuCorrection.cs",
    "Drilling.UI/Drilling.UI.csproj",
    "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs",
    "Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs",
    "Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionBackend.cs",
    "Drilling.UI/Menu/Menus/CManualInspectionController.cs",
    "Drilling.UI/Views/CManualInspectionMapControl.cs",
    "Drilling.UI/Views/CManualInspectionScatterControl.cs",
    "Drilling.UI/Views/CManualInspectionUiContract.cs",
    "Drilling.UI/Views/CManualInspectionView.xaml",
    "Drilling.UI/Views/CManualInspectionView.xaml.cs",
}


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def canonical(element):
    return (element.tag, sorted(element.attrib.items()), (element.text or "").strip(),
            tuple(canonical(child) for child in element))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    checks = []

    def check(name, ok):
        checks.append({"name": name, "passed": bool(ok)})

    paths = set(git("diff", "--name-only", BASE, "--").decode("utf-8").splitlines())
    paths.update(git("ls-files", "--others", "--exclude-standard").decode("utf-8").splitlines())
    check("changed_paths_scoped", all(p in EXISTING | NEW or p.startswith(("tests/ManualInspection/", "docs/ManualInspection/")) for p in paths))
    check("all_new_production_files_present", all((ROOT / p).is_file() for p in NEW))
    check("configuration_data_sdk_and_themes_unchanged", not any(p.startswith(("Config/", "Data/", "Drilling.UI/Theme")) or p.endswith(".dll") for p in paths))
    project = ET.parse(ROOT / "Drilling.UI/Drilling.UI.csproj").getroot()
    check("dated_program_version_matches_branch_revision_11_21_2", project.findtext(".//Version") == "2026.11.21.2"
          and b'ProgramVersion = "11.21.2"' in (ROOT / "Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs").read_bytes())
    # Only the new Manual Inspection surfaces and its file-adoption/bookmark engine contract
    # may change. Existing Correction menus, conversion math, sequences and file writers stay exact.
    for path in sorted(EXISTING):
        check("unchanged:" + path, git("rev-parse", f"{BASE}:{path}").strip() == git("hash-object", path).strip())
    path = "Drilling.Common/Review/CReviewManager.cs"
    marker = "public sealed record ST_REVIEW_OFFSET_ROW"
    check("review_executable_body_unchanged", git("show", f"{BASE}:{path}").decode("utf-8-sig").split(marker, 1)[0]
          == (ROOT / path).read_text(encoding="utf-8-sig").split(marker, 1)[0])
    check("coordinate_conversion_bridge_unchanged",git("show", f"{BASE}:Drilling.Common/Review/CManualInspectionCoordinateBridge.cs")
          == (ROOT / "Drilling.Common/Review/CManualInspectionCoordinateBridge.cs").read_bytes())
    for path in ["Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs",
                 "Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs",
                 "Drilling.Common/Review/CManualInspectionResultProjection.cs"]:
        check("verified_integration_unchanged:" + path,
              git("rev-parse", f"{BASE}:{path}").strip() == git("hash-object", path).strip())
    # Shared existing screens keep their formulas and control flow byte-for-byte. Only the
    # explicit measurement-state metadata is normalized away for this comparison.
    path = "Drilling.UI/Menu/Menus/CMenuCorrection.cs"
    current = (ROOT / path).read_text(encoding="utf-8-sig")
    current = current.replace("previous with { IsMeasurementOffsetInitialized = false, MeasurementInitializedAt=null } : target with", "previous : target with")
    current = current.replace("previous with { IsMeasurementOffsetInitialized = false } : target with", "previous : target with")
    current = re.sub(r",\s*IsMeasurementOffsetInitialized\s*=\s*initialize(?:\s*,\s*MeasurementInitializedAt\s*=\s*null)?", "", current)
    check("existing_correction_formula_unchanged_except_measurement_state_metadata",
          current == git("show", f"{BASE}:{path}").decode("utf-8-sig"))
    path = "Drilling.Common/Review/CReviewManager.cs"
    current = (ROOT / path).read_text(encoding="utf-8-sig")
    current = re.sub(r"^\s*/// <summary>Per-row reset boundary;[^\n]*\n", "", current, flags=re.M)
    current = re.sub(r"^\s*public (?:bool (?:IsMeasurementOffsetInitialized|HasMeasurementStateMetadata)|DateTimeOffset\? MeasurementInitializedAt) \{ get; init; \}\n", "", current, flags=re.M)
    check("shared_review_manager_only_adds_data_properties", current == git("show", f"{BASE}:{path}").decode("utf-8-sig"))
    path = "Drilling.File/ReviewOffset/CReviewOffsetFile.cs"
    current = (ROOT / path).read_text(encoding="utf-8-sig")
    current = current.replace('"COORDINATE_SPACE",\n        "MEASUREMENT_STATE",\n        "MEASUREMENT_INITIALIZED_AT"', '"COORDINATE_SPACE"')
    current = re.sub(r"        if \(rows.Any\(row => row.IsMeasurementOffsetInitialized[^\n]*\n            throw new InvalidDataException\([^\n]*\n", "", current)
    current = re.sub(r"        normalizedRows = normalizedRows.Select\(row => row with\n        \{\n            MeasurementInitializedAt = [^\n]*\n        \}\).ToArray\(\);\n", "", current)
    current = current.replace(", HasMeasurementStateMetadata = true", "").replace(", HasMeasurementStateMetadata = hasMeasurementState", "")
    current = re.sub(r"            var hasMeasurementState = CCsvParser.ReadHeaders\(path\)\n                [^\n]*\n", "", current)
    current = re.sub(r"            // Freeze the legacy state-column fallback[^\n]*\n            // move this row[^\n]*\n            rows = rows.Select\(row => row.IsMeasurementOffsetInitialized[^\n]*\n                [^\n]*\n", "", current)
    current = re.sub(r'        var state = CCsvParser.Get\(row, "MEASUREMENT_STATE"\).*?        return new ST_REVIEW_OFFSET_ROW\(cellId, shotId, offsetX, offsetY\)\n        \{.*?\n        \};',
                     '        return new ST_REVIEW_OFFSET_ROW(cellId, shotId, offsetX, offsetY) { IsDesignCoordinate = space == "DESIGN" };', current, flags=re.S)
    current = re.sub(r'\["COORDINATE_SPACE"\] = row.IsDesignCoordinate \? "DESIGN" : "SCANNER",\n            \["MEASUREMENT_STATE"\][^\n]*\n            \["MEASUREMENT_INITIALIZED_AT"\][^\n]*',
                     '["COORDINATE_SPACE"] = row.IsDesignCoordinate ? "DESIGN" : "SCANNER"', current)
    check("offset_writer_transaction_and_numeric_logic_unchanged_except_state_metadata",
          current == git("show", f"{BASE}:{path}").decode("utf-8-sig"))
    manager_path = "Drilling.Common/Review/CReviewManager.cs"
    def result_method(text):
        body = text.split("private async Task SaveResultForProduct", 1)[1].split("private async Task SaveProgressToProduct", 1)[0]
        return re.sub(r"\s+", "", re.sub(r"await reviewResultFile.Save\(.*?ConfigureAwait\(false\);", "<RESULT_SAVE>", body, count=1, flags=re.S))
    check("ordinary_review_control_flow_unchanged_outside_mode_metadata",
          result_method(git("show", f"{BASE}:{manager_path}").decode("utf-8-sig"))
          == result_method((ROOT / manager_path).read_text(encoding="utf-8-sig")))
    path = "Drilling.UI/Views/CCorrectionView.xaml"
    old_xml = ET.fromstring(git("show", f"{BASE}:{path}"))
    new_xml = ET.parse(ROOT / path).getroot()
    ns = {"w": "http://schemas.microsoft.com/winfx/2006/xaml/presentation"}
    for header in ["SIMPLE", "INSPECTION"]:
        old = next(e for e in old_xml.findall(".//w:TabItem", ns) if e.get("Header") == header)
        new = next(e for e in new_xml.findall(".//w:TabItem", ns) if e.get("Header") == header)
        check("existing_review_tab_unchanged:" + header, canonical(old) == canonical(new))
    check("exactly_one_new_manual_tab", len([e for e in new_xml.findall(".//w:TabItem", ns) if e.get("Header") == "MANUAL INSPECTION"]) == 1)
    bad = []
    for path in paths:
        file = ROOT / path
        if file.is_file() and file.suffix.lower() in {".cs", ".xaml", ".md", ".json", ".html", ".py", ".csproj", ".config"}:
            try:
                if "\ufffd" in file.read_text(encoding="utf-8-sig"):
                    bad.append(path)
            except UnicodeError:
                bad.append(path)
    check("utf8_without_replacement_characters", not bad)
    result = {"schemaVersion": "a3-manual-inspection-scope/1", "baseCommit": BASE,
              "passed": all(c["passed"] for c in checks), "checks": checks, "badTextPaths": bad,
              "changedPathCount": len(paths), "productionSha256": {
                  p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in sorted(EXISTING | NEW)}}
    output = Path(args.report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": result["passed"], "checks": len(checks), "changedPaths": len(paths)}))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
