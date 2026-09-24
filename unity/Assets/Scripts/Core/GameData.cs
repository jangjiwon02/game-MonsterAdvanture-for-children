using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace MonsterAdventure.Core
{
    // data/game-data.json 과 1:1 대응 (Newtonsoft는 대소문자를 구분하지 않고 바인딩한다)
    public sealed class ElementData { public string Id; public string Name; public string Color; }

    public sealed class MoveData
    {
        public string Id; public string Name; public string Type;
        public int Power; public int Accuracy;
    }

    public sealed class BaseStats { public int Hp; public int Atk; public int Def; public int Spd; }
    public sealed class LearnEntry { public int Level; public string Move; }
    public sealed class EvolveInfo { public int Level; public int To; }

    public sealed class SpeciesData
    {
        public int Id; public string Name; public string Type; public string Look; public int Stage;
        public string Color; public string Belly;
        public BaseStats BaseStats;
        public EvolveInfo Evolve;
        public double CatchRate; public int BaseExp;
        public List<LearnEntry> Learnset;
    }

    public sealed class GameData
    {
        public List<ElementData> Types;
        public Dictionary<string, Dictionary<string, double>> TypeChart;
        public List<MoveData> Moves;
        public List<SpeciesData> Species;

        Dictionary<string, MoveData> _moveById;

        public static GameData Parse(string json)
        {
            var data = JsonConvert.DeserializeObject<GameData>(json)
                       ?? throw new InvalidOperationException("game-data.json 이 비어 있다.");
            data.Index();
            return data;
        }

        void Index()
        {
            _moveById = Moves.ToDictionary(m => m.Id);
            for (int i = 0; i < Species.Count; i++)
                if (Species[i].Id != i) throw new InvalidOperationException($"종족 id가 배열 순서와 다르다: [{i}] = {Species[i].Id}");
        }

        public SpeciesData GetSpecies(int id) => Species[id];
        public MoveData GetMove(string id) => _moveById[id];
        public bool HasMove(string id) => _moveById.ContainsKey(id);

        /// <summary>기본형(1단계) 종족들. 야생 조우의 후보 풀.</summary>
        public IEnumerable<SpeciesData> BaseForms => Species.Where(s => s.Stage == 1);

        /// <summary>typeChart[공격][방어], 표에 없으면 1.</summary>
        public double TypeMultiplier(string attackType, string defenseType) =>
            TypeChart.TryGetValue(attackType, out var row) && row.TryGetValue(defenseType, out var mult) ? mult : 1.0;
    }
}
