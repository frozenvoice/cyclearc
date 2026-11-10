# CI 아티팩트 보존 정책

GitHub Actions 아티팩트는 업로드되는 순간에 보존 기간이 정해집니다. 기간이 지나면 GitHub가 그 아티팩트를 삭제합니다. 이 저장소는 그 기간을 각 `actions/upload-artifact` 단계의 `retention-days`에 적습니다.

## 워크플로

워크플로 3개(`.github/workflows/windows.yml`, `windows-build-local.yml`, `windows-e2e.yml`)는 `workflow_dispatch`만 사용합니다. `push`, `pull_request`, `pull_request_target`, `schedule`을 넣지 않습니다. `windows.yml`의 `cancel-in-progress`는 `false`입니다. 선택한 전체 검증이 실행 중일 때 다른 요청이 그 실행을 취소하지 않습니다.

감사 시점의 활성 아티팩트는 754개, 약 51.41GB였습니다. 큰 항목은 `CycleArc-win-x64` 235개 / 약 31.64GB, `setup-install-logs` 147개 / 약 10.47GB, `widget-previews` 154개였고, 예전 이름 `prometer-win-x64`와 `CodexMeter-win-x64`도 남아 있었습니다. 대부분은 과거에 push/PR 자동 실행이 같은 산출물을 반복 업로드한 결과입니다. 자동 실행은 이미 꺼져 있습니다.

## 보존 기간

이 공개 저장소에서 `retention-days`는 1 이상 90 이하입니다. 값을 빼면 저장소 기본값인 90일이 적용됩니다. `0`도 그 기본값을 뜻하므로 명시적 보존으로 치지 않습니다.

| 아티팩트 | 일수 | 이유 |
| --- | --- | --- |
| `widget-previews` | 7 | 위젯 미리보기. 실패한 화면을 잠시 확인하는 용도 |
| `setup-install-logs` | 14 | 설치·repair 진단 로그 |
| `CycleArc-verification-tools` | 14 | 같은 실행의 포터블·시작 검증이 받는 도구 |
| `setup-shortcut-choices` | 14 | 바로가기 선택 검증 기록 |
| `windows-build-local.yml`, `windows-e2e.yml`의 업로드 | 14 | 기존 값 유지 |
| `failed-test-results` | 90 | 실패한 테스트 결과 |
| `portable-distribution-evidence` | 90 | 포터블 검증 기록 |
| `managed-startup-evidence` | 90 | 관리형 시작 검증 기록 |
| `CycleArc-published-win-x64` | 90 | 릴리스가 비교하는 게시 실행 파일 |
| `CycleArc-win-x64` | 90 | 릴리스가 내려받는 설치·패키지 산출물 |

`CycleArc-win-x64`는 90일입니다. `scripts/Release.ps1`은 선택한 `windows.yml` 실행에서 `CycleArc-win-x64`와 `CycleArc-published-win-x64`가 둘 다 만료 전일 때만 그 실행을 릴리스 원본으로 씁니다. 두 아티팩트의 보존 기간이 같으면, 게시 실행 파일 기록이 남아 있는 동안 설치 패키지도 받을 수 있습니다. 과거 용량은 자동 실행이 같은 패키지를 반복 업로드하면서 쌓였고, 앞으로는 수동 실행만 업로드합니다.

같은 워크플로의 다음 job이 `download-artifact`로 받는 일은 보존 기간과 별개입니다. 그 다운로드는 실행 중에 일어납니다.

## 이미 있는 아티팩트

`retention-days`는 앞으로 올라가는 아티팩트에 적용됩니다. 이미 있는 아티팩트는 업로드 당시의 보존 기간이 끝나면 GitHub가 만료시킵니다. 워크플로를 고친 뒤에도 그 기존 아티팩트는 그 날짜까지 남아 있습니다.

## 검사

저장소 루트에서 실행합니다. 표준 라이브러리만 사용합니다.

```powershell
python3 scripts/check-artifact-retention.py
python3 scripts/check-artifact-retention.py --self-test
```

`retention-days`가 없거나 1–90의 정수가 아니면 종료 코드 1로 실패합니다. `push`, `pull_request`, `pull_request_target`, `schedule`이 있으면 경고만 출력합니다. 보존 기간이 맞으면 그때의 종료 코드는 0입니다. 수동 전용 정책을 어긴 워크플로를 게이트에서 실패시키는 검사는 기존 `tests/VerificationWorkflow.Tests.ps1`입니다.
