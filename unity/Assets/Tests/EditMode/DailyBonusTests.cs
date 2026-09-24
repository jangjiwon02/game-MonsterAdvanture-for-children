using System;
using MonsterAdventure.Core;
using NUnit.Framework;

namespace MonsterAdventure.Tests
{
    /// <summary>하루 한 번 지급되는 출석 보너스(DailyBonus) 규칙 검증. GameData 로딩이 필요 없다.</summary>
    public class DailyBonusTests
    {
        [Test]
        public void FirstLogin_GrantsBonus_AndUpdatesLastLogin()
        {
            var s = new PlayerState();                              // LastLoginUtc 기본값 = DateTime.MinValue
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            int before = s.Money;

            bool granted = DailyBonus.TryGrant(s, now);

            Assert.IsTrue(granted);
            Assert.AreEqual(before + DailyBonus.Amount, s.Money);
            Assert.AreEqual(now, s.LastLoginUtc);
        }

        [Test]
        public void SecondLoginSameDay_DoesNotGrantAgain()
        {
            var s = new PlayerState();
            var morning = new DateTime(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
            var evening = new DateTime(2026, 9, 23, 21, 0, 0, DateTimeKind.Utc);
            DailyBonus.TryGrant(s, morning);
            int afterFirst = s.Money;

            bool granted = DailyBonus.TryGrant(s, evening);

            Assert.IsFalse(granted);
            Assert.AreEqual(afterFirst, s.Money);
        }

        [Test]
        public void NextDayLogin_GrantsBonusAgain()
        {
            var s = new PlayerState();
            var day1 = new DateTime(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
            var day2 = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
            DailyBonus.TryGrant(s, day1);
            int afterFirst = s.Money;

            bool granted = DailyBonus.TryGrant(s, day2);

            Assert.IsTrue(granted);
            Assert.AreEqual(afterFirst + DailyBonus.Amount, s.Money);
            Assert.AreEqual(day2, s.LastLoginUtc);
        }

        [Test]
        public void Amount_IsExactly150()
        {
            Assert.AreEqual(150, DailyBonus.Amount);
        }

        [Test]
        public void JustAfterMidnight_CountsAsNewDay_EvenIfLessThanOneHourPassed()
        {
            var s = new PlayerState();
            var beforeMidnight = new DateTime(2026, 9, 22, 23, 59, 0, DateTimeKind.Utc);
            var afterMidnight = new DateTime(2026, 9, 23, 0, 1, 0, DateTimeKind.Utc);
            DailyBonus.TryGrant(s, beforeMidnight);
            int afterFirst = s.Money;

            bool granted = DailyBonus.TryGrant(s, afterMidnight);

            Assert.IsTrue(granted);
            Assert.AreEqual(afterFirst + DailyBonus.Amount, s.Money);
        }
    }
}
