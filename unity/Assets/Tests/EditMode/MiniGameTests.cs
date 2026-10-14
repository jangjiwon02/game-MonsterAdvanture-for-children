using System;
using System.Collections.Generic;
using System.IO;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>아파트 작은 게임: 미로 탈출(연수주공 6단지) · 등딱지 피하기(세경아파트).</summary>
    public class MiniGameTests
    {
        static readonly DateTime Noon = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        static readonly (int, int)[] Dirs4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        [Test]
        public void DailyAttempts_AreThreePerGame_AndResetTheNextDay()
        {
            int day = 0, tries = 0;
            Assert.AreEqual(3, MiniGameGate.Left(day, tries, Noon));
            for (int i = 0; i < 3; i++) MiniGameGate.Record(ref day, ref tries, Noon);
            Assert.AreEqual(0, MiniGameGate.Left(day, tries, Noon));
            Assert.AreEqual(0, MiniGameGate.Left(day, tries, Noon.AddHours(5)), "같은 날이면 계속 0");
            Assert.AreEqual(3, MiniGameGate.Left(day, tries, Noon.AddDays(1)), "다음 날엔 다시 3번");
            MiniGameGate.Record(ref day, ref tries, Noon.AddDays(1));
            Assert.AreEqual(2, MiniGameGate.Left(day, tries, Noon.AddDays(1)));
        }

        [Test]
        public void Mazes_AreFiveDistinctSolvableMazes()
        {
            Assert.AreEqual(5, Mazes.Count);
            var seen = new HashSet<string>();
            for (int i = 0; i < Mazes.Count; i++)
            {
                var m = Mazes.Get(i);
                Assert.Greater(m.ShortestSteps, 20, $"미로 {i} 가 풀 수 있는 길이여야 한다");
                Assert.IsFalse(m.IsWall(m.StartX, m.StartY));
                Assert.IsFalse(m.IsWall(m.GoalX, m.GoalY));
                Assert.Greater(m.TimeLimit, m.ShortestSteps * 0.3f, "제한 시간이 최단 경로를 걷기에 모자라면 안 된다");
                var sb = new System.Text.StringBuilder();
                for (int y = 0; y < m.Height; y++) for (int x = 0; x < m.Width; x++) sb.Append(m.IsWall(x, y) ? '#' : '.');
                Assert.IsTrue(seen.Add(sb.ToString()), $"미로 {i} 가 다른 미로와 같다");
                Assert.AreSame(m, Mazes.Get(i), "같은 번호는 늘 같은 미로");
            }
        }

        [Test]
        public void MazePickNext_NeverRepeatsLast_AndEventuallyUsesAll()
        {
            var rng = new SystemRng(3);
            var used = new HashSet<int>();
            int last = -1;
            for (int i = 0; i < 200; i++)
            {
                int next = Mazes.PickNext(rng, last);
                Assert.AreNotEqual(last, next);
                Assert.That(next, Is.InRange(0, Mazes.Count - 1));
                used.Add(next); last = next;
            }
            Assert.AreEqual(Mazes.Count, used.Count);
        }

        [Test]
        public void MazeRun_WalksFollowingTheShortestPath_AndStopsAtWalls()
        {
            var m = Mazes.Get(0);
            var run = new MazeRun(m);
            int wallDirs = 0;
            foreach (var (dx, dy) in Dirs4)
                if (m.IsWall(run.X + dx, run.Y + dy)) { Assert.IsFalse(run.TryMove(dx, dy)); wallDirs++; }
            Assert.Greater(wallDirs, 0);

            // BFS 로 최단 경로를 구해 그대로 걸으면 정확히 ShortestSteps 번에 도착한다.
            var prev = new Dictionary<(int, int), (int, int)>();
            var q = new Queue<(int, int)>();
            q.Enqueue((m.StartX, m.StartY));
            prev[(m.StartX, m.StartY)] = (-1, -1);
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                foreach (var (dx, dy) in Dirs4)
                {
                    var n = (x + dx, y + dy);
                    if (m.IsWall(n.Item1, n.Item2) || prev.ContainsKey(n)) continue;
                    prev[n] = (x, y); q.Enqueue(n);
                }
            }
            var path = new List<(int, int)>();
            for (var c = (m.GoalX, m.GoalY); c != (-1, -1); c = prev[c]) path.Add(c);
            path.Reverse();
            for (int i = 1; i < path.Count; i++)
                Assert.IsTrue(run.TryMove(path[i].Item1 - path[i - 1].Item1, path[i].Item2 - path[i - 1].Item2));
            Assert.IsTrue(run.AtGoal);
            Assert.AreEqual(m.ShortestSteps, run.Moves);
        }

        [Test]
        public void MazeRun_TimeUp_AfterTheTimeLimit()
        {
            var run = new MazeRun(Mazes.Get(1));
            Assert.IsFalse(run.TimeUp);
            run.Elapsed = run.Layout.TimeLimit + 0.01f;
            Assert.IsTrue(run.TimeUp);
            Assert.AreEqual(0f, run.TimeLeft);
        }

        [Test]
        public void Shell_SurvivingTheWholeDurationWithNoShells_Wins()
        {
            var g = new ShellDodge { Spawning = false };
            var rng = new SystemRng(1);
            for (int i = 0; i < 400 && !g.Over; i++) g.Step(0.1f, 0, 0, rng);
            Assert.IsTrue(g.Won);
            Assert.IsFalse(g.Hit);
        }

        [Test]
        public void Shell_HeadingForThePlayer_Hits_AndEndsTheRun()
        {
            var g = new ShellDodge { Spawning = false };
            var rng = new SystemRng(1);
            g.AddShell(g.PlayerX - 60, g.PlayerY, 150f, 0f);    // 곧장 날아온다
            for (int i = 0; i < 30 && !g.Over; i++) g.Step(0.05f, 0, 0, rng);
            Assert.IsTrue(g.Hit);
            Assert.IsFalse(g.Won);
            Assert.IsTrue(g.Over);
        }

        [Test]
        public void Shell_MovingOutOfThePath_Dodges()
        {
            var g = new ShellDodge { Spawning = false };
            var rng = new SystemRng(1);
            g.AddShell(g.PlayerX - 60, g.PlayerY, 150f, 0f);
            for (int i = 0; i < 70; i++) g.Step(0.05f, 0, -1, rng);   // 위로 비켜 선다(경계에서 멈춘다)
            Assert.IsFalse(g.Hit, "비켜 서면 맞지 않는다");
            Assert.AreEqual(0, g.Shells.Count, "지나간 등딱지는 사라진다");
        }

        [Test]
        public void Shell_PlayerStaysInsideTheArena()
        {
            var g = new ShellDodge { Spawning = false };
            var rng = new SystemRng(1);
            for (int i = 0; i < 200 && !g.Over; i++) g.Step(0.05f, -1, -1, rng);
            Assert.GreaterOrEqual(g.PlayerX, ShellDodge.AreaLeft);
            Assert.GreaterOrEqual(g.PlayerY, ShellDodge.AreaTop);
            var h = new ShellDodge { Spawning = false };
            for (int i = 0; i < 200 && !h.Over; i++) h.Step(0.05f, 1, 1, rng);
            Assert.LessOrEqual(h.PlayerX, ShellDodge.AreaRight);
            Assert.LessOrEqual(h.PlayerY, ShellDodge.AreaBottom);
        }

        [Test]
        public void Shell_StandingStillMostlyGetsYouHit_SoTheGameHasAChallenge()
        {
            int hits = 0;
            for (int seed = 0; seed < 20; seed++)
            {
                var g = new ShellDodge();
                var rng = new SystemRng(seed);
                for (int i = 0; i < 700 && !g.Over; i++) g.Step(0.05f, 0, 0, rng);
                if (g.Hit) hits++;
            }
            Assert.Greater(hits, 12, "가만히 서 있으면 대부분 맞아야 게임이 된다");
        }

        /// <summary>사람이 하듯 "곧 부딪힐 등딱지에서 멀어지는" 간단한 규칙만으로도 대부분 끝까지 버텨야 한다(너무 어렵지 않다는 확인).</summary>
        [Test]
        public void Shell_AGreedyDodgingBot_CanUsuallySurvive()
        {
            int wins = 0;
            for (int seed = 0; seed < 20; seed++)
            {
                var g = new ShellDodge();
                var rng = new SystemRng(seed);
                for (int i = 0; i < 700 && !g.Over; i++)
                {
                    int bestDx = 0, bestDy = 0; float best = float.MinValue;
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        float px = g.PlayerX + dx * ShellDodge.PlayerSpeed * 0.25f, py = g.PlayerY + dy * ShellDodge.PlayerSpeed * 0.25f;
                        px = Mathf.Clamp(px, ShellDodge.AreaLeft + 7, ShellDodge.AreaRight - 7);
                        py = Mathf.Clamp(py, ShellDodge.AreaTop + 7, ShellDodge.AreaBottom - 7);
                        float worst = 1000f;
                        foreach (var sh in g.Shells)
                            for (float t = 0.05f; t <= 0.5f; t += 0.05f)
                            {
                                float ex = sh.X + sh.Vx * t - px, ey = sh.Y + sh.Vy * t - py;
                                worst = Mathf.Min(worst, Mathf.Sqrt(ex * ex + ey * ey));
                            }
                        float score = worst - (dx == 0 && dy == 0 ? 0f : 0.5f)
                            - 0.05f * (Mathf.Abs(px - 240f) + Mathf.Abs(py - 180f)) * 0.1f;
                        if (score > best) { best = score; bestDx = dx; bestDy = dy; }
                    }
                    g.Step(0.05f, bestDx, bestDy, rng);
                }
                if (g.Won) wins++;
            }
            Assert.GreaterOrEqual(wins, 14, $"사람 같은 회피 규칙으로 20판 중 {wins}판만 이겼다 — 너무 어렵다");
        }

        [Test]
        public void ApartmentDoors_ArePlacedAndLeadToTheRightGames()
        {
            var map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(map);
            Assert.AreEqual(Tile.AptDoor, map[WorldMap.MazeDoorX, WorldMap.MazeDoorY]);
            Assert.AreEqual(Tile.AptDoor, map[WorldMap.ShellDoorX, WorldMap.ShellDoorY]);
            Assert.IsTrue(map.IsPassable(WorldMap.MazeDoorX, WorldMap.MazeDoorY), "문은 걸어 들어갈 수 있다");
            Assert.AreEqual(ApartmentGame.Maze, WorldMap.GameAt(WorldMap.MazeDoorX, WorldMap.MazeDoorY));
            Assert.AreEqual(ApartmentGame.Shell, WorldMap.GameAt(WorldMap.ShellDoorX, WorldMap.ShellDoorY));
            Assert.AreEqual(ApartmentGame.None, WorldMap.GameAt(0, 0));
            var six = Array.Find(WorldMap.YeonsuApartments, a => a.Line2 == "6단지");
            Assert.AreEqual((six.X + 1, six.Y + 2), (WorldMap.MazeDoorX, WorldMap.MazeDoorY), "미로 문은 연수주공 6단지 맨 아래 줄 가운데");
        }

        [Test]
        public void PlayerState_MiniGameCounters_SurviveSaveAndLoad()
        {
            var data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));
            var s = PlayerState.NewGame(data, 0);
            int d = 0, a = 0;
            MiniGameGate.Record(ref d, ref a, Noon);
            s.MazeDay = d; s.MazeAttempts = a; s.MazeLast = 3;
            var loaded = SaveSerializer.FromJson(data, SaveSerializer.ToJson(s));
            Assert.AreEqual((d, a, 3), (loaded.MazeDay, loaded.MazeAttempts, loaded.MazeLast));
            Assert.AreEqual(-1, PlayerState.NewGame(data, 0).MazeLast);
        }
    }
}
