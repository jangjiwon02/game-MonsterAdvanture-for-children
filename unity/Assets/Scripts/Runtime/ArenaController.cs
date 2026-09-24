using System.Collections;
using System.Collections.Generic;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static MonsterAdventure.Core.Korean;

namespace MonsterAdventure
{
    /// <summary>
    /// LAN 대결 마당(같은 충주 지도를 여럿이서 함께 걷고, 마주치면 도전). 단일 플레이 저장(PlayerState)과는
    /// 완전히 분리된 모드다 — 이 모드의 진행은 저장 파일에 손대지 않는다. 트레이너 이름별 영구 계정(서버 쪽 파일)과
    /// 접속 시 출석 보너스는 서버(TcpArenaServer)가 처리하고, 도전이 성사되면 1:1 대결(DuelController)로 이어진다.
    /// </summary>
    public sealed class ArenaController : MonoBehaviour
    {
        public const int DefaultPort = 7777;

        GameData _data;
        WorldMap _map;
        GameUi _ui;
        TcpArenaServer _hostedServer;
        TcpArenaClient _client;
        PlayerController _localPlayer;
        readonly Dictionary<int, ArenaAvatar> _avatars = new Dictionary<int, ArenaAvatar>();
        Transform _avatarRoot;

        int _localId = -1;
        string _localName;
        int _localStarter;
        PlayerState _localState;       // 서버가 Welcome 때 보내준 내 계정 사본(가방·파티·소지금) — 월드 메뉴/상점/센터가 이걸 직접 쓰고,
                                        // 바뀌면 SyncState() 로 서버에 되돌려 보낸다(이동처럼 클라이언트를 신뢰하는 모델).
        bool _busy;                    // 도전 응답 대기, 월드 메뉴/상점/센터 진행 중 등 — 이동·재도전을 잠근다
        int? _waitingOnChallengeTo;    // 우리가 도전을 건 상대(응답 대기 중)
        string _statusLine;            // 화면 아래에 계속 보여주는 짧은 안내
        WelcomeMessage _pendingWelcome; // Update() 가 큐에서 받아 두면 Lobby() 코루틴이 가져간다(소비자를 하나로 유지)
        DuelController _duel;          // 대결 중이면 non-null — Update() 의 이동·도전 입력을 잠그는 데도 쓴다

        /// <summary>
        /// 이미 있는 GameInput/GameUi(타이틀 화면이 쓰던 것)를 그대로 물려받는다 — 여기서 새로 만들면
        /// GameInput.Instance 싱글턴이 두 개가 되어 서로의 입력 큐를 가로채게 된다.
        /// </summary>
        public static GameObject Boot(GameData data, WorldMap map, GameUi ui)
        {
            var go = new GameObject("Arena");
            var c = go.AddComponent<ArenaController>();
            c._data = data; c._map = map; c._ui = ui;
            return go;
        }

        void Start()
        {
            // 창이 포커스를 잃어도 네트워크 메시지 처리(Update)가 멈추면 안 된다 — 방치하면 다른 사람에게는
            // 이 사람이 얼어붙은 것처럼 보인다. 프로젝트 설정에도 켜 두지만 여기서도 다시 보장한다.
            Application.runInBackground = true;
            _ui.Data = _data;

            var world = new GameObject("World").AddComponent<WorldView>();
            world.Build(_map);
            _avatarRoot = new GameObject("Avatars").transform;
            _avatarRoot.SetParent(transform, false);

            var cam = Camera.main;
            if (cam == null) { cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>(); cam.tag = "MainCamera"; }
            if (!cam.TryGetComponent<CameraRig>(out var rig)) rig = cam.gameObject.AddComponent<CameraRig>();

            Bgm.Play(BgmKind.World);
            StartCoroutine(Lobby(rig));
        }

        void OnDestroy()
        {
            _client?.Dispose();
            _hostedServer?.Dispose();
        }

        /* ---------------------------------- 접속 흐름 ---------------------------------- */

