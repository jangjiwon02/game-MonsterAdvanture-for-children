using System.Collections;
using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// "나는 누구인가" — 일반 게임과 LAN 대결 마당이 같은 이름(그래서 같은 저장·같은 진행상황)을 쓰도록 이름 고르기를 한곳에 둔다.
    /// 이름은 PlayerState.PlayerName 에 저장되고, 원격 접속 기록(TelemetryClient)과 LAN 접속 이름에도 그대로 쓰인다.
    /// </summary>
    public static class PlayerIdentity
    {
        const string NotOnRosterLabel = "명단에 없음(직접 입력)";

        /// <summary>state.PlayerName 이 비어 있으면(첫 실행이거나 예전 저장) 명단에서 고르게 하고 저장에 남긴다.
        /// 명단에 없는 사람은 "직접 입력"을 골라 이름을 타이핑하되, 실제 학생 이름과 안 겹치도록 기기ID 일부를 붙인다
        /// (스프레드시트의 최신현황 탭은 이름 하나당 한 줄만 유지하므로, 겹치면 서로 덮어쓴다).</summary>
        public static IEnumerator EnsureName(GameUi ui, PlayerState state)
        {
            if (!string.IsNullOrEmpty(state.PlayerName)) yield break;

            var names = new List<string> { Roster.Teacher };
            names.AddRange(Roster.Students);
            names.Add(NotOnRosterLabel);
            yield return ui.Say("진행상황 기록을 위해 명단에서 이름을 골라 주세요.");
            yield return ui.Choose(names, new MenuOptions
            {
                Rect = new Rect(UiKit.VirtualWidth / 2f - 140, 70, 280, 7 * 26 + 16),
                Cols = 2, Full = true, Prompt = "누구인가요?",
            });
            int choice = Mathf.Max(0, ui.Choice);

            if (names[choice] == NotOnRosterLabel)
            {
                string typed = null;
                yield return ui.EnterText("이름을 입력하세요", 10, n => typed = n?.Trim());
                string tag = TelemetryClient.DeviceId.Substring(0, 6);
                state.PlayerName = string.IsNullOrEmpty(typed) ? $"손님({tag})" : $"{typed}({tag})";
            }
            else
            {
                state.PlayerName = names[choice];
            }
        }
    }
}
