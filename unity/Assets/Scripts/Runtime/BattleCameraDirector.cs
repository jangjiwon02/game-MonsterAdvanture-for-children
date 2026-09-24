using System.Collections;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 씬 전환 없이 필드 위에서 전투로 들어가는 카메라 연출(Cinemachine 없이 CameraRig 오버라이드로 직접 구현).
    /// 메인은 이 코루틴이 끝난 뒤에 전투 UI(IMGUI 오버레이)를 띄운다.
    /// </summary>
    public static class BattleCameraDirector
    {
        /// <summary>전투 중 화면 높이(타일). 기본 10칸에서 이만큼으로 줄여 둘을 크게 보여 준다.</summary>
        public const float EncounterOrthoSize = 3.2f;
        public const float DefaultZoomInSeconds = 0.55f;
        public const float DefaultZoomOutSeconds = 0.45f;

        /// <summary>두 월드 위치의 한가운데(플레이어와 야생 몬스터 사이).</summary>
        public static Vector3 Midpoint(Vector3 a, Vector3 b) => (a + b) * 0.5f;

        /// <summary>0→1 을 천천히 출발해 천천히 멈추는 곡선.</summary>
        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
        }

        /// <summary>
        /// 카메라를 focusWorldPos 로 옮기며 화면을 확대한다. 시작 직후 짧게 흔들려 "붙었다"는 느낌을 준다.
        /// 끝나도 오버라이드는 유지된다(전투 내내 그 화면). 필드로 돌아올 때 <see cref="ZoomBack"/> 을 쓴다.
        /// </summary>
        public static IEnumerator ZoomToEncounter(CameraRig rig, Vector3 focusWorldPos,
            float duration = DefaultZoomInSeconds, float targetOrthoSize = EncounterOrthoSize, float shake = 0.07f)
        {
            if (rig == null) yield break;
            Vector3 startFocus = rig.transform.position;
            float startSize = rig.OrthoSize;
            focusWorldPos.z = startFocus.z;

            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                float k = t / duration, e = Ease(k);
                float amp = shake * (1f - k) * (1f - k);          // 처음에 세고 빠르게 잦아든다
                var jitter = new Vector3(Mathf.Sin(t * 95f), Mathf.Cos(t * 121f), 0f) * amp;
                rig.SetOverride(Vector3.Lerp(startFocus, focusWorldPos, e) + jitter, Mathf.Lerp(startSize, targetOrthoSize, e));
                yield return null;
            }
            rig.SetOverride(focusWorldPos, targetOrthoSize);
        }

        /// <summary>전투가 끝난 뒤 원래의 플레이어 추적·기본 줌으로 부드럽게 되돌린다. 끝나면 오버라이드를 푼다.</summary>
        public static IEnumerator ZoomBack(CameraRig rig, float duration = DefaultZoomOutSeconds)
        {
            if (rig == null) yield break;
            Vector3 startFocus = rig.transform.position;
            float startSize = rig.OrthoSize;

            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                float e = Ease(t / duration);
                // 플레이어는 멈춰 있지만 순간이동(패배 후 귀환)했을 수도 있어 목표를 매 프레임 다시 읽는다.
                var goal = rig.TrackedFocus;
                goal.z = startFocus.z;
                rig.SetOverride(Vector3.Lerp(startFocus, goal, e), Mathf.Lerp(startSize, CameraRig.DefaultOrthoSize, e));
                yield return null;
            }
            rig.ClearOverride();
        }
    }
}
