# 학습이 끝나면 사람 없이 평가·분석까지 이어서 돌린다.
#   powershell -ExecutionPolicy Bypass -File Tools/ML/after_training.ps1 -Runs director-v1b,director-v2 [-Episodes 100]
# 단계마다 _ml/pipeline_status.txt에 시간과 한 줄을 남기고, 실패하면 그 단계에서 오류를 적고 멈춘다.
#   1) 학습 종료 대기(각 RunId의 BossDirector.onnx가 나올 때까지)
#   2) 모델을 넣은 평가 실행 파일 빌드(Unity 배치, 모델마다)
#   3) 평가: A 고정(-condition fixed) · B 규칙(rule) · C_<run>(rl, 모델 추론) × 봇 3단계 × N판, 시드 고정
#   4) Python 연결 추론 대조(마지막 모델, 숙련 봇, 15분)
#   5) analyze_eval.py(신뢰구간·H1·H2·H3·그래프), plot_training.py(학습 곡선), pattern_weights.py(가중치 후보)
#   6) _team_context/ML_RESULTS_1117.md에 자동 요약을 쓴다(해석은 사람이 덧붙인다)
param(
    [string]$Runs = 'director-v1b,director-v2',
    [int]$Episodes = 100,
    [int]$Seed = 50000,
    [string]$BaseBuild = 'build2',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$Repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Workspace = Resolve-Path (Join-Path $Repo '..\..\..\..')
if (-not (Test-Path (Join-Path $Workspace '_ml'))) { $Workspace = Resolve-Path (Join-Path $Repo '..') }
$Ml = Join-Path $Workspace '_ml'
$Team = Join-Path $Workspace '_team_context'
$Figs = Join-Path $Team 'ml_figs'
$Status = Join-Path $Ml 'pipeline_status.txt'
$Py = Join-Path $Ml 'venv\Scripts\python.exe'
$EvalRoot = Join-Path $Ml 'eval2'
$RunList = $Runs -split ','
$Skills = @('novice', 'intermediate', 'expert')

function Log([string]$text) {
    $line = "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Add-Content -Path $Status -Value $line -Encoding UTF8
}
function Fail([string]$text) { Log "실패: $text"; exit 1 }

Log "시작: 학습 $Runs 종료 대기"

# 1) 학습 종료 대기
foreach ($run in $RunList) {
    $onnx = Join-Path $Ml "results\$run\BossDirector.onnx"
    $out = Join-Path $Ml "logs\$run.out"
    while (-not (Test-Path $onnx) -or -not (Select-String -Path $out -Pattern 'Copied .*BossDirector.onnx' -Quiet)) {
        if ((Test-Path "$Ml\logs\$run.err") -and (Select-String -Path "$Ml\logs\$run.err" -Pattern 'Traceback' -Quiet)) { Fail "$run 학습 오류(logs\$run.err)" }
        Start-Sleep -Seconds 60
    }
    Log "학습 끝: $run → $onnx"
}

# 2) 평가 실행 파일 빌드
foreach ($run in $RunList) {
    $out = Join-Path $Ml "build_$run"
    $log = Join-Path $Ml "logs\build_$run.log"
    $p = Start-Process -FilePath $Unity -ArgumentList @('-batchmode', '-projectPath', "`"$Repo`"", '-executeMethod',
        'ProjectSoullike.Editor.BossDirectorTrainingBuild.Build', '-mlBuildOut', "`"$out`"", '-mlModel',
        "`"$Ml\results\$run\BossDirector.onnx`"", '-logFile', "`"$log`"") -PassThru -Wait
    if ($p.ExitCode -ne 0) { Fail "평가 빌드 $run (logs\build_$run.log)" }
    Log "평가 빌드: $out"
}

# 3) 평가
$cells = @(@{ Name = 'A'; Exe = "$Ml\$BaseBuild\BossDirector.exe"; Cond = 'fixed' },
           @{ Name = 'B'; Exe = "$Ml\$BaseBuild\BossDirector.exe"; Cond = 'rule' })
foreach ($run in $RunList) { $cells += @{ Name = "C_$($run -replace 'director-', '')"; Exe = "$Ml\build_$run\BossDirector.exe"; Cond = 'rl' } }
$jobs = @()
foreach ($cell in $cells) {
    foreach ($skill in $Skills) {
        $root = Join-Path $EvalRoot "$($cell.Name)\$($cell.Name)_$skill"
        New-Item -ItemType Directory -Force $root | Out-Null
        $jobs += @{ Exe = $cell.Exe; Args = @('-batchmode', '-nographics', '-condition', $cell.Cond, '-botSkill', $skill, '-episodes', "$Episodes",
            '-seed', "$Seed", '-telemetryRoot', "`"$root`"", '-logFile', "`"$root\player.log`""); Name = "$($cell.Name)_$skill" }
    }
}
Log "평가 시작: $($jobs.Count)칸 × $Episodes판(6개씩 동시)"
for ($i = 0; $i -lt $jobs.Count; $i += 6) {
    $batch = $jobs[$i..([Math]::Min($i + 5, $jobs.Count - 1))]
    $procs = foreach ($j in $batch) { Start-Process -FilePath $j.Exe -ArgumentList $j.Args -PassThru -WindowStyle Hidden }
    $procs | ForEach-Object { $_.WaitForExit() }
    Log ("평가 묶음 끝: " + (($batch | ForEach-Object { $_.Name }) -join ', '))
}

# 4) Python 연결 추론 대조(마지막 모델, 숙련 봇)
$last = $RunList[-1]
$pyRoot = Join-Path $Ml "eval_py2\rl_expert"
New-Item -ItemType Directory -Force $pyRoot | Out-Null
$learn = Join-Path $Ml 'venv\Scripts\mlagents-learn.exe'
$pl = Start-Process -FilePath $learn -ArgumentList @("`"$Repo\Tools\ML\boss_director.yaml`"", "--run-id=check-$last", "--initialize-from=$last", '--inference',
    "--env=`"$Ml\$BaseBuild\BossDirector.exe`"", '--num-envs=1', '--base-port=5300', "--results-dir=`"$Ml\results`"", '--force',
    '--env-args', '-condition', 'rl', '-botSkill', 'expert', '-seed', "$Seed", '-telemetryRoot', "`"$pyRoot`"") -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$Ml\logs\check-$last.out" -RedirectStandardError "$Ml\logs\check-$last.err"
if (-not $pl.WaitForExit(15 * 60 * 1000)) { taskkill /F /T /PID $pl.Id | Out-Null }
Log "Python 연결 추론 대조 끝: $pyRoot"

# 5) 분석
New-Item -ItemType Directory -Force $Figs | Out-Null
$cellArgs = foreach ($cell in $cells) { '--cell'; "$($cell.Name)=$EvalRoot\$($cell.Name)" }
& $Py "$Repo\Tools\ML\analyze_eval.py" @cellArgs --figs $Figs --out "$EvalRoot\summary.md" *> "$Ml\logs\analyze.log"
if ($LASTEXITCODE -ne 0) { Fail '분석(logs\analyze.log)' }
$runDirs = @("$Ml\results\director-v1") + ($RunList | ForEach-Object { "$Ml\results\$_" })
& $Py "$Repo\Tools\ML\plot_training.py" @runDirs --out "$Figs\training.png" *> "$Ml\logs\plot.log"
$telemetry = ($RunList | ForEach-Object { "$Ml\telemetry\$_" }) + @($EvalRoot)
& $Py "$Repo\Tools\ML\pattern_weights.py" @telemetry --out "$Ml\weights_v1_candidate_2.csv" *> "$Ml\logs\weights.log"
Log '분석 끝: summary.md, ml_figs, weights_v1_candidate_2.csv'

# 6) 결과 문서
$doc = Join-Path $Team 'ML_RESULTS_1117.md'
$text = @"
# 보스 디렉터 ML·RL 결과 (자동 요약, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))

학습: $Runs (각 50만 결정, 봇 체력 배수 1.5·2.8·4.5, v2는 패턴 비중 상한 0.3). 평가: A 고정 · B 규칙 · C(PPO) × 봇 3단계 × $Episodes판, 시드 $Seed, 게임과 같은 ONNX 단독 추론.
Python 연결 추론 대조: _ml/eval_py2. 해석과 한계는 아래 "해석" 칸에 덧붙인다.

![학습 곡선](ml_figs/training.png)
![H1 결과](ml_figs/h1_outcomes.png)
![H2 점수](ml_figs/h2_scores.png)
![압박 배율](ml_figs/pressure.png)

$(Get-Content "$EvalRoot\summary.md" -Raw -Encoding UTF8)

## 패턴 가중치 v1 후보(로지스틱 회귀)
``````
$(Get-Content "$Ml\logs\weights.log" -Raw)
``````

## 해석
(작성 전)
"@
[IO.File]::WriteAllText($doc, $text, (New-Object Text.UTF8Encoding $false))
Log "끝: $doc"
