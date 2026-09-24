using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class DuelTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        static DuelEventKind[] Kinds(IEnumerable<DuelEvent> events) => events.Select(e => e.Kind).ToArray();

        [Test]
        public void FasterSideActsFirst_AndDealsExpectedDamage()
        {
            var a = Monster.Create(_data, 0, 20);   // 불꼬마(스피드 29)
            var b = Monster.Create(_data, 4, 20);   // 새싹이(스피드 23) — 불꽃이 유리한 상대
            var rng = new ScriptedRng(0.0, 0.5, 0.5, 0.0, 0.5, 0.5);   // A: 명중,급소X,편차 / B: 명중,급소X,편차
            var duel = new Duel(_data, a, b, rng);

            var events = duel.ResolveRound("ember", "vine").ToList();

            CollectionAssert.AreEqual(new[]
            {
                DuelEventKind.MoveUsed, DuelEventKind.Damage, DuelEventKind.MoveUsed, DuelEventKind.Damage,
            }, Kinds(events));
            Assert.AreEqual(DuelSide.A, events[0].Side, "스피드가 빠른 A 가 먼저 움직인다");
            Assert.AreEqual(2.0, events[1].Multiplier, "불꽃 -> 풀 상성");
            Assert.IsFalse(duel.IsOver);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void DefeatedSide_NeverGetsToActThatRound()
        {
            var a = Monster.Create(_data, 15, 30);   // 화염뿔 Lv.30 — 한 방에 끝날 만큼 강하다
            var b = Monster.Create(_data, 4, 2);     // 새싹이 Lv.2 — 아주 약하다
            var rng = new ScriptedRng(0.0, 0.5, 0.5);   // A 선공: 명중, 급소X, 편차 (B 는 기절해서 굴림 없음)
            var duel = new Duel(_data, a, b, rng);

            var events = duel.ResolveRound("ember", "tackle").ToList();

            CollectionAssert.AreEqual(new[] { DuelEventKind.MoveUsed, DuelEventKind.Damage, DuelEventKind.Fainted, DuelEventKind.Ended },
                Kinds(events));
            Assert.AreEqual(DuelSide.B, events[2].Side);
            Assert.AreEqual(DuelSide.A, events[3].Winner);
            Assert.IsTrue(duel.IsOver);
            Assert.AreEqual(DuelSide.A, duel.Winner);
            Assert.AreEqual(0, rng.Remaining, "기절한 쪽은 굴림을 쓰지 않는다");
        }

        [Test]
        public void Miss_DealsNoDamage_ButFasterSideAlreadyActed()
        {
            var a = Monster.Create(_data, 0, 20);   // 불꼬마 — 스피드가 더 빨라 먼저 움직인다
            var b = Monster.Create(_data, 4, 20);   // 새싹이
            // A: 몸통박치기(명중100, 항상 명중) — 명중 굴림, 급소X, 편차. B: 돌진(명중90) — 0.95*100=95>90 이라 빗나간다.
            var rng = new ScriptedRng(0.0, 0.5, 0.5, 0.95);
            var duel = new Duel(_data, a, b, rng);

            var events = duel.ResolveRound("tackle", "rush").ToList();

            CollectionAssert.AreEqual(new[] { DuelEventKind.MoveUsed, DuelEventKind.Damage, DuelEventKind.MoveUsed, DuelEventKind.Missed },
                Kinds(events));
            Assert.AreEqual(DuelSide.A, events[0].Side, "먼저 움직인 A 는 정상적으로 명중했다");
            Assert.AreEqual(DuelSide.B, events[2].Side, "B 는 시도는 했지만 빗나갔다");
            Assert.IsFalse(duel.IsOver);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void ResolveRound_AfterDuelEnded_Throws()
        {
            var a = Monster.Create(_data, 15, 30);
            var b = Monster.Create(_data, 4, 2);
            var duel = new Duel(_data, a, b, new ScriptedRng(0.0, 0.5, 0.5));
            duel.ResolveRound("ember", "tackle").ToList();
            Assert.IsTrue(duel.IsOver);
            Assert.Throws<System.InvalidOperationException>(() => duel.ResolveRound("ember", "tackle").ToList());
        }

        [Test]
        public void WhyNotMove_RejectsUnknownMove()
        {
            var a = Monster.Create(_data, 0, 20);
            var b = Monster.Create(_data, 4, 20);
            var duel = new Duel(_data, a, b, new ScriptedRng());
            Assert.IsNull(duel.WhyNotMove(DuelSide.A, a.Moves[0]));
            Assert.AreEqual("배우지 않은 기술이다!", duel.WhyNotMove(DuelSide.A, "blast"));
        }

        [Test]
        public void BothCouldWin_ButFasterSideStrikesFirst_SlowerSideNeverActs()
        {
            // 둘 다 쓰러뜨릴 수 있을 만큼 낮은 HP 로 맞대결시켜, 선공이 정하고 후공은 기회가 없는지 확인.
            var a = Monster.Create(_data, 0, 5);
            var b = Monster.Create(_data, 4, 5);
            a.Hp = 1; b.Hp = 1;
            var rng = new ScriptedRng(0.0, 0.5, 0.5);   // 선공만 굴림을 쓴다(후공은 기절해서 스킵)
            var duel = new Duel(_data, a, b, rng);
            var events = duel.ResolveRound("ember", "vine").ToList();
            Assert.IsTrue(duel.IsOver);
            Assert.IsNotNull(duel.Winner);
            // 후공 쪽 MoveUsed 이벤트가 없어야 한다(기절해서 스킵됨)
            var mover = events.Where(e => e.Kind == DuelEventKind.MoveUsed).Select(e => e.Side).ToList();
            Assert.AreEqual(1, mover.Count);
        }
    }
}
