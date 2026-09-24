using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterAdventure.Core;
using UnityEngine;
using static MonsterAdventure.Core.Korean;

namespace MonsterAdventure
{
    /// <summary>
    /// 야생 전투 한 판의 진행과 연출. 규칙은 전부 Core 의 BattleSession 이 계산하고,
    /// 여기서는 그 이벤트를 하나씩 받아 웹 버전과 같은 문구·애니메이션으로 재생한다.
    /// </summary>
    public sealed class BattleController
    {
        struct Vis
        {
            public float X, Y, A, S;
            public bool Blink;
            public static Vis Default => new Vis { A = 1f, S = 1f };
        }

        readonly MonoBehaviour _host;
        readonly GameUi _ui;
        readonly GameData _data;
        readonly PlayerState _state;
        readonly IRng _rng;

        BattleSession _s;
        BattleAction _picked;
        Vis _ve = Vis.Default, _vp = Vis.Default;
        float _dhpEnemy, _dhpPlayer;                 // 화면에 보이는 HP(모델 값을 따라 천천히 움직인다)
        int _shownSpecies, _shownLevel;              // 성장 이벤트를 재생하기 전까지 화면에 보이는 아군 종족·레벨
        bool _afterFaint;
        bool _ballVisible; Vector2 _ballPos; float _ballRot;

        static Texture2D _background, _ball;

        public BattleOutcome Outcome => _s.Outcome;

        public BattleController(MonoBehaviour host, GameUi ui, GameData data, PlayerState state, IRng rng)
        {
            _host = host; _ui = ui; _data = data; _state = state; _rng = rng;
        }

        string PlayerName => _data.GetSpecies(_shownSpecies).Name;
        string EnemyName => _s.Enemy.Species(_data).Name;
        string TypeName(string id) => _data.Types.Find(t => t.Id == id).Name;

        /// <summary>전투가 끝나면 화면은 검게 덮인 채(Fade = 1)로 돌아온다. 이후 처리는 호출한 쪽이 한다.</summary>
        public IEnumerator Run(Monster enemy)
        {
            _s = new BattleSession(_data, _state, enemy, _rng);
            _dhpEnemy = enemy.Hp;
            _dhpPlayer = _s.Active.Hp;
            _shownSpecies = _s.Active.SpeciesId; _shownLevel = _s.Active.Level;
            _ve = Vis.Default; _vp = Vis.Default;
            _ballVisible = false; _afterFaint = false;
            _ui.Data = _data;

            // 흰 점멸 → 검게 → 전투 화면 → 밝게
            Sfx.Play(SfxKind.Encounter);
            yield return GameUi.Tween(.65f, p => _ui.Flash = Mathf.FloorToInt(p * 6) % 2 == 0 ? .8f : 0f);
            _ui.Flash = 0f;
            yield return GameUi.Tween(.3f, p => _ui.Fade = p);
            _ui.DrawScene = DrawBattle;
            _ui.AlwaysShowTextBox = true;
            _ve.X = 160f; _vp.X = -160f;
            yield return GameUi.Tween(.3f, p => _ui.Fade = 1f - p);
            _host.StartCoroutine(GameUi.Tween(.6f, p => { _ve.X = 160f * (1f - p); _vp.X = -160f * (1f - p); }));
            yield return GameUi.Wait(.6f);

            yield return _ui.Say($"앗! 야생의 {J(EnemyName, "이", "가")} 나타났다!", 900);
            _vp.S = 0f;
            yield return _ui.Say($"가라! {PlayerName}!", 500);
            yield return GameUi.Tween(.3f, p => _vp.S = p);

            while (!_s.IsOver)
            {
                yield return PickAction();
                if (_picked == null) continue;
                yield return PlayTurn(_picked);
            }

            yield return GameUi.Tween(.3f, p => _ui.Fade = p);
            _ui.DrawScene = null;
            _ui.AlwaysShowTextBox = false;
        }

        /* ---------------------------------- 행동 선택 ---------------------------------- */

