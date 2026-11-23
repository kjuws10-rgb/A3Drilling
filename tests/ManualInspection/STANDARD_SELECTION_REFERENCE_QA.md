# 11.23.2 · 표준 선택·대표 지정·연동 3회 교차검증

기준 Commit은 `89e92a015802523ba81b871a752d0c71e62e627f`다. 최종 횟수·소스·DLL·보고서 SHA는 [manifest](../../docs/ManualInspection/validation.json)를 따른다.

## 1차 · 계약과 호출 관계

화면 행·열 끝 이동, Shift 고정 기준점의 사각 범위와 반대 방향 축소, 필터/Masking 제외·행 감기 금지, 지도 보기 이동과 장비 이동 분리를 확인했다. Cell 라벨의 화면 크기·회전·클릭 범위·겹침과 대표 지정/값 복사/보정 자격을 구분했다. Manual→native writer→Recipe/Monitor/Correction의 생산자·소비자와 단위를 읽었다.

## 2차 · 상호 소스 검토와 집중 회귀

- Core: 측정 0/빈 0, 실제 B 근거, NG·판정 없음·nullable 편차, 프록시 평탄화와 원본 의존 순환, 동결 F의 부분 변경, 역사적인 역방향 복사, 미채택 파일, legacy optional metadata, 복원 시 실제 기준 결합, Init/Undo/Redo를 검사했다.
- Map: Ctrl/Shift/CtrlShift, 범위 축소·기준점 초기화, 행·열 끝/필터·Masking, 회전·Y 반전·Cell/Head 경계, Space/Esc와 Alt, 라벨 48개·축소·충돌·클릭·실제 배치 불변을 검사했다.
- View/Controller: 모든 상태의 대표 표시, 출처/판정 변경 후 유지, 모델/좌표계 변경 시 해제, 미저장 편집 중 지정, 값 없음·부분 좌표 복사 금지, 원측정/NG 상태·중복 소비·파일 및 저장 가드를 확인했다.

기존 ‘proxy·기존 B·IMPORTED는 대표일 수 없음’ 기대는 새 사용자 요구에 맞게 갱신했다. 원측정·편차·레벨·필터·장비 가드 검사는 삭제하지 않았다. partial F의 한 축을 임의로 추측하지 않는 기존 회귀도 보존했다.

## 3차 · 실제 Assembly와 전체 회귀

정식 solution Release, 실제 Controller+Common+UI contract, Native Backend/CSV와 기존 Recipe/Monitor 표시 회귀, WPF 기존 11모드+새 대표 지정 모드를 실행했다. 실제 Head0 legacy SCANNER 근거 차단과 정상 Head4 채널 보존도 검사했다. Head0 시험은 사용 Head를 하나 켠 채 기판 밖에 배치하며, 최소 사용 Head 준비 조건은 완화하지 않았다.

Source-linked Controller는 actual Assembly 시험과 중복 합산하지 않는다. 1·2차 집중 회귀를 최종 전체 횟수에 다시 더하지 않는다. 이전 writer/projection은 이번 보호된 동일 구현의 과거 근거이며 실행했다고 주장하지 않는다.

```powershell
dotnet build Drilling.sln -c Release
dotnet run --project tests/ManualInspection/ManualInspection.Tests.csproj -c Release -- --report core.json
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -c Release -p:UseActualAssemblies=true -- --report controller.json
dotnet run --project tests/ManualInspection/BackendReadHarness/BackendReadHarness.csproj -c Release -- --report backend.json
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI private-output --cell-overview
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI private-output --representative
python tests/ManualInspection/verify_scope.py --report scope.json
```

WPF는 옵션 없음 및 integration-impact/sync-levels/coverage-compact/hardening/visual-usability/map-first/cell-overview/offset-init/measurement-workflow/bulk-history/representative 12모드다. Dark/Light의 실제 WPF 렌더를 검토했다. 실제 장비 App/Root/Manager·벤더 SDK·모션·PLC·Vision·AeroScript·레이저와 실제 OS 입력/DPI 전환 시험은 실행하지 않았다.
