using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    public enum QuizOp { Add, Sub, Mul, Div }

    /// <summary>
    /// 남산초등학교 수학 퀴즈 한 문제.
    /// 범위: 덧셈·뺄셈 = 두 자릿수 ± 두 자릿수(뺄셈 답은 1 이상), 곱셈 = 두 자릿수 × 한 자릿수(2~9, 답 최대 891),
    /// 나눗셈 = 두 자릿수 ÷ 한 자릿수(2~9), 몫은 2 이상의 정수(나누어떨어짐).
    /// </summary>
    public sealed class QuizQuestion
    {
        public readonly int A, B, Answer;
        public readonly QuizOp Op;

        public QuizQuestion(int a, int b, QuizOp op)
        {
            A = a; B = b; Op = op;
            switch (op)
            {
                case QuizOp.Add: Answer = a + b; break;
                case QuizOp.Sub: Answer = a - b; break;
                case QuizOp.Mul: Answer = a * b; break;
                default: Answer = a / b; break;
            }
        }

        public static string Symbol(QuizOp op) =>
            op == QuizOp.Add ? "+" : op == QuizOp.Sub ? "-" : op == QuizOp.Mul ? "×" : "÷";

        /// <summary>화면에 보일 문구. 예: "47 + 68 = ?"</summary>
        public string Text => $"{A} {Symbol(Op)} {B} = ?";
    }

    public static class MathQuiz
    {
        /// <summary>네 연산을 같은 확률로 골라 한 문제를 낸다.</summary>
        public static QuizQuestion Generate(IRng rng)
        {
            var op = (QuizOp)rng.Range(0, 3);
            switch (op)
            {
                case QuizOp.Add:
                    return new QuizQuestion(rng.Range(10, 99), rng.Range(10, 99), op);
                case QuizOp.Sub:
                {
                    int a = rng.Range(11, 99);          // b < a 여야 답이 1 이상
                    return new QuizQuestion(a, rng.Range(10, a - 1), op);
                }
                case QuizOp.Mul:
                    return new QuizQuestion(rng.Range(10, 99), rng.Range(2, 9), op);
                default:
                {
                    int b = rng.Range(2, 9);
                    int qMin = Math.Max(2, (10 + b - 1) / b), qMax = 99 / b;    // 피제수 b*q 가 10~99
                    return new QuizQuestion(b * rng.Range(qMin, qMax), b, op);
                }
            }
        }

        /// <summary>정답 포함 서로 다른 4개 보기(모두 0 이상), 무작위 순서.</summary>
        public static int[] MakeChoices(QuizQuestion q, IRng rng)
        {
            int ans = q.Answer;
            var pool = new List<int>();
            void Add(int v) { if (v >= 0 && v != ans && !pool.Contains(v)) pool.Add(v); }
            // 한 끗 차이(올림/내림 실수)와 연산을 헷갈린 값
            Add(ans + 1); Add(ans - 1); Add(ans + 10); Add(ans - 10); Add(ans + 2); Add(ans - 2);
            switch (q.Op)
            {
                case QuizOp.Add: Add(Math.Abs(q.A - q.B)); break;
                case QuizOp.Sub: Add(q.A + q.B); break;
                case QuizOp.Mul: Add(q.A + q.B); Add(ans + q.A); Add(ans - q.A); break;
                default: Add(q.A - q.B); Add(ans + q.B); break;
            }
            Shuffle(pool, rng);

            var choices = new List<int> { ans };
            for (int i = 0; i < pool.Count && choices.Count < 4; i++) choices.Add(pool[i]);
            for (int k = 3; choices.Count < 4; k++)        // 후보가 모자랄 때만(사실상 도달하지 않음)
                if (!choices.Contains(ans + k)) choices.Add(ans + k);
            Shuffle(choices, rng);
            return choices.ToArray();
        }

        static void Shuffle<T>(IList<T> list, IRng rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Range(0, i);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    /// <summary>퀴즈 규칙 값. "하루"는 DailyBonus 와 같은 UTC 날짜 기준이다.</summary>
    public static class QuizRules
    {
        /// <summary>덧셈·뺄셈 정답 보상.</summary>
        public const int RewardEasy = 40;
        /// <summary>곱셈·나눗셈 정답 보상.</summary>
        public const int RewardHard = 60;
        /// <summary>하루 최대 시도 횟수(정답·오답 모두 센다).</summary>
        public const int DailyAttempts = 5;
        /// <summary>오답 후 재도전까지 기다리는 시간(분).</summary>
        public const int WrongWaitMinutes = 3;

        public static int RewardFor(QuizOp op) => op == QuizOp.Add || op == QuizOp.Sub ? RewardEasy : RewardHard;
    }

    public enum QuizGateResult { Ready, Waiting, DailyLimit }

    public readonly struct QuizStatus
    {
        public readonly QuizGateResult Result;
        public readonly TimeSpan Wait;          // Waiting 일 때 남은 시간
        public readonly int AttemptsLeft;       // 오늘 남은 기회
        public QuizStatus(QuizGateResult r, TimeSpan wait, int left) { Result = r; Wait = wait; AttemptsLeft = left; }
    }

    public static class QuizGate
    {
        /// <summary>UTC 날짜를 정수 일수로(0 = 아직 푼 적 없음).</summary>
        public static int DayKey(DateTime nowUtc) => (int)(nowUtc.Date - DateTime.UnixEpoch.Date).TotalDays + 1;

        static int AttemptsToday(PlayerState s, DateTime nowUtc) =>
            s.QuizDay == DayKey(nowUtc) ? s.QuizAttempts : 0;

        /// <summary>지금 풀 수 있는지. 상태를 바꾸지 않는다. 횟수 소진이 대기보다 먼저 안내된다.</summary>
        public static QuizStatus Check(PlayerState s, DateTime nowUtc)
        {
            int left = Math.Max(0, QuizRules.DailyAttempts - AttemptsToday(s, nowUtc));
            if (left == 0) return new QuizStatus(QuizGateResult.DailyLimit, TimeSpan.Zero, 0);
            var wait = s.QuizRetryUtc - nowUtc;
            var max = TimeSpan.FromMinutes(QuizRules.WrongWaitMinutes);
            if (wait > max) wait = max;             // 기기 시계를 되감아도 대기는 최대치까지만
            if (wait > TimeSpan.Zero) return new QuizStatus(QuizGateResult.Waiting, wait, left);
            return new QuizStatus(QuizGateResult.Ready, TimeSpan.Zero, left);
        }

        /// <summary>한 번의 시도를 기록한다. 날이 바뀌었으면 횟수를 먼저 0 으로 되돌린다.
        /// 정답이면 reward 를 지급하고 대기를 없앤다. 오답이면 재도전 가능 시각을 정한다. 지급한 금액을 돌려준다.</summary>
        public static int Record(PlayerState s, bool correct, DateTime nowUtc, int reward = QuizRules.RewardEasy)
        {
            int day = DayKey(nowUtc);
            if (s.QuizDay != day) { s.QuizDay = day; s.QuizAttempts = 0; }
            s.QuizAttempts++;
            if (correct)
            {
                s.Money += reward;
                s.QuizRetryUtc = DateTime.MinValue;
                return reward;
            }
            s.QuizRetryUtc = nowUtc.AddMinutes(QuizRules.WrongWaitMinutes);
            return 0;
        }

        /// <summary>남은 시간 문구. 예: "2분 15초", "45초"(초는 올림).</summary>
        public static string FormatWait(TimeSpan t)
        {
            int total = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
            int m = total / 60, sec = total % 60;
            if (m == 0) return $"{sec}초";
            return sec == 0 ? $"{m}분" : $"{m}분 {sec}초";
        }
    }
}
