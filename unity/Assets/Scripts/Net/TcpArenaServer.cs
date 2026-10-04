using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;

namespace MonsterAdventure.Net
{
    /// <summary>
    /// LAN 위 「대결 마당」서버. ArenaState 로 이동·도전을 판정하고, 트레이너 계정(TrainerAccountStore)으로
    /// 이름별 영구 저장과 출석 보너스를 처리하고, 도전이 성사되면 Duel 로 1:1 대결을 중계한다.
    /// 연결마다 읽기 전용 스레드를 하나 쓴다(LAN 소수 인원용 — 대규모 동접은 고려하지 않는다).
    /// </summary>
    public sealed class TcpArenaServer : IDisposable
    {
        sealed class Conn
        {
            public int Id; public TcpClient Client; public NetworkStream Stream; public readonly object WriteLock = new object();
        }

        /// <summary>진행 중인 결투 하나. 두 참가자 id 가 같은 인스턴스를 가리킨다.</summary>
        sealed class DuelRuntime
        {
            public int AId, BId;
            public PvpBattle Duel;
            public BattleAction PendingA, PendingB;
        }

        readonly ArenaState _state;
        readonly GameData _data;
        readonly TrainerAccountStore _accounts;   // null 이면 계정 저장 없이(테스트용) 예전처럼 동작한다
        readonly TcpListener _listener;
        readonly IRng _rng = new SystemRng();
        readonly Dictionary<int, Conn> _conns = new Dictionary<int, Conn>();
        readonly Dictionary<int, PlayerState> _accountsByPlayerId = new Dictionary<int, PlayerState>();
        readonly Dictionary<int, DuelRuntime> _duelsByPlayer = new Dictionary<int, DuelRuntime>();
        readonly object _lock = new object();
        volatile bool _running;
        Thread _acceptThread;

        public int Port { get; }
        /// <summary>방 목록에 보이는 이름(호스트가 정한다).</summary>
        public string RoomName { get; set; } = "대결 마당";
        /// <summary>지금 입장해 있는 사람 수(방 목록·비콘에 보인다).</summary>
        public int PlayerCount { get { lock (_lock) return _state.Players.Count; } }
        /// <summary>디버깅·테스트용 훅. 예외 문자열을 받는다(콘솔에 못 찍는 환경에서도 원인을 볼 수 있게).</summary>
        public event Action<string> Logged;

