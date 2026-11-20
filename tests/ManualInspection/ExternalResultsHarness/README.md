# 외부 결과의 안전한 조회 병합

```powershell
dotnet run --project tests/ManualInspection/ExternalResultsHarness/ExternalResultsHarness.csproj -c Release -- --report external-results.json
```

실제 `Drilling.Common` 프로젝트의 `CManualInspectionResultProjection`과 공통 DTO를 사용한다. 입력은 합성 결과 파일 DTO와 합성 Recipe 목표이며 파일/장비 Manager는 생성하지 않는다. 로드된 벤더 Assembly가 없는지도 검사한다.

자동 연결은 현재 Recipe, 명확한 GlassId, Product 생성/최근 가공 완료 이후, Shift, Stage·Vision Simulation 모드, Head/설계 위치/Beam이 모두 맞을 때만 한다. 행별로 가장 최근의 구체적인 `MEASURED`/`EDITED`/`MANUAL`/`ERROR` 관측을 고른다. 외부 결과는 UI의 IMPORTED 조회값이며 native 직접 실측으로 승격하지 않는다.

`RETAINED`는 새 파일에 저장될 때 원래 실측 시각이 아니라 새 저장 시각을 갖는다. 그래서 이전 Run의 값을 현재 측정으로 혼합하지 않도록 자동 연결에서 제외한다. `UNMEASURED`도 기존 실측을 지우지 않는다. 이 파일들과 모드·기하가 없는 구형 자료는 최근 결과 목록에서 명시적으로 선택해 조회할 수 있다.

시험 범위는 순수 병합 정책이다. 실제 결과 writer의 mode·일관성 기록, 파일 변경 감시·Controller의 미적용 후보/잔여 보호, 활성 CSV 보정값 색상, 물리 모션/트리거·Vision 수신은 각각 별도의 검증 범위다.

`HardeningCases.cs`는 정확한 Run 시작 시각과 직전 tick, 파일 저장 시각과 개별 행 관측 시각의 차이, 파일 입력 순서, Beam 기본값 중복, 다른 기판/Recipe의 최신 행, 잘못된 수치, 불명확한 현재 기판, 좌표 허용오차, 여러 메뉴의 파일/Origin 보존과 현재 목표 중복을 추가로 확인한다. 다른 경로의 최신 파일이라는 이유만으로 유효한 이전 실측을 덮거나 다른 Cell·Beam으로 이관하지 않는 것이 검사 목적이다.
