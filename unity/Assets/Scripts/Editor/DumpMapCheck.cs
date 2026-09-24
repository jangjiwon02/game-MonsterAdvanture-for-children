using MonsterAdventure.Core;
using UnityEngine;
using System.IO;

namespace MonsterAdventure.EditorTools
{
    public static class DumpMapCheck
    {
        public static void Run()
        {
            var map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(map);
            var sb = new System.Text.StringBuilder();
            for (int y = 0; y < WorldMap.Height; y++)
            {
                for (int x = 0; x < WorldMap.Width; x++)
                    sb.Append((int)map[x, y]);
                sb.Append('\n');
            }
            File.WriteAllText(Path.Combine(Application.dataPath, "..", "map_dump.txt"), sb.ToString());
        }
    }
}
