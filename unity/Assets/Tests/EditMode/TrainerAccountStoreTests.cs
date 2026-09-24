using System;
using System.IO;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class TrainerAccountStoreTests
    {
        GameData _data;
        string _dir;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        [SetUp]
        public void MakeTempDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "trainer_account_tests_" + Guid.NewGuid());
        }

        [TearDown]
        public void CleanTempDir()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        TrainerAccountStore NewStore() => new TrainerAccountStore(_dir);

        [Test]
        public void UnknownName_DoesNotExist_AndLoadsAsNull()
        {
            var store = NewStore();
            Assert.IsFalse(store.Exists("아무개"));
            Assert.IsNull(store.Load(_data, "아무개"));
        }

        [Test]
        public void SaveThenLoad_RoundTripsBasicFields()
        {
            var store = NewStore();
            var original = PlayerState.NewGame(_data, 0);
            original.Money = 555;

            store.Save("철수", original);

            Assert.IsTrue(store.Exists("철수"));
            var loaded = store.Load(_data, "철수");
            Assert.IsNotNull(loaded);
            Assert.AreEqual(original.Money, loaded.Money);
            Assert.AreEqual(original.Party.Count, loaded.Party.Count);
            Assert.AreEqual(original.Party[0].SpeciesId, loaded.Party[0].SpeciesId);
        }

        [Test]
        public void DifferentNames_AreStoredSeparately_AndDoNotOverwriteEachOther()
        {
            var store = NewStore();
            var cheolsu = PlayerState.NewGame(_data, 0);
            cheolsu.Money = 100;
            var younghee = PlayerState.NewGame(_data, 2);
            younghee.Money = 200;

            store.Save("철수", cheolsu);
            store.Save("영희", younghee);

            var loadedCheolsu = store.Load(_data, "철수");
            var loadedYounghee = store.Load(_data, "영희");
            Assert.AreEqual(100, loadedCheolsu.Money);
            Assert.AreEqual(200, loadedYounghee.Money);
            Assert.AreEqual(cheolsu.Party[0].SpeciesId, loadedCheolsu.Party[0].SpeciesId);
            Assert.AreEqual(younghee.Party[0].SpeciesId, loadedYounghee.Party[0].SpeciesId);
        }

        [Test]
        public void NameCasingOrWhitespace_DoesNotThrow_EvenIfItCollidesWithAnotherFile()
        {
            var store = NewStore();
            var state = PlayerState.NewGame(_data, 0);

            Assert.DoesNotThrow(() => store.Save("Cheolsu", state));
            Assert.DoesNotThrow(() => store.Save("cheolsu", state));
            Assert.DoesNotThrow(() => store.Save(" Cheol Su ", state));
            // Sanitize 가 소문자로 통일하므로 이런 이름들은 같은 파일로 취급될 수 있다(알려진 동작).
        }

        [Test]
        public void SavingAgain_OverwritesWithLatestValue()
        {
            var store = NewStore();
            var first = PlayerState.NewGame(_data, 0);
            first.Money = 10;
            store.Save("철수", first);

            var second = PlayerState.NewGame(_data, 0);
            second.Money = 9999;
            store.Save("철수", second);

            var loaded = store.Load(_data, "철수");
            Assert.AreEqual(9999, loaded.Money);
        }
    }
}
