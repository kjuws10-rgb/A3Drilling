# Manual Inspection Controller 교차검증

실제 Controller·공통 엔진을 실행하고 장비 Backend만 비동기 fake로 대체한다. 일반 WPF `Application`과 STA Dispatcher만 만들며 설비 `Drilling.UI.App`, `CRootView`, 실제 장비 Manager·벤더 SDK는 생성하지 않는다.

```powershell
# 독립 소스 링크 검사: 생산 프로젝트 bin을 갱신하지 않는다.
dotnet restore tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -p:UseActualAssemblies=false
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -p:UseActualAssemblies=false -- --report controller-source-link.json

# 실제 Drilling.UI / Common Assembly 경로: 다른 생산 빌드와 직렬로 실행한다.
dotnet restore tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -p:UseActualAssemblies=true
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -p:UseActualAssemblies=true -- --report controller-production.json
```

기본 회귀는 명시적 확인·버전, 읽기 전용, 보정 저장 성공/실패, 원시값 중복 적용 방지, 대표 복사, Flying 범위, STOP 정리, 세션·CSV, 외부 결과 동기화·파일 감시와 기존 보정의 측정 이력을 확인한다. 실제 Assembly 모드는 기존 Flying 요청 계약과 실제 CSV의 모드 기록 검사도 수행한다. 소스 링크와 실제 Assembly 검사는 상당 부분 중복되므로 독립 기능 수처럼 합산하지 않는다.

`HardeningCases.cs`의 추가 경계 검사는 다음을 확인한다.

- 외부 갱신 이후 오래된 확인 요청을 실행하지 않으며, 새 Product Run에 이전 복사·측정·잔여 동작을 섞지 않는다.
- 같은 Run의 Revision 변경·위치 감소는 미완료 작업을 조용히 삭제하거나 쓰기 가능 상태로 전환하지 않는다. 실제 Beam 추가는 기존 원시값과 공유 보정·원복 이력을 유지한다.
- 다른 Run의 세션은 거절한다. 명시적 파일 조회 뒤 현재 native 세션을 가져오면 자동 동기화를 다시 허용한다.
- UI Changed 구독자·상태 보고자 예외가 명령 semaphore를 잠그지 않으며, 앞 구독자 오류가 뒤 구독자 알림을 막지 않는다.
- STOP에서 취소 callback·정지 명령·화면 알림이 실패해도 장비 Backend 정리를 기다리는 동안 Busy를 유지한다.
- 보정 저장 도중 외부 알림은 commit 뒤에 처리한다. 저장 전 취소는 활성값을 바꾸지 않고 새 검증을 요구한다.
- 실제 임시 디렉터리의 `FileSystemWatcher`를 켜고·끄고·다시 연결해도 자동으로 설비 명령/보정 쓰기를 실행하지 않는다.
- 대표 복사 뒤 대표점을 다시 측정하더라도 적용 SourceIds JSON은 복사 당시의 불변 측정 token·Final을 기록한다. 직접 측정 후보는 현재 snapshot의 token을 기록한다.

두 모드를 전환할 때 restore의 `UseActualAssemblies` 값을 실행 모드와 맞춰야 한다. 잘못된 모드의 assets로 `--no-restore`를 실행하면 전이 참조 누락 또는 소스/Assembly 타입 중복이 생길 수 있다.

출력 JSON은 검사 이름별 결과, 실제 Controller Assembly SHA-256, 로드된 벤더 Assembly 목록을 포함한다. 합성값의 수치 일관성과 소프트웨어 상태 전이 검증이며 모션 도달 정밀도·Vision/트리거·실물 보정 성능을 확인한 결과가 아니다.
