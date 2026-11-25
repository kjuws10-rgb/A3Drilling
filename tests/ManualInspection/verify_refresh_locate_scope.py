"""11.25.1 refresh gating, read-only location and explicit selected-Cell save scope."""
import argparse,hashlib,json,subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[2];BASE='0d342edcb105ab5fd007dc3a273df52b2099940b'
ALLOWED={'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs','Drilling.UI/Drilling.UI.csproj',
 'Drilling.UI/Menu/Menus/CManualInspectionBackend.cs','Drilling.UI/Menu/Menus/CManualInspectionController.cs',
 'Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs','Drilling.UI/Views/CManualInspectionMapControl.cs',
 'Drilling.UI/Views/CManualInspectionUiContract.cs','Drilling.UI/Views/CManualInspectionView.xaml','Drilling.UI/Views/CManualInspectionView.xaml.cs'}
def git(*args):return subprocess.check_output(['git',*args],cwd=R)
def norm(b):return b.decode('utf-8-sig').replace('\r\n','\n')
def now(p):return norm((R/p).read_bytes())
def old(p):return norm(git('show',BASE+':'+p))
def section(s,a,b):return s.split(a,1)[1].split(b,1)[0]
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--report',required=True);a=p.parse_args();checks=[]
 def check(name,ok):checks.append({'name':name,'passed':bool(ok)})
 paths=set(norm(git('diff','--name-only',BASE)).splitlines())|set(norm(git('ls-files','--others','--exclude-standard')).splitlines())
 check('manual_only',all(n in ALLOWED or n.startswith(('tests/ManualInspection/','docs/ManualInspection/')) for n in paths))
 check('version',ET.parse(R/'Drilling.UI/Drilling.UI.csproj').findtext('.//Version')=='2026.11.25.1' and 'ProgramVersion = "11.25.1"' in now('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
 protected=['Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs','Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
 'Drilling.Common/Review/CManualInspectionCoordinateBridge.cs','Drilling.Common/Review/CReviewManager.cs','Drilling.Common/Review/CAutoFineReviewCoordinator.cs',
 'Drilling.Common/Station/CStageReviewSequences.cs','Drilling.Common/Station/CStationManager.cs','Drilling.Common/Station/CStationProcess.cs',
 'Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs','Drilling.File/ReviewOffset/CReviewOffsetFile.cs','Drilling.File/Script/CAutomation1ScriptFile.cs',
 'Drilling.UI/CRootView.cs','Drilling.UI/CAppStartup.cs','Drilling.UI/Menu/Menus/CMenuMonitor.cs','Drilling.UI/Menu/Menus/CMenuRecipe.cs',
 'Drilling.UI/Menu/Menus/CManualInspectionUpdateMonitor.cs','Drilling.UI/Views/CManualInspectionScatterControl.cs']
 protected+=norm(git('ls-files','Drilling.UI/Menu/Menus/CMenuCorrection*.cs','Drilling.UI/Themes/*.xaml')).splitlines()
 for n in protected:check('preserved:'+n,now(n)==old(n))
 check('no_config_data_vendor',not any(n.startswith(('Config/','Data/','Drilling.Common/Motion/','Drilling.Common/Interface/')) for n in paths))
 c=now('Drilling.UI/Menu/Menus/CManualInspectionController.cs');oc=old('Drilling.UI/Menu/Menus/CManualInspectionController.cs')
 watch=section(c,'public void EnableAutomaticUpdates()','public void DisableAutomaticUpdates()')
 check('filesystem_invalidation_only','new Action(NotifyFilesChanged)' in watch and 'SynchronizeExternalAsync' not in watch and 'RefreshFinalOffsets' not in watch)
 execute=section(c,'public async Task ExecuteAsync(','private async Task LoadSource(')
 check('no_post_command_catalogue_reload','SynchronizeExternalAsync' not in execute)
 sync=section(c,'public async Task SynchronizeExternalAsync(','private void ScheduleQueuedSynchronization(')
 check('busy_notification_before_saved_map_read',sync.index('if(IsBusy || _command.CurrentCount==0)')<sync.index('await RefreshFinalOffsets'))
 check('current_guard_unchanged',section(c,'private async Task Current(','private async Task ReconnectEditableSource(')==section(oc,'private async Task Current(','private async Task ReconnectEditableSource('))
 b=now('Drilling.UI/Menu/Menus/CManualInspectionBackend.cs');ob=old('Drilling.UI/Menu/Menus/CManualInspectionBackend.cs')
 check('motion_measurement_sequence_unchanged',section(b,'public async Task<CManualInspectionMeasurement> ExecutePointAsync(','internal static ST_FINE_REVIEW_REQUEST CreateSingleFlyingRequest(')==section(ob,'public async Task<CManualInspectionMeasurement> ExecutePointAsync(','internal static ST_FINE_REVIEW_REQUEST CreateSingleFlyingRequest('))
 check('sha_current_guard_unchanged',section(b,'public async Task EnsureCurrentAsync(','private Task<IReadOnlyList<ST_RECIPE_DEFINITION>> ReadRecipesOffUiAsync(')==section(ob,'public async Task EnsureCurrentAsync(','private Task<IReadOnlyList<ST_RECIPE_DEFINITION>> ReadRecipesOffUiAsync('))
 position=section(b,'public async Task<CManualInspectionPosition> ReadCurrentPositionAsync(','internal static IReadOnlyList<string> FindPositionTargets(')
 check('locate_reads_and_verifies_only',all(n in position for n in ['ReadStationaryPosition','VerifyCurrentTarget','EnsureCurrentAsync']) and not any(n in position for n in ['.Move(','.ExecuteOneShot(','.StartFineCorrection(','.Save']))
 check('one_bulk_candidate_transaction','IEnumerable<int> requestedCells=' in c and 'scopeCells.Contains(p.CellNo)' in c and 'one authoritative CSV transaction' in c)
 drawing='Drilling.UI/Views/CManualInspectionMapControl.cs';marker='internal static class CManualInspectionDrawing'
 check('measurement_colors_and_levels_preserved',now(drawing).split(marker,1)[1]==old(drawing).split(marker,1)[1])
 check('valid_utf8',all('\ufffd' not in (R/n).read_text(encoding='utf-8-sig') for n in paths if (R/n).is_file() and (R/n).suffix.lower() in {'.cs','.xaml','.md','.json','.py','.csproj'}))
 out={'passed':all(x['passed'] for x in checks),'count':len(checks),'checks':checks,'baseCommit':BASE,'changedPaths':sorted(paths),
  'productionSha256':{n:hashlib.sha256((R/n).read_bytes()).hexdigest() for n in sorted(ALLOWED|set(protected))},
  'scope':'Manual refresh scheduling/display/query and explicit Cell union. Engine/schema/CSVwriter/Station/otherReview/device execution unchanged.'}
 Path(a.report).write_text(json.dumps(out,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps({'passed':out['passed'],'count':len(checks),'failed':[x['name'] for x in checks if not x['passed']]}));return 0 if out['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
