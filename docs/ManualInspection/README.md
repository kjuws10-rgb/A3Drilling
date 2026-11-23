# MANUAL INSPECTION · 11.24.0

브랜치: `feature/manual-inspection-retry-visibility-20261124.0`

기준 Commit: `3a2526dacf988cf303a102dc432a6eaa9394e9d4`

**master에는 병합하지 않는다.** 기존 테마와 지도 중심 구성을 유지한다.

## 11.24.0 · 비례 라벨·재측정·진행 표시·Monitor

셀 번호는 셀 크기와 확대율에 비례해 조절한다. 실패 후 남은 위치를 다시 측정하거나 새 선택으로 진행할 수 있고, 현재 P2P 대상/Flying 행은 0.5초마다 점멸한다. Monitor Shot Detail은 최신 저장 Review Offset을 좌표계·스케일에 맞춰 표시하고 값 없음과 실제 0을 구분한다. NG·미측정 보정 제한은 유지한다.

[변경 설명·화면](RETRY_VISIBILITY_11.24.0.md) · [3회 검증](../../tests/ManualInspection/RETRY_VISIBILITY_QA.md) · [manifest](validation.json)

## 유지한 표준 선택과 대표점

| 단축키 | 동작 |
| --- | --- |
| 방향키 | 다음 측정점 한 개 선택 |
| Ctrl+방향키 | 현재 화면 행·열의 끝으로 이동해 한 점 선택 |
| Shift+방향키 | 기준점부터 사각 범위 선택·축소 |
| Ctrl+Shift+방향키 | 기준점부터 행·열 끝까지 범위 선택 |
| Space / Esc | 컨텍스트 메뉴 열기 / 닫기 |
| Alt+방향키 | 화면 보기 이동 |

전체 Cell 번호는 마우스를 올리지 않아도 표시한다. 11.24.0부터 셀 크기에 비례해 확대·축소하고 셀 내부에 배치한다. 실제 셀 배치는 바꾸지 않는다.

어떤 점이든 대표로 지정할 수 있다. 검증 전·NG·복사된 점·기존 보정·미측정·초기화·Masking도 지정이 가능하다. 값 없는 점은 값을 만들지 않고 복사할 수 없다고 표시한다. 실제 값의 복사와 보정 저장은 출처·판정·단위·한계 검사를 각각 유지한다.

[현재 변경 설명](STANDARD_SELECTION_REFERENCE_11.23.2.md) · [3회 교차검증](../../tests/ManualInspection/STANDARD_SELECTION_REFERENCE_QA.md)

## 11.23.1에서 유지한 조작

- **전체 Cell:** 셀 모양 안 어디든 클릭하면 해당 셀로 들어간다. Ctrl+클릭도 같은 동작이다. 측정점 다중 선택은 드래그로 하고, Ctrl+드래그는 기존 선택에 더한다.
- **키보드:** 방향키로 화면의 측정점 선택을 옮긴다. Space는 컨텍스트 메뉴, Esc는 메뉴 닫기다. Alt+방향키는 보기 이동을 유지하며 실제 장비 이동은 메뉴에서 확인한다.
- **선택점 보정:** 선택한 점의 계산 → 검증 → 모델 저장을 하나로 진행한다. 세 버튼을 차례로 누르지 않는다. **일괄 보정**은 모델 전체, **선택점 보정**은 선택한 대상이다.
- 초기화/통합 Offset 편집 중에는 상황에 맞는 저장 버튼만 표시하고 저장 전에 자동 검증한다. 적용 전 확인창에서 취소하면 저장하지 않는다.
- 저장한 Review Offset은 같은 모델의 Correction 현재값·Recipe 최종 좌표 미리보기·Monitor 좌표 보기에 반영한다. Recipe 편집 원본이나 다른 모델의 보정값은 바꾸지 않는다.

[현재 변경·검증 설명](CELL_CLICK_OFFSET_SYNC_11.23.1.md) · [현재 QA](../../tests/ManualInspection/CELL_CLICK_OFFSET_SYNC_QA.md)

## 측정하고 따로 보정하기

1. 지도에서 점을 선택하고 우클릭 **이동 후 측정**을 실행한다. 결과는 오른쪽에 표시하며, 측정만으로 보정값을 저장하지 않는다.
2. 오른쪽 보정 영역의 **일괄 보정**을 누른다. **현재 모델 전체**에서 적용 가능한 새 OK 결과를 계산·검증해 한 번에 저장한다. 지도 선택·필터는 이 범위를 줄이지 않는다.
3. 필요하면 **되돌리기 / 다시 적용**을 누른다. 보정값은 최대 10단계 이전까지 되돌리고, 남아 있는 마지막 적용 상태까지 다시 진행할 수 있다.

**이동 후 측정 및 보정** 메뉴는 제거했다. 이전 동작을 직접 요청해도 측정·자동 보정을 시작하지 않는다. 현재 위치가 확인된 한 점을 이동 없이 측정하는 **현 위치 측정** 버튼은 유지한다.

