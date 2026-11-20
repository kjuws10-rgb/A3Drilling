# Manual Inspection 결과 동기화·공통 Level 검증

기준은 `a062c701d825c925a8ba74184bc8eff98dce0ab6`의 독립 Manual Inspection이다. 이 후속은 다른 Review 메뉴의 결과 열람, 이미 저장된 보정값의 전점 Level 색상, 조작 단순화를 확인한다. 장비 App·Root·Manager·벤더 SDK·PLC/Vision·모션은 실행하지 않는다.

## 독립 소스 추적

- `CReviewResultFile.Catalog`는 MANUAL의 ALL_SHOT / ONE_SHOT / ONE_LINE / BEAM_INSPECTION / ROUGH / FINE, AUTO, ZERO_DEFENCE를 함께 목록화한다. 실제 읽기에 사용한 Recipe/Glass/시각/Shift/Head/Design와 표시 출처를 확인해야 한다.
- Rough/Fine과 AutoFine은 `SaveNewFromSource`, Simple 초기화/적용은 `SaveNew`를 사용한다. 결과 저장 알림만 받아서는 적용 보정의 변경을 모두 잡을 수 없다. offset의 새 저장·원복도 감지 대상이다.
- Fine/Rough의 `RETAINED` 행은 새 파일 시각을 갖지만 새 직접 측정은 아니다. 최신 RETAINED가 다른 경로의 구체 실측을 덮지 않도록 병합 우선순위를 검증한다. UNMEASURED는 과거 실측을 0으로 지우는 뜻이 아니다.
- 파일의 같은 Recipe/Glass라는 사실만으로 같은 가공 Run을 확정할 수 없다. 마지막 가공 이전 기록·다른 Shift·Simulation·다른 Head/기하를 자동으로 현재 좌표에 섞지 않는다. 기하 근거가 부족한 파일은 명시 조회와 현재 자동 병합을 구분한다.

## 확인 기준

| 경계 | 확인할 결과 |
| --- | --- |
| 외부 결과 저장 | 같은 현재 frame의 최신 결과가 지도/표에 보임. 별도 이동·측정·보정은 실행하지 않음 |
| 외부 보정 적용 | Rough/Fine/복사에서 쓴 활성 CSV 값을 미측정 좌표까지 표시. 원측정 Judge·raw를 새로 만들지 않음 |
| 공통 Level | 같은 DESIGN µm 벡터는 Cell/Head/측정 경로와 무관하게 같은 등급. MAX_AXIS와 RADIAL 경계·유한값 확인 |
| 표시 기준 | 측정 편차와 적용 보상값을 별도 보기/범례로 명시. 미적용 후보는 활성 보정으로 색칠하지 않음 |
| 자기 저장 알림 | 새 native raw의 직접 출처·소비 이력·활성 Revision 유지, 중복 누적·갱신 루프 없음 |
| Busy·후보·미완료 | 활성 작업 중 Backend 재로드 금지. 잔여·미적용 후보를 알림이 삭제하지 않음 |
| Revision 변경 | 외부 활성값 변경 후 옛 후보 적용 차단. 실제 새 기준을 확인한 후에만 동작 가능 |
| 명시 파일 조회 | 타 기판/기하 미확인 자료를 자동 current와 구분. 현재 작업을 몰래 갈아끼우지 않음 |
| UI 단순화 | 기본 주요 동작만 노출. 장비 이동/측정/보정/원복/복사에는 범위·영향 설명과 확인/취소 |
| 취소·오류 | STOP/검증/출처/소비 ledger/원측정 보존은 삭제하지 않음. 명령 취소 시 활성값 불변 |

이 문서의 표는 검증 기준이다. 실제 실행 결과·소스 SHA·검증 수는 최종 결과 JSON과 아래 완료 기록으로 구분하며, 위 항목을 적었다는 사실만으로 합격 처리하지 않는다.

## 최종 실행 기록

최종 실제 UI 프로젝트 참조 하네스 **65/65 통과**. 기존 38개 Controller 회귀를 유지하고 동기화·Level·원측정 보존·파일 관찰·mode 저장/읽기 27개를 추가했다. 결과 JSON은 로컬 `qa-sync/controller-production.json`이며 해당 시험이 실제 로드한 Drilling.UI Assembly의 SHA와 벤더 Assembly 목록(0개)을 함께 기록한다.

소스 링크 모드도 **62/62 통과**했다. 실제 Assembly 모드에서만 실행하는 기존 Fine 요청 검증기·실제 결과 writer/parser 3개와 구분한다. 같은 검사를 두 배로 합산하지 않는다. 소스 링크 fixture는 외부 Backend 경계 계약만 대체하며, 실제 Controller·순수 엔진·UI 계약·FileSystemWatcher 구현을 직접 사용한다.

주요 실행 경계:

- Busy / 미완료 / 미적용 후보 중에는 외부 알림이 Backend 재로드나 후보 삭제를 유발하지 않는다. 외부 Revision 이후 이전 후보의 쓰기는 차단한다.
- 동기화의 첫 파일 snapshot을 읽는 도중 새 결과가 도착해도 알림을 잃지 않고 다음 갱신을 실행한다.
- 명시적으로 고른 과거 파일은 자동 current 결과로 바뀌지 않는다. 중간 취소한 동기화는 성공적인 재로드 전까지 조회 전용이다.
- 대표값을 서로 다른 Cell/Head에 복사해도 후보는 활성 보정색으로 표시하지 않는다. 실제 적용 후 같은 DESIGN µm 값은 같은 Level이고 대상 원측정은 보존된다.
- ERROR/EDITED/IMPORTED를 직접 실측 앵커로 승격하지 않는다. 원측정 없는 EDITED값의 관측 시각도 세션에 남겨 오래된 값이 최신 값을 덮지 못한다.
- 실제 고유 임시 폴더에서 CSV 변경/rename/delete, 폴더 통째로 반입·반출, debounce, 처음에 없는 root, Dispose 이후 무통지를 확인했다.
- 실제 일반 결과 writer의 Stage/Vision mode 저장/읽기와 legacy/mixed mode를 unknown으로 보존하는 parser를 확인했다. 실제 장비 Manager는 생성하지 않는다.

Rough/Fine의 RETAINED는 원실측 시각이 새 파일 시각과 구분되지 않으므로 자동 현재 병합에서 제외하고 명시적 파일 조회에 남긴다. 운영 CSV 실제 레코드는 이 시험에 사용하거나 복제하지 않았다.

원본 11.19.0 하드웨어 평가, 실제 네트워크 지연·IOF 트리거 수·물리 좌표/정밀도 검증은 별도다. 이 기록은 fake Backend와 순수 파일·UI 검증이며 전체 장비 안전·정밀도 합격을 뜻하지 않는다.
