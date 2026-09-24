using System;
using System.Collections.Generic;
using System.Text;

namespace MonsterAdventure.Net
{
    /// <summary>
    /// TCP는 바이트가 이어붙어 오므로, 메시지 하나의 길이를 앞에 4바이트(빅엔디안)로 붙여
    /// 어디서 끊어 읽어야 할지 표시한다. 소켓 없이도 테스트할 수 있게 순수 바이트 배열로만 다룬다.
    /// </summary>
    public static class Frame
    {
        public const int MaxMessageBytes = 1 << 20; // 1MB. 이보다 크면 깨진 연결로 본다.

        public static byte[] Encode(string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var framed = new byte[4 + body.Length];
            WriteLength(framed, 0, body.Length);
            Buffer.BlockCopy(body, 0, framed, 4, body.Length);
            return framed;
        }

        static void WriteLength(byte[] buf, int offset, int length)
        {
            buf[offset] = (byte)(length >> 24);
            buf[offset + 1] = (byte)(length >> 16);
            buf[offset + 2] = (byte)(length >> 8);
            buf[offset + 3] = (byte)length;
        }

        static int ReadLength(IReadOnlyList<byte> buf, int offset) =>
            (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

        /// <summary>들어오는 바이트를 누적하고, 완성된 메시지가 쌓이면 하나씩 꺼내 준다.</summary>
        public sealed class Reader
        {
            readonly List<byte> _buf = new List<byte>();

            public void Feed(byte[] data, int count)
            {
                for (int i = 0; i < count; i++) _buf.Add(data[i]);
            }

            /// <summary>완성된 메시지가 있으면 꺼내고 true. 없으면 false(더 기다린다).</summary>
            public bool TryTake(out string json)
            {
                json = null;
                if (_buf.Count < 4) return false;
                int len = ReadLength(_buf, 0);
                if (len < 0 || len > MaxMessageBytes) throw new InvalidOperationException($"프레임 길이가 비정상적이다: {len}");
                if (_buf.Count < 4 + len) return false;

                var body = new byte[len];
                _buf.CopyTo(4, body, 0, len);
                json = Encoding.UTF8.GetString(body);
                _buf.RemoveRange(0, 4 + len);
                return true;
            }
        }
    }
}
