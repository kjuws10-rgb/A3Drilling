# MANUAL INSPECTION · 11.24.9

브랜치: `feature/manual-inspection-calibration-basis-20261124.9` · 기준: 11.24.8 `dc3a15e6ebc382c6f14d76a916c6b7c510e25590`. **master 미병합**.

현재 동작은 [11.24.9 간단한 조작·보정 기준](CALIBRATION_WORKFLOW_11.24.9.md)과 [검증 manifest](validation.json)를 따른다. 아래 과거 설명은 당시 기록이며 현재 정책과 다를 수 있다.

## 현재 사용법

1. 지도에서 점을 선택하고 우클릭 **이동 후 측정**한다.
2. 필요할 때 **대표값 복사**나 기존 키패드의 **보정값 직접 입력**을 사용한다.
3. **Cell 보정 저장**에서 확인하면 해당 Cell의 최종 Review CSV를 저장한다.

실측 잔차는 **실제 가공에 쓴 Review + 잔차/Scale**로 다음 최종값을 계산한다. 대표·파일·직접 입력한 절대 Review 값은 대상의 기존값에 더하지 않고 대체한다. 계산·출처·중복·범위 검사는 내부에서 처리한다. 실제 가공 기준이 없으면 이동·측정은 가능하고 실측 자동 보정에 필요한 사유만 표시한다.

지도는 원측정 편차 Level로 표시한다. NG는 빨강, 값 없음/Init은 회색, 실제 측정한 0은 유효값이다. 선택점의 **저장 Review**와 **미저장 작업값**은 짧게 구분한다. 최종값만 있는 CSV를 새 실측이라고 표시하지 않는다.

가져오기·Offset Init도 현재 Cell에서 준비하고 같은 저장 버튼으로 반영한다. 파일 메뉴는 **통합 Offset 가져오기(JSON/CSV)**와 **현재 저장 Offset CSV 내보내기** 두 개다. 내보내기는 모델 전체의 마지막 저장본이며 미저장 작업값은 제외한다.

기존 테마, 표 접기, 산포의 Cell/Head 범위, 다중 선택, Ctrl+휠 확대, Alt 보기 이동, 더블클릭 맞춤, 방향키/Ctrl/Shift 선택, Space 우클릭 메뉴, Esc 닫기는 유지한다. 이동은 확인 후 실행하며 STOP은 즉시 요청한다. 되돌리기/다시 적용은 최대 10단계다.

## 이전 기록

| 버전 | 당시 설명 |
| --- | --- |
| 11.24.8 | [이전 편차 대체 정책](REPLACEMENT_WORKFLOW_11.24.8.md) — 실측 잔차 수식은 .9로 대체 |
| 11.24.7 | [현재 Cell·CSV 파일 전송](CELL_CSV_WORKFLOW_11.24.7.md) |
| 11.24.6 | [CSV 재확인·Monitor 일치](BULK_FINAL_CONSISTENCY_11.24.6.md) |
| 11.24.5 | [대표값 적용·수동 OK·키패드](REPRESENTATIVE_APPLY_11.24.5.md) |
| 11.24.4 | [기존 최종 CSV·Init](EXISTING_FINAL_CSV_11.24.4.md) |
| 11.24.3 | [최종 Review Offset](FINAL_REVIEW_OFFSET_11.24.3.md) |
| 11.23.2 | [표준 선택·대표 지정](STANDARD_SELECTION_REFERENCE_11.23.2.md) |
| 11.23.1 | [Cell 클릭·Offset 동기화](CELL_CLICK_OFFSET_SYNC_11.23.1.md) |
| 11.23.0 | [일괄 보정·Undo/Redo](BULK_HISTORY_11.23.0.md) |

[전체 시험 안내](../../tests/ManualInspection/README.md) · [첨부 HTML 원본](Reference/A3_Review_Workspace.html)

실제 장비 App·벤더 SDK·PLC/Vision·모션·AeroScript·레이저와 물리 정밀도는 실행하지 않는다. 합성 시험과 실제 설비 검증을 구분한다.
