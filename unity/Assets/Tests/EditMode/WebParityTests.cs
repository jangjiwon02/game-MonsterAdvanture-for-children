using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>
    /// web/index.html 을 실제로 실행해 뽑은 기준값(web-golden.json, `node data/golden.js`)과
    /// C# 코어의 결과가 같은지 확인한다.
    /// </summary>
    public class WebParityTests
    {
        GameData _data;
        JObject _golden;

        [OneTimeSetUp]
        public void Load()
        {
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));
            _golden = JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Tests/EditMode/web-golden.json")));
        }

        static int[] Pair(JToken t) => new[] { (int)t[0], (int)t[1] };
        static double[] Rolls(JToken t) => t.Select(v => (double)v).ToArray();

        [Test]
        public void Mulberry32_MatchesWebSequence()
        {
            var rng = new Mulberry32(WorldMap.DefaultSeed);
            foreach (var expected in _golden["rng"])
                Assert.AreEqual((double)expected, rng.NextDouble(), 0.0);
        }

        [Test]
        public void GeneratedMap_MatchesWebTileForTile()
        {
            var map = WorldMap.Generate();
            var rows = _golden["map"].Select(r => (string)r).ToArray();
            Assert.AreEqual(WorldMap.GenHeight, rows.Length);
            int mismatches = 0;
            string firstMismatch = null;
            // 웹 지형은 북쪽 확장 구역 아래(y + NorthExtension)에 그대로 붙어 있다.
            for (int y = 0; y < WorldMap.GenHeight; y++)
                for (int x = 0; x < WorldMap.GenWidth; x++)
                {
                    char c = rows[y][x];
                    int expected = c <= '9' ? c - '0' : c - 'a' + 10;
                    if ((int)map[x, y + WorldMap.NorthExtension] == expected) continue;
                    mismatches++;
                    firstMismatch ??= $"({x},{y}) 웹={expected} C#={(int)map[x, y + WorldMap.NorthExtension]}";
                }
            Assert.AreEqual(0, mismatches, $"불일치 {mismatches}칸, 첫 불일치 {firstMismatch}");
        }

        [Test]
        public void AreaNames_MatchWeb()
        {
            foreach (var a in _golden["areas"])
                Assert.AreEqual((string)a["name"], WorldMap.AreaName((int)a["x"], (int)a["y"] + WorldMap.NorthExtension), $"({a["x"]},{a["y"]})");
        }

        [Test]
        public void ExpToNext_MatchesWeb()
        {
            var expected = _golden["need"].Select(v => (int)v).ToArray();
            for (int lv = 1; lv <= 60; lv++) Assert.AreEqual(expected[lv - 1], Growth.ExpToNext(lv), $"Lv.{lv}");
        }

        [Test]
        public void Stats_MatchWebForEverySpecies()
        {
            foreach (var sp in _golden["stats"])
                foreach (var row in sp["byLevel"])
                {
                    var m = new Monster { SpeciesId = (int)sp["id"], Level = (int)row["lv"] };
                    m.RecalcStats(_data);
                    string label = $"종족 {sp["id"]} Lv.{row["lv"]}";
                    Assert.AreEqual((int)row["hp"], m.MaxHp, label + " HP");
                    Assert.AreEqual((int)row["atk"], m.Attack, label + " 공격");
                    Assert.AreEqual((int)row["def"], m.Defense, label + " 방어");
                    Assert.AreEqual((int)row["spd"], m.Speed, label + " 스피드");
                }
        }

        [Test]
        public void CreatedMonsterMoves_MatchWeb()
        {
            foreach (var c in _golden["makeMon"])
            {
                var m = Monster.Create(_data, (int)c["id"], (int)c["lv"]);
                CollectionAssert.AreEqual(c["moves"].Select(v => (string)v).ToArray(), m.Moves, $"종족 {c["id"]} Lv.{c["lv"]}");
                Assert.AreEqual(m.MaxHp, m.Hp);
            }
        }

        [Test]
        public void Damage_MatchesWeb()
        {
            foreach (var c in _golden["damage"])
            {
                var a = Pair(c["a"]); var d = Pair(c["d"]);
                var rolls = Rolls(c["rolls"]);
                // 웹의 calcDamage 에는 명중 굴림이 없으므로, 명중은 항상 성공하는 0을 앞에 붙인다.
                var rng = new ScriptedRng(new[] { 0.0 }.Concat(rolls).ToArray());
                var result = DamageCalc.Roll(_data, Monster.Create(_data, a[0], a[1]), Monster.Create(_data, d[0], d[1]),
                    _data.GetMove((string)c["move"]), rng);
                string label = $"{a[0]}/L{a[1]} → {d[0]}/L{d[1]} {c["move"]} rolls[{string.Join(",", rolls)}]";
                Assert.IsFalse(result.Missed, label);
                Assert.AreEqual((int)c["dmg"], result.Damage, label + " 데미지");
                Assert.AreEqual((double)c["eff"], result.Multiplier, label + " 상성");
                Assert.AreEqual((bool)c["crit"], result.Critical, label + " 급소");
                Assert.AreEqual(0, rng.Remaining, label);
            }
        }

        [Test]
        public void EnemyMovePick_MatchesWeb()
        {
            foreach (var c in _golden["enemyMove"])
            {
                var e = Pair(c["e"]); var t = Pair(c["t"]);
                var enemy = Monster.Create(_data, e[0], e[1]);
                CollectionAssert.AreEqual(c["moves"].Select(v => (string)v).ToArray(), enemy.Moves);
                var picked = EnemyAI.PickMove(_data, enemy, Monster.Create(_data, t[0], t[1]), new ScriptedRng(Rolls(c["rolls"])));
                Assert.AreEqual((string)c["move"], picked, $"{e[0]}/L{e[1]} vs {t[0]}/L{t[1]} rolls[{c["rolls"]}]");
            }
        }

        [Test]
        public void WildEncounters_MatchWeb()
        {
            // 풀숲("wild")과 물("wildWater") 두 후보 풀 모두 웹과 같아야 한다.
            foreach (var (key, water) in new[] { ("wild", false), ("wildWater", true) })
                foreach (var c in _golden[key])
                {
                    var rng = new ScriptedRng(Rolls(c["rolls"]));
                    var m = WildEncounter.Generate(_data, (int)c["x"], (int)c["y"] + WorldMap.NorthExtension, rng, water);   // 웹 좌표 → 이 맵 좌표
                    string label = $"[{key}] ({c["x"]},{c["y"]}) rolls[{c["rolls"]}]";
                    Assert.AreEqual((int)c["id"], m.SpeciesId, label + " 종족");
                    Assert.AreEqual((int)c["lv"], m.Level, label + " 레벨");
                    Assert.AreEqual(0, rng.Remaining, label);
                }
        }

        [Test]
        public void CatchProbability_MatchesSpecFormula()
        {
            foreach (var c in _golden["catchP"])
                Assert.AreEqual((double)c["p"], Capture.Probability((double)c["rate"], (int)c["hp"], (int)c["mhp"]), 1e-12);
        }

        [Test]
        public void FleeChance_MatchesSpecFormula()
        {
            foreach (var c in _golden["flee"])
                Assert.AreEqual((double)c["chance"], Flee.Chance((int)c["p"], (int)c["e"], (int)c["tries"]), 1e-12);
        }
    }
}

