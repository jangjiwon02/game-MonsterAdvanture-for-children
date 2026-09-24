using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>월드 메뉴의 박스·선두 교체 규칙과 효과음 합성.</summary>
    public class MenuRulesTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        PlayerState Party(int count)
        {
            var s = PlayerState.NewGame(_data, 0);
            for (int i = 1; i < count; i++) s.Party.Add(Monster.Create(_data, 10, i + 2));
            return s;
        }

        static string Names(PlayerState s, GameData d) => string.Join(",", s.Party.Select(m => m.SpeciesId));

        [Test]
        public void MoveToFront_ReordersOnly_AndIgnoresBadIndexes()
        {
            var s = Party(3);
            var third = s.Party[2];
            s.MoveToFront(2);
            Assert.AreSame(third, s.Party[0]);
            Assert.AreEqual(3, s.Party.Count);
            var lead = s.Party[0];
            s.MoveToFront(0); s.MoveToFront(-1); s.MoveToFront(9);
            Assert.AreSame(lead, s.Party[0]);
        }

        [Test]
        public void Deposit_MovesPartyMemberToBox()
        {
            var s = Party(3);
            var m = s.Party[1];
            BoxOps.Deposit(s, 1);
            Assert.AreEqual(2, s.Party.Count);
            CollectionAssert.AreEqual(new[] { m }, s.Box);
        }

        [Test]
        public void Deposit_RefusesTheLastMonster()
        {
            var s = Party(1);
            Assert.AreEqual("마지막 한 마리는 맡길 수 없다!", BoxOps.WhyNotDeposit(s, 0));
            Assert.Throws<System.InvalidOperationException>(() => BoxOps.Deposit(s, 0));
            Assert.AreEqual(1, s.Party.Count);
        }

        [Test]
        public void Deposit_RefusesWhenNoOneElseCanFight()
        {
            var s = Party(3);
            s.Party[1].Hp = 0; s.Party[2].Hp = 0;                       // 선두만 살아 있다
            Assert.AreEqual("싸울 수 있는 몬스터가 없어진다!", BoxOps.WhyNotDeposit(s, 0));
            Assert.IsNull(BoxOps.WhyNotDeposit(s, 1), "기절한 애를 맡기는 건 괜찮다");
            Assert.IsNull(BoxOps.WhyNotDeposit(s, 2));
        }

        [Test]
        public void Deposit_AllowsLeadWhenAnotherCanFight()
        {
            var s = Party(2);
            Assert.IsNull(BoxOps.WhyNotDeposit(s, 0));
        }

        [Test]
        public void Withdraw_AppendsToParty_UntilItIsFull()
        {
            var s = Party(5);
            s.Box.Add(Monster.Create(_data, 12, 4));
            s.Box.Add(Monster.Create(_data, 12, 5));
            Assert.IsTrue(BoxOps.Withdraw(s, 0));
            Assert.AreEqual(6, s.Party.Count);
            Assert.AreEqual(4, s.Party[5].Level);
            Assert.IsFalse(BoxOps.Withdraw(s, 0), "파티가 가득 차면 꺼낼 수 없다");
            Assert.AreEqual((6, 1), (s.Party.Count, s.Box.Count));
        }

        [Test]
        public void Swap_ExchangesBoxAndPartyMember()
        {
            var s = Party(6);
            var boxed = Monster.Create(_data, 12, 9);
            s.Box.Add(boxed);
            var sent = s.Party[3];
            BoxOps.Swap(s, 0, 3);
            Assert.AreSame(boxed, s.Party[3]);
            Assert.AreSame(sent, s.Box[0]);
            Assert.AreEqual((6, 1), (s.Party.Count, s.Box.Count));
        }

        [Test]
        public void Sfx_DefinesEveryKind_MatchingTheWebBeepList()
        {
            foreach (SfxKind k in System.Enum.GetValues(typeof(SfxKind)))
                Assert.IsTrue(Sfx.Definitions.ContainsKey(k), k.ToString());
            Assert.AreEqual(4, Sfx.Definitions[SfxKind.LevelUp].Length);
            Assert.AreEqual(6, Sfx.Definitions[SfxKind.Encounter].Length);
            Assert.AreEqual(6, Sfx.Definitions[SfxKind.Heal].Length);
            Assert.AreEqual(2, Sfx.Definitions[SfxKind.Crit].Length);
        }

        [Test]
        public void Sfx_Render_HasExpectedLength_AndIsAudibleButNotClipping()
        {
            foreach (var kv in Sfx.Definitions)
            {
                var data = Sfx.Render(kv.Value);
                float end = kv.Value.Max(b => b.Delay + b.Duration);
                Assert.AreEqual(Mathf.CeilToInt((end + .02f) * Sfx.SampleRate), data.Length, kv.Key.ToString());
                float peak = data.Max(Mathf.Abs);
                Assert.Greater(peak, .005f, kv.Key + " 가 무음이다");
                Assert.LessOrEqual(peak, 1f, kv.Key + " 가 클리핑된다");
            }
        }

        [Test]
        public void Sfx_Render_DecaysExponentially_ToNearSilence()
        {
            var data = Sfx.Render(Sfx.Definitions[SfxKind.Faint]);
            float early = data.Take(Sfx.SampleRate / 100).Max(Mathf.Abs);
            float late = data.Skip(data.Length - Sfx.SampleRate / 100).Max(Mathf.Abs);
            Assert.Greater(early, .04f);
            Assert.Less(late, early * .05f);
        }

        [Test]
        public void Sfx_Render_DelayedBeepsStartLater()
        {
            var data = Sfx.Render(Sfx.Definitions[SfxKind.Buy]);          // 두 번째 비프는 0.05초 뒤 시작
            Assert.Greater(data.Take(100).Max(Mathf.Abs), 0f);
            var second = new[] { new Sfx.Beep(1320, .08f, Sfx.Wave.Square, .03f, 0, .05f) };
            var d2 = Sfx.Render(second);
            Assert.AreEqual(0f, d2.Take(Mathf.RoundToInt(.05f * Sfx.SampleRate) - 1).Max(Mathf.Abs), "지연 구간은 무음");
        }

        [Test]
        public void Sfx_Muted_DoesNothingAndDoesNotThrow()
        {
            bool old = Sfx.Muted;
            Sfx.Muted = true;
            try { Assert.DoesNotThrow(() => Sfx.Play(SfxKind.Hit)); }
            finally { Sfx.Muted = old; }
        }
    }
}
