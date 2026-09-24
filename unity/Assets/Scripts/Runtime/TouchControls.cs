using System.Collections.Generic;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 모바일(터치 기기)엔 물리 키보드가 없으니, 화면에 방향 패드 + Z(확인)/X(취소) 버튼을 그린다.
    /// GameInput 의 같은 입력 경로(SetTouchHeld/Inject)로 들어가므로, 이 버튼을 누르는 것과 키보드를
    /// 누르는 것은 게임 입장에서 완전히 같다.
    ///
    /// <para>실기기(안드로이드)에서 방향 패드와 X 버튼이 안 눌리는 문제가 있었다 — 원인은
    /// <c>GUI.RepeatButton</c>/<c>GUI.Button</c> 이 진짜 터치스크린의 "누르고 있음" 상태를 에디터의
    /// 마우스만큼 안정적으로 못 잡는 것(레거시 IMGUI + 터치의 잘 알려진 한계)과, 화면 맨 아래 가장자리가
    /// 안드로이드 제스처 내비게이션 바 영역과 겹쳐 탭을 가로채는 것 두 가지로 보인다. 그래서 GUI 컨트롤에
    /// 판정을 맡기지 않고, <see cref="Update"/> 에서 매 프레임 <c>Input.touches</c>(에디터에서는 마우스)를
    /// 직접 읽어 각 버튼 사각형 안에 있는지 검사하고, 레이아웃도 <c>Screen.safeArea</c> 기준으로 가장자리를
    /// 피해서 그린다. OnGUI 는 그리기만 한다.</para>
    /// </summary>
    public sealed class TouchControls : MonoBehaviour
    {
        static bool ShouldShow => Application.isMobilePlatform || Application.isEditor;

        static GUIStyle _buttonStyle;
        static Texture2D _normalTex, _pressedTex;

        readonly Dictionary<GameKey, Rect> _dirRects = new Dictionary<GameKey, Rect>();
        readonly Dictionary<GameKey, bool> _dirHeld = new Dictionary<GameKey, bool>();
        Rect _okRect, _cancelRect;
        bool _okHeldVisual, _cancelHeldVisual;

        readonly List<Vector2> _pointers = new List<Vector2>();       // 지금 누르고 있는 모든 손가락(또는 마우스) 위치, GUI 좌표(원점 좌상단)
        readonly List<Vector2> _justPressed = new List<Vector2>();    // 이번 프레임에 새로 눌리기 시작한 위치

        static void EnsureStyle()
        {
            if (_buttonStyle != null) return;
            _normalTex = SolidTexture(new Color(0.1f, 0.12f, 0.2f, 0.55f));
            _pressedTex = SolidTexture(new Color(1f, 0.85f, 0.29f, 0.55f));
            _buttonStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white, background = _normalTex },
                onNormal = { textColor = Color.white, background = _pressedTex },
            };
        }

        /// <summary>버튼 배치를 계산한다(그리기·입력 판정 양쪽에서 같은 좌표를 쓰도록 공용 함수로 뺐다).
        /// Screen.safeArea 를 기준으로 삼아, 노치·안드로이드 제스처 내비게이션 바 영역을 피한다.</summary>
        void ComputeLayout()
        {
            Rect safe = Screen.safeArea;
            float btn = Screen.height * 0.11f;
            float gap = btn * 0.15f;
            float margin = Screen.height * 0.045f; // 세이프 에어리어 안쪽에 추가로 두는 여백

            // GUI 좌표(원점 좌상단, y 아래로 증가)로 변환: safeArea 는 원점 좌하단 기준이라 뒤집어야 한다.
            float safeLeft = safe.xMin;
            float safeRight = safe.xMax;
            float safeBottomGui = Screen.height - safe.yMin; // 화면 아래 안전영역 경계의 GUI y좌표

            // 왼쪽 아래: 십자 방향 패드(3x3 칸 중 상하좌우만 그린다)
            float padX = safeLeft + margin, padY = safeBottomGui - margin - btn * 3 - gap * 2;
            _dirRects[GameKey.Up] = new Rect(padX + btn + gap, padY, btn, btn);
            _dirRects[GameKey.Left] = new Rect(padX, padY + btn + gap, btn, btn);
            _dirRects[GameKey.Right] = new Rect(padX + (btn + gap) * 2, padY + btn + gap, btn, btn);
            _dirRects[GameKey.Down] = new Rect(padX + btn + gap, padY + (btn + gap) * 2, btn, btn);

            // 오른쪽 아래: 확인/취소
            float abY = safeBottomGui - margin - btn;
            _cancelRect = new Rect(safeRight - margin - btn * 2.3f - gap, abY, btn, btn);
            _okRect = new Rect(safeRight - margin - btn, abY - btn * 0.5f, btn, btn);
        }

        static readonly GameKey[] Dirs = { GameKey.Up, GameKey.Down, GameKey.Left, GameKey.Right };

        void Update()
        {
            if (!ShouldShow || GameInput.Instance == null) return;
            ComputeLayout();
            CollectPointers();

            // 메뉴·대화(GameUi.Choose 등)는 "새로 눌린 순간"만 큐에 들어오는 WaitForKey/TryDequeue 로
            // 커서를 움직인다 — 계속 누르고 있는 상태(SetTouchHeld)만으로는 월드 이동만 되고 메뉴는
            // 전혀 안 움직인다. 그래서 키보드의 GetKeyDown 처럼, 안 눌림→눌림으로 바뀌는 순간에도
            // Inject 로 한 번 큐에 넣어 준다.
            foreach (var key in Dirs)
            {
                bool held = Contains(_dirRects[key], _pointers);
                bool wasHeld = _dirHeld.TryGetValue(key, out var w) && w;
                if (held && !wasHeld) GameInput.Instance.Inject(key);
                _dirHeld[key] = held;
                GameInput.Instance.SetTouchHeld(key, held);
            }
            _okHeldVisual = Contains(_okRect, _pointers);
            _cancelHeldVisual = Contains(_cancelRect, _pointers);

            if (Contains(_okRect, _justPressed)) GameInput.Instance.Inject(GameKey.Ok);
            if (Contains(_cancelRect, _justPressed)) GameInput.Instance.Inject(GameKey.Cancel);
        }

        /// <summary>실제 터치(다중 손가락)를 읽는다. 터치가 없으면(에디터·터치 없는 기기) 마우스로 대신한다.</summary>
        void CollectPointers()
        {
            _pointers.Clear();
            _justPressed.Clear();

            if (Input.touchCount > 0)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch t = Input.GetTouch(i);
                    if (t.phase == TouchPhase.Canceled || t.phase == TouchPhase.Ended) continue;
                    Vector2 gui = new Vector2(t.position.x, Screen.height - t.position.y);
                    _pointers.Add(gui);
                    if (t.phase == TouchPhase.Began) _justPressed.Add(gui);
                }
            }
            else
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 gui = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                    _pointers.Add(gui);
                    if (Input.GetMouseButtonDown(0)) _justPressed.Add(gui);
                }
            }
        }

        static bool Contains(Rect r, List<Vector2> points)
        {
            for (int i = 0; i < points.Count; i++) if (r.Contains(points[i])) return true;
            return false;
        }

        void OnGUI()
        {
            if (!ShouldShow || GameInput.Instance == null) return;
            EnsureStyle();
            ComputeLayout();
            _buttonStyle.fontSize = Mathf.RoundToInt(Screen.height * 0.03f);

            Draw(_dirRects[GameKey.Up], "▲", _dirHeld.TryGetValue(GameKey.Up, out var u) && u);
            Draw(_dirRects[GameKey.Left], "◀", _dirHeld.TryGetValue(GameKey.Left, out var l) && l);
            Draw(_dirRects[GameKey.Right], "▶", _dirHeld.TryGetValue(GameKey.Right, out var ri) && ri);
            Draw(_dirRects[GameKey.Down], "▼", _dirHeld.TryGetValue(GameKey.Down, out var d) && d);
            Draw(_cancelRect, "X", _cancelHeldVisual);
            Draw(_okRect, "Z", _okHeldVisual);
        }

        static void Draw(Rect r, string label, bool held)
        {
            GUI.Box(r, label, held ? StyleOn() : _buttonStyle);
        }

        static GUIStyle _onStyle;
        static GUIStyle StyleOn()
        {
            if (_onStyle == null)
            {
                _onStyle = new GUIStyle(_buttonStyle);
                _onStyle.normal.background = _pressedTex;
            }
            _onStyle.fontSize = _buttonStyle.fontSize;
            return _onStyle;
        }

        static Texture2D SolidTexture(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
