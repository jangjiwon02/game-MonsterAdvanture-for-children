using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MonsterAdventure.Core
{
    /// <summary>SPEC "능력치" 공식. 정수 나눗셈은 웹의 floor와 같다(모두 양수).
    /// 아래 4-인자/5-인자 오버로드는 웹에는 없는 Unity 전용 추가(개체값·노력치·성격) — iv=0, ev=0, natureMult=1
    /// 이면 정수 나눗셈의 성질상 원래 2-인자 공식과 정확히 같은 값이 나오므로, 기존 골든 테스트는 그대로 유효하다.</summary>
    public static class StatCalc
    {
        public static int MaxHp(int baseHp, int level) => baseHp * 2 * level / 100 + level + 10;
        public static int Stat(int baseStat, int level) => baseStat * 2 * level / 100 + 5;

        public static int MaxHp(int baseHp, int level, int iv, int ev) =>
            (baseHp * 2 + iv + ev / 4) * level / 100 + level + 10;

        public static int Stat(int baseStat, int level, int iv, int ev, double natureMult = 1.0) =>
            (int)(((baseStat * 2 + iv + ev / 4) * level / 100 + 5) * natureMult);
    }

    /// <summary>능력치 4종 묶음(개체값·노력치에 같이 쓴다). HP/공격/방어/스피드 — 이 게임엔 특수공격/특수방어 구분이 없다.</summary>
    public sealed class StatSpread
    {
        public int Hp, Atk, Def, Spd;
    }

    public enum StatKind { Atk, Def, Spd }

    /// <summary>성격(웹에는 없는 Unity 전용 추가) — HP를 뺀 세 능력치 중 하나를 10% 올리고 하나를 10% 내린다
    /// (진짜 포켓몬 성격처럼 HP는 성격의 영향을 안 받는다). Balanced 는 영향 없음.</summary>
    public enum Nature { Balanced, Aggressive, Reckless, Sturdy, Bulky, Swift, Nimble }

    public static class NatureInfo
    {
        const double Up = 1.1, Down = 0.9;

        public static double Multiplier(Nature nature, StatKind stat) => (nature, stat) switch
        {
            (Nature.Aggressive, StatKind.Atk) => Up, (Nature.Aggressive, StatKind.Def) => Down,
            (Nature.Reckless, StatKind.Atk) => Up, (Nature.Reckless, StatKind.Spd) => Down,
            (Nature.Sturdy, StatKind.Def) => Up, (Nature.Sturdy, StatKind.Atk) => Down,
            (Nature.Bulky, StatKind.Def) => Up, (Nature.Bulky, StatKind.Spd) => Down,
            (Nature.Swift, StatKind.Spd) => Up, (Nature.Swift, StatKind.Atk) => Down,
            (Nature.Nimble, StatKind.Spd) => Up, (Nature.Nimble, StatKind.Def) => Down,
            _ => 1.0,
        };

        public static string DisplayName(Nature n) => n switch
        {
            Nature.Aggressive => "저돌적인", Nature.Reckless => "무모한", Nature.Sturdy => "든든한",
            Nature.Bulky => "대범한", Nature.Swift => "재빠른", Nature.Nimble => "민첩한",
            _ => "차분한",
        };
    }

    /// <summary>파티·박스에 들어가는 개체. 종족 데이터는 id로만 참조한다.</summary>
    public sealed class Monster
    {
        public const int MaxMoves = 4;
        public const int MaxIv = 31;

        public int SpeciesId;
        public int Level;
        public int Exp;
        public int Hp;
        public int MaxHp;
        public int Attack;
        public int Defense;
        public int Speed;
        public List<string> Moves = new List<string>();

        /// <summary>개체값·노력치·성격. 전부 기본값(0/0/Balanced)이면 원래 공식과 완전히 같다 — 새 계정을
        /// 처음 만들 때만(<see cref="RollIndividualValues"/>) 실제로 굴려서 개체마다 편차가 생긴다.</summary>
        public StatSpread IVs = new StatSpread();
        public StatSpread EVs = new StatSpread();
        public Nature Nature = Nature.Balanced;

        [JsonIgnore] public bool IsFainted => Hp <= 0;

        public SpeciesData Species(GameData data) => data.GetSpecies(SpeciesId);

        public static Monster Create(GameData data, int speciesId, int level)
        {
            var m = new Monster { SpeciesId = speciesId, Level = level };
            m.RecalcStats(data);
            m.Hp = m.MaxHp;
            foreach (var entry in data.GetSpecies(speciesId).Learnset)
                if (entry.Level <= level && !m.Moves.Contains(entry.Move)) m.Moves.Add(entry.Move);
            while (m.Moves.Count > MaxMoves) m.Moves.RemoveAt(0);
            return m;
        }

        /// <summary>기존 개체(source)와 같은 개체값·노력치·성격·기술로, HP만 가득 채운 새 사본을 만든다
        /// (PvP 친선 대결용 — 진짜 그 개체가 싸우는 것처럼 보이되 원본 HP는 안 다친다).</summary>
        public static Monster CreateFullHpCopy(GameData data, Monster source)
        {
            var m = new Monster
            {
                SpeciesId = source.SpeciesId,
                Level = source.Level,
                Exp = source.Exp,
                Moves = new List<string>(source.Moves),
                IVs = new StatSpread { Hp = source.IVs.Hp, Atk = source.IVs.Atk, Def = source.IVs.Def, Spd = source.IVs.Spd },
                EVs = new StatSpread { Hp = source.EVs.Hp, Atk = source.EVs.Atk, Def = source.EVs.Def, Spd = source.EVs.Spd },
                Nature = source.Nature,
            };
            m.RecalcStats(data);
            m.Hp = m.MaxHp;
            return m;
        }

        /// <summary>새로 만들어진 개체(새 파트너·야생 조우·새 계정)에 한 번만 부른다 — 0~31 개체값 4개와
        /// 성격을 무작위로 굴리고, 그 자리에서 바로 능력치도 다시 계산한다(안 하면 롤 직후에도 예전 스탯이
        /// 남아 있는 진짜 버그가 됨 — 새로 추가한 테스트가 이걸 잡아냈다). 시드를 안 넘기면 진짜 무작위(SystemRng)를 쓴다.</summary>
        public void RollIndividualValues(GameData data) => RollIndividualValues(data, new SystemRng());

        public void RollIndividualValues(GameData data, IRng rng)
        {
            IVs.Hp = rng.Range(0, MaxIv);
            IVs.Atk = rng.Range(0, MaxIv);
            IVs.Def = rng.Range(0, MaxIv);
            IVs.Spd = rng.Range(0, MaxIv);
            Nature = rng.Pick((Nature[])Enum.GetValues(typeof(Nature)));
            RecalcStats(data);
            Hp = MaxHp;   // 새로 태어난 개체이므로 항상 풀피로 시작한다.
        }

        /// <summary>레벨·종족이 바뀐 뒤 능력치를 다시 계산한다(현재 HP는 건드리지 않는다).
        /// IVs/EVs 가 전부 0이고 Nature 가 Balanced 면 원래 2-인자 공식과 정확히 같다.</summary>
        public void RecalcStats(GameData data)
        {
            var b = data.GetSpecies(SpeciesId).BaseStats;
            MaxHp = StatCalc.MaxHp(b.Hp, Level, IVs.Hp, EVs.Hp);
            Attack = StatCalc.Stat(b.Atk, Level, IVs.Atk, EVs.Atk, NatureInfo.Multiplier(Nature, StatKind.Atk));
            Defense = StatCalc.Stat(b.Def, Level, IVs.Def, EVs.Def, NatureInfo.Multiplier(Nature, StatKind.Def));
            Speed = StatCalc.Stat(b.Spd, Level, IVs.Spd, EVs.Spd, NatureInfo.Multiplier(Nature, StatKind.Spd));
        }

        public void Heal(int amount) => Hp = Math.Min(MaxHp, Hp + amount);
        public void FullHeal() => Hp = MaxHp;
    }
}
