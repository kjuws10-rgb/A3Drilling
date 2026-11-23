# Manual Inspection 11.23.0 · 일괄 보정·10단계 이력·빈 측정값 검증

기준 Commit은 `61578dbefbef8d5d5ebe83e4ec289e452771904f`다. 기존 측정·표시·파일 회귀를 유지하고, 사용자가 바꾼 동작만 새 기대값으로 검사한다. 최종 횟수와 소스·DLL·보고서 SHA는 [현재 manifest](../../docs/ManualInspection/validation.json)를 따른다.

## 새 시나리오

- **측정과 보정 분리:** 이동 후 측정은 저장하지 않는다. 제거한 이동 후 측정·보정 action은 직접 호출해도 장비·파일 작업 전에 거부한다. 일괄 보정은 선택·필터와 무관하게 현재 모델 전체의 유효한 새 OK/채택/명시적 대표 결과를 계산·검증·저장한다. NG·미측정·Masking·소비한 raw는 제외하고, 한 Shot의 서로 다른 Beam 보정값은 충돌을 무시하지 않고 전체 저장을 막는다.
- **실패 원자성:** 일괄 저장·Undo·Redo 실패는 실제 활성값과 이력 cursor를 유지한다. 미저장 Init/절대값 편집 중에는 저장·취소 전 다른 보정·이력 이동을 막는다.
- **10단계:** 14회 새 측정을 적용한 뒤 정확히 최근 10개 상태까지만 Undo한다. 끝을 넘는 요청은 저장하지 않으며, Redo는 마지막 적용 상태까지 돌아간다. Undo 뒤 새 보정을 적용하면 이전 Redo 분기를 폐기한다. 원본 감사 Revision 기록은 지우지 않는다.
- **기준이 바뀐 잔차:** Undo/Redo로 값 또는 Init 상태가 달라지는 Shot의 미저장 잔차·대표 자격은 해제한다. raw 원본은 보존하고 소비 기록도 저장해 같은 구멍을 다시 더하지 못하게 한다. 바뀌지 않은 Shot의 새 실측과 Init 시각은 보존한다.
- **상태와 세션:** Init 0과 실제 측정 0, 최초 보정 없는 모델, mm 6자리 저장 양자화, Undo한 cursor의 세션 왕복, null·중복·외부 Key·잘못된 cursor를 검사한다. 좌표계나 외부 활성 Revision이 바뀌면 이전 이력을 현재 모델에 강제로 쓰지 않는다. 같은 기준의 파일 열기·파일 보정·원래 화면 복귀는 동일 적용 이력을 이어간다.
- **대표값:** 빈 대상에 실제 0 대표값을 복사해 저장하면 상태도 측정 기반으로 전환한다. Undo는 없음 상태, Redo는 저장된 실측 0 상태를 복원한다. EDITED/IMPORTED 결과에 대상 자신의 원시값이 없어도 유효한 대표 출처를 보존하며, 실제로 없는 대상 raw는 만들지 않는다. 대표 Key/Token·Run·좌표계 근거가 없는 세션은 계속 거부한다.
- **모든 Review Init:** Simple·Rough·Fine·ReviewData에서 측정 보정의 상태와 표시를 확인한다. 좌표 계산의 중립값 0과 유효한 측정 0을 분리하고, 화면은 `—`/미측정으로 보여준다. 하드웨어·Default·First Beam·Distortion 값은 바꾸지 않는다. Fine Init은 이전 측정·통계·적용 자격을 해제한다. 외부 Init 관측은 Rough 대표 범위·Fine 보정 열까지 무효화하되, 새 실측 이후 같은 초기화 epoch를 다시 읽어도 지우지 않는다.

## 기존 시험을 조정한 이유

기존 Core·Controller·WPF 시험을 삭제하지 않았다. 자동 측정·보정 성공 사례는 이제 제거 action 거부 또는 이동 후 측정/별도 보정으로 바꿨다. 우클릭 메뉴는 이동 동작 3개에서 2개, 실행 동작 5개에서 4개로 줄었고 비활성 선택 요약은 동작 수에 포함하지 않는다.

Undo 후 옛 잔차·대표가 유효하던 기대는 새 기준에서 재측정이 필요한 것으로 바꿨다. 32×6 조합 회귀는 다음 회차에 새 대표 실측을 취득해 같은 시나리오를 계속 검증한다. 대표의 출처가 유효하면 대상 자신의 raw가 없다는 이유만으로 복구를 막지 않으며, 기존 무효 세션 검사는 대표 Reference Token이 없는 경계로 유지했다. 실제 시각 기반 writer를 모사하는 fake는 새 Backend가 세션을 가져와도 기존 `REV-1`을 재발급하지 않는다.

## 범위 검사

`verify_scope.py`는 새 기준 Commit에서 허용한 Manual Inspection 및 Review Init 파일만 바뀌는지 확인한다. 기존 Review Manager/CSV writer·Stage 시퀀스·좌표 bridge·테마/SDK/Config/Data와 다른 Correction partial은 원문을 비교한다. 좌표 planner와 같은 파일에 있는 변경은 `CReviewOffsetData`의 측정 상태 지역으로 제한하며, planner 본문·스캐너 변환·DESIGN↔SCANNER 식은 별도로 원문을 비교한다. 이전 27개 범위 검사는 이번 허용 범위에 맞춰 29개로 구성했다. 이는 실행 시험 삭제나 수치 알고리즘 면제를 뜻하지 않는다.

## 재현 명령

```powershell
dotnet run --project tests/ManualInspection/ManualInspection.Tests.csproj -c Release -- --report core.json
dotnet run --project tests/ManualInspection/BridgeWriterHarness/BridgeWriterHarness.csproj -c Release -- --report writer.json
dotnet run --project tests/ManualInspection/ExternalResultsHarness/ExternalResultsHarness.csproj -c Release -- --report projection.json
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -c Release -p:UseActualAssemblies=true -- --report controller.json
dotnet run --project tests/ManualInspection/BackendReadHarness/BackendReadHarness.csproj -c Release -- --report backend.json
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI private-output --bulk-history
python tests/ManualInspection/verify_scope.py --report scope.json
```

WPF 전체 회귀는 옵션 없음, `--integration-impact`, `--sync-levels`, `--coverage-compact`, `--hardening`, `--visual-usability`, `--map-first`, `--cell-overview`, `--offset-init`, `--measurement-workflow`, `--bulk-history`를 각각 실행한다. ProjectReference를 사용하는 실제 Controller/Backend는 전체 빌드와 직렬로 실행해 출력 파일 경합을 피한다.

Controller는 합성 fake Backend, 실제 CSV/Backend 검사는 임시 합성 자료와 순수 Recipe/Review manager·공개 화면 모델을 사용한다. 화면 모델 일부는 private 자료를 reflection으로 주입해 계산·상태만 검증한다. 실제 CMenu/장비 App/Root/Manager·벤더 SDK·모션/PLC/Vision/AeroScript/레이저는 생성·실행하지 않는다. 이런 시험은 실장비 도달/정지 타이밍·트리거 수·인터락·가공 정밀도 합격을 뜻하지 않는다.
