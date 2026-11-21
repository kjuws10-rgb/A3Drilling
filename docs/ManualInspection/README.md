# MANUAL INSPECTION · 11.21.1

현재 브랜치는 `feature/manual-inspection-offsets-20261121.1`, 기준 Commit은 `50f02b0cc44e3b627b483d9b8fff54119abe8eab`다. **master에는 병합하지 않는다.** 기존 Correction의 다른 화면·좌표 계산·장비 시퀀스를 유지한다.

## 이번 동작

- 전체 Cell 전환 버튼은 M행×N열 제목 옆 **한 개**다. 작은/빈 그룹에서는 고정 위치의 한 개를 사용한다.
- Simple / Rough / Fine / One Shot / All Shot / Line Shot의 **최신 누적 Review Offset 하나**를 현재 모델(Recipe)의 측정보정으로 사용한다. 과거 CSV를 다시 더하지 않는다.
- **Offset Init → 모델 Offset 저장**: 화면의 측정 기반 통합값만 먼저 0으로 만든다. 저장 전 실제 활성값/파일은 유지하고 편집 취소로 돌아간다. 저장하면 새 누적 CSV로 반영하며 이전 파일은 보존한다.
- 초기화에서 **하드웨어·Default·First Beam·Distortion**은 바꾸지 않는다. 측정 잔차/대표값을 새 기준에 재누적하지 않도록 자격을 해제하며 원측정·이력·소비 기록은 남긴다.
- **통합 Offset JSON**은 누적 절대 Review 맵+보호 설정 참고값+기존 좌표 생성기의 최종 좌표를 한 파일로 담는다. 불러오기는 절대 교체 작업값이며 B에 다시 더하지 않는다. 보호 설정/내보낸 Scanner 명령은 적용하지 않는다.
- **조회 전용 모드는 없다.** 예전 자료/복구 오류는 현재 모델 기준을 재연결해 새 측정한다. 좌표가 바뀌면 대상 재확인, 정상 이전 작업은 복원, 복구 원문은 Recovery에 보관한다. 실제 Busy·알람·PM·좌표/저장 오류는 원인별로 확인한다.
- 산포도 기본은 **통합 Offset / 전체 Cell·모든 Head / Auto**다. 원측정 잔차와 누적 B를 별도로 표시해 기존 보정이 가려지지 않는다. 명시적 0 행도 데이터이며 없는 행을 가짜 0으로 만들지 않는다.

[초기화·저장·휴대 파일 상세](OFFSET_WORKFLOW_11.21.1.md) · [현재 검증 manifest](validation.json)

## 지도와 산포 조작

| 작업 | 조작 |
| --- | --- |
| 전체 Cell / 단일 Cell | 전체 Cell 버튼 → 실제 Recipe 배치, Cell # 클릭 → 해당 Cell |
| 여러 점 / 셀 | Ctrl+클릭·드래그, Ctrl+Cell # → 해당 Cell 유효점 추가/해제 |
| 지도 확대·보기 이동 | Ctrl+휠 / Alt+드래그·방향키 / 더블클릭 100%·맞춤 |
| 실제 이동·측정·보정 | 점 우클릭 → 이동만 / 이동 후 측정 / 이동 후 측정 & 보정 |
| 단동·상세 | 점 우클릭 → 현재 위치 한 점 측정 / 측정·출처 상세 |
| 대표값 복사 | 유효한 실측/명시 채택 값을 대표로 지정 → 대상·사유·최종값 확인. NG도 판정을 복사하나 NG 보정 적용은 막음 |
| 산포 범위 | 현재 Cell / 전체 Cell의 Head별 / 선택 Cell들, 선택 따라가기 또는 직접 체크 |
| 산포 데이터 | 통합 Offset / 작업 결과 / 원측정 기록 |
| 산포 축 | Auto / 고정 ±µm / +−·휠 확대축소 / 더블클릭 Auto 맞춤 |
| 파일 작업 복귀 | 파일 → 불러오기 전 작업으로 돌아가기. 선택·필터·지도/산포·접기 복원 |
| STOP | 확인창 없이 즉시 요청. 실제 정리 반환까지 잠금 유지 |

Cell 경계·배치·회전은 Recipe의 실제 mm 기하다. 내부 색상 타일은 **논리 측정 행렬**이며 실제 레이저 점의 mm 위치를 뜻하지 않는다. 장비 이동은 기존 좌표 계산을 사용한다.

지도는 기본 작업공간 대부분을 사용하고 표·산포·설명·보정은 접어서 확인한다. 상세를 펼쳐도 지도 최소 높이를 유지한다. 파일 복귀는 화면/작업 상태 복원이며 실제 Stage 위치나 이미 저장한 활성 보정의 롤백이 아니다. 저장 전 Init/절대파일 교체는 실제 활성 기준과 다르므로 저장/편집 취소를 먼저 진행한다.

## 실제 WPF 합성 미리보기

[통합 Offset 산포 · Dark](images/offset-11.21.1-scatter-dark.png) · [Light](images/offset-11.21.1-scatter-light.png) · [Offset Init 저장 전 · Dark](images/offset-11.21.1-init-dark.png) · [Light](images/offset-11.21.1-init-light.png)

그림은 합성 데이터로 실제 WPF를 렌더한 결과이며 설비 캡처가 아니다.

## 확인 범위

Windows/.NET 8 Release 빌드, 순수 엔진, 실제 Assembly Controller+fake Backend, 실제 CSV/순수 Recipe/Backend 연결과 WPF+합성값을 각각 검증했다. [시험 실행 안내](../../tests/ManualInspection/README.md)와 [manifest](validation.json)에 수치·소스/Assembly/그림 SHA를 기록한다. 장비 App/Root/벤더 SDK·모션/PLC/Vision/AeroScript/레이저, 실제 보정 정밀도·OS 입력/DPI는 실행하지 않았다.

## 이전 근거

이전 화면·검증은 해당 Commit의 범위다. 현재 11.21.1의 편집/통합 Offset 정책과 구분한다.

- [50f02b0의 원래 안내](https://github.com/kjuws10-rgb/A3Drilling/blob/50f02b0cc44e3b627b483d9b8fff54119abe8eab/docs/ManualInspection/README.md) · [그 버전 manifest 원문](history/validation-50f02b0.json)
- [전체 Cell·파일 복귀 검증](../../tests/ManualInspection/CELL_OVERVIEW_QA.md)
- [지도 중심 검증](../../tests/ManualInspection/MAP_FIRST_QA.md)
- [화면·그래픽 검증](../../tests/ManualInspection/VISUAL_USABILITY_QA.md)
- [기존 보정 경로 안정화 검증](../../tests/ManualInspection/HARDENING_QA.md)
- [첨부 HTML 원본](Reference/A3_Review_Workspace.html)
