using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static MonsterAdventure.Core.Korean;

namespace MonsterAdventure
{
    /// <summary>
    /// 야생 전투 한 판의 진행과 연출. 규칙은 전부 Core 의 BattleSession 이 계산하고,
    /// 여기서는 그 이벤트를 하나씩 받아 웹 버전과 같은 문구·애니메이션으로 재생한다.
    /// LAN 대결(<see cref="RunPvp"/>)도 같은 화면·재생 코드를 쓴다 — 규칙은 서버(PvpBattle)가 판정하고, 여기서는
    /// 서버가 보낸 사건을 복제본(PvpReplica)에 반영하면서 BattleEvent 로 바꿔 재생할 뿐이다.
    /// </summary>
    public sealed class BattleController
    {
        struct Vis
        {
            public float X, Y, A, S;
            public bool Blink;
            public static Vis Default => new Vis { A = 1f, S = 1f };
        }

        readonly MonoBehaviour _host;
        readonly GameUi _ui;
        readonly GameData _data;
        readonly PlayerState _state;
        readonly IRng _rng;

        BattleSession _s;
        // LAN 대결 모드(RunPvp)에서만 채워진다. 단일 플레이에서는 전부 null.
        PvpReplica _pvp;
        TcpArenaClient _client;
        readonly Queue<(string Type, JObject Data)> _incoming = new Queue<(string, JObject)>();
        BattleAction _picked;
        Vis _ve = Vis.Default, _vp = Vis.Default;
        float _dhpEnemy, _dhpPlayer;                 // 화면에 보이는 HP(모델 값을 따라 천천히 움직인다)
        int _shownSpecies, _shownLevel;              // 성장 이벤트를 재생하기 전까지 화면에 보이는 아군 종족·레벨
        bool _afterFaint;
        bool _ballVisible; Vector2 _ballPos; float _ballRot;

        // 볼 투척: BallisticThrow 는 y 가 위쪽 양수라서 화면 좌표(y 아래로 증가)의 y 를 뒤집어 넘긴다.
        // 속도 520·중력 600 이면 아군→적까지 약 0.6초로, 예전 투척 연출 길이와 비슷하다.
        static readonly Vec2 ThrowStart = new Vec2(110, -190), ThrowTarget = new Vec2(355, -108);
        const double ThrowSpeed = 520, ThrowGravity = 600, ThrowRadius = 30, ThrowAimRange = 0.35;
        bool _aiming; ThrowResult _aimPreview, _lastThrow;

        static Texture2D _background, _ball;

        /// <summary>이번 전투의 날씨(None 이면 없음). Run 전에 호출한 쪽이 정한다.</summary>
        public WeatherKind Weather = WeatherKind.None;
        public int WeatherTurns = 5;

        public BattleOutcome Outcome => _s.Outcome;

        public BattleController(MonoBehaviour host, GameUi ui, GameData data, PlayerState state, IRng rng)
        {
            _host = host; _ui = ui; _data = data; _state = state; _rng = rng;
        }

        /// <summary>LAN 대결용. 규칙 난수·플레이어 저장은 쓰지 않는다(서버가 판정하고 임시 사본으로 싸운다).</summary>
        public BattleController(MonoBehaviour host, GameUi ui, GameData data, TcpArenaClient client)
        {
            _host = host; _ui = ui; _data = data; _client = client;
        }

        // 화면·재생 코드는 이 접근자만 거치므로 단일 플레이(_s)와 대결(_pvp)이 같은 코드를 쓴다.
        Monster ActiveMon => _pvp != null ? _pvp.Active : _s.Active;
        Monster EnemyMon => _pvp != null ? _pvp.Opp : _s.Enemy;
        int ActiveIdx => _pvp != null ? _pvp.ActiveIndex : _s.ActiveIndex;
        IList<Monster> PartyList => _pvp != null ? _pvp.Mine : _state.Party;
        /// <summary>상대 몬스터 앞에 붙는 말: 야생이면 "야생의 ", 대결이면 "{상대 이름}의 ".</summary>
        string EnemyPrefix => _pvp != null ? $"{_pvp.OpponentName}의 " : "야생의 ";

        string PlayerName => _data.GetSpecies(_shownSpecies).Name;
        string EnemyName => EnemyMon.Species(_data).Name;
        string TypeName(string id) => _data.Types.Find(t => t.Id == id).Name;

