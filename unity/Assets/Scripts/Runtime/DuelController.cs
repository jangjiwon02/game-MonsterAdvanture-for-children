using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static MonsterAdventure.Core.Korean;

namespace MonsterAdventure
{
    /// <summary>
    /// LAN 대결 마당의 1:1 대결 화면. 규칙은 전부 서버(Duel, Core)가 판정하고, 여기서는 기술을 고르는 입력을
    /// 보내고 서버가 스트리밍해 주는 결과(DuelEvent/DuelEnded)를 받아 그대로 재생만 한다 — 로컬에는 판정 로직이 없다.
    /// </summary>
    public sealed class DuelController
    {
        readonly GameUi _ui;
        readonly GameData _data;
        readonly TcpArenaClient _client;
        readonly DuelStartMessage _start;
        readonly Queue<(string Type, JObject Data)> _incoming = new Queue<(string, JObject)>();

        int _youHp, _oppHp;
        int _shownSpeciesId, _shownLevel;   // 이겨서 성장하면(레벨업·진화) 여기가 바뀐다 — 화면에 보이는 "나"

        public bool IsDone { get; private set; }
        public bool YouWon { get; private set; }

        public DuelController(GameUi ui, GameData data, TcpArenaClient client, DuelStartMessage start)
        {
            _ui = ui; _data = data; _client = client; _start = start;
            _youHp = start.You.Hp; _oppHp = start.Opponent.Hp;
            _shownSpeciesId = start.You.SpeciesId; _shownLevel = start.You.Level;
        }

        string YouName => _data.GetSpecies(_shownSpeciesId).Name;

        /// <summary>서버가 보낸 duelEvent/duelEnded 메시지를 넘겨준다(ArenaController.Handle 에서 호출).</summary>
        public void Feed(string type, JObject data) => _incoming.Enqueue((type, data));

        public IEnumerator Run()
        {
            _ui.DrawScene = DrawDuel;
            _ui.AlwaysShowTextBox = true;
            yield return _ui.Say($"{_start.OpponentName}과의 대결이 시작되었다!", 900);

            while (!IsDone)
            {
                var labels = _start.You.Moves.Select(id =>
                {
                    var mv = _data.GetMove(id);
                    return $"{mv.Name}  {_data.Types.Find(t => t.Id == mv.Type).Name} {mv.Power}";
                }).ToList();
                yield return _ui.Choose(labels, new MenuOptions { Cols = 2, Rect = new Rect(8, 236, 464, 72), Full = true, Prompt = "" });
                _client.SendDuelAction(_start.You.Moves[_ui.Choice]);
                yield return _ui.Say("상대의 선택을 기다리는 중...", 300);

                while (_incoming.Count == 0 && !IsDone) yield return null;
                while (_incoming.Count > 0)
                {
                    var (type, d) = _incoming.Dequeue();
                    yield return PlayOne(type, d);
                }
            }

            _ui.DrawScene = null;
            _ui.AlwaysShowTextBox = false;
        }

        IEnumerator PlayOne(string type, JObject d)
        {
            if (type == NetMsgType.DuelEnded)
            {
                var ended = d.ToObject<DuelEndedMessage>();
                IsDone = true; YouWon = ended.YouWon;
                if (ended.OpponentLeft) { yield return _ui.Say("상대가 접속을 끊어서 기권승했다!", 1300); yield break; }
                yield return _ui.Say(ended.YouWon ? "대결에서 이겼다!" : "대결에서 졌다...", 1300);
                if (ended.ExpGained > 0)   // 친선 대결이라 진 쪽도(승자보다는 적지만) 참가 경험치를 받는다.
                {
                    yield return _ui.Say($"{J(YouName, "은", "는")} 경험치를 {ended.ExpGained} 얻었다!", 800);
                    foreach (var g in ended.Growth ?? new List<GrowthEvent>()) yield return PlayGrowth(g);
                }
                yield break;
            }

            var e = d.ToObject<DuelEventMessage>();
            bool aboutMe = e.Side == 0;
            string who = aboutMe ? "나" : _start.OpponentName;

            if (e.Kind == NetMsgType.Duel.MoveUsed)
            {
                yield return _ui.Say($"{who}의 {_data.GetMove(e.MoveId).Name}!", 500);
            }
            else if (e.Kind == NetMsgType.Duel.Missed)
            {
                yield return _ui.Say("그러나 빗나갔다!", 700);
            }
            else if (e.Kind == NetMsgType.Duel.Damage)
            {
                if (aboutMe) _youHp = Mathf.Max(0, _youHp - e.Amount); else _oppHp = Mathf.Max(0, _oppHp - e.Amount);
                if (e.Critical) yield return _ui.Say("급소에 맞았다!", 600);
                if (e.Multiplier > 1) yield return _ui.Say("효과는 굉장했다!", 700);
                else if (e.Multiplier < 1) yield return _ui.Say("효과가 별로인 듯하다...", 700);
            }
            else if (e.Kind == NetMsgType.Duel.Fainted)
            {
                yield return _ui.Say(aboutMe ? "내 몬스터가 쓰러졌다!" : $"{_start.OpponentName}의 몬스터가 쓰러졌다!", 800);
            }
        }

