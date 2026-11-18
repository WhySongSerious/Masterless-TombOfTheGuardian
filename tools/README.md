# 보스 디렉터 학습 도구 (ML·강화학습)

기준 기획: 단일 난이도 + 점수(`PLAN_SCORE_1116`). 난이도 선택은 없고, 하나의 손잡이 범위 안에서 보스 디렉터가 다음 패턴과 공격 대기를 고른다. 전조·판정·피해는 바꾸지 않는다.

## 구성

| 파일 | 역할 |
|---|---|
| `Assets/Soullike/Scripts/ML/BossDirectorAgent.cs` | PPO 에이전트. 패턴을 고를 때마다 한 번 결정(패턴 19개 중 후보만 열림 × 대기 0.4·0.7·1.0·1.4·1.8초). 관측 55개. 압박 배율(점수)도 여기서 나온다 |
| `Assets/Soullike/Scripts/ML/BossDirectorArena.cs` | 학습·평가 결투장. 판마다 플레이어·보스를 새로 만들어 리셋(씬 리로드 없음). 실행 인자로 조건·봇 단계·판 수 |
| `Assets/Soullike/Scripts/ML/RuleBot.cs` | 규칙 봇 3단계(초보·중급·숙련). 사람과 같은 입력 층(`IPlayerCommandSource`) |
| `Assets/Soullike/Scripts/ML/BossDirectorRuntime.cs` | 게임에서 `Resources/ML/BossDirector.onnx`가 있으면 추론으로 켠다. 모델이 없거나 오류면 기본 추첨 |
| `Assets/Soullike/Scripts/ML/SimulationMode.cs` | 학습 모드(히트스톱·슬로모션·캘리브레이션 판 끔), 대기 시간 훅 `IBossPacingPolicy` |
| `Assets/Soullike/Scripts/Editor/ML/BossDirectorTrainingBuild.cs` | 학습 실행 파일 빌드(학습 씬을 잠깐 만들어 빌드하고 지움) |
| `boss_director.yaml` | PPO 설정 |
| `train.ps1` | 학습 실행 |
| `bot_sweep.py` | 봇 단계 보정(여러 설정을 동시에 돌려 승률·끝 체력 비교) |
| `pattern_weights.py` | 지도 학습: 전투 로그로 로지스틱 회귀 → 패턴 가중치 후보 |

## 보상 (한 판 = 한 에피소드)
- 결정마다: 두 체력 비율 차가 0.25 미만이면 +0.02, 아니면 −0.01(접전 유지)
- 같은 패턴 3연속 −0.2, 후보에 없는 패턴(마스크 밖) −0.05
- 결말: 플레이어가 체력 35% 미만으로 이김 +1, 80% 넘게 남기고 이김 −1, 그 사이 +0.3 / 보스 체력 30% 미만 남기고 플레이어가 짐 +0.7, 70% 넘게 남기고 짐 −1 / 시간 초과(240초) −0.5

## 준비 (작업공간 바깥 폴더 `_ml/`, 저장소 밖)
1. `python -m venv _ml/venv` → `_ml/venv/Scripts/python -m pip install torch==2.7.1 --index-url https://download.pytorch.org/whl/cpu` → `pip install mlagents==1.1.0 protobuf==3.20.3 scikit-learn pandas`
   (전역 Python의 protobuf가 새 버전이면 mlagents가 안 돈다. 결정 신경망이 작아서 CPU판 torch로 충분하다)
2. 학습 실행 파일: Unity 메뉴 **Tools/Project Soullike/ML/Build Boss Director Training Player** 또는 배치
   `Unity.exe -batchmode -projectPath <프로젝트> -executeMethod ProjectSoullike.Editor.BossDirectorTrainingBuild.Build -mlBuildOut <작업공간>/_ml/build -logFile <로그>` (‑quit 없이)

## 학습
```
powershell -ExecutionPolicy Bypass -File Tools/ML/train.ps1 -RunId director-v1 -Envs 6
```
결과: `_ml/results/<RunId>/BossDirector.onnx`, 전투 로그 `_ml/telemetry/<RunId>/`. 텐서보드: `_ml/venv/Scripts/tensorboard --logdir _ml/results`.
게임에 넣기: onnx를 `Assets/Soullike/Resources/ML/BossDirector.onnx`로 복사(설정의 디렉터 사용이 켜져 있으면 자동으로 쓴다).

## 봇 보정 (11/17)
규칙 봇은 사람보다 방어가 서툴러(숙련 봇도 공격의 약 25%를 맞음) 체력 배수로 실력 차를 맞춘다(학습 결투장에서만, 게임에는 안 씀).
보정은 학습·평가와 같은 조건(한 프레임 = 게임 1/60초 고정)에서 해야 한다. 실제 시간으로 잰 1차 보정(1, 1.6, 2.5배)은 고정 프레임에서 숙련 승률이 0%로 떨어져 디렉터 v1이 모든 상대에게 봐 주기만 배웠다.
기본 `-hpScale 1.5,2.8,4.5`(초보·중급·숙련): 고정 보스 상대로 40판씩 → 초보 승률 0%(끝 보스 체력 0.69), 중급 5%(0.25), 숙련 55%(0.06).
사람 기록이 생기면 같은 방법(`bot_sweep.py`)으로 다시 맞춘다.

## 평가 (비교 조건)
같은 실행 파일에 `-condition fixed`(A: 디렉터 없음) / `-condition rl`(C: 모델 추론, 빌드 때 `-mlModel`로 모델 지정) + `-botSkill` + `-episodes N`. 결과 `attempts.csv`의 승패·끝 체력·점수 열로 H1·H2·H3를 본다.

## 패턴 가중치(지도 학습)
```
_ml/venv/Scripts/python Tools/ML/pattern_weights.py _ml/telemetry/<RunId> --out _ml/weights_v1.csv
```
결과는 후보만 낸다. `ScorePatternWeights.cs`(v0)는 사람이 확인하고 옮긴다.
