using System.Collections;
using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;
using static MonsterAdventure.Core.Korean;

namespace MonsterAdventure
{
    /// <summary>
    /// 타이틀 → (새 게임: 소개 대사 → 파트너 선택) / (이어하기: 저장 불러오기). 웹의 titleScreen / newGame.
    /// Run 이 끝나면 Result 에 시작할 PlayerState 가 들어 있다.
    /// </summary>
    public sealed class TitleController
    {
        readonly GameUi _ui;
        readonly GameData _data;

        bool _starterScreen;
        int _starterIndex;
        static Texture2D _titleBackground, _shadow;

        public PlayerState Result { get; private set; }
        /// <summary>true 면 단일 플레이 대신 LAN 대결 마당(ArenaController)으로 넘어간다.</summary>
        public bool WantsArena { get; private set; }

        public TitleController(GameUi ui, GameData data) { _ui = ui; _data = data; }

        public IEnumerator Run()
        {
            _ui.Data = _data;
            _ui.DrawScene = DrawScene;
            var menuRect = new Rect(UiKit.VirtualWidth / 2f - 75, 146, 150, 4 * 26 + 16);
            while (Result == null && !WantsArena)
            {
                // 저장이 없으면 '이어하기'는 회색으로 두고 고를 수 없다.
                yield return _ui.Choose(new[] { "새 게임", "이어하기", "함께하기 (LAN)", "게임 종료" },
                    new MenuOptions { Rect = menuRect, Disabled = SaveStore.Exists ? null : new HashSet<int> { 1 } });

                if (_ui.Choice == 3) { yield return _ui.ConfirmQuit(); continue; }
                if (_ui.Choice == 2) { WantsArena = true; continue; }
                if (_ui.Choice == 1)
                {
                    var loaded = SaveStore.TryLoad(_data);
                    if (loaded == null) { yield return _ui.Say("저장 데이터를 불러오지 못했다."); continue; }
                    Result = loaded;
                    continue;
                }

                if (SaveStore.Exists)
                {
                    yield return _ui.Choose(new[] { "예", "아니오" },
                        new MenuOptions { Rect = new Rect(UiKit.VirtualWidth / 2f - 70, 160, 140, 2 * 26 + 16), Prompt = "기존 저장 데이터가 덮어써집니다.\n새로 시작할까요?", Full = true });
                    if (_ui.Choice != 0) continue;
                }
                yield return NewGame();
            }
            _ui.DrawScene = null;
        }

        IEnumerator NewGame()
        {
            yield return _ui.Say("안녕! 여기는 충주시야.\n남한강과 충주호, 그리고 사과로 유명한 고장이지!");
            yield return _ui.Say("그런데 요즘 이 동네에 신기한 몬스터들이 나타나기 시작했단다.");
            yield return _ui.Say("풀숲을 걸으면 야생 몬스터가 나타나!\n싸워서 성장시키고, 몬스터볼로 잡아 보자.");
            yield return _ui.Say("자, 먼저 함께할 파트너를 골라 봐!");

            var choices = PlayerState.Starters;
            _starterIndex = 0;
            _starterScreen = true;
            GameInput.Instance.Flush();
            for (;;)
            {
                var wait = new WaitForKey();
                yield return wait;
                if (wait.Key == GameKey.Left && _starterIndex > 0) _starterIndex--;
                else if (wait.Key == GameKey.Right && _starterIndex < choices.Length - 1) _starterIndex++;
                else if (wait.Key == GameKey.Ok)
                {
                    string name = _data.GetSpecies(choices[_starterIndex]).Name;
                    yield return _ui.Choose(new[] { "예", "아니오" },
                        new MenuOptions { Rect = new Rect(UiKit.VirtualWidth - 120, 236, 112, 68), Prompt = $"{J(name, "으로", "로")} 할까요?" });
                    if (_ui.Choice == 0) break;
                }
            }
            _starterScreen = false;

            int starter = choices[_starterIndex];
            Result = PlayerState.NewGame(_data, starter);
            Result.Party[0].RollIndividualValues(_data);   // 진짜 새 개체이므로 여기서만 개체값을 굴린다
            yield return _ui.Say($"좋아! {J(_data.GetSpecies(starter).Name, "은", "는")} 멋진 파트너가 될 거야!\n몬스터볼 5개, 상처약 3개, 용돈도 챙겨 가렴.");
            yield return _ui.Say("시내 왼쪽 건물은 네잎클로버지역아동센터,\n오른쪽은 학생과학백화점이야. 가운데엔 중앙탑도 있지!");
            yield return _ui.Say("X 키로 메뉴를 열 수 있어. 충주 모험을 떠나자!");
        }

