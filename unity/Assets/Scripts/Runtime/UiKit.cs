using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 웹 버전의 480x320 가상 화면 좌표를 그대로 쓰는 IMGUI 헬퍼.
    /// 행렬 확대 대신 좌표·글자 크기를 직접 배율 계산해 글자가 흐려지지 않게 한다.
    /// </summary>
    public static class UiKit
    {
        public const int VirtualWidth = 480, VirtualHeight = 320;
        static readonly string[] FontNames = { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "NanumGothic" };

        static Font _font;
        static Texture2D _white;
        static GUIStyle _style;

        public static float Scale { get; private set; } = 1f;
        public static Vector2 Offset { get; private set; }

        // 안드로이드(그리고 아마 WebGL)에는 이 이름들로 된 한글 글꼴이 OS에 없어서
        // CreateDynamicFontFromOSFont가 실패하고 라틴 전용 기본 글꼴로 빠져, 한글 글자가
        // 깨진 글리프(예: 'Z')로 나오는 게 실기기에서 확인됐다. 그래서 한글이 보장되는
        // 번들 폰트(Resources/Fonts/NotoSansKR-Regular.ttf, OFL 라이선스)를 우선 쓰고,
        // 혹시 그 폰트를 못 찾을 때만(리소스 삭제 등) OS 폰트로 넘어간다.
        public static Font Font
        {
            get
            {
                if (_font != null) return _font;
                _font = Resources.Load<Font>("Fonts/NotoSansKR-Regular");
                if (_font == null) _font = Font.CreateDynamicFontFromOSFont(FontNames, 16);
                return _font;
            }
        }

        public static Color C(string hex, float a = 1f)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            c.a = a;
            return c;
        }

        /// <summary>OnGUI 맨 처음에 한 번 호출한다.</summary>
        public static void Begin()
        {
            Scale = Mathf.Min((float)Screen.width / VirtualWidth, (float)Screen.height / VirtualHeight);
            Offset = new Vector2((Screen.width - VirtualWidth * Scale) / 2f, (Screen.height - VirtualHeight * Scale) / 2f);
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { wordWrap = false, clipping = TextClipping.Overflow, richText = false, padding = new RectOffset() };
            _style.font = Font;
        }

        public static Rect ToScreen(float x, float y, float w, float h) =>
            new Rect(Offset.x + x * Scale, Offset.y + y * Scale, w * Scale, h * Scale);

        public static void Fill(float x, float y, float w, float h, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(ToScreen(x, y, w, h), _white);
            GUI.color = old;
        }

        /// <summary>가상 화면 밖(레터박스·반올림 오차 1px 포함)까지 창 전체를 한 색으로 덮는다.</summary>
        public static void FillScreen(Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _white);
            GUI.color = old;
        }

        public static void Texture(Texture tex, float x, float y, float w, float h, float alpha = 1f)
        {
            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.DrawTexture(ToScreen(x, y, w, h), tex, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// <summary>웹의 box(): 그림자 + 테두리 + 안쪽 채움.</summary>
        public static void Box(float x, float y, float w, float h, Color? fill = null, Color? border = null)
        {
            Fill(x + 3, y + 3, w, h, new Color(0, 0, 0, .35f));
            Fill(x, y, w, h, border ?? C("#f2f2f8"));
            Fill(x + 3, y + 3, w - 6, h - 6, fill ?? C("#1b2340"));
        }

        public static void Text(string s, float x, float y, int size = 14, Color? color = null, TextAnchor align = TextAnchor.UpperLeft, bool bold = false)
        {
            if (string.IsNullOrEmpty(s)) return;
            _style.fontSize = Mathf.Max(6, Mathf.RoundToInt(size * Scale));
            _style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            _style.alignment = align;
            var c = color ?? Color.white;
            _style.normal.textColor = c;
            const float span = 1000f;
            float rx = align == TextAnchor.UpperCenter ? x - span / 2 : align == TextAnchor.UpperRight ? x - span : x;
            GUI.Label(ToScreen(rx, y - 1, span, size * 2f), s, _style);
        }

        public static float MeasureWidth(string s, int size, bool bold = false)
        {
            _style.fontSize = Mathf.Max(6, Mathf.RoundToInt(size * Scale));
            _style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            return _style.CalcSize(new GUIContent(s)).x / Scale;
        }

        public static void Bar(float x, float y, float w, float h, float fraction, Color color)
        {
            Fill(x, y, w, h, C("#0a0d1a"));
            Fill(x + 1, y + 1, Mathf.Max(0, (w - 2) * Mathf.Clamp01(fraction)), h - 2, color);
        }

        public static Color HpColor(float f) => f > .5f ? C("#58d058") : f > .2f ? C("#f8d030") : C("#f05040");
    }
}
