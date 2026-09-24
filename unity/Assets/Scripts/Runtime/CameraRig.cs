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
        Vector3? _focusOverride;
        float? _sizeOverride;

        /// <summary>플레이어 추적 대신 지정한 초점/줌을 쓰는 중인지(전투 진입 연출용).</summary>
        public bool HasOverride => _focusOverride.HasValue || _sizeOverride.HasValue;
        public float OrthoSize => _cam != null ? _cam.orthographicSize : ViewTilesHigh / 2f;
        public const float DefaultOrthoSize = ViewTilesHigh / 2f;
        /// <summary>추적 중이라면 카메라가 보고 있어야 할 월드 위치(오버라이드 해제 후 복귀 목표).</summary>
        public Vector3 TrackedFocus => Target != null ? Target.position : transform.position;

        /// <summary>초점/줌을 덮어쓴다. null 인 항목은 평소대로(초점=플레이어, 줌=기본). 맵 가장자리 클램프는 그대로 적용된다.</summary>
        public void SetOverride(Vector3? focus, float? orthoSize)
        {
            _focusOverride = focus;
            _sizeOverride = orthoSize;
        }

        /// <summary>오버라이드를 풀고 평소 추적으로 돌아간다(줌도 기본값으로).</summary>
        public void ClearOverride()
        {
            _focusOverride = null;
            _sizeOverride = null;
            if (_cam != null) _cam.orthographicSize = DefaultOrthoSize;
        }

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
            if (HasOverride) _cam.orthographicSize = _sizeOverride ?? DefaultOrthoSize;
            if (Target == null && !_focusOverride.HasValue) return;

            float halfH = _cam.orthographicSize, halfW = halfH * UiKit.VirtualWidth / UiKit.VirtualHeight;
            var p = _focusOverride ?? Target.position;
            p.x = Mathf.Clamp(p.x, halfW, WorldMap.Width - halfW);
            p.y = Mathf.Clamp(p.y, halfH, WorldMap.Height - halfH);
            transform.position = new Vector3(p.x, p.y, -10f);
        }
    }
}
