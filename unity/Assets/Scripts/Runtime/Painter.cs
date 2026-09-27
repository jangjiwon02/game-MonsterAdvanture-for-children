using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 웹 버전의 Canvas 2D 그리기(사각형·타원·다각형)를 흉내 내는 소프트웨어 래스터라이저.
    /// 좌표는 웹과 같이 왼쪽 위가 원점, y는 아래로 증가한다. 도형 가장자리는 3x3 슈퍼샘플링으로 안티앨리어싱한다.
    /// </summary>
    public sealed class Painter
    {
        const int Sub = 3;

        public readonly int Width;
        public readonly int Height;
        readonly Color32[] _px;      // 0번 행이 맨 위

        public Painter(int width, int height)
        {
            Width = width; Height = height;
            _px = new Color32[width * height];
        }

        public static Color32 Hex(string hex, float alpha = 1f)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            c.a = alpha;
            return c;
        }

        public void Clear(Color32 c) { for (int i = 0; i < _px.Length; i++) _px[i] = c; }

        /// <summary>한 픽셀을 알파 합성으로 찍는다(범위 밖은 무시).</summary>
        public void Plot(int x, int y, Color32 c)
        {
            if (x >= 0 && y >= 0 && x < Width && y < Height) Blend(x, y, c, 1f);
        }

        void Blend(int x, int y, Color32 src, float coverage)
        {
            float a = src.a / 255f * coverage;
            if (a <= 0f) return;
            int i = y * Width + x;
            Color32 dst = _px[i];
            float da = dst.a / 255f;
            float outA = a + da * (1f - a);
            if (outA <= 0f) return;
            float k = da * (1f - a);
            _px[i] = new Color32(
                (byte)Mathf.Round((src.r * a + dst.r * k) / outA),
                (byte)Mathf.Round((src.g * a + dst.g * k) / outA),
                (byte)Mathf.Round((src.b * a + dst.b * k) / outA),
                (byte)Mathf.Round(outA * 255f));
        }

        Func<float, float, bool> _clip;   // 한 겹만 지원(중첩 안 함) — ClipEllipse/ClearClip 으로 건다/푼다

        /// <summary>이후의 채우기를 타원 안쪽으로만 제한한다(광택 하이라이트가 몸통 밖으로 안 번지게). ClearClip 으로 푼다.</summary>
        public void ClipEllipse(float cx, float cy, float rx, float ry) => _clip = EllipseTest(cx, cy, Mathf.Abs(rx), Mathf.Abs(ry), 0f);
        public void ClearClip() => _clip = null;

        void Fill(float minX, float minY, float maxX, float maxY, Color32 c, Func<float, float, bool> inside)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(minX)), x1 = Mathf.Min(Width - 1, Mathf.CeilToInt(maxX));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(minY)), y1 = Mathf.Min(Height - 1, Mathf.CeilToInt(maxY));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (_clip != null && !_clip(x + 0.5f, y + 0.5f)) continue;
                    int hit = 0;
                    for (int j = 0; j < Sub; j++)
                        for (int i = 0; i < Sub; i++)
                            if (inside(x + (i + 0.5f) / Sub, y + (j + 0.5f) / Sub)) hit++;
                    if (hit > 0) Blend(x, y, c, hit / (float)(Sub * Sub));
                }
        }

        public void Rect(float x, float y, float w, float h, Color32 c)
        {
            // 정수 좌표 사각형은 안티앨리어싱 없이 빠르게 채운다.
            int x0 = Mathf.Max(0, Mathf.RoundToInt(x)), x1 = Mathf.Min(Width, Mathf.RoundToInt(x + w));
            int y0 = Mathf.Max(0, Mathf.RoundToInt(y)), y1 = Mathf.Min(Height, Mathf.RoundToInt(y + h));
            for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++) Blend(xx, yy, c, 1f);
        }

        static Func<float, float, bool> EllipseTest(float cx, float cy, float rx, float ry, float rot)
        {
            float cos = Mathf.Cos(-rot), sin = Mathf.Sin(-rot);
            rx = Mathf.Max(0.01f, rx); ry = Mathf.Max(0.01f, ry);
            return (px, py) =>
            {
                float dx = px - cx, dy = py - cy;
                float u = (dx * cos - dy * sin) / rx, v = (dx * sin + dy * cos) / ry;
                return u * u + v * v <= 1f;
            };
        }

        /// <summary>outline 이 있으면 캔버스의 stroke(폭 2, 경로 중심)처럼 안팎으로 1px씩 덮는다.</summary>
        public void Ellipse(float cx, float cy, float rx, float ry, Color32 fill, Color32? outline = null, float rot = 0f)
        {
            rx = Mathf.Abs(rx); ry = Mathf.Abs(ry);
            float pad = rx + ry + 2f;
            Fill(cx - pad, cy - pad, cx + pad, cy + pad, fill, EllipseTest(cx, cy, rx, ry, rot));
            if (!outline.HasValue) return;
            var outer = EllipseTest(cx, cy, rx + 1f, ry + 1f, rot);
            var inner = EllipseTest(cx, cy, Mathf.Max(0.01f, rx - 1f), Mathf.Max(0.01f, ry - 1f), rot);
            Fill(cx - pad, cy - pad, cx + pad, cy + pad, outline.Value, (px, py) => outer(px, py) && !inner(px, py));
        }

        static bool InPolygon(IList<Vector2> p, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].y > y) != (p[j].y > y) && x < (p[j].x - p[i].x) * (y - p[i].y) / (p[j].y - p[i].y) + p[i].x)
                    inside = !inside;
            return inside;
        }

        public void Polygon(IList<Vector2> pts, Color32 fill, Color32? outline = null)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts) { minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y); }
            Fill(minX - 1, minY - 1, maxX + 1, maxY + 1, fill, (x, y) => InPolygon(pts, x, y));
            if (!outline.HasValue) return;
            for (int i = 0; i < pts.Count; i++) Line(pts[i], pts[(i + 1) % pts.Count], 2f, outline.Value);
        }

        public void Polygon(Color32 fill, Color32? outline, params float[] xy)
        {
            var pts = new List<Vector2>(xy.Length / 2);
            for (int i = 0; i + 1 < xy.Length; i += 2) pts.Add(new Vector2(xy[i], xy[i + 1]));
            Polygon(pts, fill, outline);
        }

        public void Line(Vector2 a, Vector2 b, float width, Color32 c)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (width * 0.5f);
            Polygon(new List<Vector2> { a + n, b + n, b - n, a - n }, c);
        }

        /// <summary>다른 Painter 의 내용을 (x, y) 위치에 알파 합성한다.</summary>
        public void Blit(Painter src, int x, int y)
        {
            for (int sy = 0; sy < src.Height; sy++)
            {
                int dy = y + sy;
                if (dy < 0 || dy >= Height) continue;
                for (int sx = 0; sx < src.Width; sx++)
                {
                    int dx = x + sx;
                    if (dx < 0 || dx >= Width) continue;
                    Blend(dx, dy, src._px[sy * src.Width + sx], 1f);
                }
            }
        }

        public Texture2D ToTexture()
        {
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var flipped = new Color32[_px.Length];
            for (int y = 0; y < Height; y++)
                Array.Copy(_px, y * Width, flipped, (Height - 1 - y) * Width, Width);
            tex.SetPixels32(flipped);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>텍스처의 왼쪽 위 기준 영역(x, y, w, h)을 스프라이트로 자른다.</summary>
        public static Sprite Slice(Texture2D tex, int x, int yFromTop, int w, int h, float pixelsPerUnit, Vector2 pivot)
        {
            var sprite = Sprite.Create(tex, new Rect(x, tex.height - (yFromTop + h), w, h), pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        public Sprite ToSprite(float pixelsPerUnit, Vector2 pivot) => Slice(ToTexture(), 0, 0, Width, Height, pixelsPerUnit, pivot);
    }
}
