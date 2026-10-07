using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class PvpBattleTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        static PvpEventKind[] Kinds(IEnumerable<PvpEvent> events) => events.Select(e => e.Kind).ToArray();
        static Monster[] Party(params Monster[] m) => m;

        PvpBattle Battle(Monster[] a, Monster[] b, IRng rng, int potions = PvpBattle.StartPotions) => new PvpBattle(_data, a, b, rng, potions);

        [Test]
        public void FasterSideActsFirst_AndDealsExpectedDamage()
        {
            var a = Monster.Create(_data, 0, 20);   // 불꼬마(스피드 29)
            var b = Monster.Create(_data, 4, 20);   // 새싹이(스피드 23) — 불꽃이 유리한 상대
            var rng = new ScriptedRng(0.0, 0.5, 0.5, 0.0, 0.5, 0.5);
            var battle = Battle(Party(a), Party(b), rng);

            var events = battle.ResolveRound(new MoveAction("ember"), new MoveAction("vine"));

            CollectionAssert.AreEqual(new[] { PvpEventKind.MoveUsed, PvpEventKind.Damage, PvpEventKind.MoveUsed, PvpEventKind.Damage }, Kinds(events));
            Assert.AreEqual(DuelSide.A, events[0].Side, "스피드가 빠른 A 가 먼저");
            Assert.AreEqual(2.0, events[1].Multiplier, "불꽃 → 풀 상성");
            Assert.AreEqual(DuelSide.B, events[1].Side, "Damage 의 Side 는 맞은 쪽");
            Assert.IsFalse(battle.IsOver);
            Assert.AreEqual(PvpPhase.AwaitingActions, battle.Phase);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void FaintedMonster_NeverGetsToAct_AndRequestsReplacement()
        {
            var a = Monster.Create(_data, 4, 2);     // 약하고 느린 A
            var a2 = Monster.Create(_data, 4, 10);
            var b = Monster.Create(_data, 15, 30);   // 강하고 빠른 B
            var rng = new ScriptedRng(0.0, 0.5, 0.5);   // B 의 공격 한 번분만(A 는 기절해서 굴림 없음)
            var battle = Battle(Party(a, a2), Party(b), rng);

            var events = battle.ResolveRound(new MoveAction("tackle"), new MoveAction("tackle"));

            CollectionAssert.AreEqual(new[]
            {
                PvpEventKind.MoveUsed, PvpEventKind.Damage, PvpEventKind.Fainted, PvpEventKind.ReplacementNeeded,
            }, Kinds(events));
            Assert.AreEqual(DuelSide.B, events[0].Side);
            Assert.AreEqual(DuelSide.A, events[2].Side);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Potion_HealsOnlyWhatIsMissing_AndAppearsBeforeTheAttack()
        {
            var a = Monster.Create(_data, 4, 20);
            var b = Monster.Create(_data, 0, 20);
            a.Hp = a.MaxHp - 10;
            var battle = Battle(Party(a), Party(b), new ScriptedRng(0.0, 0.5, 0.5));

            var events = battle.ResolveRound(new PotionAction(0), new MoveAction("tackle"));

            CollectionAssert.AreEqual(new[] { PvpEventKind.PotionUsed, PvpEventKind.MoveUsed, PvpEventKind.Damage }, Kinds(events));
            Assert.AreEqual(10, events[0].Amount, "30 이 아니라 실제로 차오른 만큼");
            Assert.AreEqual(a.MaxHp, events[0].Hp, "이벤트에는 회복 직후의 HP 가 실린다");
            Assert.IsTrue(events[0].TargetActive);
            Assert.AreEqual(2, battle.Potions(DuelSide.A));
            Assert.AreEqual(3, battle.Potions(DuelSide.B));
        }

        [Test]
        public void Potion_HealsFullPotionAmount_AndCanTargetABenchedMonster()
        {
            var a = Monster.Create(_data, 4, 20);
            var bench = Monster.Create(_data, 0, 20);
            bench.Hp = 1;
            var b = Monster.Create(_data, 0, 20);
            var battle = Battle(Party(a, bench), Party(b), new ScriptedRng(0.0, 0.5, 0.5));

            var events = battle.ResolveRound(new PotionAction(1), new MoveAction("tackle"));

            Assert.AreEqual(PlayerState.PotionHeal, events[0].Amount);
            Assert.AreEqual(1 + PlayerState.PotionHeal, bench.Hp);
            Assert.IsFalse(events[0].TargetActive, "출전 중이 아닌 몬스터에게 쓴 상처약");
            Assert.AreEqual(1, events[0].PartyIndex);
        }

        [Test]
        public void PotionCount_IsLimited()
        {
            var a = Monster.Create(_data, 4, 20);
            var b = Monster.Create(_data, 0, 20);
            var battle = Battle(Party(a), Party(b), new ScriptedRng(0.0, 0.5, 0.5, 0.0, 0.5, 0.5), potions: 1);
            a.Hp = 5;
            Assert.IsNull(battle.WhyNot(DuelSide.A, new PotionAction(0)));
            battle.ResolveRound(new PotionAction(0), new MoveAction("tackle"));
            a.Hp = 5;
            Assert.AreEqual("상처약이 없다!", battle.WhyNot(DuelSide.A, new PotionAction(0)));
            Assert.Throws<System.InvalidOperationException>(() => battle.ResolveRound(new PotionAction(0), new MoveAction("tackle")));
        }

        [Test]
        public void SwitchHappensBeforeTheAttack_SoTheNewMonsterTakesTheHit()
        {
            var a1 = Monster.Create(_data, 4, 20);
            var a2 = Monster.Create(_data, 2, 20);
            var b = Monster.Create(_data, 0, 20);
            var rng = new ScriptedRng(0.0, 0.5, 0.5);   // B 의 공격만
            var battle = Battle(Party(a1, a2), Party(b), rng);

            var events = battle.ResolveRound(new SwitchAction(1), new MoveAction("tackle"));

            CollectionAssert.AreEqual(new[]
            {
                PvpEventKind.SwitchOut, PvpEventKind.SwitchIn, PvpEventKind.MoveUsed, PvpEventKind.Damage,
            }, Kinds(events));
            Assert.AreEqual(0, events[0].PartyIndex);
            Assert.AreEqual(1, events[1].PartyIndex);
            Assert.AreEqual(2, events[1].SpeciesId, "SwitchIn 은 새 몬스터의 모습을 싣는다");
            Assert.AreEqual(a1.MaxHp, a1.Hp, "벤치로 물러난 몬스터는 맞지 않는다");
            Assert.Less(a2.Hp, a2.MaxHp);
            Assert.AreEqual(1, battle.ActiveIndex(DuelSide.A));
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void SwitchesComeBeforePotions_RegardlessOfSide()
        {
            var a1 = Monster.Create(_data, 4, 20); a1.Hp = 3;
            var a2 = Monster.Create(_data, 2, 20);
            var b = Monster.Create(_data, 0, 20);
            var b2 = Monster.Create(_data, 4, 20);
            var battle = Battle(Party(a1, a2), Party(b, b2), new ScriptedRng());

            var events = battle.ResolveRound(new PotionAction(0), new SwitchAction(1));

            CollectionAssert.AreEqual(new[] { PvpEventKind.SwitchOut, PvpEventKind.SwitchIn, PvpEventKind.PotionUsed }, Kinds(events));
            Assert.AreEqual(DuelSide.B, events[0].Side);
            Assert.AreEqual(DuelSide.A, events[2].Side);
        }

        [Test]
        public void FaintedMonster_ForcesReplacement_ThenBattleContinues()
        {
            var a1 = Monster.Create(_data, 4, 2);
            var a2 = Monster.Create(_data, 4, 10);
            var b = Monster.Create(_data, 15, 30);
            var battle = Battle(Party(a1, a2), Party(b), new ScriptedRng(0.0, 0.5, 0.5));
            battle.ResolveRound(new MoveAction("tackle"), new MoveAction("tackle"));

            Assert.AreEqual(PvpPhase.AwaitingReplacement, battle.Phase);
            Assert.AreEqual(DuelSide.A, battle.ReplacingSide);
            Assert.IsFalse(battle.IsOver);
            Assert.Throws<System.InvalidOperationException>(() => battle.ResolveRound(new MoveAction("tackle"), new MoveAction("tackle")), "교체 전에는 라운드를 못 돌린다");
            Assert.IsNotNull(battle.WhyNot(DuelSide.A, new MoveAction("tackle")));
            Assert.IsNotNull(battle.WhyNotReplacement(DuelSide.B, 0), "쓰러지지 않은 쪽은 고를 차례가 아니다");
            Assert.AreEqual("기절한 몬스터는 싸울 수 없다!", battle.WhyNotReplacement(DuelSide.A, 0));
            Assert.IsNotNull(battle.WhyNotReplacement(DuelSide.A, 5));
            Assert.Throws<System.InvalidOperationException>(() => battle.SubmitReplacement(DuelSide.A, 0));

            var events = battle.SubmitReplacement(DuelSide.A, 1);

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(PvpEventKind.SwitchIn, events[0].Kind);
            Assert.AreEqual(1, events[0].PartyIndex);
            Assert.AreEqual(PvpPhase.AwaitingActions, battle.Phase);
            Assert.AreSame(a2, battle.Active(DuelSide.A));
            Assert.IsNull(battle.WhyNot(DuelSide.A, new MoveAction("tackle")));
        }

        [Test]
        public void LastMonsterFainting_EndsTheBattle()
        {
            var a = Monster.Create(_data, 15, 30);
            var b = Monster.Create(_data, 4, 2);
            var battle = Battle(Party(a), Party(b), new ScriptedRng(0.0, 0.5, 0.5));

            var events = battle.ResolveRound(new MoveAction("ember"), new MoveAction("tackle"));

            CollectionAssert.AreEqual(new[] { PvpEventKind.MoveUsed, PvpEventKind.Damage, PvpEventKind.Fainted, PvpEventKind.Ended }, Kinds(events));
            Assert.AreEqual(DuelSide.A, events[3].Winner);
            Assert.IsTrue(battle.IsOver);
            Assert.AreEqual(DuelSide.A, battle.Winner);
            Assert.AreSame(b, battle.Of(DuelSide.B), "끝난 뒤에도 마지막 몬스터를 볼 수 있다(경험치 계산용)");
            Assert.Throws<System.InvalidOperationException>(() => battle.ResolveRound(new MoveAction("ember"), new MoveAction("tackle")));
        }

        [Test]
        public void Forfeit_LosesImmediately_BeforeAnythingElseHappens()
        {
            var battle = Battle(Party(Monster.Create(_data, 0, 20)), Party(Monster.Create(_data, 4, 20)), new ScriptedRng());

            var events = battle.ResolveRound(new FleeAction(), new MoveAction("vine"));

            CollectionAssert.AreEqual(new[] { PvpEventKind.Forfeit, PvpEventKind.Ended }, Kinds(events));
            Assert.AreEqual(DuelSide.A, events[0].Side);
            Assert.AreEqual(DuelSide.B, battle.Winner);
            Assert.IsTrue(battle.IsOver);
        }

        [Test]
        public void ForfeitMethod_WorksWithoutTheOpponentsAction_AndOnlyOnce()
        {
            var battle = Battle(Party(Monster.Create(_data, 0, 20)), Party(Monster.Create(_data, 4, 20)), new ScriptedRng());

            var events = battle.Forfeit(DuelSide.B);

            Assert.AreEqual(DuelSide.A, battle.Winner);
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(0, battle.Forfeit(DuelSide.A).Count, "이미 끝난 대결에서 또 기권해도 아무 일도 없다");
            Assert.AreEqual(DuelSide.A, battle.Winner);
        }

        [Test]
        public void InvalidActions_AreRejectedWithReasons_AndDoNotChangeState()
        {
            var a = Monster.Create(_data, 4, 20);
            var fainted = Monster.Create(_data, 2, 20); fainted.Hp = 0;
            var b = Monster.Create(_data, 0, 20);
            var battle = Battle(Party(a, fainted), Party(b), new ScriptedRng());

            Assert.AreEqual("배우지 않은 기술이다!", battle.WhyNot(DuelSide.A, new MoveAction("blast")));
            Assert.AreEqual("배우지 않은 기술이다!", battle.WhyNot(DuelSide.A, new MoveAction("nope")));
            Assert.AreEqual("이미 HP가 가득 찼다!", battle.WhyNot(DuelSide.A, new PotionAction(0)));
            Assert.AreEqual("기절한 몬스터에게는 쓸 수 없다!", battle.WhyNot(DuelSide.A, new PotionAction(1)));
            Assert.AreEqual("이미 싸우고 있다!", battle.WhyNot(DuelSide.A, new SwitchAction(0)));
            Assert.AreEqual("기절한 몬스터는 싸울 수 없다!", battle.WhyNot(DuelSide.A, new SwitchAction(1)));
            Assert.IsNotNull(battle.WhyNot(DuelSide.A, new SwitchAction(2)));
            Assert.IsNotNull(battle.WhyNot(DuelSide.A, new SwitchAction(-1)));
            Assert.IsNotNull(battle.WhyNot(DuelSide.A, new PotionAction(9)));
            Assert.AreEqual("대결 중에는 쓸 수 없다!", battle.WhyNot(DuelSide.A, new BallAction()));
            Assert.IsNotNull(battle.WhyNot(DuelSide.A, null));
            Assert.IsNull(battle.WhyNot(DuelSide.A, new FleeAction()), "기권은 언제나 할 수 있다");

            Assert.Throws<System.InvalidOperationException>(() => battle.ResolveRound(new MoveAction("blast"), new MoveAction("tackle")));
            Assert.AreEqual(a.MaxHp, a.Hp);
            Assert.AreEqual(PvpPhase.AwaitingActions, battle.Phase);
            Assert.AreEqual(3, battle.Potions(DuelSide.A));
        }

        [Test]
        public void Constructor_NeedsAUsableMonsterOnBothSides()
        {
            var dead = Monster.Create(_data, 0, 5); dead.Hp = 0;
            Assert.Throws<System.ArgumentException>(() => Battle(Party(dead), Party(Monster.Create(_data, 4, 5)), new ScriptedRng()));
        }

        [Test]
        public void SameSeed_PlaysOutIdentically()
        {
            string Play(int seed)
            {
                var rng = new SystemRng(seed);
                var a = new[] { Monster.Create(_data, 0, 12), Monster.Create(_data, 2, 8), Monster.Create(_data, 4, 6) };
                var b = new[] { Monster.Create(_data, 4, 12), Monster.Create(_data, 6, 9) };
                var battle = Battle(a, b, rng);
                var log = new List<string>();
                for (int round = 0; round < 200 && !battle.IsOver; round++)
                {
                    BattleAction Pick(DuelSide s) =>
                        battle.WhyNot(s, new PotionAction(0)) == null && round % 4 == 3 ? new PotionAction(0) : new MoveAction(battle.Active(s).Moves[round % 2]);
                    foreach (var e in battle.ResolveRound(Pick(DuelSide.A), Pick(DuelSide.B)))
                        log.Add($"{e.Kind}:{e.Side}:{e.MoveId}:{e.Amount}:{e.Critical}:{e.PartyIndex}:{e.Hp}");
                    while (battle.Phase == PvpPhase.AwaitingReplacement)
                    {
                        var side = battle.ReplacingSide.Value;
                        int next = battle.Party(side).ToList().FindIndex(m => !m.IsFainted);
                        foreach (var e in battle.SubmitReplacement(side, next)) log.Add($"{e.Kind}:{e.Side}:{e.PartyIndex}");
                    }
                }
                Assert.IsTrue(battle.IsOver, "200 라운드 안에는 끝나야 한다");
                return string.Join("|", log);
            }

            Assert.AreEqual(Play(7), Play(7));
            Assert.AreNotEqual(Play(7), Play(8));
        }
    }
}
