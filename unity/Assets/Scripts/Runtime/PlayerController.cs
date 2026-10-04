using System;
using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>한 칸씩 이동하는 플레이어(웹의 updateWorld). 이동 140ms, 막히면 방향만 바꾼다.</summary>
    public sealed class PlayerController : MonoBehaviour
    {
        const float StepSeconds = 0.14f;

        /// <summary>한 칸 이동을 마쳤을 때(맵 좌표).</summary>
        public event Action<int, int> Stepped;

        /// <summary>true 면 입력을 받지 않는다(전투·대화 중).</summary>
        public bool Locked { get; set; }

        public int TileX { get; private set; }
        public int TileY { get; private set; }
        public Direction Facing { get; private set; } = Direction.Down;
        public bool IsMoving { get; private set; }
        public Vector3 WorldPosition => transform.position;

        WorldMap _map;
        SpriteRenderer _renderer;
        Sprite[] _facingSprites;
        Vector3 _from, _to;
        float _elapsed;

        public void Init(WorldMap map, int x, int y, int color = PlayerLook.DefaultColor)
        {
            _map = map;
            _facingSprites = PlayerArt.Build(color);
            var go = new GameObject("Sprite", typeof(SpriteRenderer));
            go.transform.SetParent(transform, false);
            _renderer = go.GetComponent<SpriteRenderer>();
            _renderer.sortingOrder = WorldView.PlayerSortingOrder;
            Teleport(x, y, Direction.Down);
        }

        public void Teleport(int x, int y, Direction facing)
        {
            TileX = x; TileY = y; Facing = facing;
            IsMoving = false;
            transform.position = WorldView.TileCenter(x, y);
            _renderer.transform.localPosition = Vector3.zero;
            RefreshSprite();
        }

        void RefreshSprite() => _renderer.sprite = _facingSprites[(int)Facing];

        void Update()
        {
            if (IsMoving)
            {
                _elapsed += Time.deltaTime;
                float k = Mathf.Min(1f, _elapsed / StepSeconds);
                transform.position = Vector3.Lerp(_from, _to, k);
                // 걷는 동안 살짝 위아래로 흔들린다(웹: |sin(t/70)| * 2px)
                _renderer.transform.localPosition = new Vector3(0, Mathf.Abs(Mathf.Sin(Time.time * 1000f / 70f)) * 2f / 32f, 0);
                if (k >= 1f)
                {
                    float carry = _elapsed - StepSeconds;
                    IsMoving = false;
                    _renderer.transform.localPosition = Vector3.zero;
                    Stepped?.Invoke(TileX, TileY);
                    // 방향키를 계속 누르고 있으면 한 프레임도 쉬지 않고 바로 다음 칸으로 간다 — 예전엔 칸마다 한 프레임씩
                    // 멈춰서 걸음이 미세하게 끊겨 보였다(프레임이 낮은 폰일수록 심하다). 남은 시간(carry)도 이어서 쓴다.
                    if (!Locked && GameInput.Instance != null)
                    {
                        var held = GameInput.Instance.HeldDirection;
                        if (held.HasValue && TryStep(held.Value)) _elapsed = Mathf.Clamp(carry, 0f, StepSeconds * .5f);
                    }
                }
                return;
            }
            if (Locked || GameInput.Instance == null) return;
            var dir = GameInput.Instance.HeldDirection;
            if (dir.HasValue) TryStep(dir.Value);
        }

        /// <summary>한 칸 이동을 시도한다. 막혀 있으면 방향만 바꾸고 false.</summary>
        public bool TryStep(Direction dir)
        {
            if (IsMoving) return false;
            Facing = dir;
            RefreshSprite();
            int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0;
            int dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
            if (!_map.IsPassable(TileX + dx, TileY + dy)) return false;
            TileX += dx; TileY += dy;
            _from = transform.position;
            _to = WorldView.TileCenter(TileX, TileY);
            _elapsed = 0f;
            IsMoving = true;
            return true;
        }
    }

    /// <summary>웹의 drawPlayer 를 4방향 스프라이트로 옮긴 것. color 번호는 PlayerLook(0 = 기본 빨강)의 팔레트 칸이다.</summary>
    public static class PlayerArt
    {
        // (윗옷·모자, 바지). 바지는 윗옷과 섞여 보이지 않게 색마다 따로 골랐다.
        static readonly (string Top, string Pants)[] Palette =
        {
            ("#d84a4a", "#3a4a9a"),   // 0 빨강(기본)
            ("#3fa34d", "#3a3f5a"),   // 1 초록
            ("#3a7bd8", "#4a4a4a"),   // 2 파랑
            ("#8a5ad8", "#3a3a4a"),   // 3 보라
            ("#f0922a", "#3a4a9a"),   // 4 주황
            ("#27b5a6", "#4a3a3a"),   // 5 청록
            ("#ee6aa8", "#3a4a9a"),   // 6 분홍
            ("#e8c42a", "#3a4a9a"),   // 7 노랑
        };

        static readonly Sprite[][] Cache = new Sprite[PlayerLook.ColorCount][];

        public static Sprite[] Build(int color = PlayerLook.DefaultColor)
        {
            color = ((color % PlayerLook.ColorCount) + PlayerLook.ColorCount) % PlayerLook.ColorCount;
            if (Cache[color] != null && Cache[color][0] != null) return Cache[color];
            var sprites = new Sprite[4];
            foreach (Direction d in Enum.GetValues(typeof(Direction)))
            {
                var p = new Painter(32, 32);
                Draw(p, d, Palette[color]);
                sprites[(int)d] = p.ToSprite(32f, new Vector2(0.5f, 0.5f));
            }
            return Cache[color] = sprites;
        }

        static void Draw(Painter p, Direction dir, (string Top, string Pants) look)
        {
            Color32 C(string hex, float a = 1f) => Painter.Hex(hex, a);
            const float y = 1;      // 모자 윗부분이 잘리지 않도록 웹 좌표에서 1px 아래로
            var top = C(look.Top);
            p.Ellipse(16, y + 29, 10, 4, C("#000000", .25f));
            p.Rect(10, y + 22, 5, 7, C(look.Pants)); p.Rect(17, y + 22, 5, 7, C(look.Pants));
            p.Rect(8, y + 12, 16, 12, top);
            p.Ellipse(16, y + 10, 8, 8, C("#f6d2a8"));
            var dark = C("#222222");
            switch (dir)
            {
                case Direction.Down: p.Rect(12, y + 10, 2, 3, dark); p.Rect(18, y + 10, 2, 3, dark); break;
                case Direction.Left: p.Rect(10, y + 10, 2, 3, dark); break;
                case Direction.Right: p.Rect(20, y + 10, 2, 3, dark); break;
            }
            p.Rect(8, y + 1, 16, 5, C("#ffffff"));
            p.Rect(8, y - 1, 16, 4, top);
            if (dir == Direction.Down) p.Rect(8, y + 5, 16, 2, top);
            if (dir == Direction.Left) p.Rect(4, y + 4, 8, 2, top);
            if (dir == Direction.Right) p.Rect(20, y + 4, 8, 2, top);
        }
    }
}
