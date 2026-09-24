using UnityEditor;
using UnityEngine;

namespace MonsterAdventure.Editor
{
    /// <summary>저장이 있으면 게임이 자동으로 이어서 시작하므로, 새 게임을 보려면 저장을 지운다.</summary>
    public static class SaveMenu
    {
        [MenuItem("Monster Adventure/Delete Save")]
        public static void DeleteSave()
        {
            bool existed = SaveStore.Exists;
            SaveStore.Delete();
            Debug.Log(existed ? $"[MonsterAdventure] 저장을 지웠다: {SaveStore.FilePath}" : "[MonsterAdventure] 지울 저장이 없다.");
        }

        [MenuItem("Monster Adventure/Reveal Save Folder")]
        public static void RevealSaveFolder() => EditorUtility.RevealInFinder(SaveStore.FilePath);
    }
}
