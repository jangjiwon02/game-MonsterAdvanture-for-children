using System;
using System.IO;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>세이브 파일 한 개(persistentDataPath). 쓰기는 임시 파일에 먼저 쓴 뒤 바꿔치기해서 도중에 끊겨도 기존 저장이 남는다.</summary>
    public static class SaveStore
    {
        public static string FilePath => Path.Combine(Application.persistentDataPath, "monster_adventure_save.json");

        public static bool Exists => File.Exists(FilePath);

        public static bool Save(PlayerState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, SaveSerializer.ToJson(state));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                // 저장이 일어나는 모든 지점(센터·상점·퀴즈·포획 등)에서 자연스럽게 진행상황을 같이 보낸다.
                TelemetryClient.SendProgress(state.PlayerName,
                    state.Party.Count > 0 ? state.Party[0].Level : 0,
                    state.Dex.Count, state.Money, state.TotalPlaySeconds);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"저장 실패: {e.Message}");
                return false;
            }
        }

        /// <summary>저장이 없거나 손상됐으면 null.</summary>
        public static PlayerState TryLoad(GameData data)
        {
            try
            {
                if (!Exists) return null;
                var state = SaveSerializer.FromJson(data, File.ReadAllText(FilePath));
                if (state == null) Debug.LogWarning("저장 데이터가 손상되어 새 게임으로 시작한다: " + FilePath);
                return state;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"저장 불러오기 실패: {e.Message}");
                return null;
            }
        }

        public static void Delete()
        {
            try { if (Exists) File.Delete(FilePath); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Debug.LogWarning(e.Message); }
        }
    }
}