일괄 보정은 새 실측, 검증된 파일 측정, 사유가 있는 대표값 복사를 대상으로 한다. NG·Masking·미측정·초기화·이미 적용한 동일 결과는 제외한다. 같은 Cell/Shot의 Beam 결과가 서로 충돌하거나 보정 한계·Scanner Field 검증을 통과하지 못하면 전체 저장을 막는다. 제외 수와 오류는 화면에서 확인한다.

[일괄 보정·Undo/Redo 상세](BULK_HISTORY_11.23.0.md) · [현재 검증 manifest](validation.json) · [검증 기록](../../tests/ManualInspection/BULK_HISTORY_QA.md)

## Offset Init은 ‘값 없음’

- 모든 Review 메뉴에서 Offset Init으로 지운 측정 보정은 **미측정·값 없음**으로 구분한다. 정확히 측정된 오차 0처럼 Level을 부여하거나 산포에 넣지 않는다.
- Manual Inspection의 Init은 전체 / Cell / Head 범위를 지원한다. 화면에서 먼저 초기화하고 **모델 Offset 저장**으로 반영한다. 저장 전에는 **Offset 편집 취소**로 돌아갈 수 있다.
- **실제로 측정한 0**은 유효한 측정값이다. 초기화와 구분해 정상적인 Level과 판정을 표시한다.
- 하드웨어·Default·First Beam·Distortion은 측정 Offset Init 대상이 아니다. 기존 측정 파일과 원측정 이력도 남긴다.

처음 모델을 열었을 때 적용된 Review 보정이 없으면 빈 보정 기준으로 시작한다. 좌표 계산에서는 보정을 더하지 않는 중립값 0을 사용하지만, 이를 측정 결과 0으로 표시하지 않는다. 이미 새 측정 결과가 있다면 그 OK/NG·편차는 그대로 표시한다. 첫 보정을 되돌렸을 때도 원래 빈 보정 기준을 복원한다.

## 유지한 조작

| 작업 | 조작 |
| --- | --- |
| 전체 Cell / 단일 Cell | 제목 옆 전체 Cell → 실제 Recipe 배치, 셀 내부 어디든 클릭 → 해당 Cell |
| 복수 선택 | 전체 Cell은 드래그·Ctrl+드래그, 단일 Cell은 Ctrl+클릭·드래그 |
| 지도 확대·보기 이동 | Ctrl+휠 / Alt+드래그·방향키 / 더블클릭 맞춤 |
| 장비 동작 | 점 우클릭 → 이동만 / 이동 후 측정 / 현 위치 측정 |
| 대표값 복사 | 대표 지정 → 대상 선택 → 같은 값·판정 복사. NG는 빨강이며 일괄 보정에서 제외 |
| 산포 | 측정 편차 기본, Cell·Head·선택 Cell 범위, Auto·고정 축·확대축소·더블클릭 맞춤 |
| 파일 작업 | 통합 Offset JSON 가져오기·내보내기 / 파일 불러오기 전 작업으로 돌아가기 |
| STOP | 확인창 없이 즉시 요청, 실제 정리 완료까지 다음 동작 차단 |

지도는 측정 편차 Level / OK·NG 두 보기다. NG는 편차와 관계없이 빨강, 값이 없는 점은 회색이다. 누적 보정량 B는 현재 가공의 잔여 오차와 다르므로 보조 정보로 구분한다. 조회 전용 모드는 두지 않으며 현재 모델·좌표를 확인한 뒤 새 측정할 수 있다. 저장 전 Init/통합 파일 초안은 저장하거나 취소한 뒤 다음 측정을 진행한다.

## 미리보기와 검증

[축소한 48셀 · Light](images/manual-11.24.0-48cells-light.png) · [측정 점멸 ON · Dark](images/manual-11.24.0-blink-on-dark.png) · [측정 점멸 OFF · Dark](images/manual-11.24.0-blink-off-dark.png)

그림은 합성값으로 실제 WPF를 렌더한 미리보기이며 실장비 캡처가 아니다. 최종 시험 수치·소스/Assembly/그림 SHA는 [validation.json](validation.json)에 기록한다. 실제 장비 App·벤더 SDK·PLC·Vision·모션·AeroScript·레이저와 물리 보정 정밀도는 실행 검증하지 않았다.

이전 [11.21.2 설명](MEASUREMENT_WORKFLOW_11.21.2.md)과 [그 시점 manifest](history/validation-61578db.json)는 당시 기록으로 보존한다. 현재 측정/일괄 보정·이력·값 없음 정책은 위 11.24.0를 따른다. [전체 시험 안내](../../tests/ManualInspection/README.md) · [첨부 HTML 원본](Reference/A3_Review_Workspace.html)

이전 11.23.0의 [동작 설명](BULK_HISTORY_11.23.0.md)과 [검증 manifest](history/validation-367a727.json)는 당시 기록으로 보존한다. 새 조작·통합 저장·표시 갱신은 현재 설명을 따른다.

이전11.23.1의 [변경 설명](CELL_CLICK_OFFSET_SYNC_11.23.1.md)과 [검증 manifest](history/validation-89e92a0.json)는 당시 기록이다. 대표 지정과 단축키는 현재11.24.0를 따른다.
