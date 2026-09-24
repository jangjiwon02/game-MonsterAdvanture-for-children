using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonsterAdventure
{
    public enum SfxKind { Select, Move, Hit, Crit, Faint, LevelUp, Catch, Shake, Encounter, Heal, Buy }

    /// <summary>
    /// 웹 버전의 WebAudio 비프음(sfx)을 그대로 옮긴 효과음. 오디오 에셋 없이 파형을 코드로 합성해 AudioClip 으로 만든다.
    /// 메뉴의 '소리'로 Muted 를 토글한다(웹처럼 저장하지 않는다).
    /// </summary>
    public static class Sfx
    {
        public const int SampleRate = 44100;

        public enum Wave { Square, Sawtooth, Triangle }

        public readonly struct Beep
        {
            public readonly float Freq, Duration, Volume, Slide, Delay;
            public readonly Wave Wave;
            public Beep(float freq, float duration, Wave wave, float volume, float slide = 0f, float delay = 0f)
            {
                Freq = freq; Duration = duration; Wave = wave; Volume = volume; Slide = slide; Delay = delay;
            }
        }

        public static bool Muted { get; set; }

        static Beep[] Seq(float[] freqs, float dur, Wave w, float vol, float step)
        {
            var b = new Beep[freqs.Length];
            for (int i = 0; i < freqs.Length; i++) b[i] = new Beep(freqs[i], dur, w, vol, 0f, i * step);
            return b;
        }

        static readonly Dictionary<SfxKind, Beep[]> Defs = new Dictionary<SfxKind, Beep[]>
        {
            [SfxKind.Select] = new[] { new Beep(720, .05f, Wave.Square, .03f) },
            [SfxKind.Move] = new[] { new Beep(300, .03f, Wave.Square, .015f) },
            [SfxKind.Hit] = new[] { new Beep(160, .18f, Wave.Sawtooth, .06f, -110) },
            [SfxKind.Crit] = new[] { new Beep(220, .25f, Wave.Sawtooth, .07f, -160), new Beep(500, .1f, Wave.Square, .04f, 0, .05f) },
            [SfxKind.Faint] = new[] { new Beep(400, .5f, Wave.Triangle, .06f, -320) },
            [SfxKind.LevelUp] = Seq(new float[] { 523, 659, 784, 1047 }, .12f, Wave.Square, .04f, .09f),
            [SfxKind.Catch] = Seq(new float[] { 784, 988, 1175, 1568 }, .14f, Wave.Triangle, .05f, .1f),
            [SfxKind.Shake] = new[] { new Beep(260, .07f, Wave.Square, .04f) },
            [SfxKind.Encounter] = Seq(new float[] { 300, 400, 300, 500, 400, 600 }, .08f, Wave.Square, .04f, .07f),
            [SfxKind.Heal] = Seq(new float[] { 523, 659, 784, 659, 784, 1047 }, .1f, Wave.Triangle, .05f, .09f),
            [SfxKind.Buy] = new[] { new Beep(880, .05f, Wave.Square, .03f), new Beep(1320, .08f, Wave.Square, .03f, 0, .05f) },
        };

        public static IReadOnlyDictionary<SfxKind, Beep[]> Definitions => Defs;

        /// <summary>비프 목록을 하나의 모노 파형으로 합친다(엔진 없이도 호출 가능).</summary>
        public static float[] Render(IReadOnlyList<Beep> beeps)
        {
            float end = 0f;
            foreach (var b in beeps) end = Mathf.Max(end, b.Delay + b.Duration);
            var samples = new float[Mathf.CeilToInt((end + .02f) * SampleRate)];

            foreach (var b in beeps)
            {
                int start = Mathf.RoundToInt(b.Delay * SampleRate);
                int count = Mathf.RoundToInt(b.Duration * SampleRate);
                float f1 = Mathf.Max(30f, b.Freq + b.Slide);
                double phase = 0;
                for (int i = 0; i < count && start + i < samples.Length; i++)
                {
                    float t = i / (float)count;                                  // 0~1
                    double freq = b.Slide == 0f ? b.Freq : Mathf.Lerp(b.Freq, f1, t);   // 선형 주파수 이동
                    phase += freq / SampleRate;
                    float gain = b.Volume * Mathf.Pow(.0001f / b.Volume, t);            // 지수 감쇠(웹의 exponentialRamp)
                    samples[start + i] += Osc(b.Wave, (float)(phase - Math.Floor(phase))) * gain;
                }
            }
            for (int i = 0; i < samples.Length; i++) samples[i] = Mathf.Clamp(samples[i], -1f, 1f);
            return samples;
        }

        static float Osc(Wave w, float p)
        {
            switch (w)
            {
                case Wave.Square: return p < .5f ? 1f : -1f;
                case Wave.Sawtooth: return 2f * p - 1f;
                default: return 4f * Mathf.Abs(p - .5f) - 1f;
            }
        }

        static readonly Dictionary<SfxKind, AudioClip> Clips = new Dictionary<SfxKind, AudioClip>();
        static AudioSource _source;

        public static void Play(SfxKind kind)
        {
            if (Muted || !Application.isPlaying) return;
            if (_source == null)
            {
                var go = new GameObject("Sfx") { hideFlags = HideFlags.HideAndDontSave };
                _source = go.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                Clips.Clear();
            }
            if (!Clips.TryGetValue(kind, out var clip) || clip == null)
            {
                var data = Render(Defs[kind]);
                clip = AudioClip.Create("sfx_" + kind, data.Length, 1, SampleRate, false);
                clip.SetData(data, 0);
                Clips[kind] = clip;
            }
            _source.PlayOneShot(clip);
        }
    }
}
