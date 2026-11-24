# Manual Inspection · 다른 Review 경로 교차검증

기준 Commit: `4cbf259a16cd04bdab0bf6f76cd4591c681c109f`. 새 브랜치: `feature/manual-inspection-review-hardening-20261120`. master 병합·설비 배포는 하지 않는다.

## 수정한 문제

| 경계 | 문제와 수정 |
| --- | --- |
| 활성 보정 파일 | 같은 경로의 내용 교체가 이전 Revision으로 보였다. 읽은 행과 같은 파일 bytes의 SHA를 반환하고, Revision과 저장 직전 비교에 SHA를 포함했다. 저장 비교부터 게시까지 기존 파일의 읽기 잠금도 유지한다. |
| 여러 저장 객체·프로세스 | 인스턴스별 잠금만 있어 같은 기준으로 두 저장이 성공할 수 있었다. 저장소 공통 잠금과 프로세스 간 파일 잠금으로 직렬화한다. 대기는 취소 가능하다. |
| 외부 적용 이력 | 한 번 만든 색인이 다른 객체의 저장이나 같은 크기·수정시각의 교체를 놓쳤다. 이력 목록·내용 SHA를 확인하고 변경 시 다시 색인한다. |
| 이력 저장 실패 | 기존 파일에 직접 append하다 실패하면 반쪽 행이 남을 수 있었다. 전체 다음 이력을 임시 파일에 쓴 후 원자 교체한다. |
| PC 시계 역전 | 새 파일 이름이 기존 활성 파일보다 과거여서 저장 성공값이 실제 활성값이 되지 않을 수 있었다. 새 버전 이름의 순서를 보장하고, 해석 불가능한 활성 이름은 저장을 차단한다. |
| 다른 메뉴의 원복 | 기존 Rollback이 활성 중복 적용 이력을 지워 Manual Inspection의 소비 증거도 사라질 수 있었다. 원복 시 `_committed`에 과거 저장 증거를 보존한다. 기존 메뉴의 활성 이력 조회와 새 불변 증거 조회를 분리한다. |
| 기판/가공 회차 | 같은 Host Product·Glass ID를 재사용하면 회차가 같아질 수 있었다. Product 생성 시각·Recipe와 가공 완료 Shift를 포함하고, 로딩 시작 전에 회차를 캡처하여 로딩 후 다시 확인한다. |
| Shift 출처 | 현재 Product의 Recipe와 선택 Recipe가 다르면 완료 기록의 Shift 출처를 입증할 수 없다. 해당 Shift를 자동 차용하지 않는다. 비영점 Shift가 모호하면 조회 전용으로 차단한다. Product 없는 기존 수동 MOF 흐름은 유지한다. |
| 이전 버전 자료 | 새 회차 식별자를 붙여 옛 raw를 새 측정으로 바꾸지 않는다. 같은 물리 회차의 옛 commit 증거 또는 현 Revision의 미완료 세션·후보가 발견되면 원본을 보존한 조회 전용으로 연다. 새 Product/가공 회차에서 진행한다. |
| Controller 잠금 | 화면 구독자·상태 보고 예외가 정리 통지를 막을 수 있었다. 구독자를 개별 격리하고, 명령 잠금과 cleanup 통지는 finally에서 보장한다. |
| STOP | 취소 callback 또는 장비 STOP 전달 오류가 나도 두 경로를 각각 시도한다. 오류를 남기고 실제 작업 반환까지 Busy와 cleanup 대기를 유지한다. |
| 세션 가져오기 | 과거 파일 조회 상태가 남아 자동 결과 갱신이 재개되지 않았다. 현재 세션 가져오기 후 파일 조회 고정과 위치·동기화 상태를 초기화한다. |
| 대표 복사 감사 | 복사 후 대표점을 재측정하면 옛 복사값에 새 raw 토큰을 기록할 수 있었다. 복사 당시 Proxy의 불변 참조 토큰을 기록한다. |
| UI 일관성 | 산포도에서 고른 숨김 점을 지도에 드러내고, 갱신 후 자격을 잃은 대표 지정을 해제한다. 조회 전용 안내·입력 오류를 갱신하며, 확인 중 자료 변경·조회 전용 요청을 UI에서도 차단한다. STOP은 항상 전달한다. |

## 실제 추적 범위

