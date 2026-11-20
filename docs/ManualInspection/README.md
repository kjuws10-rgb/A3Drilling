# MANUAL INSPECTION · 결과 동기화·공통 Level

## 현재 2D 지도 중심 화면

현재 브랜치는 `feature/manual-inspection-map-first-20261120`, 기준은 `7c6cd31ecaa5b201e6138c59fa757f2f87e292d8`다. master에는 병합하지 않는다.

- 작은 점 대신 **칸을 채우는 큰 Level 색상 사각형**으로 표시한다. 각 Cell/Head 구획의 가로·세로를 사용하며 선택·대표·NG·Masking 표시와 Light/Dark 테마를 유지한다.
- 지도가 작업공간 대부분을 사용한다. **검사·보정은 우측 접이식**, 측정 결과 표·산포도·필터/범례/작업 정보는 하단 접이식이다. 기본 화면에서 1120×560 창의 지도 약1069×386px, 스크롤0을 확인했다.
- 지도 **좌측 더블클릭 → 100%·맞춤**. 줌/보기 이동을 초기화하며 같은 기준의 기존 복수 선택·초점을 보존한다. Ctrl 휠/클릭·드래그 선택, Alt 보기 이동, 우클릭 이동/측정/보정은 유지한다.

미리보기(실제 WPF·합성 데이터): [Dark 전체 지도](images/map-first-dark.png) · [Light 작은 화면](images/map-first-light.png) · [검사·보정 펼침](images/map-first-inspector.png) · [하단 작업 정보 펼침](images/map-first-work-info.png).

UI **385/385**(기존312 유지+신규73), 실제 Assembly Controller **93/93**, 범위20개, 전체 Release **경고0/오류0**을 확인했다. 더블클릭은 실제 handler helper/두 클릭 상태 재현으로 검증했으며 물리 입력 주입과 구분한다. 타일 실제 색상 면적은 약97.1~97.9%였다. [변경·검증 범위](../../tests/ManualInspection/MAP_FIRST_QA.md)와 [검증 manifest](validation.json)에 근거를 기록했다.

생산 변경은 Map와 View 3파일이다. Controller·보정 엔진·저장·좌표계·다른 Correction 메뉴는 기준과 동일하다. 지도는 **논리 행렬**이며 실제 기판 mm 축척 도면과 구분한다. 실제 설비 시험·실제 모니터 DPI·활성 창 메뉴 방향키 확인은 별도다.


## 이전 화면·그래픽 개선 기록

당시 브랜치는 `feature/manual-inspection-visual-polish-20261120`이며 바로 이전 안정화 Commit `798359edaba1afb24977ea81185c0086d7fd57b3`를 기반으로 한다. master에는 병합하지 않는다.

지도·산포도·선택/대표 표시·테마 메뉴·키보드 입력과 큰 데이터 화면 갱신을 다듬었다. 기존 보정 로직과 저장·원복 안전 경계는 유지한다. [화면 변경·사용법·검증 범위](../../tests/ManualInspection/VISUAL_USABILITY_QA.md)를 참조한다.

실제 WPF 합성 데이터 미리보기: [Dark 작업화면](images/visual-dark.png) · [Light Head 산포도](images/visual-light-scatter.png) · [우클릭 메뉴](images/visual-context-dark.png) · [작은 화면](images/visual-compact-dark.png). 실제 장비 캡처가 아니다. 지도는 행·열 논리 배치이며 실제 기판 mm 축척과 구분한다.

화면 검사 **312/312**, 실제 Assembly Controller **93/93**, 전체 Release 경고·오류 **0/0**을 확인했다. 4,000점 지도에서 120점 결과 burst를 빠짐없이 기록하며 전체 지도 투영은 5회로 묶는 결정적 회귀를 통과했다. 이는 화면 처리 관찰이며 장비 Tact/정밀도 보증이 아니다.

아래 앞선 안정화 기록의 엔진113/writer35/Backend16/projection53은 해당 소스를 바꾸지 않은 이전 Commit의 근거다. 현재 변경의 재실행 수에 합산하지 않는다. 실제 모니터 DPI 전환·활성 창 메뉴 방향키 포커스 이동은 별도 확인 항목이며, 비활성 화면 테스트에서 확인하지 못한 두 방향키 사례는 통과 수에서 제외했다.


## 이전 Review 경로 교차검증 안정화 기록

당시 브랜치는 `feature/manual-inspection-review-hardening-20261120`이며 기준은 `4cbf259a16cd04bdab0bf6f76cd4591c681c109f`다. master에는 병합하지 않는다. [발견사항·수정·검증·현장 확인](../../tests/ManualInspection/HARDENING_QA.md)에 경계별 근거를 정리했다.

