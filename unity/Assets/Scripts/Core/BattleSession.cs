using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>
    /// 야생 전투 한 판(SPEC "턴 진행"). ResolveTurn 은 지연 평가 이터레이터라서, 호출자가 이벤트를
    /// 하나 소비할 때마다 그 지점까지만 상태가 바뀐다. 아군이 쓰러져 교체가 필요하면
    /// ReplacementNeeded 이벤트에서 멈추고, ChooseReplacement 를 호출한 뒤 계속 소비하면 된다.
    ///
    /// 옵트인 기능(기본값이면 기존 동작·난수 소비 순서 그대로):
    ///  - 기술 우선도: GameData.HasPriorityMoves 일 때만 우선도 큐로 턴 순서를 정한다.
    ///  - 옵저버: Observers 가 비어 있으면 훅이 하나도 불리지 않는다.
    ///  - 포획: CaptureMode.Legacy(기본)는 Capture.Attempt 그대로.
    /// </summary>
    public sealed class BattleSession
    {
        readonly GameData _data;
        readonly IRng _rng;
        int _replacement = -1;
        bool _turnOver;
        bool _started;

        public PlayerState Player { get; }
        public Monster Enemy { get; }
        public int ActiveIndex { get; private set; }
        public int FleeTries { get; private set; }
        public BattleOutcome Outcome { get; private set; } = BattleOutcome.Ongoing;
        public BattlePhase Phase { get; private set; } = BattlePhase.AwaitingAction;
        /// <summary>지금까지 시작한 턴 수(진행 중인 턴 포함).</summary>
        public int Turn { get; private set; }

        public GameData Data => _data;
        public Monster Active => Player.Party[ActiveIndex];
        public bool IsOver => Outcome != BattleOutcome.Ongoing;
        public Monster MonsterOf(Side side) => side == Side.Player ? Active : Enemy;

        /// <summary>포획 판정 방식. 기본 Legacy.</summary>
        public CaptureMode CaptureMode { get; set; } = CaptureMode.Legacy;
        /// <summary>ThreeShake 에서 쓰는 볼 계수·상태이상 계수(볼 종류·상태이상이 생기면 호출자가 채운다).</summary>
        public double BallMultiplier { get; set; } = 1.0;
        public double StatusMultiplier { get; set; } = 1.0;

        /// <summary>날씨·특성·도구 관찰자. 리스트 순서대로 훅이 불린다. 비어 있으면 아무 일도 안 일어난다.</summary>
        public List<BattleObserver> Observers { get; } = new List<BattleObserver>();

        public BattleSession(GameData data, PlayerState player, Monster enemy, IRng rng)
        {
            _data = data; _rng = rng; Player = player; Enemy = enemy;
            ActiveIndex = player.FirstUsableIndex;
            if (ActiveIndex < 0) throw new InvalidOperationException("싸울 수 있는 몬스터가 없다.");
        }

        /// <summary>
        /// 어느 쪽 몬스터에 붙은 특성/도구 관찰자를 등록한다. holder 를 주면 그 몬스터가 출전 중일 때만 효과가 있고
        /// (교체하면 꺼짐), 생략하면 그 쪽이 누구든 계속 적용된다. 저장/직렬화는 하지 않는다.
        /// </summary>
        public void AddObserver(Side side, BattleObserver observer, Monster holder = null)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            if (Observers.Contains(observer)) throw new InvalidOperationException("이미 등록된 관찰자다.");
            observer.OwnerSide = side;
            observer.Holder = holder;
            Observers.Add(observer);
        }

        /// <summary>ReplacementNeeded 이벤트 직후, 다음으로 내보낼 몬스터를 고른다.</summary>
        public void ChooseReplacement(int partyIndex)
        {
            if (partyIndex < 0 || partyIndex >= Player.Party.Count || Player.Party[partyIndex].IsFainted)
                throw new ArgumentException("내보낼 수 없는 몬스터다.", nameof(partyIndex));
            if (Phase != BattlePhase.AwaitingReplacement)
                throw new InvalidOperationException($"교체를 고를 차례가 아니다({Phase}).");
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

        /// <summary>
        /// 전투 시작 훅(OnBattleStart)의 이벤트. 첫 ResolveTurn 이 알아서 부르지만, 첫 행동을 고르기 전에
        /// 날씨 문구 등을 보여주고 싶으면 UI 가 먼저 이 이터레이터를 소비하면 된다. 두 번 불러도 한 번만 실행된다.
        /// </summary>
        public IEnumerable<BattleEvent> Begin()
        {
            if (_started) yield break;
            _started = true;
            foreach (var e in Notify((o, emit) => o.OnBattleStart(this, emit))) yield return e;
        }

        public IEnumerable<BattleEvent> ResolveTurn(BattleAction action)
        {
            if (IsOver) throw new InvalidOperationException("이미 끝난 전투다.");
            if (Phase != BattlePhase.AwaitingAction)
                throw new InvalidOperationException($"지금은 행동을 받을 수 없다({Phase}). 이전 턴의 이벤트를 끝까지 소비해야 한다.");
            string why = WhyNot(action);
            if (why != null) throw new InvalidOperationException(why);
            Phase = BattlePhase.ResolvingTurn;
            Turn++;
            _turnOver = false;

            foreach (var e in Begin()) yield return e;
            foreach (var e in Notify((o, emit) => o.OnTurnStart(this, emit))) yield return e;

            bool enemyFollows = true;   // 기술 외 행동은 먼저 실행되고 이어서 적이 공격한다
            switch (action)
            {
                case FleeAction _:
                    FleeTries++;
                    if (_rng.NextDouble() < Flee.Chance(Active.Speed, Enemy.Speed, FleeTries))
                    {
                        Finish(BattleOutcome.Fled);
                        yield return new BattleEvent { Kind = BattleEventKind.FleeSucceeded };
                        yield break;
                    }
                    yield return new BattleEvent { Kind = BattleEventKind.FleeFailed };
                    break;

                case BallAction b:
                    Player.Balls--;
                    yield return new BattleEvent { Kind = BattleEventKind.BallThrown };
                    if (!b.Hit)
                    {
                        yield return new BattleEvent { Kind = BattleEventKind.BallMissed };
                        break;
                    }
                    var attempt = CaptureMode == CaptureMode.ThreeShake
                        ? CatchMath.Attempt(_data, Enemy, _rng, BallMultiplier, StatusMultiplier, b.ThrowQuality)
                        : Capture.Attempt(_data, Enemy, _rng);
                    yield return new BattleEvent { Kind = BattleEventKind.CatchAttempt, Catch = attempt };
                    if (attempt.Caught)
                    {
                        bool toBox = Player.AddCaught(Enemy);
                        Finish(BattleOutcome.Caught);
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
                    enemyFollows = false;
                    foreach (var e in MoveRound(m.MoveId)) yield return e;
                    break;
            }

            if (enemyFollows)
            {
                foreach (var e in UseMove(Side.Enemy, EnemyAI.PickMove(_data, Enemy, Active, _rng))) yield return e;
                foreach (var e in FaintCheck()) yield return e;
            }

            if (!IsOver && Observers.Count > 0)
            {
                foreach (var e in Notify((o, emit) => o.OnTurnEnd(this, emit))) yield return e;
                // 턴 끝 피해(날씨 등)로 쓰러진 몬스터 처리.
                foreach (var e in FaintCheck()) yield return e;
            }
            if (!IsOver) Phase = BattlePhase.AwaitingAction;
        }

        /// <summary>
        /// 기술을 고른 턴. 기본은 스피드가 빠른 쪽이 먼저(동률이면 무작위)이고 적 기술은 첫 공격 뒤에 고른다(기존 경로).
        /// GameData.HasPriorityMoves 면 양쪽 기술을 먼저 확정한 뒤 우선도 큐(우선도 > 스피드 > 50:50)로 순서를 정한다.
        /// 각 공격 직후 기절 판정.
        /// </summary>
        IEnumerable<BattleEvent> MoveRound(string playerMove)
        {
            string enemyMove = null;
            bool playerFirst;
            if (_data.HasPriorityMoves)
            {
                enemyMove = EnemyAI.PickMove(_data, Enemy, Active, _rng);   // 적 기술 선택 난수는 순서 결정보다 먼저
                playerFirst = TurnOrder.FirstMovesFirst(
                    _data.GetMove(playerMove).Priority, Active.Speed, _data.GetMove(enemyMove).Priority, Enemy.Speed, _rng);
            }
            else playerFirst = EnemyAI.PlayerMovesFirst(Active.Speed, Enemy.Speed, _rng);

            foreach (var side in playerFirst ? new[] { Side.Player, Side.Enemy } : new[] { Side.Enemy, Side.Player })
            {
                string moveId = side == Side.Player ? playerMove : (enemyMove ?? EnemyAI.PickMove(_data, Enemy, Active, _rng));
                foreach (var e in UseMove(side, moveId)) yield return e;
                foreach (var e in FaintCheck()) yield return e;
                if (_turnOver) yield break;
            }
        }

        IEnumerable<BattleEvent> UseMove(Side side, string moveId)
        {
            var attacker = side == Side.Player ? Active : Enemy;
            var defender = side == Side.Player ? Enemy : Active;
            var move = _data.GetMove(moveId);
            yield return new BattleEvent { Kind = BattleEventKind.MoveUsed, Side = side, MoveId = moveId };
            foreach (var e in Notify((o, emit) => o.OnMoveUsed(this, side, move, emit))) yield return e;

            var result = DamageCalc.Roll(_data, attacker, defender, move, _rng);
            if (result.Missed)
            {
                yield return new BattleEvent { Kind = BattleEventKind.Missed, Side = side, MoveId = moveId };
                yield break;
            }
            int damage = result.Damage;
            List<BattleEvent> notices = null;
            if (Observers.Count > 0) damage = ModifyDamage(side, attacker, defender, move, damage, out notices);
            defender.Hp = Math.Max(0, defender.Hp - damage);
            yield return new BattleEvent
            {
                Kind = BattleEventKind.Damage, Side = side == Side.Player ? Side.Enemy : Side.Player,
                MoveId = moveId, Amount = damage, Multiplier = result.Multiplier, Critical = result.Critical,
            };
            if (notices != null) foreach (var n in notices) yield return n;
            foreach (var e in Notify((o, emit) => o.OnDamageDealt(this, side, move, damage, emit))) yield return e;
        }

        /// <summary>관찰자 배율을 연쇄로 곱해 최종 데미지를 낸다. 0 배면 0(면역), 그 외엔 내림하되 최소 1.</summary>
        int ModifyDamage(Side side, Monster attacker, Monster defender, MoveData move, int damage, out List<BattleEvent> notices)
        {
            notices = null;
            if (damage <= 0) return damage;
            var ctx = new DamageContext
            {
                Session = this, AttackerSide = side, Attacker = attacker, Defender = defender, Move = move, BaseDamage = damage,
            };
            foreach (var o in Observers.ToArray())
                if (IsAttached(o)) o.OnModifyDamage(ctx);
            if (ctx.Notices.Count > 0) notices = ctx.Notices;
            if (ctx.Multiplier == 1.0) return damage;
            if (ctx.Multiplier <= 0) return 0;
            return Math.Max(1, (int)Math.Floor(damage * ctx.Multiplier));
        }

        IEnumerable<BattleEvent> FaintCheck()
        {
            if (Enemy.IsFainted)
            {
                yield return new BattleEvent { Kind = BattleEventKind.Fainted, Side = Side.Enemy };
                foreach (var e in Notify((o, emit) => o.OnFaint(this, Side.Enemy, emit))) yield return e;
                int exp = Growth.ExpReward(Enemy.Species(_data), Enemy.Level);
                yield return new BattleEvent { Kind = BattleEventKind.ExpGained, Amount = exp };
                // 경험치는 출전 중인 몬스터만 받는다. 성장 결과는 이 이벤트들을 소비하는 시점에 반영된다.
                foreach (var g in Growth.GainExp(_data, Active, exp))
                    yield return new BattleEvent { Kind = BattleEventKind.Growth, Growth = g };
                Growth.GainEVs(_data, Active, Enemy.Species(_data));   // 노력치(조용히 반영 — 웹 대사에는 안 나옴)
                int money = Growth.MoneyReward(Enemy.Level, _rng);
                Player.Money += money;
                Finish(BattleOutcome.Win);
                _turnOver = true;
                yield return new BattleEvent { Kind = BattleEventKind.MoneyGained, Amount = money };
                yield break;
            }
            if (!Active.IsFainted) yield break;

            yield return new BattleEvent { Kind = BattleEventKind.Fainted, Side = Side.Player };
            _turnOver = true;
            foreach (var e in Notify((o, emit) => o.OnFaint(this, Side.Player, emit))) yield return e;
            if (!Player.HasUsableMonster)
            {
                Finish(BattleOutcome.Lose);
                yield return new BattleEvent { Kind = BattleEventKind.BlackedOut };
                yield break;
            }
            Phase = BattlePhase.AwaitingReplacement;
            yield return new BattleEvent { Kind = BattleEventKind.ReplacementNeeded };
            if (_replacement < 0) throw new InvalidOperationException("ReplacementNeeded 뒤에는 ChooseReplacement 를 호출해야 한다.");
            ActiveIndex = _replacement;
            _replacement = -1;
            Phase = BattlePhase.ResolvingTurn;
            yield return new BattleEvent { Kind = BattleEventKind.SwitchIn, PartyIndex = ActiveIndex };
        }

        void Finish(BattleOutcome outcome)
        {
            Outcome = outcome;
            Phase = BattlePhase.Ended;
        }

        bool IsAttached(BattleObserver o) =>
            o.OwnerSide == null || o.Holder == null || MonsterOf(o.OwnerSide.Value) == o.Holder;

        /// <summary>등록된(그리고 지금 유효한) 관찰자에게 훅을 하나씩 부르고, 낸 이벤트를 차례로 흘려보낸다. 관찰자가 없으면 아무것도 안 한다.</summary>
        IEnumerable<BattleEvent> Notify(Action<BattleObserver, List<BattleEvent>> hook)
        {
            if (Observers.Count == 0) yield break;
            foreach (var o in Observers.ToArray())   // 훅 안에서 스스로 빠질 수 있으니 복사본으로 돈다
            {
                if (!IsAttached(o)) continue;
                var emit = new List<BattleEvent>();
                hook(o, emit);
                foreach (var e in emit) yield return e;
            }
        }
    }
}
