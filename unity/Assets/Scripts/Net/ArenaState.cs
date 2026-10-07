using System;
using System.Collections.Generic;
using System.Linq;
using MonsterAdventure.Core;

namespace MonsterAdventure.Net
{
    public enum NetDirection { Up, Down, Left, Right }

    public sealed class Trainer
    {
        public int Id; public string Name; public int SpeciesId; public int X; public int Y; public NetDirection Dir;
        public int Color;
        public TrainerInfo ToInfo() => new TrainerInfo { Id = Id, Name = Name, SpeciesId = SpeciesId, X = X, Y = Y, Dir = (int)Dir, Color = Color };
    }

    public readonly struct MoveOutcome
    {
        public readonly bool Moved; public readonly int X, Y; public readonly NetDirection Dir;
        public MoveOutcome(bool moved, int x, int y, NetDirection dir) { Moved = moved; X = x; Y = y; Dir = dir; }
    }

    /// <summary>
    /// 공유 맵 위에서 「누가 어디 있는지」와 「누가 누구에게 도전 중인지」를 판정하는 순수 규칙(소켓 없음).
    /// 서버(TcpArenaServer)는 이 클래스가 내린 판정을 그대로 방송한다.
    /// </summary>
    public sealed class ArenaState
    {
        readonly GameData _data;
        readonly WorldMap _map;
        readonly Dictionary<int, Trainer> _players = new Dictionary<int, Trainer>();
        readonly Dictionary<int, int> _pendingChallenges = new Dictionary<int, int>();   // targetId -> fromId
        readonly IRng _rng;
        int _nextId = 1;

        public ArenaState(GameData data, WorldMap map, IRng rng = null) { _data = data; _map = map; _rng = rng ?? new SystemRng(); }

        public IReadOnlyDictionary<int, Trainer> Players => _players;
        public Trainer Get(int id) => _players.TryGetValue(id, out var t) ? t : null;
        public IEnumerable<Trainer> Others(int exceptId) => _players.Values.Where(t => t.Id != exceptId);

        bool Occupied(int x, int y, int exceptId) => _players.Values.Any(t => t.Id != exceptId && t.X == x && t.Y == y);

        /// <summary>새 트레이너를 받아들인다. 종족은 존재하는 범위 안이면 무엇이든 허용한다(오래 키운 계정의 진화한 몬스터도 겉모습으로 쓸 수 있다).</summary>
        public Trainer Join(string name, int speciesId)
        {
            if (speciesId < 0 || speciesId >= _data.Species.Count)
                throw new ArgumentException($"존재하지 않는 종족이다: {speciesId}");
            var (x, y) = FindSpawn();
            var color = PlayerLook.PickColor(_players.Values.Select(p => p.Color).ToList(), _rng);   // 겹치지 않는 색(처음 들어온 사람은 기본 빨강)
            var t = new Trainer { Id = _nextId++, Name = string.IsNullOrWhiteSpace(name) ? "트레이너" : name, SpeciesId = speciesId, X = x, Y = y, Dir = NetDirection.Down, Color = color };
            _players[t.Id] = t;
            return t;
        }

        (int, int) FindSpawn()
        {
            int bx = WorldMap.VillageX, by = WorldMap.VillageY + 1;
            for (int r = 0; r < 24; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int x = bx + dx, y = by + dy;
                        if (_map.IsPassable(x, y) && !Occupied(x, y, -1)) return (x, y);
                    }
            return (bx, by); // 이론상 도달하지 않는다(맵이 그 정도로 꽉 찰 수 없다).
        }

        public void Leave(int id)
        {
            _players.Remove(id);
            var stale = _pendingChallenges.Where(kv => kv.Key == id || kv.Value == id).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _pendingChallenges.Remove(k);
        }

        /// <summary>한 칸 이동 시도. 막혀 있거나 다른 트레이너가 있으면 방향만 바꾸고 Moved=false.</summary>
        public MoveOutcome TryMove(int id, NetDirection dir)
        {
            var t = _players[id];
            t.Dir = dir;
            int dx = dir == NetDirection.Left ? -1 : dir == NetDirection.Right ? 1 : 0;
            int dy = dir == NetDirection.Up ? -1 : dir == NetDirection.Down ? 1 : 0;
            int nx = t.X + dx, ny = t.Y + dy;
            if (!_map.IsPassable(nx, ny) || Occupied(nx, ny, id)) return new MoveOutcome(false, t.X, t.Y, dir);
            t.X = nx; t.Y = ny;
            return new MoveOutcome(true, nx, ny, dir);
        }

        /// <summary>도전 신청. 안 되는 이유가 있으면 그 문구, 되면 null.
        /// 같은 방에 있으면 어디에 있든 신청할 수 있다(월드 메뉴의 "대결 신청" 목록) — 예전엔 바로 옆 칸까지 걸어가야만 했다.</summary>
        public string RequestChallenge(int fromId, int targetId)
        {
            if (fromId == targetId) return "자기 자신에게는 도전할 수 없다!";
            var from = Get(fromId); var target = Get(targetId);
            if (from == null || target == null) return "상대를 찾을 수 없다!";
            if (_pendingChallenges.ContainsValue(fromId)) return "이미 다른 도전에 응답을 기다리는 중이다!";
            _pendingChallenges[targetId] = fromId;
            return null;
        }

        /// <summary>응답. 이 대상에게 걸린 도전이 없으면 null.</summary>
        public (int FromId, bool Accepted)? Respond(int targetId, bool accept)
        {
            if (!_pendingChallenges.TryGetValue(targetId, out int fromId)) return null;
            _pendingChallenges.Remove(targetId);
            return (fromId, accept);
        }
    }
}