        IEnumerator Lobby(CameraRig rig)
        {
            yield return _ui.Choose(new[] { "서버 열기 (내가 호스트)", "서버 접속" },
                new MenuOptions { Rect = new Rect(UiKit.VirtualWidth / 2f - 90, 130, 180, 68) });
            bool hosting = _ui.Choice == 0;

            string enteredName = null;
            do
            {
                yield return _ui.EnterText("이름을 입력하세요 (트레이너 이름)", 10, n => enteredName = n?.Trim());
                if (string.IsNullOrEmpty(enteredName)) yield return _ui.Say("이름을 입력해야 한다!");
            } while (string.IsNullOrEmpty(enteredName));
            _localName = enteredName;

            // 이 이름으로 처음 접속하는 것이면 여기서 고른 파트너로 새 계정을 만든다. 이미 있는 트레이너라면
            // 서버가 이 선택을 무시하고 저장된 계정을 그대로 불러온다(그래도 매번 물어보는 건 약간 어색한 부분 —
            // 계정 존재 여부를 먼저 물어보는 2단계 접속으로 다음에 다듬을 수 있다).
            yield return _ui.Choose(new[] { "불꼬마", "물방울이", "새싹이" },
                new MenuOptions { Rect = new Rect(UiKit.VirtualWidth / 2f - 70, 130, 140, 68), Prompt = "(처음이라면) 파트너를 골라 주세요" });
            int starter = PlayerState.Starters[_ui.Choice];

            _localStarter = starter;
            string host = "127.0.0.1";
            if (hosting)
            {
                yield return StartServer();
            }
            else
            {
                var octets = new[] { 127, 0, 0, 1 };
                for (int i = 0; i < 4; i++)
                {
                    var items = new string[256];
                    for (int v = 0; v < 256; v++) items[v] = v.ToString();
                    yield return _ui.Choose(items, new MenuOptions
                    {
                        Rect = new Rect(160, 40, 160, 7 * 26 + 16), MaxRows = 7, Start = octets[i],
                        Prompt = $"서버 주소 {string.Join(".", octets)} — {i + 1}번째 자리를 고르세요",
                    });
                    octets[i] = _ui.Choice;
                }
                host = string.Join(".", octets);
            }

            yield return _ui.Say($"{host}:{DefaultPort} 에 접속하는 중...", 400);
            bool connected = TryConnect(host, out string failReason);
            if (!connected)
            {
                if (hosting) { _hostedServer?.Dispose(); _hostedServer = null; }
                yield return _ui.Say($"접속하지 못했다: {failReason}\n메뉴로 돌아간다.");
                StartCoroutine(Lobby(rig));
                yield break;
            }

            // Welcome 이 올 때까지 기다린다. 큐는 Update() 가 유일하게 비우고(Handle 에서 _pendingWelcome 에 담아 둔다),
            // 여기서는 그 결과만 폴링한다 — 두 곳에서 같은 큐를 직접 읽으면 서로 메시지를 가로채는 경합이 생긴다.
            _pendingWelcome = null;
            float timeout = Time.time + 5f;
            while (_pendingWelcome == null && Time.time < timeout) yield return null;
            var welcome = _pendingWelcome;
            _pendingWelcome = null;
            if (welcome == null)
            {
                yield return _ui.Say("서버 응답이 없다. 메뉴로 돌아간다.");
                _client.Dispose(); _hostedServer?.Dispose(); _hostedServer = null;
                StartCoroutine(Lobby(rig));
                yield break;
            }

            _localId = welcome.Id;
            _localPlayer = new GameObject("Player").AddComponent<PlayerController>();
            _localPlayer.Init(_map, welcome.X, welcome.Y);
            rig.Target = _localPlayer.transform;
            _localPlayer.Stepped += OnLocalStepped;

            Debug.Log($"[Arena] 입장 완료: id={_localId} name={_localName} pos=({welcome.X},{welcome.Y}) others={welcome.Others.Count}");
            foreach (var o in welcome.Others) SpawnAvatar(o);

            if (hosting)
            {
                string lan = LanAddress.TryGetLocalIPv4();
                _statusLine = lan != null ? $"다른 사람 접속 주소: {lan}:{DefaultPort}" : $"이 기기 LAN 주소를 찾지 못했다 (포트 {DefaultPort})";
            }
            _localState = !string.IsNullOrEmpty(welcome.StateJson) ? SaveSerializer.FromJson(_data, welcome.StateJson) : null;
            yield return _ui.Say($"{J(_localName, "은", "는")} {host}:{DefaultPort} 에 접속했다!\n소지금 ₩{welcome.Money}", 900);
            // "접속했는데 아무도 안 보인다"는 서로 다른 세션(호스트 IP를 잘못 입력 등)에 들어간 경우가 잦다 —
            // 지금 같은 세션에 몇 명이 있는지 바로 알려주면 그 경우를 즉시 구분할 수 있다.
            yield return _ui.Say(welcome.Others.Count == 0
                ? "지금 이 세션엔 나 혼자다. (다른 사람이 안 보이면 같은 서버에 접속했는지부터 확인!)"
                : $"지금 같은 세션에 {welcome.Others.Count}명이 더 있다. 걸어서 찾아가 보자!", 1100);
            if (welcome.BonusGranted)
                yield return _ui.Say($"오늘의 출석 보너스 ₩{DailyBonus.Amount}을(를) 받았다!", 900);
        }

