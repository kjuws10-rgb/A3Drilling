# MANUAL INSPECTION · 11.24.8

브랜치: `feature/manual-inspection-replacement-workflow-20261124.8` · 기준: 11.24.7 `35ef09b13dddb2d581582689e34978b9ac285d39`. **master 미병합**.

현재 동작은 [11.24.8 사용·저장 정책](REPLACEMENT_WORKFLOW_11.24.8.md)과 [검증 manifest](validation.json)를 따른다. 아래 과거 버전 설명은 당시의 기록이며 현재 정책과 다를 수 있다.

## 현재 사용법

1. Cell 하나를 열고 우클릭 **이동 후 측정** 또는 기존 키패드로 편차 X/Y를 준비한다. 측정만으로 CSV를 저장하지 않는다.
2. 대표점의 편차·판정을 복사하면 선택 대상의 작업값이 대체된다. 확인창을 거치며 CSV는 변경하지 않는다.
3. **현재 Cell 일괄 보정·CSV 저장**에서 확인하면 새 최종 Review CSV를 생성한다. **새 Review 값은 이번 편차 자체**이며 기존 Review 값에 더하지 않는다. 다른 Cell·보호 Offset은 유지한다.

지도는 측정편차 Level로만 표시한다. NG는 빨강, 값 없음/Init은 회색, 실제 측정한 0은 유효값이다. 최종값만 있는 과거 CSV를 새 측정편차라고 표시하지 않는다. 파일 입력은 별도 출처로 표시한다.

가져오기·Offset Init도 현재 Cell에서 준비하고 같은 일괄 저장 버튼으로 반영한다. 취소하면 활성 파일을 바꾸지 않는다. 파일 메뉴는 **통합 Offset 가져오기(JSON/CSV)**와 **현재 저장 Offset CSV 내보내기** 두 개다. 내보내기는 모델 전체의 마지막 저장본이며 미저장 작업값은 제외한다.

기존 테마, 표 접기, 산포의 Cell/Head 범위, 다중 선택, Ctrl+휠 확대, Alt 보기 이동, 더블클릭 맞춤, 방향키/Ctrl/Shift 선택, Space 우클릭 메뉴, Esc 닫기는 유지한다. 이동은 확인 후 실행하며 STOP은 즉시 요청한다. 되돌리기/다시 적용은 최대 10단계다.

## 이전 기록

| 버전 | 당시 설명 |
| --- | --- |
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
