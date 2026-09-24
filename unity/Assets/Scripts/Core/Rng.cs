using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>[0,1) 난수원. 테스트에서 굴림을 고정하려고 인터페이스로 분리한다.</summary>
    public interface IRng
    {
        double NextDouble();
    }

    public static class RngExtensions
    {
        /// <summary>min..max 정수(양끝 포함). 웹의 rnd(a, b)와 같다.</summary>
        public static int Range(this IRng rng, int min, int max) =>
            min + (int)Math.Floor(rng.NextDouble() * (max - min + 1));

        /// <summary>웹의 pick(a)와 같다.</summary>
        public static T Pick<T>(this IRng rng, IReadOnlyList<T> list) =>
            list[(int)Math.Floor(rng.NextDouble() * list.Count)];
    }

    public sealed class SystemRng : IRng
    {
        readonly Random _random;
        public SystemRng() { _random = new Random(); }
        public SystemRng(int seed) { _random = new Random(seed); }
        public double NextDouble() => _random.NextDouble();
    }

    /// <summary>웹 버전과 비트 단위로 같은 mulberry32. 월드 생성 시드(20240921)에 쓴다.</summary>
    public sealed class Mulberry32 : IRng
    {
        uint _state;

        public Mulberry32(int seed) { _state = unchecked((uint)seed); }

        public double NextDouble()
        {
            unchecked
            {
                _state += 0x6D2B79F5;
                uint t = (_state ^ (_state >> 15)) * (1 | _state);
                t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    /// <summary>미리 정한 값을 차례로 돌려주는 난수원(테스트용).</summary>
    public sealed class ScriptedRng : IRng
    {
        readonly Queue<double> _values;
        public ScriptedRng(params double[] values) { _values = new Queue<double>(values); }
        public int Remaining => _values.Count;
        public double NextDouble()
        {
            if (_values.Count == 0) throw new InvalidOperationException("ScriptedRng: 준비된 굴림이 없다.");
            return _values.Dequeue();
        }
    }
}