        bool TryConnect(string host, out string reason)
        {
            reason = null;
            try
            {
                _client = new TcpArenaClient();
                _client.Connect(host, DefaultPort, _localName, _localStarter);
                return true;
            }
            catch (System.Exception e) { reason = e.Message; return false; }
        }

        IEnumerator StartServer()
        {
            var accounts = new TrainerAccountStore(System.IO.Path.Combine(Application.persistentDataPath, "trainer_accounts"));
            _hostedServer = new TcpArenaServer(_data, _map, accounts, DefaultPort);
            _hostedServer.Logged += msg => Debug.Log("[ArenaServer] " + msg);
            _hostedServer.Start();
            yield return null;
        }

        /* ---------------------------------- 진행 중 ---------------------------------- */

        void Update()
        {
            if (_client == null) return;
            while (_client.TryTakeReceived(out var type, out var d)) Handle(type, d);
            if (!_client.IsConnected && _localPlayer != null && string.IsNullOrEmpty(_statusLine))
                _statusLine = "서버 연결이 끊겼다.";

            if (_localPlayer == null || _ui.IsBusy || _busy || _duel != null) return;
            if (GameInput.Instance.TryDequeue(out var key))
            {
                if (key == GameKey.Ok)
                {
                    int? nearby = FindAdjacentAvatar();
                    if (nearby.HasValue) StartCoroutine(SendChallengeTo(nearby.Value));
                }
                else if (key == GameKey.Cancel)
                {
                    StartCoroutine(ArenaWorldMenu());
                }
            }
        }

