using MonsterAdventure.Core;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>지도 위의 다른 접속자. PlayerController와 달리 입력을 받지 않고, 서버가 알려준 자리로만 움직인다.</summary>
    public sealed class ArenaAvatar : MonoBehaviour
    {
        const float StepSeconds = 0.14f;   // PlayerController 와 같은 보폭(같은 화면 안에서 속도가 맞아야 자연스럽다)

        public int Id { get; private set; }
        public string DisplayName { get; private set; }
        public int TileX { get; private set; }
        public int TileY { get; private set; }

        SpriteRenderer _renderer;
        Sprite[] _facingSprites;
        TextMesh _nameTag;
        Vector3 _from, _to;
        float _elapsed;
        bool _moving;

        public void Init(int id, string name, int x, int y, Direction facing, int color = 0)
        {
            Id = id;
            DisplayName = name;
            _facingSprites = PlayerArt.Build(color);
            var go = new GameObject("Sprite", typeof(SpriteRenderer));
            go.transform.SetParent(transform, false);
            _renderer = go.GetComponent<SpriteRenderer>();
            _renderer.sortingOrder = WorldView.PlayerSortingOrder;
            // 예전엔 살짝 푸른 색조로만 「다른 사람」을 구분했는데, 이제 사람마다 옷 색이 달라서 색조는 필요 없다.

            var tagGo = new GameObject("NameTag", typeof(TextMesh));
            tagGo.transform.SetParent(transform, false);
            _nameTag = tagGo.GetComponent<TextMesh>();
            _nameTag.font = UiKit.Font;
            _nameTag.text = name;
            _nameTag.fontSize = 48;
            _nameTag.characterSize = 0.028f;
            _nameTag.anchor = TextAnchor.LowerCenter;
            _nameTag.color = Color.white;
            tagGo.GetComponent<MeshRenderer>().sharedMaterial = UiKit.Font.material;
            tagGo.GetComponent<MeshRenderer>().sortingOrder = WorldView.PlayerSortingOrder + 1;
            tagGo.transform.localPosition = new Vector3(0, 0.62f, 0);

            TileX = x; TileY = y;
            transform.position = WorldView.TileCenter(x, y);
            SetFacing(facing);
        }

        void SetFacing(Direction dir) => _renderer.sprite = _facingSprites[(int)dir];

        /// <summary>서버가 알려준 새 위치로 부드럽게 이동을 시작한다(이미 그 칸이면 방향만 바꾼다).</summary>
        public void MoveTo(int x, int y, Direction facing)
        {
            SetFacing(facing);
            if (x == TileX && y == TileY) return;
            TileX = x; TileY = y;
            _from = transform.position;
            _to = WorldView.TileCenter(x, y);
            _elapsed = 0f;
            _moving = true;
        }

        void Update()
        {
            if (!_moving) return;
            _elapsed += Time.deltaTime;
            float k = Mathf.Min(1f, _elapsed / StepSeconds);
            transform.position = Vector3.Lerp(_from, _to, k);
            if (k >= 1f) _moving = false;
        }
    }
}
