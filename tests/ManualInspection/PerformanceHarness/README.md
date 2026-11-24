# Manual Inspection Controller 성능 재현

실제 Core와 Controller를 소스로 연결해 현재 위치 한 점 측정을 반복합니다. 장비, Vision, SDK, 실제 기판 데이터, 설비 `App`과 `Root`는 실행하지 않습니다. 가짜 Backend는 `Task.Yield`로 응답하며 지연 시간을 넣지 않습니다. 작업 상태 JSON은 실제 임시 폴더에 직렬화·저장합니다.

```powershell
$env:A3_BENCH_SOURCE_ROOT = (Get-Location).Path
dotnet run --project tests/ManualInspection/PerformanceHarness/PerformanceHarness.csproj -c Release -- performance.json 1000,10000,20000,32000 3
```

매 규모마다 별도 모델을 만들고 로드를 따로 측정합니다. 작은 모델의 준비 실행 후 실제 측정 세 번을 기록합니다. 측정 후 원측정, 미측정인 다른 점, 기준 보정 0, 암묵적 보정 저장 없음도 확인합니다. 임시 JSON은 복구 내용을 확인할 수 있도록 자동 삭제하지 않습니다.

- `WallMs`: 실제 JSON 보관을 포함한 명령 완료 시간입니다.
- `MaxDispatcherGapMs`, `MaxDispatcherQueueDelayMs`: 10 ms 간격으로 요청한 UI Input 작업의 실행 간격과 대기 시간입니다. 요청은 하나만 쌓이도록 제한합니다.
- `TotalAllocatedBytes`, `UiThreadAllocatedBytes`: 전체 및 UI 스레드의 할당량입니다. GC 횟수와 화면 게시·점 목록 재생성 횟수도 기록합니다.

이 결과는 Native Recipe/Setting/CSV 파일 검사, 실제 Vision 응답, 이동, 실제 화면 렌더링 시간을 포함하지 않습니다. 작은 표본의 타이머 지연은 운영체제와 GC 영향도 받으므로 실장비 지연이나 전체 프로그램 개선율로 해석하지 않습니다.

이전 코드와 비교할 때 `SourceRoot`를 보존된 소스 루트로 바꾸고 `A3_BENCH_SOURCE_ROOT`도 같은 경로로 지정합니다. 두 실행은 같은 하네스·모델·반복 수를 사용하고 다른 빌드·벤치를 중지한 뒤 순서대로 실행합니다. 소스 및 실행 DLL 해시를 함께 보존합니다.

```powershell
dotnet run --project tests/ManualInspection/PerformanceHarness/PerformanceHarness.csproj -c Release -p:SourceRoot=D:/saved-source -- before.json 1000,10000,20000,32000 3
```

`--checkpoint-due`를 마지막에 넣으면 가짜 응답 직전에 Controller의 마지막 보관 시각만 과거로 설정합니다. 이는 주기 보관이 필요한 상태의 UI 정지 여부를 보는 별도 진단입니다. 실제 측정 지연을 흉내 내려고 기다리는 기능이 아니며, 정상 before/after 개선율에 합산하지 않습니다.

Native 파일 I/O의 스레드 분리·취소·오류·같은 경로 SHA 변경 검사는 `BackendReadHarness/NativeIoSchedulingCases.cs`에 있습니다. 동기 읽기를 이벤트로 제어하는 해당 검사는 실행 순서와 UI 응답 가능성을 검사하며 성능 수치를 만들지 않습니다. 실제 Native `SaveCore`의 설비 Guard는 실행하지 않습니다.
