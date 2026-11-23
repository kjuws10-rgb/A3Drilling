# Manual Inspection 11.23.1 검증

기준 Commit: `367a7276822d73d7709cd64f94847ee979c4d557`. 최종 횟수와 현재 소스·Assembly·보고서 SHA는 [manifest](../../docs/ManualInspection/validation.json)를 따른다.

## 주요 시나리오

- 전체 Cell의 몸체·타일·제목·Ctrl+클릭은 Cell 진입, 드래그·Ctrl+드래그는 측정점 선택이다. 회전한 셀, 줌/팬, 작은 흔들림, 마지막 Up만 먼 위치로 들어온 빠른 드래그, 캡처 소실, 필터·Masking을 확인한다.
- 방향키 선택·Space 메뉴·Esc 닫기, 열린 메뉴 방향키 통과, Alt 보기 이동, 키 반복, 선택을 화면 안으로 보여주는 동작을 검사한다. 화면 선택으로 장비 명령은 발생하지 않는다.
- 최초 CSV 없음/저장 완료 Init도 새 측정이 가능하다. 초기값 없음과 실측 0은 다르다. 이전 미저장 초안 자동 복원 경로는 바이트 백업·원문 보존·복구 실패·취소·다른 모델 전환·명시적 복원과 미완료 작업을 따로 검사한다.
- 선택점 보정은 기존 계산·검증·저장 경로를 한 명령으로 실행한다. NG/Masking/미선택/이미 적용한 raw, 실제 0 재요청, 대표값, 한 Shot의 Beam 공유, 검증·저장 실패, 연타, Undo/Redo를 확인한다.
- 적용 확인창의 취소·중첩·연속 클릭·버전 변경·자격 상실·예외가 저장을 유발하지 않는지 확인한다. 기본 화면에 세 단계 버튼을 노출하지 않고 편집 중 저장 버튼만 보이는지 검사한다.
- 저장 Offset은 같은 모델의 Recipe 미리보기·Monitor 표시에만 적용한다. 편집 항목/Recipe 저장 입력과 보호 Offset은 불변이다. 같은 경로의 내용 교체, SHA가 없는 저장소의 내용 지문, Init/Undo/Redo, 잘못된 파일, 느린 폴링 중복, 모델 전환 중 오래된 응답, 읽기 실패 복구를 검사한다.

## 재현

```powershell
dotnet build Drilling.sln -c Release
dotnet run --project tests/ManualInspection/ManualInspection.Tests.csproj -c Release -- --report core.json
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -c Release -p:UseActualAssemblies=true -- --report controller.json
dotnet run --project tests/ManualInspection/BackendReadHarness/BackendReadHarness.csproj -c Release -- --report backend.json
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI private-output --cell-overview
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI private-output --bulk-history
python tests/ManualInspection/verify_scope.py --report scope.json
```

WPF 회귀는 기존 11개 모드를 보존한다. 사용자 요청으로 바뀐 클릭·확인·버튼 기대만 갱신했으며 기존 검사를 삭제하지 않는다. 소스 링크 시험과 실제 Assembly 시험의 중복 회수는 합산하지 않는다. 이전 writer/projection 시험은 보호된 동일 구현의 이전 근거이며 이번 실행 횟수에 더하지 않는다.

Controller는 합성 Backend, Backend 시험은 실제 순수 Recipe/Review planner·CSV와 임시 합성 자료를 사용한다. Monitor의 표시 갱신은 생성자/장비 polling service를 실행하지 않고 실제 표시 메서드에 순수 의존성을 주입한다. Recipe 시험도 설비 Root·벤더 객체를 만들지 않는다. 실제 프로그램 창·장비 시퀀스·물리 입력 주입·실장비 정밀도 시험으로 과장하지 않는다.

독립 교차검토에서 발견한 복구 보호의 모델 간 누수, 폴링 중첩, 오래된 Recipe Build 취소 전파, 잘못된 저장 파일의 표시 오류 경계를 수정했다. 실제 Recipe 전환 중 Root의 메뉴 재시도는 소스 연결을 검토하고, Menu 단계의 중단/다음 정상 Build를 합성 시험했다. 장비 Root 자체를 실행한 시험은 아니다.
