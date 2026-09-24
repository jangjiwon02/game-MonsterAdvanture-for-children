using System;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>수학 퀴즈(문제·보기·게이트)와 명단 규칙 검증.</summary>
    public class QuizAndRosterTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        static DateTime T(int day, int h = 12, int min = 0, int sec = 0) =>
            new DateTime(2026, 9, day, h, min, sec, DateTimeKind.Utc);

        /* ------------------------------- 문제 생성 ------------------------------- */

        [Test]
        public void Generate_1000Questions_SatisfyRules()
        {
            var rng = new SystemRng(1234);
            var seen = new bool[4];
            for (int i = 0; i < 1000; i++)
            {
                var q = MathQuiz.Generate(rng);
                seen[(int)q.Op] = true;
                StringAssert.Contains(QuizQuestion.Symbol(q.Op), q.Text);
                Assert.IsTrue(q.Text.EndsWith("= ?"));
                switch (q.Op)
                {
                    case QuizOp.Add:
                        Assert.That(q.A, Is.InRange(10, 99)); Assert.That(q.B, Is.InRange(10, 99));
                        Assert.AreEqual(q.A + q.B, q.Answer);
                        break;
                    case QuizOp.Sub:
                        Assert.That(q.A, Is.InRange(10, 99)); Assert.That(q.B, Is.InRange(10, 99));
                        Assert.AreEqual(q.A - q.B, q.Answer);
                        Assert.GreaterOrEqual(q.Answer, 0);
                        break;
                    case QuizOp.Mul:
                        Assert.That(q.A, Is.InRange(10, 99)); Assert.That(q.B, Is.InRange(2, 9));
                        Assert.AreEqual(q.A * q.B, q.Answer);
                        break;
                    default:
                        Assert.That(q.A, Is.InRange(10, 99)); Assert.That(q.B, Is.InRange(2, 9));
                        Assert.AreEqual(0, q.A % q.B, "나누어떨어져야 한다");
                        Assert.AreEqual(q.A / q.B, q.Answer);
                        Assert.GreaterOrEqual(q.Answer, 2);
                        break;
                }
            }
            CollectionAssert.AreEqual(new[] { true, true, true, true }, seen, "네 연산이 모두 나온다");
        }

        [Test]
        public void Text_ShowsSymbols()
        {
            Assert.AreEqual("47 + 68 = ?", new QuizQuestion(47, 68, QuizOp.Add).Text);
            Assert.AreEqual("47 - 12 = ?", new QuizQuestion(47, 12, QuizOp.Sub).Text);
            Assert.AreEqual("47 × 8 = ?", new QuizQuestion(47, 8, QuizOp.Mul).Text);
            Assert.AreEqual("96 ÷ 8 = ?", new QuizQuestion(96, 8, QuizOp.Div).Text);
        }

        [Test]
        public void MakeChoices_AreFourDistinctNonNegative_WithAnswer()
        {
            var rng = new SystemRng(99);
            for (int i = 0; i < 1000; i++)
            {
                var q = MathQuiz.Generate(rng);
                var c = MathQuiz.MakeChoices(q, rng);
                Assert.AreEqual(4, c.Length);
                Assert.AreEqual(4, c.Distinct().Count(), q.Text);
                Assert.IsTrue(c.Contains(q.Answer), q.Text);
                Assert.IsTrue(c.All(v => v >= 0), q.Text);
            }
        }

        [Test]
        public void MakeChoices_SmallAnswer_StillFourDistinct()
        {
            var q = new QuizQuestion(10, 9, QuizOp.Sub);   // 답 1
            var c = MathQuiz.MakeChoices(q, new SystemRng(5));
            Assert.AreEqual(4, c.Distinct().Count());
            Assert.IsTrue(c.All(v => v >= 0));
            Assert.IsTrue(c.Contains(1));
        }

        [Test]
        public void MakeChoices_SameSeed_IsDeterministic()
        {
            var q = new QuizQuestion(47, 68, QuizOp.Add);
            CollectionAssert.AreEqual(MathQuiz.MakeChoices(q, new SystemRng(7)), MathQuiz.MakeChoices(q, new SystemRng(7)));
        }

        [Test]
        public void MakeChoices_ShuffleMovesAnswerAround()
        {
            var q = new QuizQuestion(47, 68, QuizOp.Add);
            var positions = Enumerable.Range(0, 200)
                .Select(i => Array.IndexOf(MathQuiz.MakeChoices(q, new SystemRng(i)), q.Answer)).Distinct().Count();
            Assert.AreEqual(4, positions);
        }

        /* --------------------------------- 게이트 --------------------------------- */

        [Test]
        public void Gate_FreshState_IsReady_WithFullAttempts()
        {
            var st = QuizGate.Check(new PlayerState(), T(1));
            Assert.AreEqual(QuizGateResult.Ready, st.Result);
            Assert.AreEqual(QuizRules.DailyAttempts, st.AttemptsLeft);
        }

        [Test]
        public void Record_Correct_PaysReward_NoWait_CountsAttempt()
        {
            var s = new PlayerState(); int before = s.Money;
            int paid = QuizGate.Record(s, true, T(1), 60);
            Assert.AreEqual(60, paid);
            Assert.AreEqual(before + 60, s.Money);
            Assert.AreEqual(1, s.QuizAttempts);
            var st = QuizGate.Check(s, T(1));
            Assert.AreEqual(QuizGateResult.Ready, st.Result);
            Assert.AreEqual(QuizRules.DailyAttempts - 1, st.AttemptsLeft);
        }

        [Test]
        public void Record_Wrong_NoMoney_ThenWaitsThenReady()
        {
            var s = new PlayerState(); int before = s.Money;
            Assert.AreEqual(0, QuizGate.Record(s, false, T(1, 12, 0, 0)));
            Assert.AreEqual(before, s.Money);

            var wait = QuizGate.Check(s, T(1, 12, 1, 0));
            Assert.AreEqual(QuizGateResult.Waiting, wait.Result);
            Assert.AreEqual(TimeSpan.FromMinutes(QuizRules.WrongWaitMinutes - 1), wait.Wait);

            Assert.AreEqual(QuizGateResult.Waiting, QuizGate.Check(s, T(1, 12, QuizRules.WrongWaitMinutes, 0).AddSeconds(-1)).Result);
            Assert.AreEqual(QuizGateResult.Ready, QuizGate.Check(s, T(1, 12, QuizRules.WrongWaitMinutes, 0)).Result);
        }

        [Test]
        public void Gate_DailyLimit_ThenNextDayResets()
        {
            var s = new PlayerState();
            for (int i = 0; i < QuizRules.DailyAttempts; i++) QuizGate.Record(s, true, T(1, 10 + i));
            Assert.AreEqual(QuizGateResult.DailyLimit, QuizGate.Check(s, T(1, 20)).Result);
            Assert.AreEqual(QuizGateResult.DailyLimit, QuizGate.Check(s, T(1, 23, 59, 59)).Result);

            var next = QuizGate.Check(s, T(2, 0, 0, 1));
            Assert.AreEqual(QuizGateResult.Ready, next.Result);
            Assert.AreEqual(QuizRules.DailyAttempts, next.AttemptsLeft);

            QuizGate.Record(s, true, T(2, 0, 0, 1));
            Assert.AreEqual(1, s.QuizAttempts, "날이 바뀌면 횟수가 다시 센다");
        }

        [Test]
        public void Gate_WrongAnswersAlsoCountTowardLimit()
        {
            var s = new PlayerState();
            for (int i = 0; i < QuizRules.DailyAttempts; i++) QuizGate.Record(s, false, T(1, 8, i * 10));
            Assert.AreEqual(QuizGateResult.DailyLimit, QuizGate.Check(s, T(1, 20)).Result);
        }

        [Test]
        public void Gate_CorrectAfterWrong_ClearsWait()
        {
            var s = new PlayerState();
            QuizGate.Record(s, false, T(1, 12, 0, 0));
            QuizGate.Record(s, true, T(1, 12, 5, 0));
            Assert.AreEqual(QuizGateResult.Ready, QuizGate.Check(s, T(1, 12, 5, 1)).Result);
        }

        [Test]
        public void Gate_ClockRolledBack_WaitIsCapped()
        {
            var s = new PlayerState();
            QuizGate.Record(s, false, T(1, 12, 0, 0));
            var st = QuizGate.Check(s, T(1, 6, 0, 0));      // 기기 시계를 되감음
            Assert.AreEqual(QuizGateResult.Waiting, st.Result);
            Assert.AreEqual(TimeSpan.FromMinutes(QuizRules.WrongWaitMinutes), st.Wait);
        }

        [Test]
        public void Rewards_AreSensible()
        {
            Assert.AreEqual(QuizRules.RewardEasy, QuizRules.RewardFor(QuizOp.Add));
            Assert.AreEqual(QuizRules.RewardEasy, QuizRules.RewardFor(QuizOp.Sub));
            Assert.AreEqual(QuizRules.RewardHard, QuizRules.RewardFor(QuizOp.Mul));
            Assert.AreEqual(QuizRules.RewardHard, QuizRules.RewardFor(QuizOp.Div));
            Assert.Greater(QuizRules.RewardHard, QuizRules.RewardEasy);
        }

        [Test]
        public void FormatWait_Korean()
        {
            Assert.AreEqual("2분 15초", QuizGate.FormatWait(TimeSpan.FromSeconds(135)));
            Assert.AreEqual("3분", QuizGate.FormatWait(TimeSpan.FromMinutes(3)));
            Assert.AreEqual("45초", QuizGate.FormatWait(TimeSpan.FromSeconds(44.2)));
        }

        /* ------------------------------ 저장 호환 ------------------------------ */

        [Test]
        public void QuizFields_RoundTripThroughSaveSerializer()
        {
            var s = PlayerState.NewGame(_data, 0);
            QuizGate.Record(s, false, T(3, 9, 30, 0));
            QuizGate.Record(s, true, T(3, 9, 40, 0), 60);
            QuizGate.Record(s, false, T(3, 9, 50, 0));
            var loaded = SaveSerializer.FromJson(_data, SaveSerializer.ToJson(s));
            Assert.IsNotNull(loaded);
            Assert.AreEqual(s.QuizDay, loaded.QuizDay);
            Assert.AreEqual(3, loaded.QuizAttempts);
            Assert.AreEqual(s.QuizRetryUtc, loaded.QuizRetryUtc);
            Assert.AreEqual(s.Money, loaded.Money);
            Assert.AreEqual(QuizGateResult.Waiting, QuizGate.Check(loaded, T(3, 9, 51, 0)).Result);
        }

        [Test]
        public void OldSaveWithoutQuizFields_LoadsWithSafeDefaults()
        {
            var s = PlayerState.NewGame(_data, 0);
            var root = JObject.Parse(SaveSerializer.ToJson(s));
            var state = (JObject)root["State"];
            foreach (var f in new[] { "QuizDay", "QuizAttempts", "QuizRetryUtc" }) Assert.IsTrue(state.Remove(f), f);

            var loaded = SaveSerializer.FromJson(_data, root.ToString());
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.QuizDay);
            Assert.AreEqual(0, loaded.QuizAttempts);
            Assert.AreEqual(DateTime.MinValue, loaded.QuizRetryUtc);
            var st = QuizGate.Check(loaded, T(1));
            Assert.AreEqual(QuizGateResult.Ready, st.Result);
            Assert.AreEqual(QuizRules.DailyAttempts, st.AttemptsLeft);
        }

        [Test]
        public void NegativeQuizFields_AreClampedOnLoad()
        {
            var root = JObject.Parse(SaveSerializer.ToJson(PlayerState.NewGame(_data, 0)));
            root["State"]["QuizAttempts"] = -9;
            root["State"]["QuizDay"] = -3;
            var loaded = SaveSerializer.FromJson(_data, root.ToString());
            Assert.AreEqual((0, 0), (loaded.QuizDay, loaded.QuizAttempts));
        }

        /* ---------------------------------- 명단 ---------------------------------- */

        [Test]
        public void Roster_HasTeacherAndElevenStudentsInOrder()
        {
            Assert.AreEqual("장지원", Roster.Teacher);
            CollectionAssert.AreEqual(
                new[] { "문창식", "신서희", "김하나린", "정현진", "정민성", "김레담", "김영진", "오시우", "오수호", "정하성", "신희주" },
                Roster.Students);
            Assert.AreEqual(11, Roster.Students.Count);
            Assert.AreEqual(11, Roster.Students.Distinct().Count());
        }
    }
}
