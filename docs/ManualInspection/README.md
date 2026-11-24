# MANUAL INSPECTION · 11.24.10

브랜치: `feature/manual-inspection-overwrite-only-20261124.10` · 기준: 11.24.8 `dc3a15e6ebc382c6f14d76a916c6b7c510e25590`. **master 미병합**.

현재 동작은 [11.24.8 기준 최소 보정 수정](INCREMENTAL_OVERWRITE_FIX_11.24.10.md)과 [검증 manifest](validation.json)를 따른다. 화면 구성은 11.24.8을 유지하며 11.24.9의 추가 설계는 포함하지 않는다. 아래 과거 버전 설명은 당시 기록이다.

## 현재 사용법

1. Cell 하나를 열고 우클릭 **이동 후 측정** 또는 기존 키패드로 편차 X/Y를 준비한다. 측정만으로 CSV를 저장하지 않는다.
2. 필요하면 대표점의 편차·판정을 복사한다. 확인창을 거치며 아직 CSV는 변경하지 않는다.
3. **현재 Cell 일괄 보정·CSV 저장**에서 확인하면 **각 대상의 기존 Review + 이번 측정·복사 편차**를 저장한다. 기존값 20 µm에 대표 편차 3 µm이면 23 µm다. 같은 원본을 중복 가산하지 않는다. 다른 Cell·보호 Offset은 유지한다.

지도는 측정편차 Level로만 표시한다. NG는 빨강, 값 없음/Init은 회색, 실제 측정한 0은 유효값이다. 최종값만 있는 과거 CSV를 새 측정편차라고 표시하지 않는다. 파일 입력은 별도 출처로 표시한다.

**Offset Init은 현재 Cell의 마지막 저장 Review까지 즉시 비워 저장**한다. 다음 보정은 0 기준으로 시작하며 지도는 값 없음 회색이다. 파일 가져오기는 현재 Cell에 준비한 절대값을 일괄 저장으로 확정한다. 파일 메뉴는 **통합 Offset 가져오기(JSON/CSV)**와 **현재 저장 Offset CSV 내보내기** 두 개다. 내보내기는 모델 전체의 마지막 저장본이며 미저장 작업값은 제외한다.

기존 테마, 표 접기, 산포의 Cell/Head 범위, 다중 선택, Ctrl+휠 확대, Alt 보기 이동, 더블클릭 맞춤, 방향키/Ctrl/Shift 선택, Space 우클릭 메뉴, Esc 닫기는 유지한다. 이동은 확인 후 실행하며 STOP은 즉시 요청한다. 되돌리기/다시 적용은 최대 10단계다.

## 이전 기록

| 버전 | 당시 설명 |
| --- | --- |
| 11.24.8 | [당시 편차 대체·저장 정책](REPLACEMENT_WORKFLOW_11.24.8.md) — 계산과 Init은 .10 정책으로 대체 |
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
