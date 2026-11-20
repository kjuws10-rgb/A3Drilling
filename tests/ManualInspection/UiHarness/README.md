# Native Manual Inspection UI harness

Run from the repository root on Windows with the .NET 8 Windows Desktop runtime and a suitable SDK:

```powershell
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI D:/A3Qa/ManualInspectionUi
```

The first argument points to the actual `Drilling.UI` folder so the harness reads the current Light/Dark dictionaries and the existing shared button/panel/grid styles. The output must be a separate local QA folder. The harness directly links the five production UI source files. Its fake controller captures requests; it does not load or start the equipment application, vendor SDK, motion controller, Vision communication or AeroScript.

An off-screen STA WPF window provides real layout/presentation and produces PNG previews plus `ui-results.json`. Checks cover both themes, table paging, the current-cell/Head/selected-cells scatter scopes, original versus proxy values, selection/rectangle selection, bounded cursor-centred zoom, visual-only pan, busy/read-only context commands, current-version controller requests and a 10,000-point full-dataset fit. The render time is a local observation, not an equipment tact or production performance guarantee.

Manual visual checks must also inspect the PNGs for clipped labels, numeric units, mixed OK/NG/proxy states and the correction/scatter/results panels. These tests complement the Common/session and production bridge/writer tests; they do not certify physical axis directions or machine safety.

For only the subsequent integration changes, append `--integration-impact`. This runs a separate targeted report (`ui-integration-impact-results.json`) rather than repeating the initial 49 checks. It covers the explicit IMPORTED filter/provenance, engine-resolved reference final F, same-Beam Flying row expansion, controller-backed full history and level-name constraints.
