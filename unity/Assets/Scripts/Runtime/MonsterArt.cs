using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// web/index.html 의 drawMon 을 옮긴 몬스터 그림(종족당 한 번만 그려 캐시한다).
    /// 텍스처 안에서 몸통 반지름은 BodyRadius 픽셀이고, 중심은 (CenterX, CenterY)다.
    /// 화면에 그릴 때는 웹에서 쓰던 반지름 r 에 맞춰 Scale = r / BodyRadius 로 확대·축소한다.
    /// </summary>
    public static class MonsterArt
    {
        public const int CanvasSize = 256;
        public const float BodyRadius = 44f;
        public const float CenterX = 128f, CenterY = 150f;

        static readonly Dictionary<int, Texture2D> Cache = new Dictionary<int, Texture2D>();

        // 종족별 룩(look) → 몸통 실루엣 archetype. 기존엔 전부 동그란 몸통 하나였는데,
        // 뱀/용/물고기/나비/공룡/호랑이/늑대 7가지로 나눠서 실루엣 자체가 다르게 보이게 한다.
        static readonly Dictionary<string, string> BodyShape = new Dictionary<string, string> {
            {"flame","dinosaur"}, {"bumps","dinosaur"},
            {"horn","dragon"}, {"wings","dragon"},
            {"fin","fish"}, {"shell","fish"},
            {"leaf","butterfly"}, {"worm","butterfly"},
            {"bolt","wolf"},
            {"ears","tiger"},
            {"coil","snake"},
        };
        // 옆으로 긴 몸통(물고기·뱀)은 눈코입도 앞쪽으로 당겨줘야 자연스럽다. look 기준으로 건다.
        static readonly Dictionary<string, Vector2> FaceOffset = new Dictionary<string, Vector2> {
            {"fin", new Vector2(-.22f, -.02f)},
            {"shell", new Vector2(-.22f, -.02f)},
            {"coil", new Vector2(-.15f, -.05f)},
        };
        // 세부 디자인(발톱·이빨·눈동자 모양)을 archetype별로 켤지 결정하는 플래그.
        static readonly HashSet<string> Fierce = new HashSet<string> { "dinosaur", "dragon", "wolf", "tiger", "snake" };   // 송곳니
        static readonly HashSet<string> SlitEye = new HashSet<string> { "dragon", "tiger", "snake" };                     // 세로 슬릿 눈동자

        public static Texture2D Get(SpeciesData sp)
        {
            if (Cache.TryGetValue(sp.Id, out var tex) && tex != null) return tex;
            var p = new Painter(CanvasSize, CanvasSize);
            Draw(p, sp);
            tex = p.ToTexture();
            tex.filterMode = FilterMode.Bilinear;    // 축소해서 그리므로 부드럽게
            Cache[sp.Id] = tex;
            return tex;
        }

        /// <summary>웹의 drawMon(sp, cx, cy, r) 과 같은 위치·크기로 IMGUI 에 그린다(가상 화면 좌표).</summary>
        public static void DrawAt(SpeciesData sp, float cx, float cy, float r, float alpha = 1f)
        {
            if (r <= 0.5f || alpha <= 0.01f) return;
            float s = r / BodyRadius;
            UiKit.Texture(Get(sp), cx - CenterX * s, cy - CenterY * s, CanvasSize * s, CanvasSize * s, alpha);
        }

        static Color32 C(string hex, float a = 1f) => Painter.Hex(hex, a);

        /// <summary>amt: -1(검게)~+1(희게). 기본색에서 하이라이트/그림자 톤을 만든다(공식 포켓몬 아트의 광택 셰이딩 참고).
        /// alpha 는 덧칠할 때의 반투명도(1이면 완전 불투명).</summary>
        static Color32 Shade(Color32 c, float amt, float alpha = 1f)
        {
            float t = amt >= 0f ? 1f : 0f;
            float k = Mathf.Abs(amt);
            return new Color32(
                (byte)Mathf.Round(c.r + (t * 255f - c.r) * k),
                (byte)Mathf.Round(c.g + (t * 255f - c.g) * k),
                (byte)Mathf.Round(c.b + (t * 255f - c.b) * k),
                (byte)Mathf.Round(255f * alpha));
        }

        static void Draw(Painter p, SpeciesData sp)
        {
            bool st2 = sp.Stage == 2;
            float r = BodyRadius * (st2 ? 1.15f : 1f);
            Color32 body = C(sp.Color), belly = C(sp.Belly), O = C("#000000", .35f);
            float cx = CenterX, cy = CenterY;

            // 좌표 도우미: 몸통 중심 기준 (r 배율) → 텍스처 픽셀
            Vector2 P(float x, float y) => new Vector2(cx + x * r, cy + y * r);
            void Poly(Color32 fill, Color32? stroke, params float[] xy)
            {
                var pts = new List<Vector2>();
                for (int i = 0; i + 1 < xy.Length; i += 2) pts.Add(P(xy[i], xy[i + 1]));
                p.Polygon(pts, fill, stroke);
            }
            void Ell(float x, float y, float rx, float ry, Color32 fill, Color32? stroke = null, float rot = 0f)
            {
                var c = P(x, y);
                p.Ellipse(c.x, c.y, rx * r, ry * r, fill, stroke, rot);
            }
            void Stroke(Color32 c, float width, params float[] xy)
            {
                for (int i = 0; i + 3 < xy.Length; i += 2) p.Line(P(xy[i], xy[i + 1]), P(xy[i + 2], xy[i + 3]), width, c);
            }
            // 광택 있는 타원: 기본색을 채운 뒤 그 실루엣 안에서만 밝은/어두운 반점을 겹쳐 입체감을 준다(단색 대비 업그레이드).
            void ShinyEll(float x, float y, float rx, float ry, Color32 fill, Color32? stroke = null)
            {
                Ell(x, y, rx, ry, fill, stroke);
                var center = P(x, y);
                p.ClipEllipse(center.x, center.y, rx * r, ry * r);
                Ell(x - rx * 0.32f, y - ry * 0.42f, rx * 0.62f, ry * 0.55f, Shade(fill, 0.55f, 0.30f));
                Ell(x + rx * 0.38f, y + ry * 0.5f, rx * 0.55f, ry * 0.45f, Shade(fill, -0.5f, 0.20f));
                p.ClearClip();
            }
            // 발 위치(fx,fy)에 작은 발톱 3개를 그린다. fw: 발 폭 대략치(전부 r-unit).
            void Claws(float fx, float fy, float fw)
            {
                var cl = new Color32(25, 25, 25, 217);
                foreach (int i in new[] { -1, 0, 1 })
                {
                    float dx = i * fw * .4f;
                    Poly(cl, null, fx + dx - fw * .1f, fy + fw * .1f, fx + dx + fw * .1f, fy + fw * .1f, fx + dx, fy + fw * .42f);
                }
            }

            /// <summary>몸통·다리·꼬리·날개 등 archetype별 실루엣. 종족별 부착물(look)은 이 위/아래에 그대로 얹힌다.</summary>
            void DrawBody(string shape)
            {
                if (shape == "dinosaur")
                {
                    Poly(body, O, .5f, .5f, 1.25f, .7f, .68f, .95f);
                    Ell(-.42f, .92f, .24f, .2f, body, O);
                    Ell(.42f, .92f, .24f, .2f, body, O);
                    Ell(-.68f, .62f, .13f, .2f, body, O);
                    Ell(.68f, .62f, .13f, .2f, body, O);
                    Claws(-.42f, .92f, .24f); Claws(.42f, .92f, .24f);
                    ShinyEll(0, -.02f, .78f, .85f, body, O);
                    ShinyEll(0, .32f, .5f, .46f, belly);
                }
                else if (shape == "dragon")
                {
                    Poly(body, O, .55f, .3f, 1.35f, .1f, 1.05f, .55f, .65f, .55f);
                    Ell(-.4f, .88f, .26f, .17f, body, O);
                    Ell(.4f, .88f, .26f, .17f, body, O);
                    Claws(-.4f, .88f, .26f); Claws(.4f, .88f, .26f);
                    ShinyEll(0, 0, .85f, .8f, body, O);
                    ShinyEll(0, .25f, .55f, .42f, belly);
                }
                else if (shape == "fish")
                {
                    Poly(body, O, .8f, -.2f, 1.4f, -.4f, 1.15f, 0f, 1.4f, .4f, .8f, .2f);
                    ShinyEll(-.05f, 0, .95f, .6f, body, O);
                    ShinyEll(-.05f, .18f, .58f, .3f, belly);
                }
                else if (shape == "butterfly")
                {
                    var wing = new Color32(belly.r, belly.g, belly.b, 217); // JS globalAlpha=.85 대응
                    foreach (int s in new[] { -1, 1 })
                    {
                        Poly(wing, O, s * .2f, -.5f, s * 1.15f, -.9f, s * .95f, -.15f, s * .25f, -.05f);
                        Poly(wing, O, s * .2f, .05f, s * .95f, .25f, s * .7f, .65f, s * .2f, .5f);
                    }
                    ShinyEll(0, .05f, .34f, .78f, body, O);
                    ShinyEll(0, .3f, .2f, .4f, belly);
                }
                else if (shape == "wolf" || shape == "tiger")
                {
                    bool stocky = shape == "tiger";
                    Poly(body, O, -.6f, .3f, -1.2f, .1f * (stocky ? 1f : 1.3f), -.65f, .55f);
                    Ell(-.55f, .95f, stocky ? .17f : .14f, .22f, body, O);
                    Ell(.55f, .95f, stocky ? .17f : .14f, .22f, body, O);
                    Ell(-.32f, .8f, .12f, .2f, body, O);
                    Ell(.32f, .8f, .12f, .2f, body, O);
                    Claws(-.55f, .95f, stocky ? .17f : .14f); Claws(.55f, .95f, stocky ? .17f : .14f);
                    Claws(-.32f, .8f, .12f); Claws(.32f, .8f, .12f);
                    ShinyEll(0, -.02f, stocky ? .78f : .7f, stocky ? .82f : .78f, body, O);
                    ShinyEll(0, .28f, .5f, .42f, belly);
                    Ell(0, .05f, .16f, .12f, Shade(body, -.15f), O);
                }
                else if (shape == "snake")
                {
                    Poly(body, O, .7f, .32f, 1.35f, .18f, .85f, -.05f);
                    ShinyEll(0, 0, .92f, .55f, body, O);
                    ShinyEll(0, .2f, .62f, .28f, belly);
                }
                else
                {
                    Ell(-.45f, .85f, .3f, .16f, body, O);
                    Ell(.45f, .85f, .3f, .16f, body, O);
                    ShinyEll(0, 0, 1f, .9f, body, O);
                    ShinyEll(0, .3f, .62f, .5f, belly);
                }
                Ell(-.18f, -.55f, .28f, .13f, C("#ffffff", .32f), null, -0.3f);
            }

            string look = sp.Look;

            /* 뒤쪽 파츠 */
            if (look == "flame")
            {
                float k = st2 ? 1.3f : 1f;
                Poly(C("#ff9a2e"), O, .7f, .55f, 1.25f, .55f - .7f * k, 1.0f, .75f);
                Poly(C("#ffe060"), null, .85f, .6f, 1.1f, .55f - .35f * k, 1.0f, .72f);
            }
            else if (look == "bolt")
            {
                float m = st2 ? 1.1f : 1f, w = r * .24f;
                var pts = new[] { .75f, .5f, 1.25f, .3f, 1.0f, .02f, 1.55f * m, -.3f };
                Stroke(C("#ffd84a"), w, pts);
                for (int i = 2; i < pts.Length - 2; i += 2) { var c = P(pts[i], pts[i + 1]); p.Ellipse(c.x, c.y, w / 2, w / 2, C("#ffd84a")); }   // 꺾이는 곳 메우기
            }
            else if (look == "wings")
            {
                foreach (int s in new[] { -1, 1 })
                {
                    float wr = .3f * (st2 ? 1.3f : 1f), wl = .6f * (st2 ? 1.25f : 1f);
                    float rot = s * .35f;
                    Ell(s * .9f, .05f, wr, wl, belly, O, rot);
                    // 깃털 결(층이 진 타원을 겹쳐서 깃털 줄무늬처럼 보이게)
                    Ell(s * .9f, .05f, wr * .68f, wl * .78f, Shade(belly, -0.18f), null, rot);
                    Ell(s * .9f, .05f, wr * .4f, wl * .5f, Shade(belly, -0.3f), null, rot);
                }
            }
            else if (look == "leaf" && st2)
            {
                for (int i = 0; i < 6; i++)
                {
                    float a = i / 6f * Mathf.PI * 2f;
                    Ell(Mathf.Cos(a) * .3f, -1.0f + Mathf.Sin(a) * .3f, .22f, .22f, C("#ff8fb8"), O);
                }
                Ell(0, -1.0f, .16f, .16f, C("#ffd84a"));
            }
            else if (look == "ears" && st2)
            {
                for (int i = 0; i < 10; i++)
                {
                    float a = i / 10f * Mathf.PI * 2f;
                    Ell(Mathf.Cos(a) * .95f, Mathf.Sin(a) * .85f, .27f, .27f, belly, O);
                }
            }
            else if (look == "fin" && st2)
            {
                Poly(C("#5a92e8"), O, -.4f, -.7f, -.15f, -1.35f, .05f, -.85f);
                Poly(C("#5a92e8"), O, .4f, -.7f, .15f, -1.35f, -.05f, -.85f);
            }
            else if (look == "worm")
            {
                foreach (int s in new[] { -1, 1 })
                {
                    Stroke(Shade(body, -0.3f), r * .05f, s * .18f, -.85f, s * .42f, -1.15f, s * .55f, -1.5f);
                    Ell(s * .55f, -1.55f, .09f, .09f, Shade(body, 0.25f));
                }
            }

            /* 몸 — 종족별 archetype 실루엣(뱀/용/물고기/나비/공룡/호랑이/늑대) */
            DrawBody(BodyShape.TryGetValue(look, out var bodyShapeName) ? bodyShapeName : "round");

            /* 앞쪽 파츠 */
            if (look == "flame")
            {
                float k = st2 ? 1.35f : 1f;
                Poly(C("#ff9a2e"), O, -.35f, -.75f, -.05f, -1.55f * k, .3f, -.8f);
                Poly(C("#ffe060"), null, -.15f, -.78f, 0f, -1.2f * k, .15f, -.8f);
                if (st2)
                {
                    Poly(C("#ff9a2e"), O, .15f, -.8f, .55f, -1.3f, .6f, -.65f);
                    Poly(C("#ff9a2e"), O, -.6f, -.65f, -.55f, -1.3f, -.15f, -.8f);
                }
            }
            else if (look == "fin")
            {
                Poly(C("#a8d4ff"), O, -.1f, -.85f, .2f, -1.5f * (st2 ? 1.1f : 1f), .45f, -.8f);
                Ell(-.97f, .15f, .18f, .3f, C("#a8d4ff"), O);
                Ell(.97f, .15f, .18f, .3f, C("#a8d4ff"), O);
            }
            else if (look == "leaf")
            {
                Stroke(C("#3a8a3a"), 3f, 0f, -.85f, 0f, -1.1f);
                foreach (int s in new[] { -1, 1 }) Ell(s * .3f, -1.2f, .34f, .17f, C("#58c84a"), O, s * .5f);
            }
            else if (look == "bolt")
            {
                float m = st2 ? 1.1f : 1f;
                foreach (int s in new[] { -1, 1 })
                {
                    Poly(body, O, s * .75f, -.55f, s * .55f, -1.55f * m, s * .2f, -.8f);
                    Poly(C("#ffd84a"), null, s * .6f, -1.1f, s * .55f, -1.55f * m, s * .4f, -1.2f);
                    Poly(C("#ffd84a"), null, s * .5f, .12f, s * .78f, .28f, s * .6f, .3f, s * .72f, .5f);
                }
            }
            else if (look == "bumps")
            {
                var cl = C("#7a6c54");
                Ell(-.45f, -.78f, .22f, .2f, cl, O);
                Ell(0, -.93f, .26f, .22f, cl, O);
                Ell(.45f, -.78f, .22f, .2f, cl, O);
                if (st2)
                    foreach (int s in new[] { -1, 1 })
                    {
                        Poly(cl, O, s * .82f, -.2f, s * 1.3f, -.55f, s * .85f, -.5f);
                        Poly(cl, O, s * .82f, .2f, s * 1.3f, .05f, s * .88f, -.1f);
                    }
            }
            else if (look == "ears")
            {
                foreach (int s in new[] { -1, 1 })
                {
                    Ell(s * .6f, -.72f, .28f, .3f, body, O);
                    Ell(s * .6f, -.7f, .15f, .17f, C("#ffb0c0"));
                }
            }
            else if (look == "wings")
            {
                Poly(C("#ffb030"), O, -.14f, .05f, .14f, .05f, 0f, .32f);
                Poly(body, O, -.1f, -.85f, 0f, -1.2f, .12f, -.85f);
                Poly(body, O, .05f, -.85f, .25f, -1.1f, .3f, -.8f);
            }
            else if (look == "horn")
            {
                foreach (int s in new[] { -1, 1 })
                {
                    Poly(C("#f6ead0"), O, s * .55f, -.65f, s * .45f, -1.5f * (st2 ? 1.15f : 1f), s * .15f, -.85f);
                    Poly(C("#d8563c"), null, s * .5f, -.9f, s * .47f, -1.2f, s * .3f, -.95f);
                }
            }
            else if (look == "worm")
            {
                Ell(0, -.95f, .22f, .12f, C("#58c84a"), O, -0.2f);
            }
            else if (look == "shell")
            {
                foreach (int k in new[] { -1, 0, 1 })
                    Stroke(Shade(body, -0.4f), r * .045f, k * .3f - .12f, .28f, k * .3f + .12f, .1f);
                Ell(0, -.78f, .13f, .1f, Shade(body, -0.3f), O);
            }
            else if (look == "coil")
            {
                Poly(Shade(body, -0.25f), O, -.12f, -.85f, 0f, -1.18f, .12f, -.85f);
                Poly(Shade(body, -0.2f), O, -.28f, -.7f, -.36f, -.95f, -.16f, -.78f);
                Poly(Shade(body, -0.2f), O, .28f, -.7f, .36f, -.95f, .16f, -.78f);
            }

            /* 얼굴 — 옆으로 긴 몸통(물고기·뱀)은 앞쪽으로 당겨서 자연스럽게 */
            var faceOff = FaceOffset.TryGetValue(look, out var fo) ? fo : Vector2.zero;
            cx += faceOff.x * r; cy += faceOff.y * r;
            string shapeName = BodyShape.TryGetValue(look, out var sn) ? sn : "round";

            Ell(-.36f, -.16f, .17f, .21f, C("#ffffff"), O);
            Ell(.36f, -.16f, .17f, .21f, C("#ffffff"), O);
            // 눈동자: 맹수·파충류 계열(용/호랑이/뱀)은 세로 슬릿, 나머지는 동그란 눈동자.
            float prx = SlitEye.Contains(shapeName) ? .035f : .09f;
            float pry = SlitEye.Contains(shapeName) ? .16f : .12f;
            Ell(-.34f, -.13f, prx, pry, C("#151515"));
            Ell(.34f, -.13f, prx, pry, C("#151515"));
            Ell(-.31f, -.18f, .035f, .035f, C("#ffffff"));
            Ell(.37f, -.18f, .035f, .035f, C("#ffffff"));
            if (look != "wings")
            {
                // 입: 중심 (0, .12r), 반지름 .16r, 각도 .15π ~ .85π 의 호
                var mouth = new List<float>();
                for (int i = 0; i <= 8; i++)
                {
                    float a = Mathf.PI * Mathf.Lerp(.15f, .85f, i / 8f);
                    mouth.Add(Mathf.Cos(a) * .16f); mouth.Add(.12f + Mathf.Sin(a) * .16f);
                }
                Stroke(C("#222222"), 2f, mouth.ToArray());
            }
            if (Fierce.Contains(shapeName) && look != "wings")
            {
                // 송곳니: 입 양 끝에서 아래로 뾰족하게. 부리(wings)는 이빨이 안 어울려서 제외.
                foreach (float tx in new[] { .142f, -.142f })
                    Poly(C("#fdfdf6"), C("#000000", .4f), tx - .045f, .173f, tx + .045f, .173f, tx, .313f);
            }
            if (st2)
            {
                Stroke(C("#222222"), 3f, -.6f, -.46f, -.2f, -.34f);
                Stroke(C("#222222"), 3f, .6f, -.46f, .2f, -.34f);
            }
        }
    }
}