        /// <summary>전투가 끝나면 화면은 검게 덮인 채(Fade = 1)로 돌아온다. 이후 처리는 호출한 쪽이 한다.</summary>
        public IEnumerator Run(Monster enemy)
        {
            _s = new BattleSession(_data, _state, enemy, _rng);
            _s.CaptureMode = CaptureMode.ThreeShake;    // 흔들림 3회 판정(웹 골든과 무관한 Legacy 는 테스트용으로만 남는다)
            if (Weather != WeatherKind.None) _s.Observers.Add(new WeatherObserver(Weather, WeatherTurns));
            _lastThrow = null; _aiming = false;
            _dhpEnemy = enemy.Hp;
            _dhpPlayer = _s.Active.Hp;
            _shownSpecies = _s.Active.SpeciesId; _shownLevel = _s.Active.Level;
            _ve = Vis.Default; _vp = Vis.Default;
            _ballVisible = false; _afterFaint = false;
            _ui.Data = _data;

            yield return EnterScene();

            yield return _ui.Say($"앗! 야생의 {J(EnemyName, "이", "가")} 나타났다!", 900);
            _vp.S = 0f;
            yield return _ui.Say($"가라! {PlayerName}!", 500);
            yield return GameUi.Tween(.3f, p => _vp.S = p);

            // 전투 시작 훅(날씨 등)의 안내를 첫 행동 선택 전에 보여준다. 관찰자가 없으면 아무 이벤트도 없다.
            foreach (var ev in _s.Begin()) yield return PlayEvent(ev);

            while (!_s.IsOver)
            {
                yield return PickAction();
                if (_picked == null) continue;
                yield return PlayTurn(_picked);
            }

            yield return ExitScene();
        }

        /// <summary>흰 점멸 → 검게 → 전투 화면 → 밝게 (양쪽 몬스터가 옆에서 들어온다).</summary>
        IEnumerator EnterScene()
        {
            Sfx.Play(SfxKind.Encounter);
            yield return GameUi.Tween(.65f, p => _ui.Flash = Mathf.FloorToInt(p * 6) % 2 == 0 ? .8f : 0f);
            _ui.Flash = 0f;
            yield return GameUi.Tween(.3f, p => _ui.Fade = p);
            _ui.DrawScene = DrawBattle;
            _ui.AlwaysShowTextBox = true;
            _ve.X = 160f; _vp.X = -160f;
            yield return GameUi.Tween(.3f, p => _ui.Fade = 1f - p);
            _host.StartCoroutine(GameUi.Tween(.6f, p => { _ve.X = 160f * (1f - p); _vp.X = -160f * (1f - p); }));
            yield return GameUi.Wait(.6f);
        }

        IEnumerator ExitScene()
        {
            yield return GameUi.Tween(.3f, p => _ui.Fade = p);
            _ui.DrawScene = null;
            _ui.AlwaysShowTextBox = false;
        }

        /* ---------------------------------- LAN 대결 ---------------------------------- */

        /// <summary>서버가 보낸 duelEvents/duelEnded 메시지를 넘겨준다(ArenaController.Handle 에서 호출).</summary>
        public void Feed(string type, JObject data) => _incoming.Enqueue((type, data));

        /// <summary>
        /// LAN 대결 한 판. 단일 플레이처럼 메뉴에서 행동을 고르면 서버로 보내고, 양쪽이 고른 뒤 서버가 판정해 보낸
        /// 사건 묶음을 같은 화면에서 재생한다. 끝나면(단일 전투처럼) 화면이 검게 덮인 채 돌아온다.
        /// </summary>
        public IEnumerator RunPvp(DuelStartMessage start)
        {
            _pvp = new PvpReplica(_data, start);
            _dhpEnemy = _pvp.Opp.Hp;
            _dhpPlayer = _pvp.Active.Hp;
            _shownSpecies = _pvp.Active.SpeciesId; _shownLevel = _pvp.Active.Level;
            _ve = Vis.Default; _vp = Vis.Default;
            _ballVisible = false; _afterFaint = false; _aiming = false;
            _ui.Data = _data;

            yield return EnterScene();

            string opp = _pvp.OpponentName;
            yield return _ui.Say($"{J(opp, "과", "와")}의 대결이 시작되었다!", 900);
            yield return _ui.Say($"{J(opp, "은", "는")} {J(EnemyName, "을", "를")} 내보냈다!", 700);
            _vp.S = 0f;
            yield return _ui.Say($"가라! {PlayerName}!", 500);
            yield return GameUi.Tween(.3f, p => _vp.S = p);

            bool needAction = true;     // 쓰러져서 교체를 기다리는 중이면 행동을 고르지 않고 다음 묶음을 기다린다
            for (;;)
            {
                if (needAction && !EndPending())
                {
                    yield return PickAction();
                    if (_picked == null) continue;
                    if (!EndPending())      // 상대가 끊고 나갔으면 보낼 필요 없다
                    {
                        _client.SendDuelAction(_picked);
                        if (!(_picked is FleeAction)) yield return _ui.Say("상대의 선택을 기다리는 중...", 300);
                    }
                }
                while (_incoming.Count == 0) yield return null;
                var (type, d) = _incoming.Dequeue();
                if (type == NetMsgType.DuelEnded)
                {
                    yield return PlayPvpEnd(d.ToObject<DuelEndedMessage>());
                    break;
                }
                if (type == NetMsgType.Error)   // 서버가 방금 행동을 거절했다 — 안내하고 같은 단계를 다시 한다
                {
                    yield return _ui.Say(d.ToObject<ErrorMessage>().Reason);
                    continue;
                }
                if (type != NetMsgType.DuelEvents) continue;
                needAction = true;
                foreach (var m in d.ToObject<DuelEventsMessage>().Events)
                {
                    yield return PlayPvpEvent(m);
                    needAction = m.Kind != NetMsgType.Duel.ReplacementNeeded;
                }
            }

            yield return ExitScene();
            _pvp = null;
        }

