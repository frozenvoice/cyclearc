# CI 아티팩트 보존 정책

GitHub Actions 아티팩트는 업로드되는 순간에 보존 기간이 정해집니다. 기간이 지나면 GitHub가 그 아티팩트를 삭제합니다. 이 저장소는 그 기간을 각 `actions/upload-artifact` 단계의 `retention-days`에 적습니다.

## 워크플로

워크플로 3개(`.github/workflows/windows.yml`, `windows-build-local.yml`, `windows-e2e.yml`)는 `workflow_dispatch`만 사용합니다. `push`, `pull_request`, `pull_request_target`, `schedule`을 넣지 않습니다. `windows.yml`의 `cancel-in-progress`는 `false`입니다. 선택한 전체 검증이 실행 중일 때 다른 요청이 그 실행을 취소하지 않습니다.

감사 시점의 활성 아티팩트는 754개, 약 51.41GB였습니다. 큰 항목은 `CycleArc-win-x64` 235개 / 약 31.64GB, `setup-install-logs` 147개 / 약 10.47GB, `widget-previews` 154개였고, 예전 이름 `prometer-win-x64`와 `CodexMeter-win-x64`도 남아 있었습니다. 대부분은 과거에 push/PR 자동 실행이 같은 산출물을 반복 업로드한 결과입니다. 자동 실행은 이미 꺼져 있습니다.

## 보존 기간

이 공개 저장소에서 `retention-days`는 1 이상 90 이하입니다. 값을 빼면 저장소 기본값인 90일이 적용됩니다. `0`도 그 기본값을 뜻하므로 명시적 보존으로 치지 않습니다.

기본은 세 가지입니다. 미리보기는 7일, 일반 빌드와 설치 로그는 14일, 테스트·배포 증거는 90일입니다.

| 구분 | 아티팩트 | 일수 |
| --- | --- | --- |
| 미리보기 | `widget-previews` | 7 |
| 일반 빌드·설치 로그 | `CycleArc-win-x64`, `setup-install-logs`, `CycleArc-verification-tools`, `setup-shortcut-choices`, `windows-build-local.yml`과 `windows-e2e.yml`의 업로드 | 14 |
| 테스트·배포 증거 | `failed-test-results`, `portable-distribution-evidence`, `managed-startup-evidence`, `CycleArc-published-win-x64` | 90 |

수동 full run은 업로드 후 14일 동안 릴리스 원본으로 쓸 수 있습니다. `scripts/Release.ps1`은 그 실행의 `CycleArc-win-x64`와 `CycleArc-published-win-x64`가 둘 다 만료 전일 때만 릴리스를 진행합니다. 게시 실행 파일 증거는 90일 동안 남고, 설치 패키지는 14일에 만료됩니다. 14일이 지난 실행으로 릴리스하려면 `windows.yml`을 다시 실행합니다.

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
