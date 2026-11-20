# Native Manual Inspection UI harness

Run from the repository root on Windows with the .NET 8 Windows Desktop runtime and a suitable SDK:

```powershell
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI D:/A3Qa/ManualInspectionUi
```

The first argument points to the actual `Drilling.UI` folder so the harness reads the current Light/Dark dictionaries and the existing shared button/panel/grid styles. The output must be a separate local QA folder. The harness directly links the five production UI source files. Its fake controller captures requests; it does not load or start the equipment application, vendor SDK, motion controller, Vision communication or AeroScript.

An off-screen STA WPF window provides real layout/presentation and produces PNG previews plus `ui-results.json`. Checks cover both themes, table paging, the current-cell/Head/selected-cells scatter scopes, original versus proxy values, selection/rectangle selection, bounded cursor-centred zoom, visual-only pan, busy/read-only context commands, current-version controller requests and a 10,000-point full-dataset fit. The render time is a local observation, not an equipment tact or production performance guarantee.

Manual visual checks must also inspect the PNGs for clipped labels, numeric units, mixed OK/NG/proxy states and the correction/scatter/results panels. These tests complement the Common/session and production bridge/writer tests; they do not certify physical axis directions or machine safety.

For the subsequent integration changes, append `--integration-impact`. This runs a separate targeted report (`ui-integration-impact-results.json`) covering the IMPORTED filter/provenance, engine-resolved reference final F, same-Beam Flying row expansion, controller-backed full history and level-name constraints.

For the simplified UI and shared applied-offset Level policy, append `--sync-levels`. It creates `ui-sync-levels-results.json` and simplified Light/Dark PNGs. It checks the actual native description/confirm/cancel dispatch through an internal test seam, default active-B colors across Cell/Head/Rough/Fine/manual origins, applied zero versus missing offset, separation from candidate F and measurement results, common inclusive Level thresholds, external source/path details, recent-result selection without a file picker, and version changes while a confirmation is open. The main 49-check suite and 23-check integration suite were updated for the intentionally removed SIM/duplicate controls and are still separate from the new checks. No test starts the equipment Root, Manager, SDK, controller or Vision service.

For measurement-derived correction coverage and the compact workspace, append `--coverage-compact`. The 71 checks verify measured-history classification and Level/scatter/filter/table consistency without fabricating raw data, explicit zero versus missing offsets, native measurement precedence, recent errors and invalid/masked/candidate boundaries. They also check both themes at 1480×900, 1120×720 and 1120×560: no default left/right scrolling, collapsed results/scatter/details, a map that fits inside its parent and retains every visible slot, and the five point-context actions after removal of the right-side buttons. PNGs use the `coverage-<theme>-<size>.png` names. The 560-pixel height simulates the smaller view left below the existing Correction chrome. Expanded tables, scatter, settings or error messages can intentionally need local scrolling.
