"""11.24.8: Manual replacement UI and protected production scope, against .7."""
import argparse, hashlib, json, re, subprocess
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = '35ef09b13dddb2d581582689e34978b9ac285d39'
ALLOWED = {
    'Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs',
    'Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs',
    'Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs',
    'Drilling.UI/Drilling.UI.csproj', 'Drilling.UI/CRootView.cs',
    'Drilling.UI/Menu/Menus/CManualInspectionController.cs',
    'Drilling.UI/Views/CManualInspectionUiContract.cs',
    'Drilling.UI/Views/CManualInspectionView.xaml',
    'Drilling.UI/Views/CManualInspectionView.xaml.cs',
}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)

def norm(data):
    return data.decode('utf-8-sig').replace('\r\n', '\n')

def now(path):
    return norm((ROOT / path).read_bytes())

def old(path):
    return norm(git('show', BASE + ':' + path))

def between(text, start, end):
    return text.split(start, 1)[1].split(end, 1)[0]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    checks = []

    def check(name, passed):
        checks.append({'name': name, 'passed': bool(passed)})

    paths = set(norm(git('diff', '--name-only', BASE)).splitlines())
    paths.update(norm(git('ls-files', '--others', '--exclude-standard')).splitlines())
    check('only_manual_sources_tests_docs', all(p in ALLOWED or p.startswith(('docs/ManualInspection/', 'tests/ManualInspection/')) for p in paths))
    check('no_settings_data_vendor_themes_changes', not any(p.startswith(('Config/', 'Data/', 'Drilling.Common/Motion/', 'Drilling.Common/Interface/', 'Drilling.UI/Themes/')) or p.endswith(('.dll', '.exe')) for p in paths))
    check('program_version_11_24_8', ET.parse(ROOT / 'Drilling.UI/Drilling.UI.csproj').findtext('.//Version') == '2026.11.24.8')
    check('portable_version_11_24_8', 'ProgramVersion = "11.24.8"' in now('Drilling.Common/Review/ManualInspection/CManualInspectionOffsetBundle.cs'))
    protected = [
        'Drilling.Common/Recipe/CShotCoordinatePlanBuilder.cs',
        'Drilling.Common/Review/CReviewManager.cs',
        'Drilling.Common/Review/CManualInspectionCoordinateBridge.cs',
        'Drilling.Common/Station/CStageReviewSequences.cs',
        'Drilling.UI/Menu/Menus/CManualInspectionBackend.cs',
        'Drilling.UI/Menu/Menus/CReviewOffsetDisplaySnapshot.cs',
        'Drilling.UI/Menu/Menus/CMenuMonitor.cs',
        'Drilling.UI/Views/CMonitorView.xaml',
        'Drilling.UI/Views/CManualInspectionMapControl.cs',
        'Drilling.UI/Views/CManualInspectionScatterControl.cs',
        'Drilling.UI/Views/CCorrectionView.xaml',
        'Drilling.File/ReviewOffset/CReviewOffsetFile.cs',
        'Drilling.UI/CAppStartup.cs', 'Drilling.Common/Station/CStationProcess.cs',
        'Drilling.File/Script/CAutomation1ScriptFile.cs',
    ]
    protected += [p for p in norm(git('ls-files', 'Drilling.UI/Menu/Menus/CMenuCorrection*.cs')).splitlines() if p not in protected]
    for path in protected:
        check('preserved:' + path, now(path) == old(path))
    root_now = now('Drilling.UI/CRootView.cs'); root_old = old('Drilling.UI/CRootView.cs')
    start = '        private IReadOnlyDictionary<string, string> CreateShiftOffsetPreviewParameters('
    end = '        private static IReadOnlyList<ST_SHIFT_OFFSET_HEAD_ROW> BuildShiftOffsetHeadRows'
    current_method = between(root_now, start, end); old_method = between(root_old, start, end)
    check('root_only_shift_preview_overlay_changed', root_now.replace(current_method, '', 1) == root_old.replace(old_method, '', 1))
    check('shift_preview_uses_latest_csv_after_setting_overlay', 'ReviewOffsetFile().LoadLatest(recipe.Id)' in current_method and 'CReviewOffsetData.RemoveFromParameters(parameters)' in current_method and 'CReviewOffsetData.ApplyToParameters(parameters, saved)' in current_method)
    core = now('Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs')
    previous = old('Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs')
    generic = between(core, 'public ST_MANUAL_REFERENCE_VALUE ResolveReferenceValue(', '/// <summary>Validated current/captured XY only.')
    original = between(previous, 'public ST_MANUAL_REFERENCE_VALUE ResolveReferenceValue(', 'public ST_MANUAL_COPY_PREVIEW PreviewReferenceCopy(')
    check('generic_other_review_reference_resolution_preserved', generic.rstrip() == original.rstrip())
    check('manual_has_explicit_measurement_replacement_candidate', 'BuildMeasurementReplacementCandidate' in core and 'IsMeasurementReplacement' in core)
    controller = now('Drilling.UI/Menu/Menus/CManualInspectionController.cs')
    check('manual_copy_stages_replacement', 'ConfirmMeasurementReferenceCopy' in controller)
    check('manual_bulk_uses_replacement', 'PreviewMeasurementReplacement(cellKeys)' in controller and 'BuildMeasurementReplacementCandidate(preview.Keys)' in controller)
    check('cell_scope_is_explicit', 'RequireCurrentCell(currentCellNo)' in controller and 'ReplacementCellNo!=currentCellNo' in controller)
    view = now('Drilling.UI/Views/CManualInspectionView.xaml.cs')
    xaml = now('Drilling.UI/Views/CManualInspectionView.xaml')
    for obsolete in ['ColorFilter', 'AllowManualCheck', 'CalculateButton', 'ValidateButton', 'CandidateGrid', 'CorrectionPaneTabs', 'ShowFinalOffsetsButton', 'InitScopeCombo', 'InitHeadCombo']:
        check('removed_control:' + obsolete, obsolete not in xaml and obsolete not in view)
    check('legacy_correction_actions_only_rejected_in_ui', view.count('EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets') == 1 and '보정은 현재 Cell 일괄 보정 · CSV 저장에서 진행하세요.' in view)
    check('no_separate_model_save_action_in_ui', 'Tag="Apply"' not in xaml)
    check('map_keeps_cell_and_measurement_interactions', all(p in view for p in ['PositionMap', 'MoveMeasure', 'CopyReference', 'BulkApply']))
    contract = now('Drilling.UI/Views/CManualInspectionUiContract.cs')
    enum = lambda text: re.findall(r'\b[A-Za-z_]\w*\b', between(text, 'public enum EN_MANUAL_INSPECTION_UI_ACTION', '}'))
    check('existing_action_enum_ordinals_preserved', enum(contract)[:len(enum(old('Drilling.UI/Views/CManualInspectionUiContract.cs')))] == enum(old('Drilling.UI/Views/CManualInspectionUiContract.cs')))
    check('saved_offset_not_fabricated_as_measurement', 'UsesAppliedMeasurement => false' in contract and 'MeasurementXUm => HasWorkingMeasurement ? WorkingXUm : null' in contract)
    bad = []
    for path in paths:
        file = ROOT / path
        if file.is_file() and file.suffix.lower() in {'.cs', '.xaml', '.md', '.json', '.py', '.csproj'}:
            try:
                if '\ufffd' in file.read_text(encoding='utf-8-sig'):
                    bad.append(path)
            except UnicodeError:
                bad.append(path)
    check('utf8_no_replacement_characters', not bad)
    report = {'baseCommit': BASE, 'passed': all(c['passed'] for c in checks), 'count': len(checks), 'checks': checks, 'changedPaths': sorted(paths), 'badTextPaths': bad,
              'productionSha256': {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in sorted(ALLOWED | set(protected))},
              'sourceHashPolicy': 'Tested Windows checkout raw bytes; Git publication additionally normalized for line endings/BOM.'}
    target = Path(args.report)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'passed': report['passed'], 'count': len(checks), 'failed': [c['name'] for c in checks if not c['passed']]}))
    return 0 if report['passed'] else 1

if __name__ == '__main__':
    raise SystemExit(main())
