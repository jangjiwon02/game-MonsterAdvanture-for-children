using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>
    /// 야생 전투 한 판(SPEC "턴 진행"). ResolveTurn 은 지연 평가 이터레이터라서, 호출자가 이벤트를
    /// 하나 소비할 때마다 그 지점까지만 상태가 바뀐다. 아군이 쓰러져 교체가 필요하면
    /// ReplacementNeeded 이벤트에서 멈추고, ChooseReplacement 를 호출한 뒤 계속 소비하면 된다.
    /// </summary>
    public sealed class BattleSession
    {
        readonly GameData _data;
        readonly IRng _rng;
        int _replacement = -1;
        bool _turnOver;

        public PlayerState Player { get; }
        public Monster Enemy { get; }
        public int ActiveIndex { get; private set; }
        public int FleeTries { get; private set; }
        public BattleOutcome Outcome { get; private set; } = BattleOutcome.Ongoing;

        public Monster Active => Player.Party[ActiveIndex];
        public bool IsOver => Outcome != BattleOutcome.Ongoing;

        public BattleSession(GameData data, PlayerState player, Monster enemy, IRng rng)
        {
            _data = data; _rng = rng; Player = player; Enemy = enemy;
            ActiveIndex = player.FirstUsableIndex;
            if (ActiveIndex < 0) throw new InvalidOperationException("싸울 수 있는 몬스터가 없다.");
        }

        /// <summary>ReplacementNeeded 이벤트 직후, 다음으로 내보낼 몬스터를 고른다.</summary>
        public void ChooseReplacement(int partyIndex)
        {
            if (partyIndex < 0 || partyIndex >= Player.Party.Count || Player.Party[partyIndex].IsFainted)
                throw new ArgumentException("내보낼 수 없는 몬스터다.", nameof(partyIndex));
            _replacement = partyIndex;
        }

        /// <summary>이 행동을 지금 할 수 있는지(가방·교체 등). 불가능하면 이유를 돌려준다.</summary>
        public string WhyNot(BattleAction action)
        {
            switch (action)
            {
                case MoveAction m: return Active.Moves.Contains(m.MoveId) ? null : "배우지 않은 기술이다!";
                case BallAction _: return Player.Balls > 0 ? null : "몬스터볼이 없다!";
                case PotionAction p:
                    if (Player.Potions <= 0) return "상처약이 없다!";
                    var t = Player.Party[p.PartyIndex];
                    return t.IsFainted ? "기절한 몬스터에게는 쓸 수 없다!" : t.Hp >= t.MaxHp ? "이미 HP가 가득 찼다!" : null;
                case SwitchAction s:
                    var to = Player.Party[s.PartyIndex];
                    return to.IsFainted ? "기절한 몬스터는 싸울 수 없다!" : s.PartyIndex == ActiveIndex ? "이미 싸우고 있다!" : null;
                default: return null;
            }
        }

        public IEnumerable<BattleEvent> ResolveTurn(BattleAction action)
        {
            if (IsOver) throw new InvalidOperationException("이미 끝난 전투다.");
            string why = WhyNot(action);
            if (why != null) throw new InvalidOperationException(why);
            _turnOver = false;

            switch (action)
            {
                case FleeAction _:
                    FleeTries++;
                    if (_rng.NextDouble() < Flee.Chance(Active.Speed, Enemy.Speed, FleeTries))
                    {
                        Outcome = BattleOutcome.Fled;
                        yield return new BattleEvent { Kind = BattleEventKind.FleeSucceeded };
                        yield break;
                    }
                    yield return new BattleEvent { Kind = BattleEventKind.FleeFailed };
                    break;

                case BallAction _:
                    Player.Balls--;
                    yield return new BattleEvent { Kind = BattleEventKind.BallThrown };
                    var attempt = Capture.Attempt(_data, Enemy, _rng);
                    yield return new BattleEvent { Kind = BattleEventKind.CatchAttempt, Catch = attempt };
                    if (attempt.Caught)
                    {
                        bool toBox = Player.AddCaught(Enemy);
                        Outcome = BattleOutcome.Caught;
                        yield return new BattleEvent { Kind = BattleEventKind.Caught, SentToBox = toBox };
                        yield break;
                    }
                    break;

                case PotionAction p:
                    var target = Player.Party[p.PartyIndex];
                    int before = target.Hp;
                    Player.Potions--;
                    target.Heal(PlayerState.PotionHeal);
                    yield return new BattleEvent { Kind = BattleEventKind.PotionUsed, PartyIndex = p.PartyIndex, Amount = target.Hp - before };
                    break;

                case SwitchAction s:
                    yield return new BattleEvent { Kind = BattleEventKind.SwitchOut, PartyIndex = ActiveIndex };
                    ActiveIndex = s.PartyIndex;
                    yield return new BattleEvent { Kind = BattleEventKind.SwitchIn, PartyIndex = ActiveIndex };
                    break;

                case MoveAction m:
                    // 기술을 고르면 스피드가 빠른 쪽이 먼저(동률이면 무작위). 각 공격 직후 기절 판정.
                    bool playerFirst = EnemyAI.PlayerMovesFirst(Active.Speed, Enemy.Speed, _rng);
                    foreach (var side in playerFirst ? new[] { Side.Player, Side.Enemy } : new[] { Side.Enemy, Side.Player })
                    {
                        string moveId = side == Side.Player ? m.MoveId : EnemyAI.PickMove(_data, Enemy, Active, _rng);
                        foreach (var e in UseMove(side, moveId)) yield return e;
                        foreach (var e in FaintCheck()) yield return e;
                        if (_turnOver) yield break;
                    }
                    yield break;
            }

            // 기술 외 행동(도망 실패·볼 실패·아이템·교체)은 먼저 실행되고, 이어서 적이 공격한다.
            foreach (var e in UseMove(Side.Enemy, EnemyAI.PickMove(_data, Enemy, Active, _rng))) yield return e;
            foreach (var e in FaintCheck()) yield return e;
        }

        IEnumerable<BattleEvent> UseMove(Side side, string moveId)
        {
            var attacker = side == Side.Player ? Active : Enemy;
            var defender = side == Side.Player ? Enemy : Active;
            var move = _data.GetMove(moveId);
            yield return new BattleEvent { Kind = BattleEventKind.MoveUsed, Side = side, MoveId = moveId };

            var result = DamageCalc.Roll(_data, attacker, defender, move, _rng);
            if (result.Missed)
            {
                yield return new BattleEvent { Kind = BattleEventKind.Missed, Side = side, MoveId = moveId };
                yield break;
            }
            defender.Hp = Math.Max(0, defender.Hp - result.Damage);
            yield return new BattleEvent
            {
                Kind = BattleEventKind.Damage, Side = side == Side.Player ? Side.Enemy : Side.Player,
                MoveId = moveId, Amount = result.Damage, Multiplier = result.Multiplier, Critical = result.Critical,
            };
        }

        IEnumerable<BattleEvent> FaintCheck()
        {
            if (Enemy.IsFainted)
            {
                yield return new BattleEvent { Kind = BattleEventKind.Fainted, Side = Side.Enemy };
                int exp = Growth.ExpReward(Enemy.Species(_data), Enemy.Level);
                yield return new BattleEvent { Kind = BattleEventKind.ExpGained, Amount = exp };
                // 경험치는 출전 중인 몬스터만 받는다. 성장 결과는 이 이벤트들을 소비하는 시점에 반영된다.
                foreach (var g in Growth.GainExp(_data, Active, exp))
                    yield return new BattleEvent { Kind = BattleEventKind.Growth, Growth = g };
                Growth.GainEVs(_data, Active, Enemy.Species(_data));   // 노력치(조용히 반영 — 웹 대사에는 안 나옴)
                int money = Growth.MoneyReward(Enemy.Level, _rng);
                Player.Money += money;
                Outcome = BattleOutcome.Win;
                _turnOver = true;
                yield return new BattleEvent { Kind = BattleEventKind.MoneyGained, Amount = money };
                yield break;
            }
            if (!Active.IsFainted) yield break;

            yield return new BattleEvent { Kind = BattleEventKind.Fainted, Side = Side.Player };
            _turnOver = true;
            if (!Player.HasUsableMonster)
            {
                Outcome = BattleOutcome.Lose;
                yield return new BattleEvent { Kind = BattleEventKind.BlackedOut };
                yield break;
            }
            yield return new BattleEvent { Kind = BattleEventKind.ReplacementNeeded };
            if (_replacement < 0) throw new InvalidOperationException("ReplacementNeeded 뒤에는 ChooseReplacement 를 호출해야 한다.");
            ActiveIndex = _replacement;
            _replacement = -1;
            yield return new BattleEvent { Kind = BattleEventKind.SwitchIn, PartyIndex = ActiveIndex };
        }
    }
}
