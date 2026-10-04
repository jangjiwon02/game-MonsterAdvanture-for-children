using System;

namespace MonsterAdventure.Core
{
    /// <summary>타일 값은 웹 버전과 같다(SPEC "월드"). 11/12는 중앙탑(아래/위) 장식.
    /// 13/14(SchoolRoof/SchoolDoor)는 웹에 없는 Unity 전용 추가 — <see cref="WorldMap.AddChungjuLandmarks"/> 참고.</summary>
    public enum Tile : byte
    {
        Grass = 0, TallGrass = 1, Tree = 2, Water = 3, Path = 4, Wall = 5,
        CenterDoor = 6, ShopDoor = 7, Flower = 8, CenterRoof = 9, ShopRoof = 10,
        TowerBase = 11, TowerTop = 12, SchoolRoof = 13, SchoolDoor = 14,
    }

    /// <summary>충주 시내 맵 48x36. 시드 20240921 로 웹과 같은 지형이 나온다.</summary>
    public sealed class WorldMap
    {
        public const int Width = 48, Height = 36;
        public const int VillageX = 24, VillageY = 18;
        public const int DefaultSeed = 20240921;

        readonly Tile[,] _tiles = new Tile[Height, Width];   // [y, x], y는 위에서 아래로

        public Tile this[int x, int y] => _tiles[y, x];

        public static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public bool IsPassable(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            switch (_tiles[y, x])
            {
                // 물(Tile.Water)은 풀숲처럼 걸어 다닐 수 있다 — 물에서는 물 타입 야생 몬스터를 만난다(WildEncounter).
                case Tile.Tree: case Tile.Wall: case Tile.CenterRoof:
                case Tile.ShopRoof: case Tile.TowerBase: case Tile.TowerTop: case Tile.SchoolRoof:
                    return false;
                default:
                    return true;
            }
        }

        static double Distance(int x, int y)
        {
            double dx = x - VillageX, dy = y - VillageY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static double DistanceFromVillage(int x, int y) => Distance(x, y);

        /// <summary>실제 충주 도로명(OpenStreetMap, <see cref="AddChungjuLandmarks"/> 의 RealRoadGrid와 같은 자료에서
        /// 뽑은 대표 좌표). 표지판(WorldView.BuildSign)과 <see cref="AreaName"/> 둘 다 이걸 같이 쓴다.</summary>
        public static readonly (int X, int Y, string Name)[] RealRoads =
        {
            (25, 24, "호암대로"), (40, 19, "남산로"), (19, 3, "중앙로"), (20, 1, "탄금대로"),
        };

        /// <summary>마을 중심 기준 지역명(연출용 지명). SPEC "배경 설정: 충주시".
        /// 웹과 100% 같은 결과를 내야 골든 테스트(WebParityTests.AreaNames_MatchWeb)가 유효하므로,
        /// 실제 도로명을 얹은 화면 표시용은 <see cref="DisplayAreaName"/> 으로 따로 뺐다.</summary>
        public static string AreaName(int x, int y)
        {
            int dx = x - VillageX, dy = y - VillageY;
            if (Math.Sqrt((double)dx * dx + (double)dy * dy) <= 7) return "충주시 시내";
            if (dx <= -12) return "탄금대·남한강변";
            if (dx >= 12) return "충주호 둘레길";
            if (dy >= 9) return "수안보 온천길";
            if (dy <= -9) return "월악산 자락";
            return "충주 외곽 들판";
        }

        /// <summary>HUD에 실제로 보여주는 지역명 — 실제 도로 표지판 근처(3칸 이내)면 그 도로명을,
        /// 아니면 <see cref="AreaName"/> 을 그대로 쓴다. Unity 전용 추가라 골든 테스트 대상이 아니다.</summary>
        public static string DisplayAreaName(int x, int y)
        {
            foreach (var (rx, ry, name) in RealRoads)
                if (Math.Abs(x - rx) + Math.Abs(y - ry) <= 3) return name;
            return AreaName(x, y);
        }

        /// <summary>web/index.html genMap()의 이식. 난수 호출 순서(단락 평가 포함)까지 같아야 지형이 일치한다.</summary>
        public static WorldMap Generate(int seed = DefaultSeed)
        {
            var map = new WorldMap();
            var t = map._tiles;
            var rng = new Mulberry32(seed);
            int R(int a, int b) => rng.Range(a, b);

            // 풀숲
            for (int n = 0; n < 70; n++)
            {
                int bx = R(2, Width - 3), by = R(2, Height - 3), br = R(2, 5);
                for (int y = by - br; y <= by + br; y++)
                    for (int x = bx - br; x <= bx + br; x++)
                    {
                        if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) continue;
                        double hyp = Math.Sqrt((double)(x - bx) * (x - bx) + (double)(y - by) * (y - by));
                        bool inside = hyp + rng.NextDouble() * 1.5 < br;   // 난수는 항상 소비
                        if (inside && Distance(x, y) > 7) t[y, x] = Tile.TallGrass;
                    }
            }

            // 호수 (lx, ly, rx, ry)
            foreach (var lake in new[] { (9, 8, 5, 4), (39, 10, 5, 5), (8, 28, 6, 4), (38, 29, 6, 4), (24, 5, 4, 2) })
            {
                var (lx, ly, rx, ry) = lake;
                for (int y = ly - ry; y <= ly + ry; y++)
                    for (int x = lx - rx; x <= lx + rx; x++)
                    {
                        if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) continue;
                        double u = (double)(x - lx) / rx, v = (double)(y - ly) / ry;
                        if (u * u + v * v <= 1 + (rng.NextDouble() - 0.5) * 0.3) t[y, x] = Tile.Water;
                    }
            }

