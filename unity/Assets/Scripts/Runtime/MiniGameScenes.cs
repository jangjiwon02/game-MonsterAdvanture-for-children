using System;
using System.Collections;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 아파트 문으로 들어가는 작은 게임 두 개(미로 탈출 · 등딱지 피하기). 문 이벤트가 BeginScene/EndScene 으로 감싸서
    /// <c>yield return MiniGameScenes.Visit(...)</c> 로 부른다. 규칙은 Core(MiniGames.cs), 여기는 화면과 입력만.
    /// 도전 횟수는 시작하는 순간 센다(도중에 그만둬도 기회는 안 돌아온다). 성공하면 용돈을 주고, 돈이 바뀔 때마다 onChanged 를 부른다.
    /// </summary>
    public static class MiniGameScenes
    {
        public static IEnumerator Visit(ApartmentGame game, GameUi ui, PlayerState state, IRng rng, Func<DateTime> now, Action onChanged)
        {
            if (game == ApartmentGame.Maze) yield return MazeVisit(ui, state, rng, now, onChanged);
            else if (game == ApartmentGame.Shell) yield return ShellVisit(ui, state, rng, now, onChanged);
        }

        /// <summary>도전할지 묻는다. 결과는 ui.Choice == 0 이면 도전.</summary>
        static IEnumerator AskToStart(GameUi ui, int left, string rule, int reward)
        {
            yield return ui.Choose(new[] { "도전한다", "다음에 할게" }, new MenuOptions
            {
                Rect = new Rect(UiKit.VirtualWidth - 158, 8, 150, 68), Cancel = true, Full = true,
                Prompt = $"오늘 남은 기회는 {left}번!\n{rule}\n성공하면 용돈 ₩{reward}!",
            });
        }

        /* ------------------------------------------------ 미로 ------------------------------------------------ */

        static IEnumerator MazeVisit(GameUi ui, PlayerState state, IRng rng, Func<DateTime> now, Action onChanged)
        {
            yield return ui.Say("여기는 연수주공 6단지!\n복도가 미로처럼 얽혀 있어. 제한 시간 안에 출구까지 가 볼래?");
            int left = MiniGameGate.Left(state.MazeDay, state.MazeAttempts, now());
            if (left <= 0)
            {
                yield return ui.Say($"오늘은 미로를 {MiniGameRules.DailyAttempts}번 다 도전했어.\n내일 또 도전해 보렴!");
                yield break;
            }
            yield return AskToStart(ui, left, "제한 시간 안에 출구에 닿기.", MiniGameRules.MazeReward);
            if (ui.Choice != 0) { yield return ui.Say("그래, 다음에 또 오렴!"); yield break; }

            int index = Mazes.PickNext(rng, state.MazeLast);
            state.MazeLast = index;
            MiniGameGate.Record(ref state.MazeDay, ref state.MazeAttempts, now());
            onChanged?.Invoke();

            var run = new MazeRun(Mazes.Get(index));
            string banner = "준비!";
            Direction facing = Direction.Down;
            ui.DrawScene = () => DrawMaze(run, banner, facing, left - 1, index);

            // 3, 2, 1
            for (int n = 3; n >= 1; n--)
            {
                banner = n.ToString();
                yield return GameUi.Wait(0.8f);
            }
            banner = "출발!";
            GameInput.Instance.Flush();

            float stepTimer = 0f;
            bool quit = false;
            for (;;)
            {
                float dt = Time.deltaTime;
                run.Elapsed += dt;
                if (run.Elapsed > 0.6f) banner = null;

                while (GameInput.Instance.TryDequeue(out var key))
                    if (key == GameKey.Cancel) quit = true;
                if (quit)
                {
                    yield return ui.Choose(new[] { "계속한다", "그만둔다" }, new MenuOptions
                    {
                        Rect = new Rect(UiKit.VirtualWidth / 2f - 70, 120, 140, 68), Full = true, Prompt = "그만둘까요?\n(도전 횟수는 그대로 줄어요)",
                    });
                    if (ui.Choice == 1) break;
                    quit = false; GameInput.Instance.Flush();
                }

                stepTimer -= dt;
                var held = GameInput.Instance.HeldDirection;
                if (held.HasValue)
                {
                    facing = held.Value;
                    if (stepTimer <= 0f)
                    {
                        int dx = held == Direction.Left ? -1 : held == Direction.Right ? 1 : 0;
                        int dy = held == Direction.Up ? -1 : held == Direction.Down ? 1 : 0;
                        if (run.TryMove(dx, dy)) { stepTimer = 0.085f; Sfx.Play(SfxKind.Move); }
                    }
                }
                if (run.AtGoal || run.TimeUp) break;
                yield return null;
            }

            bool won = run.AtGoal;
            ui.DrawScene = null;
            GameInput.Instance.ClearHeld();
            if (won)
            {
                state.Money += MiniGameRules.MazeReward;
                onChanged?.Invoke();
                Sfx.Play(SfxKind.LevelUp);
                yield return ui.Say($"탈출 성공! 남은 시간 {Mathf.CeilToInt(run.TimeLeft)}초\n용돈 ₩{MiniGameRules.MazeReward} 획득!");
            }
            else
            {
                Sfx.Play(SfxKind.Faint);
                yield return ui.Say(run.TimeUp ? "시간이 다 됐어! 아쉽다..." : "미로를 그만두었다.");
            }
            int remaining = MiniGameGate.Left(state.MazeDay, state.MazeAttempts, now());
            yield return ui.Say(remaining > 0 ? $"오늘 남은 기회: {remaining}번" : "오늘의 미로는 여기까지야. 내일 또 만나자!");
        }

        const float MazeCell = 20f, MazeX = 30f, MazeY = 48f;

        static void DrawMaze(MazeRun run, string banner, Direction facing, int attemptsLeftAfter, int index)
        {
            var lay = run.Layout;
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, UiKit.C("#1e2748"));
            UiKit.Text($"미로 탈출 {index + 1}/{Mazes.Count}", 12, 8, 14, UiKit.C("#ffd84a"), bold: true);
            UiKit.Text($"남은 기회 {attemptsLeftAfter}번   (X: 그만두기)", UiKit.VirtualWidth - 12, 10, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperRight);

            // 남은 시간 막대
            float frac = Mathf.Clamp01(run.TimeLeft / lay.TimeLimit);
            UiKit.Fill(12, 28, 456, 10, UiKit.C("#0a0d1a"));
            UiKit.Fill(13, 29, 454 * frac, 8, frac > .5f ? UiKit.C("#58d058") : frac > .2f ? UiKit.C("#f8d030") : UiKit.C("#f05040"));
            UiKit.Text($"{Mathf.CeilToInt(run.TimeLeft)}초", UiKit.VirtualWidth / 2f, 26, 11, Color.white, TextAnchor.UpperCenter, true);

            var wall = UiKit.C("#3a4a8a"); var floor = UiKit.C("#f3ecd2");
            for (int y = 0; y < lay.Height; y++)
                for (int x = 0; x < lay.Width; x++)
                    UiKit.Fill(MazeX + x * MazeCell, MazeY + y * MazeCell, MazeCell, MazeCell, lay.IsWall(x, y) ? wall : floor);

            // 출구: 깃발
            float gx = MazeX + lay.GoalX * MazeCell, gy = MazeY + lay.GoalY * MazeCell;
            UiKit.Fill(gx + 5, gy + 2, 2, 16, UiKit.C("#7a5a3a"));
            UiKit.Fill(gx + 7, gy + 2, 10, 7, UiKit.C("#e63a2e"));
            // 출발 표시
            UiKit.Fill(MazeX + lay.StartX * MazeCell + 4, MazeY + lay.StartY * MazeCell + 4, 12, 12, UiKit.C("#58d058", .45f));

            var tex = PlayerArt.Build()[(int)facing].texture;
            UiKit.Texture(tex, MazeX + run.X * MazeCell - 2, MazeY + run.Y * MazeCell - 4, MazeCell + 4, MazeCell + 4);

            if (!string.IsNullOrEmpty(banner))
            {
                UiKit.Fill(150, 130, 180, 60, new Color(0, 0, 0, .65f));
                UiKit.Text(banner, UiKit.VirtualWidth / 2f, 140, 36, Color.white, TextAnchor.UpperCenter, true);
            }
        }

        /* ------------------------------------------------ 등딱지 ------------------------------------------------ */

        static Texture2D _shellTex;

        static Texture2D ShellTexture()
        {
            if (_shellTex != null) return _shellTex;
            var p = new Painter(32, 32);
            Color32 C(string h) => Painter.Hex(h);
            p.Ellipse(16, 16, 15, 15, C("#f2e2a0"), C("#7a6a30"));      // 테두리(크림색)
            p.Ellipse(16, 16, 12, 12, C("#2fa84a"), C("#14602a"));      // 등딱지
            p.Ellipse(16, 16, 5, 5, C("#8ae08a"));
            foreach (var (ax, ay) in new[] { (16f, 5f), (16f, 27f), (5f, 16f), (27f, 16f) })
                p.Line(new Vector2(16, 16), new Vector2(ax, ay), 1.5f, C("#14602a"));
            return _shellTex = p.ToTexture();
        }

        static IEnumerator ShellVisit(GameUi ui, PlayerState state, IRng rng, Func<DateTime> now, Action onChanged)
        {
            yield return ui.Say("여기는 세경아파트!\n옥상에서 거북이 등딱지가 마구 날아와... 끝까지 피할 수 있겠니?");
            int left = MiniGameGate.Left(state.ShellDay, state.ShellAttempts, now());
            if (left <= 0)
            {
                yield return ui.Say($"오늘은 등딱지 피하기를 {MiniGameRules.DailyAttempts}번 다 했어.\n내일 또 도전해 보렴!");
                yield break;
            }
            yield return AskToStart(ui, left, $"{(int)ShellDodge.Duration}초 동안 한 번도 안 맞기.", MiniGameRules.ShellReward);
            if (ui.Choice != 0) { yield return ui.Say("그래, 다음에 또 오렴!"); yield break; }

            MiniGameGate.Record(ref state.ShellDay, ref state.ShellAttempts, now());
            onChanged?.Invoke();

            var run = new ShellDodge();
            run.Spawning = false;
            string banner = "준비!";
            Direction facing = Direction.Down;
            ui.DrawScene = () => DrawShell(run, banner, facing, left - 1);

            for (int n = 3; n >= 1; n--) { banner = n.ToString(); yield return GameUi.Wait(0.8f); }
            banner = "시작!";
            run.Spawning = true;
            GameInput.Instance.Flush();

            bool quit = false;
            float bannerUntil = 0.6f;
            for (;;)
            {
                float dt = Mathf.Min(Time.deltaTime, 0.05f);   // 프레임이 튀어도 한 번에 너무 멀리 날아가지 않게
                var held = GameInput.Instance.HeldDirection;
                int dx = held == Direction.Left ? -1 : held == Direction.Right ? 1 : 0;
                int dy = held == Direction.Up ? -1 : held == Direction.Down ? 1 : 0;
                if (held.HasValue) facing = held.Value;
                run.Step(dt, dx, dy, rng);
                bannerUntil -= dt; if (bannerUntil < 0) banner = null;

                while (GameInput.Instance.TryDequeue(out var key))
                    if (key == GameKey.Cancel) quit = true;
                if (quit)
                {
                    yield return ui.Choose(new[] { "계속한다", "그만둔다" }, new MenuOptions
                    {
                        Rect = new Rect(UiKit.VirtualWidth / 2f - 70, 120, 140, 68), Full = true, Prompt = "그만둘까요?\n(도전 횟수는 그대로 줄어요)",
                    });
                    if (ui.Choice == 1) break;
                    quit = false; GameInput.Instance.Flush();
                }
                if (run.Over) break;
                yield return null;
            }

            bool won = run.Won;
            bool hit = run.Hit;
            if (hit) { Sfx.Play(SfxKind.Hit); yield return GameUi.Wait(0.5f); }   // 맞은 장면을 잠깐 보여 준다
            ui.DrawScene = null;
            GameInput.Instance.ClearHeld();
            if (won)
            {
                state.Money += MiniGameRules.ShellReward;
                onChanged?.Invoke();
                Sfx.Play(SfxKind.LevelUp);
                yield return ui.Say($"{(int)ShellDodge.Duration}초 동안 다 피했어! 대단해!\n용돈 ₩{MiniGameRules.ShellReward} 획득!");
            }
            else
            {
                Sfx.Play(SfxKind.Faint);
                yield return ui.Say(hit ? $"으악, 맞았다!\n{run.Elapsed:0.0}초 버텼어. 아쉽다..." : "등딱지 피하기를 그만두었다.");
            }
            int remaining = MiniGameGate.Left(state.ShellDay, state.ShellAttempts, now());
            yield return ui.Say(remaining > 0 ? $"오늘 남은 기회: {remaining}번" : "오늘의 등딱지 피하기는 여기까지야. 내일 또 만나자!");
        }

        static void DrawShell(ShellDodge run, string banner, Direction facing, int attemptsLeftAfter)
        {
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, UiKit.C("#2a3a2c"));
            // 바닥 격자무늬
            for (int gy = 0; gy < 7; gy++)
                for (int gx = 0; gx < 12; gx++)
                    if ((gx + gy) % 2 == 0)
                        UiKit.Fill(gx * 40, ShellDodge.AreaTop + gy * 40, 40, 40, UiKit.C("#33463a"));
            UiKit.Fill(0, 0, UiKit.VirtualWidth, ShellDodge.AreaTop, UiKit.C("#1e2748"));
            UiKit.Text("등딱지 피하기", 12, 8, 14, UiKit.C("#ffd84a"), bold: true);
            UiKit.Text($"남은 기회 {attemptsLeftAfter}번   (X: 그만두기)", UiKit.VirtualWidth - 12, 10, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperRight);

            float frac = Mathf.Clamp01(run.TimeLeft / ShellDodge.Duration);
            UiKit.Fill(12, 28, 456, 8, UiKit.C("#0a0d1a"));
            UiKit.Fill(13, 29, 454 * (1f - frac), 6, UiKit.C("#58d058"));   // 버틴 만큼 차오른다
            UiKit.Text($"{Mathf.CeilToInt(run.TimeLeft)}초", UiKit.VirtualWidth / 2f, 24, 10, Color.white, TextAnchor.UpperCenter, true);

            var tex = ShellTexture();
            foreach (var s in run.Shells)
            {
                var r = UiKit.ToScreen(s.X - 11, s.Y - 11, 22, 22);
                var old = GUI.matrix;
                GUIUtility.RotateAroundPivot(s.Angle, r.center);
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
                GUI.matrix = old;
            }

            var ptex = PlayerArt.Build()[(int)facing].texture;
            if (run.Hit) UiKit.Fill(run.PlayerX - 14, run.PlayerY - 14, 28, 28, UiKit.C("#f05040", .5f));
            UiKit.Texture(ptex, run.PlayerX - 14, run.PlayerY - 16, 28, 28);

            if (!string.IsNullOrEmpty(banner))
            {
                UiKit.Fill(150, 130, 180, 60, new Color(0, 0, 0, .65f));
                UiKit.Text(banner, UiKit.VirtualWidth / 2f, 140, 36, Color.white, TextAnchor.UpperCenter, true);
            }
        }
    }
}
