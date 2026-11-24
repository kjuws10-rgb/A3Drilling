"""11.25.0 Manual responsiveness/progress change scope against 11.24.10."""
import argparse,hashlib,json,re,subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[2];BASE='26a7db3e27bb17c4b9f141f2831d168965d978d5'
ALLOWED={'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
 'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs','Drilling.UI/Drilling.UI.csproj',
 'Drilling.UI/Menu/Menus/CManualInspectionController.cs','Drilling.UI/Menu/Menus/CManualInspectionBackend.cs',
 'Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs','Drilling.UI/Views/CManualInspectionMapControl.cs',
 'Drilling.UI/Views/CManualInspectionUiContract.cs','Drilling.UI/Views/CManualInspectionView.xaml','Drilling.UI/Views/CManualInspectionView.xaml.cs'}
def git(*args):return subprocess.check_output(['git',*args],cwd=R)
def norm(b):return b.decode('utf-8-sig').replace('\r\n','\n')
def now(p):return norm((R/p).read_bytes())
def old(p):return norm(git('show',BASE+':'+p))
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--report',required=True);args=p.parse_args();checks=[]
 def check(name,ok):checks.append({'name':name,'passed':bool(ok)})
 paths=set(norm(git('diff','--name-only',BASE)).splitlines())|set(norm(git('ls-files','--others','--exclude-standard')).splitlines())
 check('manual_performance_tests_docs_only',all(n in ALLOWED or n.startswith(('tests/ManualInspection/','docs/ManualInspection/')) for n in paths))
 check('no_data_config_vendor_theme_mutation',not any(n.startswith(('Config/','Data/','Drilling.Common/Motion/','Drilling.Common/Interface/','Drilling.UI/Themes/')) or n.endswith(('.dll','.exe')) for n in paths))
 check('program_version',ET.parse(R/'Drilling.UI/Drilling.UI.csproj').findtext('.//Version')=='2026.11.25.0')
 check('portable_version','ProgramVersion = "11.25.0"' in now('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
 protected=['Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs',
 'Drilling.Common/Review/CManualInspectionCoordinateBridge.cs','Drilling.Common/Review/CReviewManager.cs',
 'Drilling.Common/Review/CAutoFineReviewCoordinator.cs','Drilling.Common/Station/CStageReviewSequences.cs',
 'Drilling.Common/Station/CStationManager.cs','Drilling.Common/Station/CStationProcess.cs','Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs',
 'Drilling.File/ReviewOffset/CReviewOffsetFile.cs','Drilling.File/Script/CAutomation1ScriptFile.cs',
 'Drilling.UI/CRootView.cs','Drilling.UI/CAppStartup.cs','Drilling.UI/Menu/Menus/CMenuMonitor.cs','Drilling.UI/Menu/Menus/CMenuRecipe.cs',
 'Drilling.UI/Views/CManualInspectionScatterControl.cs','Drilling.UI/Views/CCorrectionView.xaml']
 protected+=norm(git('ls-files','Drilling.UI/Menu/Menus/CMenuCorrection*.cs')).splitlines()
 for n in protected:check('preserved:'+n,now(n)==old(n))
 core='Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs'
 def between(s,a,b):return s.split(a,1)[1].split(b,1)[0]
 for name,end in [('BuildMeasurementReplacementCandidate(','/// <summary>Prepare a whole-model'),
                  ('BuildScopedOffsetReplacementCandidate(','public ST_MANUAL_CANDIDATE InitializeMeasurementOffsets(')]:
  check('math_scope_preserved:'+name,between(now(core),name,end)==between(old(core),name,end))
 controller=now('Drilling.UI/Menu/Menus/CManualInspectionController.cs')
 check('checkpoint_still_contains_full_session','var captured=Bundle()' in controller)
 check('checkpoint_no_sync_callback_write','SaveLocalSession();' not in controller)
 check('progress_has_worker_safe_stage_fields',all(s in controller for s in ['ProgressStage=','ProgressText=','IsProgressIndeterminate=']))
 check('line_fence_before_next_motion','FlushDueCheckpointAsync, token' in controller and 'await afterLine(token);' in now('Drilling.UI/Menu/Menus/CManualInspectionBackend.cs'))
 tree=ET.parse(R/'Drilling.UI/Views/CManualInspectionView.xaml');names=[(e,e.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name')) for e in tree.iter()]
 check('single_progress_bar',sum(name=='JobProgressBar' for _,name in names)==1)
 parents={child:parent for parent in tree.iter() for child in parent}
 bar=next(e for e,name in names if name=='JobProgressBar');ancestors=[];node=bar
 while node in parents:node=parents[node];ancestors.append(node.tag.split('}')[-1])
 check('progress_not_hidden_in_expander','Expander' not in ancestors)
 bad=[]
 for n in paths:
  f=R/n
  if f.is_file() and f.suffix.lower() in {'.cs','.xaml','.md','.json','.py','.csproj'}:
   try:
    if '\ufffd' in f.read_text(encoding='utf-8-sig'):bad.append(n)
   except UnicodeError:bad.append(n)
 check('valid_utf8',not bad)
 result={'passed':all(c['passed'] for c in checks),'count':len(checks),'checks':checks,'baseCommit':BASE,'changedPaths':sorted(paths),'badTextPaths':bad,
 'productionSha256':{n:hashlib.sha256((R/n).read_bytes()).hexdigest() for n in sorted(ALLOWED|set(protected))},
 'scope':'Manual display, immutable snapshots, checkpoint I/O and progress only; actual B+residual/Init/CSV/sequence formulas retained.'}
 path=Path(args.report);path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'passed':result['passed'],'count':len(checks),'failed':[c['name'] for c in checks if not c['passed']]}));return 0 if result['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
