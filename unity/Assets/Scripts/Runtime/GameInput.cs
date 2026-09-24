using System.Collections.Generic;
using UnityEngine;

namespace MonsterAdventure
{
    public enum GameKey { Up, Down, Left, Right, Ok, Cancel }

    public enum Direction { Up, Down, Left, Right }

    /// <summary>
    /// 키 입력. 메뉴·대화는 눌림 이벤트를 한 번씩 소비하는 큐로, 월드 이동은 누르고 있는 방향 스택으로 읽는다.
    /// (웹 버전의 press()/dirStack 과 같은 역할)
    /// </summary>
    public sealed class GameInput : MonoBehaviour
    {
        const int MaxQueued = 8;

        static readonly (GameKey key, KeyCode[] codes)[] Map =
        {
            (GameKey.Up,     new[] { KeyCode.UpArrow, KeyCode.W }),
            (GameKey.Down,   new[] { KeyCode.DownArrow, KeyCode.S }),
            (GameKey.Left,   new[] { KeyCode.LeftArrow, KeyCode.A }),
            (GameKey.Right,  new[] { KeyCode.RightArrow, KeyCode.D }),
            (GameKey.Ok,     new[] { KeyCode.Z, KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Space }),
            (GameKey.Cancel, new[] { KeyCode.X, KeyCode.Escape, KeyCode.Backspace }),
        };

        public static GameInput Instance { get; private set; }

        readonly Queue<GameKey> _pressed = new Queue<GameKey>();
        readonly List<GameKey> _heldDirections = new List<GameKey>();   // 마지막에 누른 방향이 우선
        readonly HashSet<GameKey> _touchHeld = new HashSet<GameKey>();  // 화면 터치 방향 패드가 누르고 있는 것(키보드와 별도로 추적)

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            foreach (var (key, codes) in Map)
            {
                bool down = false, held = false;
                foreach (var code in codes)
                {
                    down |= Input.GetKeyDown(code);
                    held |= Input.GetKey(code);
                }
                if (down)
                {
                    if (_pressed.Count >= MaxQueued) _pressed.Dequeue();
                    _pressed.Enqueue(key);
                    if (key <= GameKey.Right && !_heldDirections.Contains(key)) _heldDirections.Add(key);
                }
                // 키보드가 안 누르고 있어도, 터치 패드가 이 방향을 누르고 있으면 유지한다.
                if (!held && !_touchHeld.Contains(key)) _heldDirections.Remove(key);
            }
        }

        /// <summary>지금 누르고 있는 방향(가장 나중에 누른 것). 없으면 null.</summary>
        public Direction? HeldDirection
        {
            get
            {
                if (_heldDirections.Count == 0) return null;
                return (Direction)(int)_heldDirections[_heldDirections.Count - 1];
            }
        }

        /// <summary>코드에서 키 눌림을 넣는다(화면 터치 버튼·자동화 테스트용).</summary>
        public void Inject(GameKey key)
        {
            if (_pressed.Count >= MaxQueued) _pressed.Dequeue();
            _pressed.Enqueue(key);
        }

        /// <summary>화면 터치 방향 패드가 이 방향을 누르고/뗀 것을 알린다(모바일 — 물리 키보드가 없다).</summary>
        public void SetTouchHeld(GameKey key, bool held)
        {
            if (key > GameKey.Right) return;
            if (held)
            {
                _touchHeld.Add(key);
                if (!_heldDirections.Contains(key)) _heldDirections.Add(key);
            }
            else
            {
                _touchHeld.Remove(key);
                if (!IsKeyboardHeld(key)) _heldDirections.Remove(key);
            }
        }

        static bool IsKeyboardHeld(GameKey key)
        {
            foreach (var (k, codes) in Map)
            {
                if (k != key) continue;
                foreach (var code in codes) if (Input.GetKey(code)) return true;
            }
            return false;
        }

        public bool TryDequeue(out GameKey key)
        {
            if (_pressed.Count > 0) { key = _pressed.Dequeue(); return true; }
            key = default;
            return false;
        }

        /// <summary>아직 소비되지 않은 눌림을 버린다(대화·메뉴가 시작될 때 이전 입력이 새어 들어가지 않도록).</summary>
        public void Flush() => _pressed.Clear();

        public void ClearHeld() { _heldDirections.Clear(); _touchHeld.Clear(); }
    }

    /// <summary>다음 키 눌림을 기다린다. yield return new WaitForKey(); 후 .Key 로 읽는다.</summary>
    public sealed class WaitForKey : CustomYieldInstruction
    {
        public GameKey Key { get; private set; }
        public override bool keepWaiting => GameInput.Instance == null || !TryTake();

        bool TryTake()
        {
            if (!GameInput.Instance.TryDequeue(out var k)) return false;
            Key = k;
            return true;
        }
    }
}
