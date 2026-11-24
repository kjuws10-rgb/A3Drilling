# 11.24.1 최초 로딩·대표 상태 복사 검토

## Round 1: 원인과 계약

- clean Native 모델의 CSV/측정값 없음 자체는 이동·측정 또는 Offset Init을 금지하지 않았다. 빈 대표는 기존 finite F 복사 검증 때문에 복사 버튼만 비활성인 것이 정상 기존 동작이었다.
- 세 가지 버튼이 함께 막히는 경로는 첫 화면에서 디스크의 미완료 측정 Operation을 자동 복원하는 경우였다. 실제 실행 중인 작업과 첫 로딩의 중단 보관 작업을 구분했다.
- 수치가 없는 대표는 수치 0/OK/raw를 만들지 않고 현재 작업 결과의 빈 상태만 캡처한다. 활성 B, 물리적 Init 여부, OriginalMeasurement, 실제 파일과 consumed ledger는 바꾸지 않는다.
- NG·없는 결과의 실제 보정 저장 검증, 마스킹 장비 대상 제한, 현재 모델·좌표·Revision 확인과 실행 중 semaphore는 유지한다.

## Round 2: 구현과 회귀

- `ST_MANUAL_STATE_COPY` / `IsMeasurementStateCleared`를 추가해 저장 보정 초기화와 작업상태 비움을 분리했다. 대표 Recipe/Run/좌표/상태/시각/원출처를 기록하며 숫자 F는 가지지 않는다.
- `PreviewReferenceStateCopy` / `ConfirmReferenceStateCopy`는 선택·대표·source fingerprint를 확인하고 대상의 작업 판정/편차 없음만 복사한다. 대상 숫자 후보만 제외하며 unrelated 후보는 유지한다. 직전 복사 취소는 원래 대상과 후보를 복원한다.
- 새로운 실제 RecordMeasurement 또는 유효 finite 대표 복사는 logical clear를 해제한다. 같은 시각/이전 외부 파일 알림은 logical epoch 이하에서 빈 상태를 다시 되살리지 못한다.
- 자동 첫 로딩은 원본 전체 세션의 Operation/기하/Revision을 먼저 strict 검증한다. 원문 bytes를 고유 Recovery 파일에 flush·검증 후 보존하고, 중단 큐를 Audit JSON으로 보존한 뒤 현재 저장 모델에서 idle로 연다. 명시 세션 Import 및 이미 열려 있는 동일 작업공간은 이 자동 해제 대상이 아니다.
- 실패한 Recovery 백업 또는 세션 검증은 원본 checkpoint 경로를 자동 덮어쓰지 못하도록 보호한다. 다른 모델의 정상 checkpoint 저장은 계속 가능하다.
- 명시 Copy/Init의 미완료 큐 전환은 실제 엔진 변경 전에 별도 strict sandbox에서 큐 해제 후 결과를 예측한다. 잘못된 대표·선택·scope·사유면 원래 큐/Audit는 유지한다.
- Round 2: Core 218/218, source-linked Controller 210/210. `core-round2.json`, `controller-round2.json` 참고.

## Round 3: 독립 검토 보완

- 실패한 측정에서 새 응답이 없으면 기존 captured absence를 지우지 않고 state epoch만 증가시킨다. 과거 저장 B가 숫자 대표나 실측 잔차로 되살아나는 것을 막았다.
- Init/통합 Offset 저장/Undo로 작업 메타데이터를 정리할 때 IMPORTED 외국 Run/좌표 원raw의 출처를 보존한다. 이력의 원raw를 현재 native raw로 오인하여 전체 lineage를 무효화하지 않는다.
- 같은 Shot의 sibling Beam 저장은 공유 B/evidence를 갱신하지만 해당점의 명시적인 빈 상태/ERROR/NG 상태 캡처를 없애지 않는다. 실제 NG 원측정도 보존한다.
- 파일 작업에서 다른 Shot의 보정 저장 후 이전 작업공간으로 복귀해도 captured state와 새 B가 함께 복구된다.
- Recipe/Run/좌표가 다른 state metadata, self/missing reference, 위조 OK 빈 상태는 거부한다.
- 최종: Core **221/221**, source-linked Controller **211/211** PASS. `core-round3.json`, `controller-round3.json` 및 각 `.log` 참고. 이후 의미가 없는 구형 only-retry 주석 한 줄만 정정했다. 실제 어셈블리·WPF 통합과 전체 빌드는 부모 작업에서 별도로 검증한다.

## 검증 한계

모든 신규 수치와 raw는 synthetic이다. Common 소스와 Controller fake backend를 연결해 상태·동시성·세션·파일 복구 경계를 검증했다. 장비 프로그램, 벤더 DLL, PLC, Vision, AeroScript 또는 실제 축 동작은 실행하지 않았다. 원본 Git commit/push/merge 작업은 수행하지 않았다.
