using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class BattleSessionTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        PlayerState State(int starter = 0, int level = 5)
        {
            var s = PlayerState.NewGame(_data, starter);
            s.Party[0] = Monster.Create(_data, starter, level);
            return s;
        }

        /// <summary>한 턴을 끝까지 소비한다. 교체 요청이 나오면 replacement 로 응답한다.</summary>
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

        [Test]
        public void FasterPlayerKillsEnemy_EnemyNeverActs_AndRewardsAreGiven()
        {
            var state = State(0, 30);                                  // 불꼬마 Lv.30 (스피드 42)
            var enemy = Monster.Create(_data, 4, 3);                   // 새싹이 Lv.3 (스피드 7, HP 15)
            var rng = new ScriptedRng(0.0, 0.5, 0.5, 0.0);             // 명중, 급소 아님, 편차, 돈(15)
            var session = new BattleSession(_data, state, enemy, rng);

            var events = Play(session, new MoveAction("ember"));

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.MoveUsed, BattleEventKind.Damage, BattleEventKind.Fainted,
                BattleEventKind.ExpGained, BattleEventKind.MoneyGained,
            }, Kinds(events));
            Assert.AreEqual(2.0, events[1].Multiplier, "불꽃 → 풀 상성");
            Assert.AreEqual(25, events[3].Amount, "경험치 = floor(60 * 3 / 7)");
            Assert.AreEqual(25, state.Party[0].Exp);
            Assert.AreEqual(300 + 15 + 3 * 6, state.Money);
            Assert.AreEqual(BattleOutcome.Win, session.Outcome);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void SlowerPlayerFaints_WithNoBackup_LosesTheBattle()
        {
            var state = State(0, 5);                                   // 스피드 11, HP 19
            var enemy = Monster.Create(_data, 10, 30);                 // 뭉치 Lv.30 (스피드 35): 먼저 움직이고 돌진(93.6점)을 고른다
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5);             // 최고점 기술, 명중, 급소 아님, 편차
            var session = new BattleSession(_data, state, enemy, rng);

            var events = Play(session, new MoveAction("ember"));

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.MoveUsed, BattleEventKind.Damage, BattleEventKind.Fainted, BattleEventKind.BlackedOut,
            }, Kinds(events));
            Assert.AreEqual(Side.Enemy, events[0].Side);
            Assert.AreEqual("rush", events[0].MoveId);
            Assert.AreEqual(BattleOutcome.Lose, session.Outcome);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void FaintedActiveWithBackup_AsksForReplacement_AndSkipsThePlayersMove()
        {
            var state = State(0, 5);
            state.Party.Add(Monster.Create(_data, 0, 20));
            var enemy = Monster.Create(_data, 10, 30);
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5);
            var session = new BattleSession(_data, state, enemy, rng);

            var events = Play(session, new MoveAction("ember"), replacement: 1);

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.MoveUsed, BattleEventKind.Damage, BattleEventKind.Fainted,
                BattleEventKind.ReplacementNeeded, BattleEventKind.SwitchIn,
            }, Kinds(events));
            Assert.AreEqual(BattleOutcome.Ongoing, session.Outcome);
            Assert.AreEqual(1, session.ActiveIndex);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.MoveUsed && e.Side == Side.Player), "교체한 턴에 아군은 공격하지 않는다");
            Assert.AreEqual(state.Party[1].MaxHp, state.Party[1].Hp);
        }

        [Test]
        public void ReplacementNeeded_WithoutChoice_Throws()
        {
            var state = State(0, 5);
            state.Party.Add(Monster.Create(_data, 0, 20));
            var session = new BattleSession(_data, state, Monster.Create(_data, 10, 30), new ScriptedRng(0.0, 0.0, 0.5, 0.5));
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new MoveAction("ember")).ToList());
        }

        [Test]
        public void Ball_Success_CatchesAndRegistersInDex_WithoutEnemyTurn()
        {
            var state = State();
            var enemy = Monster.Create(_data, 6, 5);                   // 찌릿이(catchRate 0.45) → p = 0.26
            var rng = new ScriptedRng(0.1);
            var session = new BattleSession(_data, state, enemy, rng);

            var events = Play(session, new BallAction());

            CollectionAssert.AreEqual(new[] { BattleEventKind.BallThrown, BattleEventKind.CatchAttempt, BattleEventKind.Caught }, Kinds(events));
            Assert.IsTrue(events[1].Catch.Caught);
            Assert.AreEqual(3, events[1].Catch.Shakes);
            Assert.AreEqual(BattleOutcome.Caught, session.Outcome);
            Assert.AreEqual(4, state.Balls);
            Assert.AreEqual(2, state.Party.Count);
            Assert.IsTrue(state.Dex.Contains(6));
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Ball_Failure_LetsEnemyAttack()
        {
            var state = State(2, 5);                                   // 물방울이
            var enemy = Monster.Create(_data, 6, 5);                   // 찌릿이: 전기쇼크(전기 → 물 2배)
            var rng = new ScriptedRng(0.9, 0.5, 0.0, 0.0, 0.5, 0.5);   // 포획 실패, 흔들림 1회, 최고점 기술, 명중, 급소 아님, 편차
            var session = new BattleSession(_data, state, enemy, rng);

            var events = Play(session, new BallAction());

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.BallThrown, BattleEventKind.CatchAttempt, BattleEventKind.MoveUsed, BattleEventKind.Damage,
            }, Kinds(events));
            Assert.IsFalse(events[1].Catch.Caught);
            Assert.AreEqual(1, events[1].Catch.Shakes);
            Assert.AreEqual("shock", events[2].MoveId);
            Assert.AreEqual(BattleOutcome.Ongoing, session.Outcome);
            Assert.AreEqual(4, state.Balls);
            Assert.AreEqual(1, state.Party.Count);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Flee_Success_EndsBattleImmediately()
        {
            var state = State();                                       // 스피드 11
            var session = new BattleSession(_data, state, Monster.Create(_data, 10, 5), new ScriptedRng(0.0));   // 적 스피드 10
            var events = Play(session, new FleeAction());
            CollectionAssert.AreEqual(new[] { BattleEventKind.FleeSucceeded }, Kinds(events));
            Assert.AreEqual(BattleOutcome.Fled, session.Outcome);
        }

        [Test]
        public void Flee_Failure_LetsEnemyAttack_AndCountsTries()
        {
            var state = State();
            var rng = new ScriptedRng(0.99, 0.0, 0.0, 0.5, 0.5);       // 도망 실패, 이어서 적 공격
            var session = new BattleSession(_data, state, Monster.Create(_data, 10, 5), rng);
            var events = Play(session, new FleeAction());
            CollectionAssert.AreEqual(new[] { BattleEventKind.FleeFailed, BattleEventKind.MoveUsed, BattleEventKind.Damage }, Kinds(events));
            Assert.AreEqual(1, session.FleeTries);
            Assert.AreEqual(BattleOutcome.Ongoing, session.Outcome);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Potion_HealsUpToMax_ThenEnemyAttacks()
        {
            var state = State();
            state.Party[0].Hp = 5;                                     // 최대 19
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5);
            var session = new BattleSession(_data, state, Monster.Create(_data, 10, 5), rng);
            var events = Play(session, new PotionAction(0));
            CollectionAssert.AreEqual(new[] { BattleEventKind.PotionUsed, BattleEventKind.MoveUsed, BattleEventKind.Damage }, Kinds(events));
            Assert.AreEqual(14, events[0].Amount, "5 → 19(최대)라서 실제 회복량은 14");
            Assert.AreEqual(2, state.Potions);
            Assert.AreEqual(19 - events[2].Amount, state.Party[0].Hp);
        }

        [Test]
        public void Switch_ReplacesActive_AndTheNewOneTakesTheHit()
        {
            var state = State();
            state.Party.Add(Monster.Create(_data, 2, 5));
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5);
            var session = new BattleSession(_data, state, Monster.Create(_data, 10, 5), rng);
            var events = Play(session, new SwitchAction(1));
            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.SwitchOut, BattleEventKind.SwitchIn, BattleEventKind.MoveUsed, BattleEventKind.Damage,
            }, Kinds(events));
            Assert.AreEqual(1, session.ActiveIndex);
            Assert.AreEqual(state.Party[0].MaxHp, state.Party[0].Hp);
            Assert.Less(state.Party[1].Hp, state.Party[1].MaxHp);
        }

        [Test]
        public void Winning_CanLevelUpTheActiveMonster()
        {
            var state = State();
            state.Party[0].Exp = Growth.ExpToNext(5) - 1;
            var enemy = Monster.Create(_data, 4, 2);                   // HP 13, 불꽃 한 방(16)이면 쓰러진다
            var session = new BattleSession(_data, state, enemy, new ScriptedRng(0.0, 0.5, 0.5, 0.0));
            var events = Play(session, new MoveAction("ember"));
            var growth = events.Where(e => e.Kind == BattleEventKind.Growth).Select(e => e.Growth).ToList();
            Assert.AreEqual(1, growth.Count);
            Assert.AreEqual(GrowthKind.LevelUp, growth[0].Kind);
            Assert.AreEqual(6, growth[0].Level);
            Assert.AreEqual(6, state.Party[0].Level);
            Assert.AreEqual(BattleOutcome.Win, session.Outcome);
        }

        [Test]
        public void InvalidActions_AreRejected()
        {
            var state = State();
            var session = new BattleSession(_data, state, Monster.Create(_data, 10, 5), new ScriptedRng());
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new PotionAction(0)).ToList(), "HP 가득");
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new MoveAction("blast")).ToList(), "배우지 않은 기술");
            state.Balls = 0;
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new BallAction()).ToList(), "볼 없음");
        }

        [Test]
        public void Defeat_HalvesMoney_HealsEveryone_AndReturnsToTown()
        {
            var state = State();
            state.Money = 301;
            state.Party[0].Hp = 0;
            state.X = 40; state.Y = 30;
            state.ApplyDefeat();
            Assert.AreEqual(150, state.Money);
            Assert.AreEqual(state.Party[0].MaxHp, state.Party[0].Hp);
            Assert.AreEqual((WorldMap.VillageX, WorldMap.VillageY + 1), (state.X, state.Y));
        }
    }
}
