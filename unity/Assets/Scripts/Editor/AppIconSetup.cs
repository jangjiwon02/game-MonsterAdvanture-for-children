using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MonsterAdventure.EditorTools
{
    /// <summary>
    /// Assets/Icons/AppIcon/ 의 절차적으로 만든 앱 아이콘(네잎클로버+몬스터볼+찌릿이,
    /// 텍스트 없는 정사각형 구도)을 Android/Standalone Player Settings 아이콘 슬롯에 채운다.
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

            ApplyFor(NamedBuildTarget.Android);
            ApplyFor(NamedBuildTarget.Standalone);

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

        static Texture2D Load(int size) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconDir}/icon_{size}.png");

        static void ApplyFor(NamedBuildTarget target)
        {
            var sizes = PlayerSettings.GetIconSizes(target, IconKind.Any);
            if (sizes == null || sizes.Length == 0) return;

            var available = new[] { 36, 48, 72, 96, 144, 192, 512 };
            var icons = new Texture2D[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                int closest = available[0];
                foreach (var a in available)
                    if (Mathf.Abs(a - sizes[i]) < Mathf.Abs(closest - sizes[i])) closest = a;
                icons[i] = Load(closest);
            }
            PlayerSettings.SetIcons(target, icons, IconKind.Any);
        }
    }
}
