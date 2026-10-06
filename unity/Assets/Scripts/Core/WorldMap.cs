using System;

namespace MonsterAdventure.Core
{
    /// <summary>타일 값은 웹 버전과 같다(SPEC "월드"). 11/12는 중앙탑(아래/위) 장식.
    /// 13~16(SchoolRoof/SchoolDoor/AptRoof/AptWall)은 웹에 없는 Unity 전용 추가 — <see cref="WorldMap.AddChungjuLandmarks"/> 참고.</summary>
    public enum Tile : byte
    {
        Grass = 0, TallGrass = 1, Tree = 2, Water = 3, Path = 4, Wall = 5,
        CenterDoor = 6, ShopDoor = 7, Flower = 8, CenterRoof = 9, ShopRoof = 10,
        TowerBase = 11, TowerTop = 12, SchoolRoof = 13, SchoolDoor = 14,
        AptRoof = 15, AptWall = 16,   // 연수주공아파트 — 장식용(들어갈 수 없다)
    }

    /// <summary>충주 시내 맵. 웹과 같은 48x36(시드 20240921)이 남쪽 부분이고, 그 위(북쪽)로 연수동까지
    /// <see cref="NorthExtension"/> 행을 더 이은 48x68 이다. 웹 지형은 <see cref="Generate"/> 가 그대로 만들어
    /// 북쪽 확장 구역 아래에 붙이므로, 웹 좌표 (x, y) 는 이 맵에서 (x, y + NorthExtension) 이다.</summary>
    public sealed class WorldMap
    {
        /// <summary>웹 genMap 이 만드는 지형 크기(골든 테스트 대상).</summary>
        public const int GenWidth = 48, GenHeight = 36, GenVillageY = 18;
        /// <summary>웹 지형 위에 덧붙인 북쪽 행 수 — 충주시청·연수동까지(OpenStreetMap 기준 약 2.3km).</summary>
        public const int NorthExtension = 32;
        /// <summary>좌표계 버전. 1 = 북쪽 확장 이후(예전 저장의 y 는 NorthExtension 만큼 내려 줘야 한다).</summary>
        public const int Revision = 1;

        public const int Width = GenWidth, Height = GenHeight + NorthExtension;
        public const int VillageX = 24, VillageY = GenVillageY + NorthExtension;
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
                case Tile.AptRoof: case Tile.AptWall:
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