            // 나무: 테두리 + 마을 밖 7%
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    bool edge = x == 0 || y == 0 || x == Width - 1 || y == Height - 1;
                    if (edge) t[y, x] = Tile.Tree;
                    else if (t[y, x] != Tile.Water && Distance(x, y) > 7 && rng.NextDouble() < 0.07) t[y, x] = Tile.Tree;
                }

            // 마을 정리(반경 6.5 안은 전부 잔디)
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    if (Distance(x, y) <= 6.5 && t[y, x] != Tile.Path) t[y, x] = Tile.Grass;

            // 길: 가로(VY-2) + 세로(VX)
            for (int x = VillageX - 6; x <= VillageX + 6; x++) t[VillageY - 2, x] = Tile.Path;
            for (int y = 1; y < Height - 1; y++) t[y, VillageX] = Tile.Path;

            // 건물: 왼쪽 아동센터, 오른쪽 학생과학백화점(마트)
            void Building(int x0, Tile roof, Tile door)
            {
                for (int x = x0; x < x0 + 3; x++) { t[VillageY - 4, x] = roof; t[VillageY - 3, x] = Tile.Wall; }
                t[VillageY - 3, x0 + 1] = door;
            }
            Building(VillageX - 4, Tile.CenterRoof, Tile.CenterDoor);
            Building(VillageX + 2, Tile.ShopRoof, Tile.ShopDoor);

            // 꽃
            for (int n = 0; n < 14; n++)
            {
                int x = VillageX + R(-6, 6), y = VillageY + R(-5, 6);
                if (InBounds(x, y) && t[y, x] == Tile.Grass && Distance(x, y) <= 6.5) t[y, x] = Tile.Flower;
            }