        IEnumerator PickAction()
        {
            _picked = null;
            var pm = _s.Active;
            yield return _ui.Choose(new[] { "싸운다", "가방", "몬스터", "도망친다" },
                new MenuOptions { Cols = 2, Rect = new Rect(240, 236, 228, 72), Prompt = $"{J(pm.Species(_data).Name, "은", "는")} 무엇을 할까?" });
            int a = _ui.Choice;

            if (a == 0)
            {
                var labels = pm.Moves.Select(id => { var mv = _data.GetMove(id); return $"{mv.Name}  {TypeName(mv.Type)} {mv.Power}"; }).ToList();
                yield return _ui.Choose(labels, new MenuOptions { Cols = 2, Rect = new Rect(8, 236, 464, 72), Cancel = true, Prompt = "", Full = true });
                if (_ui.Choice >= 0) _picked = new MoveAction(pm.Moves[_ui.Choice]);
            }
            else if (a == 1)
            {
                yield return _ui.Choose(new[] { $"몬스터볼 x{_state.Balls}", $"상처약 x{_state.Potions}", "취소" },
                    new MenuOptions { Rect = new Rect(240, 150, 228, 82), Cancel = true, Prompt = "" });
                int i = _ui.Choice;
                if (i < 0 || i == 2) yield break;
                if (i == 0)
                {
                    if (_state.Balls <= 0) { yield return _ui.Say("몬스터볼이 없다!"); yield break; }
                    _picked = new BallAction();
                }
                else
                {
                    if (_state.Potions <= 0) { yield return _ui.Say("상처약이 없다!"); yield break; }
                    yield return _ui.ChooseParty(_state.Party, new PartyScreenOptions
                    {
                        Title = "누구에게 사용할까요?", Mark = _s.ActiveIndex,
                        Filter = (m, _) => m.Hp <= 0 ? "기절한 몬스터에게는 쓸 수 없다!" : m.Hp >= m.MaxHp ? "이미 HP가 가득 찼다!" : null,
                    });
                    if (_ui.Choice >= 0) _picked = new PotionAction(_ui.Choice);
                }
            }
            else if (a == 2)
            {
                yield return _ui.ChooseParty(_state.Party, new PartyScreenOptions
                {
                    Title = "누구로 교체할까요?", Mark = _s.ActiveIndex, Start = _s.ActiveIndex,
                    Filter = (m, i) => m.Hp <= 0 ? "기절한 몬스터는 싸울 수 없다!" : i == _s.ActiveIndex ? "이미 싸우고 있다!" : null,
                });
                if (_ui.Choice >= 0) _picked = new SwitchAction(_ui.Choice);
            }
            else _picked = new FleeAction();
        }

        /* ---------------------------------- 이벤트 재생 ---------------------------------- */

        IEnumerator PlayTurn(BattleAction action)
        {
            var it = _s.ResolveTurn(action).GetEnumerator();
            for (;;)
            {
                if (!it.MoveNext()) yield break;
                yield return PlayEvent(it.Current);
            }
        }

