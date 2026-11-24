# 11.24.3 · 최종 Review Offset 검증

기준 `7de760ccfe57ed75de3b8c9484c0b14114857be0`. 이번 소프트웨어 검사와 실제 장비 성능을 구분한다.

## 확인 경계

- 기존 최종 저장 파일과 방식별 원문 분리, sparse 저장 행의 전체 누적 보존, 실제 0 / Init / 없는 행 구분.
- 기존 `.2` DLL에서 양 주행 방향 × Head8 × Vision Flip8 × Scale2를 비교한 256조합, 실제 CSV8왕복. 단순 이름이 아닌 최종 Scanner 기여값까지 비교한다.
- 실제 ReviewData 6종 적용과 기존 DESIGN/SCANNER B, 실제 AUTO 누적·다른 Shot 보존. Fine의 Stage-mm 출력 계약은 바꾸지 않는다.
- 두 저장 인스턴스 경합·동일 경로 내용 교체·옛 계산 기준·중복 정책·취소·잘못된 모델에서 조건부 저장이 쓰기 전에 차단되는지 검사한다.
- 미저장 후보 / Init 초안 / 열린 옛 파일 / 미완료 / 실행 중에도 final 조회만 새로 하고 실제 작업·adapter·원측정을 유지한다.
- 늦게 도착한 읽기와 저장·새 Run/좌표 기준 변경, 조회 중 또 온 알림을 확인한다. 같은 파일명·mtime이어도 내용 SHA로 새 값을 구분한다.
- 최종 export는 새 활성 파일·전체 모델을 읽고 미저장 초안과 일반 측정 후보를 제외한다. 필터·옛 결과 파일·숨겨진 기존 저장 행을 포함한 범위를 검증한다.
- 실제 WPF의 최종 지도·표·산포·파일명/경로/시각/SHA, NG 빨강·Init 회색·실측0·값 없음, 작은 화면/양쪽 패널/두 테마/보기 복귀를 확인한다.

## 결과와 근거

최종 결과·개수·소스와 DLL SHA는 [현재 manifest](../../docs/ManualInspection/validation.json)에 기록한다. 기존 13종 WPF 회귀와 새 `--final-offsets` 모드를 구분하며 source-linked Controller와 실제 Assembly Controller를 중복 합산하지 않는다.

새 Backend 회귀는 `FinalOffsetParityCases.cs` 및 `FinalOffsetSnapshotCases.cs`, Controller 회귀는 `FinalOffsetSyncCases.cs`, WPF 회귀는 `UiHarness/FinalOffsetCases.cs`에 있다. `verify_final_review_offset.py`는 변경 범위·보호 파일·좌표/저장/표시 계약을 확인한다. 기존 `.2` 통합 범위 검사기는 과거 기준으로 보존한다.

실행 중 발견한 snapshot 저장 시각 대조는 Save 반환값의 미세 tick과 파일명 밀리초 정밀도를 구분하여 실제 LoadLatest 결과와 비교한다. 조회 중 추가 알림의 누락 문제는 별도 재조회 회귀로 검증한다. 검사 기대값을 실제 결함에 맞춰 약화시키지 않는다.

## 검증하지 않은 것

같은 가공 흔적을 새 잔차로 다시 누적해도 되는지를 timestamp나 사용자 확인 클릭으로 증명하지 않는다. 기존 파일에 없는 가공 당시 Offset Revision은 unknown이다. 실제 장비·벤더 SDK·통신·실물 이동·레이저는 실행하지 않았으며 실물 정밀도 합격이 아니다.
