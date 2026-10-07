<div align="center">

# 몬스터 어드벤처 — 충주 편

**작은 동네를 배경으로 한 포켓몬 스타일 몬스터 수집 RPG — 웹 참조 구현, 유니티/안드로이드 포팅, LAN 멀티플레이까지.**

[![Unity](https://img.shields.io/badge/Unity-6000.6.2f1-000000?logo=unity&logoColor=white)](unity/ProjectSettings/ProjectVersion.txt)
[![Platform](https://img.shields.io/badge/platform-Android%20%7C%20Windows%20%7C%20Web-blue)](#시작하기)
[![Reference](https://img.shields.io/badge/reference%20build-HTML5%20Canvas-F7DF1E)](web/index.html)

[Read in English →](README.md)

</div>

---

## 목차

- [소개](#소개)
- [주요 기능](#주요-기능)
- [기술 스택](#기술-스택)
- [프로젝트 구조](#프로젝트-구조)
- [시작하기](#시작하기)
- [LAN 멀티플레이](#lan-멀티플레이)
- [게임플레이 스펙](#게임플레이-스펙)
- [텔레메트리](#텔레메트리)
- [기여하기](#기여하기)

---

## 소개

충주시를 배경으로 한 가상의 마을에서 시작해 파트너 몬스터를 고르고, 마을과 들판을 탐험합니다. 풀숲(그리고 물)에서 야생 몬스터와 싸우고 잡아서 파티를 키우고, LAN을 통해 다른 트레이너와 대결할 수 있습니다.

이 프로젝트는 **같은 규칙을 두 가지 구현으로 나란히 유지**합니다.

- **`web/`** — 단일 HTML 파일로 된 HTML5 Canvas 빌드. 바로 플레이 가능한 참조 구현이며, 동작의 기준(ground truth)입니다.
- **`unity/`** — 안드로이드(주 타깃)와 Windows Standalone을 대상으로 한 Unity 2D 포팅. 실시간 LAN 멀티플레이가 추가돼 있습니다.

`SPEC.md`에 두 구현이 공유하는 엔진 무관 규칙(능력치 공식, 데미지, 포획 확률, 맵 생성 등)이 문서화돼 있고, 공유 "골든" 테스트로 두 빌드의 동작이 일치하는지 검증합니다.

---

## 주요 기능

- 상성표, 자속보정(STAB), 급소, 적 AI가 있는 턴제 전투
- 개체값(IV)·노력치(EV)·성격을 가진 19종의 몬스터
- 풀숲·물에서의 야생 조우(물 타입은 물에서만 등장)
- 포획, 회복소, 상점, 학교 수학 퀴즈 미니게임
- 출석 보너스, 저장/불러오기, 도감
- **LAN 멀티플레이** (Unity 빌드 전용):
  - 같은 Wi-Fi 안에서 자동 방 찾기(UDP 비콘 + 직접 탐색 폴백), 또는 수동 IP 입력
  - 싱글플레이 저장(같은 몬스터·레벨·가방)을 그대로 이어받아 함께하는 세계로 입장
  - 서버가 판정하는 파티 대결(상처약·교체·기절 시 강제 교체)

---

## 기술 스택

| 구성 요소 | 기술 |
| --- | --- |
| 참조 구현 | HTML5 Canvas, 순수 JavaScript |
| 게임 엔진(포팅) | Unity 6000.6.2f1 (2D) |
| 멀티플레이 전송 | `System.Net.Sockets` 직접 사용 — 길이 프리픽스 JSON over TCP(7777) + UDP 방 탐색(7778) |
| 게임 데이터 | `data/game-data.json`(종족·기술·상성표), `data/extract.js`로 생성 |
| 텔레메트리 백엔드 | Google Apps Script + Google Sheets |
| 대상 플랫폼 | 안드로이드(`com.chungju.monsteradventure`), Windows Standalone |

Mirror·Photon 같은 외부 네트워킹 미들웨어 없이, LAN 레이어는 직접 구현했고 EditMode 소켓 테스트로 검증합니다.

---

## 프로젝트 구조

```
.
├── web/
│   └── index.html          # 바로 플레이 가능한 HTML5 Canvas 참조 빌드
├── unity/
│   ├── Assets/Scripts/
│   │   ├── Core/             # 엔진 무관 규칙(전투·성장·맵·저장 등)
│   │   ├── Net/               # LAN 전송, 방 탐색, 서버 쪽 대결 마당 상태
│   │   ├── Runtime/           # MonoBehaviour, UI, 씬 연결
│   │   └── Editor/            # 빌드/테스트 도구
│   └── Assets/Tests/EditMode/ # 규칙 일치·네트워킹 테스트
├── data/
│   ├── game-data.json        # 종족 / 기술 / 상성표
│   └── extract.js            # game-data.json 재생성
├── telemetry/                 # 선택적 설치/진행상황 로깅 (Google Apps Script)
└── SPEC.md                    # 엔진 무관 게임플레이 스펙(공유 기준 문서)
```

---

## 시작하기

### 웹 버전 플레이

`web/index.html`을 브라우저에서 바로 열거나, 저장소 루트를 아무 정적 파일 서버로 서빙하면 됩니다.

### Unity 프로젝트 열기

1. **Unity Hub** + **에디터 6000.6.2f1** 설치 (APK를 빌드하려면 Android Build Support 모듈도 추가)
2. `unity/` 폴더를 Unity 프로젝트로 엽니다.
3. 메인 씬을 열고 **Play**를 누릅니다.

### 테스트 실행

`unity/Assets/Tests/EditMode`에 있는 EditMode 테스트는 게임플레이 규칙(웹 빌드와의 일치)과 LAN 네트워킹(실제 루프백 소켓)을 모두 검증합니다. Unity Test Runner(**Window → General → Test Runner**)로 실행하거나, 배치 모드로:

```bash
Unity -batchmode -runTests -testPlatform EditMode -projectPath unity -testResults results.xml
```

---

## LAN 멀티플레이

타이틀 화면에서 **함께하기 (LAN)**을 고른 뒤:

- **방 만들기** — 게임을 호스팅합니다. 같은 Wi-Fi 네트워크에 방이 알려집니다.
- **방 찾기** — 열려 있는 방을 자동으로 찾아 목록에서 고릅니다.
- **주소로 접속 (고급)** — 브로드캐스트를 막는 네트워크를 위해 호스트 IP를 직접 입력합니다.

전체 설계 과정과 알려진 한계는 `SPEC.md`의 "멀티플레이(LAN)" 항목들을 참고하세요.

---

## 게임플레이 스펙

`SPEC.md`는 능력치 공식, 데미지 계산, 턴 진행, 포획/도망 확률, 월드 생성, LAN 프로토콜까지 게임 규칙의 기준이 되는 엔진 무관 문서입니다. 웹 빌드와 Unity 빌드 모두 이 문서와 정확히 일치해야 하며, 공유 "골든" 테스트로 이를 검증합니다.

---

## 텔레메트리

Unity 빌드는 선택적으로 설치/접속/진행상황 이벤트를 Google 스프레드시트로 보낼 수 있습니다(교실에서 학생들이 설치·플레이를 잘 하고 있는지 확인하는 용도). **기본값은 꺼짐**(엔드포인트가 설정돼 있지 않음)이며, 꺼져 있거나 네트워크가 없어도 게임 플레이에는 전혀 영향이 없습니다.

이 기능은 미성년자의 실명과 플레이 기록을 다룹니다 — 사용하기 전에 `telemetry/README.md`를 꼭 읽고, 대상 스프레드시트 접근 권한을 본인만으로 제한하세요.

---

## 기여하기

```bash
git checkout -b feature/amazing-feature
git commit -m "Add amazing feature"
git push origin feature/amazing-feature
```

이후 Pull Request를 열어 주세요. `web/`과 `unity/`는 항상 `SPEC.md`와 동작이 일치해야 하며, 게임플레이 규칙을 바꿀 땐 `unity/Assets/Tests/EditMode`의 테스트도 함께 추가/수정해 주세요.

---

<div align="center">

[Read in English →](README.md)

</div>
