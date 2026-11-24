# 11.24.0 Map UI 검토·구현·교차검증

## Round 1 — 요구와 데이터 계약

- 기준 브랜치: `feature/manual-inspection-retry-visibility-20261124.0`, 기준 Commit `3a2526d`.
- 기존 고정 65×21 DIP 라벨과 11 DIP 글꼴·화면 빈자리 배치를 제거한다. 셀의 실제 화면 크기와 확대율에 비례하여 글꼴·글자 영역을 계산하고 각 셀 내부에 둔다.
- 불투명 박스 대신 투명 글자와 작은 명암 경계만 그린다. 실제 Cell polygon, 측정점 slot, 클릭/드래그·키보드 선택 좌표는 바꾸지 않는다.
- 컨트롤러 담당과 `IsMeasurementInProgress`, `ActiveMeasurementPointKey`, `ActiveMeasurementPointKeys` 계약을 합의했다. 마지막 측정 결과를 현재 위치로 추측하지 않는다.
- P2P는 확정된 현재 요청 점, Flying은 실제 Backend callback이 제공한 현재 검사 line의 대상 묶음을 표시한다. 이는 실제 모터 도착을 보증하는 표시가 아니다.

## Round 2 — 구현과 직접 시험

- 라벨의 크기는 Cell 화면 edge와 zoom에 비례하고 글꼴 상한은 22 DIP이다. 고정 badge 최소 크기는 없다. 회전 polygon 내부에 글자를 맞추고, Cell 상단이 화면 밖이면 Cell∩viewport의 내부로만 맞춘다.
- 측정 중인 대상의 윤곽과 십자를 500 ms DispatcherTimer로 점멸한다. 대상 변경은 즉시 켜고 동일 대상의 진행 알림은 점멸 위상을 유지한다. 작은 측정점에서는 선 두께·십자 크기도 함께 줄여 기존 NG/Level 배경을 덮지 않는다.
- 이동만/저장 Busy, 완료·중지, 잘못된 Key, 필터 밖·Masking·미사용 Cell, 숨김·Unloaded에서는 점멸을 중단한다. 재표시 때는 현재 확정된 측정 대상만 다시 켠다.
- 기존 마우스 Cell 진입, 드래그 복수 선택, Ctrl/Shift 방향키, Space/Esc, 산포도와 Offset 검증 시험을 유지했다.
- 최종 source-linked WPF UI 하네스 빌드: **경고 0, 오류 0**. `--cell-overview`: **181/181 통과**.
- 실제 DispatcherTimer tick, 0.5초 설정, 점멸 on/off PNG 차이, 최신 Masking 반영, 진행 알림 위상 유지, 새 frame 위상 재설정, 숨김·Unload·Reload, 원데이터와 선택·장비 요청 불변을 확인했다.

## Round 3 — 독립 검토와 시각 확인

- `/root/bulk_history_qa_1123`가 라벨 비례/viewport clip/회전 hit, 타이머 생명주기, 확정 현재 대상 계약과 NG 배경 보존을 독립 검토했다.
- 피드백을 반영하여 부분 노출된 Cell의 글자가 viewport 내부에 남도록 보완했고, standalone Dark 예제의 리소스 상속도 수정했다.
- 마지막 동결 소스를 다시 빌드·실행했다. 보고서의 소스 hash와 실행 DLL을 기록했으며, 최신 `measurement-blink-on-dark.png`와 `measurement-blink-off-dark.png`를 직접 열어 실제 Dark 배경·금색 점멸·원래 판정색을 확인했다.
- 독립 검토자의 최종 판정: **현재 동결 소스에서 확정 미해결 결함 없음**.
- `git diff --check`: 통과. 커밋·Push·브랜치 전환·master 변경은 수행하지 않았다.

## 검증 근거

- [최종 UI 보고서](ui/ui-cell-overview-results.json)
- [점멸 켬 — Dark](ui/measurement-blink-on-dark.png)
- [점멸 끔 — Dark](ui/measurement-blink-off-dark.png)
- [48개 Cell 축소 — Dark](ui/cell-labels-48-small-dark.png)
- [48개 Cell 축소 — Light](ui/cell-labels-48-small-light.png)

SHA-256:

- Map: `5B6CC72CEDD966186FAE81DD825986E498018A06DD4B373192F762CBE274FBB3`
- CellOverviewCases: `F18078DF81F2D3EAB06C69118DE8B900E7129FDA9426E0A89683BD5F27FB7989`
- 실행 UI 하네스 DLL: `00191D442C505488D884CF31FD8E194DD329F30F2B6233EF8403F465A0893D12`

아주 작은 축소 화면의 글자는 Cell과 함께 작아지므로 육안 판독에 한계가 있다. 번호가 자료에 존재하는 것과 어느 배율에서나 읽을 수 있다는 주장은 다르다. 확대하면 글자도 함께 커진다. 이번 검증은 합성 geometry·측정값, 실제 WPF 소스 연결 UI와 fake Controller 범위이며, 장비 프로그램·Motion·Vision·벤더 DLL·Script는 실행하지 않았다. 500 ms는 화면 타이머 설정이며 실시간 모터 제어 보장은 아니다.
