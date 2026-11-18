# 보스 디렉터 학습 실행.
#   .\train.ps1 -RunId director-v1 [-Envs 6] [-Resume] [-MaxSteps 300000]
# 준비: 바깥 작업공간의 _ml/venv(mlagents 1.1.0 + protobuf 3.20.3)와 학습 실행 파일(_ml/build/BossDirector.exe,
#       Editor 메뉴 Tools/Project Soullike/ML 또는 BossDirectorTrainingBuild.Build로 만든다).
# 결과: _ml/results/<RunId>/BossDirector.onnx, 텐서보드 기록. 전투 로그는 _ml/telemetry/<RunId>/.
param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [int]$Envs = 4,
    [switch]$Resume,
    [int]$MaxSteps = 0,
    # 학습 실행 파일에 넘길 추가 인자를 공백으로 이어 한 문자열로(예: -EnvArgs '-patternShareCap 0.3')
    [string]$EnvArgs = '',
    # 학습 실행 파일 폴더(_ml 아래), 학습 두 개를 동시에 돌릴 때는 BasePort를 다르게
    [string]$Build = 'build',
    [int]$BasePort = 5005
)
$ErrorActionPreference = 'Stop'
# mlagents는 설정 파일을 시스템 기본 인코딩으로 읽는다(한국어 Windows는 cp949). 주석이 한국어라 UTF-8로 읽게 한다.
$env:PYTHONUTF8 = '1'
$Workspace = Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..\..')
if (-not (Test-Path (Join-Path $Workspace '_ml'))) { $Workspace = Resolve-Path (Join-Path $PSScriptRoot '..\..\..') }
$Ml = Join-Path $Workspace '_ml'
$Learn = Join-Path $Ml 'venv\Scripts\mlagents-learn.exe'
$Env = Join-Path $Ml "$Build\BossDirector.exe"
$Results = Join-Path $Ml 'results'
$Telemetry = Join-Path $Ml "telemetry\$RunId"
New-Item -ItemType Directory -Force $Telemetry | Out-Null

$Config = Join-Path $PSScriptRoot 'boss_director.yaml'
if ($MaxSteps -gt 0) {
    $Config2 = Join-Path $Results "$RunId.yaml"
    New-Item -ItemType Directory -Force $Results | Out-Null
    $text = [IO.File]::ReadAllText($Config, [Text.Encoding]::UTF8) -replace 'max_steps: \d+', "max_steps: $MaxSteps"
    [IO.File]::WriteAllText($Config2, $text, (New-Object Text.UTF8Encoding $false))
    $Config = $Config2
}

$mlArgs = @($Config, "--run-id=$RunId", "--env=$Env", "--num-envs=$Envs", "--results-dir=$Results", "--base-port=$BasePort",
          '--env-args', '-telemetryRoot', $Telemetry) + @($EnvArgs -split ' ' | Where-Object { $_ })
if ($Resume) { $mlArgs = @('--resume') + $mlArgs } else { $mlArgs = @('--force') + $mlArgs }
& $Learn @mlArgs
