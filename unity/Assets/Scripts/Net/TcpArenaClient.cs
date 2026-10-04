using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;

namespace MonsterAdventure.Net
{
    /// <summary>
    /// 서버 연결 하나. 받은 메시지는 큐에 쌓아 두기만 한다 — Unity API는 메인 스레드에서만
    /// 불러야 하므로, 실제 처리는 MonoBehaviour 쪽이 Update() 에서 <see cref="TryTakeReceived"/> 로 꺼내 한다.
    /// </summary>
    public sealed class TcpArenaClient : IDisposable
    {
        readonly TcpClient _client = new TcpClient();
        NetworkStream _stream;
        Thread _readThread;

        readonly ConcurrentQueue<(string Type, JObject Data)> _received = new ConcurrentQueue<(string, JObject)>();
        readonly ConcurrentQueue<string> _disconnectReasons = new ConcurrentQueue<string>();

        public bool IsConnected { get; private set; }

        /// <summary>연결하고 hello 를 보낸다. 실패하면 예외를 던진다(호출자가 try/catch 로 사용자에게 알린다).</summary>
        public void Connect(string host, int port, string name, int starterSpeciesId, int timeoutMs = 4000)
        {
            var result = _client.BeginConnect(host, port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException($"{host}:{port} 에 연결할 수 없다(시간 초과).");
            _client.EndConnect(result);
            _stream = _client.GetStream();
            IsConnected = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ArenaClient.Read" };
            _readThread.Start();
            SendRaw(NetMsgType.Hello, new HelloMessage { Name = name, SpeciesId = starterSpeciesId });
        }

        void ReadLoop()
        {
            var reader = new Frame.Reader();
            var buf = new byte[4096];
            try
            {
                for (;;)
                {
                    while (reader.TryTake(out var json))
                        if (NetCodec.TryDecode(json, out var type, out var d)) _received.Enqueue((type, d));
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    reader.Feed(buf, n);
                }
            }
            catch (Exception e) { _disconnectReasons.Enqueue(e.Message); }
            finally { IsConnected = false; }
        }

        public void SendMove(NetDirection dir) => SendRaw(NetMsgType.Move, new MoveMessage { Dir = (int)dir });
        public void SendChallenge(int targetId) => SendRaw(NetMsgType.ChallengeRequest, new ChallengeRequestMessage { TargetId = targetId });
        public void SendChallengeResponse(bool accept) => SendRaw(NetMsgType.ChallengeResponse, new ChallengeResponseMessage { Accept = accept });
        public void SendDuelAction(BattleAction action) => SendRaw(NetMsgType.DuelAction, DuelActionMessage.From(action));
        public void SendDuelReplace(int partyIndex) => SendRaw(NetMsgType.DuelReplace, new DuelReplaceMessage { PartyIndex = partyIndex });
        public void SendUpdateAccount(string stateJson) => SendRaw(NetMsgType.UpdateAccount, new UpdateAccountMessage { StateJson = stateJson });

        void SendRaw(string type, object payload)
        {
            if (!IsConnected) return;
            var bytes = Frame.Encode(NetCodec.Encode(type, payload));
            try { _stream.Write(bytes, 0, bytes.Length); }
            catch (Exception e) { _disconnectReasons.Enqueue(e.Message); IsConnected = false; }
        }

        public bool TryTakeReceived(out string type, out JObject data)
        {
            if (_received.TryDequeue(out var item)) { type = item.Type; data = item.Data; return true; }
            type = null; data = null; return false;
        }

        public bool TryTakeDisconnectReason(out string reason) => _disconnectReasons.TryDequeue(out reason);

        public void Dispose()
        {
            IsConnected = false;
            try { _stream?.Close(); } catch (Exception) { /* 무시 */ }
            try { _client.Close(); } catch (Exception) { /* 무시 */ }
        }
    }
}
