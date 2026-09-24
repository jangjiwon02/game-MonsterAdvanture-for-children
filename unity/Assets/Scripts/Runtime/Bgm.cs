using System.Collections.Generic;
using UnityEngine;

namespace MonsterAdventure
{
    public enum BgmKind { World, Battle }

    /// <summary>
    /// 배경음악. 오디오 에셋 없이 <see cref="Sfx"/>와 똑같은 파형 합성 방식(Sfx.Render 재사용)으로
    /// 짧은 멜로디를 만들어 반복 재생한다 — 그래서 이 배경음도 APK 용량을 늘리지 않는다.
    /// "소리" 메뉴 토글(<see cref="Sfx.Muted"/>)을 효과음과 그대로 같이 쓴다.
    /// </summary>
    public static class Bgm
    {
        static AudioSource _source;
        static BgmKind? _current;
        static readonly Dictionary<BgmKind, AudioClip> _clips = new Dictionary<BgmKind, AudioClip>();

        /// <summary>해당 곡을 반복 재생한다. 이미 그 곡이 재생 중이면 아무 것도 안 한다(끊김 없음).</summary>
        public static void Play(BgmKind kind)
        {
            if (!Application.isPlaying) return;
            try
            {
                EnsureSource();
                _source.mute = Sfx.Muted;
                if (_current == kind && _source.isPlaying) return;
                _current = kind;
                _source.clip = GetClip(kind);
                _source.loop = true;
                _source.Play();
                Debug.Log($"[Bgm] Play({kind}) — mute={_source.mute} isPlaying={_source.isPlaying} clipLen={_source.clip.length:F2}s vol={_source.volume} listenerVol={AudioListener.volume} listenerPause={AudioListener.pause}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Bgm] Play({kind}) 실패: {e}");
            }
        }

        /// <summary>"소리" 메뉴 토글이 바뀔 때 같이 불러 준다.</summary>
        public static void SetMuted(bool muted)
        {
            if (_source != null) _source.mute = muted;
        }

        static void EnsureSource()
        {
            if (_source != null) return;
            var go = new GameObject("Bgm") { hideFlags = HideFlags.HideAndDontSave };
            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.volume = 1f;
        }

        static AudioClip GetClip(BgmKind kind)
        {
            if (_clips.TryGetValue(kind, out var c) && c != null) return c;
            var data = Sfx.Render(kind == BgmKind.World ? WorldBeeps() : BattleBeeps());
            c = AudioClip.Create("bgm_" + kind, data.Length, 1, Sfx.SampleRate, false);
            c.SetData(data, 0);
            _clips[kind] = c;
            return c;
        }

        /* ---------------------------------- 작곡 (다 도 기준 음이름 → Hz) ---------------------------------- */

        const float C3 = 130.81f, D3 = 146.83f, E3 = 164.81f, G3 = 196.00f, A3 = 220.00f;
        const float C4 = 261.63f, D4 = 293.66f, E4 = 329.63f, G4 = 392.00f, A4 = 440.00f;

        /// <summary>월드 테마: 느긋한 5음 음계 산책 멜로디(트라이앵글) + 저음 패드(스퀘어). 약 5.6초 루프.</summary>
        static List<Sfx.Beep> WorldBeeps()
        {
            var list = new List<Sfx.Beep>();
            const float step = .35f;
            float[] melody = { C4, E4, G4, A4, G4, E4, D4, C4, D4, E4, G4, E4, D4, C4, D4, C4 };
            for (int i = 0; i < melody.Length; i++)
                list.Add(new Sfx.Beep(melody[i], step * .92f, Sfx.Wave.Triangle, .05f, 0f, i * step));

            list.Add(new Sfx.Beep(C3, step * 8f * .95f, Sfx.Wave.Square, .022f, 0f, 0f));
            list.Add(new Sfx.Beep(G3, step * 8f * .95f, Sfx.Wave.Square, .022f, 0f, step * 8f));
            return list;
        }

        /// <summary>전투 테마: 빠른 단조풍 멜로디(스퀘어) + 톱니 저음. 약 2.9초 루프.</summary>
        static List<Sfx.Beep> BattleBeeps()
        {
            var list = new List<Sfx.Beep>();
            const float step = .18f;
            float[] melody = { A3, C4, E4, A4, G4, E4, C4, A3, A3, C4, E4, A4, G4, E4, D4, C4 };
            for (int i = 0; i < melody.Length; i++)
                list.Add(new Sfx.Beep(melody[i], step * .85f, Sfx.Wave.Square, .045f, 0f, i * step));

            float[] bass = { A3, A3, E3, E3, A3, A3, E3, E3, A3, A3, E3, E3, A3, A3, D3, D3 };
            for (int i = 0; i < bass.Length; i++)
                list.Add(new Sfx.Beep(bass[i], step * .9f, Sfx.Wave.Sawtooth, .03f, -20f, i * step));
            return list;
        }
    }
}