        public TcpArenaServer(GameData data, WorldMap map, TrainerAccountStore accounts = null, int port = 0)
        {
            _data = data;
            _state = new ArenaState(data, map);
            _accounts = accounts;
            _listener = new TcpListener(IPAddress.Any, port);
            // 리눅스(안드로이드)에서는 직전 세션의 연결이 TIME_WAIT 로 남아 있으면 같은 포트로 바로 다시 방을 열 때
            // "Address already in use" 로 실패한다. 주소 재사용을 켜서 방을 닫았다 바로 다시 열 수 있게 한다.
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start()
        {
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ArenaServer.Accept" };
            _acceptThread.Start();
        }

        void Log(string s) => Logged?.Invoke(s);

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { return; } // 리스너가 Stop() 된 경우

                var thread = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "ArenaServer.Client" };
                thread.Start();
            }
        }

        void HandleClient(TcpClient client)
        {
            var stream = client.GetStream();
            var reader = new Frame.Reader();
            var buf = new byte[4096];
            Conn conn = null;
            try
            {
                // 첫 메시지는 Hello(입장) 또는 Probe(방 검색 — 방 이름·인원만 알려 주고 바로 끊는다)여야 한다.
                string helloJson = ReadOneBlocking(stream, reader, buf);
                if (helloJson == null || !NetCodec.TryDecode(helloJson, out var type, out var d))
                { Log("첫 메시지를 읽을 수 없다"); client.Close(); return; }
                if (type == NetMsgType.Probe)
                {
                    var info = Frame.Encode(NetCodec.Encode(NetMsgType.RoomInfo, new RoomInfoMessage { Name = RoomName, Players = PlayerCount }));
                    stream.Write(info, 0, info.Length);
                    client.Close();
                    return;
                }
                if (type != NetMsgType.Hello) { Log("첫 메시지가 hello 가 아니다"); client.Close(); return; }
                var hello = d.ToObject<HelloMessage>();

                // 이름으로 영구 저장 계정을 찾아 불러오거나(처음 보는 이름이면) 새로 만든다. 접속 시 출석 보너스도 여기서 준다.
                PlayerState account = null;
                bool bonusGranted = false;
                int avatarSpeciesId = hello.SpeciesId;
                if (_accounts != null)
                {
                    account = _accounts.Exists(hello.Name) ? _accounts.Load(_data, hello.Name) : null;
                    if (account == null)
                    {
                        account = PlayerState.NewGame(_data, hello.SpeciesId);   // 새 계정이거나 저장이 손상됨
                        account.Party[0].RollIndividualValues(_data);            // 새로 만든 파트너만 개체값을 굴린다
                    }
                    bonusGranted = DailyBonus.TryGrant(account, DateTime.UtcNow);
                    _accounts.Save(hello.Name, account);
                    avatarSpeciesId = account.Party[0].SpeciesId;
                }

                Trainer me;
                lock (_lock) me = _state.Join(hello.Name, avatarSpeciesId);
                conn = new Conn { Id = me.Id, Client = client, Stream = stream };
                lock (_lock)
                {
                    _conns[me.Id] = conn;
                    if (account != null) _accountsByPlayerId[me.Id] = account;
                }

                var others = new List<TrainerInfo>();
                lock (_lock) foreach (var o in _state.Others(me.Id)) others.Add(o.ToInfo());
                Send(conn, NetMsgType.Welcome, new WelcomeMessage
                {
                    Id = me.Id, X = me.X, Y = me.Y, Dir = (int)me.Dir, Others = others,
                    Money = account?.Money ?? 0, BonusGranted = bonusGranted,
                    StateJson = account != null ? SaveSerializer.ToJson(account) : null,
                });
                Broadcast(NetMsgType.Joined, new JoinedMessage { Info = me.ToInfo() }, exceptId: me.Id);
                Log($"#{me.Id} {me.Name} 입장" + (bonusGranted ? " (출석 보너스 지급)" : ""));

                for (;;)
                {
                    string json = ReadOneBlocking(stream, reader, buf);
                    if (json == null) break;
                    if (!NetCodec.TryDecode(json, out var t2, out var d2)) continue;
                    Handle(conn.Id, t2, d2);
                }
            }
            catch (Exception e) { Log("클라이언트 처리 중 오류: " + e.Message); }
            finally
            {
                if (conn != null)
                {
                    ForfeitDuelIfAny(conn.Id);
                    lock (_lock) { _state.Leave(conn.Id); _conns.Remove(conn.Id); _accountsByPlayerId.Remove(conn.Id); }
                    Broadcast(NetMsgType.Left, new LeftMessage { Id = conn.Id });
                    Log($"#{conn.Id} 퇴장");
                }
                try { client.Close(); } catch (Exception) { /* 이미 닫힘 */ }
            }
        }

        static string ReadOneBlocking(NetworkStream stream, Frame.Reader reader, byte[] buf)
        {
            for (;;)
            {
                if (reader.TryTake(out var json)) return json;
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (IOException) { return null; }
                catch (ObjectDisposedException) { return null; }
                if (n <= 0) return null;
                reader.Feed(buf, n);
            }
        }

        void Handle(int fromId, string type, JObject d)
        {
            switch (type)
            {
                case NetMsgType.Move:
                {
                    var dir = (NetDirection)d.ToObject<MoveMessage>().Dir;
                    MoveOutcome outcome;
                    lock (_lock) outcome = _state.TryMove(fromId, dir);
                    Broadcast(NetMsgType.Moved, new MovedMessage { Id = fromId, X = outcome.X, Y = outcome.Y, Dir = (int)outcome.Dir });
                    break;
                }
                case NetMsgType.ChallengeRequest:
                {
                    int targetId = d.ToObject<ChallengeRequestMessage>().TargetId;
                    if (_duelsByPlayer.ContainsKey(fromId)) { SendTo(fromId, NetMsgType.Error, new ErrorMessage { Reason = "이미 대결 중이다!" }); break; }
                    if (_duelsByPlayer.ContainsKey(targetId)) { SendTo(fromId, NetMsgType.Error, new ErrorMessage { Reason = "상대가 이미 다른 대결 중이다!" }); break; }
                    string why;
                    Trainer from;
                    lock (_lock) { why = _state.RequestChallenge(fromId, targetId); from = _state.Get(fromId); }
                    if (why != null) { SendTo(fromId, NetMsgType.Error, new ErrorMessage { Reason = why }); break; }
                    SendTo(targetId, NetMsgType.ChallengeOffer, new ChallengeOfferMessage { FromId = fromId, FromName = from?.Name });
                    break;
                }
                case NetMsgType.ChallengeResponse:
                {
                    bool accept = d.ToObject<ChallengeResponseMessage>().Accept;
                    (int FromId, bool Accepted)? result;
                    lock (_lock) result = _state.Respond(fromId, accept);
                    if (result == null) break;
                    SendTo(result.Value.FromId, NetMsgType.ChallengeResult, new ChallengeResultMessage { OtherId = fromId, Accepted = result.Value.Accepted });
                    SendTo(fromId, NetMsgType.ChallengeResult, new ChallengeResultMessage { OtherId = result.Value.FromId, Accepted = result.Value.Accepted });
                    if (result.Value.Accepted) StartDuel(result.Value.FromId, fromId);
                    break;
                }
                case NetMsgType.DuelAction:
                    HandleDuelAction(fromId, TryParse<DuelActionMessage>(d));
                    break;
                case NetMsgType.DuelReplace:
                    HandleDuelReplace(fromId, TryParse<DuelReplaceMessage>(d));
                    break;
                case NetMsgType.UpdateAccount:
                    HandleUpdateAccount(fromId, d.ToObject<UpdateAccountMessage>());
                    break;
            }
        }

        /// <summary>잘못된 모양의 메시지는 예외 대신 null — 연결을 끊지 않고 그 메시지만 버린다.</summary>
        static T TryParse<T>(JObject d) where T : class
        {
            try { return d.ToObject<T>(); }
            catch (Exception) { return null; }
        }

        /// <summary>월드 메뉴(가방·상점·센터 등)에서 클라이언트가 바꾼 자기 계정을 반영·저장한다.
        /// 이동처럼 클라이언트를 신뢰하지만, 그래도 SaveSerializer.FromJson 으로 형식만은 검증한다.</summary>
        void HandleUpdateAccount(int fromId, UpdateAccountMessage msg)
        {
            if (_accounts == null) return;   // 계정 저장 없이 도는 테스트 서버 등
            var state = SaveSerializer.FromJson(_data, msg.StateJson);
            if (state == null) { Log($"#{fromId} 계정 갱신 무시(형식이 이상함)"); return; }
            string name;
            lock (_lock)
            {
                if (!_accountsByPlayerId.ContainsKey(fromId)) return;   // 계정 없이 접속한 경우(테스트 등)
                name = _state.Get(fromId)?.Name;
                _accountsByPlayerId[fromId] = state;
            }
            if (name != null) _accounts.Save(name, state);
        }

        /* ---------------------------------- 파티 대결 ---------------------------------- */

        const int DuelFallbackLevel = 10;   // 계정 정보가 없을 때(테스트 등)만 쓰는 기본 레벨

        static DuelMonsterInfo Info(Monster m) =>
            new DuelMonsterInfo { SpeciesId = m.SpeciesId, Level = m.Level, Exp = m.Exp, Hp = m.Hp, MaxHp = m.MaxHp, Moves = new List<string>(m.Moves) };

        /// <summary>상대에게 보여 주는 모습 — 기술·경험치는 숨긴다.</summary>
        static DuelMonsterInfo PublicInfo(Monster m) =>
            new DuelMonsterInfo { SpeciesId = m.SpeciesId, Level = m.Level, Hp = m.Hp, MaxHp = m.MaxHp };

        /// <summary>친선 대결: 계정에 저장된 진짜 파티(레벨·개체값·노력치·성격 그대로)를 쓰되, 매번 HP 는 가득 채운 새 사본으로 붙는다
        /// — 진 쪽 몬스터가 실제로 다치지는 않는다(치료소가 없는 대결 마당의 임시 규칙).</summary>
        List<Monster> BuildDuelParty(int playerId)
        {
            lock (_lock)
            {
                if (_accountsByPlayerId.TryGetValue(playerId, out var account) && account.Party.Count > 0)
                {
                    var party = new List<Monster>();
                    foreach (var m in account.Party) party.Add(Monster.CreateFullHpCopy(_data, m));
                    return party;
                }
                var trainer = _state.Get(playerId);
                return new List<Monster> { Monster.Create(_data, trainer?.SpeciesId ?? 0, DuelFallbackLevel) };
            }
        }

        void StartDuel(int aId, int bId)
        {
            Trainer ta, tb;
            lock (_lock) { ta = _state.Get(aId); tb = _state.Get(bId); }
            if (ta == null || tb == null) return;   // 수락하는 사이에 한쪽이 나갔다

            var partyA = BuildDuelParty(aId);
            var partyB = BuildDuelParty(bId);
            var rt = new DuelRuntime { AId = aId, BId = bId, Duel = new PvpBattle(_data, partyA, partyB, _rng) };
            lock (_lock) { _duelsByPlayer[aId] = rt; _duelsByPlayer[bId] = rt; }

            SendTo(aId, NetMsgType.DuelStart, new DuelStartMessage
            {
                OpponentId = bId, OpponentName = tb.Name, Party = partyA.ConvertAll(Info), Potions = PvpBattle.StartPotions,
                Opponent = PublicInfo(rt.Duel.Active(DuelSide.B)),
            });
            SendTo(bId, NetMsgType.DuelStart, new DuelStartMessage
            {
                OpponentId = aId, OpponentName = ta.Name, Party = partyB.ConvertAll(Info), Potions = PvpBattle.StartPotions,
                Opponent = PublicInfo(rt.Duel.Active(DuelSide.A)),
            });
            Log($"대결 시작: #{aId} vs #{bId}");
        }

        DuelRuntime DuelOf(int playerId)
        {
            lock (_lock) { _duelsByPlayer.TryGetValue(playerId, out var rt); return rt; }
        }

        /// <summary>양쪽이 행동을 낼 때까지 기다렸다가 한 라운드를 판정한다. 기권만은 상대를 기다리지 않고 바로 처리한다.
        /// 대결 중이 아니거나 이미 낸 행동, 규칙에 어긋난 행동은 오류를 알리거나 조용히 무시한다.</summary>
        void HandleDuelAction(int fromId, DuelActionMessage msg)
        {
            var rt = DuelOf(fromId);
            if (rt == null) return;   // 대결 중이 아니다(끝난 뒤 늦게 도착한 메시지 등) — 조용히 무시
            var action = msg?.ToAction();
            if (action == null) { SendTo(fromId, NetMsgType.Error, new ErrorMessage { Reason = "알 수 없는 행동이다!" }); return; }

            bool isA = rt.AId == fromId;
            var side = isA ? DuelSide.A : DuelSide.B;
            List<PvpEvent> events = null;
            string why = null;
            lock (_lock)
            {
                if (rt.Duel.IsOver) return;
                if (action is FleeAction) events = rt.Duel.Forfeit(side);
                else
                {
                    why = rt.Duel.WhyNot(side, action);
                    if (why == null)
                    {
                        if ((isA ? rt.PendingA : rt.PendingB) != null) return;   // 이미 냈다
                        if (isA) rt.PendingA = action; else rt.PendingB = action;
                        if (rt.PendingA != null && rt.PendingB != null)
                        {
                            try { events = rt.Duel.ResolveRound(rt.PendingA, rt.PendingB); }
                            catch (InvalidOperationException e) { Log("라운드 판정 실패: " + e.Message); }
                            rt.PendingA = rt.PendingB = null;
                        }
                    }
                }
            }
            if (why != null) { SendTo(fromId, NetMsgType.Error, new ErrorMessage { Reason = why }); return; }
            if (events == null) return;   // 상대가 아직 안 골랐다 — 기다린다

            SendDuelEvents(rt, events);
            FinishDuelIfOver(rt);
        }

        /// <summary>몬스터가 쓰러진 쪽이 다음으로 내보낼 몬스터를 고른다.</summary>
        void HandleDuelReplace(int fromId, DuelReplaceMessage msg)
        {
            var rt = DuelOf(fromId);
            if (rt == null || msg == null) return;
            var side = rt.AId == fromId ? DuelSide.A : DuelSide.B;
            List<PvpEvent> events = null;
            string why;
            lock (_lock)
            {
                if (rt.Duel.IsOver) return;
                why = rt.Duel.WhyNotReplacement(side, msg.PartyIndex);
                if (why == null) events = rt.Duel.SubmitReplacement(side, msg.PartyIndex);
            }
            if (why != null) { SendTo(fromId, NetMsgType.Error, new ErrorMessage { Reason = why }); return; }
            SendDuelEvents(rt, events);
        }

        void FinishDuelIfOver(DuelRuntime rt)
        {
            if (!rt.Duel.IsOver) return;
            lock (_lock)
            {
                if (!_duelsByPlayer.TryGetValue(rt.AId, out var cur) || cur != rt) return;   // 그 사이 연결 끊김으로 이미 정리됐다
                _duelsByPlayer.Remove(rt.AId); _duelsByPlayer.Remove(rt.BId);
            }
            var winnerSide = rt.Duel.Winner.Value;
            var loserSide = winnerSide == DuelSide.A ? DuelSide.B : DuelSide.A;
            int winnerId = winnerSide == DuelSide.A ? rt.AId : rt.BId;
            int loserId = winnerId == rt.AId ? rt.BId : rt.AId;
            var (winExp, winGrowth, loseExp, loseGrowth) = ApplyDuelGrowth(rt, winnerId, loserId, winnerSide, loserSide);

            SendTo(winnerId, NetMsgType.DuelEnded, new DuelEndedMessage { YouWon = true, ExpGained = winExp, Growth = winGrowth });
            SendTo(loserId, NetMsgType.DuelEnded, new DuelEndedMessage { YouWon = false, ExpGained = loseExp, Growth = loseGrowth });
            Log($"대결 종료: #{rt.AId} vs #{rt.BId} 승자={winnerId}"
                + (winExp > 0 ? $" (승자 경험치 {winExp})" : "") + (loseExp > 0 ? $" (패자 경험치 {loseExp})" : ""));
        }

        /// <summary>이긴 쪽의 영구 계정(선두 몬스터)에는 SPEC "승리 시 경험치"(패배한 종족의 baseExp·레벨 기준)를 그대로 적용한다.
        /// 친선 대결이라 진 쪽에게 벌칙은 없고, 대신 참가 보상으로 같은 공식을 상대(이긴 쪽) 기준으로 계산해 40%만 준다
        /// (그래서 지더라도 대결에 나설 이유가 있다 — 다만 승리보다는 항상 적다). 계정이 없으면(테스트 등) 그 쪽만 건너뛴다.</summary>
        (int WinExp, List<GrowthEvent> WinGrowth, int LoseExp, List<GrowthEvent> LoseGrowth) ApplyDuelGrowth(
            DuelRuntime rt, int winnerId, int loserId, DuelSide winnerSide, DuelSide loserSide)
        {
            lock (_lock)
            {
                var winnerMon = rt.Duel.Of(winnerSide);
                var loserMon = rt.Duel.Of(loserSide);

                int winExp = 0; var winGrowth = new List<GrowthEvent>();
                if (_accountsByPlayerId.TryGetValue(winnerId, out var winnerAccount) && winnerAccount.Party.Count > 0)
                {
                    winExp = Growth.ExpReward(_data.GetSpecies(loserMon.SpeciesId), loserMon.Level);
                    winGrowth = Growth.GainExp(_data, winnerAccount.Party[0], winExp);
                    Growth.GainEVs(_data, winnerAccount.Party[0], _data.GetSpecies(loserMon.SpeciesId));   // 노력치(승자만)
                    var winnerTrainer = _state.Get(winnerId);
                    if (_accounts != null && winnerTrainer != null) _accounts.Save(winnerTrainer.Name, winnerAccount);
                }

                int loseExp = 0; var loseGrowth = new List<GrowthEvent>();
                if (_accountsByPlayerId.TryGetValue(loserId, out var loserAccount) && loserAccount.Party.Count > 0)
                {
                    loseExp = Growth.ExpReward(_data.GetSpecies(winnerMon.SpeciesId), winnerMon.Level) * 2 / 5;   // 40%
                    if (loseExp > 0)
                    {
                        loseGrowth = Growth.GainExp(_data, loserAccount.Party[0], loseExp);
                        var loserTrainer = _state.Get(loserId);
                        if (_accounts != null && loserTrainer != null) _accounts.Save(loserTrainer.Name, loserAccount);
                    }
                }
                return (winExp, winGrowth, loseExp, loseGrowth);
            }
        }

        /// <summary>수신자 기준(0=나,1=상대)으로 뒤집어 양쪽에 한 묶음씩 보낸다. Ended 는 DuelEnded 메시지로 따로 보내므로 빠진다.</summary>
        void SendDuelEvents(DuelRuntime rt, List<PvpEvent> events)
        {
            foreach (var viewer in new[] { DuelSide.A, DuelSide.B })
            {
                var batch = new List<DuelEventMessage>();
                foreach (var e in events)
                {
                    var m = DuelEventMessage.From(e, viewer);
                    if (m != null) batch.Add(m);
                }
                if (batch.Count > 0) SendTo(viewer == DuelSide.A ? rt.AId : rt.BId, NetMsgType.DuelEvents, new DuelEventsMessage { Events = batch });
            }
        }

        /// <summary>연결이 끊긴 트레이너가 대결 중이었다면, 상대에게 기권승을 알리고 대결을 정리한다.</summary>
        void ForfeitDuelIfAny(int playerId)
        {
            DuelRuntime rt;
            lock (_lock) { _duelsByPlayer.TryGetValue(playerId, out rt); }
            if (rt == null) return;
            int otherId = rt.AId == playerId ? rt.BId : rt.AId;
            lock (_lock) { _duelsByPlayer.Remove(rt.AId); _duelsByPlayer.Remove(rt.BId); }
            SendTo(otherId, NetMsgType.DuelEnded, new DuelEndedMessage { YouWon = true, OpponentLeft = true });
        }

        void Send(Conn conn, string type, object payload)
        {
            var bytes = Frame.Encode(NetCodec.Encode(type, payload));
            lock (conn.WriteLock)
            {
                try { conn.Stream.Write(bytes, 0, bytes.Length); }
                catch (Exception e) { Log($"#{conn.Id} 전송 실패: {e.Message}"); }
            }
        }

        void SendTo(int id, string type, object payload)
        {
            Conn conn;
            lock (_lock) _conns.TryGetValue(id, out conn);
            if (conn != null) Send(conn, type, payload);
        }

        void Broadcast(string type, object payload, int exceptId = -1)
        {
            List<Conn> targets;
            lock (_lock) targets = new List<Conn>(_conns.Values);
            foreach (var c in targets) if (c.Id != exceptId) Send(c, type, payload);
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch (Exception) { /* 무시 */ }
            List<Conn> targets;
            lock (_lock) targets = new List<Conn>(_conns.Values);
            foreach (var c in targets) try { c.Client.Close(); } catch (Exception) { /* 무시 */ }
        }
    }
}