같은 경로의 보정 파일 교체, 저장 경합·시계 역전·이력 저장 실패, 다른 Review 메뉴의 원복 후 소비 증거, 회차 재사용·로딩 중 기준 변경, STOP·화면 callback 오류, 세션 재연결과 대표 복사 출처를 보완했다. 기존 측정 이력·Level·접기 정책과 다른 Correction 화면/좌표 계산·장비 시퀀스는 유지한다.

이전 버전에서 **같은 물리 회차에 적용했거나 중단한 작업**이 있으면 원본을 보존한 조회 전용으로 연다. 새 기판/Product 또는 새 가공 회차에서 다시 불러온다. 현재 Product와 선택 Recipe가 다르고 완료 기록의 비영점 Shift 출처를 확인할 수 없어도 이동·보정을 막는다. 임의로 이력 파일을 지워 우회하지 않는다.


사용자 첨부 [Review Workspace HTML](Reference/A3_Review_Workspace.html)의 작업공간을 **Correction → Review → MANUAL INSPECTION**에 별도 WPF 탭으로 구현했다. 기존 SIMPLE / INSPECTION, Default·Simple·Rough·Fine·Review Data 화면과 계산 방식은 유지한다. 기존 Light/Dark 테마의 패널·버튼·콤보·표를 사용한다.

- 기준: 바로 이전 보정 이력 후속 `4cbf259a16cd04bdab0bf6f76cd4591c681c109f`. 원래 구현은 11.19.0+누락 Log 추적 수정 `334b359`에 기반한다.
- 이전 안정화 브랜치: `feature/manual-inspection-review-hardening-20261120`. **master 병합·배포는 하지 않았다.**
- 빌드: Windows / .NET 8 Windows Desktop, `dotnet build Drilling.sln -c Release`.
- 시험은 독립 하네스에서 진행했다. 실제 설비용 App·Root·장비 Manager·벤더 SDK·모션·AeroScript는 실행하지 않았다.

## 화면에서 하는 일

실제 WPF 하네스 미리보기(합성 데이터): [Dark](images/dark.png) · [Light](images/light.png). 실제 장비 화면을 캡처한 자료는 아니다.

[작은 가용 화면 미리보기](images/compact.png)에서는 결과표·산포도가 접힌 상태와 지도 전체 범위를 확인할 수 있다.

| 작업 | 조작 |
| --- | --- |
| 최신 결과 확인 | `결과 새로고침`. 같은 Recipe·현재 기판의 외부 측정과 활성 Review Offset을 자동 확인 |
| 다른 결과 선택 | `더보기 → 최근 측정 결과 보기`. Rough/Fine/단동/Auto 등 목록에서 선택하거나 CSV 직접 선택 |
| 여러 위치 선택 | 클릭, Ctrl+클릭, 드래그, Ctrl+드래그 |
| 지도 확대 / 이동 | Ctrl+휠, Alt+드래그·방향키, 좌측 더블클릭 100%·맞춤. **보기 이동이며 장비는 움직이지 않는다.** |
| 실제 작업 | 선택점 우클릭 또는 오른쪽 버튼 → 이동만 / 이동 후 측정 / 이동 후 측정 & 보정 |
| 현재 위치 측정 | 도착·정지 확인된 한 점만 가능. 복수점 측정은 이동 후 측정 사용 |
| Flying | 선택 Cell/Head/행/Beam의 유효한 행 전체를 확인 후 측정 |
| 대표값 복사 | 직접 실측 OK/NG를 대표로 지정 → 대상 선택 → 사유·최종 F·대상별 증감량 확인 |
| 보정 저장 | 계산 → 후보 검증 → 보정 적용·저장. 파일 저장 성공 후 활성값 갱신 |
| 원복 | 직전 활성 보정값을 **새 Revision**으로 저장. 기존 파일·측정·이력 보존 |
| 결과 확인 | 측정 결과 표, 점별 원시 값·전체 이력, 현재 Cell / 전체 Cell의 Head별 / 선택 Cell 산포도 |
| 작업 보관 | `더보기`의 CSV·세션 JSON, 같은 Run의 자동 세션 복구. 복구만으로 동작 시작하지 않음 |

