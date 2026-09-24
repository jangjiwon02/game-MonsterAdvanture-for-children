using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>OnModifyDamage 에 넘기는 컨텍스트. 관찰자들이 차례로 Multiplier 를 곱해서 연쇄 적용한다.</summary>
    public sealed class DamageContext
    {
        public BattleSession Session;
        public Side AttackerSide;
        public Monster Attacker, Defender;
        public MoveData Move;
        /// <summary>타입 상성·급소·편차까지 끝난 뒤의 데미지(관찰자 배율 적용 전).</summary>
        public int BaseDamage;
        /// <summary>관찰자 누적 배율. 0 이면 무효(면역), 1 이면 변화 없음.</summary>
        public double Multiplier = 1.0;
        /// <summary>Damage 이벤트 직후에 내보낼 안내 이벤트(특성·도구 발동 문구 등).</summary>
        public readonly List<BattleEvent> Notices = new List<BattleEvent>();

        public Side DefenderSide => AttackerSide == Side.Player ? Side.Enemy : Side.Player;
        public void Multiply(double factor) { Multiplier *= factor; }
    }

    /// <summary>
    /// 배틀 이벤트 훅(Observer 패턴). 특성·도구·날씨가 이걸 상속해서 필요한 콜백만 재정의한다.
    /// 상태를 바꾸는 훅은 <c>emit</c> 리스트에 BattleEvent 를 넣어 UI 에 알린다(세션이 그 순서대로 내보낸다).
    /// <see cref="BattleSession.Observers"/> 가 비어 있으면 아무 훅도 불리지 않는다.
    /// </summary>
    public abstract class BattleObserver
    {
        /// <summary>null 이면 전장 전체(날씨 등). 값이 있으면 그 쪽 몬스터에 붙은 특성/도구.</summary>
        public Side? OwnerSide { get; internal set; }
        /// <summary>붙은 몬스터. 지정돼 있으면 그 몬스터가 출전 중일 때만 훅이 불린다(교체하면 효과가 꺼진다).</summary>
        public Monster Holder { get; internal set; }

        public virtual void OnBattleStart(BattleSession session, List<BattleEvent> emit) { }
        public virtual void OnTurnStart(BattleSession session, List<BattleEvent> emit) { }
        public virtual void OnMoveUsed(BattleSession session, Side side, MoveData move, List<BattleEvent> emit) { }
        /// <summary>데미지 배율을 바꾼다(ctx.Multiply). 이벤트는 ctx.Notices 로 낸다. 명중한 공격에만 불린다.</summary>
        public virtual void OnModifyDamage(DamageContext ctx) { }
        /// <summary>attackerSide 가 쓴 기술이 실제로 amount 만큼 깎은 직후(면역이면 0).</summary>
        public virtual void OnDamageDealt(BattleSession session, Side attackerSide, MoveData move, int amount, List<BattleEvent> emit) { }
        public virtual void OnFaint(BattleSession session, Side side, List<BattleEvent> emit) { }
        /// <summary>턴이 끝나 모두 살아 있을 때(전투가 끝났으면 안 불린다). 날씨 지속 감소·피해, 도구 회복 등.</summary>
        public virtual void OnTurnEnd(BattleSession session, List<BattleEvent> emit) { }

        protected static string NameOf(BattleSession session, Side side) =>
            session.MonsterOf(side).Species(session.Data).Name;
    }

    /* ---------------------------- 특성 예시 ---------------------------- */

    /// <summary>체력이 hpNumer/hpDenom 이하일 때 특정 타입 기술을 multiplier 배(기본 1/3 이하·1.5배). 정수 비교로 판정한다.</summary>
    public sealed class LowHpTypeBoostAbility : BattleObserver
    {
        public readonly string AbilityId, TypeId;
        readonly double _multiplier; readonly int _numer, _denom;

        public LowHpTypeBoostAbility(string abilityId, string typeId, double multiplier = 1.5, int hpNumer = 1, int hpDenom = 3)
        {
            AbilityId = abilityId; TypeId = typeId; _multiplier = multiplier; _numer = hpNumer; _denom = hpDenom;
        }

        public override void OnModifyDamage(DamageContext ctx)
        {
            if (ctx.AttackerSide != OwnerSide || ctx.Move.Type != TypeId) return;
            if (ctx.Attacker.Hp * _denom > ctx.Attacker.MaxHp * _numer) return;
            ctx.Multiply(_multiplier);
            ctx.Notices.Add(new BattleEvent
            {
                Kind = BattleEventKind.ObserverNotice, Side = ctx.AttackerSide, Name = AbilityId,
                Text = $"{NameOf(ctx.Session, ctx.AttackerSide)}의 특성이 발동했다!",
            });
        }
    }

    /// <summary>특정 타입 기술을 무효로 하고 최대 HP 의 1/healDenom 을 회복한다(물 흡수 같은 특성).</summary>
    public sealed class TypeAbsorbAbility : BattleObserver
    {
        public readonly string AbilityId, TypeId;
        readonly int _healDenom;
        bool _absorbing;

        public TypeAbsorbAbility(string abilityId, string typeId, int healDenom = 4)
        {
            AbilityId = abilityId; TypeId = typeId; _healDenom = healDenom;
        }

        public override void OnModifyDamage(DamageContext ctx)
        {
            if (ctx.DefenderSide != OwnerSide || ctx.Move.Type != TypeId) return;
            ctx.Multiplier = 0.0;
            _absorbing = true;
        }

        public override void OnDamageDealt(BattleSession session, Side attackerSide, MoveData move, int amount, List<BattleEvent> emit)
        {
            if (!_absorbing) return;
            _absorbing = false;
            var owner = session.MonsterOf(OwnerSide.Value);
            int before = owner.Hp;
            owner.Heal(Math.Max(1, owner.MaxHp / _healDenom));
            emit.Add(new BattleEvent
            {
                Kind = BattleEventKind.ResidualHeal, Side = OwnerSide.Value, Amount = owner.Hp - before, Name = AbilityId,
                Text = $"{NameOf(session, OwnerSide.Value)}의 특성으로 HP를 회복했다!",
            });
        }
    }

    /* ---------------------------- 도구 예시 ---------------------------- */

    /// <summary>턴 끝에 최대 HP 의 1/denom(최소 1)을 회복한다(먹다 남은 음식 같은 도구).</summary>
    public sealed class LeftoversItem : BattleObserver
    {
        public readonly string ItemId;
        readonly int _denom;

        public LeftoversItem(string itemId = "leftovers", int denom = 16) { ItemId = itemId; _denom = denom; }

        public override void OnTurnEnd(BattleSession session, List<BattleEvent> emit)
        {
            var m = session.MonsterOf(OwnerSide.Value);
            if (m.IsFainted || m.Hp >= m.MaxHp) return;
            int before = m.Hp;
            m.Heal(Math.Max(1, m.MaxHp / _denom));
            emit.Add(new BattleEvent
            {
                Kind = BattleEventKind.ResidualHeal, Side = OwnerSide.Value, Amount = m.Hp - before, Name = ItemId,
                Text = $"{NameOf(session, OwnerSide.Value)}의 HP가 조금 회복되었다!",
            });
        }
    }

    /// <summary>특정 타입 기술의 데미지를 multiplier 배(기본 1.2배)로 올리는 도구.</summary>
    public sealed class TypeBoostItem : BattleObserver
    {
        public readonly string ItemId, TypeId;
        readonly double _multiplier;

        public TypeBoostItem(string itemId, string typeId, double multiplier = 1.2)
        {
            ItemId = itemId; TypeId = typeId; _multiplier = multiplier;
        }

        public override void OnModifyDamage(DamageContext ctx)
        {
            if (ctx.AttackerSide == OwnerSide && ctx.Move.Type == TypeId) ctx.Multiply(_multiplier);
        }
    }
}