        bool EndPending() => _incoming.Count > 0 && _incoming.Peek().Type == NetMsgType.DuelEnded;

        IEnumerator PlayPvpEvent(DuelEventMessage m)
        {
            var e = _pvp.Apply(m);
            if (e != null) { yield return PlayEvent(e); yield break; }
            if (m.Kind == NetMsgType.Duel.Forfeit)
                yield return _ui.Say(m.Side == 0 ? "기권했다..." : $"{J(_pvp.OpponentName, "은", "는")} 기권했다!", 900);
        }

        IEnumerator PlayPvpEnd(DuelEndedMessage ended)
        {
            if (ended.OpponentLeft) { yield return _ui.Say("상대가 접속을 끊어서 기권승했다!", 1300); yield break; }
            yield return _ui.Say(ended.YouWon ? "대결에서 이겼다!" : "대결에서 졌다...", 1300);
            if (ended.ExpGained <= 0) yield break;
            // 친선 대결이라 진 쪽도(승자보다는 적지만) 참가 경험치를 받는다. 성장은 선두 몬스터가 받으니 화면도 선두로 바꾼다.
            _pvp.ShowLead();
            var lead = _pvp.Active;
            _shownSpecies = lead.SpeciesId; _shownLevel = lead.Level;
            _dhpPlayer = lead.Hp; _vp = Vis.Default;
            yield return _ui.Say($"{J(PlayerName, "은", "는")} 경험치를 {ended.ExpGained} 얻었다!", 800);
            foreach (var g in ended.Growth ?? new List<GrowthEvent>()) yield return PlayGrowth(g);
        }

        /* ---------------------------------- 행동 선택 ---------------------------------- */

        static int FirstUsableIndex(IList<Monster> party)
        {
            for (int i = 0; i < party.Count; i++) if (party[i].Hp > 0) return i;
            return 0;
        }

        static string PotionBlock(Monster m, int _) =>
            m.Hp <= 0 ? "기절한 몬스터에게는 쓸 수 없다!" : m.Hp >= m.MaxHp ? "이미 HP가 가득 찼다!" : null;