        /* ---------------------------------- 그리기 ---------------------------------- */

        void DrawScene()
        {
            if (_titleBackground == null) _titleBackground = BuildTitleBackground();
            if (_shadow == null) _shadow = BuildShadow();
            UiKit.FillScreen(Color.black);
            if (_starterScreen) DrawStarterScreen(); else DrawTitle();
        }

        void DrawTitle()
        {
            float t = Time.time * 1000f;
            UiKit.Texture(_titleBackground, 0, 0, 480, 320);
            for (int i = 0; i < 30; i++)
            {
                float x = ((float)TileArt.TileHash(i, 1) * 480f + t * .01f * (1f + (float)TileArt.TileHash(i, 2))) % 480f;
                float y = (float)TileArt.TileHash(i, 3) * 150f;
                UiKit.Fill(x, y, 2, 2, new Color(1, 1, 1, .3f + (float)TileArt.TileHash(i, 4) * .6f));
            }
            UiKit.Fill(0, 250, 480, 70, UiKit.C("#4aa848"));
            UiKit.Text("몬스터", 240, 46, 46, UiKit.C("#ffd84a"), TextAnchor.UpperCenter, true);
            UiKit.Text("어드벤처", 240, 96, 38, Color.white, TextAnchor.UpperCenter, true);
            UiKit.Text("- 충주 편 -", 240, 142, 15, UiKit.C("#9be0ff"), TextAnchor.UpperCenter, true);
            var parade = new[] { 0, 2, 4, 6, 10, 12 };
            for (int i = 0; i < parade.Length; i++)
            {
                float bob = Mathf.Abs(Mathf.Sin(t / 260f + i)) * 8f;
                MonsterArt.DrawAt(_data.GetSpecies(parade[i]), 50 + i * 76, 214 - bob, 22);
            }
        }

        void DrawStarterScreen()
        {
            float t = Time.time * 1000f;
            UiKit.Fill(0, 0, 480, 232, UiKit.C("#243060"));
            UiKit.Fill(0, 170, 480, 62, UiKit.C("#4aa848"));
            UiKit.Text("파트너를 선택하세요", 240, 12, 18, UiKit.C("#ffd84a"), TextAnchor.UpperCenter, true);
            var choices = PlayerState.Starters;
            for (int i = 0; i < choices.Length; i++)
            {
                float x = 90 + i * 150;
                bool on = i == _starterIndex;
                var sp = _data.GetSpecies(choices[i]);
                UiKit.Texture(_shadow, x - 52, 162, 104, 20);
                MonsterArt.DrawAt(sp, x, 128 - (on ? Mathf.Abs(Mathf.Sin(t / 200f)) * 10f : 0f), on ? 38 : 30, on ? 1f : .55f);
                UiKit.Text(sp.Name, x, 186, 14, on ? Color.white : UiKit.C("#8a92c0"), TextAnchor.UpperCenter, true);
                if (!on) continue;
                _ui.Chip(sp.Type, x - 17, 205);
                UiKit.Text("▼", x, 44, 16, UiKit.C("#ffd84a"), TextAnchor.UpperCenter);
            }
        }

        static Texture2D BuildTitleBackground()
        {
            var p = new Painter(480, 320);
            var stops = new[] { (0f, "#1e2a5a"), (.6f, "#4a5fa8"), (1f, "#6cc04a") };
            for (int y = 0; y < 320; y++)
            {
                float t = y / 320f;
                int i = t <= stops[1].Item1 ? 0 : 1;
                var (t0, c0) = stops[i]; var (t1, c1) = stops[i + 1];
                p.Rect(0, y, 480, 1, Color32.Lerp(Painter.Hex(c0), Painter.Hex(c1), Mathf.Clamp01((t - t0) / (t1 - t0))));
            }
            var tex = p.ToTexture();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        static Texture2D BuildShadow()
        {
            var p = new Painter(104, 20);
            p.Ellipse(52, 10, 52, 10, Painter.Hex("#000000", .3f));
            var tex = p.ToTexture();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
    }
}
