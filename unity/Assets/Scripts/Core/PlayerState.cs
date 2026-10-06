using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace MonsterAdventure.Core
{
    /// <summary>SPEC "저장 항목": party, box, balls, potions, money, dex, 플레이어 위치.</summary>
    public sealed class PlayerState
    {
        public const int StartLevel = 5;
        public const int PotionHeal = 30;

        /// <summary>시작 파트너 후보: 불꼬마 / 물방울이 / 새싹이.</summary>
        public static readonly int[] Starters = { 0, 2, 4 };

        public List<Monster> Party = new List<Monster>();
        public List<Monster> Box = new List<Monster>();
        public int Balls = 5;
        public int Potions = 3;
        public int Money = 300;
        public HashSet<int> Dex = new HashSet<int>();
        public int X = WorldMap.VillageX;
        public int Y = WorldMap.VillageY + 1;
        /// <summary>X, Y 가 어느 좌표계의 값인지(WorldMap.Revision). 0 = 북쪽 확장 이전 저장 — 불러올 때 SaveSerializer 가 y 를 옮겨 준다.</summary>
        public int MapRevision;

        /// <summary>마지막 접속 시각(UTC). 서버 출석 보너스 판정용.</summary>
        public DateTime LastLoginUtc = DateTime.MinValue;

        /// <summary>수학 퀴즈: 마지막으로 푼 날(QuizGate.DayKey, UTC). 0 이면 아직 없음.</summary>
        public int QuizDay;
        /// <summary>QuizDay 날의 시도 횟수.</summary>
        public int QuizAttempts;
        /// <summary>오답 후 이 시각(UTC)부터 다시 도전할 수 있다.</summary>
        public DateTime QuizRetryUtc = DateTime.MinValue;

        /// <summary>원격 진행상황 기록(TelemetryClient)에 쓰는, 명단에서 고른 플레이어 이름. 예전 저장엔 없을 수 있다.</summary>
        public string PlayerName = "";
        /// <summary>지금까지 실제로 켜 두고 플레이한 총 시간(초). 원격 진행상황 기록용.</summary>
        public double TotalPlaySeconds;

        public static PlayerState NewGame(GameData data, int starterSpeciesId)
        {
            var s = new PlayerState();
            s.Party.Add(Monster.Create(data, starterSpeciesId, StartLevel));
            s.Dex.Add(starterSpeciesId);
            return s;
        }

        [JsonIgnore] public bool HasUsableMonster => Party.Any(m => !m.IsFainted);
        [JsonIgnore] public int FirstUsableIndex => Party.FindIndex(m => !m.IsFainted);

        public void HealAll() { foreach (var m in Party) m.FullHeal(); }

        /// <summary>파티의 i번째를 맨 앞(선두)으로 올린다. 선두가 전투에 먼저 나간다.</summary>
        public void MoveToFront(int index)
        {
            if (index <= 0 || index >= Party.Count) return;
            var m = Party[index];
            Party.RemoveAt(index);
            Party.Insert(0, m);
        }

        /// <summary>전멸 시(SPEC): 소지금 절반, 전원 회복, 마을 시작 지점으로 복귀.</summary>
        public void ApplyDefeat()
        {
            Money /= 2;
            HealAll();
            X = WorldMap.VillageX;
            Y = WorldMap.VillageY + 1;
        }

        /// <summary>잡은 몬스터 등록. 파티가 가득 차면 박스로 가고 true(=박스행)를 돌려준다.</summary>
        public bool AddCaught(Monster m)
        {
            Dex.Add(m.SpeciesId);
            if (Party.Count < Capture.MaxPartySize) { Party.Add(m); return false; }
            Box.Add(m);
            return true;
        }
    }
}
