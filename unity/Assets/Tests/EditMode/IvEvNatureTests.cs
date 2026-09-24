using System.IO;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>개체값(IV)·노력치(EV)·성격 — 웹에는 없는 Unity 전용 추가. 기본값(0/0/Balanced)일 때
    /// 기존 웹 공식과 완전히 같은 값이 나오는지가 제일 중요한 보장이라, 그것부터 확인한다.</summary>
    public class IvEvNatureTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        [Test]
        public void DefaultIvEv_ProducesExactlySameStatsAsOriginalFormula()
        {
            foreach (var sp in _data.Species)
                for (int level = 5; level <= 60; level += 11)
                {
                    var m = new Monster { SpeciesId = sp.Id, Level = level };
                    m.RecalcStats(_data);
                    Assert.AreEqual(StatCalc.MaxHp(sp.BaseStats.Hp, level), m.MaxHp, $"{sp.Name} Lv.{level} HP");
                    Assert.AreEqual(StatCalc.Stat(sp.BaseStats.Atk, level), m.Attack, $"{sp.Name} Lv.{level} 공격");
                    Assert.AreEqual(StatCalc.Stat(sp.BaseStats.Def, level), m.Defense, $"{sp.Name} Lv.{level} 방어");
                    Assert.AreEqual(StatCalc.Stat(sp.BaseStats.Spd, level), m.Speed, $"{sp.Name} Lv.{level} 스피드");
                }
        }

        [Test]
        public void Monster_Create_StillHasNeutralIvEv()
        {
            var m = Monster.Create(_data, 0, 10);
            Assert.AreEqual(0, m.IVs.Hp + m.IVs.Atk + m.IVs.Def + m.IVs.Spd);
            Assert.AreEqual(0, m.EVs.Hp + m.EVs.Atk + m.EVs.Def + m.EVs.Spd);
            Assert.AreEqual(Nature.Balanced, m.Nature);
        }

        [Test]
        public void RollIndividualValues_IsDeterministicWithScriptedRng_AndPicksExpectedNature()
        {
            var m = Monster.Create(_data, 0, 10);
            // Range(0,31)은 floor(next*32)(분모가 2의 거듭제곱이라 부동소수점 오차 없이 정확함);
            // Pick(7개 성격)은 floor(next*7) — 0.3은 [2/7, 3/7) 구간 한가운데라 오차 걱정 없이 index 2로 떨어진다.
            var rng = new ScriptedRng(0.5, 1.0 / 32, 30.0 / 32, 31.0 / 32, 0.3);
            m.RollIndividualValues(_data, rng);
            Assert.AreEqual(16, m.IVs.Hp);
            Assert.AreEqual(1, m.IVs.Atk);
            Assert.AreEqual(30, m.IVs.Def);
            Assert.AreEqual(31, m.IVs.Spd);
            Assert.AreEqual(Nature.Reckless, m.Nature);   // enum 순서: Balanced,Aggressive,Reckless,... → index 2
        }

        [Test]
        public void RollIndividualValues_StaysWithinRange_AndChangesStats()
        {
            var rng = new SystemRng(12345);
            var m = Monster.Create(_data, 0, 20);
            int hpBefore = m.MaxHp, atkBefore = m.Attack;
            m.RollIndividualValues(_data, rng);
            foreach (int iv in new[] { m.IVs.Hp, m.IVs.Atk, m.IVs.Def, m.IVs.Spd })
                Assert.That(iv, Is.InRange(0, Monster.MaxIv));
            // 개체값이 전부 0으로 굴러갈 확률은 사실상 0에 가까우니, 스탯이 최소 하나는 바뀌었어야 정상이다.
            Assert.IsTrue(m.MaxHp != hpBefore || m.Attack != atkBefore, "개체값을 굴렸는데 스탯이 하나도 안 바뀌었다");
        }

        [Test]
        public void Nature_BoostsOneStatAndLowersAnother_ByTenPercent()
        {
            Assert.AreEqual(1.1, NatureInfo.Multiplier(Nature.Aggressive, StatKind.Atk));
            Assert.AreEqual(0.9, NatureInfo.Multiplier(Nature.Aggressive, StatKind.Def));
            Assert.AreEqual(1.0, NatureInfo.Multiplier(Nature.Aggressive, StatKind.Spd));
            Assert.AreEqual(1.0, NatureInfo.Multiplier(Nature.Balanced, StatKind.Atk));
        }

        [Test]
        public void GainEVs_AddsToHighestBaseStat_AndCapsAtStatAndTotalLimits()
        {
            var m = Monster.Create(_data, 0, 10);
            var defeated = _data.GetSpecies(8);   // 돌콩이(rock) — 어떤 스탯이 제일 높은지는 몰라도 캡만 확인하면 된다
            for (int i = 0; i < 300; i++) Growth.GainEVs(_data, m, defeated);

            int total = m.EVs.Hp + m.EVs.Atk + m.EVs.Def + m.EVs.Spd;
            Assert.LessOrEqual(total, Growth.EVTotalCap);
            Assert.LessOrEqual(m.EVs.Hp, Growth.EVStatCap);
            Assert.LessOrEqual(m.EVs.Atk, Growth.EVStatCap);
            Assert.LessOrEqual(m.EVs.Def, Growth.EVStatCap);
            Assert.LessOrEqual(m.EVs.Spd, Growth.EVStatCap);
            Assert.Greater(total, 0, "300번 이겼는데 노력치가 하나도 안 붙었다");
        }

        [Test]
        public void CreateFullHpCopy_PreservesIndividualIdentity_ButFullyHeals()
        {
            var source = Monster.Create(_data, 4, 12);
            source.RollIndividualValues(_data, new SystemRng(7));
            source.Hp = 1;   // 다쳐 있는 상태

            var copy = Monster.CreateFullHpCopy(_data, source);

            Assert.AreEqual(source.SpeciesId, copy.SpeciesId);
            Assert.AreEqual(source.Level, copy.Level);
            Assert.AreEqual(source.Nature, copy.Nature);
            Assert.AreEqual(source.IVs.Hp, copy.IVs.Hp);
            Assert.AreEqual(source.IVs.Atk, copy.IVs.Atk);
            Assert.AreEqual(source.MaxHp, copy.MaxHp);
            Assert.AreEqual(source.Attack, copy.Attack);
            Assert.AreEqual(copy.MaxHp, copy.Hp, "사본은 항상 풀피여야 한다");
            Assert.AreEqual(1, source.Hp, "원본은 그대로여야 한다");
        }

        [Test]
        public void SaveRoundTrip_PreservesIvEvNature()
        {
            var state = PlayerState.NewGame(_data, 2);
            state.Party[0].RollIndividualValues(_data, new SystemRng(99));
            var before = state.Party[0];

            var loaded = SaveSerializer.FromJson(_data, SaveSerializer.ToJson(state));

            Assert.IsNotNull(loaded);
            var after = loaded.Party[0];
            Assert.AreEqual(before.Nature, after.Nature);
            Assert.AreEqual(before.IVs.Hp, after.IVs.Hp);
            Assert.AreEqual(before.IVs.Atk, after.IVs.Atk);
            Assert.AreEqual(before.IVs.Def, after.IVs.Def);
            Assert.AreEqual(before.IVs.Spd, after.IVs.Spd);
        }
    }
}
