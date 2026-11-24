"""Verify .9 change scope and unchanged coordinate/device consumers against published .8."""
import argparse, hashlib, json, re, subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
R = Path(__file__).resolve().parents[2]
BASE = 'dc3a15e6ebc382c6f14d76a916c6b7c510e25590'
ALLOWED = {
    '.gitignore', 'Drilling.UI/Drilling.UI.csproj',
    'Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs',
    'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
    'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs',
    'Drilling.Common/Review/CReviewProcessingBasis.cs', 'Drilling.Common/Review/CManualInspectionCoordinateBridge.cs',
    'Drilling.Common/Station/CStationProcess.cs', 'Drilling.Common/Station/CStationManager.cs',
    'Drilling.UI/Menu/Menus/CManualInspectionBackend.cs', 'Drilling.UI/Menu/Menus/CManualInspectionController.cs',
    'Drilling.UI/Menu/Menus/CManualInspectionReplayBackend.cs',
    'Drilling.UI/Views/CManualInspectionUiContract.cs', 'Drilling.UI/Views/CManualInspectionView.xaml',
    'Drilling.UI/Views/CManualInspectionView.xaml.cs', 'Drilling.UI/Views/CManualInspectionMapControl.cs',
    'Drilling.UI/Views/CManualInspectionScatterControl.cs',
}
def git(*args): return subprocess.check_output(['git', *args], cwd=R)
def norm(data): return data.decode('utf-8-sig').replace('\r\n', '\n')
def now(p): return norm((R / p).read_bytes())
def old(p): return norm(git('show', BASE + ':' + p))
def main():
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('--report', required=True); args = p.parse_args()
    checks = []
    def check(name, passed): checks.append({'name': name, 'passed': bool(passed)})
    paths = set(norm(git('diff', '--name-only', BASE)).splitlines()) | set(norm(git('ls-files', '--others', '--exclude-standard')).splitlines())
    check('authorized_calibration_sources_tests_docs_only', all(x in ALLOWED or x.startswith(('tests/ManualInspection/', 'docs/ManualInspection/')) for x in paths))
    check('no_config_data_vendor_theme_mutation', not any(x.startswith(('Config/', 'Data/', 'Drilling.Common/Motion/', 'Drilling.Common/Interface/', 'Drilling.UI/Themes/')) or x.endswith(('.dll', '.exe')) for x in paths))
    check('version_11_24_9', ET.parse(R / 'Drilling.UI/Drilling.UI.csproj').findtext('.//Version') == '2026.11.24.9')
    check('portable_version_11_24_9', 'ProgramVersion = "11.24.9"' in now('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
    protected = [
        'Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs', 'Drilling.Common/Review/CReviewManager.cs',
        'Drilling.Common/Review/CAutoFineReviewCoordinator.cs',
        'Drilling.Common/Station/CStageReviewSequences.cs', 'Drilling.File/ReviewOffset/CReviewOffsetFile.cs',
        'Drilling.File/Script/CAutomation1ScriptFile.cs', 'Drilling.UI/CRootView.cs', 'Drilling.UI/CAppStartup.cs',
        'Drilling.UI/Menu/Menus/CMenuMonitor.cs', 'Drilling.UI/Menu/Menus/CMenuRecipe.cs',
        'Drilling.UI/Menu/Menus/CReviewOffsetDisplaySnapshot.cs', 'Drilling.UI/Views/CCorrectionView.xaml',
    ]
    protected += [x for x in norm(git('ls-files', 'Drilling.UI/Menu/Menus/CMenuCorrection*.cs')).splitlines() if x not in protected]
    for path in protected: check('preserved:' + path, now(path) == old(path))
    contract = now('Drilling.UI/Views/CManualInspectionUiContract.cs')
    def actions(text): return re.findall(r'\b[A-Za-z_]\w*\b', text.split('public enum EN_MANUAL_INSPECTION_UI_ACTION', 1)[1].split('}', 1)[0])
    original = actions(old('Drilling.UI/Views/CManualInspectionUiContract.cs'))
    check('persisted_action_ordinals_preserved', actions(contract)[:len(original)] == original)
    xaml = now('Drilling.UI/Views/CManualInspectionView.xaml')
    for obsolete in ['ColorFilter', 'CandidateGrid', 'CorrectionPaneTabs', 'AllowManualCheck', 'InitScopeCombo']:
        check('no_obsolete_control:' + obsolete, obsolete not in xaml)
    check('runtime_processing_provenance_is_local_ignored', bool(git('check-ignore', '--no-index', 'Data/ProcessingBasis/LastProcessingBasis.json').strip()))
    bad = []
    for name in paths:
        path = R / name
        if path.is_file() and path.suffix.lower() in {'.cs', '.xaml', '.md', '.json', '.py', '.csproj'}:
            try:
                if '\ufffd' in path.read_text(encoding='utf-8-sig'): bad.append(name)
            except UnicodeError: bad.append(name)
    check('utf8_text_valid', not bad)
    output = {'passed': all(c['passed'] for c in checks), 'count': len(checks), 'baseCommit': BASE, 'checks': checks,
        'changedPaths': sorted(paths), 'badTextPaths': bad,
        'productionSha256': {name: hashlib.sha256((R / name).read_bytes()).hexdigest() for name in sorted(ALLOWED | set(protected))},
        'sourceHashPolicy': 'Tested Windows raw bytes; publication additionally normalizes Git BOM/line endings.'}
    target = Path(args.report); target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'passed': output['passed'], 'count': len(checks), 'failed': [c['name'] for c in checks if not c['passed']]}))
    return 0 if output['passed'] else 1
if __name__ == '__main__': raise SystemExit(main())