        /// <summary>Generate 안(웹 좌표계)에서 쓰는 마을 거리.</summary>
        static double GenDistance(int x, int y)
        {
            double dx = x - VillageX, dy = y - GenVillageY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static double DistanceFromVillage(int x, int y) => Distance(x, y);

        /// <summary>실제 충주 도로명(OpenStreetMap, <see cref="AddChungjuLandmarks"/> 의 RealRoadGrid와 같은 자료에서
        /// 뽑은 대표 좌표). 표지판(WorldView.BuildSign)과 <see cref="AreaName"/> 둘 다 이걸 같이 쓴다.</summary>
        public static readonly (int X, int Y, string Name)[] RealRoads =
        {
            (25, 24 + NorthExtension, "호암대로"), (40, 19 + NorthExtension, "남산로"), (19, 3 + NorthExtension, "중앙로"), (20, 1 + NorthExtension, "탄금대로"),
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
            if (InYeonsu(x, y)) return "연수동";
            foreach (var (rx, ry, name) in RealRoads)
                if (Math.Abs(x - rx) + Math.Abs(y - ry) <= 3) return name;
            return AreaName(x, y);
        }

        /// <summary>연수동 일대(연수주공아파트 단지들이 있는 곳)인지.</summary>
        public static bool InYeonsu(int x, int y) => x >= 26 && x <= 42 && y >= 4 && y <= 20;

        /// <summary>web/index.html genMap()의 이식. 난수 호출 순서(단락 평가 포함)까지 같아야 지형이 일치한다.
        /// 웹 좌표계(48x36)로 만든 뒤 북쪽 확장 구역 아래(y + NorthExtension)에 붙인다 — 북쪽 구역은 비어 있고(잔디)
        /// <see cref="AddChungjuLandmarks"/> 가 채운다.</summary>
        public static WorldMap Generate(int seed = DefaultSeed)
        {
            var map = new WorldMap();
            var t = new Tile[GenHeight, GenWidth];
            var rng = new Mulberry32(seed);
            int R(int a, int b) => rng.Range(a, b);
            const int VY = GenVillageY;

            // 풀숲
            for (int n = 0; n < 70; n++)
            {
                int bx = R(2, GenWidth - 3), by = R(2, GenHeight - 3), br = R(2, 5);
                for (int y = by - br; y <= by + br; y++)
                    for (int x = bx - br; x <= bx + br; x++)
                    {
                        if (x < 1 || y < 1 || x >= GenWidth - 1 || y >= GenHeight - 1) continue;
                        double hyp = Math.Sqrt((double)(x - bx) * (x - bx) + (double)(y - by) * (y - by));
                        bool inside = hyp + rng.NextDouble() * 1.5 < br;   // 난수는 항상 소비
                        if (inside && GenDistance(x, y) > 7) t[y, x] = Tile.TallGrass;
                    }
            }

            // 호수 (lx, ly, rx, ry)
            foreach (var lake in new[] { (9, 8, 5, 4), (39, 10, 5, 5), (8, 28, 6, 4), (38, 29, 6, 4), (24, 5, 4, 2) })
            {
                var (lx, ly, rx, ry) = lake;
                for (int y = ly - ry; y <= ly + ry; y++)
                    for (int x = lx - rx; x <= lx + rx; x++)
                    {
                        if (x < 1 || y < 1 || x >= GenWidth - 1 || y >= GenHeight - 1) continue;
                        double u = (double)(x - lx) / rx, v = (double)(y - ly) / ry;
                        if (u * u + v * v <= 1 + (rng.NextDouble() - 0.5) * 0.3) t[y, x] = Tile.Water;
                    }
            }

            // 나무: 테두리 + 마을 밖 7%
            for (int y = 0; y < GenHeight; y++)
                for (int x = 0; x < GenWidth; x++)
                {
                    bool edge = x == 0 || y == 0 || x == GenWidth - 1 || y == GenHeight - 1;
                    if (edge) t[y, x] = Tile.Tree;
                    else if (t[y, x] != Tile.Water && GenDistance(x, y) > 7 && rng.NextDouble() < 0.07) t[y, x] = Tile.Tree;
                }

            // 마을 정리(반경 6.5 안은 전부 잔디)
            for (int y = 0; y < GenHeight; y++)
                for (int x = 0; x < GenWidth; x++)
                    if (GenDistance(x, y) <= 6.5 && t[y, x] != Tile.Path) t[y, x] = Tile.Grass;

            // 길: 가로(VY-2) + 세로(VX)
            for (int x = VillageX - 6; x <= VillageX + 6; x++) t[VY - 2, x] = Tile.Path;
            for (int y = 1; y < GenHeight - 1; y++) t[y, VillageX] = Tile.Path;

            // 건물: 왼쪽 아동센터, 오른쪽 학생과학백화점(마트)
            void Building(int x0, Tile roof, Tile door)
            {
                for (int x = x0; x < x0 + 3; x++) { t[VY - 4, x] = roof; t[VY - 3, x] = Tile.Wall; }
                t[VY - 3, x0 + 1] = door;
            }
            Building(VillageX - 4, Tile.CenterRoof, Tile.CenterDoor);
            Building(VillageX + 2, Tile.ShopRoof, Tile.ShopDoor);

            // 꽃
            for (int n = 0; n < 14; n++)
            {
                int x = VillageX + R(-6, 6), y = VY + R(-5, 6);
                if (x >= 0 && y >= 0 && x < GenWidth && y < GenHeight && t[y, x] == Tile.Grass && GenDistance(x, y) <= 6.5) t[y, x] = Tile.Flower;
            }

            // 충주 중앙탑(2타일 높이)
            t[VY + 2, VillageX + 3] = Tile.TowerBase;
            t[VY + 1, VillageX + 3] = Tile.TowerTop;

            for (int y = 0; y < GenHeight; y++)
                for (int x = 0; x < GenWidth; x++)
                    map._tiles[y + NorthExtension, x] = t[y, x];
            return map;
        }

        /// <summary>실제 충주 호암지·남산초등학교 일대(OpenStreetMap, 2026-09 기준)를 웹 지형(48x36) 위에 옮긴 도로·호수 배치.
        /// <c>'#'</c>=실제 도로(2급~3급 도로, api.openstreetmap.org 로 직접 받은 way 지오메트리를 이 격자 좌표계로
        /// 투영해 결정적으로 구운 것 — 실행 중 인터넷 접속은 안 한다), <c>'~'</c>=호암지(way 468393981) 실제 모양,
        /// <c>'.'</c>=이 칸은 안 건드림(<see cref="Generate"/> 가 만든 잔디·나무 그대로 둔다).
        /// 남산초등학교(way 517410791) 위치는 실제 중심점을 이 격자로 투영한 칸(NamsanTileX/Y)에 별도로 건물을 짓는다.
        /// 이 격자의 첫 줄은 웹 지형의 첫 줄(맵의 y = NorthExtension)에 대응한다.</summary>
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

        /// <summary>연수동 쪽 북쪽 확장 구역(맵의 y = 0 ~ NorthExtension-1)의 실제 간선도로(OpenStreetMap, 같은 투영으로 구운 것).
        /// 마지막 줄은 RealRoadGrid 첫 줄 바로 위 줄이다.</summary>
        static readonly string[] NorthRoadGrid =
        {
            ".........#............#.........................",
            ".......####...........##........................",
            ".......#.##............#........................",
            ".........#.............#........................",
            ".........#.............#........................",
            ".........#.............#........................",
            ".........#####################################..",
            ".........#.....#.......#.......#.....#.......#..",
            ".........#.....#.......#.......#.....#.......#..",
            ".........#.....#.......#.......#.....#.......#..",
            ".........#.....#.......#.......#.....#.......#..",
            ".........#.....#.......###....##..#..#.......#..",
            ".........#.....#.......#.##...####...#.......#..",
            "..........#....#.......#...#.###.....#.......#..",
            ".........##....#.......##...#.#......#.......#..",
            "################################################",
            ".........#.....#.......#......#......#.......#..",
            "........#......#.......#......#......#.......#..",
            ".......#.......#.......#......#......#.......#..",
            ".......#.......#.......#......#......#.......#..",
            "......#........#.......#......##......#......#..",
            "#....###.......#.......##......#......#.#######.",
            "###..#..################################.....##.",
            "..###..........#.......#.......#......#......#..",
            "...###.........#.......#.......#......#.......#.",
            "...#..##.....#####.....#........#.....#.......#.",
            "~.#.....##.###...###...#........#.....#.......#.",
            "..#......##.........####........#.....#.......#.",
            ".##......####..........####.....###...#.......#.",
            ".#.......#...#.......##...#######..############.",
            "##...~..#.....##.....####.......#.....#.......#.",
            "##.....##.......##.##...#####...#.....#.......#.",
        };

        public const int HoamSignTileX = 16, HoamSignTileY = 11 + NorthExtension;   // 호수 북쪽 가장자리 바로 위, 물 밖 잔디 칸
        public const int NamsanTileX = 34, NamsanTileY = 20 + NorthExtension;

        /// <summary>연수주공아파트 단지들(실제 위치를 OpenStreetMap 에서 같은 투영으로 옮기고, 도로를 안 덮도록 가까운 빈 칸으로 맞춘
        /// 3x3 블록의 왼쪽 위 칸). 장식일 뿐 들어갈 수 없다.</summary>
        public static readonly (int X, int Y, string Line1, string Line2)[] YeonsuApartments =
        {
            (31, 16, "연수주공", "1단지"), (34, 12, "연수주공", "2단지"), (34, 8, "연수주공", "3단지"),
            (28, 7, "연수주공", "4·5단지"), (38, 12, "연수주공", "6단지"), (38, 7, "연수주공", "7단지"),
        };

        /// <summary>연수동 표지판 자리(풀밭 위, 왼쪽 위 칸).</summary>
        public const int YeonsuSignTileX = 24, YeonsuSignTileY = 13;

        /// <summary>웹에는 없는 Unity 전용 추가(실제 충주 지리 반영). <see cref="Generate"/> 는 웹과 100% 같은 지형만
        /// 만들어야 골든 테스트(WebParityTests)가 계속 유효하므로, 이 추가는 별도 단계로 분리해 호출하는 쪽(맵을 실제로
        /// 쓰는 GameBootstrap/DedicatedServerBootstrap)에서 <c>Generate()</c> 직후에 이어서 부른다.</summary>
        public static void AddChungjuLandmarks(WorldMap map)
        {
            var t = map._tiles;
            int N = NorthExtension;

            // 1) 북쪽 확장 구역: 웹 지형과 같은 성격(풀숲 덩어리 + 7% 나무 + 테두리 나무). 웹 난수와 섞이지 않게 따로 굴린다.
            var rng = new Mulberry32(DefaultSeed + 1);
            for (int n = 0; n < 34; n++)
            {
                int bx = rng.Range(2, Width - 3), by = rng.Range(2, N - 3), br = rng.Range(2, 5);
                for (int y = by - br; y <= by + br; y++)
                    for (int x = bx - br; x <= bx + br; x++)
                    {
                        if (x < 1 || y < 1 || x >= Width - 1 || y >= N) continue;
                        double hyp = Math.Sqrt((double)(x - bx) * (x - bx) + (double)(y - by) * (y - by));
                        if (hyp + rng.NextDouble() * 1.5 < br) t[y, x] = Tile.TallGrass;
                    }
            }
            for (int y = 0; y < N; y++)
                for (int x = 0; x < Width; x++)
                {
                    bool edge = x == 0 || y == 0 || x == Width - 1;
                    if (edge) t[y, x] = Tile.Tree;
                    else if (rng.NextDouble() < 0.07) t[y, x] = Tile.Tree;
                }
            // 웹 지형의 맨 윗줄은 원래 맵 테두리(나무)였는데 이제 안쪽이다 — 좌우 테두리만 남기고 터서 북쪽으로 이어 준다.
            for (int x = 1; x < Width - 1; x++) if (t[N, x] == Tile.Tree) t[N, x] = Tile.Grass;

            // 2) 실제 도로·호수.
            void Overlay(string[] grid, int y0)
            {
                for (int gy = 0; gy < grid.Length && y0 + gy < Height; gy++)
                {
                    string row = grid[gy];
                    for (int x = 0; x < row.Length && x < Width; x++)
                    {
                        char c = row[x];
                        if (c == '#') t[y0 + gy, x] = Tile.Path;
                        else if (c == '~') t[y0 + gy, x] = Tile.Water;
                        // '.' 는 그대로 둔다.
                    }
                }
            }
            Overlay(NorthRoadGrid, 0);
            Overlay(RealRoadGrid, N);

            // 3) 남산초등학교 — 실제 위치(거룡2길 31, 성남동)를 이 격자로 투영한 자리.
            int sx0 = NamsanTileX - 1, roofY = NamsanTileY - 1, wallY = NamsanTileY;
            for (int x = sx0; x < sx0 + 3; x++) { t[roofY, x] = Tile.SchoolRoof; t[wallY, x] = Tile.Wall; }
            t[wallY, sx0 + 1] = Tile.SchoolDoor;

            // 4) 연수주공아파트 — 3x3 블록(맨 윗줄 지붕, 아래 두 줄 벽). 표지판은 WorldView 가 지붕 위에 붙인다.
            foreach (var (ax, ay, _, _) in YeonsuApartments)
                for (int y = ay; y < ay + 3; y++)
                    for (int x = ax; x < ax + 3; x++)
                        t[y, x] = y == ay ? Tile.AptRoof : Tile.AptWall;
        }
    }
}