        void Handle(string type, JObject d)
        {
            switch (type)
            {
                case NetMsgType.Welcome:
                    _pendingWelcome = d.ToObject<WelcomeMessage>();
                    break;
                case NetMsgType.Joined:
                {
                    var info = d["Info"].ToObject<TrainerInfo>();
                    Debug.Log($"[Arena] #{info.Id} {info.Name} 입장 at ({info.X},{info.Y})");
                    SpawnAvatar(info);
                    break;
                }
                case NetMsgType.Left:
                {
                    int id = d.ToObject<LeftMessage>().Id;
                    Debug.Log($"[Arena] #{id} 퇴장");
                    if (_avatars.TryGetValue(id, out var av)) { Destroy(av.gameObject); _avatars.Remove(id); }
                    break;
                }
                case NetMsgType.Moved:
                {
                    var m = d.ToObject<MovedMessage>();
                    if (m.Id == _localId)
                    {
                        // 서버 판정이 예측과 다르면(다른 플레이어와 동시에 같은 칸을 노린 경우 등) 위치를 맞춘다.
                        if (m.X != _localPlayer.TileX || m.Y != _localPlayer.TileY)
                            _localPlayer.Teleport(m.X, m.Y, (Direction)m.Dir);
                    }
                    else if (_avatars.TryGetValue(m.Id, out var av))
                    {
                        Debug.Log($"[Arena] #{m.Id} 이동 -> ({m.X},{m.Y})");
                        av.MoveTo(m.X, m.Y, (Direction)m.Dir);
                    }
                    break;
                }
                case NetMsgType.ChallengeOffer:
                {
                    var offer = d.ToObject<ChallengeOfferMessage>();
                    Debug.Log($"[Arena] #{offer.FromId} {offer.FromName} 이(가) 도전을 걸어옴");
                    StartCoroutine(RespondToChallenge(offer));
                    break;
                }
                case NetMsgType.ChallengeResult:
                {
                    var r = d.ToObject<ChallengeResultMessage>();
                    Debug.Log($"[Arena] 도전 결과: 상대#{r.OtherId} accepted={r.Accepted}");
                    _waitingOnChallengeTo = null;
                    StartCoroutine(ShowChallengeResult(r));
                    break;
                }
                case NetMsgType.Error:
                    StartCoroutine(_ui.Say(d.ToObject<ErrorMessage>().Reason));
                    break;
                case NetMsgType.DuelStart:
                {
                    var start = d.ToObject<DuelStartMessage>();
                    _localPlayer.Locked = true;
                    GameInput.Instance.ClearHeld();
                    Bgm.Play(BgmKind.Battle);
                    _duel = new DuelController(_ui, _data, _client, start);
                    StartCoroutine(RunDuel());
                    break;
                }
                case NetMsgType.DuelEvent:
                case NetMsgType.DuelEnded:
                    _duel?.Feed(type, d);
                    break;
            }
        }

        IEnumerator RunDuel()
        {
            yield return _duel.Run();
            _duel = null;
            _localPlayer.Locked = false;
            GameInput.Instance.ClearHeld();
            Bgm.Play(BgmKind.World);
        }

        void SpawnAvatar(TrainerInfo info)
        {
            var go = new GameObject($"Avatar #{info.Id} {info.Name}");
            go.transform.SetParent(_avatarRoot, false);
            var av = go.AddComponent<ArenaAvatar>();
            av.Init(info.Id, info.Name, info.X, info.Y, (Direction)info.Dir);
            _avatars[info.Id] = av;
        }

        void OnLocalStepped(int x, int y)
        {
            Debug.Log($"[Arena] 내 이동 -> ({x},{y})");
            _client.SendMove((NetDirection)_localPlayer.Facing);
            if (_busy || _localState == null) return;
            var tile = _map[x, y];
            if (tile == Tile.CenterDoor) StartCoroutine(ArenaHealScene());
            else if (tile == Tile.ShopDoor) StartCoroutine(ArenaShopScene());
        }

        int? FindAdjacentAvatar()
        {
            foreach (var kv in _avatars)
            {
                int d = System.Math.Abs(kv.Value.TileX - _localPlayer.TileX) + System.Math.Abs(kv.Value.TileY - _localPlayer.TileY);
                if (d == 1) return kv.Key;
            }
            return null;
        }

        IEnumerator SendChallengeTo(int targetId)
        {
            _busy = true;
            _waitingOnChallengeTo = targetId;
            string targetName = _avatars.TryGetValue(targetId, out var av) ? av.DisplayName : "상대";
            _client.SendChallenge(targetId);
            yield return _ui.Say($"{targetName}에게 도전을 신청했다. 응답을 기다리는 중...", 900);
            _busy = false;   // 실제 결과(ChallengeResult)는 이 뒤에 언제든 도착해서 따로 보여준다
        }

        IEnumerator RespondToChallenge(ChallengeOfferMessage offer)
        {
            _busy = true;
            yield return _ui.Choose(new[] { "수락", "거절" },
                new MenuOptions { Rect = new Rect(UiKit.VirtualWidth / 2f - 80, 160, 160, 68), Prompt = $"{offer.FromName}이(가) 대결을 신청했다!" });
            _client.SendChallengeResponse(_ui.Choice == 0);
            _busy = false;
        }

