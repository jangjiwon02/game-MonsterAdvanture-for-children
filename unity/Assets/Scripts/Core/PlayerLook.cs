using System.Collections.Generic;
using System.Linq;

namespace MonsterAdventure.Core
{
    /// <summary>LAN 대결 마당에서 접속자를 구분하는 겉모습 색. 번호가 PlayerArt 팔레트의 칸이다(0 = 기본 빨강).</summary>
    public static class PlayerLook
    {
        public const int ColorCount = 8;
        public const int DefaultColor = 0;

        /// <summary>새로 들어온 사람의 색. 방이 비어 있었다면(처음 들어온 사람) 기본 빨강, 아니면 지금 쓰이지 않는 색 중
        /// 무작위 — 그래서 같은 방 사람끼리는 색이 겹치지 않는다. 팔레트보다 사람이 많아지면 가장 덜 쓰인 색 중에서 고른다.</summary>
        public static int PickColor(IReadOnlyCollection<int> inUse, IRng rng)
        {
            if (inUse.Count == 0) return DefaultColor;
            var free = Enumerable.Range(0, ColorCount).Where(c => !inUse.Contains(c)).ToList();
            if (free.Count > 0) return rng.Pick(free);

            var counts = Enumerable.Range(0, ColorCount).ToDictionary(c => c, c => inUse.Count(x => x == c));
            int min = counts.Values.Min();
            return rng.Pick(counts.Where(kv => kv.Value == min).Select(kv => kv.Key).ToList());
        }
    }
}