기본 화면은 지도에 집중한다. 상단은 **Cell/색상 / 100%·맞춤 / 선택 / 파일 / 새로고침 / STOP**이며, **측정 결과 표와 산포도는 기본 접힘**이다. 지도는 남는 높이에 맞춰 표시하고 기본 화면의 세로 스크롤을 없앴다. 결과표·산포도·좌표 상세·대표 복사·보정·설정은 필요한 때 펼친다. 좁은 화면에서 상세를 펼쳤을 때에는 내부 스크롤로 내용을 확인할 수 있다.

오른쪽의 **선택점 측정 / 다른 동작 / 측정·출처 상세** 버튼과 설정의 현재 위치 측정 버튼은 제거했다. 점을 우클릭하면 이동만 / 이동 후 측정 / 이동 후 측정 & 보정 / 현재 위치 한 점 측정 / 측정·출처 상세를 실행한다. 처음 3동작은 선택 집합, 마지막 2동작은 우클릭한 한 점을 사용한다. 미완료가 있을 때의 잔여 재측정은 복구를 위해 유지했다. Ctrl 복수 선택·드래그·확대, Alt 보기 이동은 그대로다.

이동·측정·계산·검증·보정 저장·원복·대표 복사·자료 전환 시 대상과 동작 설명을 읽고 확인한다. 취소하면 명령을 보내지 않는다. **STOP은 확인창 없이 즉시 요청한다.**

## 다른 메뉴 결과와 전체 셀 색상

- 결과·Offset 저장 폴더의 CSV 저장/이름 변경/삭제·폴더 반입을 감지해 갱신한다. 화면 진입 때도 다시 확인한다. 이 갱신은 장비를 움직이거나 보정 파일을 쓰지 않는다.
- 현재 Recipe·확실한 Glass·가공 회차의 시작 시각·Shift·Simulation 모드·Head/설계 좌표를 확인한 **점별 최신 결과**를 합친다. 구형/기준 미확인 파일은 최근 목록에서 선택해 조회한다. RETAINED·미검사를 새 직접 측정으로 합치지 않는다.
- 다른 기판, 과거 가공, Simulation 혼합, 좌표 불일치 결과를 현재 측정으로 자동 합류하지 않는다. 외부 파일은 IMPORTED 조회값이며 직접 실측 대표로 자동 승인하지 않는다.
- 측정/저장 중에는 갱신을 대기시킨다. 미완료 측정·미적용 후보가 있으면 보존하고 안내한다. 확인 후 새로고침하거나 잔여 측정을 완료한다. 외부 보정 Revision 변경 시 옛 후보를 바로 쓰지 않는다.

기본 지도는 **적용 보정값 B의 Level**이다. Rough/Fine의 보간·확장 결과나 대표값 복사가 활성 CSV에 저장되면, 실제 행이 있는 **모든 Cell/Shot/표시 Beam**을 같은 DESIGN µm 임계값으로 표시한다. 아직 측정하지 않은 좌표도 보정 행이 있으면 Level이 나온다. 저장하지 않은 후보 F는 적용 색상에 포함하지 않는다.

| 보기 | 색상의 뜻 |
| --- | --- |
| 적용 보정값 · Level (기본) | 실제 활성 CSV의 B. 행 없음은 회색, 명시적 0 보정 행은 L1 |
| 측정 편차 · Level | 작업 측정 편차의 등급. 수신 오류·미측정은 0 측정으로 만들지 않음 |
| 측정 판정 | OK/NG/ERROR 등 측정 상태 |

기본 공통 기준은 `MAX_AXIS = max(|X|, |Y|)`, 상한 5 / 10 / 20 µm다. 모든 Cell/Head에 같은 기준을 적용한다. 예를 들어 B=(6,−8) µm는 어느 셀에서도 L2다. RADIAL로 바꾸면 벡터 크기에 같은 경계를 사용하며, 설정 변경은 원측정·활성값을 바꾸지 않는다. 적용 보정 크기는 가공 후 잔류 정밀도와 구분한다.

### 기존 보정점의 측정 상태

Manual Inspection에서 직접 측정하지 않았어도 기존 활성 보정 행이 있으면 **측정 이력이 있는 점**으로 분류한다. 표·선택 상세·미측정 필터·측정됨 통계·지도·작업 산포도에 같은 기준을 적용한다.

- 유효한 측정/대표 복사 편차가 있으면 그 값을 우선한다.
- 원편차가 없고 활성 보정값이 있으면 **측정됨·보정기반**으로 표시하고 B를 보정기반 편차로 사용해 동일한 측정 Level을 붙인다. 명시적으로 저장된 0 보정도 측정 이력·L1이다.
- 최신 수신 오류가 있더라도 기존 보정 이력은 유지하고 최근 오류를 함께 표시한다. 보정 행도 측정값도 없는 점만 미측정이다. 미적용 후보만 있는 점은 이력으로 승격하지 않는다.
- 보정기반 점은 □, 대표 복사는 ◇로 구분한다. 작업 산포도에는 보정기반 점을 포함하고 개수를 별도로 표시한다. **원측정 전용 산포도는 실제 원측정만 사용**한다.