| 흐름 | 확인한 생산 코드·검증 경계 |
| --- | --- |
| Simple/Rough/Fine/Auto/수동 결과 → 작업공간 | 실제 결과 Catalog/CSV → Backend의 현재 Recipe·Glass·시간·Shift·mode → `CManualInspectionResultProjection` → 실제 planner의 Cell/Head/Shot/Beam 기하 → IMPORTED 표시를 추적했다. 외부 결과를 native 보정 대표로 자동 승격하지 않는다. |
| 직접 측정·Flying | Controller → Current/Guard → 기존 Review Manager의 OneShot/Fine 공통 잠금·Process Move/IOF → 공식 결과 저장 → 측정 완료 반영 순서를 읽었다. 컨트롤 시험은 fake Backend이고 장비 시퀀스의 실제 실행 시험이 아니다. |
| 보정 | 직접 B+편차 / 대표의 확정 F → 공통 Field·총량·증감·공유 Shot 검사 → 원자 비교 저장 → engine commit. DESIGN µm↔기존 CSV mm, Vision Flip 1회와 기존 가공 1µm 반올림은 유지한다. |
| 원복·재시작 | Manual의 새 Revision 원복과 기존 메뉴의 파일 Rollback을 구분했다. 세션과 실제 commit 증거로 동일 raw의 재누적을 막는다. |
| UI·운전 인터락 | 기존 CRootView·Correction/Review 실행 잠금과 새 Controller를 읽었다. 기존 SIMPLE/INSPECTION/Theme·장비 시퀀스·Review 실행 본문은 Git 내용 동일성을 확인한다. |

## 검증 결과

| 검사 | 결과·실행 범위 |
| --- | --- |
| 전체 Drilling.sln Release | 경고 0 / 오류 0 |
| 순수 보정 엔진 | 113/113. 32 seeds×6 cycles=192 복합 상태 시나리오를 한 회귀에 포함한다. 이 시나리오 수를 별도 시험 수로 더하지 않는다. |
| 좌표 bridge·실제 Offset writer | 35/35. 같은 경로 교체, 두 객체 CAS, 이력 캐시, 시계 역전, 원복 증거, 별도 프로세스 파일 잠금·취소 포함. |
| 실제 Backend 로딩 연결 | 16/16. 실제 Recipe/Setting/Review 순수 planner·CSV/Offset writer와 합성 임시 데이터. 장비 Manager는 생성하지 않는다. |
| 실제 Assembly Controller | 89/89. 일반 WPF STA·fake Backend. observer·STOP 오류, 갱신·세션·복사 출처 포함. |
| 외부 결과 projection | 53/53. 실제 Common과 합성 결과·기하. |
| 실제 WPF 화면 | 기존 49+23+34+71 및 새 30 모두 통과. Light/Dark·1120×560 포함. 기본 무스크롤·지도 부모/점 클리핑 확인. |
| 변경 범위·기존 구현 보존 | 15/15. Config/Data/SDK/Theme·기존 탭·장비 실행 본문·순수 계산 코드 보존. |

Controller source-link 86개는 실제 Assembly 89개와 중복되므로 합산하지 않는다. 예전 Writer에 새 API 인자 형식만 맞춘 대조는 35개 중 11개 보장 검사에서 실패했고, 현재 Writer는 통과했다. 그중 신규 SHA/증거 API 검사는 구버전에 없던 보장의 확인이며, 모두가 기존 기능의 동일한 사용자 재현 결함이라는 뜻은 아니다. UI 신규 30개 중 구버전에서 13개가 실패했고 현재 모두 통과했다.

새 실행 명령:

```powershell
dotnet run --project tests/ManualInspection/BackendReadHarness/BackendReadHarness.csproj -c Release -- --report backend-read-results.json
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI D:/A3Qa/Hardening --hardening
```

나머지 명령은 [테스트 안내](README.md), 자세한 현재 결과·소스 SHA는 [검증 manifest](../../docs/ManualInspection/validation.json)를 참조한다. 테스트 원문은 각 하네스의 `HardeningCases.cs`, 엔진의 `StateSequenceCases.cs`, BackendReadHarness의 `Program.cs`에 있다.

## 현장 확인이 남는 항목

1. 실제 장비의 Process Move/IOF 도착·Busy·STOP 정리, Vision 단위/방향·트리거 수·UID·P2P/Flying 응답, 다른 Correction의 회귀는 별도 실기 시험이 필요하다. 이번에는 벤더 DLL·장비 프로그램·모션·AeroScript·레이저를 실행하지 않았다.
2. 기존 MOF 완료 저장 형식에는 Recipe/Glass 식별자가 없다. 이번 변경은 모호한 경우 자동 동작을 막는 보수적 대응이다. 출처를 완전히 연결하려면 별도 사양으로 완료 기록에 식별자를 추가해야 한다.
3. 기존 Rough/Fine 호출자의 **읽기→계산→저장 사이 전체 계산 구간**은 새 CAS 호출로 바꾸지 않았다. 파일 저장 거래는 직렬화되지만, 여러 설비 앱이 같은 Recipe를 동시에 운전하는 환경을 승인한 결과가 아니다. 한 설비·한 실행 주체로 현장 검증한다.
4. 파일 잠금을 따르지 않는 외부 편집 도구의 모든 임의 변경을 거래 전체에서 통제하는 것은 아니다. 실제 활성 파일은 CAS 동안 read-handle로 보호하며, 외부 변경은 다음 읽기/저장 기준 확인에서 거부한다. 저장소 루트는 잠금 파일을 만들 수 있는 권한이 필요하다.
5. 기존 설정에서 관찰한 Field 밖 좌표와 실제 허용 보정량 승인 항목은 유지한다. 보정 파일 저장만으로 이미 가공된 구멍은 움직이지 않는다. 새 가공 후 재측정해야 한다.
