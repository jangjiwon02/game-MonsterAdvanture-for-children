using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>
    /// 날씨 종류. 이 데이터의 타입은 normal/fire/water/grass/electric/rock 뿐이라 그에 맞춘다:
    /// 맑음=불 ×1.5·물 ×0.5, 비=물 ×1.5·불 ×0.5, 모래바람=바위 타입 외 매 턴 끝 최대 HP 1/16 피해.
    /// </summary>
    public enum WeatherKind { None, Sunny, Rain, Sandstorm }

    public static class Weather
    {
        public const double Boost = 1.5, Weaken = 0.5;

        public static string Id(WeatherKind kind) => kind switch
        {
            WeatherKind.Sunny => "sunny", WeatherKind.Rain => "rain", WeatherKind.Sandstorm => "sandstorm", _ => "none",
        };

        /// <summary>날씨가 이 타입 기술에 주는 배율.</summary>
        public static double MoveMultiplier(WeatherKind kind, string moveType)
        {
            switch (kind)
            {
                case WeatherKind.Sunny: return moveType == "fire" ? Boost : moveType == "water" ? Weaken : 1.0;
                case WeatherKind.Rain: return moveType == "water" ? Boost : moveType == "fire" ? Weaken : 1.0;
                default: return 1.0;
            }
        }

        /// <summary>턴 끝 피해량(0 이면 피해 없음).</summary>
        public static int ChipDamage(WeatherKind kind, string monsterType, int maxHp) =>
            kind == WeatherKind.Sandstorm && monsterType != "rock" ? Math.Max(1, maxHp / 16) : 0;
    }

    /// <summary>날씨를 관찰자로 구현한 것. 전장 전체에 걸리므로 BattleSession.Observers 에 그냥 넣는다.</summary>
    public sealed class WeatherObserver : BattleObserver
    {
        public WeatherKind Kind { get; }
        public int TurnsLeft { get; private set; }

        public WeatherObserver(WeatherKind kind, int turns)
        {
            if (turns < 1) throw new ArgumentOutOfRangeException(nameof(turns));
            Kind = kind; TurnsLeft = turns;
        }

        public override void OnBattleStart(BattleSession session, List<BattleEvent> emit)
        {
            if (Kind == WeatherKind.None) return;
            emit.Add(new BattleEvent
            {
                Kind = BattleEventKind.WeatherStarted, Name = Weather.Id(Kind), Amount = TurnsLeft, Text = StartText(Kind),
            });
        }

        public override void OnModifyDamage(DamageContext ctx)
        {
            double m = Weather.MoveMultiplier(Kind, ctx.Move.Type);
            if (m != 1.0) ctx.Multiply(m);
        }

        public override void OnTurnEnd(BattleSession session, List<BattleEvent> emit)
        {
            if (Kind == WeatherKind.None) { session.Observers.Remove(this); return; }
            foreach (var side in new[] { Side.Player, Side.Enemy })
            {
                var m = session.MonsterOf(side);
                if (m.IsFainted) continue;
                int dmg = Weather.ChipDamage(Kind, m.Species(session.Data).Type, m.MaxHp);
                if (dmg <= 0) continue;
                int before = m.Hp;
                m.Hp = Math.Max(0, m.Hp - dmg);
                emit.Add(new BattleEvent
                {
                    Kind = BattleEventKind.ResidualDamage, Side = side, Amount = before - m.Hp, Name = Weather.Id(Kind),
                    Text = $"모래바람이 {Korean.J(NameOf(session, side), "을", "를")} 덮쳤다!",
                });
            }
            if (--TurnsLeft > 0) return;
            emit.Add(new BattleEvent { Kind = BattleEventKind.WeatherEnded, Name = Weather.Id(Kind), Text = EndText(Kind) });
            session.Observers.Remove(this);
        }

        static string StartText(WeatherKind k) => k switch
        {
            WeatherKind.Sunny => "햇살이 강해졌다!", WeatherKind.Rain => "비가 내리기 시작했다!", _ => "모래바람이 불기 시작했다!",
        };

        static string EndText(WeatherKind k) => k switch
        {
            WeatherKind.Sunny => "햇살이 원래대로 돌아왔다.", WeatherKind.Rain => "비가 그쳤다.", _ => "모래바람이 잦아들었다.",
        };
    }
}
