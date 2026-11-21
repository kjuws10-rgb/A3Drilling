# Manual Inspection 독립 검증

현재 `11.21.1`의 통합 Offset/Init·휴대 JSON·현재 모델 재연결·산포 기본값 검사는 [OFFSET_INIT_QA.md](OFFSET_INIT_QA.md)를 따른다. 이전 검증 기록은 아래에 보존한다.

이 폴더의 Common 회귀는 새 프로그램의 실제 `Drilling.Common/Review/ManualInspection/*.cs`를 직접 Include한 .NET 8 콘솔입니다. 별도의 SDK나 장비 연결이 필요하지 않으며 실제 장비 프로그램을 시작하지 않습니다.

```powershell
dotnet run --project tests/ManualInspection/ManualInspection.Tests.csproj -c Release -- --report core-results.json
dotnet run --project tests/ManualInspection/BridgeWriterHarness/BridgeWriterHarness.csproj -c Release -- --report bridge-writer-results.json
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -c Release -p:UseActualAssemblies=true -- --report controller-production-results.json
dotnet run --project tests/ManualInspection/ExternalResultsHarness/ExternalResultsHarness.csproj -c Release -- --report external-results.json
python tests/ManualInspection/verify_scope.py --report manual-scope-results.json
```

Common 회귀는 대표값·작업 상태·원측정 보존, 계산/검증/저장 앞뒤의 불변성, 같은 raw의 재누적 차단, 좌표/Revision 변경, 같은 Cell/Shot의 Beam 공유, 중지/오류/잔여 재실행, 세션 검증·원복, 파일 소수6자리 mm 양자화와 산포도를 확인합니다. 실제 장비 명령은 포함하지 않습니다.

`BridgeWriterHarness`는 새 좌표 bridge, 기존 순수 좌표 계획/축변환, 실제 Review Offset writer/CSV parser 소스를 링크합니다. 거대한 장비 Manager를 실행하지 않도록 계약 DTO와 기존 `VisionErrorToStageOffset` 순수 함수만 fixture에서 분리했습니다. 따라서 전체 장비 시퀀스 합격 시험과 다릅니다. `fixture-provenance.json`은 이 범위를 설명합니다. 시험은 매번 고유한 임시 디렉터리에 합성 CSV를 만들며 실제 Config/Data 파일을 읽거나 변경하지 않습니다.

별도 소스 설정 관찰이 필요한 경우에만 `A3_MANUAL_QA_PARAMETERS` 환경변수로 로컬의 검증용 `Dictionary<string,string>` JSON 경로를 지정할 수 있습니다. 이는 선택 기능이며 설정 자료는 이 저장소에 포함하지 않습니다. 해당 시험은 실제 순수 planner의 모든 점과 Head별 Encoder Scale을 이용한 독립 Stage 추종 제거식, 실제 Masking/Chess 제외 조건을 대조합니다. `CReviewManager.CreateAllPlan` 객체를 실행한 시험은 아니며, 소스의 동일한 제외 조건을 사용한 수치 관찰입니다. 기존 설정에서 Field 밖인 점이 발견되면 별도 관찰 결과로 남기며 장비 실행 합격이나 파일 저장 회귀 결과에 합산하지 않습니다.

`UiHarness`는 실제 WPF View/그리기 컨트롤과 기존 Light/Dark 테마를 사용하되 Controller는 fake입니다. HTML과의 기능·데이터 흐름 대조는 [검증 기준](HTML_PARITY_CHECKLIST.md)을 따릅니다. 원본 첨부를 실행한 HTML oracle의 결과는 별도이며, HTML 시험을 새 WPF/실장비 시험과 합산해 한 종류의 검증처럼 표현하지 않습니다.

`ControllerHarness`의 `UseActualAssemblies=true`는 현재 저장소의 `Drilling.UI.csproj`를 ProjectReference하여 실제 Controller/Common/Replay/UI 계약을 사용합니다. 일반 WPF `Application`과 STA Dispatcher만 시작하고 장비용 `Drilling.UI.App`, Root, Manager를 생성하지 않습니다. 측정·저장은 합성 fake Backend를 주입하며 실제 PLC/Vision/SDK를 호출하지 않습니다. 로드된 벤더 Assembly가 없는지도 검사합니다. 기본 소스 링크 모드는 같은 실제 신규 소스를 Include하되 Backend 계약과 해시·원자 텍스트 함수만 fixture로 분리합니다. 두 모드의 중복 시험은 별도의 검증 횟수로 부풀리지 않습니다. STOP 요청 후 cleanup 반환까지 Busy 유지, 명령 중복 차단, 저장 실패 원자성, 부분 측정·retry, 대표 복사·중복 raw, 세션/CSV·레벨·원복, 같은 Run의 보정 이후에도 구멍 측정값이 바뀌지 않는 Replay를 확인합니다.