        IEnumerator PickAction()
        {
            _picked = null;
            var pm = ActiveMon;
            yield return _ui.Choose(new[] { "싸운다", "가방", "몬스터", _pvp != null ? "기권한다" : "도망친다" },
                new MenuOptions { Cols = 2, Rect = new Rect(240, 236, 228, 72), Prompt = $"{J(pm.Species(_data).Name, "은", "는")} 무엇을 할까?" });
            int a = _ui.Choice;

            if (a == 0)
            {
                var labels = pm.Moves.Select(id => { var mv = _data.GetMove(id); return $"{mv.Name}  {TypeName(mv.Type)} {mv.Power}"; }).ToList();
                yield return _ui.Choose(labels, new MenuOptions { Cols = 2, Rect = new Rect(8, 236, 464, 72), Cancel = true, Prompt = "", Full = true });
                if (_ui.Choice >= 0) _picked = new MoveAction(pm.Moves[_ui.Choice]);
            }
            else if (a == 1 && _pvp != null)
            {
                // 대결에는 볼이 없다 — 상처약(이 대결의 남은 개수)만 보인다.
                yield return _ui.Choose(new[] { $"상처약 x{_pvp.Potions}", "취소" },
                    new MenuOptions { Rect = new Rect(240, 174, 228, 58), Cancel = true, Prompt = "" });
                if (_ui.Choice != 0) yield break;
                if (_pvp.Potions <= 0) { yield return _ui.Say("상처약이 없다!"); yield break; }
                yield return _ui.ChooseParty(_pvp.Mine, new PartyScreenOptions { Title = "누구에게 사용할까요?", Mark = _pvp.ActiveIndex, Filter = PotionBlock });
                if (_ui.Choice >= 0) _picked = new PotionAction(_ui.Choice);
            }
            else if (a == 1)
            {
                yield return _ui.Choose(new[] { $"몬스터볼 x{_state.Balls}", $"상처약 x{_state.Potions}", "취소" },
                    new MenuOptions { Rect = new Rect(240, 150, 228, 82), Cancel = true, Prompt = "" });
                int i = _ui.Choice;
                if (i < 0 || i == 2) yield break;
                if (i == 0)
                {
                    if (_state.Balls <= 0) { yield return _ui.Say("몬스터볼이 없다!"); yield break; }
                    yield return AimThrow();     // 각도를 맞춰 던진다 — 결과(_picked)는 X 로 취소하면 null
                    yield break;
                }
                else
                {
                    if (_state.Potions <= 0) { yield return _ui.Say("상처약이 없다!"); yield break; }
                    yield return _ui.ChooseParty(_state.Party, new PartyScreenOptions
                    {
                        Title = "누구에게 사용할까요?", Mark = _s.ActiveIndex, Filter = PotionBlock,
                    });
                    if (_ui.Choice >= 0) _picked = new PotionAction(_ui.Choice);
                }
            }
            else if (a == 2)
            {
                yield return _ui.ChooseParty(PartyList, new PartyScreenOptions
                {
                    Title = "누구로 교체할까요?", Mark = ActiveIdx, Start = ActiveIdx,
                    Filter = (m, i) => m.Hp <= 0 ? "기절한 몬스터는 싸울 수 없다!" : i == ActiveIdx ? "이미 싸우고 있다!" : null,
                });
                if (_ui.Choice >= 0) _picked = new SwitchAction(_ui.Choice);
            }
            else if (_pvp != null)
            {
                yield return _ui.Choose(new[] { "예", "아니오" },
                    new MenuOptions { Rect = new Rect(348, 174, 120, 58), Cancel = true, Prompt = "정말 기권할까요?" });
                if (_ui.Choice == 0) _picked = new FleeAction();
            }
            else _picked = new FleeAction();
        }

        /* ---------------------------------- 이벤트 재생 ---------------------------------- */

        IEnumerator PlayTurn(BattleAction action)
        {
            var it = _s.ResolveTurn(action).GetEnumerator();
            for (;;)
            {
                if (!it.MoveNext()) yield break;
                yield return PlayEvent(it.Current);
            }
        }

