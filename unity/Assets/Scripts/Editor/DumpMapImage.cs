using MonsterAdventure.Core;
using UnityEngine;
using System.IO;

namespace MonsterAdventure.EditorTools
{
    /// <summary>타일 그림만으로 지도 일부를 PNG 로 뽑아 눈으로 확인하는 도구(표지판 글자는 안 나온다).
    /// <c>-executeMethod MonsterAdventure.EditorTools.DumpMapImage.Run</c> → 프로젝트 루트에 map_dump.png</summary>
    public static class DumpMapImage
    {
        public static void Run()
        {
            var map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(map);
            int x0 = 20, x1 = WorldMap.Width, y0 = 0, y1 = 26, S = 32;
            var tex = new Texture2D((x1 - x0) * S, (y1 - y0) * S, TextureFormat.RGBA32, false);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var sprite = TileArt.Get(map[x, y], x, y).sprite;
                    var src = sprite.texture;
                    var r = sprite.textureRect;
                    var px = src.GetPixels((int)r.x, (int)r.y, S, S);
                    tex.SetPixels((x - x0) * S, (y1 - 1 - y) * S, S, S, px);
                }
            tex.Apply();
            File.WriteAllBytes(Path.Combine(Application.dataPath, "..", "map_dump.png"), tex.EncodeToPNG());
        }
    }
}
