using System;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;

namespace MonsterAdventure.Net
{
    /// <summary>서버에 저장되는 트레이너 계정(이름으로 구분). SaveSerializer 를 그대로 재사용해 단일 플레이 저장과 같은 포맷을 쓴다.</summary>
    public sealed class TrainerAccountStore
    {
        readonly string _root;

        public TrainerAccountStore(string rootDir)
        {
            _root = rootDir;
            Directory.CreateDirectory(_root);
        }

        /// <summary>파일 이름으로 쓸 수 있게 다듬는다. 한글 등 문자는 유지하고(IsLetterOrDigit 은 한글도 true), 공백·특수문자만 제거한다.</summary>
        static string Sanitize(string name)
        {
            var cleaned = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray());
            return (cleaned.Length == 0 ? "trainer" : cleaned).ToLowerInvariant();
        }

        string PathFor(string name) => Path.Combine(_root, Sanitize(name) + ".json");

        public bool Exists(string name) => File.Exists(PathFor(name));

        /// <summary>손상됐거나 없으면 null.</summary>
        public PlayerState Load(GameData data, string name)
        {
            try
            {
                if (!Exists(name)) return null;
                return SaveSerializer.FromJson(data, File.ReadAllText(PathFor(name)));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>임시 파일에 먼저 쓰고 바꿔치기해서, 저장 도중 끊겨도 기존 파일이 안전하다.</summary>
        public void Save(string name, PlayerState state)
        {
            string path = PathFor(name);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, SaveSerializer.ToJson(state));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
