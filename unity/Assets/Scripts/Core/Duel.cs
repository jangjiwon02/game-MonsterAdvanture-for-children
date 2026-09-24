using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>어느 결투자인지(플레이어 개념이 아니라 자리 표시). A/B 는 대칭이라 어느 쪽이 먼저인지는 의미가 없다.</summary>
    public enum DuelSide { A, B }

    public enum DuelEventKind { MoveUsed, Missed, Damage, Fainted, Ended }

    public sealed class DuelEvent
    {
        public DuelEventKind Kind;
        public DuelSide Side;       // MoveUsed: 누가 썼는지 / Damage·Fainted: 누가 맞았는지
        public string MoveId;
        public int Amount;
        public double Multiplier = 1.0;
        public bool Critical;
        public DuelSide? Winner;    // Ended 에서만 쓴다
    }

    /// <summary>
    /// 트레이너 대 트레이너 1:1 대결(SPEC "실제 PvP" 1단계). 몬스터 1마리씩, 기술만 고른다 —
    /// 파티 교체·아이템·포획·도망은 이 범위에 없다. 소켓을 몰라서 서버가 그대로 판정에 쓸 수 있다.
    /// 두 쪽 모두 사람이 조작하므로, 한 라운드는 양쪽이 기술을 고른 뒤 <see cref="ResolveRound"/> 로 한 번에 처리한다.
    /// </summary>
    public sealed class Duel
    {
        readonly GameData _data;
        readonly IRng _rng;

        public Monster A { get; }
        public Monster B { get; }
        public bool IsOver { get; private set; }
        public DuelSide? Winner { get; private set; }

        public Duel(GameData data, Monster a, Monster b, IRng rng)
        {
            _data = data; A = a; B = b; _rng = rng;
        }

        public Monster Of(DuelSide side) => side == DuelSide.A ? A : B;
        static DuelSide Other(DuelSide side) => side == DuelSide.A ? DuelSide.B : DuelSide.A;

        /// <summary>이 결투자가 지금 이 기술을 쓸 수 없는 이유. 쓸 수 있으면 null.</summary>
        public string WhyNotMove(DuelSide side, string moveId) =>
            Of(side).Moves.Contains(moveId) ? null : "배우지 않은 기술이다!";

        /// <summary>양쪽이 고른 기술로 한 라운드를 판정한다. 스피드가 빠른 쪽이 먼저(동률이면 무작위).</summary>
        public IEnumerable<DuelEvent> ResolveRound(string moveA, string moveB)
        {
            if (IsOver) throw new InvalidOperationException("이미 끝난 대결이다.");
            var moveOf = new Dictionary<DuelSide, string> { [DuelSide.A] = moveA, [DuelSide.B] = moveB };
            bool aFirst = EnemyAI.PlayerMovesFirst(A.Speed, B.Speed, _rng);
            foreach (var side in aFirst ? new[] { DuelSide.A, DuelSide.B } : new[] { DuelSide.B, DuelSide.A })
            {
                foreach (var e in UseMove(side, moveOf[side])) yield return e;
                if (IsOver) yield break;
            }
        }

        IEnumerable<DuelEvent> UseMove(DuelSide side, string moveId)
        {
            var attacker = Of(side);
            if (attacker.IsFainted) yield break;   // 같은 라운드 상대 선공에 이미 쓰러졌다

            var defenderSide = Other(side);
            var defender = Of(defenderSide);
            var move = _data.GetMove(moveId);
            yield return new DuelEvent { Kind = DuelEventKind.MoveUsed, Side = side, MoveId = moveId };

            var result = DamageCalc.Roll(_data, attacker, defender, move, _rng);
            if (result.Missed)
            {
                yield return new DuelEvent { Kind = DuelEventKind.Missed, Side = side, MoveId = moveId };
                yield break;
            }
            defender.Hp = Math.Max(0, defender.Hp - result.Damage);
            yield return new DuelEvent
            {
                Kind = DuelEventKind.Damage, Side = defenderSide, MoveId = moveId,
                Amount = result.Damage, Multiplier = result.Multiplier, Critical = result.Critical,
            };
            if (defender.IsFainted)
            {
                yield return new DuelEvent { Kind = DuelEventKind.Fainted, Side = defenderSide };
                IsOver = true; Winner = side;
                yield return new DuelEvent { Kind = DuelEventKind.Ended, Winner = side };
            }
        }
    }
}
