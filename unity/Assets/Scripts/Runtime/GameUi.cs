using System;
using System.Collections;
using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    public sealed class MenuOptions
    {
        public int Cols = 1;
        public float W = 150;
        public float Y = 8;
        public Rect? Rect;            // 지정하면 위치·크기를 그대로 쓴다(가상 화면 좌표)
        public int Start;
        public bool Cancel;           // X 로 닫을 수 있는지 (닫으면 Choice = -1)
        public string Prompt;         // 메뉴가 떠 있는 동안 텍스트 박스에 보일 문구
        public bool Full;             // 텍스트 박스를 전체 너비로
        public int MaxRows;
        public ISet<int> Disabled;    // 회색으로 보이고 선택되지 않는 항목(예: 저장이 없을 때의 '이어하기')
    }

    public sealed class PartyScreenOptions
    {
        public string Title = "몬스터";
        public int Start;
        public int Mark = -1;                                       // '출전 중' 표시
        public bool Cancel = true;
        public Func<Monster, int, string> Filter;                   // 못 고르는 이유(null 이면 선택 가능)
    }

    /// <summary>
    /// 웹의 say / choose / partyScreen 과 페이드·플래시. 모든 흐름은 코루틴으로 쓴다:
    /// <c>yield return ui.Say("...");</c>, <c>yield return ui.Choose(items, opts); int i = ui.Choice;</c>
    /// </summary>
    public sealed class GameUi : MonoBehaviour
    {
        const float CharsPerSecond = 55f;       // 웹: 0.055자/ms

        public GameData Data { get; set; }
        public float Fade;                     // 0~1 검은 막
        public float Flash;                     // 0~1 흰 막
        public bool AlwaysShowTextBox;          // 전투 중에는 대사가 없어도 박스를 보인다
        public Action DrawScene;                // 텍스트 박스·메뉴 아래에 깔리는 화면(전투 등)
        public int Choice { get; private set; }

        // 대사
        string _text = "";
        float _shown, _autoMs, _autoElapsedMs, _textWidth = 456f;
        bool _sayActive;
        List<string> _lines;
        string _linesFor;
        float _linesWidth;

        // 메뉴
        IList<string> _items;
        MenuOptions _menu;
        int _menuIndex;

        // 파티 화면
        Action _overlay;

        /// <summary>파티·상세 같은 전체 화면이 떠 있는지(월드 HUD 를 숨기는 데 쓴다).</summary>
        public bool OverlayActive => _overlay != null;

        public bool IsBusy => _sayActive || _menu != null || _overlay != null;

        void Update()
        {
            if (!_sayActive) return;
            _shown = Mathf.Min(_text.Length, _shown + Time.deltaTime * CharsPerSecond);
            if (_shown >= _text.Length && _autoMs > 0f)
            {
                _autoElapsedMs += Time.deltaTime * 1000f;
                if (_autoElapsedMs >= _autoMs) _sayActive = false;
            }
        }

        public static IEnumerator Tween(float seconds, Action<float> apply)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                apply(t / seconds);
                yield return null;
            }
            apply(1f);
        }

        public static IEnumerator Wait(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
        }

        /// <summary>대사 한 줄. Z/Enter 로 넘기고(타이핑 중이면 먼저 전부 보여줌), autoMs 가 있으면 자동으로 넘어간다.</summary>
        public IEnumerator Say(string text, float autoMs = 0f)
        {
            GameInput.Instance.Flush();
            _text = text; _shown = 0f; _autoMs = autoMs; _autoElapsedMs = 0f;
            _textWidth = _menu != null && !_menu.Full ? 220f : 456f;
            _sayActive = true;
            while (_sayActive)
            {
                if (GameInput.Instance.TryDequeue(out var k) && (k == GameKey.Ok || k == GameKey.Cancel))
                {
                    if (_shown < _text.Length) _shown = _text.Length; else _sayActive = false;
                }
                yield return null;
            }
            _text = ""; _shown = 0f;
        }

        public IEnumerator Choose(IList<string> items, MenuOptions o = null)
        {
            o = o ?? new MenuOptions();
            GameInput.Instance.Flush();
            _items = items; _menu = o; _menuIndex = o.Start;
            if (o.Prompt != null)
            {
                _text = o.Prompt; _shown = _text.Length; _textWidth = o.Full ? 456f : 220f;
            }
            int n = items.Count, cols = o.Cols;
            for (;;)
            {
                var wait = new WaitForKey();
                yield return wait;
                var k = wait.Key;
                int i = _menuIndex;
                if (k == GameKey.Up) i = i - cols >= 0 ? i - cols : i;
                else if (k == GameKey.Down) i = i + cols < n ? i + cols : i;
                else if (k == GameKey.Left && cols > 1) i = i % cols > 0 ? i - 1 : i;
                else if (k == GameKey.Right && cols > 1) i = (i % cols < cols - 1 && i + 1 < n) ? i + 1 : i;
                else if (k == GameKey.Ok)
                {
                    if (o.Disabled != null && o.Disabled.Contains(_menuIndex)) continue;
                    Sfx.Play(SfxKind.Select);
                    Choice = _menuIndex; break;
                }
                else if (k == GameKey.Cancel && o.Cancel) { Choice = -1; break; }
                if (k != GameKey.Ok && k != GameKey.Cancel) Sfx.Play(SfxKind.Move);
                _menuIndex = i;
            }
            _menu = null; _items = null;
            if (o.Prompt != null) { _text = ""; _shown = 0f; }
        }

        /// <summary>파티 화면에서 몬스터를 고른다. 결과는 Choice(취소면 -1).</summary>
        public IEnumerator ChooseParty(IList<Monster> party, PartyScreenOptions o)
        {
            int idx = Mathf.Clamp(o.Start, 0, party.Count - 1);
            GameInput.Instance.Flush();
            _overlay = () => DrawPartyScreen(party, o, idx);
            int result;
            for (;;)
            {
                var wait = new WaitForKey();
                yield return wait;
                var k = wait.Key;
                if (k == GameKey.Up && idx - 2 >= 0) idx -= 2;
                else if (k == GameKey.Down && idx + 2 < party.Count) idx += 2;
                else if (k == GameKey.Left && idx % 2 == 1) idx--;
                else if (k == GameKey.Right && idx % 2 == 0 && idx + 1 < party.Count) idx++;
                else if (k == GameKey.Ok)
                {
                    string why = o.Filter?.Invoke(party[idx], idx);
                    if (why != null) { yield return Say(why); continue; }
                    Sfx.Play(SfxKind.Select);
                    result = idx; break;
                }
                else if (k == GameKey.Cancel && o.Cancel) { result = -1; break; }
                if (k <= GameKey.Right) Sfx.Play(SfxKind.Move);     // 클로저가 idx 를 참조하므로 다시 그릴 때 자동으로 반영된다.
            }
            _overlay = null;
            Choice = result;
        }

        /// <summary>몬스터 상세 화면(능력치·경험치·진화 정보·기술 4개). Z/X 로 닫는다.</summary>
        public IEnumerator ShowSummary(Monster m)
        {
            GameInput.Instance.Flush();
            _overlay = () => DrawSummary(m);
            for (;;)
            {
                var wait = new WaitForKey();
                yield return wait;
                if (wait.Key == GameKey.Ok || wait.Key == GameKey.Cancel) break;
            }
            _overlay = null;
        }

        static Texture2D _summaryShadow;

        void DrawSummary(Monster m)
        {
            var sp = m.Species(Data);
            if (_summaryShadow == null)
            {
                var p = new Painter(160, 32);
                p.Ellipse(80, 16, 80, 16, Painter.Hex("#000000", .35f));
                _summaryShadow = p.ToTexture();
                _summaryShadow.filterMode = FilterMode.Bilinear;
            }
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, UiKit.C("#1e2748"));
            UiKit.Texture(_summaryShadow, 30, 174, 160, 32);
            MonsterArt.DrawAt(sp, 110, 130, 56);
            UiKit.Text(sp.Name, 210, 14, 22, bold: true);
            UiKit.Text($"Lv.{m.Level}", 210, 44, 14, UiKit.C("#cfd6f5"));
            Chip(sp.Type, 270, 46);
            UiKit.Text($"HP  {m.Hp} / {m.MaxHp}", 210, 70, 13);
            float f = (float)m.Hp / m.MaxHp;
            UiKit.Bar(210, 88, 240, 8, f, UiKit.HpColor(f));
            UiKit.Text($"공격 {m.Attack}   방어 {m.Defense}   스피드 {m.Speed}", 210, 104, 13);
            UiKit.Text("경험치", 210, 128, 12, UiKit.C("#cfd6f5"));
            UiKit.Bar(258, 131, 192, 7, (float)m.Exp / Growth.ExpToNext(m.Level), UiKit.C("#58a8f8"));
            if (sp.Evolve != null)
                UiKit.Text($"Lv.{sp.Evolve.Level}에 {Data.GetSpecies(sp.Evolve.To).Name}(으)로 진화", 210, 146, 12, UiKit.C("#ffd84a"));
            int ivTotal = m.IVs.Hp + m.IVs.Atk + m.IVs.Def + m.IVs.Spd;
            UiKit.Text($"성격: {NatureInfo.DisplayName(m.Nature)}   개체값 {ivTotal}/{Monster.MaxIv * 4}", 210, 164, 11, UiKit.C("#9aa2d0"));
            for (int i = 0; i < m.Moves.Count; i++)
            {
                var mv = Data.GetMove(m.Moves[i]);
                float x = 12 + (i % 2) * 234, y = 206 + (i / 2) * 50;
                UiKit.Box(x, y, 224, 44, UiKit.C("#1b2340"), UiKit.C(Data.Types.Find(t => t.Id == mv.Type).Color));
                UiKit.Text(mv.Name, x + 12, y + 8, 14, bold: true);
                Chip(mv.Type, x + 12, y + 26);
                UiKit.Text($"위력 {mv.Power}  명중 {mv.Accuracy}", x + 212, y + 27, 12, UiKit.C("#cfd6f5"), TextAnchor.UpperRight);
            }
            UiKit.Text("X: 뒤로", UiKit.VirtualWidth - 12, UiKit.VirtualHeight - 26, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperRight);
        }

        /// <summary>몬스터 도감: 종족 16종 목록(잡은 것만 이름이 보이고, 못 잡은 건 "???") → 하나 고르면
        /// 종류·진화·레벨별로 배우는 기술 전체를 보여준다(개체별 현재 상태가 아니라 종족 자체의 참고 자료).
        /// 싱글플레이·LAN 양쪽 월드 메뉴가 그대로 같이 쓴다(PlayerState 만 다르게 넘기면 된다).</summary>
        public IEnumerator DexScreen(PlayerState state)
        {
            int start = 0;
            for (;;)
            {
                var labels = new string[Data.Species.Count];
                for (int i = 0; i < labels.Length; i++)
                {
                    var sp = Data.GetSpecies(i);
                    labels[i] = state.Dex.Contains(i) ? $"No.{i:00}  {sp.Name}" : $"No.{i:00}  ???";
                }
                yield return Choose(labels, new MenuOptions
                {
                    Rect = new Rect(120, 8, 240, 208), MaxRows = 7, Start = start, Cancel = true,
                    Prompt = $"도감 {state.Dex.Count}/{Data.Species.Count}",
                });
                int i2 = Choice;
                if (i2 < 0) yield break;
                start = i2;
                yield return ShowDexEntry(Data.GetSpecies(i2), state.Dex.Contains(i2));
            }
        }

        IEnumerator ShowDexEntry(SpeciesData sp, bool caught)
        {
            GameInput.Instance.Flush();
            _overlay = () => DrawDexEntry(sp, caught);
            for (;;)
            {
                var wait = new WaitForKey();
                yield return wait;
                if (wait.Key == GameKey.Ok || wait.Key == GameKey.Cancel) break;
            }
            _overlay = null;
        }

        void DrawDexEntry(SpeciesData sp, bool caught)
        {
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, UiKit.C("#1e2748"));
            UiKit.Text($"No.{sp.Id:00}", 14, 14, 13, UiKit.C("#cfd6f5"));

            if (!caught)
            {
                UiKit.Text("???", 110, 100, 30, UiKit.C("#5a6280"), TextAnchor.UpperCenter, true);
                UiKit.Text("아직 잡지 못했다.", 14, 150, 14, UiKit.C("#8a92c0"));
                UiKit.Text("X/Z: 목록으로", UiKit.VirtualWidth - 12, UiKit.VirtualHeight - 26, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperRight);
                return;
            }

            MonsterArt.DrawAt(sp, 90, 110, 50);
            UiKit.Text(sp.Name, 180, 14, 22, bold: true);
            Chip(sp.Type, 180, 46);
            if (sp.Evolve != null)
                UiKit.Text($"Lv.{sp.Evolve.Level}에 {Data.GetSpecies(sp.Evolve.To).Name}(으)로 진화", 180, 68, 12, UiKit.C("#ffd84a"));
            else
                UiKit.Text("더 진화하지 않는다.", 180, 68, 12, UiKit.C("#8a92c0"));

            UiKit.Text("레벨별로 배우는 기술", 14, 178, 13, UiKit.C("#cfd6f5"), bold: true);
            var ls = sp.Learnset;
            for (int i = 0; i < ls.Count; i++)
            {
                var mv = Data.GetMove(ls[i].Move);
                float x = 14 + (i % 2) * 234, y = 198 + (i / 2) * 46;
                UiKit.Box(x, y, 224, 42, UiKit.C("#1b2340"), UiKit.C(Data.Types.Find(t => t.Id == mv.Type).Color));
                UiKit.Text($"Lv.{ls[i].Level}", x + 10, y + 5, 11, UiKit.C("#ffd84a"), bold: true);
                UiKit.Text(mv.Name, x + 58, y + 5, 13, bold: true);
                UiKit.Text($"위력 {mv.Power}  명중 {mv.Accuracy}", x + 212, y + 24, 11, UiKit.C("#cfd6f5"), TextAnchor.UpperRight);
            }
            UiKit.Text("X/Z: 목록으로", UiKit.VirtualWidth - 12, UiKit.VirtualHeight - 26, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperRight);
        }

        /// <summary>플레이어가 이름 등을 직접 타이핑해서 넣는다. 터치 기기는 OS 키보드, 데스크톱은 자체 입력창을 그린다.
        /// 빈 문자열도 그대로 돌려준다(빈 값 처리는 호출한 쪽이 한다).</summary>
        public IEnumerator EnterText(string prompt, int maxLength, Action<string> onDone)
        {
            // TouchScreenKeyboard.isSupported 는 터치 지원 데스크톱(터치스크린 노트북 등)에서도 true 가 될 수 있어서,
            // "실제 모바일 플랫폼인가"로 더 명확하게 나눈다 — Windows/Mac 데스크톱은 항상 자체 입력창을 쓴다.
            if (Application.isMobilePlatform && TouchScreenKeyboard.isSupported)
            {
                var kb = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default, false, false, false, false, prompt);
                while (kb != null && kb.status == TouchScreenKeyboard.Status.Visible) yield return null;
                string result = kb?.text ?? "";
                onDone(result.Length > maxLength ? result.Substring(0, maxLength) : result);
                yield break;
            }

            GameInput.Instance.Flush();
            string text = "";
            bool done = false;
            _overlay = () => DrawTextEntry(prompt, text);
            // 이 코루틴이 시작되는 바로 그 프레임에는, 방금 이전 메뉴를 확정한 Enter 의 '\r' 이
            // Input.inputString 에 아직 남아 있다(그 값은 프레임 단위라 다음 프레임이 되어야 비워진다).
            // 한 프레임 건너뛰지 않으면 타이핑을 시작하기도 전에 빈 이름으로 즉시 확정돼 버린다.
            yield return null;
            while (!done)
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (text.Length > 0) text = text.Substring(0, text.Length - 1); }
                    else if (c == '\n' || c == '\r') done = true;
                    else if (!char.IsControl(c) && text.Length < maxLength) text += c;
                }
                // Cancel(X/Esc) 로도 빈 값으로 확정할 수 있게(막다른 길 방지).
                if (GameInput.Instance.TryDequeue(out var k) && k == GameKey.Cancel) done = true;
                if (!done) yield return null;
            }
            _overlay = null;
            onDone(text);
        }

        /* ---------------------------------- 그리기 ---------------------------------- */

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            UiKit.Begin();
            DrawScene?.Invoke();
            _overlay?.Invoke();
            DrawTextBox();
            DrawMenu();
            if (Flash > 0f) UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, new Color(1, 1, 1, Flash));
            if (Fade > 0f) UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, new Color(0, 0, 0, Fade));
        }

        void EnsureWrapped()
        {
            if (_lines != null && _linesFor == _text && Mathf.Approximately(_linesWidth, _textWidth)) return;
            _lines = Wrap(_text, _textWidth - 16f);
            _linesFor = _text; _linesWidth = _textWidth;
        }

        static List<string> Wrap(string s, float width)
        {
            var lines = new List<string>();
            string cur = "";
            foreach (char ch in s)
            {
                if (ch == '\n') { lines.Add(cur); cur = ""; continue; }
                if (UiKit.MeasureWidth(cur + ch, 15) > width) { lines.Add(cur); cur = ch.ToString(); }
                else cur += ch;
            }
            lines.Add(cur);
            return lines;
        }

        void DrawTextBox()
        {
            if (_text.Length == 0 && !AlwaysShowTextBox) return;
            UiKit.Box(8, 232, 464, 80);
            if (_text.Length == 0) return;
            EnsureWrapped();
            int left = Mathf.FloorToInt(_shown);
            for (int i = 0; i < _lines.Count && left > 0; i++)
            {
                string ln = _lines[i];
                UiKit.Text(ln.Substring(0, Mathf.Min(ln.Length, left)), 22, 244 + i * 22, 15);
                left -= ln.Length;
            }
            if (_sayActive && _shown >= _text.Length && _autoMs <= 0f && Mathf.FloorToInt(Time.time * 1000f / 350f) % 2 == 1)
                UiKit.Text("▼", 452, 292, 12);
        }

        void DrawMenu()
        {
            if (_menu == null) return;
            var o = _menu;
            int n = _items.Count, cols = o.Cols;
            int rows = Mathf.CeilToInt(n / (float)cols);
            int maxRows = o.MaxRows > 0 ? o.MaxRows : rows;
            int vis = Mathf.Min(rows, maxRows);
            Rect rect = o.Rect ?? new Rect(UiKit.VirtualWidth - o.W - 8, o.Y, o.W, vis * 26 + 16);
            UiKit.Box(rect.x, rect.y, rect.width, rect.height);
            int cur = _menuIndex / cols;
            int top = Mathf.Max(0, Mathf.Min(cur - vis + 1, rows - vis));
            float cw = (rect.width - 24f) / cols;
            for (int i = 0; i < n; i++)
            {
                int r = i / cols - top;
                if (r < 0 || r >= vis) continue;
                float cx = rect.x + 16 + (i % cols) * cw, cy = rect.y + 10 + r * 26;
                bool off = o.Disabled != null && o.Disabled.Contains(i);
                UiKit.Text(_items[i], cx + 14, cy + 2, 14, off ? UiKit.C("#7a7f9a") : Color.white);
                if (i == _menuIndex) UiKit.Text("▶", cx, cy + 3, 12, UiKit.C("#ffd84a"));
            }
        }

        void DrawPartyScreen(IList<Monster> party, PartyScreenOptions o, int idx)
        {
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, UiKit.C("#1e2748"));
            UiKit.Text(o.Title, 12, 10, 16, UiKit.C("#ffd84a"), bold: true);
            for (int i = 0; i < party.Count; i++)
            {
                var m = party[i];
                var sp = m.Species(Data);
                float x = 8 + (i % 2) * 238, y = 34 + (i / 2) * 84;
                bool sel = i == idx;
                UiKit.Box(x, y, 226, 78, UiKit.C(sel ? "#33427a" : "#1b2340"), UiKit.C(sel ? "#ffd84a" : "#8a92c0"));
                MonsterArt.DrawAt(sp, x + 34, y + 44, 17, m.Hp > 0 ? 1f : .45f);
                UiKit.Text(sp.Name, x + 68, y + 8, 15, bold: true);
                UiKit.Text($"Lv.{m.Level}", x + 200, y + 9, 12, UiKit.C("#cfd6f5"), TextAnchor.UpperRight);
                Chip(sp.Type, x + 68, y + 30);
                float f = (float)m.Hp / m.MaxHp;
                UiKit.Bar(x + 68, y + 50, 140, 8, f, UiKit.HpColor(f));
                UiKit.Text(m.Hp > 0 ? $"{m.Hp} / {m.MaxHp}" : "기절", x + 208, y + 60, 11,
                           m.Hp > 0 ? Color.white : UiKit.C("#ff8a8a"), TextAnchor.UpperRight);
                if (o.Mark == i) UiKit.Text("출전 중", x + 110, y + 31, 11, UiKit.C("#ffd84a"));
            }
            UiKit.Text("Z: 선택   X: 뒤로", UiKit.VirtualWidth - 12, 12, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperRight);
        }

        /// <summary>데스크톱용 텍스트 입력창(가운데 프롬프트 + 입력 박스 + 깜빡이는 커서).</summary>
        void DrawTextEntry(string prompt, string text)
        {
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, UiKit.VirtualWidth, UiKit.VirtualHeight, UiKit.C("#1e2748"));
            UiKit.Text(prompt, UiKit.VirtualWidth / 2f, 100, 16, UiKit.C("#ffd84a"), TextAnchor.UpperCenter, true);
            float boxW = 280f, boxX = UiKit.VirtualWidth / 2f - boxW / 2f;
            UiKit.Box(boxX, 150, boxW, 40, UiKit.C("#0a0d1a"), Color.white);
            bool blink = Mathf.FloorToInt(Time.time * 2f) % 2 == 0;
            string shown = text + (blink ? "▏" : "");
            UiKit.Text(shown, boxX + 12, 162, 16, Color.white);
            UiKit.Text("Enter: 확정   Esc: 취소(빈 값)", UiKit.VirtualWidth / 2f, 210, 11, UiKit.C("#9aa2d0"), TextAnchor.UpperCenter);
        }

        /// <summary>타입 칩(34x14).</summary>
        public void Chip(string typeId, float x, float y)
        {
            var t = Data.Types.Find(e => e.Id == typeId);
            UiKit.Fill(x, y, 34, 14, UiKit.C(t.Color));
            UiKit.Text(t.Name, x + 17, y + 1, 10, Color.white, TextAnchor.UpperCenter, true);
        }
    }
}
