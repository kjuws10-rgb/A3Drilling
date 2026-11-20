# 독립 소스·정책 검토 기록

이 파일은 첫 Manual Inspection `a062c701`의 검증 기록이다. 이후 결과 동기화·공통 Level·화면 단순화의 현재 검증은 [SYNC_QA.md](SYNC_QA.md)와 `docs/ManualInspection/validation.json`을 따른다. 이전 시험 수를 후속 소스의 재실행 결과로 합산하지 않는다.

사용자 첨부 HTML 전체와 11.19.0 기반 신규 Manual Inspection을 대조했다. 기존 Correction 화면은 교체하지 않는다. 아래 검증은 서로 다른 경계이며 한 종류의 시험으로 합산하지 않는다.

| 검증 경계 | 결과 | 실제 사용한 구현 / 대체한 외부 경계 |
| --- | --- | --- |
| 첨부 HTML oracle | 14/14 | 원본 HTML·실제 브라우저 입력, 외부 통신 없음 |
| Common 계산·상태·세션 | 111/111 | 실제 신규 Common 소스, 합성 측정·가짜 Field 검증기 |
| 좌표 bridge·CSV 저장 | 22/22 | 실제 순수 planner·변환·offset writer·CSV parser 소스, 계약 DTO fixture와 고유 임시 CSV |
| Controller 통합 | 최종 38/38 | 실제 Drilling.UI 프로젝트 참조, generic WPF Application/STA, 가짜 비동기 Backend. 원래 35개에 최종 경계 3개 추가 |
| WPF 화면 | 최종 49/49 + 23/23 | 실제 View·지도·산포·Light/Dark 테마, 가짜 Controller. 최종 저장소 소스에서 두 묶음 재실행 |

Controller 시험에서 `Drilling.UI.App`, Root, 장비 Manager를 생성하지 않았고, 로드된 벤더 Assembly도 없었다. 이 기록은 실제 설비 모션·PLC·Vision·비상정지·광학/가공 정밀도 합격을 뜻하지 않는다.

## 교차 검토로 보완한 경계

- 작업 편차·Judge와 직접 원측정의 일치, 세션의 Run/좌표/Revision 계보를 엄격하게 대조한다.
- ERROR/미측정 응답은 완료로 세지 않고 잔여에 남긴다. 유효한 NG 응답은 측정 완료로 세되 자동 보정은 허용하지 않는다.
- 실제 CSV의 0.000001 mm 저장 정밀도와 가공 소비 코드의 0.001 mm 반올림을 구분한다. 파일 저장이 성공하기 전 활성 B를 바꾸지 않는다.
- 대표점의 같은 raw를 적용한 후 다시 복사할 때 편차를 두 번 더하지 않는다. 같은 Cell/Shot을 공유하는 Beam의 활성 B와 소비 raw도 같이 관리한다.
- raw A → raw B → 과거 A, 같은 raw의 시각/응답 UID 변경, 재시작·세션 복원·표시 Beam 변경으로 소비 이력을 지울 수 없게 한다.
- 다른 Shot에 대표값만 복사한 경우는 대표점의 raw를 잘못 소비하지 않는다. 여러 대상 그룹에 같은 최종 F를 적용할 수 있다.
- 멈춤 요청 후 실제 Backend cleanup 반환까지 Busy를 유지한다. 부분 측정·retry 취소·오류에서 잔여를 지우거나 자동 보정을 재개하지 않는다.
- CSV의 IMPORTED/EDITED 조회값과 직접 측정값을 구분하고, ERROR 원본에 가짜 숫자를 넣지 않는다.
- 실제 UI 프로젝트 참조 빌드로 네임스페이스 충돌과 Common 내부 도착 확인 API 접근 문제를 찾아 수정했다.
- 추가 독립 검토로 Flying 단일행 번호 1 재설정, 쓰기 직전 현재 Run/Recipe/설정 재확인, 첫 checkpoint 오류의 Operation 종료, 공통 Stage cleanup 소유권 차단을 보완했다. 기존 Fine의 실제 순수 요청 검증기도 반사 호출로 확인했으며 장비 Manager는 생성하지 않았다.
- 정보창을 열어 둔 동안 HEAVY STOP이 사용자 창 닫기를 기다리지 않게 했다. 실제 모션·측정·보정 쓰기의 cleanup 대기는 유지했다.

## 별도 소스 설정 수치 관찰

로컬 검증용 설정으로 순수 가공 planner의 20,740점(45 Cell)을 만들고, Head별 Encoder Scale의 독립 Stage 추종 제거식과 새 bridge의 정지 기여분을 전수 대조했다. 모든 점의 수치가 일치했다. 실제 Head USE와 소스의 Masking/Chess 제외 조건을 적용하면 실행 대상은 19,525점, 제외는 1,215점이었다.

현재 해당 설정의 기존 활성값을 그대로 유지해도 실행 대상 중 90점이 정지 Field ±55 mm 조건을 벗어났다. 전체 가공점에서는 91점이며 최대 절댓값은 55.9815 mm였다. 이는 새 보정 저장 회귀의 실패로 세지 않고, 기존 설정·가공점의 Field 경계 확인 사항으로 분리했다. `CReviewManager.CreateAllPlan` 장비 객체를 실행한 결과가 아니라, 그 소스의 동일한 제외 조건을 적용한 순수 수치 관찰이다. 설정 원문·측정 운영 레코드는 이 저장소에 포함하지 않는다.

## 현장 확인 범위

실제 장비에서는 새 탭의 권한·인터락, 현재 위치 측정의 정지/도착 피드백, P2P·Flying의 트리거/결과 수와 식별자, STOP 이후 신호 정리, 실제 소프트리밋을 확인해야 한다. 기존 Correction 동작의 실장비 회귀와 가공 후 새 Run의 재측정 정밀도도 별도다. 오프셋을 저장하는 행위만으로 같은 기판의 이미 가공된 구멍 위치가 바뀌는 것으로 시뮬레이션하지 않는다.
