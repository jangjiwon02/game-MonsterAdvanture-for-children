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
    /// LAN 대결 마당(같은 충주 지도를 여럿이서 함께 걷고, 마주치면 도전). 일반 게임과 같은 저장(SaveStore)·같은 이름을 쓴다 —
    /// 혼자 키운 몬스터·레벨 그대로 들어와 겨루고, 대결 성장과 상점·가방 변화는 다시 저장에 돌아온다. 접속 시 출석 보너스는
    /// 서버(TcpArenaServer)가 처리하고, 도전이 성사되면 1:1 대결로 이어진다.
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

        // 원격 접속 기록(TelemetryClient). 일반 게임과 같은 이름(= 같은 사람)으로 같은 줄에 이어서 올라간다.
        const float HeartbeatSeconds = 60f;
        string TelemetryName => _localName;
        System.DateTime _sessionStartUtc;
        bool _sessionOpen;
        Coroutine _heartbeat;

        void BeginArenaSession()
        {
            if (_localState == null || _sessionOpen) return;
            _sessionOpen = true;
            _sessionStartUtc = System.DateTime.UtcNow;
            TelemetryClient.SendSessionStart(TelemetryName);
            SendArenaProgress();
            _heartbeat = StartCoroutine(ArenaHeartbeat());
        }

        void EndArenaSession()
        {
            if (_localState == null || !_sessionOpen) return;
            _sessionOpen = false;
            if (_heartbeat != null) { StopCoroutine(_heartbeat); _heartbeat = null; }
            double elapsed = (System.DateTime.UtcNow - _sessionStartUtc).TotalSeconds;
            if (elapsed <= 0) return;
            _localState.TotalPlaySeconds += elapsed;
            TelemetryClient.SendSessionEnd(TelemetryName, elapsed, _localState.TotalPlaySeconds);
            SyncState();
        }

        IEnumerator ArenaHeartbeat()
        {
            for (;;)
            {
                yield return new WaitForSecondsRealtime(HeartbeatSeconds);
                SendArenaProgress();
            }
        }

        void SendArenaProgress()
        {
            if (_localState == null) return;
            double total = _localState.TotalPlaySeconds + (_sessionOpen ? (System.DateTime.UtcNow - _sessionStartUtc).TotalSeconds : 0);
            TelemetryClient.SendProgress(TelemetryName,
                _localState.Party.Count > 0 ? _localState.Party[0].Level : 0, _localState.Dex.Count, _localState.Money, total);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) EndArenaSession(); else BeginArenaSession();
        }

        void OnApplicationQuit() => EndArenaSession();

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
            DisposeHosting();
        }

        /* ---------------------------------- 접속 흐름 ---------------------------------- */

        IEnumerator Lobby(CameraRig rig)
        {
            // 최신 로컬 대결 방식: 호스트는 "방 만들기", 상대는 "방 찾기"로 같은 Wi-Fi 안의 방 목록에서 골라 들어간다.
            // 주소를 한 자리씩 손으로 고르는 방식은 "주소로 접속(고급)" 으로만 남겨 둔다(검색이 막힌 공유기용).
            yield return _ui.Choose(new[] { "방 만들기 (내가 호스트)", "방 찾기 (자동 검색)", "주소로 접속 (고급)" },
                new MenuOptions { Rect = new Rect(UiKit.VirtualWidth / 2f - 100, 124, 200, 3 * 26 + 16), Prompt = "같은 Wi-Fi의 친구와 함께하기" });
            int mode = _ui.Choice;          // 0 방 만들기, 1 방 찾기, 2 주소로 접속
            bool hosting = mode == 0;

            // 일반 게임과 같은 저장을 이어 쓴다. 저장이 없으면(LAN부터 시작한 사람) 여기서 새로 시작해 저장해 둔다.
            var save = SaveStore.TryLoad(_data);
            if (save == null) yield return NewSave(state => save = state);
            if (string.IsNullOrEmpty(save.PlayerName))
            {
                yield return PlayerIdentity.EnsureName(_ui, save);
                SaveStore.Save(save);
            }
            _localName = save.PlayerName;
            _localStarter = save.Party[0].SpeciesId;
            _localSaveJson = SaveSerializer.ToJson(save);

            string host = "127.0.0.1";
            int port = DefaultPort;
            if (hosting)
            {
                yield return StartServer();
                if (_hostedServer == null)
                {
                    yield return _ui.Say($"방을 만들지 못했다: {_hostFailReason}\n앱을 완전히 종료했다가 다시 켜 보세요.\n메뉴로 돌아간다.");
                    StartCoroutine(Lobby(rig));
                    yield break;
                }
                port = _hostedServer.Port;
                string hostLan = LanAddress.TryGetLocalIPv4();
                yield return _ui.Say(hostLan != null
                    ? $"'{_hostedServer.RoomName}' 방을 열었다!\n친구는 [방 찾기]에서 이 방을 고르면 돼요.\n(직접 입력할 땐 주소: {hostLan})"
                    : $"'{_hostedServer.RoomName}' 방을 열었다!\n이 기기의 Wi-Fi 주소를 찾지 못했지만 [방 찾기]로는 보일 수 있어요.\n같은 Wi-Fi에 연결돼 있는지 확인해 주세요.");
            }
            else if (mode == 1)
            {
                string pickedHost = null; int pickedPort = 0; bool wantsManual = false;
                yield return DiscoverRoom((h, p) => { pickedHost = h; pickedPort = p; }, () => wantsManual = true);
                if (wantsManual) { yield return PickAddressManually(h => pickedHost = h); pickedPort = DefaultPort; }
                if (pickedHost == null) { StartCoroutine(Lobby(rig)); yield break; }   // "뒤로"
                host = pickedHost; port = pickedPort;
            }
            else
            {
                string manualHost = null;
                yield return PickAddressManually(h => manualHost = h);
                host = manualHost ?? host;
            }

            bool loopbackByMistake = !hosting && host.StartsWith("127.");
            if (loopbackByMistake)
                yield return _ui.Say("주의: 127.x.x.x 는 '이 기기 자신'이라 다른 폰의 서버에 접속할 수 없어요.\n호스트 폰에 표시된 주소(예: 192.168.0.12)를 입력해야 해요.", 2200);
            yield return _ui.Say($"{host}:{port} 에 접속하는 중...", 400);
            bool connected = TryConnect(host, port, out string failReason);
            if (!connected)
            {
                DisposeHosting();
                string hint = loopbackByMistake ? "\n(127.x 는 이 기기 자신이에요. 호스트 폰 주소를 입력해 보세요)" : "";
                yield return _ui.Say($"접속하지 못했다: {failReason}{hint}\n메뉴로 돌아간다.");
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
                _client.Dispose(); DisposeHosting();
                StartCoroutine(Lobby(rig));
                yield break;
            }

            _localId = welcome.Id;
            _localPlayer = new GameObject("Player").AddComponent<PlayerController>();
            _localPlayer.Init(_map, welcome.X, welcome.Y, welcome.Color);
            rig.Target = _localPlayer.transform;
            _localPlayer.Stepped += OnLocalStepped;

            Debug.Log($"[Arena] 입장 완료: id={_localId} name={_localName} pos=({welcome.X},{welcome.Y}) others={welcome.Others.Count}");
            foreach (var o in welcome.Others) SpawnAvatar(o);

            if (hosting)
            {
                string lan = LanAddress.TryGetLocalIPv4();
                _statusLine = lan != null ? $"방 '{_hostedServer.RoomName}' 공개 중 · 주소 {lan}" : $"방 '{_hostedServer.RoomName}' 공개 중";
            }
            _localState = !string.IsNullOrEmpty(welcome.StateJson) ? SaveSerializer.FromJson(_data, welcome.StateJson) : null;
            if (_localState != null) SaveStore.Save(_localState);   // 출석 보너스가 반영된 계정을 일반 게임 저장에도 남긴다
            BeginArenaSession();
            yield return _ui.Say($"{J(_localName, "은", "는")} 대결 마당에 입장했다!\n소지금 ₩{welcome.Money}", 900);
            yield return _ui.Say(welcome.Others.Count == 0
                ? "지금 이 방엔 나 혼자다. 친구가 [방 찾기]로 들어오면 X 메뉴의 '대결 신청'으로 겨뤄 보자!"
                : $"지금 같은 방에 {welcome.Others.Count}명이 더 있다. X 메뉴의 '대결 신청'으로 겨뤄 보자!", 1400);
            if (welcome.BonusGranted)
                yield return _ui.Say($"오늘의 출석 보너스 ₩{DailyBonus.Amount}을(를) 받았다!", 900);
        }

        /// <summary>같은 Wi-Fi 안의 방을 자동으로 찾아 목록으로 보여 준다. 고르면 onPick, '주소 직접 입력'이면 onManual,
        /// '뒤로'면 아무것도 부르지 않고 끝난다.</summary>
        IEnumerator DiscoverRoom(System.Action<string, int> onPick, System.Action onManual)
        {
            var scanner = new LanScanner();
            try { scanner.StartListening(); }
            catch (System.Exception e) { Debug.LogWarning("[Arena] UDP 방 검색을 못 열었다(직접 두드려 찾는 방식은 계속 동작): " + e.Message); }

            for (;;)
            {
                scanner.StartSweep();
                yield return _ui.Say("근처 방을 찾는 중...\n(호스트와 같은 Wi-Fi에 연결돼 있어야 해요)", 1800);
                float until = Time.time + 2.5f;
                while (scanner.SweepRunning && Time.time < until) yield return null;

                var rooms = scanner.Rooms(System.TimeSpan.FromSeconds(8));
                var labels = new List<string>();
                foreach (var r in rooms) labels.Add($"{r.Name}  ({r.Players}명)  {r.Address}");
                labels.Add("다시 찾기"); labels.Add("주소 직접 입력"); labels.Add("뒤로");

                yield return _ui.Choose(labels, new MenuOptions
                {
                    Rect = new Rect(UiKit.VirtualWidth / 2f - 170, 36, 340, Mathf.Min(labels.Count, 7) * 26 + 16), MaxRows = 7, Full = true,
                    Prompt = rooms.Count > 0
                        ? "들어갈 방을 고르세요"
                        : "근처에서 방을 찾지 못했어요.\n호스트가 [방 만들기]를 했는지, 같은 Wi-Fi인지 확인하세요.\n(공유기가 기기끼리 통신을 막으면 한 폰의 핫스팟을 쓰세요)",
                });
                int c = _ui.Choice;
                if (c >= 0 && c < rooms.Count) { onPick(rooms[c].Address, rooms[c].Port); break; }
                if (c == rooms.Count) continue;                       // 다시 찾기
                if (c == rooms.Count + 1) { onManual(); break; }      // 주소 직접 입력
                break;                                                // 뒤로
            }
            scanner.Dispose();
        }

        /// <summary>(고급) 주소를 네 자리로 나눠 하나씩 고른다. 기본값은 이 기기와 같은 대역.</summary>
        IEnumerator PickAddressManually(System.Action<string> onPick)
        {
            // 같은 Wi-Fi라면 주소의 앞 세 자리(예: 192.168.0)는 호스트와 같다 — 이 기기의 주소를 기본값으로 깔아 두면
            // 마지막 한 자리만 고르면 된다. (127.0.0.1 은 "이 기기 자신"이라 다른 기기를 가리킬 수 없다.)
            var octets = new[] { 192, 168, 0, 1 };
            var ownLan = LanAddress.TryGetLocalIPv4();
            if (ownLan != null)
            {
                var parts = ownLan.Split('.');
                if (parts.Length == 4 && int.TryParse(parts[0], out int a0) && int.TryParse(parts[1], out int a1) && int.TryParse(parts[2], out int a2))
                    octets = new[] { a0, a1, a2, 1 };
            }
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
            onPick(string.Join(".", octets));
        }

        string _localSaveJson;   // 접속할 때 서버에 보낼 내 저장(일반 게임에서 키운 그대로)

        /// <summary>저장이 하나도 없을 때의 간단한 새 시작: 파트너만 고른다(소개 대사는 일반 게임의 새 게임에서).</summary>
        IEnumerator NewSave(System.Action<PlayerState> onDone)
        {
            yield return _ui.Choose(new[] { "불꼬마", "물방울이", "새싹이" },
                new MenuOptions { Rect = new Rect(UiKit.VirtualWidth / 2f - 70, 130, 140, 68), Prompt = "저장된 게임이 없어요.\n함께할 파트너를 골라 주세요" });
            var state = PlayerState.NewGame(_data, PlayerState.Starters[_ui.Choice]);
            state.Party[0].RollIndividualValues(_data);
            onDone(state);
        }

        bool TryConnect(string host, int port, out string reason)
        {
            reason = null;
            try
            {
                _client = new TcpArenaClient();
                _client.Connect(host, port, _localName, _localStarter, 4000, _localSaveJson);
                return true;
            }
            catch (System.Exception e) { reason = ExplainConnectFailure(e); return false; }
        }

        /// <summary>소켓 예외 문구를 "그래서 뭘 하면 되는지"가 보이는 말로 바꾼다.</summary>
        static string ExplainConnectFailure(System.Exception e)
        {
            if (e is System.TimeoutException)
                return "응답이 없다(시간 초과).\n같은 Wi-Fi가 아니거나, 공유기가 기기끼리 통신을 막고 있을 수 있어요.\n한 폰의 핫스팟을 켜고 다른 폰이 거기에 연결해 보세요.";
            if (e is System.Net.Sockets.SocketException se && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
                return "방이 열려 있지 않아요(호스트가 방을 닫았을 수 있어요).";
            return e.Message;
        }

        string _hostFailReason;
        LanBeacon _beacon;

        IEnumerator StartServer()
        {
            _hostFailReason = null;
            var accounts = new TrainerAccountStore(System.IO.Path.Combine(Application.persistentDataPath, "trainer_accounts"));
            try
            {
                _hostedServer = new TcpArenaServer(_data, _map, accounts, DefaultPort) { RoomName = $"{_localName}의 방" };
                _hostedServer.Logged += msg => Debug.Log("[ArenaServer] " + msg);
                _hostedServer.Start();
            }
            catch (System.Exception e)
            {
                // 예전엔 여기서 예외가 나면(포트가 아직 쓰이는 중 등) 코루틴이 조용히 죽어서 화면이 멈춘 것처럼 보였다.
                _hostFailReason = e is System.Net.Sockets.SocketException ? "이 기기의 7777 포트를 쓸 수 없다" : e.Message;
                _hostedServer?.Dispose(); _hostedServer = null;
                yield break;
            }
            try
            {
                _beacon = new LanBeacon(() => _hostedServer.RoomName, _hostedServer.Port, () => _hostedServer.PlayerCount);
                _beacon.Start();
            }
            catch (System.Exception e) { Debug.LogWarning("[Arena] 방 알림(UDP)을 못 열었다 — 직접 검색은 계속 된다: " + e.Message); _beacon = null; }
            yield return null;
        }

        void DisposeHosting()
        {
            _beacon?.Dispose(); _beacon = null;
            _hostedServer?.Dispose(); _hostedServer = null;
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
                        // 서버는 내가 보낸 이동마다 결과를 하나씩 돌려준다. 걸어가는 중에는 클라이언트가 이미 다음 칸을
                        // 예측해서 서버 답보다 앞서 있으므로, 도착한 답이 "지금 위치"와 다르다고 바로 되돌리면 한 칸씩
                        // 끌려가는 렉이 생긴다. 보낸 이동에 대한 답을 전부 받고 멈춰 있을 때만, 서버 판정과 다르면
                        // (다른 플레이어와 같은 칸을 노린 경우 등) 위치를 맞춘다.
                        if (_movesInFlight > 0) _movesInFlight--;
                        if (_movesInFlight == 0 && !_localPlayer.IsMoving && (m.X != _localPlayer.TileX || m.Y != _localPlayer.TileY))
                            _localPlayer.Teleport(m.X, m.Y, (Direction)m.Dir);
                    }
                    else if (_avatars.TryGetValue(m.Id, out var av))
                    {
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
                case NetMsgType.AccountUpdated:
                {
                    // 대결 성장(경험치·레벨업·진화)·상금이 서버에서 계산돼 온다 — 내 계정에 반영하고 일반 게임 저장에도 남긴다.
                    var updated = SaveSerializer.FromJson(_data, d.ToObject<AccountUpdatedMessage>().StateJson);
                    if (updated != null) { _localState = updated; SaveStore.Save(_localState); }
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
            av.Init(info.Id, info.Name, info.X, info.Y, (Direction)info.Dir, info.Color);
            _avatars[info.Id] = av;
        }

        int _movesInFlight;   // 서버에 보냈지만 아직 답(Moved)을 못 받은 내 이동 수

        void OnLocalStepped(int x, int y)
        {
            _movesInFlight++;
            _client.SendMove((NetDirection)_localPlayer.Facing);
            if (_busy || _localState == null) return;
            var tile = _map[x, y];
            if (tile == Tile.CenterDoor) StartCoroutine(ArenaHealScene());
            else if (tile == Tile.ShopDoor) StartCoroutine(ArenaShopScene());
            else if (tile == Tile.SchoolDoor) StartCoroutine(ArenaSchoolQuiz());
        }

        /// <summary>남산초 수학 퀴즈. 돈·시도 기록이 바뀔 때마다 SyncState 로 서버 계정에 되돌려 보낸다.</summary>
        IEnumerator ArenaSchoolQuiz()
        {
            BeginArenaScene();
            yield return SchoolQuizScene.Visit(_ui, _localState, new SystemRng(), () => System.DateTime.UtcNow, SyncState);
            _localPlayer.Teleport(_localPlayer.TileX, _localPlayer.TileY, Direction.Down);
            EndArenaScene();
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
            SaveStore.Save(_localState);
            _client.SendUpdateAccount(SaveSerializer.ToJson(_localState));
        }

        IEnumerator ArenaWorldMenu()
        {
            if (_localState == null) { yield return _ui.Say("계정 정보를 아직 못 받았다."); yield break; }
            BeginArenaScene();
            for (;;)
            {
                var items = new[] { "대결 신청", "몬스터", "가방", "도감", "저장", $"소리: {(Sfx.Muted ? "끔" : "켬")}", "게임 종료", "닫기" };
                yield return _ui.Choose(items,
                    new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 146, 8, 138, items.Length * 26 + 16), Cancel = true });
                int i = _ui.Choice;
                if (i == -1 || i == 7) break;
                if (i == 0) { yield return ArenaChallengeMenu(); if (_challengeSentFromMenu) break; }
                else if (i == 1) yield return ArenaPartyMenu();
                else if (i == 2) yield return ArenaBagMenu();
                else if (i == 3) yield return _ui.DexScreen(_localState);
                else if (i == 4) { SyncState(); yield return _ui.Say("저장했다!", 500); }
                else if (i == 5) { Sfx.Muted = !Sfx.Muted; Bgm.SetMuted(Sfx.Muted); }
                else yield return _ui.ConfirmQuit(() => { EndArenaSession(); });
            }
            EndArenaScene();
        }

        bool _challengeSentFromMenu;

        /// <summary>같은 방의 사람 목록에서 골라 대결을 신청한다(바로 옆까지 걸어가지 않아도 된다).
        /// 신청을 보냈으면 _challengeSentFromMenu 가 true — 호출한 메뉴가 닫혀서 상대 응답을 받을 수 있게 한다.</summary>
        IEnumerator ArenaChallengeMenu()
        {
            _challengeSentFromMenu = false;
            if (_avatars.Count == 0)
            {
                yield return _ui.Say("지금 이 방엔 나 혼자다.\n친구가 [방 찾기]로 들어오면 여기서 대결을 신청할 수 있어요.");
                yield break;
            }
            var ids = new List<int>(_avatars.Keys);
            ids.Sort();
            var labels = new List<string>();
            foreach (int id in ids) labels.Add(_avatars[id].DisplayName);
            labels.Add("취소");
            yield return _ui.Choose(labels, new MenuOptions
            {
                Rect = new Rect(UiKit.VirtualWidth / 2f - 90, 40, 180, Mathf.Min(labels.Count, 7) * 26 + 16), MaxRows = 7, Cancel = true,
                Prompt = "누구에게 대결을 신청할까요?",
            });
            int c = _ui.Choice;
            if (c < 0 || c >= ids.Count) yield break;
            _challengeSentFromMenu = true;
            yield return SendChallengeTo(ids[c]);
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
