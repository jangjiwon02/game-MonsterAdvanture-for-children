using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MonsterAdventure.EditorTools
{
    /// <summary>
    /// Android 빌드 모듈 설치 여부를 확인하고, 됐으면 실제 APK 빌드까지 시도해 결과를
    /// 프로젝트 루트의 android_build_result.txt 에 남긴다. `-batchmode -buildTarget Android
    /// -executeMethod MonsterAdventure.EditorTools.AutoAndroidBuildCheck.RunFromCommandLine` 로 호출한다
    /// (에디터를 미리 Android 타겟으로 띄우므로 런타임 타겟 전환/도메인 리로드를 신경 쓸 필요가 없다).
    /// </summary>
    public static class AutoAndroidBuildCheck
    {
        static readonly string ResultPath = Path.Combine(Application.dataPath, "..", "android_build_result.txt");

        public static void RunFromCommandLine()
        {
            bool supported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
            Log($"IsBuildTargetSupported(Android) = {supported}");

            if (!supported)
            {
                WriteResult($"ANDROID_SUPPORTED=false\ntime={DateTime.UtcNow:o}");
                return;
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                WriteResult($"ANDROID_SUPPORTED=true\nACTIVE_TARGET={EditorUserBuildSettings.activeBuildTarget}\ntime={DateTime.UtcNow:o}\n(-buildTarget Android 로 시작되지 않음)");
                return;
            }

            // 에뮬레이터(SwiftShader 소프트웨어 렌더러)가 Vulkan/ES3.1+ 컨텍스트 생성에 실패하는 걸
            // 실기기에서 재현해서 확인했다 — Auto Graphics API를 끄고 GLES3 하나만 명시해서
            // 에뮬레이터에서도 뜨는지 확인해본다. (Vulkan 지원 실기기에는 원래도 문제없었음)
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            Log("Graphics APIs(Android) 를 OpenGLES3 단일 지정으로 변경(Auto 끔)");

            try
            {
                string outDir = Path.Combine(Application.dataPath, "..", "Builds", "Android");
                Directory.CreateDirectory(outDir);
                string apkPath = Path.Combine(outDir, "MonsterAdventure.apk");

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/Chungju.unity" },
                    locationPathName = apkPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };
                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                string msg = $"ANDROID_SUPPORTED=true\nBUILD_RESULT={summary.result}\nOUTPUT={summary.outputPath}\nSIZE_BYTES={summary.totalSize}\nERRORS={summary.totalErrors}\nWARNINGS={summary.totalWarnings}\nTIME={DateTime.UtcNow:o}";
                WriteResult(msg);
                Log(msg);
            }
            catch (Exception e)
            {
                WriteResult($"ANDROID_SUPPORTED=true\nBUILD_RESULT=Exception\nERROR={e}\nTIME={DateTime.UtcNow:o}");
                Log("Build threw exception: " + e);
            }
        }

        static void WriteResult(string text) => File.WriteAllText(ResultPath, text);
        static void Log(string s) => Debug.Log("[AutoAndroidBuildCheck] " + s);
    }
}
