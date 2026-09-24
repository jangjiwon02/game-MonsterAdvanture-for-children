using System;

namespace MonsterAdventure.Core
{
    /// <summary>파티 ↔ 박스 이동 규칙(SPEC "포획": 파티 최대 6, 초과분은 박스로). 웹의 boxMenu.</summary>
    public static class BoxOps
    {
        /// <summary>이 파티원을 맡길 수 없는 이유. 맡길 수 있으면 null.</summary>
        public static string WhyNotDeposit(PlayerState s, int partyIndex)
        {
            if (s.Party.Count <= 1) return "마지막 한 마리는 맡길 수 없다!";
            for (int i = 0; i < s.Party.Count; i++)
                if (i != partyIndex && !s.Party[i].IsFainted) return null;
            return "싸울 수 있는 몬스터가 없어진다!";
        }

        public static void Deposit(PlayerState s, int partyIndex)
        {
            string why = WhyNotDeposit(s, partyIndex);
            if (why != null) throw new InvalidOperationException(why);
            var m = s.Party[partyIndex];
            s.Party.RemoveAt(partyIndex);
            s.Box.Add(m);
        }

        /// <summary>파티에 자리가 있으면 박스에서 꺼내 파티 맨 뒤에 넣는다. 가득 차 있으면 false.</summary>
        public static bool Withdraw(PlayerState s, int boxIndex)
        {
            if (s.Party.Count >= Capture.MaxPartySize) return false;
            var m = s.Box[boxIndex];
            s.Box.RemoveAt(boxIndex);
            s.Party.Add(m);
            return true;
        }

        /// <summary>파티가 가득 찼을 때: 박스의 몬스터와 파티원을 맞바꾼다.</summary>
        public static void Swap(PlayerState s, int boxIndex, int partyIndex)
        {
            var fromBox = s.Box[boxIndex];
            s.Box[boxIndex] = s.Party[partyIndex];
            s.Party[partyIndex] = fromBox;
        }
    }
}
