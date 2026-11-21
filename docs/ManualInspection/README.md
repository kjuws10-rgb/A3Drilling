# MANUAL INSPECTION · 11.21.2

브랜치: `feature/manual-inspection-measurement-20261121.2`
기준 Commit: `d9c2779f888f1cc925c1515724a00038fe633bca`
**master에는 병합하지 않는다.** 변경 범위는 Manual Inspection과 그 측정 보정값의 저장·복원 경로다.

## 이번에 달라진 사용법

- 지도 보기는 **측정 편차 Level / OK·NG** 두 가지다. NG는 편차 크기와 관계없이 빨강, 초기화한 점과 미측정점은 회색이다. 실제 측정한 0은 초기화 0과 구분한다.
- **이동 후 측정**을 끝내면 오른쪽에 최신 결과·X/Y 편차·판정이 바로 보인다. **현 위치 측정** 버튼은 도착이 확인된 한 점만 측정하며 장비를 이동하지 않는다.
- 표와 산포도는 작게 펼쳐 보고, 필터·범례·작업 정보는 오른쪽에서 확인한다. 지도에는 선택 테두리와 짧은 상태를 표시하고 출처는 마우스를 올려 확인한다.
- **Offset Init**은 모델 전체 / Cell / Head를 고른다. 측정 누적 Review 보정만 화면에서 먼저 초기화하고 **모델 Offset 저장**으로 반영한다. 하드웨어·Default·First Beam·Distortion은 보존한다.
- 확인창은 **실제 이동이 포함된 동작과 잔여 재측정**에만 나온다. 현 위치 측정·복사·초기화·계산·검증·저장·원복·필터는 바로 실행한다. 파일 선택창과 실제 인터록 검사는 유지한다.

[11.21.2 작업 흐름·저장 형식](MEASUREMENT_WORKFLOW_11.21.2.md) · [현재 검증 manifest](validation.json) · [이번 검증 기록](../../tests/ManualInspection/MEASUREMENT_WORKFLOW_QA.md)

## 지도와 산포 조작

| 작업 | 조작 |
| --- | --- |
| 전체 Cell / 단일 Cell | 제목 옆 전체 Cell 버튼 → 실제 Recipe 배치, Cell # 클릭 → 해당 Cell |
| 여러 점 / Cell 선택 | Ctrl+클릭·드래그, Ctrl+Cell # → 해당 Cell 유효점 추가/해제 |
| 지도 확대·보기 이동 | Ctrl+휠 / Alt+드래그·방향키 / 더블클릭 100%·맞춤 |
| 실제 이동·측정·보정 | 점 우클릭 → 이동만 / 이동 후 측정 / 이동 후 측정 & 보정 |
| 현재 위치 측정 | 오른쪽 현 위치 측정 버튼 또는 점 우클릭 |
| 대표값 복사 | 대표 지정 → 대상 선택 → 같은 오프셋·상태 적용. NG도 복사하지만 NG 보정 저장은 차단 |
| 산포 범위 | 현재 Cell / 전체 Cell의 Head별 / 선택 Cell들 |
| 산포 데이터 | 기본 **측정 편차** / 보조 원측정 기록·누적 보정량 |
| 산포 축 | Auto / 고정 ±µm / +−·휠 확대축소 / 더블클릭 Auto 맞춤 |
| 파일 작업 복귀 | 파일 → 불러오기 전 작업으로 돌아가기 |
| STOP | 확인창 없이 즉시 요청. 정리 완료까지 다음 동작 잠금 유지 |

기존 보정 **B는 누적 보정량**이다. 현재 가공 후 잔류 오차와 같지 않다. 원편차가 없는 기존 보정 이력은 보정 기반 값으로 표시하며, 이를 새 카메라 원측정으로 만들지 않는다. Cell 외곽은 Recipe의 실제 mm 배치이고 내부 타일은 논리 측정 행렬이다.

조회 전용 모드는 두지 않는다. 현재 모델·좌표 기준을 연결해 다시 측정할 수 있다. 다만 저장 전 Init/통합 파일 교체 초안은 실제 활성값과 다르므로 저장 또는 편집 취소 후 다음 이동·측정을 진행한다. Busy·알람·PM·도달 확인·STOP·좌표·저장 검사는 유지한다.

## 실제 WPF 합성 미리보기

[측정 결과 · Dark](images/measurement-11.21.2-dark.png) · [Light](images/measurement-11.21.2-light.png) · [Init·실측 0 구분 · Dark](images/measurement-11.21.2-init-dark.png) · [Light](images/measurement-11.21.2-init-light.png)

합성값을 실제 WPF로 렌더한 미리보기이며 설비 캡처가 아니다.

## 검증과 이전 기록

최종 시험 수치·소스/Assembly/그림 SHA는 [validation.json](validation.json)에 기록한다. 순수 엔진, 실제 Assembly Controller+fake Backend, CSV/Recipe/Backend 연결, WPF 합성 검증을 구분한다. **실제 장비 App·벤더 SDK·PLC·Vision·모션·AeroScript·레이저는 실행하지 않았으며 실제 보정 정밀도도 검증하지 않았다.**

이전 문서는 해당 버전의 기록으로 보존하며, 현재 기본 산포·범위 Init·확인창 정책은 위 11.21.2를 따른다.

- [11.21.1 통합 Offset 작업 기록](OFFSET_WORKFLOW_11.21.1.md) · [기준 버전 manifest](history/validation-d9c2779.json)
- [50f02b0의 안내](https://github.com/kjuws10-rgb/A3Drilling/blob/50f02b0cc44e3b627b483d9b8fff54119abe8eab/docs/ManualInspection/README.md) · [그 버전 manifest](history/validation-50f02b0.json)
- [전체 Cell·파일 복귀 검증](../../tests/ManualInspection/CELL_OVERVIEW_QA.md) · [지도 중심 검증](../../tests/ManualInspection/MAP_FIRST_QA.md)
- [시험 실행 안내](../../tests/ManualInspection/README.md) · [첨부 HTML 원본](Reference/A3_Review_Workspace.html)
