using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class SaveAndShopTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        PlayerState SampleState()
        {
            var s = PlayerState.NewGame(_data, 2);
            var lead = s.Party[0];
            lead.Level = 12; lead.Exp = 77; lead.RecalcStats(_data); lead.Hp = 9;
            lead.Moves = new System.Collections.Generic.List<string> { "tackle", "splash", "bubble" };
            s.Party.Add(Monster.Create(_data, 10, 7));
            s.Box.Add(Monster.Create(_data, 12, 4));
            s.Dex.Add(10); s.Dex.Add(12);
            s.Balls = 2; s.Potions = 7; s.Money = 1234; s.X = 30; s.Y = 22;
            return s;
        }

        [Test]
        public void RoundTrip_PreservesEverythingInTheSpecSaveList()
        {
            var original = SampleState();
            var loaded = SaveSerializer.FromJson(_data, SaveSerializer.ToJson(original));

            Assert.IsNotNull(loaded);
            Assert.AreEqual((2, 7, 1234), (loaded.Balls, loaded.Potions, loaded.Money));
            Assert.AreEqual((30, 22), (loaded.X, loaded.Y));
            CollectionAssert.AreEquivalent(new[] { 2, 10, 12 }, loaded.Dex);
            Assert.AreEqual(2, loaded.Party.Count);
            Assert.AreEqual(1, loaded.Box.Count);

            var lead = loaded.Party[0];
            Assert.AreEqual((2, 12, 77, 9), (lead.SpeciesId, lead.Level, lead.Exp, lead.Hp));
            CollectionAssert.AreEqual(new[] { "tackle", "splash", "bubble" }, lead.Moves);
            Assert.AreEqual(original.Party[0].MaxHp, lead.MaxHp, "능력치는 불러올 때 다시 계산된다");
            Assert.AreEqual(original.Party[0].Attack, lead.Attack);
            Assert.AreEqual(12, loaded.Box[0].SpeciesId);
        }

        [Test]
        public void Json_DoesNotContainDerivedProperties()
        {
            string json = SaveSerializer.ToJson(SampleState());
            StringAssert.DoesNotContain("HasUsableMonster", json);
            StringAssert.DoesNotContain("IsFainted", json);
        }

        [Test]
        public void Load_RecomputesStatsAndClampsHp()
        {
            var j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            var lead = j["State"]["Party"][0];
            lead["MaxHp"] = 9999; lead["Attack"] = 1; lead["Hp"] = 5000;       // 조작·구버전 데이터
            var loaded = SaveSerializer.FromJson(_data, j.ToString());
            Assert.IsNotNull(loaded);
            var m = loaded.Party[0];
            Assert.AreEqual(StatCalc.MaxHp(_data.GetSpecies(2).BaseStats.Hp, 12), m.MaxHp);
            Assert.AreEqual(m.MaxHp, m.Hp, "HP는 최대 HP를 넘지 않는다");
            Assert.AreEqual(StatCalc.Stat(_data.GetSpecies(2).BaseStats.Atk, 12), m.Attack);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{}")]
        [TestCase("null")]
        [TestCase("{\"Version\":1}")]
        public void Load_RejectsGarbage_ByReturningNull(string json) =>
            Assert.IsNull(SaveSerializer.FromJson(_data, json));

        [Test]
        public void Load_RejectsUnknownVersion()
        {
            var j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            j["Version"] = 99;
            Assert.IsNull(SaveSerializer.FromJson(_data, j.ToString()));
        }

        [TestCase("SpeciesId", 99)]
        [TestCase("SpeciesId", -1)]
        [TestCase("Level", 0)]
        [TestCase("Level", 61)]
        public void Load_RejectsOutOfRangeMonsterFields(string field, int value)
        {
            var j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            j["State"]["Party"][0][field] = value;
            Assert.IsNull(SaveSerializer.FromJson(_data, j.ToString()));
        }

        [Test]
        public void Load_RejectsUnknownMoveAndEmptyParty()
        {
            var j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            j["State"]["Party"][0]["Moves"] = new JArray("no_such_move");
            Assert.IsNull(SaveSerializer.FromJson(_data, j.ToString()));

            j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            j["State"]["Party"] = new JArray();
            Assert.IsNull(SaveSerializer.FromJson(_data, j.ToString()), "파티가 비면 이어할 수 없다");
        }

        [Test]
        public void Load_RejectsBoxedMonsterWithBadSpecies_AndBadDexEntry()
        {
            var j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            j["State"]["Box"][0]["SpeciesId"] = 50;
            Assert.IsNull(SaveSerializer.FromJson(_data, j.ToString()));

            j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            ((JArray)j["State"]["Dex"]).Add(77);
            Assert.IsNull(SaveSerializer.FromJson(_data, j.ToString()));
        }

        [Test]
        public void Load_ClampsNegativeItemsAndResetsOutOfMapPosition()
        {
            var j = JObject.Parse(SaveSerializer.ToJson(SampleState()));
            j["State"]["Balls"] = -3; j["State"]["Money"] = -1; j["State"]["X"] = 500; j["State"]["Y"] = -2;
            var s = SaveSerializer.FromJson(_data, j.ToString());
            Assert.IsNotNull(s);
            Assert.AreEqual((0, 0), (s.Balls, s.Money));
            Assert.AreEqual((WorldMap.VillageX, WorldMap.VillageY + 1), (s.X, s.Y));
        }

        [Test]
        public void Loaded_MonsterIsUsableInBattle()
        {
            var s = SaveSerializer.FromJson(_data, SaveSerializer.ToJson(SampleState()));
            var session = new BattleSession(_data, s, Monster.Create(_data, 10, 3), new ScriptedRng(0.0, 0.5, 0.5, 0.0));
            var events = session.ResolveTurn(new MoveAction("bubble")).ToList();
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Damage));
        }

        [Test]
        public void Shop_BuysBallAndPotion_AtSpecPrices()
        {
            var s = PlayerState.NewGame(_data, 0);                 // ₩300, 볼 5, 상처약 3
            Assert.IsTrue(Shop.TryBuy(s, ShopItem.Ball));
            Assert.AreEqual((200, 6, 3), (s.Money, s.Balls, s.Potions));
            Assert.IsTrue(Shop.TryBuy(s, ShopItem.Potion));
            Assert.AreEqual((120, 6, 4), (s.Money, s.Balls, s.Potions));
        }

        [Test]
        public void Shop_RefusesWhenShortOfMoney_AndChangesNothing()
        {
            var s = PlayerState.NewGame(_data, 0);
            s.Money = 79;
            Assert.IsFalse(Shop.TryBuy(s, ShopItem.Potion));
            Assert.IsFalse(Shop.TryBuy(s, ShopItem.Ball));
            Assert.AreEqual((79, 5, 3), (s.Money, s.Balls, s.Potions));
            s.Money = 80;
            Assert.IsTrue(Shop.TryBuy(s, ShopItem.Potion), "정확히 ₩80이면 살 수 있다");
            Assert.AreEqual(0, s.Money);
        }

        [Test]
        public void Center_HealAll_RestoresFaintedAndHurtMonsters()
        {
            var s = SampleState();
            s.Party[1].Hp = 0;
            s.HealAll();
            Assert.IsTrue(s.Party.All(m => m.Hp == m.MaxHp));
        }
    }
}
