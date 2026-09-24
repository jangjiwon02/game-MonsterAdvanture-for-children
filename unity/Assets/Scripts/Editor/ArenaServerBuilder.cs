using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MonsterAdventure.Editor
{
    /// <summary>Monster Adventure ▸ Arena ▸ ... : LAN 대결용 헤드리스 서버 씬을 만들고 exe 로 빌드한다.</summary>
    public static class ArenaServerBuilder
    {
        const string ScenePath = "Assets/Scenes/ArenaServer.unity";

        [MenuItem("Monster Adventure/Arena/Build Server Scene")]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Server", typeof(DedicatedServerBootstrap));
            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(s => s.path == ScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
            Debug.Log($"[MonsterAdventure] 서버 씬을 만들었다: {ScenePath}");
        }

        [MenuItem("Monster Adventure/Arena/Build Windows Server (headless)")]
        public static void BuildWindowsServer()
        {
            if (!System.IO.File.Exists(ScenePath)) BuildScene();
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/ArenaServer/MonsterAdventureServer.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[MonsterAdventure] 서버 빌드: {report.summary.result} ({report.summary.totalSize / 1024 / 1024}MB) -> {opts.locationPathName}");
        }
    }
}
