using System;
using System.Collections.Generic;
using MonsterAdventure.Core;

namespace MonsterAdventure.Net
{
    /// <summary>
    /// 클라이언트가 들고 있는 대결의 복제본. 서버가 보낸 사건을 재생하는 시점에 맞춰 <see cref="Apply"/> 로 HP·출전 몬스터를
    /// 바꿔 주면, 화면(BattleController)은 단일 플레이처럼 모델 값을 읽기만 해도 HP 바가 사건 순서대로 움직인다.
    /// 규칙은 계산하지 않는다 — 서버가 보낸 값을 그대로 반영할 뿐이다.
    /// </summary>
    public sealed class PvpReplica
    {
        readonly GameData _data;

        public string OpponentName { get; }
        /// <summary>내 파티(서버가 만든 임시 풀피 사본의 복제). 능력치는 HP 외에는 채우지 않는다.</summary>
        public List<Monster> Mine { get; }
        public int ActiveIndex { get; private set; }
        /// <summary>상대의 출전 몬스터(파티 전체는 모른다).</summary>
        public Monster Opp { get; private set; }
        public int Potions { get; private set; }

        public Monster Active => Mine[ActiveIndex];

        public PvpReplica(GameData data, DuelStartMessage start)
        {
            _data = data;
            OpponentName = start.OpponentName;
            Mine = new List<Monster>();
            foreach (var i in start.Party) Mine.Add(ToMonster(i));
            Opp = ToMonster(start.Opponent);
            Potions = start.Potions;
        }

        static Monster ToMonster(DuelMonsterInfo i) => new Monster
        {
            SpeciesId = i.SpeciesId, Level = i.Level, Exp = i.Exp, Hp = i.Hp, MaxHp = i.MaxHp,
            Moves = i.Moves != null ? new List<string>(i.Moves) : new List<string>(),
        };

        /// <summary>대결이 끝난 뒤 성장(경험치)은 선두 몬스터가 받으므로, 화면도 선두를 보여 준다.</summary>
        public void ShowLead() => ActiveIndex = 0;

        static Side SideOf(DuelEventMessage e) => e.Side == 0 ? Side.Player : Side.Enemy;

        /// <summary>
        /// 사건 하나를 복제본에 반영하고, 단일 플레이 전투와 같은 문구·연출로 재생할 BattleEvent 를 돌려준다
        /// (BattleEvent.Side: Player = 나, Enemy = 상대). 대응하는 BattleEvent 가 없으면(기권) null.
        /// 모르는 종류거나 범위를 벗어난 값이면 상태를 건드리지 않고 null.
        /// </summary>
        public BattleEvent Apply(DuelEventMessage e)
        {
            var side = SideOf(e);
            bool mine = e.Side == 0;
            switch (e.Kind)
            {
                case NetMsgType.Duel.MoveUsed:
                    return new BattleEvent { Kind = BattleEventKind.MoveUsed, Side = side, MoveId = e.MoveId };
                case NetMsgType.Duel.Missed:
                    return new BattleEvent { Kind = BattleEventKind.Missed, Side = side, MoveId = e.MoveId };

                case NetMsgType.Duel.Damage:
                {
                    var target = mine ? Active : Opp;
                    target.Hp = Math.Max(0, target.Hp - e.Amount);
                    return new BattleEvent
                    {
                        Kind = BattleEventKind.Damage, Side = side, MoveId = e.MoveId,
                        Amount = e.Amount, Multiplier = e.Multiplier, Critical = e.Critical,
                    };
                }
                case NetMsgType.Duel.Fainted:
                    return new BattleEvent { Kind = BattleEventKind.Fainted, Side = side };
                case NetMsgType.Duel.ReplacementNeeded:
                    return new BattleEvent { Kind = BattleEventKind.ReplacementNeeded, Side = side };

                case NetMsgType.Duel.SwitchOut:
                    return new BattleEvent { Kind = BattleEventKind.SwitchOut, Side = side, PartyIndex = mine ? ActiveIndex : 0 };
                case NetMsgType.Duel.SwitchIn:
                    if (mine)
                    {
                        if (e.PartyIndex < 0 || e.PartyIndex >= Mine.Count) return null;
                        ActiveIndex = e.PartyIndex;
                    }
                    else
                    {
                        if (e.Monster == null) return null;
                        Opp = ToMonster(e.Monster);
                    }
                    return new BattleEvent { Kind = BattleEventKind.SwitchIn, Side = side, PartyIndex = mine ? ActiveIndex : 0 };

                case NetMsgType.Duel.PotionUsed:
                    if (mine)
                    {
                        if (e.PartyIndex < 0 || e.PartyIndex >= Mine.Count) return null;
                        var t = Mine[e.PartyIndex];
                        t.Hp = Math.Min(t.MaxHp, t.Hp + e.Amount);
                        Potions = Math.Max(0, Potions - 1);
                        return new BattleEvent { Kind = BattleEventKind.PotionUsed, Side = side, PartyIndex = e.PartyIndex, Amount = e.Amount };
                    }
                    if (e.Monster == null) return null;
                    if (e.TargetActive) Opp.Hp = Math.Min(Opp.MaxHp, e.Monster.Hp);
                    // 상대 파티 칸은 모른다 — 문구에 쓸 종족 이름만 Name 으로 넘긴다.
                    return new BattleEvent
                    {
                        Kind = BattleEventKind.PotionUsed, Side = side, Amount = e.Amount, Name = _data.GetSpecies(e.Monster.SpeciesId).Name,
                    };
            }
            return null;
        }
    }
}
