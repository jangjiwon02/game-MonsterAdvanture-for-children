using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace MonsterAdventure.EditorTools
{
    /// <summary>
    /// Assets/Icons/AppIcon/ 의 절차적으로 만든 앱 아이콘(네잎클로버+몬스터볼+찌릿이,
    /// 텍스트 없는 정사각형 구도)을 Player Settings 아이콘 슬롯에 채운다.
    /// - Standalone: 배경 포함 단일 정사각형 이미지(icon_*.png).
    /// - Android: 최신 Adaptive 아이콘 2겹(fg_*.png=투명배경 safe-zone 구도, bg_*.png=단색배경).
    ///   Legacy/Round 는 최신 Unity에서 obsolete(Adaptive 하나로 통일) — Adaptive 아이콘만
    ///   설정하면 Unity가 빌드 시 예전 버전 안드로이드용 레거시 아이콘을 알아서 만들어 준다.
    /// `-executeMethod MonsterAdventure.EditorTools.AppIconSetup.RunFromCommandLine` 로 헤드리스 호출 가능.
    /// </summary>
    public static class AppIconSetup
    {
        const string IconDir = "Assets/Icons/AppIcon";

        [MenuItem("Monster Adventure/Apply App Icon")]
        public static void Apply()
        {
            AssetDatabase.Refresh();
            EnsureReadable();

            ApplyStandalone();
            ApplyAndroidAdaptive();

            AssetDatabase.SaveAssets();
            Debug.Log("AppIconSetup: 아이콘 적용 완료.");
        }

        public static void RunFromCommandLine()
        {
            Apply();
        }

        static void EnsureReadable()
        {
            foreach (var path in Directory.GetFiles(IconDir, "*.png"))
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                bool changed = false;
                if (!importer.isReadable) { importer.isReadable = true; changed = true; }
                if (importer.textureCompression != TextureImporterCompression.Uncompressed)
                {
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    changed = true;
                }
                if (changed) importer.SaveAndReimport();
            }
        }

        static Texture2D Load(string prefix, int size) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconDir}/{prefix}_{size}.png");

        static int Closest(int[] available, int target)
        {
            int closest = available[0];
            foreach (var a in available)
                if (Mathf.Abs(a - target) < Mathf.Abs(closest - target)) closest = a;
            return closest;
        }

        /// <summary>Windows Standalone 빌드 실행 파일 아이콘 — 배경 포함 단일 이미지 하나로 충분.</summary>
        static void ApplyStandalone()
        {
            var sizes = PlayerSettings.GetIconSizes(NamedBuildTarget.Standalone, IconKind.Any);
            if (sizes == null || sizes.Length == 0) return;

            var available = new[] { 36, 48, 72, 96, 144, 192, 512 };
            var icons = sizes.Select(s => Load("icon", Closest(available, s))).ToArray();
            PlayerSettings.SetIcons(NamedBuildTarget.Standalone, icons, IconKind.Any);
        }

        /// <summary>안드로이드 Adaptive 아이콘 — 슬롯마다 배경/전경 2겹을 채워야 실제로 반영된다
        /// (전경만 비워두면 빌드에 Unity 기본 큐브 로고가 그대로 남는 버그가 있었음 — 반드시 SetTextures로 둘 다 지정).</summary>
        static void ApplyAndroidAdaptive()
        {
            var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, AndroidPlatformIconKind.Adaptive);
            if (icons == null || icons.Length == 0) return;

            var available = new[] { 432, 324, 216, 162, 108, 81 };
            foreach (var icon in icons)
            {
                int size = Closest(available, icon.width);
                var bg = Load("bg", size);
                var fg = Load("fg", size);
                // 레이어 순서는 AndroidManifest의 <adaptive-icon><background/><foreground/></adaptive-icon> 과 동일 — 0=배경, 1=전경.
                icon.SetTextures(new[] { bg, fg });
            }
            PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, AndroidPlatformIconKind.Adaptive, icons);
        }
    }
}
