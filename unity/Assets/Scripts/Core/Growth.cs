using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    public enum GrowthKind { LevelUp, LearnedMove, ReplacedMove, Evolved }

    public sealed class GrowthEvent
    {
        public GrowthKind Kind;
        public int Level;                // LevelUp: 새 레벨
        public string MoveId;            // LearnedMove/ReplacedMove: 새로 배운 기술
        public string ForgottenMoveId;   // ReplacedMove: 잊은 기술
        public int FromSpeciesId;        // Evolved
        public int ToSpeciesId;          // Evolved
    }

    /// <summary>SPEC "경험치 · 돈 · 성장".</summary>
    public static class Growth
    {
        public const int MaxLevel = 60;

        /// <summary>다음 레벨 필요 경험치 = floor(L^2 * 1.2) + 8</summary>
        public static int ExpToNext(int level) => (int)Math.Floor(level * level * 1.2) + 8;

        /// <summary>승리 시 경험치 = floor(baseExp * 적레벨 / 7)</summary>
        public static int ExpReward(SpeciesData enemy, int enemyLevel) => enemy.BaseExp * enemyLevel / 7;

        /// <summary>돈 = rand(15..30) + 적레벨 * 6</summary>
        public static int MoneyReward(int enemyLevel, IRng rng) => rng.Range(15, 30) + enemyLevel * 6;

        public const int EVStatCap = 252, EVTotalCap = 510;

        /// <summary>웹에는 없는 Unity 전용 추가(노력치). 종족별 정확한 노력치 배분표가 데이터에 없어서,
        /// 쓰러뜨린 종족의 종족값 중 가장 높은 능력치 하나에 1점을 준다(단순화한 버전). 총합/스탯당 캡을 넘으면 멈춘다.
        /// 이긴 쪽에게만 적용한다(진 쪽은 경험치는 일부 받아도 노력치는 없음).</summary>
        public static void GainEVs(GameData data, Monster m, SpeciesData defeated)
        {
            int total = m.EVs.Hp + m.EVs.Atk + m.EVs.Def + m.EVs.Spd;
            if (total >= EVTotalCap) return;
            var b = defeated.BaseStats;
            if (b.Hp >= b.Atk && b.Hp >= b.Def && b.Hp >= b.Spd) { if (m.EVs.Hp < EVStatCap) m.EVs.Hp++; }
            else if (b.Atk >= b.Def && b.Atk >= b.Spd) { if (m.EVs.Atk < EVStatCap) m.EVs.Atk++; }
            else if (b.Def >= b.Spd) { if (m.EVs.Def < EVStatCap) m.EVs.Def++; }
            else { if (m.EVs.Spd < EVStatCap) m.EVs.Spd++; }
            m.RecalcStats(data);
        }

        /// <summary>경험치를 더하고 레벨업 → 기술 습득 → 진화 순으로 일어난 일을 돌려준다.</summary>
        public static List<GrowthEvent> GainExp(GameData data, Monster m, int amount)
        {
            var events = new List<GrowthEvent>();
            m.Exp += amount;
            bool leveled = false;
            while (m.Exp >= ExpToNext(m.Level) && m.Level < MaxLevel)
            {
                m.Exp -= ExpToNext(m.Level);
                int oldMaxHp = m.MaxHp;
                m.Level++;
                m.RecalcStats(data);
                m.Hp += m.MaxHp - oldMaxHp;
                leveled = true;
                events.Add(new GrowthEvent { Kind = GrowthKind.LevelUp, Level = m.Level });
                LearnMoves(data, m, events);
            }
            if (leveled) CheckEvolve(data, m, events);
            return events;
        }

        /// <summary>현재 레벨에 배우는 기술. 4개가 차면 가장 약한 기술을 새 기술이 더 강할 때만 교체한다.</summary>
        static void LearnMoves(GameData data, Monster m, List<GrowthEvent> events)
        {
            foreach (var entry in data.GetSpecies(m.SpeciesId).Learnset)
            {
                if (entry.Level != m.Level || m.Moves.Contains(entry.Move)) continue;
                var mv = data.GetMove(entry.Move);
                if (m.Moves.Count < Monster.MaxMoves)
                {
                    m.Moves.Add(entry.Move);
                    events.Add(new GrowthEvent { Kind = GrowthKind.LearnedMove, MoveId = entry.Move });
                    continue;
                }
                int weakest = 0;
                for (int i = 1; i < m.Moves.Count; i++)
                    if (data.GetMove(m.Moves[i]).Power < data.GetMove(m.Moves[weakest]).Power) weakest = i;
                var old = data.GetMove(m.Moves[weakest]);
                if (old.Power >= mv.Power) continue;
                m.Moves[weakest] = entry.Move;
                events.Add(new GrowthEvent { Kind = GrowthKind.ReplacedMove, MoveId = entry.Move, ForgottenMoveId = old.Id });
            }
        }

        static void CheckEvolve(GameData data, Monster m, List<GrowthEvent> events)
        {
            var sp = data.GetSpecies(m.SpeciesId);
            if (sp.Evolve == null || m.Level < sp.Evolve.Level) return;
            int oldMaxHp = m.MaxHp, from = m.SpeciesId;
            m.SpeciesId = sp.Evolve.To;
            m.RecalcStats(data);
            m.Hp += m.MaxHp - oldMaxHp;
            events.Add(new GrowthEvent { Kind = GrowthKind.Evolved, FromSpeciesId = from, ToSpeciesId = m.SpeciesId });
            LearnMoves(data, m, events);
        }
    }
}
