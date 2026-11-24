"""Check the .5 Manual Inspection scope, preserved equipment paths and UI/engine boundaries."""
import argparse,hashlib,json,subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[2]
BASE='c0a169548edef5975ea454389071dd7c22c625bd'
SOURCES=[
 'Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs',
 'Drilling.UI/Drilling.UI.csproj','Drilling.UI/Menu/Menus/CManualInspectionController.cs',
 'Drilling.UI/Views/CManualInspectionUiContract.cs','Drilling.UI/Views/CManualInspectionMapControl.cs',
 'Drilling.UI/Views/CManualInspectionView.xaml.cs','Drilling.UI/Views/CManualInspectionView.xaml']
def git(*a):return subprocess.check_output(['git',*a],cwd=R)
def norm(b):return b.decode('utf-8-sig').replace('\r\n','\n')
def read(p):return norm((R/p).read_bytes())
def old(p):return norm(git('show',BASE+':'+p))
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--report',required=True);a=p.parse_args();checks=[]
 def check(n,b):checks.append({'name':n,'passed':bool(b)})
 paths=set(norm(git('diff','--name-only',BASE)).splitlines())|set(norm(git('ls-files','--others','--exclude-standard')).splitlines())
 check('only_nine_manual_sources_and_dedicated_tests_docs',all(x in SOURCES or x.startswith(('tests/ManualInspection/','docs/ManualInspection/')) for x in paths))
 check('protected_operating_configuration_vendor_global_theme_unchanged',not any(x.startswith(('Config/','Data/','Drilling.Common/Interface/','Drilling.Common/Motion/','Drilling.UI/Themes/','Drilling.UI/Popup/')) or x.endswith(('.dll','.exe')) for x in paths))
 for path in ['Drilling.UI/CRootView.cs','Drilling.UI/CAppStartup.cs','Drilling.File/ReviewOffset/CReviewOffsetFile.cs','Drilling.UI/Menu/Menus/CManualInspectionBackend.cs','Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs','Drilling.UI/Menu/Menus/CMenuCorrection.cs','Drilling.UI/Menu/Menus/CMenuCorrection.ReviewData.Offset.cs','Drilling.UI/Menu/Menus/CMenuMonitor.cs','Drilling.UI/Menu/Menus/CMenuRecipe.cs','Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs','Drilling.Common/Station/CStationProcess.cs','Drilling.Common/Review/CManualInspectionCoordinateBridge.cs','Drilling.UI/Popup/CValueInputDialog.xaml','Drilling.UI/Popup/CValueInputDialog.xaml.cs']:
  check('unchanged:'+path,read(path)==old(path))
 check('version_11_24_5',ET.parse(R/'Drilling.UI/Drilling.UI.csproj').findtext('.//Version')=='2026.11.24.5' and 'ProgramVersion = "11.24.5"' in read(SOURCES[2]))
 view=read('Drilling.UI/Views/CManualInspectionView.xaml.cs')
 check('existing_numeric_keypad_reused_with_design_um_label','new CValueInputDialog(prompt, value, EN_RECIPE_DATA_TYPE.Double' in view and 'X 편차 · µm' in view and 'Y 편차 · µm' in view)
 check('point_input_has_finite_and_source_recheck','!double.IsFinite(x)' in view and 'expectedVersion != controller.Snapshot.Version' in view and 'ReferenceEquals(controller, _controller)' in view)
 check('copy_confirmation_uses_existing_modal_gate','대표값 복사 적용 확인' in view and 'CopyReference || !snapshot.HasIncomplete' in view and '_actionConfirmationPending = true' in view)
 projection=lambda s:s.split('private CManualInspectionUiPoint FinalOffsetPoint',1)[1].split('private void RefreshFinalOffsetInfo',1)[0]
 check('saved_final_projection_not_replaced_by_unsaved_operator',projection(view)==projection(old('Drilling.UI/Views/CManualInspectionView.xaml.cs')))
 control=read('Drilling.UI/Menu/Menus/CManualInspectionController.cs')
 force=control.split('private async Task ForceMeasurement(',1)[1].split('private async Task BulkApply(',1)[0]
 check('manual_result_entry_does_not_save_or_move',not any(t in force for t in ['_backend.Save','await Apply(','ExecutePointAsync','MoveMeasure']))
 check('copy_performs_existing_apply_pipeline','await Apply(token,sourceVerified:true)' in control.split('private async Task CopyReference(',1)[1].split('private async Task ForceMeasurement(',1)[0])
 engine=read('Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs')
 force_engine=engine.split('public void RecordOperatorResults(',1)[1].split('public ST_MANUAL_BULK_PREVIEW',1)[0]
 check('operator_entry_keeps_original_measurement','OriginalMeasurement =' not in force_engine and 'SubjectPointKey=point.Key' in force_engine)
 map_source=read('Drilling.UI/Views/CManualInspectionMapControl.cs')
 check('point_vs_blank_double_click_and_overview_sequence','followedOverviewClick' in map_source and 'ValueInputRequested?.Invoke' in map_source and 'if(TryResetOnDoubleClick(clickCount))' in map_source)
 check('diff_check',subprocess.run(['git','diff',BASE,'--check'],cwd=R,capture_output=True).returncode==0)
 j={'passed':all(c['passed'] for c in checks),'count':len(checks),'baseCommit':BASE,'programVersion':'11.24.5','checks':checks,'changedPaths':sorted(paths),'productionSha256':{x:hashlib.sha256((R/x).read_bytes()).hexdigest() for x in SOURCES},'scope':'Manual representative apply, operator result metadata, point input and existing keypad reuse only; no equipment or operating CSV writes.'}
 out=Path(a.report);out.parent.mkdir(parents=True,exist_ok=True);out.write_text(json.dumps(j,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'passed':j['passed'],'checks':len(checks),'changedPaths':len(paths)}))
 for c in checks:
  if not c['passed']:print('FAIL '+c['name'])
 return 0 if j['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
