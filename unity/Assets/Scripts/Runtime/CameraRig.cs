using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 웹과 같은 480x320(3:2, 타일 15x10칸) 화면. 창 비율이 달라도 3:2 로 레터박스하고,
    /// 플레이어를 따라가되 맵 밖은 보이지 않게 가장자리에서 멈춘다.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraRig : MonoBehaviour
    {
        public const float ViewTilesHigh = UiKit.VirtualHeight / (float)TileArt.Size;    // 10

        public Transform Target;
        Camera _cam;
        Camera _letterbox;
        int _lastW, _lastH;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = ViewTilesHigh / 2f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.depth = 0;

            // 3:2 밖의 여백을 검게 지우는 보조 카메라
            var lb = new GameObject("Letterbox Camera", typeof(Camera));
            lb.transform.SetParent(transform.parent, false);
            _letterbox = lb.GetComponent<Camera>();
            _letterbox.clearFlags = CameraClearFlags.SolidColor;
            _letterbox.backgroundColor = Color.black;
            _letterbox.cullingMask = 0;
            _letterbox.depth = -100;
            if (lb.TryGetComponent<AudioListener>(out var al)) Destroy(al);
        }

        void LateUpdate()
        {
            if (Screen.width != _lastW || Screen.height != _lastH)
            {
                _lastW = Screen.width; _lastH = Screen.height;
                float s = Mathf.Min((float)Screen.width / UiKit.VirtualWidth, (float)Screen.height / UiKit.VirtualHeight);
                float w = UiKit.VirtualWidth * s / Screen.width, h = UiKit.VirtualHeight * s / Screen.height;
                _cam.rect = new Rect((1 - w) / 2f, (1 - h) / 2f, w, h);
            }
            if (Target == null) return;

            float halfH = _cam.orthographicSize, halfW = halfH * UiKit.VirtualWidth / UiKit.VirtualHeight;
            var p = Target.position;
            p.x = Mathf.Clamp(p.x, halfW, WorldMap.Width - halfW);
            p.y = Mathf.Clamp(p.y, halfH, WorldMap.Height - halfH);
            transform.position = new Vector3(p.x, p.y, -10f);
        }
    }
}
