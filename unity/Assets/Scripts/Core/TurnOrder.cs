using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>
    /// 턴 순서 우선순위 큐. 정렬 키는 기술 우선도(큰 쪽 먼저) > 스피드(큰 쪽 먼저) > 동률이면 난수(50:50).
    /// Unity(.NET Standard 2.1)엔 PriorityQueue 가 없어서 작은 이진 힙을 직접 둔다.
    /// 동률 처리는 첫 Dequeue 때 한 번에 하므로, 동률이 없으면 난수를 한 번도 쓰지 않는다.
    /// 동률이 정확히 둘이면 난수 r 하나를 쓰고 r &lt; 0.5 면 먼저 넣은 쪽이 앞선다(<see cref="EnemyAI.PlayerMovesFirst"/> 와 같은 규칙).
    /// </summary>
    public sealed class TurnQueue<T>
    {
        struct Entry
        {
            public T Item; public int Priority, Speed, Seq, Tie;
        }

        readonly IRng _rng;
        readonly List<Entry> _pending = new List<Entry>();
        Entry[] _heap;
        int _count;
        bool _sealed;

        public TurnQueue(IRng rng) { _rng = rng; }

        public int Count => _sealed ? _count : _pending.Count;

        public void Enqueue(T item, int priority, int speed)
        {
            if (_sealed) throw new InvalidOperationException("Dequeue 를 시작한 큐에는 더 넣을 수 없다.");
            _pending.Add(new Entry { Item = item, Priority = priority, Speed = speed, Seq = _pending.Count });
        }

        /// <summary>다음으로 움직일 항목을 꺼낸다.</summary>
        public T Dequeue()
        {
            if (!_sealed) Seal();
            if (_count == 0) throw new InvalidOperationException("큐가 비었다.");
            var top = _heap[0];
            _heap[0] = _heap[--_count];
            _heap[_count] = default;
            if (_count > 0) SiftDown(0);
            return top.Item;
        }

        void Seal()
        {
            _sealed = true;
            // 우선도·스피드 내림차순, 같으면 넣은 순서(안정)로 정렬한 뒤, 키가 같은 구간마다 동률 순서를 굴린다.
            var sorted = new List<Entry>(_pending);
            sorted.Sort((a, b) =>
            {
                int c = b.Priority.CompareTo(a.Priority);
                if (c != 0) return c;
                c = b.Speed.CompareTo(a.Speed);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i + 1;
                while (j < sorted.Count && sorted[j].Priority == sorted[i].Priority && sorted[j].Speed == sorted[i].Speed) j++;
                int n = j - i;
                if (n > 1)
                {
                    // 피셔-예이츠를 뒤집어서 쓴다: n=2 이면 r<0.5 일 때 교환 없음(=먼저 넣은 쪽이 앞).
                    var order = new int[n];
                    for (int k = 0; k < n; k++) order[k] = k;
                    for (int k = n - 1; k >= 1; k--)
                    {
                        int pick = k - (int)Math.Floor(_rng.NextDouble() * (k + 1));
                        int tmp = order[k]; order[k] = order[pick]; order[pick] = tmp;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        var e = sorted[i + k];
                        e.Tie = order[k];
                        sorted[i + k] = e;
                    }
                }
                i = j;
            }

            _heap = new Entry[Math.Max(1, sorted.Count)];
            foreach (var e in sorted) { _heap[_count++] = e; SiftUp(_count - 1); }
        }

        static bool Before(Entry a, Entry b)
        {
            if (a.Priority != b.Priority) return a.Priority > b.Priority;
            if (a.Speed != b.Speed) return a.Speed > b.Speed;
            if (a.Tie != b.Tie) return a.Tie < b.Tie;
            return a.Seq < b.Seq;
        }

        void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Before(_heap[i], _heap[parent])) break;
                var t = _heap[i]; _heap[i] = _heap[parent]; _heap[parent] = t;
                i = parent;
            }
        }

        void SiftDown(int i)
        {
            for (;;)
            {
                int l = 2 * i + 1, r = l + 1, best = i;
                if (l < _count && Before(_heap[l], _heap[best])) best = l;
                if (r < _count && Before(_heap[r], _heap[best])) best = r;
                if (best == i) return;
                var t = _heap[i]; _heap[i] = _heap[best]; _heap[best] = t;
                i = best;
            }
        }
    }

    public static class TurnOrder
    {
        /// <summary>둘 중 첫째(a)가 먼저 움직이는지. 우선도 > 스피드 > 동률이면 난수 0.5.</summary>
        public static bool FirstMovesFirst(int priorityA, int speedA, int priorityB, int speedB, IRng rng)
        {
            var q = new TurnQueue<bool>(rng);
            q.Enqueue(true, priorityA, speedA);
            q.Enqueue(false, priorityB, speedB);
            return q.Dequeue();
        }
    }
}
