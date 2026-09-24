using System;
using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 플레이어 반경 안의 풀숲에 야생 몬스터를 보이게 띄우고(오브젝트 풀링), 가까이 가면 <see cref="Contact"/> 로 알린다.
    /// 무엇을 어디에 둘지는 순수 로직 <see cref="SpawnPlanner"/> 가 정하고, 여기서는 그 결과를 액터로 옮기기만 한다.
    /// 눈에 보이는 몬스터가 곧 전투 상대다(스폰 시점에 WildEncounter.Generate 로 정해 액터가 들고 있다).
    /// </summary>
    public sealed class OverworldSpawner : MonoBehaviour
    {
        /// <summary>플레이어와 액터가 서로 인접해진 순간 한 번. 이 시점에 스포너는 이미 <see cref="Paused"/> 다.</summary>
        public event Action<WildActor> Contact;

        public SpawnConfig Config = new SpawnConfig();

        /// <summary>null 이 아니면, 이 함수가 false 인 동안은 접촉을 판정하지 않는다(문 이벤트·메뉴 중 등). 예: () => !InScene</summary>
        public Func<bool> ContactGate;

        public bool Paused { get; private set; }
        public IReadOnlyList<WildActor> Actors => _live;
        public int ActiveCount => _live.Count;
        /// <summary>지금까지 실제로 만든 액터 수(풀 크기). 스폰마다 늘지 않는다는 걸 확인하는 용도.</summary>
        public int PooledTotal => _all.Count;

        /// <summary>false 로 하면 모든 액터를 풀에 돌려주고 아무것도 하지 않는다. 기존 14% 풀숲 조우와 스위치로 쓴다.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value) ReleaseAll();
                else _tick = 0f;
            }
        }

        WorldMap _map;
        GameData _data;
        PlayerController _player;
        IRng _rng;
        bool _enabled = true, _inited;
        float _tick, _hold;
        int _nextId = 1;
        Transform _root;
        readonly List<WildActor> _live = new List<WildActor>();
        readonly List<WildActor> _all = new List<WildActor>();
        readonly Stack<WildActor> _free = new Stack<WildActor>();

        public void Init(WorldMap map, GameData data, PlayerController player, IRng rng)
        {
            if (_inited && _player != null) _player.Stepped -= OnPlayerStepped;
            _map = map; _data = data; _player = player; _rng = rng;
            if (_root == null) _root = new GameObject("Wild Actors").transform;
            _player.Stepped += OnPlayerStepped;
            _inited = true;
        }

        void OnDestroy()
        {
            if (_player != null) _player.Stepped -= OnPlayerStepped;
            if (_root != null) Destroy(_root.gameObject);
        }

        void Update() => Advance(Time.deltaTime);

        /// <summary>시간을 dt 만큼 진행한다(Update 가 부른다. 배치 모드 테스트에서는 직접 부를 수 있다).</summary>
        public void Advance(float dt)
        {
            if (!_enabled || !_inited) return;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var a = _live[i];
                a.Advance(dt);
                if (a.IsFadingOut && a.Alpha <= 0f) Release(i);
            }

            if (Paused) return;

            _hold -= dt;
            _tick -= dt;
            if (_tick <= 0f) { _tick += Config.TickSeconds; if (_tick < 0f) _tick = 0f; Evaluate(); }

            WanderAll();
            CheckContacts();
        }

        /// <summary>접촉 후 전투 등이 진행되는 동안 스폰·배회·접촉 판정을 멈춘다(이미 보이는 액터는 그대로 남는다).</summary>
        public void Pause() => Paused = true;

        /// <summary>전투가 끝나 필드로 돌아왔을 때. 접촉해 있던 액터의 잠금을 푼다.</summary>
        public void Resume()
        {
            Paused = false;
            foreach (var a in _live) a.Locked = false;
        }

        /// <summary>타일 (x, y) 에 (사라지는 중이 아닌) 액터가 있으면 돌려준다. 이동 중이면 도착할 칸 기준.</summary>
        public bool TryGetActorAt(int x, int y, out WildActor actor)
        {
            foreach (var a in _live)
                if (!a.IsFadingOut && a.TileX == x && a.TileY == y) { actor = a; return true; }
            actor = null;
            return false;
        }

        /// <summary>승리·포획으로 액터를 없앤다(서서히 사라진 뒤 풀로 돌아간다). 한동안 새 스폰도 쉰다.</summary>
        public void Despawn(WildActor actor)
        {
            if (actor == null || actor.IsFree || actor.IsFadingOut) return;
            actor.Locked = false;
            actor.BeginFadeOut();
            _hold = Config.RespawnDelaySeconds;
        }

        /// <summary>도망 등으로 남겨 둘 때, 이 시간 동안은 다시 접촉하지 않는다.</summary>
        public void SetCooldown(WildActor actor, float seconds)
        {
            if (actor != null && !actor.IsFree) actor.SetCooldown(seconds);
        }

        /* ---------------------------------- 내부 ---------------------------------- */

        void Evaluate()
        {
            var points = new List<SpawnPoint>(_live.Count);
            foreach (var a in _live)
                if (!a.IsFadingOut) points.Add(new SpawnPoint(a.Id, a.TileX, a.TileY, a.Locked));

            var plan = SpawnPlanner.Plan(_map, _player.TileX, _player.TileY, points, Config, _rng);

            foreach (int id in plan.DespawnIds)
                foreach (var a in _live)
                    if (a.Id == id) { a.BeginFadeOut(); break; }

            if (_hold > 0f) return;      // 방금 전투로 하나 없앴으면 잠시 쉰다
            foreach (var (x, y) in plan.Spawns) Spawn(x, y);
        }

        void Spawn(int x, int y)
        {
            var wild = WildEncounter.Generate(_data, x, y, _rng);
            wild.RollIndividualValues(_data, _rng);      // 개체값은 스폰 때 정해져, 눈에 보이는 개체가 곧 싸울 개체다
            var actor = Rent();
            actor.Activate(_nextId++, wild, wild.Species(_data), x, y, 1f + 3f * (float)_rng.NextDouble());
            _live.Add(actor);
        }

        WildActor Rent()
        {
            WildActor a;
            if (_free.Count > 0) a = _free.Pop();
            else
            {
                var go = new GameObject("WildActor", typeof(WildActor));
                go.transform.SetParent(_root, false);
                a = go.GetComponent<WildActor>();
                _all.Add(a);
            }
            return a;
        }

        void Release(int liveIndex)
        {
            var a = _live[liveIndex];
            _live.RemoveAt(liveIndex);
            a.Deactivate();
            _free.Push(a);
        }

        void ReleaseAll()
        {
            for (int i = _live.Count - 1; i >= 0; i--) Release(i);
        }

        void WanderAll()
        {
            List<(int X, int Y)> occupied = null;
            foreach (var a in _live)
            {
                if (a.Locked || !a.WanderDue(1.5f + 2.5f * (float)_rng.NextDouble())) continue;
                if (occupied == null)
                {
                    occupied = new List<(int X, int Y)>(_live.Count);
                    foreach (var o in _live) occupied.Add((o.TileX, o.TileY));
                }
                if (SpawnPlanner.TryPickWander(_map, a.TileX, a.TileY, _player.TileX, _player.TileY, occupied, _rng, out var t))
                {
                    occupied.Remove((a.TileX, a.TileY));
                    occupied.Add(t);
                    a.StartStep(t.X, t.Y);
                }
            }
        }

        void OnPlayerStepped(int x, int y)
        {
            if (_enabled && !Paused) CheckContacts();
        }

        /// <summary>플레이어와 인접해진 액터가 있으면 한 번만 알린다. 둘 다 걸음을 마친 뒤에 판정해 화면상으로도 붙은 순간에 난다.</summary>
        void CheckContacts()
        {
            if (Paused || _player == null || _player.Locked || _player.IsMoving) return;
            if (ContactGate != null && !ContactGate()) return;

            foreach (var a in _live)
            {
                if (a.IsFadingOut || a.Alpha < 0.5f) continue;
                bool adj = SpawnPlanner.IsAdjacent(a.TileX, a.TileY, _player.TileX, _player.TileY);
                if (!adj) { a.WasAdjacent = false; continue; }
                if (a.WasAdjacent || a.IsMoving || a.CooldownRemaining > 0f) continue;

                a.WasAdjacent = true;
                a.Locked = true;
                Paused = true;
                Contact?.Invoke(a);
                return;
            }
        }
    }
}
