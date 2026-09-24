using MonsterAdventure.Core;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MonsterAdventure
{
    /// <summary>WorldMap 을 Tilemap 으로 그린다. 맵 좌표(y가 아래로 증가)와 유니티 월드 좌표(y가 위로 증가)의 변환도 여기서 한다.</summary>
    public sealed class WorldView : MonoBehaviour
    {
        public const int PlayerSortingOrder = 10;
        const int SignSortingOrder = 5;

        Tilemap _tilemap;

        /// <summary>타일 (x, y) 의 중심 월드 좌표.</summary>
        public static Vector3 TileCenter(int x, int y) => new Vector3(x + 0.5f, WorldMap.Height - 1 - y + 0.5f, 0f);

        public void Build(WorldMap map)
        {
            var gridGo = new GameObject("Grid", typeof(Grid));
            gridGo.transform.SetParent(transform, false);
            var tmGo = new GameObject("Tilemap", typeof(Tilemap), typeof(TilemapRenderer));
            tmGo.transform.SetParent(gridGo.transform, false);
            _tilemap = tmGo.GetComponent<Tilemap>();
            var renderer = tmGo.GetComponent<TilemapRenderer>();
            renderer.sortingOrder = 0;
            renderer.sharedMaterial = DefaultSpriteMaterial();

            var cells = new Vector3Int[WorldMap.Width * WorldMap.Height];
            var tiles = new TileBase[cells.Length];
            int i = 0;
            for (int y = 0; y < WorldMap.Height; y++)
                for (int x = 0; x < WorldMap.Width; x++, i++)
                {
                    cells[i] = new Vector3Int(x, WorldMap.Height - 1 - y, 0);
                    tiles[i] = TileArt.Get(map[x, y], x, y);
                }
            _tilemap.SetTiles(cells, tiles);

            BuildSign(WorldMap.VillageX - 4, WorldMap.VillageY - 4, new[] { "네잎클로버", "지역아동센터" }, "#2f8a48");
            BuildSign(WorldMap.VillageX + 2, WorldMap.VillageY - 4, new[] { "학생과학백화점" }, "#c2412e");
            BuildSign(WorldMap.NamsanTileX - 1, WorldMap.NamsanTileY - 1, new[] { "남산초등학교" }, "#2f4a7a");
            BuildSign(WorldMap.HoamSignTileX - 1, WorldMap.HoamSignTileY, new[] { "호암지" }, "#1e5aa8");   // 연못가 표지판(지붕 없이 풀밭 위에 세운다)

            // 실제 충주 도로명 표지판(한국 도로 표지판 느낌의 청색). 건물이 아니라 길바닥 옆이라 살짝 옆으로 띄운다.
            foreach (var (rx, ry, name) in WorldMap.RealRoads)
                BuildSign(rx - 1, ry - 1, new[] { name }, "#2d5f8a");
        }

        static Material _defaultSpriteMaterial;

        /// <summary>SpriteRenderer 의 기본 재질(Sprites/Default)을 그대로 빌려 쓴다. 빌드에서도 셰이더가 포함된다.</summary>
        public static Material DefaultSpriteMaterial()
        {
            if (_defaultSpriteMaterial != null) return _defaultSpriteMaterial;
            var probe = new GameObject("probe", typeof(SpriteRenderer));
            _defaultSpriteMaterial = probe.GetComponent<SpriteRenderer>().sharedMaterial;
            Destroy(probe);
            return _defaultSpriteMaterial;
        }

        /// <summary>웹의 건물 간판: 지붕 첫 줄 위에 3칸 너비의 색 판 + 글자.</summary>
        void BuildSign(int tileX, int tileY, string[] lines, string colorHex)
        {
            const int px = 12;
            var p = new Painter(TileArt.Size * 3 - 4, lines.Length * px + 6);
            p.Rect(0, 0, p.Width, p.Height, Painter.Hex(colorHex));
            p.Rect(0, 0, p.Width, 1, Painter.Hex("#ffffff")); p.Rect(0, p.Height - 1, p.Width, 1, Painter.Hex("#ffffff"));
            p.Rect(0, 0, 1, p.Height, Painter.Hex("#ffffff")); p.Rect(p.Width - 1, 0, 1, p.Height, Painter.Hex("#ffffff"));

            var go = new GameObject("Sign " + string.Join(" ", lines), typeof(SpriteRenderer));
            go.transform.SetParent(transform, false);
            var sr = go.GetComponent<SpriteRenderer>();
            sr.sprite = p.ToSprite(TileArt.PixelsPerUnit, new Vector2(0f, 1f));      // 왼쪽 위 기준
            sr.sortingOrder = SignSortingOrder;
            // 판의 왼쪽 위 = 타일 (x, y) 왼쪽 위에서 (2, 4) 픽셀 안쪽
            var topLeft = TileCenter(tileX, tileY) + new Vector3(-0.5f + 2f / 32f, 0.5f - 4f / 32f, 0);
            go.transform.position = topLeft;

            for (int i = 0; i < lines.Length; i++)
            {
                var tgo = new GameObject("Label", typeof(TextMesh));     // MeshRenderer 는 TextMesh 가 자동으로 붙인다
                tgo.transform.SetParent(go.transform, false);
                var tm = tgo.GetComponent<TextMesh>();
                tm.font = UiKit.Font;
                tm.text = lines[i];
                tm.fontSize = 64;
                tm.fontStyle = FontStyle.Bold;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.characterSize = 0.044f;      // 측정값: 0.0125 → 글자 높이 0.09유닛. 웹의 10px(0.31유닛)에 맞춘 값
                tm.color = Color.white;
                var mr = tgo.GetComponent<MeshRenderer>();
                mr.sharedMaterial = UiKit.Font.material;
                mr.sortingOrder = SignSortingOrder + 1;
                tgo.transform.localPosition = new Vector3(p.Width / 2f / 32f, -(3f + i * px + px / 2f) / 32f, 0);
            }
        }
    }
}
