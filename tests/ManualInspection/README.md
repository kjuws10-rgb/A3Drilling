# Manual Inspection 독립 검증

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

후속 결과 동기화 버전은 계산 111개, bridge/writer 22개, 실제 assembly Controller 65개, 외부 결과 병합 37개를 실행했습니다. Controller에는 기존 38개와 공통 Level·외부 최신/과거 결과·후보/잔여 보호·실제 파일/폴더 감시·갱신 중 재알림·취소·mode 저장/읽기·EDITED 시각 보존 27개가 포함됩니다. WPF UI는 49개와 증분 23개, 단순화·Level 34개를 각각 실행했습니다. 상세 범위는 [SYNC_QA.md](SYNC_QA.md)를 참조합니다. 첨부 HTML 원본은 `docs/ManualInspection/Reference`에 보존했습니다.
