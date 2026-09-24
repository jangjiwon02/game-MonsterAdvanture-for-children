using System.Collections.Generic;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 필드 위에 보이는 야생 몬스터 하나. 눈에 보이는 이 몬스터가 곧 전투 상대(<see cref="Monster"/>)다.
    /// OverworldSpawner 가 풀에서 꺼내 <see cref="Activate"/> 로 켜고, 다 쓰면 <see cref="Deactivate"/> 로 돌려준다.
    /// </summary>
    public sealed class WildActor : MonoBehaviour
    {
        public const int SortingOrder = 8;                 // 표지판(5) 위, 플레이어(10) 아래
        const float PixelsPerUnit = 112f;                  // 몸통(지름 88px)이 타일 한 칸의 약 0.8배
        const float FadeInSeconds = 0.45f, FadeOutSeconds = 0.3f;
        const float StepSeconds = 0.42f;                   // 플레이어(0.14s)보다 느긋하게
        const float BobHeight = 0.07f;

        static readonly Dictionary<int, Sprite> SpriteCache = new Dictionary<int, Sprite>();

        public int Id { get; private set; }
        public Monster Monster { get; private set; }
        public SpeciesData Species { get; private set; }
        /// <summary>논리 좌표(이동 중이면 도착할 칸).</summary>
        public int TileX { get; private set; }
        public int TileY { get; private set; }
        public bool IsMoving { get; private set; }
        public bool IsFadingOut { get; private set; }
        /// <summary>접촉·전투 중이라 디스폰하지 않는다.</summary>
        public bool Locked { get; set; }
        public float Alpha => _alpha;
        public float CooldownRemaining => Mathf.Max(0f, _cooldown);
        public Vector3 WorldPosition => transform.position;

        /// <summary>인접 상태였는지 — 접촉을 "인접해지는 순간 한 번"만 내기 위한 기록(스포너가 갱신).</summary>
        internal bool WasAdjacent;

        SpriteRenderer _renderer;
        Vector3 _from, _to;
        float _stepElapsed, _alpha, _cooldown, _wanderTimer, _bobPhase;
        bool _released;

        internal bool IsFree => _released;

        void Awake()
        {
            var go = new GameObject("Sprite", typeof(SpriteRenderer));
            go.transform.SetParent(transform, false);
            _renderer = go.GetComponent<SpriteRenderer>();
            _renderer.sortingOrder = SortingOrder;
            _released = true;
            gameObject.SetActive(false);
        }

        internal void Activate(int id, Monster monster, SpeciesData species, int x, int y, float firstWanderIn)
        {
            Id = id; Monster = monster; Species = species;
            TileX = x; TileY = y;
            IsMoving = false; IsFadingOut = false; Locked = false; WasAdjacent = false;
            _cooldown = 0f; _alpha = 0f; _released = false;
            _wanderTimer = firstWanderIn;
            _bobPhase = (id * 1.7f) % (Mathf.PI * 2f);
            name = "Wild " + species.Name + " #" + id;
            _renderer.sprite = GetSprite(species);
            transform.position = _to = _from = WorldView.TileCenter(x, y);
            _renderer.transform.localPosition = Vector3.zero;
            Apply();
            gameObject.SetActive(true);
        }

        internal void Deactivate()
        {
            _released = true;
            Monster = null; Species = null;
            gameObject.SetActive(false);
        }

        public void BeginFadeOut() => IsFadingOut = true;

        public void SetCooldown(float seconds) => _cooldown = Mathf.Max(_cooldown, seconds);

        /// <summary>한 칸 배회를 시작한다(이미 이동 중이면 무시).</summary>
        internal void StartStep(int nx, int ny)
        {
            if (IsMoving) return;
            TileX = nx; TileY = ny;
            _from = transform.position;
            _to = WorldView.TileCenter(nx, ny);
            _stepElapsed = 0f;
            IsMoving = true;
        }

        /// <summary>배회 대기 시간이 다 됐는지(스포너가 다음 걸음을 정하려고 부른다). 다 됐으면 타이머를 새로 잡는다.</summary>
        internal bool WanderDue(float nextDelay)
        {
            if (IsMoving || IsFadingOut || _wanderTimer > 0f) return false;
            _wanderTimer = nextDelay;
            return true;
        }

        /// <summary>시간을 dt 만큼 진행한다. Update 가 아니라 스포너가 부르므로 배치 모드에서도 돌릴 수 있다.</summary>
        internal void Advance(float dt)
        {
            if (_released) return;
            _cooldown -= dt;
            _wanderTimer -= dt;

            if (IsMoving)
            {
                _stepElapsed += dt;
                float k = Mathf.Clamp01(_stepElapsed / StepSeconds);
                transform.position = Vector3.Lerp(_from, _to, k * k * (3f - 2f * k));   // 부드러운 출발·정지
                if (k >= 1f) IsMoving = false;
            }

            if (IsFadingOut) _alpha = Mathf.Max(0f, _alpha - dt / FadeOutSeconds);
            else _alpha = Mathf.Min(1f, _alpha + dt / FadeInSeconds);
            Apply();
        }

        void Apply()
        {
            // 살짝 위아래로 숨쉬듯 흔들린다.
            float bob = Mathf.Abs(Mathf.Sin(Time.time * 4.2f + _bobPhase)) * BobHeight;
            _renderer.transform.localPosition = new Vector3(0f, bob, 0f);
            _renderer.color = new Color(1f, 1f, 1f, _alpha);
        }

        /// <summary>종족별 스프라이트 캐시. 몸통 중심이 피벗이라 타일 중심에 놓으면 그대로 맞는다.</summary>
        static Sprite GetSprite(SpeciesData sp)
        {
            if (SpriteCache.TryGetValue(sp.Id, out var s) && s != null) return s;
            var tex = MonsterArt.Get(sp);
            var pivot = new Vector2(MonsterArt.CenterX / tex.width, 1f - MonsterArt.CenterY / tex.height);
            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, PixelsPerUnit, 0, SpriteMeshType.FullRect);
            s.hideFlags = HideFlags.HideAndDontSave;
            SpriteCache[sp.Id] = s;
            return s;
        }
    }
}