        IEnumerator PlayEvent(BattleEvent e)
        {
            switch (e.Kind)
            {
                case BattleEventKind.MoveUsed:
                {
                    string who = e.Side == Side.Player ? PlayerName : $"야생의 {EnemyName}";
                    yield return _ui.Say($"{who}의 {_data.GetMove(e.MoveId).Name}!", 500);
                    break;
                }
                case BattleEventKind.Missed:
                    yield return _ui.Say("그러나 빗나갔다!", 700);
                    break;

                case BattleEventKind.Damage:
                {
                    // e.Side 는 맞은 쪽. 공격한 쪽이 상대를 향해 26px 돌진했다가 돌아온다.
                    bool playerAttacks = e.Side == Side.Enemy;
                    float dir = playerAttacks ? 1f : -1f;
                    yield return GameUi.Tween(.13f, p => SetX(playerAttacks, dir * 26f * p));
                    _host.StartCoroutine(GameUi.Tween(.15f, p => SetX(playerAttacks, dir * 26f * (1f - p))));
                    Sfx.Play(e.Critical ? SfxKind.Crit : SfxKind.Hit);
                    SetBlink(!playerAttacks, true);
                    yield return GameUi.Wait(.45f);
                    SetBlink(!playerAttacks, false);
                    yield return GameUi.Wait(.3f);
                    if (e.Critical) yield return _ui.Say("급소에 맞았다!", 600);
                    if (e.Multiplier > 1) yield return _ui.Say("효과는 굉장했다!", 700);
                    else if (e.Multiplier < 1) yield return _ui.Say("효과가 별로인 듯하다...", 700);
                    break;
                }

                case BattleEventKind.Fainted:
                {
                    bool enemy = e.Side == Side.Enemy;
                    Sfx.Play(SfxKind.Faint);
                    yield return GameUi.Tween(.5f, p => SetFaint(enemy, p));
                    yield return _ui.Say(enemy ? $"야생의 {J(EnemyName, "은", "는")} 쓰러졌다!" : $"{J(PlayerName, "은", "는")} 쓰러졌다!", 800);
                    break;
                }
                case BattleEventKind.BlackedOut:
                    yield return _ui.Say("눈앞이 캄캄해졌다...", 900);
                    break;

                case BattleEventKind.ExpGained:
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} 경험치를 {e.Amount} 얻었다!", 800);
                    break;
                case BattleEventKind.Growth:
                    yield return PlayGrowth(e.Growth);
                    break;
                case BattleEventKind.MoneyGained:
                    yield return _ui.Say($"₩{e.Amount}을(를) 얻었다!", 900);
                    break;

                case BattleEventKind.SwitchOut:
                    yield return _ui.Say($"돌아와, {PlayerName}!", 400);
                    yield return GameUi.Tween(.25f, p => _vp.S = 1f - p);
                    break;
                case BattleEventKind.ReplacementNeeded:
                {
                    var party = _state.Party;
                    yield return _ui.ChooseParty(party, new PartyScreenOptions
                    {
                        Title = "다음 몬스터를 고르세요", Cancel = false, Start = _state.FirstUsableIndex,
                        Filter = (m, _) => m.Hp <= 0 ? "기절한 몬스터는 싸울 수 없다!" : null,
                    });
                    _s.ChooseReplacement(_ui.Choice);
                    _afterFaint = true;
                    break;
                }
                case BattleEventKind.SwitchIn:
                {
                    var m = _s.Active;
                    _shownSpecies = m.SpeciesId; _shownLevel = m.Level;
                    _dhpPlayer = m.Hp;
                    _vp = Vis.Default; _vp.S = 0f;
                    yield return _ui.Say($"가라! {PlayerName}!", _afterFaint ? 500 : 400);
                    _afterFaint = false;
                    yield return GameUi.Tween(.3f, p => _vp.S = p);
                    break;
                }

                case BattleEventKind.PotionUsed:
                    Sfx.Play(SfxKind.Heal);
                    yield return _ui.Say($"{_state.Party[e.PartyIndex].Species(_data).Name}의 HP가 회복되었다!", 700);
                    break;

                case BattleEventKind.BallThrown:
                    yield return ThrowBall();
                    break;
                case BattleEventKind.CatchAttempt:
                    yield return ShakeBall(e.Catch);
                    break;
                case BattleEventKind.Caught:
                    Sfx.Play(SfxKind.Catch);
                    yield return _ui.Say($"신난다! 야생의 {J(EnemyName, "을", "를")} 잡았다!", 1000);
                    _ballVisible = false;
                    if (e.SentToBox) yield return _ui.Say($"파티가 가득 차서 {J(EnemyName, "은", "는")}\n박스로 전송되었다.", 1000);
                    break;

                case BattleEventKind.FleeSucceeded:
                    yield return _ui.Say("무사히 도망쳤다!", 800);
                    break;
                case BattleEventKind.FleeFailed:
                    yield return _ui.Say("도망칠 수 없었다!", 800);
                    break;
            }
        }

