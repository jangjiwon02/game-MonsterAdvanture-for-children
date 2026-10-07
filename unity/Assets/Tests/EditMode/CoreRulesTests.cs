using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>웹 기준값으로 커버되지 않는 SPEC 규칙(포획 굴림 순서, 성장, 월드 규칙 등).</summary>
    public class CoreRulesTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        [Test]
        public void GameData_LoadsAllSpeciesTypesAndMoves()
        {
            Assert.AreEqual(19, _data.Species.Count);
            Assert.AreEqual(6, _data.Types.Count);
            Assert.AreEqual(18, _data.Moves.Count);
            Assert.AreEqual("불꼬마", _data.GetSpecies(0).Name);
            Assert.IsNull(_data.GetSpecies(1).Evolve);
            Assert.AreEqual(10, _data.GetSpecies(0).Evolve.Level);   // 40% 낮춘 새 진화 레벨(원래 16)
        }

        [Test]
        public void TypeChart_MissingEntryIsNeutral_AndSuperEffectiveIsDouble()
        {
            Assert.AreEqual(2.0, _data.TypeMultiplier("fire", "grass"));
            Assert.AreEqual(0.5, _data.TypeMultiplier("fire", "water"));
            Assert.AreEqual(1.0, _data.TypeMultiplier("fire", "normal"));
            Assert.AreEqual(1.0, _data.TypeMultiplier("normal", "fire"));
            Assert.AreEqual(0.5, _data.TypeMultiplier("normal", "rock"));
        }

        [Test]
        public void Damage_MissConsumesOnlyTheAccuracyRoll()
        {
            var a = Monster.Create(_data, 0, 5); var d = Monster.Create(_data, 4, 5);
            var rng = new ScriptedRng(0.995);                       // 99.5 > 85 → 빗나감
            var r = DamageCalc.Roll(_data, a, d, _data.GetMove("blast"), rng);
            Assert.IsTrue(r.Missed);
            Assert.AreEqual(0, r.Damage);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Damage_StabAndCriticalAreOneAndAHalf_AndDamageIsAtLeastOne()
        {
            // Lv.20, 위력 60, 공 30 / 방 30 → base = floor(floor(2*20/5+2) * 60 * 30/30 / 50 + 2) = floor(10*60/50+2) = 14
            Assert.AreEqual(14, DamageCalc.Compute(20, 60, 30, 30, 1.0, 1, false, 1.0));
            Assert.AreEqual(21, DamageCalc.Compute(20, 60, 30, 30, 1.5, 1, false, 1.0), "STAB 1.5배");
            Assert.AreEqual(21, DamageCalc.Compute(20, 60, 30, 30, 1.0, 1, true, 1.0), "급소 1.5배");
            Assert.AreEqual(63, DamageCalc.Compute(20, 60, 30, 30, 1.5, 2, true, 1.0), "STAB x 상성 x 급소");
            Assert.AreEqual(1, DamageCalc.Compute(2, 40, 1, 999, 1.0, 0.5, false, 0.85), "방어가 압도적이어도 최소 1");
        }

        [Test]
        public void Capture_SuccessConsumesOneRollAndShakesThree()
        {
            var target = Monster.Create(_data, 0, 5);                // catchRate 0.5, HP 가득 → p = 0.28
            var rng = new ScriptedRng(0.10);
            var r = Capture.Attempt(_data, target, rng);
            Assert.IsTrue(r.Caught);
            Assert.AreEqual(3, r.Shakes);
            Assert.AreEqual(0.28, r.Probability, 1e-12);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Capture_FailureShakesZeroToTwo()
        {
            var target = Monster.Create(_data, 0, 5);
            Assert.AreEqual(0, Capture.Attempt(_data, target, new ScriptedRng(0.9, 0.0)).Shakes);
            Assert.AreEqual(1, Capture.Attempt(_data, target, new ScriptedRng(0.9, 0.5)).Shakes);
            var r = Capture.Attempt(_data, target, new ScriptedRng(0.9, 0.999));
            Assert.IsFalse(r.Caught);
            Assert.AreEqual(2, r.Shakes);
        }

        [Test]
        public void Capture_LowHpRaisesProbability_ClampedToBounds()
        {
            Assert.Greater(Capture.Probability(0.5, 1, 30), Capture.Probability(0.5, 30, 30));
            Assert.AreEqual(0.95, Capture.Probability(0.9, 1, 100), 1e-12, "상한 0.95");
            // 하한 0.05는 catchRate >= 0 이면 +0.08 때문에 닿지 않는다(웹과 동일). 실질 최저값은 0.08.
            Assert.AreEqual(0.08, Capture.Probability(0.0, 100, 100), 1e-12);
        }

        [Test]
        public void Growth_LevelUpCarriesOverExp_AndEvolvesAtEvolutionLevel()
        {
            var m = Monster.Create(_data, 0, 15);                    // 불꼬마 Lv.15 (진화 Lv.16)
            int need = Growth.ExpToNext(15);
            var events = Growth.GainExp(_data, m, need + 5);
            Assert.AreEqual(16, m.Level);
            Assert.AreEqual(5, m.Exp);
            Assert.AreEqual(1, m.SpeciesId);                         // 이글불
            Assert.AreEqual(new[] { GrowthKind.LevelUp, GrowthKind.Evolved }, events.Select(e => e.Kind).ToArray());
            Assert.AreEqual(m.MaxHp, m.Hp, "HP는 늘어난 최대 HP만큼 함께 증가한다");
        }

        [Test]
        public void Growth_MultipleLevelsInOneGain()
        {
            var m = Monster.Create(_data, 8, 5);                     // 돌콩이(진화 Lv.20)
            var events = Growth.GainExp(_data, m, Growth.ExpToNext(5) + Growth.ExpToNext(6) + 1);
            Assert.AreEqual(7, m.Level);
            Assert.AreEqual(1, m.Exp);
            Assert.AreEqual(2, events.Count(e => e.Kind == GrowthKind.LevelUp));
        }

        [Test]
        public void Growth_LearnsMoveAtItsLevel_WhileUnderFourMoves()
        {
            var m = Monster.Create(_data, 0, 13);                    // tackle, ember
            Growth.GainExp(_data, m, Growth.ExpToNext(13));
            CollectionAssert.AreEqual(new[] { "tackle", "ember", "flame" }, m.Moves);
        }

        [Test]
        public void Growth_FullMoveSet_ReplacesWeakestOnlyIfNewMoveIsStronger()
        {
            var m = Monster.Create(_data, 0, 26);
            m.Moves = new System.Collections.Generic.List<string> { "ember", "tackle", "flame", "scratch" };  // 위력 40,40,60,55
            var events = Growth.GainExp(_data, m, Growth.ExpToNext(26));                                    // Lv.27: 불대문자(90)
            var replaced = events.Single(e => e.Kind == GrowthKind.ReplacedMove);
            Assert.AreEqual("blast", replaced.MoveId);
            Assert.AreEqual("ember", replaced.ForgottenMoveId, "동점이면 앞선 기술을 잊는다(웹과 동일)");
            CollectionAssert.AreEqual(new[] { "blast", "tackle", "flame", "scratch" }, m.Moves);
        }

        [Test]
        public void Growth_FullMoveSet_KeepsMovesWhenNoneIsWeakerThanTheNewOne()
        {
            var m = Monster.Create(_data, 0, 26);
            m.Moves = new System.Collections.Generic.List<string> { "edge", "wave", "solar", "thunder" };    // 전부 위력 90
            var events = Growth.GainExp(_data, m, Growth.ExpToNext(26));                                    // Lv.27: 불대문자(90)
            Assert.IsFalse(events.Any(e => e.Kind == GrowthKind.ReplacedMove || e.Kind == GrowthKind.LearnedMove));
            CollectionAssert.AreEqual(new[] { "edge", "wave", "solar", "thunder" }, m.Moves);
        }

        [Test]
        public void Rewards_FollowSpec()
        {
            Assert.AreEqual(60 * 10 / 7, Growth.ExpReward(_data.GetSpecies(0), 10));
            Assert.AreEqual(15 + 5 * 6, Growth.MoneyReward(5, new ScriptedRng(0.0)));
            Assert.AreEqual(30 + 5 * 6, Growth.MoneyReward(5, new ScriptedRng(0.9999)));
        }

        [Test]
        public void Flee_HigherSpeedAndRepeatedTriesHelp_ClampedToBounds()
        {
            Assert.Greater(Flee.Chance(60, 20, 0), Flee.Chance(20, 60, 0));
            Assert.Greater(Flee.Chance(20, 60, 2), Flee.Chance(20, 60, 0));
            Assert.AreEqual(0.25, Flee.Chance(1, 1000, 0), 1e-12);
            Assert.AreEqual(1.0, Flee.Chance(50, 50, 5), 1e-12);
        }

        [Test]
        public void TurnOrder_TieUsesRandom()
        {
            Assert.IsTrue(EnemyAI.PlayerMovesFirst(30, 20, new ScriptedRng()));      // 난수 소비 없음
            Assert.IsFalse(EnemyAI.PlayerMovesFirst(20, 30, new ScriptedRng()));
            Assert.IsTrue(EnemyAI.PlayerMovesFirst(25, 25, new ScriptedRng(0.4)));
            Assert.IsFalse(EnemyAI.PlayerMovesFirst(25, 25, new ScriptedRng(0.6)));
        }

        [Test]
        public void Korean_JosaFollowsFinalConsonant()
        {
            Assert.AreEqual("불꼬마는", Korean.J("불꼬마", "은", "는"));   // 받침 없음
            Assert.AreEqual("이글불을", Korean.J("이글불", "을", "를"));   // 받침 있음
            Assert.AreEqual("몬스터볼을", Korean.J("몬스터볼", "을", "를"));
        }

        [Test]
        public void PlayerState_NewGameMatchesSpec()
        {
            var s = PlayerState.NewGame(_data, 2);
            Assert.AreEqual(5, s.Balls);
            Assert.AreEqual(3, s.Potions);
            Assert.AreEqual(300, s.Money);
            Assert.AreEqual(1, s.Party.Count);
            Assert.AreEqual(5, s.Party[0].Level);
            Assert.IsTrue(s.Dex.Contains(2));
            Assert.AreEqual((WorldMap.VillageX, WorldMap.VillageY + 1), (s.X, s.Y));
        }

        [Test]
        public void Starters_AreThreeDifferentTypeBaseForms()
        {
            var starters = PlayerState.Starters.Select(id => _data.GetSpecies(id)).ToList();
            Assert.AreEqual(new[] { "불꼬마", "물방울이", "새싹이" }, starters.Select(s => s.Name).ToArray());
            Assert.IsTrue(starters.All(s => s.Stage == 1));
            Assert.AreEqual(3, starters.Select(s => s.Type).Distinct().Count());
        }

        [Test]
        public void PlayerState_FullPartySendsCaughtMonsterToBox()
        {
            var s = PlayerState.NewGame(_data, 0);
            for (int i = 0; i < 5; i++) Assert.IsFalse(s.AddCaught(Monster.Create(_data, 10, 3)));
            Assert.AreEqual(6, s.Party.Count);
            Assert.IsTrue(s.AddCaught(Monster.Create(_data, 12, 3)));
            Assert.AreEqual(1, s.Box.Count);
            Assert.IsTrue(s.Dex.Contains(12));
        }

        [Test]
        public void World_SolidTilesBlockAndVillageIsWalkable()
        {
            var map = WorldMap.Generate();
            Assert.IsFalse(map.IsPassable(0, WorldMap.NorthExtension), "웹 지형의 테두리는 나무");
            Assert.IsFalse(map.IsPassable(-1, 5));
            Assert.IsFalse(map.IsPassable(WorldMap.Width, 5));
            Assert.IsTrue(map.IsPassable(WorldMap.VillageX, WorldMap.VillageY + 1), "시작 지점");
            Assert.AreEqual(Tile.CenterDoor, map[WorldMap.VillageX - 3, WorldMap.VillageY - 3]);
            Assert.AreEqual(Tile.ShopDoor, map[WorldMap.VillageX + 3, WorldMap.VillageY - 3]);
            Assert.AreEqual(Tile.TowerBase, map[WorldMap.VillageX + 3, WorldMap.VillageY + 2]);
            Assert.IsFalse(map.IsPassable(WorldMap.VillageX + 3, WorldMap.VillageY + 1), "중앙탑은 막혀 있다");
            Assert.IsTrue(map.IsPassable(WorldMap.VillageX - 3, WorldMap.VillageY - 3), "문은 통과 가능");
        }

        [Test]
        public void World_NoTallGrassWithinSevenTilesOfVillage()
        {
            var map = WorldMap.Generate();
            for (int y = 0; y < WorldMap.Height; y++)
                for (int x = 0; x < WorldMap.Width; x++)
                    if (map[x, y] == Tile.TallGrass)
                        Assert.Greater(WorldMap.DistanceFromVillage(x, y), 7.0, $"({x},{y})");
        }

        [Test]
        public void Encounter_OnlyInTallGrass_At14Percent()
        {
            Assert.IsFalse(WildEncounter.ShouldEncounter(Tile.Grass, new ScriptedRng()));   // 난수 소비 없음
            Assert.IsTrue(WildEncounter.ShouldEncounter(Tile.TallGrass, new ScriptedRng(0.139)));
            Assert.IsFalse(WildEncounter.ShouldEncounter(Tile.TallGrass, new ScriptedRng(0.14)));
        }

        [Test]
        public void Water_IsWalkable_AndEncountersAt14Percent()
        {
            var map = WorldMap.Generate();
            int water = 0;
            for (int y = 0; y < WorldMap.Height; y++)
                for (int x = 0; x < WorldMap.Width; x++)
                    if (map[x, y] == Tile.Water) { water++; Assert.IsTrue(map.IsPassable(x, y), $"물({x},{y})은 걸을 수 있어야 한다"); }
            Assert.Greater(water, 0, "맵에 물 타일이 있어야 이 테스트가 의미가 있다");

            Assert.IsTrue(WildEncounter.ShouldEncounter(Tile.Water, new ScriptedRng(0.139)));
            Assert.IsFalse(WildEncounter.ShouldEncounter(Tile.Water, new ScriptedRng(0.14)));
        }

        [Test]
        public void WildPool_WaterTypesOnlyInWater_AndNeverInGrass()
        {
            foreach (double d in new[] { 3.0, 15.0 })
            {
                var grass = WildEncounter.Pool(_data, d, water: false);
                var water = WildEncounter.Pool(_data, d, water: true);
                Assert.IsNotEmpty(grass);
                Assert.IsNotEmpty(water);
                Assert.IsTrue(grass.TrueForAll(s => s.Type != "water"), "풀숲엔 물 타입이 안 나온다");
                Assert.IsTrue(water.TrueForAll(s => s.Type == "water"), "물에는 물 타입만 나온다");
                Assert.IsTrue(water.TrueForAll(s => s.Stage == 1), "야생 후보는 기본형만");
            }
        }

        [Test]
        public void Wild_NearVillageNeverSpawnsHornFireFamily_ButFarAwayCan()
        {
            var rng = new SystemRng(1234);
            for (int i = 0; i < 400; i++)
            {
                var near = WildEncounter.Generate(_data, 24, 21 + WorldMap.NorthExtension, rng);            // 거리 3 (<11)
                Assert.AreNotEqual(14, near.SpeciesId);
            }
            // 거리 >= 11 이면 풀이 11종으로 늘어난다: 마지막 항목(스르릉, id 18)을 굴림 0.99 로 뽑는다.
            var far = WildEncounter.Generate(_data, 44, 18 + WorldMap.NorthExtension, new ScriptedRng(0.5, 0.99));
            Assert.AreEqual(18, far.SpeciesId);
        }

        [Test]
        public void Wild_LevelIsClampedAndScalesWithDistance()
        {
            Assert.AreEqual(2, WildEncounter.RollLevel(0, new ScriptedRng(0.0)));          // 2 + 0 - 1 → 하한 2
            Assert.AreEqual(3, WildEncounter.RollLevel(0, new ScriptedRng(0.99)));         // 2 + 0 + 1
            Assert.AreEqual(2 + 7 + 1, WildEncounter.RollLevel(25, new ScriptedRng(0.99))); // floor(25/3.4)=7
            Assert.AreEqual(40, WildEncounter.RollLevel(1000, new ScriptedRng(0.99)));     // 상한 40
        }
    }
}
