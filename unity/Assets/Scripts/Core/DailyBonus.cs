using System;

namespace MonsterAdventure.Core
{
    /// <summary>접속 시 하루 한 번 주는 출석 보너스. 서버가 트레이너 계정을 불러온 직후 호출한다.</summary>
    public static class DailyBonus
    {
        /// <summary>몬스터볼(₩100) 하나 반 정도 되는 적당한 액수로 잡았다.</summary>
        public const int Amount = 150;

        /// <summary>오늘(UTC 날짜 기준) 아직 못 받았으면 지급하고 true, 이미 받았으면 아무것도 안 하고 false.</summary>
        public static bool TryGrant(PlayerState state, DateTime nowUtc)
        {
            if (state.LastLoginUtc.Date >= nowUtc.Date) return false;
            state.Money += Amount;
            state.LastLoginUtc = nowUtc;
            return true;
        }
    }
}