        /// <summary>단일 플레이 전투(BattleController.PlayGrowth)와 같은 문구·순서로 재생한다.</summary>
        IEnumerator PlayGrowth(GrowthEvent g)
        {
            switch (g.Kind)
            {
                case GrowthKind.LevelUp:
                    _shownLevel = g.Level;
                    yield return _ui.Say($"{J(YouName, "은", "는")} 레벨 {g.Level}(으)로 올랐다!", 900);
                    break;
                case GrowthKind.LearnedMove:
                    yield return _ui.Say($"{J(YouName, "은", "는")} {J(_data.GetMove(g.MoveId).Name, "을", "를")} 배웠다!", 900);
                    break;
                case GrowthKind.ReplacedMove:
                    yield return _ui.Say($"{J(YouName, "은", "는")} {J(_data.GetMove(g.ForgottenMoveId).Name, "을", "를")} 잊고\n{J(_data.GetMove(g.MoveId).Name, "을", "를")} 배웠다!", 1100);
                    break;
                case GrowthKind.Evolved:
                {
                    string before = YouName;
                    yield return _ui.Say($"어라...? {before}의 모습이...!", 1000);
                    yield return GameUi.Tween(.9f, p => _ui.Flash = Mathf.Abs(Mathf.Sin(p * Mathf.PI * 4f)) * .9f);
                    _shownSpeciesId = g.ToSpeciesId;
                    _ui.Flash = 0f;
                    yield return _ui.Say($"{J(before, "은", "는")} {J(YouName, "으로", "로")} 진화했다!", 1200);
                    break;
                }
            }
        }

        /* ---------------------------------- 그리기(간단한 1:1 대결 화면) ---------------------------------- */

        void DrawDuel()
        {
            UiKit.FillScreen(Color.black);
            UiKit.Fill(0, 0, 480, 232, UiKit.C("#8fd0ff"));
            UiKit.Fill(0, 150, 480, 82, UiKit.C("#78c05a"));

            var you = _data.GetSpecies(_shownSpeciesId);
            var opp = _data.GetSpecies(_start.Opponent.SpeciesId);
            MonsterArt.DrawAt(opp, 355, 108, 34);
            MonsterArt.DrawAt(you, 125, 176, 44);

            DrawInfo(opp, _start.Opponent.Level, _oppHp, _start.Opponent.MaxHp, 14, 14, 208, false, _start.OpponentName);
            DrawInfo(you, _shownLevel, _youHp, _start.You.MaxHp, 258, 158, 214, true, "나");
        }

        void DrawInfo(SpeciesData sp, int level, int hp, int maxHp, float x, float y, float w, bool mine, string label)
        {
            UiKit.Box(x, y, w, mine ? 64 : 50, UiKit.C("#1b2340"), UiKit.C("#d8dcf0"));
            UiKit.Text($"{label} · {sp.Name}", x + 10, y + 7, 13, bold: true);
            UiKit.Text($"Lv.{level}", x + w - 10, y + 8, 12, UiKit.C("#cfd6f5"), TextAnchor.UpperRight);
            float f = maxHp > 0 ? (float)hp / maxHp : 0f;
            UiKit.Text("HP", x + 52, y + 26, 10, UiKit.C("#ffd84a"), bold: true);
            UiKit.Bar(x + 70, y + 27, w - 82, 8, f, UiKit.HpColor(f));
            if (mine) UiKit.Text($"{hp} / {maxHp}", x + w - 10, y + 38, 11, Color.white, TextAnchor.UpperRight);
        }
    }
}
