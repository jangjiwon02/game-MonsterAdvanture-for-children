using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    public enum PvpEventKind
    {
        MoveUsed, Missed, Damage, Fainted, SwitchOut, SwitchIn, PotionUsed, ReplacementNeeded, Forfeit, Ended,
    }

    /// <summary>
    /// <see cref="BattleEvent"/> 와 같은 어휘의 PvP 사건. Side 대신 DuelSide.
    /// SwitchIn·PotionUsed 의 SpeciesId/Level/Hp/MaxHp 는 그 사건이 일어난 순간의 대상 몬스터 값이다
    /// (같은 라운드의 뒷 사건이 HP 를 더 바꿀 수 있어서, 상대 화면에 그리려면 시점이 고정된 값이 필요하다).
    /// </summary>
    public sealed class PvpEvent
    {
        public PvpEventKind Kind;
        public DuelSide Side;       // MoveUsed/Missed: 쓴 쪽 / Damage·Fainted: 맞은 쪽 / 나머지: 그 행동을 한 쪽
        public string MoveId;
        public int Amount;          // Damage: 피해 / PotionUsed: 실제 회복량
        public double Multiplier = 1.0;
        public bool Critical;
        public int PartyIndex;      // SwitchOut/SwitchIn/PotionUsed 의 파티 칸
        public bool TargetActive;   // PotionUsed: 대상이 출전 중인 몬스터였는지
        public int SpeciesId, Level, Hp, MaxHp;
        public DuelSide? Winner;    // Ended 에서만

        public override string ToString() => $"{Kind}({Side})";
    }

    public enum PvpPhase { AwaitingActions, AwaitingReplacement, Ended }

    /// <summary>
    /// 트레이너 대 트레이너 파티 대결. 양쪽이 한 라운드에 행동 하나씩(기술·상처약·교체·기권)을 내면 서버가
    /// <see cref="ResolveRound"/> 로 한 번에 판정한다. 소켓·Unity 를 모른다. 몬스터가 쓰러지면
    /// ReplacementNeeded 에서 멈추고 <see cref="SubmitReplacement"/> 를 기다린다. 볼·도망은 없다(FleeAction = 기권).
    /// </summary>
    public sealed class PvpBattle
    {
        public const int StartPotions = 3;

        readonly GameData _data;
        readonly IRng _rng;
        readonly List<Monster>[] _party = new List<Monster>[2];
        readonly int[] _active = new int[2];
        readonly int[] _potions = new int[2];

        public PvpPhase Phase { get; private set; } = PvpPhase.AwaitingActions;
        public DuelSide? Winner { get; private set; }
        /// <summary>Phase 가 AwaitingReplacement 일 때 다음 몬스터를 골라야 하는 쪽.</summary>
        public DuelSide? ReplacingSide { get; private set; }
        public bool IsOver => Phase == PvpPhase.Ended;

        public PvpBattle(GameData data, IList<Monster> partyA, IList<Monster> partyB, IRng rng, int potions = StartPotions)
        {
            _data = data; _rng = rng;
            _party[0] = new List<Monster>(partyA);
            _party[1] = new List<Monster>(partyB);
            foreach (var side in new[] { DuelSide.A, DuelSide.B })
            {
                int i = Index(side);
                _active[i] = _party[i].FindIndex(m => !m.IsFainted);
                if (_active[i] < 0) throw new ArgumentException("싸울 수 있는 몬스터가 없다.");
                _potions[i] = potions;
            }
        }

        static int Index(DuelSide s) => s == DuelSide.A ? 0 : 1;
        public static DuelSide Other(DuelSide s) => s == DuelSide.A ? DuelSide.B : DuelSide.A;

        public IReadOnlyList<Monster> Party(DuelSide side) => _party[Index(side)];
        public int ActiveIndex(DuelSide side) => _active[Index(side)];
        public Monster Active(DuelSide side) => _party[Index(side)][_active[Index(side)]];
        /// <summary>출전 중인(대결이 끝났으면 마지막으로 나왔던) 몬스터.</summary>
        public Monster Of(DuelSide side) => Active(side);
        public int Potions(DuelSide side) => _potions[Index(side)];

        bool HasUsable(DuelSide side) => _party[Index(side)].Exists(m => !m.IsFainted);

        /// <summary>이 행동을 지금 낼 수 없는 이유. 낼 수 있으면 null.</summary>
        public string WhyNot(DuelSide side, BattleAction action)
        {
            if (Phase != PvpPhase.AwaitingActions) return "지금은 행동을 고를 수 없다!";
            var party = _party[Index(side)];
            switch (action)
            {
                case MoveAction m: return Active(side).Moves.Contains(m.MoveId) && _data.HasMove(m.MoveId) ? null : "배우지 않은 기술이다!";
                case PotionAction p:
                    if (_potions[Index(side)] <= 0) return "상처약이 없다!";
                    if (p.PartyIndex < 0 || p.PartyIndex >= party.Count) return "그런 몬스터는 없다!";
                    var t = party[p.PartyIndex];
                    return t.IsFainted ? "기절한 몬스터에게는 쓸 수 없다!" : t.Hp >= t.MaxHp ? "이미 HP가 가득 찼다!" : null;
                case SwitchAction s:
                    if (s.PartyIndex < 0 || s.PartyIndex >= party.Count) return "그런 몬스터는 없다!";
                    return party[s.PartyIndex].IsFainted ? "기절한 몬스터는 싸울 수 없다!"
                        : s.PartyIndex == _active[Index(side)] ? "이미 싸우고 있다!" : null;
                case FleeAction _: return null;   // 기권
                case BallAction _: return "대결 중에는 쓸 수 없다!";
                default: return "알 수 없는 행동이다!";
            }
        }

        /// <summary>쓰러진 쪽이 내보낼 몬스터를 지금 고를 수 있는지. 가능하면 null.</summary>
        public string WhyNotReplacement(DuelSide side, int partyIndex)
        {
            if (Phase != PvpPhase.AwaitingReplacement || ReplacingSide != side) return "지금은 몬스터를 고를 차례가 아니다!";
            var party = _party[Index(side)];
            if (partyIndex < 0 || partyIndex >= party.Count) return "그런 몬스터는 없다!";
            return party[partyIndex].IsFainted ? "기절한 몬스터는 싸울 수 없다!" : null;
        }

        /// <summary>즉시 기권한다. 상대가 이긴다.</summary>
        public List<PvpEvent> Forfeit(DuelSide side)
        {
            var events = new List<PvpEvent>();
            if (IsOver) return events;
            DoForfeit(side, events);
            return events;
        }

        void DoForfeit(DuelSide side, List<PvpEvent> events)
        {
            events.Add(new PvpEvent { Kind = PvpEventKind.Forfeit, Side = side });
            Finish(Other(side), events);
        }

        void Finish(DuelSide winner, List<PvpEvent> events)
        {
            Phase = PvpPhase.Ended; Winner = winner; ReplacingSide = null;
            events.Add(new PvpEvent { Kind = PvpEventKind.Ended, Winner = winner });
        }

        /// <summary>
        /// 양쪽이 낸 행동으로 한 라운드를 판정한다. 순서: 기권 → 교체 → 상처약 → 기술(두 기술은 Duel 과 같은 규칙:
        /// GameData.HasPriorityMoves 면 우선도 > 스피드 > 50:50, 아니면 스피드 > 동률 무작위).
        /// 행동이 규칙에 어긋나면 상태를 바꾸기 전에 예외를 던진다.
        /// </summary>
        public List<PvpEvent> ResolveRound(BattleAction actionA, BattleAction actionB)
        {
            if (IsOver) throw new InvalidOperationException("이미 끝난 대결이다.");
            if (Phase != PvpPhase.AwaitingActions) throw new InvalidOperationException("교체를 고르는 중이라 라운드를 진행할 수 없다.");
            string why = WhyNot(DuelSide.A, actionA) ?? WhyNot(DuelSide.B, actionB);
            if (why != null) throw new InvalidOperationException(why);

            var events = new List<PvpEvent>();
            var actions = new[] { actionA, actionB };
            var sides = new[] { DuelSide.A, DuelSide.B };

            for (int i = 0; i < 2; i++)
                if (actions[i] is FleeAction) { DoForfeit(sides[i], events); return events; }

            for (int i = 0; i < 2; i++)
                if (actions[i] is SwitchAction s)
                {
                    int old = _active[i];
                    events.Add(new PvpEvent { Kind = PvpEventKind.SwitchOut, Side = sides[i], PartyIndex = old });
                    _active[i] = s.PartyIndex;
                    events.Add(SwitchIn(sides[i]));
                }

            for (int i = 0; i < 2; i++)
                if (actions[i] is PotionAction p)
                {
                    var target = _party[i][p.PartyIndex];
                    int before = target.Hp;
                    _potions[i]--;
                    target.Heal(PlayerState.PotionHeal);
                    events.Add(new PvpEvent
                    {
                        Kind = PvpEventKind.PotionUsed, Side = sides[i], PartyIndex = p.PartyIndex, Amount = target.Hp - before,
                        TargetActive = p.PartyIndex == _active[i],
                        SpeciesId = target.SpeciesId, Level = target.Level, Hp = target.Hp, MaxHp = target.MaxHp,
                    });
                }

            var ma = actionA as MoveAction; var mb = actionB as MoveAction;
            if (ma != null && mb != null)
            {
                var a = Active(DuelSide.A); var b = Active(DuelSide.B);
                bool aFirst = _data.HasPriorityMoves
                    ? TurnOrder.FirstMovesFirst(_data.GetMove(ma.MoveId).Priority, a.Speed, _data.GetMove(mb.MoveId).Priority, b.Speed, _rng)
                    : EnemyAI.PlayerMovesFirst(a.Speed, b.Speed, _rng);
                if (aFirst) { UseMove(DuelSide.A, ma.MoveId, events); if (!IsOver && Phase == PvpPhase.AwaitingActions) UseMove(DuelSide.B, mb.MoveId, events); }
                else { UseMove(DuelSide.B, mb.MoveId, events); if (!IsOver && Phase == PvpPhase.AwaitingActions) UseMove(DuelSide.A, ma.MoveId, events); }
            }
            else if (ma != null) UseMove(DuelSide.A, ma.MoveId, events);
            else if (mb != null) UseMove(DuelSide.B, mb.MoveId, events);

            return events;
        }

        /// <summary>쓰러진 쪽이 다음 몬스터를 내보낸다. 규칙에 어긋나면 예외.</summary>
        public List<PvpEvent> SubmitReplacement(DuelSide side, int partyIndex)
        {
            string why = WhyNotReplacement(side, partyIndex);
            if (why != null) throw new InvalidOperationException(why);
            _active[Index(side)] = partyIndex;
            Phase = PvpPhase.AwaitingActions; ReplacingSide = null;
            return new List<PvpEvent> { SwitchIn(side) };
        }

        PvpEvent SwitchIn(DuelSide side)
        {
            var m = Active(side);
            return new PvpEvent
            {
                Kind = PvpEventKind.SwitchIn, Side = side, PartyIndex = _active[Index(side)],
                SpeciesId = m.SpeciesId, Level = m.Level, Hp = m.Hp, MaxHp = m.MaxHp,
            };
        }

        void UseMove(DuelSide side, string moveId, List<PvpEvent> events)
        {
            var attacker = Active(side);
            if (attacker.IsFainted) return;   // 같은 라운드 상대 선공에 이미 쓰러졌다

            var defenderSide = Other(side);
            var defender = Active(defenderSide);
            var move = _data.GetMove(moveId);
            events.Add(new PvpEvent { Kind = PvpEventKind.MoveUsed, Side = side, MoveId = moveId });

            var result = DamageCalc.Roll(_data, attacker, defender, move, _rng);
            if (result.Missed)
            {
                events.Add(new PvpEvent { Kind = PvpEventKind.Missed, Side = side, MoveId = moveId });
                return;
            }
            defender.Hp = Math.Max(0, defender.Hp - result.Damage);
            events.Add(new PvpEvent
            {
                Kind = PvpEventKind.Damage, Side = defenderSide, MoveId = moveId,
                Amount = result.Damage, Multiplier = result.Multiplier, Critical = result.Critical,
            });
            if (!defender.IsFainted) return;

            events.Add(new PvpEvent { Kind = PvpEventKind.Fainted, Side = defenderSide });
            if (!HasUsable(defenderSide)) { Finish(side, events); return; }
            Phase = PvpPhase.AwaitingReplacement; ReplacingSide = defenderSide;
            events.Add(new PvpEvent { Kind = PvpEventKind.ReplacementNeeded, Side = defenderSide });
        }
    }
}
