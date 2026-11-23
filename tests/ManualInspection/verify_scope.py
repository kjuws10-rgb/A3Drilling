"""Verify manual interaction, standard selection, representative data and unchanged cross-menu scope. Never starts equipment."""
import argparse,hashlib,json,re,subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2]
BASE="89e92a015802523ba81b871a752d0c71e62e627f"
EXISTING={
 "Drilling.UI/CRootView.cs","Drilling.UI/Menu/Menus/CMenuMonitor.cs","Drilling.UI/Menu/Menus/CMenuRecipe.cs","Drilling.UI/Menu/Menus/CReviewOffsetDisplaySnapshot.cs",
 "Drilling.Common/Station/CStageReviewSequences.cs","Drilling.File/ReviewResult/CReviewResultFile.cs",
 "Drilling.Common/Review/CReviewManager.cs","Drilling.File/ReviewOffset/CReviewOffsetFile.cs",
 "Drilling.UI/Views/CCorrectionView.xaml","Drilling.UI/Views/CCorrectionView.xaml.cs",
 "Drilling.Common/Review/CManualInspectionCoordinateBridge.cs","Drilling.Common/Review/CManualInspectionResultProjection.cs",
 "Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs","Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs",
 "Drilling.UI/Menu/Menus/CMenuCorrection.ManualInspection.cs","Drilling.UI/Views/CManualInspectionScatterControl.cs"}
CHANGED={
 "Drilling.UI/Drilling.UI.csproj","Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs",
 "Drilling.UI/Menu/Menus/CManualInspectionController.cs","Drilling.UI/Views/CManualInspectionMapControl.cs",
 "Drilling.UI/Views/CManualInspectionView.xaml.cs","Drilling.UI/Views/CManualInspectionUiContract.cs","Drilling.UI/Views/CManualInspectionView.xaml",
 "Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs",
 "Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs","Drilling.UI/Menu/Menus/CManualInspectionBackend.cs"}

def git(*a):return subprocess.check_output(["git",*a],cwd=ROOT)
def old(path):return git("show",f"{BASE}:{path}").decode("utf-8-sig").replace("\r\n","\n")
def current(path):return (ROOT/path).read_text(encoding="utf-8-sig")
def canonical(e):return e.tag,sorted(e.attrib.items()),(e.text or "").strip(),tuple(canonical(c) for c in e)
def main():
 p=argparse.ArgumentParser();p.add_argument("--report",required=True);args=p.parse_args();checks=[]
 def check(name,ok):checks.append({"name":name,"passed":bool(ok)})
 paths=set(git("diff","--name-only",BASE,"--").decode().splitlines())|set(git("ls-files","--others","--exclude-standard").decode().splitlines())
 check("changed_paths_are_authorized",all(x in CHANGED or x.startswith(("tests/ManualInspection/","docs/ManualInspection/")) for x in paths))
 check("configuration_runtime_data_vendor_and_themes_unchanged",not any(x.startswith(("Config/","Data/","Drilling.UI/Themes/","Drilling.Common/Interface/","Drilling.Common/Motion/")) or x.endswith((".dll",".exe")) for x in paths))
 check("daily_program_and_portable_version_11_23_2",ET.parse(ROOT/"Drilling.UI/Drilling.UI.csproj").findtext(".//Version")=="2026.11.23.2" and 'ProgramVersion = "11.23.2"' in current("Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs"))
 for path in sorted(EXISTING|{"Drilling.UI/CRootView.cs", "Drilling.UI/Menu/Menus/CMenuMonitor.cs", "Drilling.UI/Menu/Menus/CMenuRecipe.cs", "Drilling.UI/Menu/Menus/CReviewOffsetDisplaySnapshot.cs"}):check("unchanged:"+path,old(path)==current(path))
 # The coordinate planner shares a file with metadata; its arithmetic is outside this change.
 path="Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs";a=old(path);b=current(path)
 check("coordinate_plan_builder_body_unchanged",a.split("public static class CReviewOffsetData",1)[0]==b.split("public static class CReviewOffsetData",1)[0])
 check("scanner_transform_and_conversion_bodies_unchanged",a.split("public sealed class CReviewOffsetValidationException",1)[1]==b.split("public sealed class CReviewOffsetValidationException",1)[1])
 check("review_design_and_scanner_conversion_formulas_unchanged",a.split("public static bool IsDesignCoordinate",1)[1].split("public sealed class CReviewOffsetValidationException",1)[0]==b.split("public static bool IsDesignCoordinate",1)[1].split("public sealed class CReviewOffsetValidationException",1)[0])
 # Hardware/default/distortion/power partials remain exact; only the named Review hosts change.
 partials=[x for x in git("ls-files","Drilling.UI/Menu/Menus/CMenuCorrection*.cs").decode().splitlines() if x not in CHANGED]
 for path in partials:check("protected_correction_partial:"+path,old(path)==current(path))
 check("all_production_files_exist",all((ROOT/x).is_file() for x in EXISTING|CHANGED))
 ns={"w":"http://schemas.microsoft.com/winfx/2006/xaml/presentation"};path="Drilling.UI/Views/CCorrectionView.xaml"
 ox=ET.fromstring(git("show",f"{BASE}:{path}"));nx=ET.parse(ROOT/path).getroot()
 for header in ["SIMPLE","INSPECTION"]:
  ot=next(e for e in ox.findall(".//w:TabItem",ns) if e.get("Header")==header);nt=next(e for e in nx.findall(".//w:TabItem",ns) if e.get("Header")==header)
  check("existing_review_xaml_unchanged:"+header,canonical(ot)==canonical(nt))
 check("single_manual_inspection_tab",len([e for e in nx.findall(".//w:TabItem",ns) if e.get("Header")=="MANUAL INSPECTION"])==1)
 bad=[]
 for path in paths:
  file=ROOT/path
  if file.is_file() and file.suffix.lower() in {".cs",".xaml",".md",".json",".html",".py",".csproj",".config"}:
   try:
    if "\ufffd" in file.read_text(encoding="utf-8-sig"):bad.append(path)
   except UnicodeError:bad.append(path)
 check("utf8_no_replacement_characters",not bad)
 result={"schemaVersion":"a3-manual-inspection-scope/2","baseCommit":BASE,"passed":all(c["passed"] for c in checks),"checks":checks,"badTextPaths":bad,"changedPathCount":len(paths),
  "productionSha256":{x:hashlib.sha256((ROOT/x).read_bytes()).hexdigest() for x in sorted(EXISTING|CHANGED)}}
 out=Path(args.report);out.parent.mkdir(parents=True,exist_ok=True);out.write_text(json.dumps(result,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
 print(json.dumps({"passed":result["passed"],"checks":len(checks),"changedPaths":len(paths),"failed":[c["name"] for c in checks if not c["passed"]]}));return 0 if result["passed"] else 1
if __name__=="__main__":raise SystemExit(main())
