# Cell overview · 산포 축 · 파일 작업 복귀 검증

검증 기준은 `feature/manual-inspection-cell-overview-20261121`의 작업 트리이며 이전 기준은 `b38867d1140f31796bfc62531b8735a286069131`이다. 원래 첫 Cell 진입은 유지하고 `전체 Cell` 선택으로 Recipe 실제 배치를 보는 정책을 검증했다.

## 최종 결과

| 범위 | 결과 | 별도 QA 출력의 상대 경로 |
|---|---:|---|
| 전체 솔루션 Release | 경고0 / 오류0 | `build-final.log` |
| 순수 작업 엔진 회귀 | 113/113 | `core-results.json` |
| 기존 UI 회귀 | 385/385 | `final/ui-{base,integration,sync,coverage,hardening,visual,mapfirst}` |
| 신규 Cell/축/복귀 UI | 72/72 | `final/ui-cell/ui-cell-overview-results.json` |
| 실제 Controller + 비동기 fake 장비 Backend | 108/108 | `final/controller-production.json` |
| 실제 CSV/Recipe/순수 엔진/활성 보정 파일 연결 | 24/24 | `final/backend-read.json` |
| 변경 범위·기존 코드 보존 | 27/27 | `final/scope.json` |

QA 출력 루트는 `D:\CodexWork\A3_Manual_Inspection_20261121_CellOverview`이다. 소스 링크 사전 실행과 실제 Assembly 실행은 상당 부분 같은 시험이므로 별도 기능 수처럼 합산하지 않는다. 위 표의 서로 다른 하네스도 일부 의미가 겹친다.

`final/final-hash-consistency.json`에서 실제 두 연결 하네스가 최종 생산 `Drilling.UI.dll`과 동일한 SHA-256을 사용하고 신규 UI의 다섯 소스 해시가 현재 파일과 일치함을 확인했다. 로드된 벤더 Assembly는 0개다.

## 확인한 주요 경계

- Cell 번호순과 실제 위치순이 다른 합성 Recipe를 사용해 전체 배치가 번호순 재배열이 되지 않는지 확인했다. 실제 polygon의 회전·Y 방향, polygon 밖 AABB 모서리 오선택 방지, 표시 필터에 의해 배치가 움직이지 않는 조건을 검사했다.
- Cell 제목 일반 클릭은 해당 Cell 조회, Ctrl 클릭은 그 Cell의 유효점 전체를 추가/해제한다. 일부 선택 상태에서는 나머지를 추가하며 다른 Cell 선택을 지우지 않는다. geometry에만 있는 빈 Cell도 빈 조회 화면으로 전환된다.
- Auto 산포 축은 현재 모집단과 원점을 기준으로 X/Y 같은 축척으로 맞추며 공차 또는 5 µm의 불필요한 표시 하한을 쓰지 않는다. 고정 축 밖 점은 화면 hit에서만 빠지고 통계 모집단은 보존된다. 범위 변경 직후 다음 Render 전의 옛 hit도 선택되지 않는다.
- 선택 Cell 산포는 선택점이 속한 Cell의 전체 결과를 모은다. 직접 체크박스를 변경하면 수동 범위가 우선하며 따라가기 버튼으로 자동 범위에 복귀한다. 지도 판정·검색·Head 필터가 이 모집단을 줄이지 않는다.
- 산포를 펼쳐도 지도 최소 높이는 240 DIP 또는 작업영역 높이의 45% 중 큰 값을 유지한다. 컴팩트·와이드에서 지도 전체 배치와 산포 plot을 각각 스크롤로 확인할 수 있고, 산포를 닫으면 지도 중심의 기본 화면과 스크롤 0 상태로 복귀한다. 이 추가 5개 회귀도 포함했다.
- 산포 점 위 더블클릭의 첫 클릭이 일시적으로 한 Cell만 선택해도 원래 다중 선택·명시적 null focus와 전체 모집단을 복원한 뒤 Auto Fit한다. 외부 source/version·수동 범위 변경·빈 영역 클릭 뒤에는 오래된 선택을 복구하지 않는다.
- 파일 메뉴는 돌아가기·파일 불러오기·CSV 내보내기만 노출한다. 사용자 세션 Import/Export와 최근 결과 목록 UI는 제거되었지만 내부 checkpoint와 소비 이력 검사는 유지된다.
- 최초 파일을 열기 전 원작업의 raw·복사·후보·검증 한계·Level·선택·필터·정렬·지도/산포 zoom·축·내부/외부 접기 상태를 별도로 보존한다. 연속 파일을 열어도 최초 원작업으로 복귀한다. 취소·읽기 실패·오래된 확인 요청에서 자동 장비 동작이나 보정 저장은 일어나지 않는다.
- 파일에서 보정 저장 후 돌아가면 현재 B를 유지하고 옛 후보를 버린다. 이전 native raw는 보존하되 새 보정 앵커로 재적용하지 못한다. 다른 Run으로 바뀌면 옛 raw를 새 기판에 승격하지 않는다.
- 실제 writer/parser가 만든 MEASURED/EDITED/MANUAL CSV를 실제 loader로 읽어 명시적으로 채택된 파일 측정 receipt의 후보 생성·검증·순수 엔진 commit을 검사했다. EDITED/MANUAL의 알 수 없는 OriginalMeasurement는 null을 유지하고 새 직접 측정은 파일 receipt를 제거한다.
- 자동 유입 관측에는 명시 채택 receipt가 생기지 않는다. SIM/실측 모드가 맞지 않는 파일은 조회·이동·새 측정 대상으로 열리지만 그 값이 보정 앵커로 승격되지는 않는다. 현재 모드의 새 측정 이후에 후보 생성이 가능하다.
- 실제 활성 보정 파일과 committed receipt를 만든 뒤 같은 CSV를 다시 열어도 이전 token을 재적용하지 못한다. Recipe/Head/설계 좌표/Shot/Beam 불일치, 기존 버전 모호 이력·현재 문맥 검증은 보존했다.