        IEnumerator ShowChallengeResult(ChallengeResultMessage r)
        {
            string otherName = _avatars.TryGetValue(r.OtherId, out var av) ? av.DisplayName : "상대";
            if (!r.Accepted) { yield return _ui.Say($"{otherName}이(가) 대결을 거절했다."); yield break; }
            yield return _ui.Say($"{otherName}(와)과 대결이 성사되었다! 준비하는 중...", 800);
        }

        /* ---------------------------------- 월드 메뉴(X) ---------------------------------- */
        // 싱글플레이 GameBootstrap 의 WorldMenu/PartyMenu/BagMenu 와 거의 같은 화면이지만, State 대신
        // 서버가 준 계정 사본(_localState)에 대해 동작하고, 저장은 파일이 아니라 SyncState() 로 서버에 보낸다.
        // 박스(Box)는 이번엔 범위에서 뺐다(계정 동기화가 아직 몬스터/가방/저장까지만).

        void BeginArenaScene() { _busy = true; _localPlayer.Locked = true; GameInput.Instance.ClearHeld(); }
        void EndArenaScene() { GameInput.Instance.ClearHeld(); _localPlayer.Locked = false; _busy = false; }

        void SyncState()
        {
            if (_localState == null) return;
            _client.SendUpdateAccount(SaveSerializer.ToJson(_localState));
        }