            // 충주 중앙탑(2타일 높이)
            t[VillageY + 2, VillageX + 3] = Tile.TowerBase;
            t[VillageY + 1, VillageX + 3] = Tile.TowerTop;
            return map;
        }

        /// <summary>실제 충주 호암지·남산초등학교 일대(OpenStreetMap, 2026-09 기준)를 48x36 격자에 옮긴 도로·호수 배치.
        /// <c>'#'</c>=실제 도로(2급~3급 도로, api.openstreetmap.org 로 직접 받은 way 지오메트리를 이 격자 좌표계로
        /// 투영해 결정적으로 구운 것 — 실행 중 인터넷 접속은 안 한다), <c>'~'</c>=호암지(way 468393981) 실제 모양,
        /// <c>'.'</c>=이 칸은 안 건드림(<see cref="Generate"/> 가 만든 잔디·나무 그대로 둔다).
        /// 남산초등학교(way 517410791) 위치는 실제 중심점을 이 격자로 투영한 칸(NamsanTileX/Y)에 별도로 건물을 짓는다.</summary>
        static readonly string[] RealRoadGrid =
        {
            ".###...#..........##........##..#.....#.......#.",
            "....###...........###.........#.#.....#.......#.",
            ".....###.........##..##........##.....#.......#.",
            ".....#.###.....#################.#...#........#.",
            "....#....#...###.#...........######..#........##",
            "....#.....###....#............#..#####........##",
            "....#....###.....#............#.....###.......##",
            "....#..###.......#............#.....#.##......##",
            "....###..........#........#############.########",
            "...###...........#........#...#.....#.#####....#",
            "###..............#........#...#.....#....#.....#",
            "..................#.......#...#.....#....#.....#",
            "................~.#......#....#......#...#.....#",
            "...............~~.#......#....##.....#...##....#",
            "..............~~~.#.....#.....##.....#....#....#",
            "............~~~....#....#....##......#....#....#",
            "............~~~..~~#...#.....#.......#....#....#",
            "...........~~~~~~~~#..##.....#.......#....#....#",
            "..............~~~~~##.#.....###.......#..#######",
            "..............~~~~..##....###.############.#...#",
            "..............~.~~~~.#####............#....#...#",
            ".................~~.###...............#....#...#",
            ".~.................##.##..............#....#..##",
            ".................##....#...............#...####.",
            "................##.....###.################.##..",
            "####...........##........###...#..........###...",
            "...#####......##...............#.........###....",
            ".....~~####...#................#.........##.....",
            ".....~~~~~.###.................#........##......",
            ".....~~~~...###................#.......##.......",
            ".......~....#.####.............#.......#........",
            "..........###....####.###################.......",
            "........##...##.....#######.........##...#......",
            ".......##.....##....................#.....#.....",
            "......##.......###..................#......##...",
            "......#...........#................#........#...",
        };

        public const int HoamSignTileX = 16, HoamSignTileY = 11;   // 호수 북쪽 가장자리 바로 위, 물 밖 잔디 칸
        public const int NamsanTileX = 34, NamsanTileY = 20;

        /// <summary>웹에는 없는 Unity 전용 추가(실제 충주 지리 반영). <see cref="Generate"/> 는 웹과 100% 같은 지형만
        /// 만들어야 골든 테스트(WebParityTests)가 계속 유효하므로, 이 추가는 별도 단계로 분리해 호출하는 쪽(맵을 실제로
        /// 쓰는 GameBootstrap/DedicatedServerBootstrap)에서 <c>Generate()</c> 직후에 이어서 부른다.</summary>
        public static void AddChungjuLandmarks(WorldMap map)
        {
            var t = map._tiles;

            for (int y = 0; y < RealRoadGrid.Length && y < Height; y++)
            {
                string row = RealRoadGrid[y];
                for (int x = 0; x < row.Length && x < Width; x++)
                {
                    char c = row[x];
                    if (c == '#') t[y, x] = Tile.Path;
                    else if (c == '~') t[y, x] = Tile.Water;
                    // '.' 는 Generate() 가 만든 걸 그대로 둔다.
                }
            }

            // 남산초등학교 — 실제 위치(거룡2길 31, 성남동)를 이 격자로 투영한 자리.
            int sx0 = NamsanTileX - 1, roofY = NamsanTileY - 1, wallY = NamsanTileY;
            for (int x = sx0; x < sx0 + 3; x++) { t[roofY, x] = Tile.SchoolRoof; t[wallY, x] = Tile.Wall; }
            t[wallY, sx0 + 1] = Tile.SchoolDoor;
        }
    }
}
