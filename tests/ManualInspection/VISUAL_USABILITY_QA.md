# Manual Inspection · 화면·그래픽 사용성 개선

기준 Commit은 `798359edaba1afb24977ea81185c0086d7fd57b3`, 작업 브랜치는 `feature/manual-inspection-visual-polish-20261120`이다. 기존 Review/보정 경계 안정화 위에 표시·입력·화면 갱신을 보완한다. master 병합·설비 배포는 하지 않는다.

## 화면에서 달라진 부분

| 영역 | 개선 |
| --- | --- |
| 2D 지도 | Cell·Head 구획과 행·열 비율을 살린 논리 행렬, 점과 필요 시 Shot 번호, 확대 수준에 따른 라벨 표시. 필터로 숨겨진 위치를 다른 슬롯으로 다시 배치하지 않는다. |
| 선택 구분 | 선택 테두리·포커스 점선·대표점 금색 표시를 구분한다. 실측 ● / 복사 ◇ / 기존 보정 기반 □ / Masking × / NG 표시를 함께 사용한다. 색상만으로 상태를 구분하지 않는다. |
| 범위 확인 | 전체 선택수와 현재 필터 밖에 숨겨진 선택수를 표시한다. 우클릭 메뉴와 실행 전 확인의 대상은 실제 선택 집합이다. 우측은 포커스 한 점의 값과 전체 선택 통계를 구분한다. |
| 산포도 | X/Y 동일 축척, µm 단위·원점·공차·평균·그룹 범례와 출처별 모양을 표시한다. 겹친 점은 클릭으로 순환하고 Ctrl 클릭으로 겹침 집합을 추가/해제한다. 가까운 별개 점을 자동 병합하지 않는다. |
| 값 해석 | 활성 B의 보정 Level과 측정 편차 Level을 분리한다. 기존 B 기반 이력은 현재 가공의 잔류 오차를 새로 측정한 값이 아니라는 설명을 유지한다. 후보 B+Δ→F는 실제 적용 전까지 활성 지도 색상에 반영하지 않는다. |
| 화면 크기 | 넓은 화면은 선택 통계·큰 수치와 여유 있는 우측 패널을 제공한다. 1120×560은 compact 구성을 사용하고 결과표·산포도·상세의 기본 접힘과 기본 무스크롤을 유지한다. 펼친 상세에는 내부 스크롤이 생길 수 있다. |
| 테마·키보드 | 기존 Light/Dark 자원을 사용한다. 우클릭·결과/파일·선택 도구 메뉴도 동일한 색·강조·비활성 표시를 사용한다. Ctrl+F 검색, 입력 내부의 단축키 보호, 지도/산포도 접근성 설명을 추가한다. |
| 큰 데이터 | 지도와 산포도는 점마다 WPF 컨트롤을 만들지 않는다. 화면 좌표별 검색 인덱스와 geometry/통계 캐시로 반복 탐색·정렬을 줄인다. 동일 immutable Points의 진행 메시지는 사전·선택 UI·원측정 복사 배열을 다시 만들지 않는다. |
| Flying 진행 | 측정은 모든 점을 즉시 엔진에 기록하고, 대량 결과의 전체 화면 갱신만 약 80ms 간격으로 묶는다. 첫 결과·마지막 결과·종료/오류의 최종 상태는 즉시 반영한다. 이 시간은 화면용이며 장비 타이밍·취소·파일 저장·보정 계산을 바꾸지 않는다. |

Head 0은 미배정 위치이며 Head 필터의 `0=전체` 예약값과 구분한다. 지도에는 미배정/Masking 위치를 보존하고 실제 Head 선택에는 1~8만 제공한다. 화면 좌표를 바꾸는 확대·이동·선택은 설비 명령을 보내지 않는다.

## 재현 가능한 검증

최종 검사 수·소스/DLL/report SHA는 [validation.json](../../docs/ManualInspection/validation.json)에 기록한다. 실제 WPF 합성 데이터로 다음을 확인한다.

- Light/Dark의 넓은 화면과 1120×560, 긴 Recipe/출처, 기본 무스크롤·지도/점의 부모 경계, 선택·숨김 범위.
- 125%/150%에 해당하는 가용 DIP 공간과 고해상도 비트맵 렌더. OS 모니터 DPI를 직접 바꾼 시험과 구분한다.
- Ctrl 선택·드래그 취소·마우스 캡처 소실·드래그 중 외부 기준 갱신, 확대/이동 뒤 hit 좌표, 입력 내부 Alt/Apps·검색 단축키.
- 겹친 산포도 점의 순환·부분 선택 추가·전체 선택 해제, 원측정/복사/B 기반 이력 구분·동일 축척·공차·모집단 σ.
- 32,000개 합성점의 전점 Fit와 공간 검색. 로컬 렌더 시간은 설비 Tact·실물 성능 보증이 아니다.
- Controller의 결정적 화면 시계로 대량 결과의 전체 갱신 횟수, 누락 없는 기록, 중간 오류 후 최종 표시·잔여 재측정, 시간이 흐를 때의 진행 표시.
- 기존 UI 회귀 49/23/34/71/30과 실제 Controller 회귀. 의미상 같은 실제 Assembly/source-link 검사는 중복 합산하지 않는다.

```powershell
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI D:/A3Qa/Visual --visual-usability
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -c Release -p:UseActualAssemblies=true -- --report controller-visual-results.json
python tests/ManualInspection/verify_scope.py --report visual-scope.json
```

Controller는 일반 STA WPF Application과 fake Backend를 사용한다. UI는 실제 View/그리기/테마 소스를 사용하고 제어기는 요청을 기록하는 fake다. 장비 App/Root/Manager·벤더 SDK·모션·PLC/Vision·AeroScript·레이저를 실행하지 않는다. 기존 엔진·좌표 Bridge·Backend·writer·외부 결과 projection·다른 Correction 화면·Theme/Config/Data가 기준과 같은지도 Git 내용으로 확인한다.

메뉴의 Up/Down 실제 포커스 이동 두 항목은 통과 수에 넣지 않는다. 비활성 화면 밖 시험에서는 기본 시스템 메뉴와 수정 메뉴 모두 `Focus()=false`였고 키 처리 이후 실제 포커스 이동을 확인할 수 없었다. 기본 메뉴와 대조한 관찰을 JSON에 보존했다. Enter는 명시한 이벤트 소스에서의 routed 동작, Esc는 메뉴 닫기로 범위를 구분했다. 실제 활성 창의 방향키 포커스 이동은 사용자 PC에서 별도 확인해야 한다.

## 적용 범위와 남은 확인

이 개선은 화면 사용성과 표시 처리의 검증이다. 기존 [Review 경계 검토](HARDENING_QA.md)의 실제 장비 도착·정지/IOF·트리거/UID·단위/축 방향·정밀도·Field 및 보정 허용값 확인 항목은 그대로 남는다. 새 UI에서 보이는 보정량과 이미 가공한 기판의 실제 잔류 오차를 혼동하지 않는다. 실제 장비에서는 새 가공 후 재측정으로 최종 확인한다.
