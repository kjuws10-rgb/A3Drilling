# 11.24.0 · Retry / visibility / Monitor QA

기준: `3a2526dacf988cf303a102dc432a6eaa9394e9d4` → `feature/manual-inspection-retry-visibility-20261124.0`. master 미병합.

## 3회 검토

| 회차 | 검토와 조치 |
| --- | --- |
| 1 | Map·Core/Controller·독립 QA가 원인을 따로 추적했다. 고정 라벨, 잔여 큐의 전역 차단, 수신 후 last-point와 현재 요청의 차이, Monitor 실 바인딩/좌표계 경로를 대조했다. |
| 2 | 셀 비례/회전·부분 노출, 500ms 점멸 수명, 새 작업의 비파괴 Preview와 시작 시 전환, 실패한 옛 잔차 재사용 차단, 실제 Flying 행 callback을 상호 검토했다. Monitor DESIGN 요약의 GX/GY 직접 합산과 작은 값 표시를 수정했다. |
| 3 | 동결된 소스의 Release 빌드, 실제 Assembly Controller/Backend, 12종 WPF, 소스/DLL/보고서 SHA·문서·이미지·범위 검사를 독립 확인한다. 최종 결과는 manifest와 qa-round-3.json을 따른다. |

## 실행한 최종 검사

- 전체 Release 빌드: 경고 0 / 오류 0.
- Core: 206/206. 원작업 보존·명시 새 요청·취소/Mask/stale·늦은 old operation 응답·NG·세션·옛 raw 재적용 금지 포함.
- 실제 Assembly Controller: 200/200. 점멸 target-before-response·P2P/Flying 행·종료/STOP 정리·late callback·새 선택·선택/일괄 보정 제한·실제 SIM Replay 복수 행/취소 포함. 이전 단계의 소스 링크 195는 중복 합산하지 않는다.
- 실제 Backend: 105/105. 순수 Recipe/Setting/Review manager·실제 CSV writer/reader와 **Monitor BuildCoordinateViewerData → ApplyCoordinateViewerData → live polling**까지 확인한다. 기존 Recipe13/Monitor6와 새 Monitor Shot Detail8/읽기 복구1을 포함한다.
- WPF: 12종 742/742. 기존 713에서 셀 라벨/점멸24와 새 측정 재진입5를 추가했다. 전체 셀 181, 측정 흐름44이며 나머지 10모드는 기존 검사를 유지한다.
- 범위 검사: 33/33. Monitor 표시와 Manual 관련 코드만 변경하고 기존 Root/Recipe·좌표 수식·저장 writer·다른 Correction·테마·운영 데이터·벤더 파일 보존을 확인한다.

숫자는 최종 JSON과 대조한다. Map 별도 181 및 Core/Controller 소스 링크 시험은 위 통합 결과에 더하지 않는다. 이전 writer35/projection53은 동일 구현의 과거 근거이며 이번 실행 수가 아니다.

## 테스트 준비 중 발견한 fixture 문제

Monitor를 장비 생성자 없이 만드는 시험에서 속성 갱신 dictionary 초기화가 빠져 처음 실행이 실패했다. 실제 생성자가 초기화하는 dictionary를 fixture에도 넣었다. 오래된 7 mm Review 값이 있는 fixture의 `plan.Shots.First()`는 좌표 정렬 뒤 다른 Shot을 고를 수 있었다. 대상은 설계 Shot의 안정된 키 A1로 지정하도록 수정했다. 생산 guard나 합격 기대를 완화하지 않았다. 새 버전 표기 기대와 UI fixture의 존재하지 않는 위치 속성도 수정했다. 실패 중간 보고서는 최종 합격 근거로 사용하지 않는다.

## 재현과 한계

Core `dotnet run --project tests/ManualInspection/ManualInspection.Tests.csproj -c Release -- --report <path>`.

Controller는 `UseActualAssemblies=true`로 빌드한 실행 파일, BackendReadHarness는 정식 UI Assembly를 참조한 실행 파일을 사용한다. UiHarness는 `--cell-overview`, `--measurement-workflow`를 포함한 12모드를 실행한다. 정확한 SHA·개별 항목은 [manifest](../../docs/ManualInspection/validation.json)에 보존한다.

시험은 합성 스냅샷·fake 장비 경계와 실제 순수 메서드/파일 처리에 한정한다. Monitor 생성자·Root·장비 Manager·벤더 SDK를 실행하지 않는다. 모터 실제 위치나 가공 정밀도를 시험한 것으로 해석하지 않는다.
