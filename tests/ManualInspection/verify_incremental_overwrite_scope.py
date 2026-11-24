"""11.24.10: .8 UI/layout preserved; residual accumulation + immediate Init only."""
import argparse,hashlib,json,re,subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[2]
BASE='dc3a15e6ebc382c6f14d76a916c6b7c510e25590'
ALLOWED={
 'Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs',
 'Drilling.UI/Drilling.UI.csproj','Drilling.UI/Menu/Menus/CManualInspectionBackend.cs',
 'Drilling.UI/Menu/Menus/CManualInspectionController.cs','Drilling.UI/Views/CManualInspectionView.xaml.cs'}
def git(*args):return subprocess.check_output(['git',*args],cwd=R)
def norm(data):return data.decode('utf-8-sig').replace('\r\n','\n')
def now(path):return norm((R/path).read_bytes())
def old(path):return norm(git('show',BASE+':'+path))
def main():
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--report',required=True);args=parser.parse_args();checks=[]
 def check(name,ok):checks.append({'name':name,'passed':bool(ok)})
 paths=set(norm(git('diff','--name-only',BASE)).splitlines())|set(norm(git('ls-files','--others','--exclude-standard')).splitlines())
 check('only_accumulation_init_sources_tests_docs',all(p in ALLOWED or p.startswith(('tests/ManualInspection/','docs/ManualInspection/')) for p in paths))
 check('no_config_data_vendor_themes_changes',not any(p.startswith(('Config/','Data/','Drilling.Common/Motion/','Drilling.Common/Interface/','Drilling.UI/Themes/')) or p.endswith(('.dll','.exe')) for p in paths))
 check('program_version_11_24_10',ET.parse(R/'Drilling.UI/Drilling.UI.csproj').findtext('.//Version')=='2026.11.24.10')
 check('portable_version_11_24_10','ProgramVersion = "11.24.10"' in now('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
 protected=['Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs','Drilling.Common/Review/CManualInspectionCoordinateBridge.cs',
 'Drilling.Common/Review/CReviewManager.cs','Drilling.Common/Review/CAutoFineReviewCoordinator.cs',
 'Drilling.Common/Station/CStageReviewSequences.cs','Drilling.Common/Station/CStationProcess.cs','Drilling.Common/Station/CStationManager.cs',
 'Drilling.File/ReviewOffset/CReviewOffsetFile.cs','Drilling.File/Script/CAutomation1ScriptFile.cs',
 'Drilling.UI/CRootView.cs','Drilling.UI/CAppStartup.cs','Drilling.UI/Menu/Menus/CMenuMonitor.cs',
 'Drilling.UI/Menu/Menus/CMenuRecipe.cs','Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs',
 'Drilling.UI/Views/CManualInspectionView.xaml','Drilling.UI/Views/CManualInspectionMapControl.cs',
 'Drilling.UI/Views/CManualInspectionScatterControl.cs','Drilling.UI/Views/CManualInspectionUiContract.cs']
 protected+=norm(git('ls-files','Drilling.UI/Menu/Menus/CMenuCorrection*.cs')).splitlines()
 for p in protected:check('unchanged:'+p,now(p)==old(p))
 models='Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs'
 without_comments=lambda text:re.sub(r'^\s*///.*$','',text,flags=re.M)
 check('models_only_policy_comments_changed',without_comments(now(models))==without_comments(old(models)))
 view='Drilling.UI/Views/CManualInspectionView.xaml.cs'
 strip=lambda text:re.sub(r'"(?:\\.|[^"\\])*"','""',text)
 check('view_only_wording_changed',strip(now(view))==strip(old(view)))
 backend='Drilling.UI/Menu/Menus/CManualInspectionBackend.cs'
 before,after=old(backend),now(backend)
 start='        var reservations = new List<string>();'
 end='        foreach (var pair in consumed)'
 section=lambda text:text.split(start,1)[1].split(end,1)[0]
 normalized=after.replace(section(after),'',1).replace('if (driving.Contains(pair))','if (driving.GetValueOrDefault(pair.Key) == pair.Value)')
 check('backend_only_consumed_driving_reservation_merge',normalized==before.replace(section(before),'',1))
 controller='Drilling.UI/Menu/Menus/CManualInspectionController.cs'
 def without_init_action(text):
  first,tail=text.split('                case EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets:',1)
  return first+tail.split('                case EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit:',1)[1]
 current=strip(without_init_action(now(controller))).replace(' or EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets','')
 check('controller_only_immediate_init_stop_wait_and_wording',current==strip(without_init_action(old(controller))))
 check('no_new_processing_basis_design',not (R/'Drilling.Common/Review/CReviewProcessingBasis.cs').exists())
 check('init_applies_final_csv_immediately','_allowManual=false;await Apply(token,sourceVerified:true);' in now('Drilling.UI/Menu/Menus/CManualInspectionController.cs'))
 bad=[]
 for p in paths:
  f=R/p
  if f.is_file() and f.suffix.lower() in {'.cs','.xaml','.md','.json','.py','.csproj'}:
   try:
    if '\ufffd' in f.read_text(encoding='utf-8-sig'):bad.append(p)
   except UnicodeError:bad.append(p)
 check('utf8_valid',not bad)
 result={'passed':all(c['passed'] for c in checks),'count':len(checks),'checks':checks,'baseCommit':BASE,
 'changedPaths':sorted(paths),'badTextPaths':bad,'productionSha256':{p:hashlib.sha256((R/p).read_bytes()).hexdigest() for p in sorted(ALLOWED|set(protected))},
 'scope':'Residual + current B for actual/manual-copy measurements; current Cell Init immediately persists absence. .9 basis/Station/layout not ported.'}
 target=Path(args.report);target.parent.mkdir(parents=True,exist_ok=True);target.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'passed':result['passed'],'count':len(checks),'failed':[c['name'] for c in checks if not c['passed']]}));return 0 if result['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
