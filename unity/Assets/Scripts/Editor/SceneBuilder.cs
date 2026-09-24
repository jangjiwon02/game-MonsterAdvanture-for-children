using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MonsterAdventure.Editor
{
    /// <summary>Monster Adventure ▸ Build Chungju Scene: 빈 씬에 카메라와 GameBootstrap 만 놓아 Assets/Scenes/Chungju.unity 를 만든다.</summary>
    public static class SceneBuilder
    {
        const string ScenePath = "Assets/Scenes/Chungju.unity";

        [MenuItem("Monster Adventure/Build Chungju Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(CameraRig));
            cam.tag = "MainCamera";

            new GameObject("Game", typeof(GameBootstrap));

            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(s => s.path == ScenePath))
            {
                scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
            Debug.Log($"[MonsterAdventure] 씬을 만들었다: {ScenePath}");
        }
    }
}