        IEnumerator PlayEvent(BattleEvent e)
        {
            switch (e.Kind)
            {
                case BattleEventKind.MoveUsed:
                {
                    string who = e.Side == Side.Player ? PlayerName : $"{EnemyPrefix}{EnemyName}";
                    yield return _ui.Say($"{who}의 {_data.GetMove(e.MoveId).Name}!", 500);
                    break;
                }
                case BattleEventKind.Missed:
                    yield return _ui.Say("그러나 빗나갔다!", 700);
                    break;

                case BattleEventKind.Damage:
                {
                    // e.Side 는 맞은 쪽. 공격한 쪽이 상대를 향해 26px 돌진했다가 돌아온다.
                    bool playerAttacks = e.Side == Side.Enemy;
                    float dir = playerAttacks ? 1f : -1f;
                    yield return GameUi.Tween(.13f, p => SetX(playerAttacks, dir * 26f * p));
                    _host.StartCoroutine(GameUi.Tween(.15f, p => SetX(playerAttacks, dir * 26f * (1f - p))));
                    Sfx.Play(e.Critical ? SfxKind.Crit : SfxKind.Hit);
                    SetBlink(!playerAttacks, true);
                    yield return GameUi.Wait(.45f);
                    SetBlink(!playerAttacks, false);
                    yield return GameUi.Wait(.3f);
                    if (e.Critical) yield return _ui.Say("급소에 맞았다!", 600);
                    if (e.Multiplier > 1) yield return _ui.Say("효과는 굉장했다!", 700);
                    else if (e.Multiplier < 1) yield return _ui.Say("효과가 별로인 듯하다...", 700);
                    break;
                }

                case BattleEventKind.Fainted:
                {
                    bool enemy = e.Side == Side.Enemy;
                    Sfx.Play(SfxKind.Faint);
                    yield return GameUi.Tween(.5f, p => SetFaint(enemy, p));
                    yield return _ui.Say(enemy ? $"{EnemyPrefix}{J(EnemyName, "은", "는")} 쓰러졌다!" : $"{J(PlayerName, "은", "는")} 쓰러졌다!", 800);
                    break;
                }
                case BattleEventKind.BlackedOut:
                    yield return _ui.Say("눈앞이 캄캄해졌다...", 900);
                    break;

                case BattleEventKind.ExpGained:
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} 경험치를 {e.Amount} 얻었다!", 800);
                    break;
                case BattleEventKind.Growth:
                    yield return PlayGrowth(e.Growth);
                    break;
                case BattleEventKind.MoneyGained:
                    yield return _ui.Say($"₩{e.Amount}을(를) 얻었다!", 900);
                    break;

                case BattleEventKind.SwitchOut:
                    if (e.Side == Side.Enemy)
                    {
                        yield return _ui.Say($"{J(_pvp.OpponentName, "은", "는")} {J(EnemyName, "을", "를")} 불러들였다!", 400);
                        yield return GameUi.Tween(.25f, p => _ve.S = 1f - p);
                        break;
                    }
                    yield return _ui.Say($"돌아와, {PlayerName}!", 400);
                    yield return GameUi.Tween(.25f, p => _vp.S = 1f - p);
                    break;
                case BattleEventKind.ReplacementNeeded:
                {
                    if (e.Side == Side.Enemy)
                    {
                        yield return _ui.Say("상대가 몬스터를 고르는 중...", 300);
                        break;
                    }
                    var party = PartyList;
                    yield return _ui.ChooseParty(party, new PartyScreenOptions
                    {
                        Title = "다음 몬스터를 고르세요", Cancel = false, Start = FirstUsableIndex(party),
                        Filter = (m, _) => m.Hp <= 0 ? "기절한 몬스터는 싸울 수 없다!" : null,
                    });
                    if (_pvp != null) _client.SendDuelReplace(_ui.Choice); else _s.ChooseReplacement(_ui.Choice);
                    _afterFaint = true;
                    break;
                }
                case BattleEventKind.SwitchIn:
                {
                    if (e.Side == Side.Enemy)
                    {
                        _dhpEnemy = EnemyMon.Hp;
                        _ve = Vis.Default; _ve.S = 0f;
                        yield return _ui.Say($"{J(_pvp.OpponentName, "은", "는")} {J(EnemyName, "을", "를")} 내보냈다!", 500);
                        yield return GameUi.Tween(.3f, p => _ve.S = p);
                        break;
                    }
                    var m = ActiveMon;
                    _shownSpecies = m.SpeciesId; _shownLevel = m.Level;
                    _dhpPlayer = m.Hp;
                    _vp = Vis.Default; _vp.S = 0f;
                    yield return _ui.Say($"가라! {PlayerName}!", _afterFaint ? 500 : 400);
                    _afterFaint = false;
                    yield return GameUi.Tween(.3f, p => _vp.S = p);
                    break;
                }

                case BattleEventKind.PotionUsed:
                {
                    Sfx.Play(SfxKind.Heal);
                    string target = e.Side == Side.Enemy ? $"{_pvp.OpponentName}의 {e.Name}" : PartyList[e.PartyIndex].Species(_data).Name;
                    yield return _ui.Say($"{target}의 HP가 회복되었다!", 700);
                    break;
                }

                case BattleEventKind.BallThrown:
                    yield return ThrowBall();
                    break;
                case BattleEventKind.BallMissed:
                    // 궤적이 목표 원을 못 지났다 — 볼만 잃고 곧바로 적의 차례가 된다.
                    yield return GameUi.Tween(.2f, p => _ballPos += new Vector2(6f * (1f - p), -14f * Mathf.Sin(p * Mathf.PI)));
                    _ballVisible = false;
                    yield return _ui.Say("몬스터볼이 빗나갔다!", 800);
                    break;

                // 옵저버(날씨·특성·도구)가 만든 이벤트: 문장은 Core 가 이미 만들어 준다.
                case BattleEventKind.WeatherStarted:
                case BattleEventKind.WeatherEnded:
                case BattleEventKind.ObserverNotice:
                    if (!string.IsNullOrEmpty(e.Text)) yield return _ui.Say(e.Text, 800);
                    break;
                case BattleEventKind.ResidualDamage:
                    Sfx.Play(SfxKind.Hit);
                    SetBlink(e.Side == Side.Player, true);
                    yield return GameUi.Wait(.4f);
                    SetBlink(e.Side == Side.Player, false);
                    if (!string.IsNullOrEmpty(e.Text)) yield return _ui.Say(e.Text, 700);
                    break;
                case BattleEventKind.ResidualHeal:
                    Sfx.Play(SfxKind.Heal);
                    if (!string.IsNullOrEmpty(e.Text)) yield return _ui.Say(e.Text, 700);
                    break;
                case BattleEventKind.CatchAttempt:
                    yield return ShakeBall(e.Catch);
                    break;
                case BattleEventKind.Caught:
                    Sfx.Play(SfxKind.Catch);
                    yield return _ui.Say($"신난다! 야생의 {J(EnemyName, "을", "를")} 잡았다!", 1000);
                    _ballVisible = false;
                    if (e.SentToBox) yield return _ui.Say($"파티가 가득 차서 {J(EnemyName, "은", "는")}\n박스로 전송되었다.", 1000);
                    break;

                case BattleEventKind.FleeSucceeded:
                    yield return _ui.Say("무사히 도망쳤다!", 800);
                    break;
                case BattleEventKind.FleeFailed:
                    yield return _ui.Say("도망칠 수 없었다!", 800);
                    break;
            }
        }

