using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class PvpReplicaTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        static DuelMonsterInfo Info(Monster m) =>
            new DuelMonsterInfo { SpeciesId = m.SpeciesId, Level = m.Level, Exp = m.Exp, Hp = m.Hp, MaxHp = m.MaxHp, Moves = new List<string>(m.Moves) };

        static DuelStartMessage StartFor(PvpBattle battle, DuelSide viewer)
        {
            var opp = battle.Active(PvpBattle.Other(viewer));
            return new DuelStartMessage
            {
                OpponentName = "상대", Potions = PvpBattle.StartPotions,
                Party = battle.Party(viewer).Select(Info).ToList(),
                Opponent = new DuelMonsterInfo { SpeciesId = opp.SpeciesId, Level = opp.Level, Hp = opp.Hp, MaxHp = opp.MaxHp },
            };
        }

        static DuelEventMessage Msg(string kind, int side, int amount = 0, int partyIndex = -1, DuelMonsterInfo monster = null, bool targetActive = false) =>
            new DuelEventMessage { Kind = kind, Side = side, Amount = amount, PartyIndex = partyIndex, Monster = monster, TargetActive = targetActive };

        PvpReplica Replica()
        {
            var start = new DuelStartMessage
            {
                OpponentName = "영희", Potions = 3,
                Party = new List<DuelMonsterInfo>
                {
                    new DuelMonsterInfo { SpeciesId = 0, Level = 10, Hp = 40, MaxHp = 40, Moves = new List<string> { "tackle", "ember" } },
                    new DuelMonsterInfo { SpeciesId = 2, Level = 8, Hp = 30, MaxHp = 30, Moves = new List<string> { "tackle" } },
                },
                Opponent = new DuelMonsterInfo { SpeciesId = 4, Level = 12, Hp = 50, MaxHp = 50 },
            };
            return new PvpReplica(_data, start);
        }

        [Test]
        public void Damage_LowersTheRightSidesHp_AndNeverBelowZero()
        {
            var r = Replica();
            var e1 = r.Apply(Msg(NetMsgType.Duel.Damage, 0, 15));
            var e2 = r.Apply(Msg(NetMsgType.Duel.Damage, 1, 80));
            Assert.AreEqual(25, r.Active.Hp);
            Assert.AreEqual(0, r.Opp.Hp);
            Assert.AreEqual(Side.Player, e1.Side, "나를 맞췄다");
            Assert.AreEqual(Side.Enemy, e2.Side);
            Assert.AreEqual(15, e1.Amount);
        }

        [Test]
        public void MyPotion_HealsThatPartyMember_AndSpendsOne()
        {
            var r = Replica();
            r.Mine[1].Hp = 5;
            var e = r.Apply(Msg(NetMsgType.Duel.PotionUsed, 0, 25, partyIndex: 1));
            Assert.AreEqual(30, r.Mine[1].Hp);
            Assert.AreEqual(2, r.Potions);
            Assert.AreEqual(BattleEventKind.PotionUsed, e.Kind);
            Assert.AreEqual(1, e.PartyIndex);
            Assert.AreEqual(25, e.Amount);
        }

        [Test]
        public void OpponentPotion_OnTheActiveMonster_RestoresItsBar_ElseOnlyShowsTheText()
        {
            var r = Replica();
            r.Opp.Hp = 10;
            var healed = new DuelMonsterInfo { SpeciesId = 4, Level = 12, Hp = 40, MaxHp = 50 };
            var e = r.Apply(Msg(NetMsgType.Duel.PotionUsed, 1, 30, monster: healed, targetActive: true));
            Assert.AreEqual(40, r.Opp.Hp);
            Assert.AreEqual(Side.Enemy, e.Side);
            Assert.AreEqual(_data.GetSpecies(4).Name, e.Name);

            r.Opp.Hp = 10;
            r.Apply(Msg(NetMsgType.Duel.PotionUsed, 1, 30, monster: new DuelMonsterInfo { SpeciesId = 2, Level = 5, Hp = 30, MaxHp = 30 }, targetActive: false));
            Assert.AreEqual(10, r.Opp.Hp, "벤치 몬스터가 회복해도 화면의 상대 HP 는 그대로");
            Assert.AreEqual(3, r.Potions, "내 상처약 개수는 상대 사용과 무관");
        }

        [Test]
        public void SwitchIn_ChangesTheActiveMonsterOfThatSide()
        {
            var r = Replica();
            var mine = r.Apply(Msg(NetMsgType.Duel.SwitchIn, 0, partyIndex: 1));
            Assert.AreEqual(1, r.ActiveIndex);
            Assert.AreEqual(2, r.Active.SpeciesId);
            Assert.AreEqual(BattleEventKind.SwitchIn, mine.Kind);
            Assert.AreEqual(Side.Player, mine.Side);

            var theirs = r.Apply(Msg(NetMsgType.Duel.SwitchIn, 1, monster: new DuelMonsterInfo { SpeciesId = 6, Level = 9, Hp = 20, MaxHp = 33 }));
            Assert.AreEqual(6, r.Opp.SpeciesId);
            Assert.AreEqual(20, r.Opp.Hp);
            Assert.AreEqual(33, r.Opp.MaxHp);
            Assert.AreEqual(Side.Enemy, theirs.Side);
        }

        [Test]
        public void SwitchOut_KeepsTheSideOfWhoWithdrew()
        {
            var r = Replica();
            var mine = r.Apply(Msg(NetMsgType.Duel.SwitchOut, 0, partyIndex: 0));
            Assert.AreEqual(BattleEventKind.SwitchOut, mine.Kind);
            Assert.AreEqual(Side.Player, mine.Side);
            Assert.AreEqual(Side.Enemy, r.Apply(Msg(NetMsgType.Duel.SwitchOut, 1)).Side);
        }

        [Test]
        public void Forfeit_UnknownKinds_AndBadIndexes_AreIgnoredWithoutChangingState()
        {
            var r = Replica();
            Assert.IsNull(r.Apply(Msg(NetMsgType.Duel.Forfeit, 0)));
            Assert.IsNull(r.Apply(Msg("garbage", 0)));
            Assert.IsNull(r.Apply(Msg(NetMsgType.Duel.SwitchIn, 0, partyIndex: 9)));
            Assert.IsNull(r.Apply(Msg(NetMsgType.Duel.SwitchIn, 1)), "상대 SwitchIn 인데 모습이 없다");
            Assert.IsNull(r.Apply(Msg(NetMsgType.Duel.PotionUsed, 0, 5, partyIndex: -1)));
            Assert.AreEqual(0, r.ActiveIndex);
            Assert.AreEqual(3, r.Potions);
            Assert.AreEqual(4, r.Opp.SpeciesId);
        }

        [Test]
        public void ShowLead_PutsTheFirstPartyMemberOnScreen()
        {
            var r = Replica();
            r.Apply(Msg(NetMsgType.Duel.SwitchIn, 0, partyIndex: 1));
            r.ShowLead();
            Assert.AreEqual(0, r.ActiveIndex);
        }

        [Test]
        public void Wire_FlipsSidesPerViewer_HidesOpponentsPartyIndex_AndSkipsEnded()
        {
            var sw = new PvpEvent { Kind = PvpEventKind.SwitchIn, Side = DuelSide.B, PartyIndex = 2, SpeciesId = 4, Level = 7, Hp = 11, MaxHp = 22 };
            var asA = DuelEventMessage.From(sw, DuelSide.A);
            var asB = DuelEventMessage.From(sw, DuelSide.B);
            Assert.AreEqual(1, asA.Side);
            Assert.AreEqual(-1, asA.PartyIndex, "상대의 파티 칸은 알려 주지 않는다");
            Assert.AreEqual(4, asA.Monster.SpeciesId);
            Assert.AreEqual(11, asA.Monster.Hp);
            Assert.AreEqual(0, asB.Side);
            Assert.AreEqual(2, asB.PartyIndex);
            Assert.IsNull(asB.Monster, "내 사건에는 모습을 싣지 않는다(내 복제본이 이미 안다)");

            Assert.IsNull(DuelEventMessage.From(new PvpEvent { Kind = PvpEventKind.Ended, Winner = DuelSide.A }, DuelSide.A));
        }

        [Test]
        public void ActionMessages_RoundTrip_AndUnknownTypeIsRejected()
        {
            Assert.AreEqual("ember", ((MoveAction)DuelActionMessage.From(new MoveAction("ember")).ToAction()).MoveId);
            Assert.AreEqual(2, ((PotionAction)DuelActionMessage.From(new PotionAction(2)).ToAction()).PartyIndex);
            Assert.AreEqual(3, ((SwitchAction)DuelActionMessage.From(new SwitchAction(3)).ToAction()).PartyIndex);
            Assert.IsInstanceOf<FleeAction>(DuelActionMessage.From(new FleeAction()).ToAction());
            Assert.IsNull(new DuelActionMessage { Type = "ball" }.ToAction());
            Assert.IsNull(new DuelActionMessage { Type = null }.ToAction());
            Assert.IsNull(new DuelActionMessage { Type = NetMsgType.DuelActionType.Move, MoveId = null }.ToAction());
        }

        [Test]
        public void ReplayingAFullBattleThroughTheWire_KeepsBothReplicasInSyncWithTheServer()
        {
            var rng = new SystemRng(11);
            var a = new[] { Monster.Create(_data, 0, 14), Monster.Create(_data, 2, 9), Monster.Create(_data, 4, 7) };
            var b = new[] { Monster.Create(_data, 4, 13), Monster.Create(_data, 6, 11) };
            var battle = new PvpBattle(_data, a, b, rng);
            var replicas = new[] { new PvpReplica(_data, StartFor(battle, DuelSide.A)), new PvpReplica(_data, StartFor(battle, DuelSide.B)) };
            var viewers = new[] { DuelSide.A, DuelSide.B };

            void Sync(List<PvpEvent> events)
            {
                foreach (var e in events)
                    for (int v = 0; v < 2; v++)
                    {
                        var m = DuelEventMessage.From(e, viewers[v]);
                        if (m != null) replicas[v].Apply(m);
                    }
                for (int v = 0; v < 2; v++)
                {
                    var viewer = viewers[v]; var replica = replicas[v];
                    var other = PvpBattle.Other(viewer);
                    Assert.AreEqual(battle.ActiveIndex(viewer), replica.ActiveIndex, $"{viewer} 출전 칸");
                    Assert.AreEqual(battle.Potions(viewer), replica.Potions, $"{viewer} 상처약");
                    for (int i = 0; i < replica.Mine.Count; i++)
                        Assert.AreEqual(battle.Party(viewer)[i].Hp, replica.Mine[i].Hp, $"{viewer} 파티 {i} HP");
                    Assert.AreEqual(battle.Active(other).SpeciesId, replica.Opp.SpeciesId, $"{viewer} 가 보는 상대 종족");
                    Assert.AreEqual(battle.Active(other).Hp, replica.Opp.Hp, $"{viewer} 가 보는 상대 HP");
                }
            }

            for (int round = 0; round < 200 && !battle.IsOver; round++)
            {
                int r = round;
                BattleAction Pick(DuelSide s)
                {
                    var party = battle.Party(s).ToList();
                    int hurt = party.FindIndex(m => !m.IsFainted && m.Hp < m.MaxHp);
                    if (r % 3 == 2 && hurt >= 0 && battle.WhyNot(s, new PotionAction(hurt)) == null) return new PotionAction(hurt);
                    int bench = party.FindIndex(m => !m.IsFainted && m != battle.Active(s));
                    if (r % 5 == 4 && bench >= 0) return new SwitchAction(bench);
                    return new MoveAction(battle.Active(s).Moves[r % 2]);
                }
                Sync(battle.ResolveRound(Pick(DuelSide.A), Pick(DuelSide.B)));
                while (battle.Phase == PvpPhase.AwaitingReplacement)
                {
                    var side = battle.ReplacingSide.Value;
                    Sync(battle.SubmitReplacement(side, battle.Party(side).ToList().FindIndex(m => !m.IsFainted)));
                }
            }
            Assert.IsTrue(battle.IsOver);
        }
    }
}
