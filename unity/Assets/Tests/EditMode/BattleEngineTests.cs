using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>우선도 큐 턴 순서 · 옵저버(날씨/특성/도구) · 배틀 FSM.</summary>
    public class BattleEngineTests
    {
        /// <summary>항상 같은 값을 돌려주는 난수원. 0.5 면 명중·급소 아님·편차 중간·동률에서 후공.</summary>
        sealed class ConstRng : IRng
        {
            readonly double _v;
            public ConstRng(double v) { _v = v; }
            public double NextDouble() => _v;
        }

        string _json;
        GameData _real;

        [OneTimeSetUp]
        public void Load()
        {
            _json = File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json"));
            _real = GameData.Parse(_json);
        }

        /// <summary>실제 데이터에 우선도를 덧씌운 합성 GameData(원본 JSON 은 건드리지 않는다).</summary>
        GameData WithPriority(params (string id, int priority)[] moves)
        {
            var j = JObject.Parse(_json);
            foreach (var m in (JArray)j["moves"])
                foreach (var (id, pri) in moves)
                    if ((string)m["id"] == id) m["priority"] = pri;
            return GameData.Parse(j.ToString());
        }

        static void Big(Monster m) { m.MaxHp = 500; m.Hp = 500; }

        /// <summary>플레이어(선두 1마리)와 적을 만든다. 둘 다 HP 500, 적은 몸통박치기만 쓴다.</summary>
        (BattleSession session, PlayerState state, Monster enemy) Setup(GameData data, int playerSpecies, int enemySpecies, IRng rng, int level = 5)
        {
            var state = PlayerState.NewGame(data, playerSpecies);
            state.Party[0] = Monster.Create(data, playerSpecies, level);
            Big(state.Party[0]);
            var enemy = Monster.Create(data, enemySpecies, level);
            Big(enemy);
            enemy.Moves = new List<string> { "tackle" };
            return (new BattleSession(data, state, enemy, rng), state, enemy);
        }

        static List<BattleEvent> Play(BattleSession session, BattleAction action, int replacement = -1)
        {
            var events = new List<BattleEvent>();
            foreach (var e in session.ResolveTurn(action))
            {
                events.Add(e);
                if (e.Kind == BattleEventKind.ReplacementNeeded) session.ChooseReplacement(replacement);
            }
            return events;
        }

        static BattleEventKind[] Kinds(IEnumerable<BattleEvent> events) => events.Select(e => e.Kind).ToArray();

        /* ------------------------------ 턴 순서 큐 ------------------------------ */

        [Test]
        public void TurnQueue_OrdersByPriorityThenSpeed_WithoutConsumingRandomness()
        {
            var q = new TurnQueue<string>(new ScriptedRng());   // 난수를 쓰면 예외 → 동률이 없으면 안 쓴다는 증명
            q.Enqueue("느린 보통", 0, 5);
            q.Enqueue("느린 선제", 1, 1);
            q.Enqueue("빠른 보통", 0, 9);
            Assert.AreEqual("느린 선제", q.Dequeue());
            Assert.AreEqual("빠른 보통", q.Dequeue());
            Assert.AreEqual("느린 보통", q.Dequeue());
            Assert.AreEqual(0, q.Count);
        }

        [Test]
        public void TurnQueue_Tie_IsFiftyFifty_FirstEnqueuedWinsBelowHalf()
        {
            var a = new TurnQueue<string>(new ScriptedRng(0.49));
            a.Enqueue("먼저", 0, 10); a.Enqueue("나중", 0, 10);
            Assert.AreEqual("먼저", a.Dequeue());

            var b = new TurnQueue<string>(new ScriptedRng(0.5));
            b.Enqueue("먼저", 0, 10); b.Enqueue("나중", 0, 10);
            Assert.AreEqual("나중", b.Dequeue());
        }

        [Test]
        public void TurnQueue_ThreeWayTie_IsAPermutation_AndDeterministic()
        {
            List<string> Run(params double[] rolls)
            {
                var q = new TurnQueue<string>(new ScriptedRng(rolls));
                q.Enqueue("a", 0, 7); q.Enqueue("b", 0, 7); q.Enqueue("c", 0, 7); q.Enqueue("z", 0, 99);
                var order = new List<string>();
                while (q.Count > 0) order.Add(q.Dequeue());
                return order;
            }
            var one = Run(0.1, 0.9);
            Assert.AreEqual("z", one[0]);
            CollectionAssert.AreEquivalent(new[] { "a", "b", "c", "z" }, one);
            CollectionAssert.AreEqual(one, Run(0.1, 0.9));
        }

        [Test]
        public void Data_HasPriorityMoves_OnlyWhenSomeMoveHasPriority()
        {
            Assert.IsFalse(_real.HasPriorityMoves, "실제 데이터는 우선도가 전부 0");
            Assert.IsTrue(_real.Moves.All(m => m.Priority == 0));
            var data = WithPriority(("tackle", 1));
            Assert.IsTrue(data.HasPriorityMoves);
            Assert.AreEqual(1, data.GetMove("tackle").Priority);
            Assert.AreEqual(0, data.GetMove("ember").Priority);
        }

        /* ------------------------------ 세션: 우선도 ------------------------------ */

        [Test]
        public void Priority_BeatsSpeed_SlowPlayerMovesFirst()
        {
            var data = WithPriority(("tackle", 1));
            var state = PlayerState.NewGame(data, 0);                       // 불꼬마 Lv5 (스피드 11)
            state.Party[0].Moves = new List<string> { "tackle" };
            var enemy = Monster.Create(data, 10, 30);                       // 뭉치 Lv30 (스피드 35)
            enemy.Moves = new List<string> { "rush" };
            // 적 기술 선택, [아군 명중·급소·편차], [적 명중·급소·편차] — 우선도가 다르면 동률 난수는 안 쓴다.
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5, 0.0, 0.5, 0.5);
            var session = new BattleSession(data, state, enemy, rng);

            var events = Play(session, new MoveAction("tackle"));

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.MoveUsed, BattleEventKind.Damage, BattleEventKind.MoveUsed, BattleEventKind.Damage,
                BattleEventKind.Fainted, BattleEventKind.BlackedOut,
            }, Kinds(events));
            Assert.AreEqual(Side.Player, events[0].Side, "우선도 덕에 느린 쪽이 선공");
            Assert.AreEqual(Side.Enemy, events[2].Side);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void SamePriority_FallsBackToSpeed_FasterEnemyKillsFirst()
        {
            var data = WithPriority(("tackle", 1));                          // 우선도 기술이 데이터에 있어도 이번 턴엔 둘 다 0
            var state = PlayerState.NewGame(data, 0);
            var enemy = Monster.Create(data, 10, 30);
            enemy.Moves = new List<string> { "rush" };
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5);                   // 적 기술 선택, 적 공격 3굴림 — 아군은 못 움직인다
            var session = new BattleSession(data, state, enemy, rng);

            var events = Play(session, new MoveAction("ember"));

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.MoveUsed, BattleEventKind.Damage, BattleEventKind.Fainted, BattleEventKind.BlackedOut,
            }, Kinds(events));
            Assert.AreEqual(Side.Enemy, events[0].Side);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void EnemyPriorityMove_LetsSlowerEnemyGoFirst()
        {
            var data = WithPriority(("tackle", 1));
            var (session, _, _) = Setup(data, 0, 10, new ScriptedRng(0.0, 0.0, 0.5, 0.5, 0.0, 0.5, 0.5));   // 적 스피드 10 < 아군 11
            var events = Play(session, new MoveAction("ember"));
            var used = events.Where(e => e.Kind == BattleEventKind.MoveUsed).Select(e => e.Side).ToArray();
            CollectionAssert.AreEqual(new[] { Side.Enemy, Side.Player }, used);
        }

        [Test]
        public void Tie_UsesFiftyFifty_AfterTheEnemyPicksItsMove()
        {
            var data = WithPriority(("tackle", 1));
            // 굴림 순서: 적 기술 선택(0.0) → 동률 50:50(r) → 첫 공격 3굴림 → 둘째 공격 3굴림.
            Side[] Order(double r)
            {
                var (session, state, _) = Setup(data, 10, 10, new ScriptedRng(0.0, r, 0.0, 0.5, 0.5, 0.0, 0.5, 0.5));
                state.Party[0].Moves = new List<string> { "tackle" };
                return Play(session, new MoveAction("tackle")).Where(e => e.Kind == BattleEventKind.MoveUsed).Select(e => e.Side).ToArray();
            }
            CollectionAssert.AreEqual(new[] { Side.Player, Side.Enemy }, Order(0.2));
            CollectionAssert.AreEqual(new[] { Side.Enemy, Side.Player }, Order(0.8),
                "선택 난수(0.0)가 순서 난수보다 먼저 소비돼야 이 굴림이 나온다");
        }

        [Test]
        public void Tie_ConsumesExactlyOneRandom_WhenDataHasPriority()
        {
            var data = WithPriority(("tackle", 1));
            var rng = new ScriptedRng(0.0, 0.2, 0.0, 0.5, 0.5, 0.0, 0.5, 0.5);
            var (session, _, _) = Setup(data, 10, 10, rng);
            Play(session, new MoveAction("tackle"));
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void NonMoveActions_StillRunFirst_ThenEnemyAttacks_EvenWithPriorityData()
        {
            var data = WithPriority(("tackle", 1));
            var (session, state, _) = Setup(data, 0, 10, new ScriptedRng(0.0, 0.0, 0.5, 0.5));
            state.Party[0].Hp = 100;
            var events = Play(session, new PotionAction(0));
            CollectionAssert.AreEqual(new[] { BattleEventKind.PotionUsed, BattleEventKind.MoveUsed, BattleEventKind.Damage }, Kinds(events));
        }

        [Test]
        public void Duel_Priority_BeatsSpeed()
        {
            var data = WithPriority(("tackle", 1));
            var a = Monster.Create(data, 0, 5);                            // 스피드 11
            var b = Monster.Create(data, 10, 30);                          // 스피드 35
            var rng = new ScriptedRng(0.0, 0.5, 0.5, 0.0, 0.5, 0.5);       // 우선도가 다르면 순서 난수 없음
            var duel = new Duel(data, a, b, rng);

            var events = duel.ResolveRound("tackle", "rush").ToList();

            Assert.AreEqual(DuelEventKind.MoveUsed, events[0].Kind);
            Assert.AreEqual(DuelSide.A, events[0].Side, "느린 A 가 우선도로 선공");
            Assert.AreEqual(DuelSide.B, events[2].Side);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Duel_Tie_IsFiftyFifty_WhenDataHasPriority()
        {
            var data = WithPriority(("tackle", 1));
            DuelSide First(double r)
            {
                var a = Monster.Create(data, 10, 5); var b = Monster.Create(data, 10, 5);
                Big(a); Big(b);
                var duel = new Duel(data, a, b, new ScriptedRng(r, 0.0, 0.5, 0.5, 0.0, 0.5, 0.5));
                return duel.ResolveRound("tackle", "tackle").First().Side;
            }
            Assert.AreEqual(DuelSide.A, First(0.3));
            Assert.AreEqual(DuelSide.B, First(0.7));
        }

        /* ------------------------------ 옵저버 훅 ------------------------------ */

        sealed class Recorder : BattleObserver
        {
            public readonly List<string> Log = new List<string>();
            public override void OnBattleStart(BattleSession s, List<BattleEvent> emit) => Log.Add("start");
            public override void OnTurnStart(BattleSession s, List<BattleEvent> emit) => Log.Add("turnStart");
            public override void OnMoveUsed(BattleSession s, Side side, MoveData move, List<BattleEvent> emit) => Log.Add($"used:{side}:{move.Id}");
            public override void OnModifyDamage(DamageContext ctx) => Log.Add($"modify:{ctx.AttackerSide}");
            public override void OnDamageDealt(BattleSession s, Side side, MoveData move, int amount, List<BattleEvent> emit) => Log.Add($"dealt:{side}");
            public override void OnFaint(BattleSession s, Side side, List<BattleEvent> emit) => Log.Add($"faint:{side}");
            public override void OnTurnEnd(BattleSession s, List<BattleEvent> emit) => Log.Add("turnEnd");
        }

        sealed class Mult : BattleObserver
        {
            readonly double _m;
            public Mult(double m) { _m = m; }
            public override void OnModifyDamage(DamageContext ctx) => ctx.Multiply(_m);
        }

        [Test]
        public void Observers_AreCalledInTurnOrder()
        {
            var (session, _, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            var rec = new Recorder();
            session.Observers.Add(rec);
            Play(session, new MoveAction("ember"));
            CollectionAssert.AreEqual(new[]
            {
                "start", "turnStart",
                "used:Player:ember", "modify:Player", "dealt:Player",
                "used:Enemy:tackle", "modify:Enemy", "dealt:Enemy",
                "turnEnd",
            }, rec.Log);
        }

        [Test]
        public void Observers_FaintHook_RunsAndTurnEndIsSkippedWhenBattleEnds()
        {
            var (session, _, enemy) = Setup(_real, 0, 10, new ConstRng(0.5));
            enemy.Hp = 1;
            var rec = new Recorder();
            session.Observers.Add(rec);
            Play(session, new MoveAction("ember"));
            CollectionAssert.AreEqual(new[] { "start", "turnStart", "used:Player:ember", "modify:Player", "dealt:Player", "faint:Enemy" }, rec.Log);
        }

        [Test]
        public void EmptyOrNoOpObservers_DoNotChangeEventsOrRandomConsumption()
        {
            List<BattleEventKind> Run(bool withObserver, out int remaining)
            {
                var state = PlayerState.NewGame(_real, 0);
                state.Party[0] = Monster.Create(_real, 0, 30);
                var enemy = Monster.Create(_real, 4, 3);
                var rng = new ScriptedRng(0.0, 0.5, 0.5, 0.0);
                var s = new BattleSession(_real, state, enemy, rng);
                if (withObserver) s.Observers.Add(new Mult(1.0));     // 배율 1 짜리 관찰자
                var kinds = Play(s, new MoveAction("ember")).Select(e => e.Kind).ToList();
                remaining = rng.Remaining;
                return kinds;
            }
            var a = Run(false, out int ra);
            var b = Run(true, out int rb);
            CollectionAssert.AreEqual(a, b);
            Assert.AreEqual(0, ra); Assert.AreEqual(0, rb);
        }

        [Test]
        public void ModifyDamage_ChainsMultipliers_AndZeroMakesImmune()
        {
            int Damage(params BattleObserver[] obs)
            {
                var (session, _, enemy) = Setup(_real, 0, 10, new ConstRng(0.5));
                foreach (var o in obs) session.Observers.Add(o);
                var d = Play(session, new MoveAction("ember")).First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy);
                Assert.AreEqual(500 - d.Amount, enemy.Hp);
                return d.Amount;
            }
            int baseDmg = Damage();
            Assert.AreEqual((int)Math.Floor(baseDmg * 0.75), Damage(new Mult(1.5), new Mult(0.5)));
            Assert.AreEqual(0, Damage(new Mult(0.0)));
            Assert.AreEqual(1, Damage(new Mult(0.0001), new Mult(1.0)), "0 이 아니면 최소 1");
        }

        /* ------------------------------ 날씨 ------------------------------ */

        int EmberDamage(WeatherKind kind)
        {
            var (session, _, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            if (kind != WeatherKind.None) session.Observers.Add(new WeatherObserver(kind, 5));
            return Play(session, new MoveAction("ember")).First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy).Amount;
        }

        [Test]
        public void Weather_Sunny_BoostsFire_Rain_WeakensFire()
        {
            int baseDmg = EmberDamage(WeatherKind.None);
            Assert.AreEqual((int)Math.Floor(baseDmg * 1.5), EmberDamage(WeatherKind.Sunny));
            Assert.AreEqual((int)Math.Floor(baseDmg * 0.5), EmberDamage(WeatherKind.Rain));
            Assert.AreEqual(baseDmg, EmberDamage(WeatherKind.Sandstorm), "모래바람은 기술 배율을 안 바꾼다");
        }

        [Test]
        public void Weather_TypeTable()
        {
            Assert.AreEqual(1.5, Weather.MoveMultiplier(WeatherKind.Rain, "water"));
            Assert.AreEqual(0.5, Weather.MoveMultiplier(WeatherKind.Rain, "fire"));
            Assert.AreEqual(1.5, Weather.MoveMultiplier(WeatherKind.Sunny, "fire"));
            Assert.AreEqual(0.5, Weather.MoveMultiplier(WeatherKind.Sunny, "water"));
            Assert.AreEqual(1.0, Weather.MoveMultiplier(WeatherKind.Rain, "grass"));
            Assert.AreEqual(0, Weather.ChipDamage(WeatherKind.Sandstorm, "rock", 100));
            Assert.AreEqual(6, Weather.ChipDamage(WeatherKind.Sandstorm, "fire", 100));
            Assert.AreEqual(1, Weather.ChipDamage(WeatherKind.Sandstorm, "fire", 5), "최소 1");
        }

        [Test]
        public void Weather_LastsNTurns_ThenEndsWithEvent_AndDetaches()
        {
            var (session, _, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            var weather = new WeatherObserver(WeatherKind.Rain, 2);
            session.Observers.Add(weather);

            var t1 = Play(session, new MoveAction("ember"));
            Assert.AreEqual(BattleEventKind.WeatherStarted, t1[0].Kind);
            Assert.AreEqual("rain", t1[0].Name);
            Assert.AreEqual(2, t1[0].Amount);
            Assert.IsFalse(string.IsNullOrEmpty(t1[0].Text));
            Assert.IsFalse(t1.Any(e => e.Kind == BattleEventKind.WeatherEnded));
            Assert.AreEqual(1, weather.TurnsLeft);

            var t2 = Play(session, new MoveAction("ember"));
            Assert.AreEqual(BattleEventKind.WeatherEnded, t2.Last().Kind);
            Assert.AreEqual(0, session.Observers.Count);

            var t3 = Play(session, new MoveAction("ember"));
            Assert.IsFalse(t3.Any(e => e.Kind == BattleEventKind.WeatherStarted || e.Kind == BattleEventKind.WeatherEnded));
            Assert.AreEqual(BattleEventKind.MoveUsed, t3[0].Kind);
        }

        [Test]
        public void Weather_Begin_EmitsStartBeforeFirstAction_AndOnlyOnce()
        {
            var (session, _, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            session.Observers.Add(new WeatherObserver(WeatherKind.Sunny, 3));
            var begin = session.Begin().ToList();
            Assert.AreEqual(new[] { BattleEventKind.WeatherStarted }, Kinds(begin));
            var turn = Play(session, new MoveAction("ember"));
            Assert.IsFalse(turn.Any(e => e.Kind == BattleEventKind.WeatherStarted));
        }

        [Test]
        public void Sandstorm_ChipsEveryoneButRockTypes_AtTurnEnd()
        {
            var (session, state, enemy) = Setup(_real, 0, 8, new ConstRng(0.5));   // 불꼬마 vs 바위 종족
            Assert.AreEqual("rock", enemy.Species(_real).Type);
            session.Observers.Add(new WeatherObserver(WeatherKind.Sandstorm, 3));

            var events = Play(session, new MoveAction("ember"));
            var chips = events.Where(e => e.Kind == BattleEventKind.ResidualDamage).ToList();

            Assert.AreEqual(1, chips.Count, "바위 타입은 면역");
            Assert.AreEqual(Side.Player, chips[0].Side);
            Assert.AreEqual(500 / 16, chips[0].Amount);
            Assert.AreEqual("sandstorm", chips[0].Name);
            Assert.AreEqual(BattleEventKind.ResidualDamage, events.Last().Kind, "턴 끝 피해가 마지막 이벤트");
            Assert.AreEqual(500 - chips[0].Amount - events.First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Player).Amount,
                state.Party[0].Hp);
        }

        [Test]
        public void Sandstorm_CanFaintEnemyAtTurnEnd_AndPlayerWins()
        {
            var (session, state, enemy) = Setup(_real, 0, 10, new ConstRng(0.5));
            state.Party[0].Hp = 400;
            enemy.Hp = 1;
            session.Observers.Add(new WeatherObserver(WeatherKind.Sandstorm, 3));

            var events = Play(session, new PotionAction(0));   // 물약 → 적 공격 → 턴 끝 모래바람이 적을 쓰러뜨린다

            int chipEnemy = events.FindIndex(e => e.Kind == BattleEventKind.ResidualDamage && e.Side == Side.Enemy);
            Assert.GreaterOrEqual(chipEnemy, 0);
            Assert.AreEqual(BattleEventKind.Fainted, events[chipEnemy + 1].Kind);
            Assert.AreEqual(Side.Enemy, events[chipEnemy + 1].Side);
            Assert.AreEqual(BattleOutcome.Win, session.Outcome);
            Assert.AreEqual(BattlePhase.Ended, session.Phase);
            Assert.AreEqual(1, events[chipEnemy].Amount, "실제로 깎인 양(남은 HP 1)");
        }

        /* ------------------------------ 특성 · 도구 ------------------------------ */

        [Test]
        public void LowHpAbility_BoostsMatchingType_OnlyAtOneThirdOrLess()
        {
            int Ember(int hp, string ability = "blaze", bool attach = true)
            {
                var (session, state, _) = Setup(_real, 0, 10, new ConstRng(0.5));
                state.Party[0].Hp = hp;
                if (attach) session.AddObserver(Side.Player, new LowHpTypeBoostAbility(ability, "fire"));
                return Play(session, new MoveAction("ember")).First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy).Amount;
            }
            int baseDmg = Ember(500, attach: false);
            Assert.AreEqual(baseDmg, Ember(167), "167/500 는 1/3 초과");
            Assert.AreEqual((int)Math.Floor(baseDmg * 1.5), Ember(166), "166/500 는 1/3 이하");
            Assert.AreEqual(baseDmg, Ember(500));
        }

        [Test]
        public void LowHpAbility_EmitsNoticeAfterDamage_AndIgnoresOtherTypesAndSides()
        {
            var (session, state, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            state.Party[0].Hp = 100;
            session.AddObserver(Side.Player, new LowHpTypeBoostAbility("blaze", "fire"));
            var events = Play(session, new MoveAction("ember"));
            int dmg = events.FindIndex(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy);
            Assert.AreEqual(BattleEventKind.ObserverNotice, events[dmg + 1].Kind);
            Assert.AreEqual("blaze", events[dmg + 1].Name);
            Assert.AreEqual(Side.Player, events[dmg + 1].Side);
            Assert.AreEqual(1, events.Count(e => e.Kind == BattleEventKind.ObserverNotice), "적의 몸통박치기엔 발동 안 함");
        }

        [Test]
        public void HolderAbility_TurnsOffWhenTheHolderIsNotActive()
        {
            var (session, state, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            var bench = Monster.Create(_real, 0, 5); Big(bench);
            state.Party.Add(bench);
            state.Party[0].Hp = 100;
            var baseSession = Setup(_real, 0, 10, new ConstRng(0.5));
            int baseDmg = Play(baseSession.session, new MoveAction("ember")).First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy).Amount;

            session.AddObserver(Side.Player, new LowHpTypeBoostAbility("blaze", "fire"), holder: bench);   // 벤치 몬스터의 특성
            int dmg = Play(session, new MoveAction("ember")).First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy).Amount;
            Assert.AreEqual(baseDmg, dmg, "출전 중이 아닌 몬스터의 특성은 안 걸린다");
        }

        [Test]
        public void AbsorbAbility_NullifiesTypeAndHeals()
        {
            var (session, _, enemy) = Setup(_real, 0, 10, new ConstRng(0.5));
            enemy.Hp = 300;
            session.AddObserver(Side.Enemy, new TypeAbsorbAbility("fireabsorb", "fire", 4));

            var events = Play(session, new MoveAction("ember"));

            var dmg = events.First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy);
            Assert.AreEqual(0, dmg.Amount);
            var heal = events.First(e => e.Kind == BattleEventKind.ResidualHeal);
            Assert.AreEqual(Side.Enemy, heal.Side);
            Assert.AreEqual(125, heal.Amount);
            Assert.AreEqual(425, enemy.Hp);
            Assert.AreEqual("fireabsorb", heal.Name);
            Assert.Less(events.IndexOf(dmg), events.IndexOf(heal));
        }

        [Test]
        public void Leftovers_HealsAtTurnEnd_ButNotWhenFull()
        {
            var (session, state, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            state.Party[0].Hp = 300;
            session.AddObserver(Side.Player, new LeftoversItem());

            var events = Play(session, new MoveAction("ember"));
            var heal = events.Last();
            var hitOnPlayer = events.First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Player);
            Assert.AreEqual(BattleEventKind.ResidualHeal, heal.Kind);
            Assert.AreEqual(500 / 16, heal.Amount);
            Assert.AreEqual(300 - hitOnPlayer.Amount + 500 / 16, state.Party[0].Hp);

        }

        [Test]
        public void TypeBoostItem_And_Weather_Stack()
        {
            int Dmg(bool item, bool sun)
            {
                var (session, _, _) = Setup(_real, 0, 10, new ConstRng(0.5));
                if (sun) session.Observers.Add(new WeatherObserver(WeatherKind.Sunny, 3));
                if (item) session.AddObserver(Side.Player, new TypeBoostItem("charcoal", "fire", 1.5));
                return Play(session, new MoveAction("ember")).First(e => e.Kind == BattleEventKind.Damage && e.Side == Side.Enemy).Amount;
            }
            int baseDmg = Dmg(false, false);
            Assert.AreEqual((int)Math.Floor(baseDmg * 1.5), Dmg(true, false));
            Assert.AreEqual((int)Math.Floor(baseDmg * 2.25), Dmg(true, true));
        }

        /* ------------------------------ FSM ------------------------------ */

        [Test]
        public void Phase_WalksThroughTheTurn_AndEndsWithTheBattle()
        {
            var (session, _, enemy) = Setup(_real, 0, 10, new ConstRng(0.5));
            Assert.AreEqual(BattlePhase.AwaitingAction, session.Phase);
            var it = session.ResolveTurn(new MoveAction("ember")).GetEnumerator();
            Assert.IsTrue(it.MoveNext());
            Assert.AreEqual(BattlePhase.ResolvingTurn, session.Phase);
            while (it.MoveNext()) { }
            Assert.AreEqual(BattlePhase.AwaitingAction, session.Phase);
            Assert.AreEqual(1, session.Turn);

            enemy.Hp = 1;
            Play(session, new MoveAction("ember"));
            Assert.AreEqual(BattlePhase.Ended, session.Phase);
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new MoveAction("ember")).ToList());
        }

        [Test]
        public void Phase_RejectsStartingATurnWhileAnotherIsStillBeingResolved()
        {
            var (session, _, _) = Setup(_real, 0, 10, new ConstRng(0.5));
            var it = session.ResolveTurn(new MoveAction("ember")).GetEnumerator();
            it.MoveNext();
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new MoveAction("ember")).GetEnumerator().MoveNext());
        }

        [Test]
        public void Phase_ReplacementFlow_AndChooseReplacementOnlyWhenAsked()
        {
            var state = PlayerState.NewGame(_real, 0);
            state.Party[0] = Monster.Create(_real, 0, 5);
            state.Party.Add(Monster.Create(_real, 0, 20));
            var enemy = Monster.Create(_real, 10, 30);
            var session = new BattleSession(_real, state, enemy, new ScriptedRng(0.0, 0.0, 0.5, 0.5));

            Assert.Throws<InvalidOperationException>(() => session.ChooseReplacement(1), "묻기 전에는 못 고른다");

            foreach (var e in session.ResolveTurn(new MoveAction("ember")))
            {
                if (e.Kind == BattleEventKind.ReplacementNeeded)
                {
                    Assert.AreEqual(BattlePhase.AwaitingReplacement, session.Phase);
                    Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new MoveAction("ember")).ToList());
                    session.ChooseReplacement(1);
                }
            }
            Assert.AreEqual(BattlePhase.AwaitingAction, session.Phase);
            Assert.Throws<InvalidOperationException>(() => session.ChooseReplacement(1), "이미 교체가 끝났다");
        }
    }
}
