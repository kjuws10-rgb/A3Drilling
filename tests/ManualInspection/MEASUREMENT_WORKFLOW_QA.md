# Manual Inspection 11.21.2 검증

기준은 `d9c2779f888f1cc925c1515724a00038fe633bca`의 11.21.1이다. 측정 편차 Level·OK/NG를 중심으로 화면을 줄이고, 현 위치 측정과 Cell/Head별 측정 보정 초기화를 추가한 변경을 검사한다. 기존 실행 시험은 삭제하지 않았다.

## 검사 범위

- Common: 기존 131개에 범위 초기화·실제 0 측정·공유 Beam·원복·세션 상태·구형 세션 복구 검증 21개를 추가했다. 초기화는 선택 OffsetKey의 형제를 함께 0으로 만들며, 다른 Cell/Shot의 값과 원측정은 유지한다. 유효한 0 실측과 초기화 0은 별도 상태다.
- Controller: 기존 실제 Assembly 127개에 Cell/Head 요청, 성공·실패·부분 측정의 최신 결과, Flying callback, 현재 위치 측정, 원복 초기화 경계 16개를 추가했다. 실제 Assembly와 소스 링크 모드는 중복 합산하지 않는다.
- 실제 CSV/Backend: 기존 38개에 상태·시각 저장, 구형 CSV, 상태만 달라지는 저장, 숨은 Cell 행과 Head 미상 행, 초기화 시각 유지, portable schema 2/구형 1, 비정상 상태/시각 거부, 미저장 실측 중 활성값 내보내기 16개를 추가했다.
- WPF: 기존 494개를 유지하고 `--measurement-workflow` 38개를 추가했다. 새 결과는 오른쪽에 즉시 보이고, NG는 Level과 관계없이 빨강, 초기화는 회색, 실제 0 실측은 정상 Level 색이다. Light/Dark, 1490×900·1120×560, 접힘·확장 화면과 지도 우선 공간을 확인한다. 산포도 확장 직후 두 화면 크기 모두 내부 viewport에 plot 160 DIPs 이상이 보이는지 확인하며, 축 설정을 먼저 스크롤해야 그래프가 보이는 배치를 방지한다.
- Bridge/writer 35개와 외부 결과 projection 53개를 재실행한다. 기존 좌표 변환·파일 저장 동시성·원자성·결과 병합 경계가 유지되는지 확인한다.

## 기존 기대값을 바꾼 이유

적용 보정값 모드는 화면에서 숨기고 지도는 `level`/`judge` 두 종류만 제공한다. 기존 내부 보정량 검사들은 선택 모드를 명시하여 유지했다. 산포도 기본은 `WORKING` 측정 편차이며, 기존 통합 Offset 검사는 보조 `OFFSETS` 모드를 직접 선택한다. 초기화된 0행을 L1로 보던 시험은 초기화 상태·회색을 기대하도록 바꾸고, 실제 0 측정이 L1인 별도 경계를 추가했다.

구형 세션에 초기화 메타데이터가 없어도 숫자 0만 보고 초기화로 추정하지 않는다. 다만 활성 CSV가 초기화를 입증하면, 그 상태를 기록하지 않은 구형 세션이 회색 상태를 지우는 복구는 거부한다. 같은 초기화 Revision을 기록한 세션에 이후 새 실측이 있으면 해당 점의 실측 상태는 정상적으로 복구한다. 거부된 복구가 현재 작업공간을 바꾸지 않는지도 확인한다.

이동/이동 후 측정/이동 후 측정·보정과 잔여 재측정에는 확인을 유지한다. 파일 열기·복귀·초기화·저장·검증·대표 지정·현재 위치 측정은 확인창 없이 요청을 보낸다. 기존 확인 취소 시험은 새 즉시 요청·현재 버전·무이동 검증으로 바꾸었으며, 이동 취소/오래된 버전/Busy/미완료/기준 변경 차단 시험은 유지했다. 파일 chooser와 장비 도착·정지·Recipe/Run 일치 guard도 유지한다.

## 변경 범위 검사

`verify_scope.py` 기준 Commit을 위 Commit으로 갱신했다. 기존 Correction 수식은 상태 메타데이터만 제거한 뒤 원문과 비교한다. Review Manager는 DTO 상태 속성 추가만 허용하고 실행 본문은 그대로 비교한다. CSV writer는 새 선택 열과 초기화 시각 처리를 제거한 뒤 기존 저장 트랜잭션·숫자 계산·잠금 본문과 비교한다. 같은 writer의 중복 해시 검사 두 개를 이 전체 비교 한 개로 합쳐 범위 검사가 이전 28개에서 27개가 되었다. 실행 회귀의 수를 줄인 것은 아니다.

## 재현

```powershell
dotnet run --project tests/ManualInspection/ManualInspection.Tests.csproj -c Release -- --report core.json
dotnet run --project tests/ManualInspection/BridgeWriterHarness/BridgeWriterHarness.csproj -c Release -- --report writer.json
dotnet run --project tests/ManualInspection/ExternalResultsHarness/ExternalResultsHarness.csproj -c Release -- --report projection.json
dotnet run --project tests/ManualInspection/ControllerHarness/ControllerHarness.csproj -c Release -p:UseActualAssemblies=true -- --report controller.json
dotnet run --project tests/ManualInspection/BackendReadHarness/BackendReadHarness.csproj -c Release -- --report backend.json
dotnet run --project tests/ManualInspection/UiHarness/ManualInspection.UiHarness.csproj -c Release -- Drilling.UI private-output --measurement-workflow
python tests/ManualInspection/verify_scope.py --report scope.json
```

UI 전체 회귀는 옵션 없음, `--integration-impact`, `--sync-levels`, `--coverage-compact`, `--hardening`, `--visual-usability`, `--map-first`, `--cell-overview`, `--offset-init`, `--measurement-workflow`를 각각 실행한다. ProjectReference를 쓰는 실제 Controller/Backend 검사는 정식 빌드와 직렬로 실행한다. 소스 링크 검사와 실제 Assembly 검사를 함께 더해 기능 수를 늘리지 않는다.

최종 횟수·합격·소스/DLL/report SHA는 [validation.json](../../docs/ManualInspection/validation.json)에 기록한다. 합성 데이터·fake 장비와 순수 계산/파일 어댑터를 사용한 검증이다. 실제 장비 App/Root/Manager, 벤더 DLL, PLC/Vision/모션/AeroScript는 실행하지 않았다. 실물 정밀도·도착/정지 타이밍·트리거 수·인터락은 현장 평가가 필요하다.
