using System;
using System.Collections;
using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;
using static MonsterAdventure.Core.Korean;

namespace MonsterAdventure
{
    /// <summary>
    /// 씬에 이것 하나만 놓으면 게임이 조립된다: 데이터 로드 → (저장이 있으면 불러오기) → 월드/플레이어/카메라 → HUD.
    /// 걸음마다 문 이벤트(회복·상점)와 풀숲 조우를 처리하고, X/Z 로 월드 메뉴를 연다.
    /// (웹 버전의 startWorld / onStep / healScene / shopScene / openMenu 에 해당)
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Tooltip("개발용: 타이틀을 건너뛰고 저장이 있으면 이어서, 없으면 아래 파트너로 새 게임을 시작한다.")]
        [SerializeField] bool skipTitle;
        [Tooltip("skipTitle 일 때 새 게임의 시작 파트너: 0 불꼬마 / 2 물방울이 / 4 새싹이")]
        [SerializeField] int starterSpeciesId = 0;

        public GameData Data { get; private set; }
        public WorldMap Map { get; private set; }
        public PlayerState State { get; private set; }
        public PlayerController Player { get; private set; }
        public GameUi Ui { get; private set; }

        /// <summary>난수원. 테스트에서 굴림을 고정하려고 바꿀 수 있다.</summary>
        public IRng Rng { get; set; } = new SystemRng();

        /// <summary>전투·문 이벤트·메뉴처럼 월드 조작을 막는 흐름이 진행 중인지.</summary>
        public bool InScene { get; private set; }
        public bool InBattle { get; private set; }

        void Start()
        {
            var asset = Resources.Load<TextAsset>("game-data");
            if (asset == null) { Debug.LogError("Resources/game-data.json 을 찾을 수 없다."); enabled = false; return; }
            Data = GameData.Parse(asset.text);
            Map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(Map);

            gameObject.AddComponent<GameInput>();
            gameObject.AddComponent<TouchControls>();   // 모바일 터치 방향 패드 + Z/X — Arena 모드도 이 GameInput 을 공유하므로 자동으로 적용된다
            Ui = gameObject.AddComponent<GameUi>();
            Ui.Data = Data;

            StartCoroutine(Boot());
        }

        /// <summary>타이틀에서 시작 상태를 정한 뒤 월드를 세운다(웹의 startWorld: 검게 → 월드 → 밝게).</summary>
        IEnumerator Boot()
        {
            TelemetryClient.SendInstallIfFirstRun();

            if (skipTitle)
            {
                var loaded = SaveStore.TryLoad(Data);
                if (loaded != null) State = loaded;
                else
                {
                    State = PlayerState.NewGame(Data, starterSpeciesId);
                    State.Party[0].RollIndividualValues(Data);   // 새 파트너만 개체값을 굴린다(불러오기는 그대로)
                }
                yield return EnsurePlayerName();
                BuildWorld();
                BeginSession();
                yield break;
            }

            var title = new TitleController(Ui, Data);
            yield return title.Run();
            yield return GameUi.Tween(.3f, p => Ui.Fade = p);
            if (title.WantsArena)
            {
                // GameInput/GameUi 는 타이틀이 쓰던 것을 그대로 넘긴다 — 새로 만들면 GameInput 싱글턴이 충돌한다.
                Ui.DrawScene = null;
                ArenaController.Boot(Data, Map, Ui);
                yield return GameUi.Tween(.3f, p => Ui.Fade = 1f - p);
                yield break;
            }
            State = title.Result;
            yield return EnsurePlayerName();
            BuildWorld();
            BeginSession();
            yield return GameUi.Tween(.3f, p => Ui.Fade = 1f - p);
        }

        const string NotOnRosterLabel = "명단에 없음(직접 입력)";

        /// <summary>원격 진행상황 기록용 이름이 아직 없으면(예전 저장이거나 첫 실행) 명단에서 골라서 저장에 남긴다.
        /// 명단에 없는 제3자는 "직접 입력"을 골라 이름을 타이핑하되, 실제 학생 이름과 안 겹치도록 기기ID 일부를
        /// 붙여서 구분한다(스프레드시트의 최신현황 탭은 이름 하나당 한 줄만 유지하므로, 겹치면 서로 덮어써 버린다).</summary>
        IEnumerator EnsurePlayerName()
        {
            if (!string.IsNullOrEmpty(State.PlayerName)) yield break;

            var names = new List<string> { Roster.Teacher };
            names.AddRange(Roster.Students);
            names.Add(NotOnRosterLabel);
            yield return Ui.Say("진행상황 기록을 위해 명단에서 이름을 골라 주세요.");
            yield return Ui.Choose(names, new MenuOptions
            {
                Rect = new Rect(UiKit.VirtualWidth / 2f - 140, 70, 280, 7 * 26 + 16),
                Cols = 2, Full = true, Prompt = "누구인가요?",
            });
            int choice = Mathf.Max(0, Ui.Choice);

            if (names[choice] == NotOnRosterLabel)
            {
                string typed = null;
                yield return Ui.EnterText("이름을 입력하세요", 10, n => typed = n?.Trim());
                string tag = TelemetryClient.DeviceId.Substring(0, 6);
                State.PlayerName = string.IsNullOrEmpty(typed) ? $"손님({tag})" : $"{typed}({tag})";
            }
            else
            {
                State.PlayerName = names[choice];
            }
            SaveStore.Save(State);
        }

        const float HeartbeatSeconds = 60f;
        DateTime _sessionStartUtc;
        bool _sessionOpen;
        Coroutine _heartbeat;

        void BeginSession()
        {
            if (State == null || _sessionOpen) return;
            _sessionOpen = true;
            _sessionStartUtc = DateTime.UtcNow;
            TelemetryClient.SendSessionStart(State.PlayerName);
            SendProgressSnapshot();   // 첫 저장을 기다리지 않고 바로 '최신현황'에 이름이 올라오게 한다
            _heartbeat = StartCoroutine(HeartbeatLoop());
        }

        void EndSession()
        {
            if (State == null || !_sessionOpen) return;
            _sessionOpen = false;
            if (_heartbeat != null) { StopCoroutine(_heartbeat); _heartbeat = null; }
            double elapsed = (DateTime.UtcNow - _sessionStartUtc).TotalSeconds;
            if (elapsed <= 0) return;
            State.TotalPlaySeconds += elapsed;
            SaveStore.Save(State);
            TelemetryClient.SendSessionEnd(State.PlayerName, elapsed, State.TotalPlaySeconds);
        }

        /// <summary>플레이 중에는 1분마다 현재 상태를 보낸다. 앱이 백그라운드에서 강제 종료되면 session_end 가 못 나가는
        /// 경우가 있는데, 그래도 '마지막 접속'과 누적 플레이 시간이 최대 1분 오차로 남는다.</summary>
        IEnumerator HeartbeatLoop()
        {
            for (;;)
            {
                yield return new WaitForSecondsRealtime(HeartbeatSeconds);
                SendProgressSnapshot();
            }
        }

        void SendProgressSnapshot()
        {
            double total = State.TotalPlaySeconds + (_sessionOpen ? (DateTime.UtcNow - _sessionStartUtc).TotalSeconds : 0);
            TelemetryClient.SendProgress(State.PlayerName,
                State.Party.Count > 0 ? State.Party[0].Level : 0, State.Dex.Count, State.Money, total);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) EndSession(); else BeginSession();
        }

        void OnApplicationQuit() => EndSession();

        void BuildWorld()
        {
            var world = new GameObject("World").AddComponent<WorldView>();
            world.Build(Map);

            Player = new GameObject("Player").AddComponent<PlayerController>();
            Player.Init(Map, State.X, State.Y);
            Player.Stepped += OnStepped;

            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
                cam.tag = "MainCamera";
            }
            if (!cam.TryGetComponent<CameraRig>(out var rig)) rig = cam.gameObject.AddComponent<CameraRig>();
            rig.Target = Player.transform;
            _rig = rig;

            if (UseVisibleSpawns)
            {
                // 풀숲에서 눈에 보이는 야생 몬스터가 배회하고, 가까이 가면 카메라가 확대되며 전투가 시작된다.
                _spawner = new GameObject("Spawner").AddComponent<OverworldSpawner>();
                _spawner.Init(Map, Data, Player, Rng);
                _spawner.ContactGate = () => !InScene;
                _spawner.Contact += actor => StartCoroutine(EncounterRoutine(actor.Monster, actor));
            }

            Bgm.Play(BgmKind.World);
        }

        void Update()
        {
            // 월드에서 한가할 때 Z/X 를 누르면 메뉴가 열린다(웹: press() 의 world 분기).
            if (State == null || Player == null || InScene || Player.IsMoving) return;
            if (GameInput.Instance.TryDequeue(out var key) && (key == GameKey.Ok || key == GameKey.Cancel))
                StartCoroutine(WorldMenu());
        }

        /// <summary>true 면 필드 위에 보이는 야생 몬스터와 접촉해서 전투를 시작한다(OverworldSpawner).
        /// 기본값 false: 원래대로 풀숲을 지나다 한 칸마다 14% 로 우연히 마주친다(몬스터는 필드에 보이지 않는다).</summary>
        public static bool UseVisibleSpawns = false;

        OverworldSpawner _spawner;
        CameraRig _rig;

        void OnStepped(int x, int y)
        {
            State.X = x; State.Y = y;
            if (InScene) return;
            var tile = Map[x, y];
            if (tile == Tile.CenterDoor) StartCoroutine(HealScene());
            else if (tile == Tile.ShopDoor) StartCoroutine(ShopScene());
            else if (tile == Tile.SchoolDoor) StartCoroutine(SchoolQuizRoutine());
            // 눈에 보이는 스폰을 끄면(UseVisibleSpawns=false) 예전처럼 풀숲 한 칸 이동마다 14% 로 야생 몬스터가 나타난다.
            else if (!UseVisibleSpawns && WildEncounter.ShouldEncounter(tile, Rng))
            {
                var wild = WildEncounter.Generate(Data, x, y, Rng, water: tile == Tile.Water);
                wild.RollIndividualValues(Data);   // 야생 개체마다 실제로 개체값이 다르다(웹 골든 테스트와 무관한 별도 단계)
                StartCoroutine(EncounterRoutine(wild));
            }
        }

        void BeginScene() { InScene = true; Player.Locked = true; GameInput.Instance.ClearHeld(); }
        void EndScene() { GameInput.Instance.ClearHeld(); Player.Locked = false; InScene = false; }

        /* ---------------------------------- 전투 ---------------------------------- */

        /// <summary>야생 몬스터와의 전투 한 판. 끝나면 월드로 돌아와 조작이 다시 풀린다.</summary>
        public IEnumerator EncounterRoutine(Monster wild, WildActor actor = null)
        {
            BeginScene();
            InBattle = true;
            _spawner?.Pause();

            // 씬 전환 없이 그 자리에서 카메라가 확대된다(풀숲 조우면 플레이어 쪽으로, 보이는 몬스터와 접촉했으면 둘 사이로).
            if (_rig != null)
            {
                var focus = actor != null ? BattleCameraDirector.Midpoint(Player.WorldPosition, actor.WorldPosition) : Player.WorldPosition;
                yield return BattleCameraDirector.ZoomToEncounter(_rig, focus, actor != null ? .6f : .45f);
            }

            Bgm.Play(BgmKind.Battle);
            var battle = new BattleController(this, Ui, Data, State, Rng) { Weather = RollWeather() };
            yield return battle.Run(wild);          // 끝나면 화면이 검게 덮여 있다
            Bgm.Play(BgmKind.World);

            bool lost = battle.Outcome == BattleOutcome.Lose;
            if (lost)
            {
                State.ApplyDefeat();
                Player.Teleport(State.X, State.Y, Direction.Down);
            }
            // 화면이 검게 덮여 있는 동안 카메라를 원래대로 돌리고, 접촉한 몬스터가 있었으면 정리한다.
            if (_rig != null) yield return BattleCameraDirector.ZoomBack(_rig, .3f);
            if (actor != null && _spawner != null)
            {
                bool gone = battle.Outcome == BattleOutcome.Win || battle.Outcome == BattleOutcome.Caught;
                if (gone) _spawner.Despawn(actor); else _spawner.SetCooldown(actor, 4f);   // 도망·패배면 잠시 뒤에야 다시 접촉
            }
            InBattle = false;
            yield return GameUi.Tween(.3f, p => Ui.Fade = 1f - p);
            if (lost) yield return Ui.Say("당신은 네잎클로버지역아동센터에서 눈을 떴다...\n소지금이 절반으로 줄었다.");
            EndScene();
            _spawner?.Resume();
        }

        /// <summary>야생 전투에 날씨가 낄 확률(0 이면 끔). 맑음·비·모래바람 중 하나가 같은 확률로 뽑힌다.</summary>
        public const double WeatherChance = 0.2;

        WeatherKind RollWeather()
        {
            if (Rng.NextDouble() >= WeatherChance) return WeatherKind.None;
            return (WeatherKind)(1 + Rng.Range(0, 2));
        }

        /* ---------------------------------- 문 이벤트 ---------------------------------- */

        /// <summary>네잎클로버지역아동센터: 전체 회복 + 자동 저장.</summary>
        public IEnumerator HealScene()
        {
            BeginScene();
            yield return Ui.Say("어서 와요! 네잎클로버지역아동센터예요.\n몬스터들도 여기서 푹 쉬고 가요.");
            yield return GameUi.Tween(.3f, p => Ui.Fade = p);
            yield return GameUi.Wait(.7f);
            Sfx.Play(SfxKind.Heal);
            State.HealAll();
            SaveStore.Save(State);
            yield return GameUi.Tween(.3f, p => Ui.Fade = 1f - p);
            yield return Ui.Say("몬스터들이 모두 건강해졌어요!\n(자동 저장되었습니다)");
            Player.Teleport(Player.TileX, Player.TileY, Direction.Down);
            EndScene();
        }

        /// <summary>학생과학백화점: 몬스터볼 ₩100, 상처약 ₩80.</summary>
        public IEnumerator ShopScene()
        {
            BeginScene();
            yield return Ui.Say("어서 오세요! 학생과학백화점입니다.");
            for (;;)
            {
                yield return Ui.Choose(
                    new[] { $"몬스터볼  ₩{Shop.BallPrice}  (보유 {State.Balls})", $"상처약  ₩{Shop.PotionPrice}  (보유 {State.Potions})", "나가기" },
                    new MenuOptions { Rect = new Rect(128, 8, 344, 100), Cancel = true, Full = true, Prompt = $"소지금 ₩{State.Money}\n무엇을 사시겠어요?" });
                int i = Ui.Choice;
                if (i == -1 || i == 2) break;
                var item = i == 0 ? ShopItem.Ball : ShopItem.Potion;
                if (!Shop.TryBuy(State, item)) { yield return Ui.Say("돈이 부족합니다!"); continue; }
                Sfx.Play(SfxKind.Buy);
                yield return Ui.Say($"{(item == ShopItem.Ball ? "몬스터볼" : "상처약")}을(를) 샀다!", 500);
            }
            yield return Ui.Say("또 오세요!");
            Player.Teleport(Player.TileX, Player.TileY, Direction.Down);
            EndScene();
        }

        /// <summary>남산초등학교: 수학 퀴즈. 정답이면 돈, 오답이면 잠시 뒤 재도전, 하루 횟수 제한(SchoolQuizScene 이 판정).</summary>
        public IEnumerator SchoolQuizRoutine()
        {
            BeginScene();
            yield return SchoolQuizScene.Visit(Ui, State, Rng, () => System.DateTime.UtcNow, () => SaveStore.Save(State));
            Player.Teleport(Player.TileX, Player.TileY, Direction.Down);
            EndScene();
        }

        /* ---------------------------------- 월드 메뉴 ---------------------------------- */

        IEnumerator WorldMenu()
        {
            BeginScene();
            for (;;)
            {
                var items = new[] { "몬스터", "가방", "박스", "도감", "저장", $"소리: {(Sfx.Muted ? "끔" : "켬")}", "게임 종료", "닫기" };
                yield return Ui.Choose(items,
                    new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 146, 8, 138, items.Length * 26 + 16), Cancel = true });
                int i = Ui.Choice;
                if (i == -1 || i == 7) break;
                if (i == 0) yield return PartyMenu();
                else if (i == 1) yield return BagMenu();
                else if (i == 2) yield return BoxMenu();
                else if (i == 3) yield return Ui.DexScreen(State);
                else if (i == 4) yield return Ui.Say(SaveStore.Save(State) ? "저장했다!" : "저장에 실패했다...", 500);
                else if (i == 5) { Sfx.Muted = !Sfx.Muted; Bgm.SetMuted(Sfx.Muted); }
                else yield return Ui.ConfirmQuit(() => { EndSession(); });   // 접속 기록(세션 종료)을 먼저 내보낸 뒤 끈다
            }
            EndScene();
        }

        /// <summary>몬스터 목록 → 상세보기 / 맨 앞으로(선두 교체).</summary>
        IEnumerator PartyMenu()
        {
            int start = 0;
            for (;;)
            {
                yield return Ui.ChooseParty(State.Party, new PartyScreenOptions { Start = start });
                int i = Ui.Choice;
                if (i < 0) yield break;
                start = i;
                yield return Ui.Choose(new[] { "상세보기", "맨 앞으로", "취소" },
                    new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 158, UiKit.VirtualHeight - 108, 150, 100), Cancel = true });
                int s = Ui.Choice;
                if (s == 0) yield return Ui.ShowSummary(State.Party[i]);
                else if (s == 1 && i > 0) { State.MoveToFront(i); start = 0; }
            }
        }

        /// <summary>박스: 꺼내기(파티가 가득 차면 맞바꾸기) / 맡기기.</summary>
        IEnumerator BoxMenu()
        {
            for (;;)
            {
                yield return Ui.Choose(new[] { "꺼내기", "맡기기", "닫기" },
                    new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 158, 8, 150, 100), Cancel = true, Prompt = $"박스: {State.Box.Count}마리" });
                int a = Ui.Choice;
                if (a == -1 || a == 2) yield break;

                if (a == 0)
                {
                    if (State.Box.Count == 0) { yield return Ui.Say("박스가 비어 있다."); continue; }
                    var labels = State.Box.ConvertAll(m => $"Lv.{m.Level} {m.Species(Data).Name}");
                    yield return Ui.Choose(labels, new MenuOptions { Rect = new Rect(120, 8, 240, 208), MaxRows = 7, Cancel = true });
                    int j = Ui.Choice;
                    if (j < 0) continue;
                    var picked = State.Box[j];
                    if (BoxOps.Withdraw(State, j))
                    {
                        yield return Ui.Say($"{J(picked.Species(Data).Name, "을", "를")} 파티에 넣었다!", 600);
                        continue;
                    }
                    yield return Ui.ChooseParty(State.Party, new PartyScreenOptions { Title = "교체할 몬스터를 고르세요" });
                    int k = Ui.Choice;
                    if (k < 0) continue;
                    var sent = State.Party[k];
                    BoxOps.Swap(State, j, k);
                    yield return Ui.Say($"{J(picked.Species(Data).Name, "을", "를")} 파티에 넣고\n{J(sent.Species(Data).Name, "을", "를")} 박스로 보냈다!", 600);
                }
                else
                {
                    string last = BoxOps.WhyNotDeposit(State, 0);
                    if (State.Party.Count <= 1) { yield return Ui.Say(last); continue; }
                    yield return Ui.ChooseParty(State.Party, new PartyScreenOptions
                    {
                        Title = "누구를 맡길까요?",
                        Filter = (m, i) => BoxOps.WhyNotDeposit(State, i),
                    });
                    int k = Ui.Choice;
                    if (k < 0) continue;
                    var m2 = State.Party[k];
                    BoxOps.Deposit(State, k);
                    yield return Ui.Say($"{J(m2.Species(Data).Name, "을", "를")} 박스에 맡겼다.", 600);
                }
            }
        }

        IEnumerator BagMenu()
        {
            for (;;)
            {
                yield return Ui.Choose(new[] { $"몬스터볼 x{State.Balls}", $"상처약 x{State.Potions}", "닫기" },
                    new MenuOptions { W = 190, Y = 8, Cancel = true });
                int i = Ui.Choice;
                if (i == -1 || i == 2) yield break;
                if (i == 0) { yield return Ui.Say("몬스터볼은 야생 몬스터와의\n배틀에서 사용할 수 있다."); continue; }
                if (State.Potions <= 0) { yield return Ui.Say("상처약이 없다!"); continue; }
                yield return Ui.ChooseParty(State.Party, new PartyScreenOptions
                {
                    Title = "누구에게 사용할까요?",
                    Filter = (m, _) => m.Hp <= 0 ? "기절한 몬스터에게는 쓸 수 없다!" : m.Hp >= m.MaxHp ? "이미 HP가 가득 찼다!" : null,
                });
                if (Ui.Choice < 0) continue;
                var target = State.Party[Ui.Choice];
                State.Potions--;
                target.Heal(PlayerState.PotionHeal);
                Sfx.Play(SfxKind.Heal);
                yield return Ui.Say($"{target.Species(Data).Name}의 HP가 회복되었다!", 600);
            }
        }

        /* ---------------------------------- HUD ---------------------------------- */

        void OnGUI()
        {
            // 전투·타이틀 화면이나 파티/상세 같은 전체 화면이 떠 있는 동안에는 월드 HUD 를 그리지 않는다.
            if (Event.current.type != EventType.Repaint || State == null || Ui.DrawScene != null || Ui.OverlayActive) return;
            UiKit.Begin();
            DrawHud();
        }

        void DrawHud()
        {
            UiKit.Box(6, 6, 132, 50, UiKit.C("#1b2340", .92f));
            UiKit.Text(WorldMap.DisplayAreaName(Player.TileX, Player.TileY), 14, 10, 12, UiKit.C("#9be0ff"), bold: true);
            UiKit.Text($"₩ {State.Money}", 14, 26, 13, UiKit.C("#ffd84a"), bold: true);
            UiKit.Text($"도감 {State.Dex.Count}/{Data.Species.Count}", 14, 41, 11, UiKit.C("#cfd6f5"));
        }
    }
}