정식 프로그램 빌드, Controller와 기존 Review Manager의 연결, 실제 PLC/Vision 지연·비상정지·모션 도착·트리거/결과 수 및 장비 소프트리밋은 각자의 별도 검증 범위입니다.

이전 측정 이력·접기 후속에서는 실제 assembly Controller 71개(기존 65+표시/CSV/raw 보존 6개), WPF UI 49/23/34개 회귀와 측정 이력·우클릭·기본 스크롤·지도 클리핑 71개를 실행했습니다. `--coverage-compact` UI 옵션으로 새 화면 검사를 재현합니다. 상세 범위는 [COVERAGE_COMPACT_QA.md](COVERAGE_COMPACT_QA.md), 이전 결과 동기화 기록은 [SYNC_QA.md](SYNC_QA.md)입니다. 계산 111/bridge-writer 22/외부 결과 병합 37 검사는 관련 구현을 바꾸지 않은 이전 Commit의 별도 근거이며 이번 수정의 재실행 수로 합산하지 않습니다. 첨부 HTML 원본은 `docs/ManualInspection/Reference`에 보존했습니다.

## 현재 Review 교차검증

[현재 검토·수정·현장 한계](HARDENING_QA.md)를 참조한다. 정식 Release 0/0, 엔진113·writer35·Backend16·실제 Controller89·외부projection53·WPF49/23/34/71/30·scope15를 실행했다. 실제 Controller와 source-link86은 중복 합산하지 않는다. 세부 Source/DLL/report SHA는 [manifest](../../docs/ManualInspection/validation.json)에 있다. BackendReadHarness는 실제 새 Adapter와 Recipe/Setting/Review 순수 planner 및 CSV writer를 연결하지만, 장비 Manager/Root/SDK는 생성하지 않는다.


## 화면·그래픽 최신 후속

[현재 시각/입력 검증](VISUAL_USABILITY_QA.md)은 798359e 기준의 새 `feature/manual-inspection-visual-polish-20261120`이다. 정식 Release0/0, 실제 Controller93와 WPF49/23/34/71/30/105=312개를 실행했다. source-linked preflight89는 중복/부분 이전 근거이며 최종 실제93과 합산하지 않는다. 앞선 엔진113/writer35/Backend16/projection53은 소스 동일성을 확인한 이전 Commit의 근거로 구분한다. 화면용 80ms coalescing은 모든 측정·최종/오류/STOP 표시·잔여 복구를 보존한다. 테스트 회수와 소스/DLL/그림 SHA는 최신 manifest를 따른다.


## 2D 지도 중심 최신 후속

[현재 지도 중심 검증](MAP_FIRST_QA.md)은 7c6cd31 기준 `feature/manual-inspection-map-first-20261120`이다. 실제 Controller93/93, WPF49/23/34/71/30/105/73=385/385, 전체 Release 경고0/오류0, 범위20개를 통과했다. 이전 UI312개는 삭제하지 않고 접기 위치·짧은 문구·명시적 선택 등 의도한 UI 변경에 따른 setup/기대만 조정했다. 보정 엔진/Backend/저장/Controller는 변경하지 않았다. 이전113/35/16/53은 재실행 수로 합산하지 않는다. 소스·Assembly·보고서·그림 SHA는 최신 validation.json을 따른다.


## 전체 Cell·산포 축·파일 작업 최신 후속

[현재 검증](CELL_OVERVIEW_QA.md)은 b38867d 기준 `feature/manual-inspection-cell-overview-20261121`이다. 전체 Release0/0, 순수 엔진113/113, 실제 Assembly Controller108/108, 실제 CSV/Backend24/24, WPF457/457(기존385+신규72), 범위27/27을 확인했다. `UiHarness --cell-overview`와 Controller/BackendReadHarness의 현재 명령으로 재현한다. 이전 writer35/projection53은 구현 동일성을 확인한 과거 증거로 구분하며 이번 재실행 수에 합산하지 않는다. 실제 장비·벤더 SDK·물리 입력/DPI 전환 검증은 별도다.
