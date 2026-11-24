# 11.24.2 · master 통합 교차검증

기준 master `0219666f9053b907d96d7e0aa7d0c6c1be3a73d4`, 가져온 feature `fb869deb2e02b695c8fd39d57bbd01f80932e5c7`. 새 브랜치 `feature/manual-inspection-master-integration-20261124.2`만 게시하고 master에 병합하지 않는다.

## 검증을 나눈 이유

`master..feature`의 전체 파일 차이를 덮어쓰면 master에서 추가된 운영 자료·MELSEC/Product 기능을 지울 수 있다. 공통 조상부터 각 이력의 변경을 따로 분류한 뒤 3-way로 통합했다. 독립 검토자는 Git blob과 작업 트리를 대조하고, Monitor의 변경 구간을 별도 방식으로 합쳐 최종 본문을 확인했다.

## 교차검증

1. **이식 범위:** master 고유 1,601파일·전체 Config/Data 2,051파일 보존, feature 239파일 전부 포함. 공유 변경은 Monitor와 csproj뿐이다. 실제 생산 소스는 29개다.
2. **호환성·실행 근거:** master의 Product 필드 확장·Stage Glass Data와 기존 Review/Manual 소비자 계약을 추적했다. Release DLL과 실제 Controller/Backend 보고서 SHA를 대조한다. Core와 WPF는 소스 링크 검사이며 실제 Assembly 검사와 구분한다.
3. **최종 자료:** 현재 소스/DLL·검사 결과·manifest·내부 링크·화면 그림·버전과 master 보존을 최종 독립 감사한다. 완료 여부와 수치는 [현재 manifest](../../docs/ManualInspection/validation.json)의 이번 `.2` 보고서를 따른다.

## 재실행한 검사

| 검사 | 확인 범위 |
| --- | --- |
| `dotnet build Drilling.sln -c Release --no-restore` | 통합 생산 코드 전체 컴파일. 설비 실행은 없음 |
| Core | 최초 상태·NG·대표 복사·Init·후보·중복·Undo/Redo·모델/Run/좌표·checkpoint |
| 실제 Controller Assembly | 측정·부분 실패·새 선택·STOP 정리·파일 복귀·저장/자동 갱신·상태 복사 |
| 실제 Backend | 현재 레시피/CSV 연결·Native 최초 로딩·휴대 Offset 버전·Recipe 13/Monitor 15 표시 회귀 포함 |
| Bridge/Writer | 좌표 변환·단위·설계/스캐너 축·저장·원복·동시 파일 잠금 |
| 실제 Common 외부 결과 투영 | 다른 Review 결과 출처·모델/Run/회차·값 없음/실측 0·모호한 대상 |
| WPF 13종 | native / integration-impact / sync-levels / coverage-compact / hardening / visual-usability / map-first / cell-overview / offset-init / measurement-workflow / bulk-history / representative / side-panels |
| 통합 범위 검사 | master 보존과 `.1` 구현 보존을 분리, Monitor의 정확한 3-way 결과, 버전·탭 연결·Init·충돌·diff check |

최종 실행 결과는 Release 경고/오류 **0/0**, Core **221**, 실제 Controller **214**, 실제 Backend **111**, Bridge/Writer **35**, 실제 Common 외부 투영 **53**, WPF **843**, 통합 범위 **40** 전부 통과다. Backend에는 master 호환 신규 4개가 포함된다. `MasterCompatibilityCases.cs`는 실제 임시 Product 파일·정적 필드 변환·RunIdentity만 실행하고, CMelsec·장비 Manager 인스턴스를 만들지 않는다. Round2의 Backend **107** 보고서는 신규 4개 추가 전 시점의 근거로 별도 보존하며 Round3 및 현재 manifest는 최종 **111**을 따른다.

실행 예:

```powershell
python tests/ManualInspection/verify_master_integration.py --report D:/A3Qa/11.24.2/scope-results.json
dotnet run --project tests/ManualInspection/BackendReadHarness/BackendReadHarness.csproj -c Release -- --report D:/A3Qa/11.24.2/backend-read.json
```

Controller의 `UseActualAssemblies=true` 경로는 [기존 실행 설명](ControllerHarness/README.md)을 따른다. WPF는 [기존 설명](UiHarness/README.md)의 각 모드를 별도 실행한다. 생산 빌드는 직렬로 실행하고, 검사 도구에 공통 `BaseIntermediateOutputPath`를 전이 참조까지 넘겨 서로 다른 프로젝트의 `project.assets.json`을 공유시키지 않는다.

## 주장하지 않는 범위

합성 측정 자료·임시 CSV와 일반 WPF Window만 사용한다. 설비 Root/Manager 생성·벤더 SDK 로드/실행·실제 통신/이동·AeroScript·레이저 및 실제 기판 가공은 하지 않았다. UI의 실설비 표기는 합성 fixture 모드이며 장비 접속 사진이 아니다. 물리 축 방향·인터락·실물 보정 결과는 현장 검수 항목이다.
