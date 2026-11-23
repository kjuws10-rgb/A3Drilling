# 11.24.0 실패 후 새 선택 측정·현재 대상 표시 검토

대상 저장소: `D:\CodexWork\A3_Manual_Inspection_20261120\repository`

기준: `3a2526d`, 작업 브랜치 `feature/manual-inspection-retry-visibility-20261124.0`.

## 1차: 실제 차단 원인과 계약

- Controller는 새 `request.Keys`를 받았으나 Core `PreviewOperation`이 미완료 목록이 있으면 정확한 잔여 재시도만 허용해 새 수동 측정을 거절했다.
- 실제 실행 중/파일 저장 중 가드는 유지한다. idle 상태의 명시적 Move·현재 위치 Measure·MoveMeasure·Flying은 새 작업을 시작할 수 있다.
- 미리보기·취소·오래된 요청·Masking뿐인 요청은 기존 재시도 목록을 바꾸지 않는다. 검증된 `BeginOperation` 성공 때만 새 작업으로 전환한다.
- 이전 미완료 작업 전체는 새 저장 형식 대신 기존 Audit의 `OP_SUPERSEDED` JSON으로 보존한다. 원측정·B·적용 ledger·이전 파일은 그대로 남긴다.
- 예전 미완료 위치에 이번 작업의 새 응답이 없었다면 이전 잔차의 현재 보정 자격만 해제한다. 완료한 값과 실제 새 NG 응답은 보존한다.

## 2차: 구현과 source-linked 시험

- Core 새 9개 회귀: 취소 가능한 순수 Preview, Begin 원자 교체, 정확한 과거 큐 보관, 옛 잔차 재보정 금지, 완료/raw/NG/ledger 보존, 늦은 operation ID 거절, Session 복원, 기존 잔여 재시도 유지.
- Controller 새 13개 회귀: 새 선택이 이전 잔여 위치로 대체되지 않음, stale·masked 요청 무변경, 현재 위치/이동만 전환, NG 저장 거절, P2P의 응답 전 현재 대상 게시, 정지 정리 대기, 비측정 작업 표시 금지, Flying 현재 행 표시, 실패 종료 clear, 늦은 active callback 무시, 구형 Backend default overload 호환.
- `core-round2.json`: source-linked **206/206 통과**.
- `controller-round2.json`: source-linked **195/195 통과**.

## 3차: 독립 피드백 확인

- QA agent가 Preview에서 큐를 지우면 취소 시 복구가 불가능함을 지적했고, Begin 성공 때만 보관·교체하도록 확인했다.
- Root/QA가 전역 미완료 가드를 해제하면 예전 OK 잔차가 Bulk에 다시 들어갈 수 있음을 점검했다. 응답 버전이 증가하지 않은 과거 remaining의 working 값만 없음으로 전환하고 Original/history는 보존한다.
- Flying 전체 예약 목록을 현재 점으로 표시하지 않도록 실제 Backend의 각 실행 행 직전 typed active callback과 행 `finally`의 빈 목록을 추가했다. Controller는 Flying에서 단일 현재 점을 추측하지 않는다.
- P2P는 Backend 호출 직전 Key와 `[Key]`, Flying은 단일 Key 없음과 현재 실행 행 목록을 게시한다. 이동만/보정 저장은 측정 중 표시가 없으며 완료·실패·정지 정리 후 clear한다.
- 실제 Backend current-line 연결은 소스로 검토했고, 비동기 전환·실패·late callback 의미는 fake typed callback으로 시험했다. 장비 도착이나 실제 Vision 성능을 검증했다고 주장하지 않는다.

실제 DLL/Backend/WPF 통합과 전체 빌드는 Root의 별도 최종 결과를 따른다. 동일 source-linked 시험을 actual 시험과 중복 집계하지 않는다.

장비 App/Root·벤더 SDK·PLC·Vision·AeroScript 실행, master 병합, commit/push는 하지 않았다.