표시상 측정 이력과 직접 raw 자격을 구분한다. 원시 값·판정·소비 토큰은 그대로 보존하며 보정기반 표시값을 새 raw처럼 다시 더하지 않는다. CSV에는 기존 raw 열을 유지하고 측정 이력/근거/표시 편차/측정 Level의 5열을 추가했다.

대표값을 복사하면 대상의 **작업 편차·판정과 최종 보정 F**가 대표를 따른다. 대상의 실제 원측정은 덮어쓰지 않고 MANUAL / 대표 키 / 사유를 남긴다. NG 대표는 NG 상태도 복사하지만 보정 파일 적용은 차단한다. 복사본은 다시 직접 실측 대표로 사용할 수 없다.

산포도의 `원측정`과 `작업 결과`를 구분한다. 측정 없는 점은 0으로 그리지 않는다. 헤더 좌표는 **마지막 도착 확인값**이며 실시간 모터 위치 표시가 아니다.

## 실제 코드 연결

```mermaid
flowchart LR
    UI[새 WPF 작업공간] --> C[Manual Inspection Controller]
    C --> E[순수 계산·세션·후보 엔진]
    C --> B[기존 기능 연결 Adapter]
    B --> R[기존 Review Manager]
    R --> S[기존 Stage Process Move / IOF]
    R --> V[기존 Vision 수신·결과 CSV 저장]
    B --> W[새 비교 후 저장 API]
    W --> F[기존 Review Offset CSV·적용 이력]
    F --> P[기존 가공 좌표 소비 경로]
    V --> O[최근 결과·현재 기판 확인]
    O --> C
    F --> M[파일 변경 감시]
    M --> C
    C --> L[공통 Level·전체 셀 표시]
```

- UI: `Drilling.UI/Views/CManualInspection*`. 지도/산포는 그리기 컨트롤이며 점마다 WPF 컨트롤을 생성하지 않는다.
- 계산: `Drilling.Common/Review/ManualInspection`. 단위는 DESIGN µm, 원시 Vision/Stage 값은 별도 보존한다.
- 연결: `CManualInspectionController`, `CManualInspectionBackend`. 기존 Review Manager의 실행 잠금, Process Move·도착 확인·Vision 수신·공식 결과 저장을 사용한다.
- 좌표: `CManualInspectionCoordinateBridge`. Vision Flip 1회 → 기존 Head/Design 변환 → µm. 결과 CSV의 VISION_FLIP_CONVERT에는 Flip을 재적용하지 않는다. 활성 파일은 DESIGN **mm**로 저장한다.
- 저장: `CReviewOffsetFile.SaveNewIfUnchanged`. 실제 최신 파일을 잠금 안에서 비교하며 오래된 Revision·동일 출처 중복을 거부한다. 기존 Save/Load/Rollback 본문은 유지한다.

같은 Cell/Shot의 여러 Beam은 기존 CSV의 **하나의 보정 저장 키**를 공유한다. 서로 다른 최종 F를 요청하면 저장을 거부하며, 적용 후 형제 Beam의 현재 B도 같은 값으로 갱신한다. 직접 측정은 B+편차, 대표 복사는 대표의 확정 F를 사용한다. 이미 적용한 대표 raw를 다시 더하지 않는다.

## 실패·중지 처리

- MANUAL / PM 잠금 해제 / MAIN Ready / HEAVY 알람 없음 / 다른 Review·Stage 작업 없음에서 실행한다. Stage 정리 미완료도 차단한다.
- STOP은 즉시 취소를 요청하지만 실제 이동·측정 정리 반환 전에는 작업 잠금을 풀지 않는다. 정보창을 닫는 것 때문에 장비 알람 정리가 지연되지는 않는다.
- ERROR·수신 없음은 잔여로 남긴다. NG는 측정 완료이나 자동 보정 불가다. `잔여 재실행`은 측정만 하며 자동 보정을 재개하지 않는다.
- P2P는 공식 결과 저장 성공 후 완료를 반영한다. Flying도 행별 결과 저장 성공 후 반영하며, 각 단일행 요청의 번호를 1로 맞춘다.
- 첫 세션 저장이 실패하면 장비를 움직이지 않고 오류/잔여 상태로 종료한다. 경로를 복구한 후 잔여 재실행이 가능하다.
- 저장 직전 Recipe·Run·좌표 설정·활성 Revision을 다시 확인한다. 같은 Run/점/raw의 소비 이력을 세션과 적용 이력으로 보존한다.
- 구형/외부 결과는 IMPORTED로 구분한다. 현재 Run의 직접 실측으로 오인해 보정 앵커로 쓰지 않는다. 다른 기판 자료는 조회 전용이다.

