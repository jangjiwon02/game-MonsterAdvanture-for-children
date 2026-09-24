using System;
using System.Collections;
using System.Linq;
using MonsterAdventure.Core;

namespace MonsterAdventure
{
    /// <summary>
    /// 남산초등학교 방문. 문 이벤트가 BeginScene/EndScene 으로 감싸서 <c>yield return SchoolQuizScene.Visit(...)</c> 로 부른다.
    /// 들어가면 "수학 퀴즈 / 명단 보기 / 나가기" 메뉴가 뜬다(명단은 월드 메뉴가 아니라 여기서만 본다).
    /// 퀴즈에서 그만두기(X)는 시도로 치지 않는다 — 시도는 정답을 고른 순간에만 기록된다.
    /// </summary>
    public static class SchoolQuizScene
    {
        /// <param name="now">현재 UTC 시각(보통 <c>() =&gt; DateTime.UtcNow</c>). 시간 주입이 가능하다.</param>
        /// <param name="onChanged">돈·퀴즈 상태가 바뀔 때마다 호출(단일 플레이는 자동저장, LAN 은 계정 동기화).</param>
        public static IEnumerator Visit(GameUi ui, PlayerState state, IRng rng, Func<DateTime> now, Action onChanged)
        {
            yield return ui.Say("어서 와! 여기는 남산초등학교야.");
            for (;;)
            {
                var items = new[] { "수학 퀴즈", "명단 보기", "나가기" };
                yield return ui.Choose(items, new MenuOptions
                {
                    Rect = new UnityEngine.Rect(UiKit.VirtualWidth - 158, 8, 150, items.Length * 26 + 16), Cancel = true,
                });
                int i = ui.Choice;
                if (i == -1 || i == 2) break;
                if (i == 0) yield return Attempt(ui, state, rng, now, onChanged);
                else yield return ui.RosterScreen();
            }
            yield return ui.Say("또 놀러 와!");
        }

        static IEnumerator Attempt(GameUi ui, PlayerState state, IRng rng, Func<DateTime> now, Action onChanged)
        {
            yield return ui.Say("수학 퀴즈를 맞히면 용돈을 줄게!");

            var status = QuizGate.Check(state, now());
            if (status.Result == QuizGateResult.DailyLimit)
            {
                yield return ui.Say($"오늘은 퀴즈를 {QuizRules.DailyAttempts}번 다 풀었어.\n내일 또 도전해 보렴!");
                yield break;
            }
            if (status.Result == QuizGateResult.Waiting)
            {
                yield return ui.Say($"조금 더 생각해 보고 오렴.\n{QuizGate.FormatWait(status.Wait)} 뒤에 다시 도전할 수 있어.");
                yield break;
            }

            yield return ui.Choose(new[] { "도전한다", "다음에 할게" },
                new MenuOptions
                {
                    Rect = new UnityEngine.Rect(UiKit.VirtualWidth - 158, 8, 150, 68), Cancel = true, Full = true,
                    Prompt = $"오늘 남은 기회는 {status.AttemptsLeft}번!\n도전해 볼래?",
                });
            if (ui.Choice != 0)
            {
                yield return ui.Say("그래, 다음에 또 오렴!");
                yield break;
            }

            var q = MathQuiz.Generate(rng);
            var nums = MathQuiz.MakeChoices(q, rng);
            var labels = nums.Select(n => n.ToString()).ToArray();
            yield return ui.QuizChoose($"남산초등학교 수학 퀴즈  ({status.AttemptsLeft}번 남음)", q.Text, labels);
            int pick = ui.Choice;
            if (pick < 0)
            {
                yield return ui.Say("퀴즈를 그만두었다.\n(도전 횟수는 줄지 않아.)");
                yield break;
            }

            bool correct = nums[pick] == q.Answer;
            int paid = QuizGate.Record(state, correct, now(), QuizRules.RewardFor(q.Op));
            onChanged?.Invoke();

            if (correct)
            {
                Sfx.Play(SfxKind.LevelUp);
                yield return ui.Say($"정답이야! 대단해!\n용돈 ₩{paid} 획득!");
            }
            else
            {
                Sfx.Play(SfxKind.Faint);
                yield return ui.Say($"아쉽다! 정답: {q.Answer}\n{QuizRules.WrongWaitMinutes}분 뒤에 다시 도전할 수 있어.");
            }

            int left = Math.Max(0, QuizRules.DailyAttempts - state.QuizAttempts);
            yield return ui.Say(left > 0 ? $"오늘 남은 기회: {left}번" : "오늘의 퀴즈는 여기까지야. 내일 또 만나자!");
        }
    }
}
