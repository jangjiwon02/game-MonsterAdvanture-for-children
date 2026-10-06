using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;
using UnityEngine.Tilemaps;
using Tile = MonsterAdventure.Core.Tile;   // UnityEngine.Tilemaps.Tile 과 구분

namespace MonsterAdventure
{
    /// <summary>web/index.html 의 drawTile 을 옮긴 타일 그림. 바이너리 에셋 없이 실행 시 코드로 생성한다.</summary>
    public static class TileArt
    {
        public const int Size = 32;                 // 웹의 T
        public const float PixelsPerUnit = 32f;     // 타일 1칸 = 1 유닛

        static readonly Dictionary<(Tile, int), UnityEngine.Tilemaps.Tile> Cache = new Dictionary<(Tile, int), UnityEngine.Tilemaps.Tile>();
        static Sprite _towerBase, _towerTop;

        /// <summary>웹의 tileHash(x, y). 잔디 무늬·사과나무·꽃 색을 칸마다 다르게 고르는 데 쓴다.</summary>
        public static double TileHash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263;
                h = (h ^ (int)((uint)h >> 13)) * 1274126177;
                return (uint)(h ^ (int)((uint)h >> 16)) / 4294967295.0;
            }
        }

        public static UnityEngine.Tilemaps.Tile Get(Tile tile, int x, int y)
        {
            double h = TileHash(x, y);
            // 그림이 달라지는 경우만 변형(variant)으로 캐시한다.
            int variant = tile switch
            {
                Tile.TallGrass => GrassKey(h),
                Tile.Tree => GrassKey(h) * 2 + (h > .55 ? 1 : 0),
                Tile.Path => (int)(h * 20) + 100 * (int)(h * 31 % 26) + 10000 * (int)(h * 13 % 26),
                Tile.Flower => GrassKey(h) * 4 + (int)(h * 4),
                Tile.Wall or Tile.CenterDoor or Tile.ShopDoor or Tile.CenterRoof or Tile.ShopRoof or Tile.Water
                    or Tile.SchoolRoof or Tile.SchoolDoor or Tile.AptRoof or Tile.AptWall => 0,
                _ => GrassKey(h),   // 잔디
            };
            var key = (tile, variant);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var t = ScriptableObject.CreateInstance<UnityEngine.Tilemaps.Tile>();
            t.hideFlags = HideFlags.HideAndDontSave;
            t.sprite = Draw(tile, h);
            t.colliderType = UnityEngine.Tilemaps.Tile.ColliderType.None;
            Cache[key] = t;
            return t;
        }

        static Sprite Draw(Tile tile, double h)
        {
            if (tile == Tile.TowerBase || tile == Tile.TowerTop)
            {
                EnsureTower();
                return tile == Tile.TowerBase ? _towerBase : _towerTop;
            }
            var p = new Painter(Size, Size);
            DrawTile(p, tile, h);
            return p.ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.5f));
        }

        static Color32 C(string hex, float a = 1f) => Painter.Hex(hex, a);

        // 잔디: 두 가지 색조 + (h>0.7 이면) 풀잎 한 줄. 풀잎 위치는 4칸 격자로 양자화해 텍스처 수를 줄인다.
        static int GrassKey(double h)
        {
            int shade = h < .5 ? 0 : 1;
            if (h <= .7) return shade;
            int tx = (int)(h * 97 % 24) / 6, ty = (int)(h * 53 % 24) / 6;   // 0..3
            return 2 + shade + 2 * (tx + 4 * ty);
        }

        static void Grass(Painter p, double h)
        {
            p.Rect(0, 0, Size, Size, h < .5 ? C("#6cc04a") : C("#66b944"));
            if (h > .7) p.Rect((int)(h * 97 % 24) / 6 * 6 + 2, (int)(h * 53 % 24) / 6 * 6 + 4, 2, 4, C("#7dd35a"));
        }

        static void DrawTile(Painter p, Tile t, double h)
        {
            switch (t)
            {
                case Tile.Water:
                    p.Rect(0, 0, Size, Size, C("#3f7fe0"));
                    p.Rect(4, 8, 10, 2, C("#6aa2f2"));
                    p.Rect(16, 20, 10, 2, C("#6aa2f2"));
                    return;
                case Tile.Path:
                    p.Rect(0, 0, Size, Size, C("#dcbc7c"));
                    p.Rect((int)(h * 20), (int)(h * 31 % 26), 3, 3, C("#c8a466"));
                    p.Rect(18, (int)(h * 13 % 26) + 2, 2, 2, C("#c8a466"));
                    return;
            }

            Grass(p, h);
            switch (t)
            {
                case Tile.TallGrass:
                    p.Rect(0, 0, Size, Size, C("#3f9a36"));
                    for (int i = 0; i < 4; i++)
                    {
                        float bx = 4 + i * 7, by = 26;
                        p.Polygon(i % 2 == 1 ? C("#7fd35a") : C("#2c7a2a"), null,
                            bx - 4, by, bx, by - 18 - (i % 2) * 4, bx + 4, by);
                    }
                    break;
                case Tile.Tree:
                    p.Rect(12, 20, 8, 12, C("#6a4a2a"));
                    p.Ellipse(16, 14, 14, 13, C("#2f7a34"), C("#000000", .25f));
                    p.Ellipse(12, 10, 6, 5, C("#4a9a4a"));
                    if (h > .55)
                        foreach (var (ax, ay) in new[] { (9, 12), (19, 8), (22, 17), (14, 18) })
                            p.Ellipse(ax, ay, 2.2f, 2.2f, C("#e63a2e"));
                    break;
                case Tile.Wall: case Tile.CenterDoor: case Tile.ShopDoor: case Tile.SchoolDoor:
                    Building(p, t);
                    break;
                case Tile.CenterRoof: case Tile.ShopRoof: case Tile.SchoolRoof:
                    p.Rect(0, 0, Size, Size, t == Tile.CenterRoof ? C("#3fa858") : t == Tile.ShopRoof ? C("#e0523e") : C("#3f6ea8"));
                    for (int i = 0; i < 4; i++) p.Rect(0, i * 8 + 6, Size, 2, C("#000000", .15f));
                    break;
                case Tile.AptRoof:
                    // 아파트 옥상: 회색 지붕 + 짙은 가장자리
                    p.Rect(0, 0, Size, Size, C("#8e96a8"));
                    p.Rect(0, 0, Size, 3, C("#5f6678"));
                    p.Rect(0, Size - 3, Size, 3, C("#5f6678"));
                    for (int i = 0; i < 3; i++) p.Rect(0, i * 9 + 7, Size, 1, C("#000000", .12f));
                    break;
                case Tile.AptWall:
                    // 아파트 외벽: 연한 베이지 바탕에 창문 두 줄, 아래쪽엔 어두운 띠
                    p.Rect(0, 0, Size, Size, C("#e4dccb"));
                    foreach (var wx in new[] { 5, 18 })
                        foreach (var wy in new[] { 4, 17 })
                        {
                            p.Rect(wx, wy, 9, 9, C("#6fa9d8"));
                            p.Rect(wx + 4, wy, 1, 9, C("#ffffff"));
                            p.Rect(wx, wy + 4, 9, 1, C("#ffffff"));
                        }
                    p.Rect(0, Size - 2, Size, 2, C("#000000", .12f));
                    break;
                case Tile.Flower:
                    var col = new[] { "#ff6f91", "#ffd84a", "#ffffff", "#b58cff" }[(int)(h * 4)];
                    foreach (var (dx, dy) in new[] { (8, 10), (20, 8), (14, 22) })
                    {
                        p.Ellipse(dx, dy, 3, 3, C(col));
                        p.Ellipse(dx, dy, 1, 1, C("#ffaa00"));
                    }
                    break;
            }
        }

        static void Building(Painter p, Tile t)
        {
            p.Rect(0, 0, Size, Size, C("#efe4c8"));
            p.Rect(0, Size - 4, Size, 4, C("#000000", .08f));
            var white = C("#ffffff");
            if (t == Tile.Wall)
            {
                p.Rect(8, 8, 16, 12, C("#7ab8e8"));
                p.Rect(8, 13, 16, 2, white);
                p.Rect(15, 8, 2, 12, white);
                return;
            }
            p.Rect(6, 4, 20, 28, t == Tile.CenterDoor ? C("#2f8a48") : t == Tile.SchoolDoor ? C("#2f4a7a") : C("#4a78d8"));
            if (t == Tile.CenterDoor)
            {
                // 네잎클로버: 잎 4장 + 줄기
                foreach (var (dx, dy) in new[] { (-4.5f, 0f), (4.5f, 0f), (0f, -4.5f), (0f, 4.5f) })
                    p.Ellipse(16 + dx, 16 + dy, 4.2f, 4.2f, white);
                p.Line(new Vector2(16, 21), new Vector2(20, 27), 2f, white);
            }
            else if (t == Tile.SchoolDoor)
            {
                // 펼친 책 모양(표지 두 장 + 가운데 줄)
                p.Rect(9, 12, 14, 10, white);
                p.Line(new Vector2(16, 12), new Vector2(16, 22), 1.5f, C("#2f4a7a"));
                p.Rect(9, 12, 14, 2, C("#2f4a7a", .5f));
            }
            else
            {
                // '$' 표시(획 근사)
                p.Rect(12, 9, 8, 2, white); p.Rect(12, 9, 2, 6, white); p.Rect(12, 15, 8, 2, white);
                p.Rect(18, 15, 2, 6, white); p.Rect(12, 21, 8, 2, white); p.Rect(15, 6, 2, 19, white);
            }
        }

        /// <summary>충주 탑평리 칠층석탑(중앙탑) — 2타일 높이. 32x64 에 한 번에 그려 위/아래로 자른다.</summary>
        static void EnsureTower()
        {
            if (_towerBase != null) return;
            var p = new Painter(Size, Size * 2);
            float baseY = Size * 2;
            p.Ellipse(16, baseY - 1, 15, 4, C("#000000", .2f));
            p.Rect(3, baseY - 8, 26, 8, C("#9a968a"));
            float cy = baseY - 8;
            for (int i = 0; i < 7; i++)
            {
                float w = 24 - i * 2.4f, bh = 5.5f;
                p.Rect(16 - w / 2, cy - bh, w, bh, C("#cfcabc"));
                p.Rect(16 - w / 2 - 2, cy - bh - 2, w + 4, 2.5f, C("#7d786c"));
                cy -= bh + 2.5f;
            }
            p.Rect(15, cy - 6, 2, 6, C("#7d786c"));

            // 탑 뒤가 투명하지 않도록 잔디 위에 얹은 뒤 위/아래 타일로 자른다.
            var withGrass = new Painter(Size, Size * 2);
            for (int ty = 0; ty < 2; ty++)
            {
                var g = new Painter(Size, Size);
                Grass(g, 0.3);
                withGrass.Blit(g, 0, ty * Size);
            }
            withGrass.Blit(p, 0, 0);
            var tex = withGrass.ToTexture();
            _towerTop = Painter.Slice(tex, 0, 0, Size, Size, PixelsPerUnit, new Vector2(0.5f, 0.5f));
            _towerBase = Painter.Slice(tex, 0, Size, Size, Size, PixelsPerUnit, new Vector2(0.5f, 0.5f));
        }
    }
}