## 저장 위치와 정밀도

| 자료 | 위치·단위 |
| --- | --- |
| 공식 측정 결과 | 기존 `Data/Review/...` 경로·기존 저장기 |
| 활성 보정 | 기존 `Config/RECIPE/<Recipe>_reviewoffset/...` 저장기 경로·기존 형식, DESIGN mm |
| 세션·소비 원본 예약 | `Data/ManualInspection/Sessions`, `Data/ManualInspection/ApplySources` |
| 내부 검증·기존 SIM 세션 | `Data/ManualInspection/Simulation` 아래 격리. SIM 예제/주입 UI는 제거 |

파일 저장 정밀도는 0.000001 mm(0.001 µm)이며 기존 가공 소비 코드는 0.001 mm(1 µm)로 반올림한다. 새 탭은 이 기존 가공 동작을 변경하지 않는다. Field 검사는 실제 소비할 반올림값·REVIEW_OFFSET_SCALE과 현재 Default/First/Align/Shift 기여를 사용한다. MOF Stage 추종 성분을 정지 Field 오차로 오인하지 않는다. 이 검사는 장비 실제 소프트리밋·동역학 검증을 대신하지 않는다.

## 이전 안정화 검증 결과와 현장 항목

| 검증 | 결과 |
| --- | --- |
| 전체 솔루션 Release | 경고 0 / 오류 0 |
| 계산·상태·세션 | 113/113 · 192 복합 상태 시나리오 포함 |
| 좌표·실제 CSV writer | 35/35 |
| 실제 UI 프로젝트 참조 Controller | 89/89, 벤더 Assembly 로드 0개 |
| 실제 Backend 로딩 연결 | 16/16 · 실제 순수 planner·CSV/보정 저장기, 장비 Manager 없음 |
| 외부 결과 병합 | 53/53, 실제 Common·합성 파일/기하 |
| 실제 WPF UI / 추가 연결 검사 | 49/49 + 23/23 + 34/34 + 71/71 + 새 교차 동작 30/30 |
| 첨부 HTML oracle | 이전 구현의 14/14 기록. 후속 단순화는 새 WPF 검사로 확인 |
| 기존 화면·변경 범위 보존 | 15/15 · 구화면·Theme/Config/Data·장비 실행 본문/순수 계산 보존 |

재현 명령과 범위는 [테스트 안내](../../tests/ManualInspection/README.md), [UI 대조표](../../tests/ManualInspection/HTML_PARITY_CHECKLIST.md), [현재 검토](../../tests/ManualInspection/COVERAGE_COMPACT_QA.md)를 참조한다. 결과별 범위와 소스 SHA는 [validation.json](validation.json)에 기록했다. HTML/WPF/순수 계산/파일 시험을 실장비 시험으로 합산하지 않는다.

추가 확인 사항:

1. **실장비**: Process Move 도착·Busy·STOP 신호 정리, P2P/Flying 트리거·결과 수·UID, 외부 Product 변경·디스크 오류, 기존 Correction의 장비 회귀.
2. **실제 한계 승인**: 화면 초기 증감 30 µm / 총량 200 µm 등은 현장 허용값으로 확인 후 조정한다. Field는 실제 Head 설정과 화면 한계 중 작은 범위를 적용한다.
3. **기존 설정 관찰**: 소스의 test0929 설정으로 순수 planner 20,740점을 대조했을 때, Masking/Chess 제외 후 19,525점 중 **90점이 기존 값 그대로도 ±55 mm Field 밖**이었다. 최대 55.9815 mm. 새 기능 오류나 실제 장비 불합격으로 단정하지 않으며 실제 Recipe·설정을 확인해야 한다. 이 경우 검증을 임의로 통과시키지 않는다. 설정 원문·운영 측정 레코드는 이 브랜치에 복제하지 않았다.
4. **새 가공으로 검증**: 보정 저장만으로 이미 가공된 기판의 구멍이 움직이지 않는다. 새 가공 Run을 만든 뒤 재측정해 정밀도를 확인한다. SIM도 같은 Run의 구멍은 그대로 유지한다.
