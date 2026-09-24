using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>옵션 메뉴의 명단 화면에 보이는 교사·학생 이름(정적 데이터).</summary>
    public static class Roster
    {
        public const string Teacher = "장지원";

        public static readonly IReadOnlyList<string> Students = new[]
        {
            "문창식", "신서희", "김하나린", "정현진", "정민성", "김레담",
            "김영진", "오시우", "오수호", "정하성", "신희주",
        };
    }
}
