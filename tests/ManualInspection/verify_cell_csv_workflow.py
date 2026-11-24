"""Check the .7 Cell correction / CSV workflow scope against published .6."""
import argparse, hashlib, json, re, subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[2]
BASE='d05aec5f4d966232a25580c25ff453aedbfccb20'
ALLOWED={
 'Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs',
 'Drilling.File/ReviewOffset/CReviewOffsetFile.cs',
 'Drilling.UI/Drilling.UI.csproj','Drilling.UI/Menu/Menus/CManualInspectionBackend.cs',
 'Drilling.UI/Menu/Menus/CManualInspectionController.cs','Drilling.UI/Views/CManualInspectionUiContract.cs',
 'Drilling.UI/Views/CManualInspectionView.xaml','Drilling.UI/Views/CManualInspectionView.xaml.cs'}
def git(*args): return subprocess.check_output(['git',*args],cwd=R)
def norm(data): return data.decode('utf-8-sig').replace('\r\n','\n')
def now(path): return norm((R/path).read_bytes())
def old(path): return norm(git('show',BASE+':'+path))
def segment(text,start,end): return text.split(start,1)[1].split(end,1)[0]
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--report',required=True);a=p.parse_args();checks=[]
 def check(name,result): checks.append({'name':name,'passed':bool(result)})
 paths=set(norm(git('diff','--name-only',BASE)).splitlines())|set(norm(git('ls-files','--others','--exclude-standard')).splitlines())
 check('only_authorized_manual_csv_sources_tests_docs',all(x in ALLOWED or x.startswith(('docs/ManualInspection/','tests/ManualInspection/')) for x in paths))
 check('settings_runtime_data_vendor_themes_untouched',not any(x.startswith(('Config/','Data/','Drilling.Common/Motion/','Drilling.Common/Interface/','Drilling.UI/Themes/')) or x.endswith(('.dll','.exe')) for x in paths))
 check('same_day_program_version_11_24_7',ET.parse(R/'Drilling.UI/Drilling.UI.csproj').findtext('.//Version')=='2026.11.24.7')
 check('same_day_portable_version_11_24_7','ProgramVersion = "11.24.7"' in now('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
 protected=['Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs','Drilling.Common/Review/CReviewManager.cs',
  'Drilling.Common/Review/CManualInspectionCoordinateBridge.cs','Drilling.Common/Station/CStageReviewSequences.cs',
  'Drilling.UI/Menu/Menus/CMenuMonitor.cs','Drilling.UI/Views/CMonitorView.xaml',
  'Drilling.UI/Menu/Menus/CReviewOffsetDisplaySnapshot.cs','Drilling.UI/Menu/Menus/CMenuCorrection.ManualInspection.cs',
  'Drilling.UI/Views/CManualInspectionMapControl.cs','Drilling.UI/Views/CManualInspectionScatterControl.cs',
  'Drilling.UI/Views/CCorrectionView.xaml','Drilling.UI/CAppStartup.cs','Drilling.UI/CRootView.cs']
 protected+= [x for x in norm(git('ls-files','Drilling.UI/Menu/Menus/CMenuCorrection*.cs')).splitlines() if x not in protected]
 for path in protected: check('preserved:'+path,now(path)==old(path))
 core='Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs'
 check('representative_resolution_policy_preserved',segment(now(core),'public ST_MANUAL_REFERENCE_VALUE ResolveReferenceValue(', 'public ST_MANUAL_COPY_PREVIEW PreviewReferenceCopy(')==segment(old(core),'public ST_MANUAL_REFERENCE_VALUE ResolveReferenceValue(', 'public ST_MANUAL_COPY_PREVIEW PreviewReferenceCopy('))
 backend='Drilling.UI/Menu/Menus/CManualInspectionBackend.cs'
 for start,end,label in [('private async Task<string> SaveCoreAsync(', 'private Dictionary<string,ST_REVIEW_OFFSET_ROW> BuildWorkingRows(', 'native_save_transaction'),
                         ('private Dictionary<string,ST_REVIEW_OFFSET_ROW> BuildWorkingRows(', 'private static CManualInspectionSource SourceWithSaveState(', 'absolute_rows_and_hidden_preservation')]:
  check('preserved:'+label,segment(now(backend),start,end)==segment(old(backend),start,end))
 writer='Drilling.File/ReviewOffset/CReviewOffsetFile.cs'
 for start,end,label in [('private ST_REVIEW_OFFSET_FILE_DATA SaveNewCore(', 'public override async Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(', 'authoritative_writer'),
                         ('private static IEnumerable<string> EnumerateActiveFiles(', 'private async Task<FileStream> AcquireProcessLease(', 'active_file_selection')]:
  check('preserved:'+label,segment(now(writer),start,end)==segment(old(writer),start,end))
 view=now('Drilling.UI/Views/CManualInspectionView.xaml.cs')
 files=segment(view,'private void Files_Click(', 'private ContextMenu NewMenu(')
 check('file_menu_only_import_and_current_csv_export',files.count('EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle')==2 and 'EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv' in files
       and all(token not in files for token in ['LoadResultFile','ExportFinalOffsets','ExportOffsetBundle','ReturnToPreviousWorkspace','ExportCsv']))
 check('bulk_confirmation_rechecks_explicit_current_cell','frozen.CurrentCellNo==_cell' in view and 'CurrentCellNo = _cell>0?_cell:null' in view)
 check('copy_description_is_review_replacement','대상의 기존 Review Offset에 복사값을 더하지 않습니다' in view)
 # Appended UI actions retain the numeric identity of every old action in persisted sessions.
 contract='Drilling.UI/Views/CManualInspectionUiContract.cs'
 enum=lambda text:re.findall(r'\b[A-Za-z_]\w*\b',segment(text,'public enum EN_MANUAL_INSPECTION_UI_ACTION','}'))
 check('existing_action_enum_ordinals_preserved',enum(now(contract))[:len(enum(old(contract)))]==enum(old(contract)))
 bad=[]
 for path in paths:
  file=R/path
  if file.is_file() and file.suffix.lower() in {'.cs','.xaml','.md','.json','.py','.csproj'}:
   try:
    if '\ufffd' in file.read_text(encoding='utf-8-sig'): bad.append(path)
   except UnicodeError:bad.append(path)
 check('utf8_no_replacement_characters',not bad)
 report={'baseCommit':BASE,'passed':all(c['passed'] for c in checks),'checks':checks,'changedPaths':sorted(paths),'badTextPaths':bad,
  'productionSha256':{x:hashlib.sha256((R/x).read_bytes()).hexdigest() for x in sorted(ALLOWED|set(protected))},
  'sourceHashPolicy':'Raw bytes of the tested Windows checkout; Git blob equality is checked with line-ending normalization at publication.'}
 target=Path(a.report);target.parent.mkdir(parents=True,exist_ok=True);target.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'passed':report['passed'],'count':len(checks),'failed':[c['name'] for c in checks if not c['passed']]}));return 0 if report['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