        IEnumerator PlayGrowth(GrowthEvent g)
        {
            switch (g.Kind)
            {
                case GrowthKind.LevelUp:
                    _shownLevel = g.Level;
                    Sfx.Play(SfxKind.LevelUp);
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} 레벨 {g.Level}(으)로 올랐다!", 900);
                    break;
                case GrowthKind.LearnedMove:
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} {J(_data.GetMove(g.MoveId).Name, "을", "를")} 배웠다!", 900);
                    break;
                case GrowthKind.ReplacedMove:
                    yield return _ui.Say($"{J(PlayerName, "은", "는")} {J(_data.GetMove(g.ForgottenMoveId).Name, "을", "를")} 잊고\n{J(_data.GetMove(g.MoveId).Name, "을", "를")} 배웠다!", 1100);
                    break;
                case GrowthKind.Evolved:
                {
                    string before = PlayerName;
                    yield return _ui.Say($"어라...? {before}의 모습이...!", 1000);
                    yield return GameUi.Tween(.9f, p => _ui.Flash = Mathf.Abs(Mathf.Sin(p * Mathf.PI * 4f)) * .9f);
                    _shownSpecies = g.ToSpeciesId;
                    _ui.Flash = 0f;
                    Sfx.Play(SfxKind.LevelUp);
                    yield return _ui.Say($"{J(before, "은", "는")} {J(PlayerName, "으로", "로")} 진화했다!", 1200);
                    break;
                }
            }
        }

        IEnumerator ThrowBall()
        {
            yield return _ui.Say("몬스터볼을 던졌다!", 300);
            Vector2 from = new Vector2(110, 190), to = new Vector2(355, 108);
            _ballVisible = true; _ballPos = from; _ballRot = 0f;
            yield return GameUi.Tween(.6f, p =>
            {
                _ballPos = new Vector2(Mathf.Lerp(from.x, to.x, p), Mathf.Lerp(from.y, to.y, p) - Mathf.Sin(p * Mathf.PI) * 70f);
                _ballRot = p * 12f;
            });
            yield return GameUi.Tween(.25f, p => { _ve.S = 1f - p; _ve.A = 1f - p * .5f; });
            yield return GameUi.Tween(.3f, p => _ballPos = new Vector2(to.x, to.y + 28f * p));
        }

        IEnumerator ShakeBall(CatchResult r)
        {
            for (int i = 0; i < r.Shakes; i++)
            {
                yield return GameUi.Wait(.35f);
                Sfx.Play(SfxKind.Shake);
                yield return GameUi.Tween(.3f, q => _ballRot = Mathf.Sin(q * Mathf.PI * 2f) * .5f);
            }
            yield return GameUi.Wait(.35f);
            if (r.Caught) yield break;
            _ballVisible = false; _ve.S = 1f; _ve.A = 1f;
            yield return _ui.Say(new[] { "앗! 몬스터가 볼에서 빠져나왔다!", "아깝다! 거의 잡을 뻔했는데!", "아쉽다! 조금만 더 하면 됐는데!" }[r.Shakes], 900);
        }

        void SetX(bool player, float x) { if (player) _vp.X = x; else _ve.X = x; }
        void SetBlink(bool player, bool on) { if (player) _vp.Blink = on; else _ve.Blink = on; }
        void SetFaint(bool enemy, float p)
        {
            if (enemy) { _ve.Y = 50f * p; _ve.A = 1f - p; }
            else { _vp.Y = 50f * p; _vp.A = 1f - p; }
        }

        /* ---------------------------------- 그리기 ---------------------------------- */

        void DrawBattle()
        {
            EnsureArt();
            float dt = Time.deltaTime;
            _dhpEnemy = Approach(_dhpEnemy, _s.Enemy.Hp, _s.Enemy.MaxHp, dt);
            _dhpPlayer = Approach(_dhpPlayer, _s.Active.Hp, _s.Active.MaxHp, dt);

            UiKit.FillScreen(Color.black);
            UiKit.Texture(_background, 0, 0, 480, 232);

            float t = Time.time * 1000f;
            float bobE = Mathf.Sin(t / 450f) * 2f, bobP = Mathf.Sin(t / 450f + 2f) * 2f;
            var enemySp = _s.Enemy.Species(_data);
            var playerSp = _data.GetSpecies(_shownSpecies);
            MonsterArt.DrawAt(enemySp, 355 + _ve.X, 108 + _ve.Y + bobE, 34 * _ve.S, _ve.A * BlinkAlpha(_ve, t));
            MonsterArt.DrawAt(playerSp, 125 + _vp.X, 176 + _vp.Y + bobP, 44 * _vp.S, _vp.A * BlinkAlpha(_vp, t));

            if (_ballVisible) DrawBall();

            DrawInfo(enemySp, _s.Enemy.Level, _dhpEnemy, _s.Enemy.MaxHp, 14, 14, 208, false, 0);
            DrawInfo(playerSp, _shownLevel, _dhpPlayer, _s.Active.MaxHp, 258, 158, 214, true, _s.Active.Exp);
        }

        static float BlinkAlpha(Vis v, float tMs) => v.Blink && Mathf.FloorToInt(tMs / 60f) % 2 == 1 ? .15f : 1f;

        static float Approach(float shown, int target, int max, float dt)
        {
            float speed = Mathf.Max(max * .5f, 18f) * dt;
            return shown > target ? Mathf.Max(target, shown - speed) : Mathf.Min(target, shown + speed);
        }

        void DrawInfo(SpeciesData sp, int level, float dhp, int maxHp, float x, float y, float w, bool player, int exp)
        {
            UiKit.Box(x, y, w, player ? 64 : 50, UiKit.C("#1b2340"), UiKit.C("#d8dcf0"));
            UiKit.Text(sp.Name, x + 10, y + 7, 14, bold: true);
            UiKit.Text($"Lv.{level}", x + w - 10, y + 8, 12, UiKit.C("#cfd6f5"), TextAnchor.UpperRight);
            _ui.Chip(sp.Type, x + 10, y + 25);
            float f = dhp / maxHp;
            UiKit.Text("HP", x + 52, y + 26, 10, UiKit.C("#ffd84a"), bold: true);
            UiKit.Bar(x + 70, y + 27, w - 82, 8, f, UiKit.HpColor(f));
            if (!player) return;
            UiKit.Text($"{Mathf.CeilToInt(dhp)} / {maxHp}", x + w - 10, y + 38, 11, Color.white, TextAnchor.UpperRight);
            UiKit.Bar(x + 10, y + 54, w - 20, 4, (float)exp / Growth.ExpToNext(level), UiKit.C("#58a8f8"));
        }

        void DrawBall()
        {
            var pivot = new Vector2(UiKit.Offset.x + _ballPos.x * UiKit.Scale, UiKit.Offset.y + _ballPos.y * UiKit.Scale);
            var saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(_ballRot * Mathf.Rad2Deg, pivot);
            UiKit.Texture(_ball, _ballPos.x - 12, _ballPos.y - 12, 24, 24);
            GUI.matrix = saved;
        }

        /* ---------------------------------- 전투 그림(코드로 생성) ---------------------------------- */

        static void EnsureArt()
        {
            if (_background == null) _background = BuildBackground();
            if (_ball == null) _ball = BuildBall();
        }

        static Texture2D BuildBackground()
        {
            var p = new Painter(480, 232);
            var stops = new[] { (0f, "#8fd0ff"), (.55f, "#dff3ff"), (.56f, "#8fd070"), (1f, "#5aa848") };
            for (int y = 0; y < 232; y++)
            {
                float t = y / 232f;
                int i = 0;
                while (i < stops.Length - 2 && t > stops[i + 1].Item1) i++;
                var (t0, c0) = stops[i]; var (t1, c1) = stops[i + 1];
                var col = Color32.Lerp(Painter.Hex(c0), Painter.Hex(c1), Mathf.Clamp01((t - t0) / (t1 - t0)));
                p.Rect(0, y, 480, 1, col);
            }
            p.Ellipse(355, 138, 88, 17, Painter.Hex("#5ea648")); p.Ellipse(355, 136, 84, 14, Painter.Hex("#78c05a"));
            p.Ellipse(125, 218, 100, 18, Painter.Hex("#5ea648")); p.Ellipse(125, 216, 95, 15, Painter.Hex("#78c05a"));
            var tex = p.ToTexture();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        static Texture2D BuildBall()
        {
            var p = new Painter(24, 24);
            Color32 red = Painter.Hex("#e04040"), white = Painter.Hex("#f4f4f4"), dark = Painter.Hex("#222222");
            for (int y = 0; y < 24; y++)
                for (int x = 0; x < 24; x++)
                {
                    float dx = x + .5f - 12f, dy = y + .5f - 12f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > 11f) continue;
                    p.Plot(x, y, dy < 0 ? red : white);
                    if (Mathf.Abs(dy) <= 1.5f) p.Plot(x, y, dark);
                    if (d <= 5f) p.Plot(x, y, dark);
                    if (d <= 3.6f) p.Plot(x, y, Painter.Hex("#ffffff"));
                }
            return p.ToTexture();
        }
    }
}