        IEnumerator PlayGrowth(GrowthEvent g)
        {
            switch (g.Kind)
            {
                case GrowthKind.LevelUp:
                    _shownLevel = g.Level;
                    Sfx.Play(SfxKind.LevelUp);
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} 레벨 {g.Level}(으)로 올랐다!", 900);
                    break;
                case GrowthKind.LearnedMove:
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} {J(_data.GetMove(g.MoveId).Name, "을", "를")} 배웠다!", 900);
                    break;
                case GrowthKind.ReplacedMove:
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} {J(_data.GetMove(g.ForgottenMoveId).Name, "을", "를")} 잊고\n{J(_data.GetMove(g.MoveId).Name, "을", "를")} 배웠다!", 1100);
                    break;
                case GrowthKind.Evolved:
                {
                    string before = PlayerName;
                    yield return _ui.Say($"어라...? {before}의 모습이...!", 1000);
                    yield return GameUi.Tween(.9f, p => _ui.Flash = Mathf.Abs(Mathf.Sin(p * Mathf.PI * 4f)) * .9f);
                    _shownSpecies = g.ToSpeciesId;
                    _ui.Flash = 0f;
                    Sfx.Play(SfxKind.LevelUp);
                    yield return _ui.Say($"{J(before, "은", "는")} {J(PlayerName, "으로", "로")} 진화했다!", 1200);
                    break;
                }
            }
        }

        /// <summary>
        /// 던질 각도를 고른다: 정답 각도(SolveAngle) 둘레를 좌우로 왕복하는 각도로 궤적 미리보기가 움직이고,
        /// Z 로 던진다(X 는 취소 → _picked 가 null 이라 행동 선택으로 돌아간다). 명중 여부·품질은 BallisticThrow 가
        /// 정하므로 여기엔 난수가 없다.
        /// </summary>
        IEnumerator AimThrow()
        {
            _picked = null;
            double ideal = BallisticThrow.SolveAngle(ThrowStart, ThrowSpeed, ThrowGravity, ThrowTarget) ?? 0.6;
            GameInput.Instance.Flush();
            _aiming = true;
            float t0 = Time.time;
            for (;;)
            {
                float swing = Mathf.PingPong((Time.time - t0) / .9f, 1f) * 2f - 1f;   // -1..1 왕복
                _aimPreview = BallisticThrow.Simulate(ThrowStart, ThrowSpeed, ideal + swing * ThrowAimRange, ThrowGravity, ThrowTarget, ThrowRadius);
                if (GameInput.Instance.TryDequeue(out var key))
                {
                    if (key == GameKey.Cancel) { _aiming = false; yield break; }
                    if (key == GameKey.Ok) break;
                }
                yield return null;
            }
            _aiming = false;
            _lastThrow = _aimPreview;
            _picked = new BallAction(_lastThrow.Hit, BallisticThrow.ThrowQualityMultiplier(_lastThrow.Quality));
        }

        IEnumerator ThrowBall()
        {
            yield return _ui.Say("몬스터볼을 던졌다!", 300);
            Vector2 from = new Vector2(110, 190), to = new Vector2(355, 108);
            var r = _lastThrow;
            _ballVisible = true; _ballPos = from; _ballRot = 0f;
            if (r == null)
            {
                yield return GameUi.Tween(.6f, p =>
                {
                    _ballPos = new Vector2(Mathf.Lerp(from.x, to.x, p), Mathf.Lerp(from.y, to.y, p) - Mathf.Sin(p * Mathf.PI) * 70f);
                    _ballRot = p * 12f;
                });
            }
            else
            {
                // 계산된 궤적을 그대로 따라 날아간다(고정 dt 표본 → 비행 시간에 맞춰 보간).
                var path = r.Path;
                yield return GameUi.Tween(Mathf.Max(.25f, (float)r.FlightTime), p =>
                {
                    float f = p * (path.Count - 1);
                    int i = Mathf.Min(path.Count - 2, Mathf.FloorToInt(f)); float k = f - i;
                    if (i < 0) { _ballPos = new Vector2((float)path[0].X, -(float)path[0].Y); return; }
                    _ballPos = new Vector2(Mathf.Lerp((float)path[i].X, (float)path[i + 1].X, k), -Mathf.Lerp((float)path[i].Y, (float)path[i + 1].Y, k));
                    _ballRot = p * 12f;
                });
            }
            if (r != null && !r.Hit) yield break;      // 빗나감: 뒤이은 BallMissed 이벤트가 마무리한다

            yield return GameUi.Tween(.25f, p => { _ve.S = 1f - p; _ve.A = 1f - p * .5f; });
            yield return GameUi.Tween(.3f, p => _ballPos = new Vector2(to.x, to.y + 28f * p));
            if (r != null && r.Excellent)
            {
                Sfx.Play(SfxKind.Crit);
                yield return _ui.Say("훌륭한 투척이다!", 600);
            }
        }

        IEnumerator ShakeBall(CatchResult r)
        {
            for (int i = 0; i < r.Shakes; i++)
            {
                yield return GameUi.Wait(.35f);
                Sfx.Play(SfxKind.Shake);
                yield return GameUi.Tween(.3f, q => _ballRot = Mathf.Sin(q * Mathf.PI * 2f) * .5f);
            }
            yield return GameUi.Wait(.35f);
            if (r.Caught) yield break;
            _ballVisible = false; _ve.S = 1f; _ve.A = 1f;
            yield return _ui.Say(new[] { "앗! 몬스터가 볼에서 빠져나왔다!", "아깝다! 거의 잡을 뻔했는데!", "아쉽다! 조금만 더 하면 됐는데!" }[r.Shakes], 900);
        }

        void SetX(bool player, float x) { if (player) _vp.X = x; else _ve.X = x; }
        void SetBlink(bool player, bool on) { if (player) _vp.Blink = on; else _ve.Blink = on; }
        void SetFaint(bool enemy, float p)
        {
            if (enemy) { _ve.Y = 50f * p; _ve.A = 1f - p; }
            else { _vp.Y = 50f * p; _vp.A = 1f - p; }
        }

        /* ---------------------------------- 그리기 ---------------------------------- */

        void DrawBattle()
        {
            EnsureArt();
            float dt = Time.deltaTime;
            _dhpEnemy = Approach(_dhpEnemy, EnemyMon.Hp, EnemyMon.MaxHp, dt);
            _dhpPlayer = Approach(_dhpPlayer, ActiveMon.Hp, ActiveMon.MaxHp, dt);

            UiKit.FillScreen(Color.black);
            UiKit.Texture(_background, 0, 0, 480, 232);

            float t = Time.time * 1000f;
            float bobE = Mathf.Sin(t / 450f) * 2f, bobP = Mathf.Sin(t / 450f + 2f) * 2f;
            var enemySp = EnemyMon.Species(_data);
            var playerSp = _data.GetSpecies(_shownSpecies);
            MonsterArt.DrawAt(enemySp, 355 + _ve.X, 108 + _ve.Y + bobE, 34 * _ve.S, _ve.A * BlinkAlpha(_ve, t));
            MonsterArt.DrawAt(playerSp, 125 + _vp.X, 176 + _vp.Y + bobP, 44 * _vp.S, _vp.A * BlinkAlpha(_vp, t));

            if (_aiming) DrawAim();
            if (_ballVisible) DrawBall();

            DrawInfo(enemySp, EnemyMon.Level, _dhpEnemy, EnemyMon.MaxHp, 14, 14, 208, false, 0);
            DrawInfo(playerSp, _shownLevel, _dhpPlayer, ActiveMon.MaxHp, 258, 158, 214, true, ActiveMon.Exp);
        }

        static float BlinkAlpha(Vis v, float tMs) => v.Blink && Mathf.FloorToInt(tMs / 60f) % 2 == 1 ? .15f : 1f;

        static float Approach(float shown, int target, int max, float dt)
        {
            float speed = Mathf.Max(max * .5f, 18f) * dt;
            return shown > target ? Mathf.Max(target, shown - speed) : Mathf.Min(target, shown + speed);
        }

        void DrawInfo(SpeciesData sp, int level, float dhp, int maxHp, float x, float y, float w, bool player, int exp)
        {
            UiKit.Box(x, y, w, player ? 64 : 50, UiKit.C("#1b2340"), UiKit.C("#d8dcf0"));
            UiKit.Text(sp.Name, x + 10, y + 7, 14, bold: true);
            UiKit.Text($"Lv.{level}", x + w - 10, y + 8, 12, UiKit.C("#cfd6f5"), TextAnchor.UpperRight);
            _ui.Chip(sp.Type, x + 10, y + 25);
            float f = dhp / maxHp;
            UiKit.Text("HP", x + 52, y + 26, 10, UiKit.C("#ffd84a"), bold: true);
            UiKit.Bar(x + 70, y + 27, w - 82, 8, f, UiKit.HpColor(f));
            if (!player) return;
            UiKit.Text($"{Mathf.CeilToInt(dhp)} / {maxHp}", x + w - 10, y + 38, 11, Color.white, TextAnchor.UpperRight);
            UiKit.Bar(x + 10, y + 54, w - 20, 4, (float)exp / Growth.ExpToNext(level), UiKit.C("#58a8f8"));
        }

        /// <summary>조준 중: 지금 각도로 던졌을 때의 궤적을 점으로, 명중 판정 원을 테두리 점으로 보여준다.</summary>
        void DrawAim()
        {
            if (_aimPreview == null) return;
            var path = _aimPreview.Path;
            var dot = _aimPreview.Hit ? UiKit.C("#ffd84a") : UiKit.C("#ffffff");
            for (int i = 0; i < path.Count; i += 5)
                UiKit.Fill((float)path[i].X - 1.5f, -(float)path[i].Y - 1.5f, 3f, 3f, dot);
            for (int a = 0; a < 24; a++)
            {
                float th = a / 24f * Mathf.PI * 2f;
                UiKit.Fill((float)ThrowTarget.X + Mathf.Cos(th) * (float)ThrowRadius - 1f, -(float)ThrowTarget.Y + Mathf.Sin(th) * (float)ThrowRadius - 1f, 2f, 2f, UiKit.C("#ffffff"));
            }
            UiKit.Text("Z: 던지기   X: 취소", 356, 6, 12, Color.white, TextAnchor.UpperCenter, true);
        }

        void DrawBall()
        {
            var pivot = new Vector2(UiKit.Offset.x + _ballPos.x * UiKit.Scale, UiKit.Offset.y + _ballPos.y * UiKit.Scale);
            var saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(_ballRot * Mathf.Rad2Deg, pivot);
            UiKit.Texture(_ball, _ballPos.x - 12, _ballPos.y - 12, 24, 24);
            GUI.matrix = saved;
        }

        /* ---------------------------------- 전투 그림(코드로 생성) ---------------------------------- */

        static void EnsureArt()
        {
            if (_background == null) _background = BuildBackground();
            if (_ball == null) _ball = BuildBall();
        }

        static Texture2D BuildBackground()
        {
            var p = new Painter(480, 232);
            var stops = new[] { (0f, "#8fd0ff"), (.55f, "#dff3ff"), (.56f, "#8fd070"), (1f, "#5aa848") };
            for (int y = 0; y < 232; y++)
            {
                float t = y / 232f;
                int i = 0;
                while (i < stops.Length - 2 && t > stops[i + 1].Item1) i++;
                var (t0, c0) = stops[i]; var (t1, c1) = stops[i + 1];
                var col = Color32.Lerp(Painter.Hex(c0), Painter.Hex(c1), Mathf.Clamp01((t - t0) / (t1 - t0)));
                p.Rect(0, y, 480, 1, col);
            }
            p.Ellipse(355, 138, 88, 17, Painter.Hex("#5ea648")); p.Ellipse(355, 136, 84, 14, Painter.Hex("#78c05a"));
            p.Ellipse(125, 218, 100, 18, Painter.Hex("#5ea648")); p.Ellipse(125, 216, 95, 15, Painter.Hex("#78c05a"));
            var tex = p.ToTexture();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        static Texture2D BuildBall()
        {
            var p = new Painter(24, 24);
            Color32 red = Painter.Hex("#e04040"), white = Painter.Hex("#f4f4f4"), dark = Painter.Hex("#222222");
            for (int y = 0; y < 24; y++)
                for (int x = 0; x < 24; x++)
                {
                    float dx = x + .5f - 12f, dy = y + .5f - 12f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > 11f) continue;
                    p.Plot(x, y, dy < 0 ? red : white);
                    if (Mathf.Abs(dy) <= 1.5f) p.Plot(x, y, dark);
                    if (d <= 5f) p.Plot(x, y, dark);
                    if (d <= 3.6f) p.Plot(x, y, Painter.Hex("#ffffff"));
                }
            return p.ToTexture();
        }
    }
}
