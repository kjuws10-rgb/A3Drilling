# 통합 Offset · Init · 현재 모델 재연결 검증

기준 Commit은 `50f02b0cc44e3b627b483d9b8fff54119abe8eab`이다. 새 브랜치는 `feature/manual-inspection-offsets-20261121.1`, 프로그램 버전은 `11.21.1`(Assembly `2026.11.21.1`)이다. master에는 병합하지 않는다.

## 검증 범위

- `Offset Init`은 모든 Cell/Shot/표시 Beam의 측정 기반 Review 누적값을 **미저장 후보 0**으로 만든다. 활성 B·기존 파일·원측정은 저장 전 바뀌지 않는다. 선택점만 초기화하지 않는다.
- Init 후보는 오래된 큰 B를 제거할 수 있도록 증감 한계만 의도적으로 예외 처리하며, 최종 총량/전체 유효점 Field 검사는 유지한다. 파일에서 가져온 절대 최종값은 증감 한계도 그대로 적용한다.
- Init·파일 교체 후보 동안 이동/새 측정/대표 복사/원본 전환을 막아 실제 활성 B와 미저장 후보의 좌표 기준이 섞이지 않게 한다. 저장·검증·내보내기·편집 취소는 가능하다. 이는 제거한 사용자 조회 전용 모드와 다른 미저장 상태다.
- 저장 성공 후 작업 편차·대표 복사·파일 채택 자격을 지우되 OriginalMeasurement·이력·이미 소비한 raw 토큰은 보존한다. 취소는 이전 후보/작업값을 복구하며 기존 승인은 다시 검증한다. 저장 실패·취소는 실 B를 유지한다.
- 0→0이어도 새로 저장된 명시적 행은 보정 이력·L1이다. 저장 직후와 새로고침 뒤의 `HasAppliedOffset`·Level을 대조한다.
- 실제 최신 Review CSV는 이미 누적된 절대 B이다. 이전 timestamp 파일들을 다시 합산하지 않는다. Init 저장 행 구성은 표시하지 않는 과거/미배정 행까지 0으로 만들고 과거 CSV 바이트는 유지한다.
- 휴대 JSON은 DESIGN-mm Review 누적 맵과 보호 설정의 **참고 전용** 스냅샷, 실제 순수 planner가 생성한 SCANNER-mm 최종 좌표를 함께 담는다. Default/First·Distortion·기구 오프셋의 서로 다른 좌표계 값을 단순 숫자로 더하지 않는다. 보호 설정과 최종 Scanner 명령은 가져오기로 적용하지 않는다.
- 휴대 맵은 같은 모델 전체 Cell/Shot을 요구한다. 같은 Shot의 표시 Beam 증감은 실제 Recipe의 각 Beam 기하/Head/Masking/key를 검증한 뒤 허용하고 하나의 절대값을 형제 Beam에 공유한다. 부분 Shot 맵·다른 모델/좌표 기준·가짜/범위 밖 Beam·중복/비정상 JSON·잘못된 단위는 거부한다.
- 과거 조회 전용 flag·구형 이력·손상 checkpoint가 새 측정을 영구 차단하지 않는다. 현재 모델/회차/기하/활성 B를 재연결하고 불확실 old raw를 직접 측정으로 승격하지 않는다. 기준이 바뀌면 한 번 대상 재확인을 요구한 뒤 새 측정할 수 있다.
- 재연결 전 원래 작업과 대상 checkpoint를 Recovery에 보존하고, 대상의 유효한 미적용 후보/부분 측정을 복구한다. 손상 JSON 원문 바이트도 유지한다. A 파일→B 모델 전환에서 Recovery 쓰기를 실패시켜도 A 파일 모드·원래 native checkpoint가 섞이거나 덮이지 않는지 검사한다. 삭제된 CSV/이전 Recipe CSV는 동작 시 현재 모델로 재연결할 수 있으나 명시적인 잘못된 파일 가져오기는 계속 거부한다.

## 화면 검사

신규 `UiHarness --offset-init`은 37개를 확인한다. 중복 전체 Cell 버튼 개수, 확인 취소/최신 버전 요청, 보호 항목 설명, 미저장 표/지도 문구, 저장 전 작업 차단, 휴대 파일 메뉴와 내보내기, 취소·기본 접힘, 이전 readonly flag와 무관한 새 작업 접근을 포함한다.

산포의 기본은 `통합 Offset / 전체 셀·Head별 / 전체 Head`다. 최신 residual·NG·수신 오류가 있어도 명시적 적용 B를 사용하며, 0 보정은 포함하고 보정 행 없음과 Masking은 제외한다. 여러 Cell을 선택하면 그 Cell의 전체 B를 모으며 지도 검색/판정 필터에 의해 줄어들지 않는다. Auto·고정 축·맞춤을 대조하고 원측정 보기에는 B를 raw처럼 넣지 않는다. 원자료가 표시 전환으로 바뀌지 않는지도 확인한다.

기존 457개 UI 검사는 삭제하지 않았다. 기존 측정용 시험은 WORKING/CELL을 명시하고, 새 OFFSETS/HEAD 기본값은 신규 시험에서 별도로 확인한다. 기존 readonly 차단 기대는 현재 모델 재연결/새 동작 허용으로, 파일 메뉴 3항목은 휴대 Offset 가져오기/내보내기를 포함한 5항목으로 의도적으로 갱신했다. Busy·미저장 Init·오래된 확인창·선택 범위 보호는 유지한다.

## 재현과 결과 위치

별도 QA 루트는 `D:\CodexWork\A3_Manual_Inspection_20261121_1_Offsets`이다. 최종 숫자·실행 소스와 Assembly SHA는 [검증 manifest](../../docs/ManualInspection/validation.json)를 따른다.

| 구분 | 출력 |
| --- | --- |
| 순수 엔진 | `final/core.json` |
| 실제 Assembly Controller + fake Backend | `final/controller-production.json` |
| 실제 Adapter/순수 Recipe/CSV/휴대 파일 | `final/backend-read.json` |
| 기존 UI 8종 | `final/ui-{base,integration,sync,coverage,hardening,visual,mapfirst,cell}` |
| 신규 UI | `final/ui-offset/ui-offset-init-results.json` |
| 범위·버전 | `final/scope.json` |

`dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI D:/A3Qa/ui-offset --offset-init`으로 새 화면 시험을 재현한다. Common/Controller/BackendRead는 [테스트 안내](README.md)의 기존 명령을 사용한다. source-link 사전 실행과 실제 Assembly 시험을 중복 합산하지 않는다.

실제 Backend의 저장은 장비 Manager가 없으면 기존 guard에서 차단된다. 따라서 전체 행 구성은 순수 private builder를 반사 호출해 검증하고, 반환한 행은 실제 CSV writer의 별도 합성 임시 폴더 거래로 검사했다. 이를 실장비 저장·모션/보정 성능 시험으로 표현하지 않는다.

## 한계

모든 값은 합성 Recipe·CSV·측정/설정 데이터다. 설비 App/Root/장비 Manager·벤더 SDK·PLC/Vision/모션/AeroScript·레이저를 실행하지 않았다. WPF 그림은 합성 화면이며 실제 장비 캡처가 아니다. 실제 모니터 DPI·물리 입력 타이밍·장비 도달/트리거/공차/새 가공 정밀도는 별도 현장 검증이다. 기존 비활성 창 native 메뉴 방향키 2건은 여전히 통과 수에서 제외한다.
