using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>아파트 문으로 들어가는 작은 게임들(Unity 전용 추가). 화면·입력은 Runtime(MiniGameScenes)이 맡고, 규칙은 여기서 순수하게 계산한다.</summary>
    public enum ApartmentGame { None, Maze, Shell }

    public static class MiniGameRules
    {
        /// <summary>하루(UTC 날짜 기준, 수학 퀴즈와 같다) 최대 도전 횟수. 게임마다 따로 센다.</summary>
        public const int DailyAttempts = 3;
        public const int MazeReward = 150;
        public const int ShellReward = 150;
    }

    /// <summary>하루 도전 횟수 제한. 시도를 시작하는 순간 기록한다 — 도중에 그만둬도 기회는 돌아오지 않는다.</summary>
    public static class MiniGameGate
    {
        public static int Left(int storedDay, int attempts, DateTime nowUtc) =>
            Math.Max(0, MiniGameRules.DailyAttempts - (storedDay == QuizGate.DayKey(nowUtc) ? attempts : 0));

        /// <summary>한 번의 시도를 센다. 날이 바뀌었으면 횟수를 먼저 0 으로 되돌린다.</summary>
        public static void Record(ref int storedDay, ref int attempts, DateTime nowUtc)
        {
            int day = QuizGate.DayKey(nowUtc);
            if (storedDay != day) { storedDay = day; attempts = 0; }
            attempts++;
        }
    }

    /* ------------------------------------------------ 미로 ------------------------------------------------ */

    /// <summary>고정된 미로 한 판. 벽 격자(홀수 크기)와 출발·도착 칸, 제한 시간을 가진다.</summary>
    public sealed class MazeLayout
    {
        public readonly int Width, Height;
        readonly bool[,] _wall;
        public readonly int StartX, StartY, GoalX, GoalY;
        /// <summary>출발에서 도착까지의 최단 칸 수.</summary>
        public readonly int ShortestSteps;
        /// <summary>제한 시간(초): 최단 경로를 넉넉히 걸을 시간 + 여유.</summary>
        public float TimeLimit => 15f + ShortestSteps * 0.5f;

        public bool IsWall(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height || _wall[y, x];

        internal MazeLayout(bool[,] wall, int sx, int sy, int gx, int gy)
        {
            _wall = wall; Height = wall.GetLength(0); Width = wall.GetLength(1);
            StartX = sx; StartY = sy; GoalX = gx; GoalY = gy;
            ShortestSteps = Shortest();
        }

        int Shortest()
        {
            var dist = new int[Height, Width];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) dist[y, x] = -1;
            var q = new Queue<(int X, int Y)>();
            q.Enqueue((StartX, StartY)); dist[StartY, StartX] = 0;
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                if (x == GoalX && y == GoalY) return dist[y, x];
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy;
                    if (IsWall(nx, ny) || dist[ny, nx] >= 0) continue;
                    dist[ny, nx] = dist[y, x] + 1; q.Enqueue((nx, ny));
                }
            }
            return -1;
        }

        static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    }

    /// <summary>다섯 개의 미로. 해마다 같은 모양이도록 고정된 씨앗으로 만든다(완전 미로 — 출구까지 길이 반드시 있다).</summary>
    public static class Mazes
    {
        public const int Count = 5;
        public const int CellsX = 10, CellsY = 6;   // 칸 수 → 격자 21 x 13

        // (씨앗, 출발 칸 x, y, 도착 칸 x, y) — 칸 좌표는 CellsX x CellsY 기준
        static readonly (int Seed, int Sx, int Sy, int Gx, int Gy)[] Specs =
        {
            (7101, 0, 0, 9, 5), (7202, 0, 5, 9, 0), (7303, 9, 0, 0, 5), (7404, 9, 5, 0, 0), (7505, 4, 0, 5, 5),
        };

        static readonly MazeLayout[] Cache = new MazeLayout[Count];

        public static MazeLayout Get(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return Cache[index] ??= Build(Specs[index]);
        }

        /// <summary>지난번과 같은 미로가 연달아 나오지 않게 무작위로 고른다(다섯 판이 돌아가며 나온다).</summary>
        public static int PickNext(IRng rng, int lastIndex)
        {
            var choices = new List<int>();
            for (int i = 0; i < Count; i++) if (i != lastIndex) choices.Add(i);
            return rng.Pick(choices);
        }

        static MazeLayout Build((int Seed, int Sx, int Sy, int Gx, int Gy) s)
        {
            int w = CellsX * 2 + 1, h = CellsY * 2 + 1;
            var wall = new bool[h, w];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) wall[y, x] = true;

            var rng = new Mulberry32(s.Seed);
            var visited = new bool[CellsY, CellsX];
            var stack = new Stack<(int X, int Y)>();
            stack.Push((s.Sx, s.Sy)); visited[s.Sy, s.Sx] = true; wall[s.Sy * 2 + 1, s.Sx * 2 + 1] = false;
            var next = new List<(int X, int Y)>();
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Peek();
                next.Clear();
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx >= 0 && ny >= 0 && nx < CellsX && ny < CellsY && !visited[ny, nx]) next.Add((nx, ny));
                }
                if (next.Count == 0) { stack.Pop(); continue; }
                var (tx, ty) = next[rng.Range(0, next.Count - 1)];
                wall[cy * 2 + 1 + (ty - cy), cx * 2 + 1 + (tx - cx)] = false;   // 두 칸 사이 벽을 허문다
                wall[ty * 2 + 1, tx * 2 + 1] = false;
                visited[ty, tx] = true; stack.Push((tx, ty));
            }
            return new MazeLayout(wall, s.Sx * 2 + 1, s.Sy * 2 + 1, s.Gx * 2 + 1, s.Gy * 2 + 1);
        }
    }

    /// <summary>미로 한 번의 도전. 칸 단위로 움직이고, 시간이 흐른다.</summary>
    public sealed class MazeRun
    {
        public readonly MazeLayout Layout;
        public int X { get; private set; }
        public int Y { get; private set; }
        public float Elapsed;
        public int Moves { get; private set; }

        public MazeRun(MazeLayout layout) { Layout = layout; X = layout.StartX; Y = layout.StartY; }

        public bool AtGoal => X == Layout.GoalX && Y == Layout.GoalY;
        public bool TimeUp => Elapsed >= Layout.TimeLimit;
        public float TimeLeft => Math.Max(0f, Layout.TimeLimit - Elapsed);

        public bool TryMove(int dx, int dy)
        {
            if (Math.Abs(dx) + Math.Abs(dy) != 1 || Layout.IsWall(X + dx, Y + dy)) return false;
            X += dx; Y += dy; Moves++;
            return true;
        }
    }

    /* -------------------------------------------- 등딱지 피하기 -------------------------------------------- */

    /// <summary>
    /// 날아오는 거북이 등딱지를 피하는 한 판. 화면(480x320) 가장자리 밖에서 등딱지가 플레이어 쪽으로 날아오고,
    /// <see cref="Duration"/> 초 동안 한 번도 안 맞으면 성공이다. 시간이 갈수록 더 빠르고 더 자주 날아온다.
    /// </summary>
    public sealed class ShellDodge
    {
        public const float Duration = 30f;
        public const float AreaLeft = 0f, AreaRight = 480f, AreaTop = 40f, AreaBottom = 320f;
        public const float PlayerRadius = 7f, ShellRadius = 8f, PlayerSpeed = 135f;

        public struct Shell { public float X, Y, Vx, Vy, Angle; }

        public float PlayerX { get; private set; } = (AreaLeft + AreaRight) / 2f;
        public float PlayerY { get; private set; } = (AreaTop + AreaBottom) / 2f;
        public float Elapsed { get; private set; }
        public bool Hit { get; private set; }
        public readonly List<Shell> Shells = new List<Shell>();
        /// <summary>테스트용: false 면 새 등딱지가 생기지 않는다.</summary>
        public bool Spawning = true;

        float _spawnTimer = 0.8f;   // 시작하고 첫 등딱지까지

        public bool Won => !Hit && Elapsed >= Duration;
        public bool Over => Hit || Elapsed >= Duration;
        public float TimeLeft => Math.Max(0f, Duration - Elapsed);

        /// <summary>dt 초만큼 진행한다. (dx, dy) 는 플레이어가 누른 방향(-1/0/1).</summary>
        public void Step(float dt, int dx, int dy, IRng rng)
        {
            if (Over) return;
            Elapsed += dt;

            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len > 0)
            {
                PlayerX = Clamp(PlayerX + dx / len * PlayerSpeed * dt, AreaLeft + PlayerRadius, AreaRight - PlayerRadius);
                PlayerY = Clamp(PlayerY + dy / len * PlayerSpeed * dt, AreaTop + PlayerRadius, AreaBottom - PlayerRadius);
            }

            float progress = Math.Min(1f, Elapsed / Duration);
            if (Spawning)
            {
                _spawnTimer -= dt;
                while (_spawnTimer <= 0)
                {
                    Spawn(rng, Lerp(95f, 160f, progress));
                    _spawnTimer += Lerp(1.1f, 0.55f, progress);
                }
            }

            for (int i = Shells.Count - 1; i >= 0; i--)
            {
                var s = Shells[i];
                s.X += s.Vx * dt; s.Y += s.Vy * dt; s.Angle += 540f * dt;
                if (s.X < AreaLeft - 40 || s.X > AreaRight + 40 || s.Y < AreaTop - 40 || s.Y > AreaBottom + 40) { Shells.RemoveAt(i); continue; }
                Shells[i] = s;
                float ddx = s.X - PlayerX, ddy = s.Y - PlayerY, r = PlayerRadius + ShellRadius - 2f;   // 살짝 너그럽게
                if (ddx * ddx + ddy * ddy < r * r) Hit = true;
            }
        }

        /// <summary>테스트·연출용: 지정한 자리에서 지정한 속도로 날아가는 등딱지를 하나 더한다.</summary>
        public void AddShell(float x, float y, float vx, float vy) => Shells.Add(new Shell { X = x, Y = y, Vx = vx, Vy = vy });

        void Spawn(IRng rng, float speed)
        {
            float x, y;
            switch (rng.Range(0, 3))
            {
                case 0: x = Lerp(AreaLeft, AreaRight, (float)rng.NextDouble()); y = AreaTop - 12; break;      // 위
                case 1: x = Lerp(AreaLeft, AreaRight, (float)rng.NextDouble()); y = AreaBottom + 12; break;   // 아래
                case 2: x = AreaLeft - 12; y = Lerp(AreaTop, AreaBottom, (float)rng.NextDouble()); break;     // 왼쪽
                default: x = AreaRight + 12; y = Lerp(AreaTop, AreaBottom, (float)rng.NextDouble()); break;   // 오른쪽
            }
            // 지금 플레이어 자리 근처를 겨냥하되 조금 빗나가게 — 계속 움직이면 피할 수 있다.
            float tx = PlayerX + ((float)rng.NextDouble() - .5f) * 110f, ty = PlayerY + ((float)rng.NextDouble() - .5f) * 110f;
            float vx = tx - x, vy = ty - y, d = (float)Math.Sqrt(vx * vx + vy * vy);
            if (d < 1f) { vx = 1f; vy = 0f; d = 1f; }
            Shells.Add(new Shell { X = x, Y = y, Vx = vx / d * speed, Vy = vy / d * speed, Angle = (float)rng.NextDouble() * 360f });
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
