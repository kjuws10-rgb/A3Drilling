"""Verify the .6 final-offset fix scope against the published .5 source."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = '0103581022bfba6fe7a7b050cd357ceefbee7143'
ALLOWED = {
    'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs',
    'Drilling.UI/Drilling.UI.csproj',
    'Drilling.UI/Menu/Menus/CManualInspectionController.cs',
    'Drilling.UI/Views/CManualInspectionUiContract.cs',
    'Drilling.UI/Views/CManualInspectionView.xaml.cs',
    'Drilling.UI/Menu/Menus/CMenuMonitor.cs',
    'Drilling.UI/Menu/Menus/CReviewOffsetDisplaySnapshot.cs',
    'Drilling.UI/Views/CMonitorView.xaml',
}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)

def normalized(content):
    return content.decode('utf-8-sig').replace('\r\n', '\n')

def current(path):
    return normalized((ROOT / path).read_bytes())

def previous(path):
    return normalized(git('show', f'{BASE}:{path}'))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    checks = []
    def check(name, passed):
        checks.append({'name': name, 'passed': bool(passed)})
    paths = set(normalized(git('diff', '--name-only', BASE)).splitlines())
    paths.update(normalized(git('ls-files', '--others', '--exclude-standard')).splitlines())
    check('only_named_manual_monitor_sources_and_tests_docs', all(
        p in ALLOWED or p.startswith(('tests/ManualInspection/', 'docs/ManualInspection/')) for p in paths))
    check('configuration_data_vendor_and_themes_unchanged', not any(
        p.startswith(('Config/', 'Data/', 'Drilling.Common/Interface/', 'Drilling.Common/Motion/', 'Drilling.UI/Themes/'))
        or p.endswith(('.dll', '.exe')) for p in paths))
    check('daily_program_version_11_24_6', ET.parse(ROOT / 'Drilling.UI/Drilling.UI.csproj').findtext('.//Version') == '2026.11.24.6')
    check('portable_version_11_24_6', 'ProgramVersion = "11.24.6"' in current('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
    protected = [
        'Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs',
        'Drilling.Common/Review/CManualInspectionCoordinateBridge.cs',
        'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
        'Drilling.Common/Review/CReviewManager.cs',
        'Drilling.Common/Station/CStageReviewSequences.cs',
        'Drilling.File/ReviewOffset/CReviewOffsetFile.cs',
        'Drilling.UI/Menu/Menus/CManualInspectionBackend.cs',
        'Drilling.UI/Menu/Menus/CMenuCorrection.ManualInspection.cs',
        'Drilling.UI/CRootView.cs', 'Drilling.UI/CAppStartup.cs',
        'Drilling.UI/Views/CCorrectionView.xaml',
        'Drilling.UI/Views/CManualInspectionMapControl.cs',
        'Drilling.UI/Views/CManualInspectionScatterControl.cs',
    ]
    protected += [p for p in normalized(git('ls-files', 'Drilling.UI/Menu/Menus/CMenuCorrection*.cs')).splitlines()
                  if p not in protected]
    for path in protected:
        check('preserved:' + path, current(path) == previous(path))
    view = current('Drilling.UI/Views/CManualInspectionView.xaml.cs')
    old_view = previous('Drilling.UI/Views/CManualInspectionView.xaml.cs')
    projection = lambda text: text.split('private CManualInspectionUiPoint FinalOffsetPoint', 1)[1].split('private void RefreshFinalOffsetInfo', 1)[0]
    check('final_map_uses_saved_file_projection_not_working_errors', projection(view) == projection(old_view))
    check('view_switch_requires_verified_new_saved_revision', all(token in view for token in (
        'after.LastSaveVerificationCode == "VERIFIED"',
        'after.LastSavedRevisionId != current.LastSavedRevisionId',
        'after.LastSavedRevisionId == after.FinalOffsetRevisionId')))
    monitor = ET.parse(ROOT / 'Drilling.UI/Views/CMonitorView.xaml').getroot()
    info = [node for node in monitor.iter() if node.get('Text') == '{Binding CurrentScreen.Monitor.CoordinateReviewSavedInfoText}']
    check('monitor_saved_csv_info_is_separate_and_uses_existing_theme_key', len(info) == 1
          and info[0].get('Foreground') == '{DynamicResource TextSecondaryBrush}')
    xkey = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'
    check('monitor_info_brush_exists_in_both_preserved_themes', all(
        any(node.get(xkey) == 'TextSecondaryBrush' for node in ET.parse(ROOT / f'Drilling.UI/Themes/Theme{theme}.xaml').getroot().iter())
        for theme in ('Light', 'Dark')))
    bad_text = []
    for path in paths:
        file = ROOT / path
        if file.is_file() and file.suffix.lower() in {'.cs', '.xaml', '.md', '.json', '.py', '.csproj'}:
            try:
                if '\ufffd' in file.read_text(encoding='utf-8-sig'):
                    bad_text.append(path)
            except UnicodeError:
                bad_text.append(path)
    check('utf8_no_replacement_characters', not bad_text)
    result = {'baseCommit': BASE, 'passed': all(c['passed'] for c in checks), 'checks': checks,
              'changedPaths': sorted(paths), 'badTextPaths': bad_text,
              'productionSha256': {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in sorted(ALLOWED | set(protected))}}
    output = Path(args.report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'passed': result['passed'], 'count': len(checks), 'failed': [c['name'] for c in checks if not c['passed']]}))
    return 0 if result['passed'] else 1

if __name__ == '__main__':
    raise SystemExit(main())