        IEnumerator ArenaWorldMenu()
        {
            if (_localState == null) { yield return _ui.Say("계정 정보를 아직 못 받았다."); yield break; }
            BeginArenaScene();
            for (;;)
            {
                var items = new[] { "몬스터", "가방", "도감", "저장", $"소리: {(Sfx.Muted ? "끔" : "켬")}", "닫기" };
                yield return _ui.Choose(items,
                    new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 146, 8, 138, items.Length * 26 + 16), Cancel = true });
                int i = _ui.Choice;
                if (i == -1 || i == 5) break;
                if (i == 0) yield return ArenaPartyMenu();
                else if (i == 1) yield return ArenaBagMenu();
                else if (i == 2) yield return _ui.DexScreen(_localState);
                else if (i == 3) { SyncState(); yield return _ui.Say("저장했다!", 500); }
                else { Sfx.Muted = !Sfx.Muted; Bgm.SetMuted(Sfx.Muted); }
            }
            EndArenaScene();
        }

        IEnumerator ArenaPartyMenu()
        {
            int start = 0;
            for (;;)
            {
                yield return _ui.ChooseParty(_localState.Party, new PartyScreenOptions { Start = start });
                int i = _ui.Choice;
                if (i < 0) yield break;
                start = i;
                yield return _ui.Choose(new[] { "상세보기", "맨 앞으로", "취소" },
                    new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 158, UiKit.VirtualHeight - 108, 150, 100), Cancel = true });
                int s = _ui.Choice;
                if (s == 0) yield return _ui.ShowSummary(_localState.Party[i]);
                else if (s == 1 && i > 0) { _localState.MoveToFront(i); start = 0; SyncState(); }
            }
        }

        IEnumerator ArenaBagMenu()
        {
            for (;;)
            {
                yield return _ui.Choose(new[] { $"몬스터볼 x{_localState.Balls}", $"상처약 x{_localState.Potions}", "닫기" },
                    new MenuOptions { W = 190, Y = 8, Cancel = true });
                int i = _ui.Choice;
                if (i == -1 || i == 2) yield break;
                if (i == 0) { yield return _ui.Say("몬스터볼은 야생 몬스터와의\n배틀에서 사용할 수 있다."); continue; }
                if (_localState.Potions <= 0) { yield return _ui.Say("상처약이 없다!"); continue; }
                yield return _ui.ChooseParty(_localState.Party, new PartyScreenOptions
                {
                    Title = "누구에게 사용할까요?",
                    Filter = (m, _) => m.Hp <= 0 ? "기절한 몬스터에게는 쓸 수 없다!" : m.Hp >= m.MaxHp ? "이미 HP가 가득 찼다!" : null,
                });
                if (_ui.Choice < 0) continue;
                var target = _localState.Party[_ui.Choice];
                _localState.Potions--;
                target.Heal(PlayerState.PotionHeal);
                SyncState();
                Sfx.Play(SfxKind.Heal);
                yield return _ui.Say($"{target.Species(_data).Name}의 HP가 회복되었다!", 600);
            }
        }

        /* ---------------------------------- 문 이벤트 ---------------------------------- */

        /// <summary>대결은 매번 HP 가득 채운 새 사본으로 붙기 때문에(BuildDuelMonster), 계정 파티 HP 자체는 대결로는
        /// 줄지 않는다 — 그래도 문 앞·화면 흐름은 싱글플레이와 똑같이 동작하게 만든다(나중에 대결 밖 전투가
        /// 생기면 그때 진짜로 의미가 생긴다).</summary>
        IEnumerator ArenaHealScene()
        {
            BeginArenaScene();
            yield return _ui.Say("어서 와요! 네잎클로버지역아동센터예요.\n몬스터들도 여기서 푹 쉬고 가요.");
            yield return GameUi.Tween(.3f, p => _ui.Fade = p);
            yield return GameUi.Wait(.7f);
            Sfx.Play(SfxKind.Heal);
            _localState.HealAll();
            SyncState();
            yield return GameUi.Tween(.3f, p => _ui.Fade = 1f - p);
            yield return _ui.Say("몬스터들이 모두 건강해졌어요!");
            _localPlayer.Teleport(_localPlayer.TileX, _localPlayer.TileY, Direction.Down);
            EndArenaScene();
        }

        IEnumerator ArenaShopScene()
        {
            BeginArenaScene();
            yield return _ui.Say("어서 오세요! 학생과학백화점입니다.");
            for (;;)
            {
                yield return _ui.Choose(
                    new[] { $"몬스터볼  ₩{Shop.BallPrice}  (보유 {_localState.Balls})", $"상처약  ₩{Shop.PotionPrice}  (보유 {_localState.Potions})", "나가기" },
                    new MenuOptions { Rect = new Rect(128, 8, 344, 100), Cancel = true, Full = true, Prompt = $"소지금 ₩{_localState.Money}\n무엇을 사시겠어요?" });
                int i = _ui.Choice;
                if (i == -1 || i == 2) break;
                var item = i == 0 ? ShopItem.Ball : ShopItem.Potion;
                if (!Shop.TryBuy(_localState, item)) { yield return _ui.Say("돈이 부족합니다!"); continue; }
                SyncState();
                Sfx.Play(SfxKind.Buy);
                yield return _ui.Say($"{(item == ShopItem.Ball ? "몬스터볼" : "상처약")}을(를) 샀다!", 500);
            }
            yield return _ui.Say("또 오세요!");
            _localPlayer.Teleport(_localPlayer.TileX, _localPlayer.TileY, Direction.Down);
            EndArenaScene();
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _localPlayer == null || _duel != null) return;
            UiKit.Begin();
            UiKit.Text($"₩ {(_localState?.Money ?? 0)}", 8, 8, 13, UiKit.C("#ffd84a"), bold: true);
            // 계속 보이게 해서 "접속했는데 아무도 없다"를 언제든 바로 알 수 있게 한다(0명이면 다른 서버에 들어간 것).
            UiKit.Text($"함께 접속 중: {_avatars.Count}명", 8, 24, 11, UiKit.C("#9be0ff"));
            if (!string.IsNullOrEmpty(_statusLine)) UiKit.Text(_statusLine, 8, UiKit.VirtualHeight - 20, 11, UiKit.C("#9be0ff"));
            int? nearby = !_ui.IsBusy && !_busy ? FindAdjacentAvatar() : null;
            if (nearby.HasValue && _avatars.TryGetValue(nearby.Value, out var av))
                UiKit.Text($"Z: {av.DisplayName}에게 도전", 8, UiKit.VirtualHeight - 36, 12, UiKit.C("#ffd84a"), bold: true);
        }
    }
}
