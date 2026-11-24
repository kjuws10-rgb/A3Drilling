# 11.24.1 · First load / state copy / side panels QA

기준: `301ff172660d32fb6fb59b190e4025945b1ef4c9` → `feature/manual-inspection-firstload-panels-20261124.1`. master 미병합.

## 3회 검토

1. 초기 버튼을 clean no-CSV / 저장 B만 있음 / 새 실측 / 소비된 결과 / 자동 복원 미완료로 나눠 추적했다. 초기 값 없음과 실제 측정 0을 구별하고, 좌우 패널 이동의 검색·자동 선택·파일 복귀·높이 계산 영향을 대조했다.
2. 빈 상태 복사를 숫자 Proxy/Init와 분리했다. nonzero B 보존·Undo·세션·외부 관측 시각·불완전 큐 전환의 사전 검증·원문 백업 실패를 상호 검토했다. 실제 WPF 작은 화면·125/150% 배율·모든 페이지의 필수 조작 가시성을 검사했다.
3. 모델 구분, 응답 없는 실패 후 빈 상태 보존, 실제 DLL/출처·문서/그림·범위·최종 보고서를 독립 확인한다. 완료 결과는 manifest 및 qa-round-3.json을 따른다.

## 실제로 확인할 경계

- 값 없음 대표 → 초기 점 / nonzero B 점 / 이전 NG 원측정 점. 빈 작업 상태와 실제 B·Init·원문·ledger 분리.
- 같은 값·판정 복사, 빈 상태 복사, 실제 0, NG/미측정 실제 보정 금지, 파일에 존재하는 저장 B의 Native Adapter 연결.
- 잘못된 요청·stale·Masking·모델/Run/좌표 기준 변경·현재 실행 중·부분 실패·명시적 복원·startup 디스크 복원의 구분.
- 전체 checkpoint 검증 후 정확한 bytes 백업. 검증/백업 실패 시 원문 덮어쓰기 없음. post-retire 상태까지 사전 검증하여 실패 요청이 큐를 지우지 않음.
- 빈 상태 복사 후 오래된 파일 재관측으로 상태 부활 금지. 실제 새 측정 후 정상 해제. ORIGINAL 산포는 보존 원문, OFFSETS 산포는 실제 B, WORKING 산포는 현재 빈 상태를 구분.
- 양쪽/한쪽/접힘·필터 활성·하단표·산포·Level 실제 입력 7개와 저장·긴 이력 8개·패널 및 내부 페이지 복귀. 중앙/좌우 외부 스크롤 없음, 데이터 목록의 국소 스크롤과 구분.

## 시험 준비 중 발견한 문제

XAML 재배치 스크립트가 `minidom.firstChild`의 공백 노드를 선택해 EditorBody를 재부착하지 못한 중간 파일을 Root의 실제 파일 검색으로 발견했다. 보존한 원문에서 해당 XAML만 재생성하고 실제 디스크 XML·기존 이름·handler 연결·컴파일·WPF로 다시 확인했다. 메모리 DOM 검사만 통과했던 중간 결과는 합격 근거로 쓰지 않는다.

이전 회귀의 산포도 부모, 접이식 페이지 가시성, 미완료 Init/대상 없는 Bulk 버튼 기대는 이번 사용자 변경에 맞췄다. NG/미측정·중복·실제 실행 중 보호 기대는 유지한다. 지도 확대/축소 버튼에서 빠진 접근성 이름도 복원했다. 옛 산포도 부모를 상대로 한 TransformToAncestor 예외는 새 왼쪽 viewport 기준으로 갱신했다.

## 최종 결과

Release 경고0/오류0, Core **221/221**, 실제 Assembly Controller **214/214**, 실제 Backend **107/107**, WPF **13종 843/843**, 범위 **34/34**를 통과했다. Backend에는 Native 최초 빈 상태/저장 B 보존 2개와 기존 Recipe·Monitor 표시 회귀가 포함된다. WPF에는 상태 복사 후 WORKING/ORIGINAL/OFFSETS 산포 분리 4개와 좌우 패널 96개가 포함된다.

최종 Release 경고/오류, Core·실제 Assembly Controller/Backend·13종 WPF·범위 수치 및 SHA는 [manifest](../../docs/ManualInspection/validation.json)를 기준으로 한다. 각 소스 링크 사전검사·Sidebar 별도 검사를 통합 결과에 더하지 않는다. 보호된 writer/좌표식·다른 메뉴 회귀는 실행한 검사와 이전 동일 구현 근거를 구분한다.

시험은 합성 스냅샷과 실제 순수 코드/파일 처리에 한정한다. 장비 Root/Manager/벤더 SDK·PLC/Vision/실모션·AeroScript·레이저를 실행한 실기 합격이 아니다.