## 재현한 문제와 수정 확인

파일 작업에서 일부 측정 후 실패했을 때 같은 파일 또는 다른 파일을 다시 열면 새 엔진이 미완료 상태를 지울 수 있었다. `controller-source-preflight3.json`에서 105개 중 이 경계 하나가 실패했다. 생산 코드가 미완료 묶음의 파일 교체·복귀를 거절하도록 수정된 뒤 최종 실제 Controller 108개에서 같은 경계가 통과했다. native 중단 묶음도 같은 정책으로 남은 점을 보존하고 Retry가 가능하다.

시각 검토에서 작은 Cell의 미사용 제목 줄바꿈과 회전 타일 라벨 앞부분 clipping도 수정 후 확인했다. 현재 그림은 한 줄 말줄임과 회전 타일 내부의 온전한 라벨을 보여준다.

기존 검사는 삭제하지 않았다. 이전 per-Cell 시험은 Cell 1을 명시적으로 선택한다. 기존 최근 결과 메뉴 세 검사는 의도적으로 단순화한 파일 메뉴의 설명·취소·현재 버전 전달 검사로 갱신했다. 기존 지도 `+`/`−` 검사도 새 산포 버튼과 구분하도록 접근성 이름으로 원래 대상을 지정했다.

## 이미지와 검증 한계

`final/ui-cell`의 `cell-overview-dark-compact.png`, `cell-overview-light-wide.png`, `cell-selected-scatter-light.png`, `cell-selected-scatter-dark.png`, `cell-rotated-y-up.png`를 직접 확인했다. Light/Dark, 작은 화면, 125%/150% 상당 DIP/bitmap 렌더 및 기존 32,000점 회귀를 포함한다.

펼친 상태의 실용 높이와 산포 접근 이미지는 같은 폴더의 `expanded-map-{light,dark}-{compact,wide}.png`, `expanded-scatter-plot-{light,dark}-{compact,wide}.png`에 나누어 남겼다. 긴 화면은 필요한 위치로 스크롤해 보는 정책이며 모든 내용을 한 화면에 압축하지 않는다.

이는 합성 Recipe·측정값을 사용하는 소프트웨어/화면 검증이다. 운영 CSV를 사용하거나 설비 App/Root/장비 Manager, SDK 객체, PLC/Vision/모션/AeroScript를 실행하지 않았다. 실제 장비 Backend의 이동·저장은 안전 guard를 우회하지 않았으며, 동작 연결은 실제 Controller와 fake Backend로 검사했다. 실제 모니터 DPI·물리 입력 타이밍, 축 도달 정밀도·Trigger·Vision·실물 보정 성능은 별도 설비 시험 대상이다. 오프스크린 창에서의 native 메뉴 Up/Down focus 한계는 기존 Visual QA 기록을 유지한다.
