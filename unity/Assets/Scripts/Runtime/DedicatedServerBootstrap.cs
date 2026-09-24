using MonsterAdventure.Core;
using MonsterAdventure.Net;
using UnityEngine;

namespace MonsterAdventure
{
    /// <summary>
    /// 아무도 직접 조작하지 않는, 서버 전용 빌드의 시작점(Assets/Scenes/ArenaServer.unity). -batchmode -nographics 로 실행한다.
    /// 화면·입력·저장을 전혀 만들지 않고 TcpArenaServer 만 띄운다.
    /// </summary>
    public sealed class DedicatedServerBootstrap : MonoBehaviour
    {
        TcpArenaServer _server;

        void Start()
        {
            // 창이 포커스를 잃어도(host 가 다른 창을 보고 있어도) 계속 돌아야 하는 서버다.
            // 프로젝트 설정에도 켜 두지만, 그 설정이 어떤 이유로든 비어 있어도 여기서 다시 보장한다.
            Application.runInBackground = true;
            Application.targetFrameRate = 30;   // 화면이 없으니 낮게 잡아 CPU 를 아낀다
            var asset = Resources.Load<TextAsset>("game-data");
            var data = GameData.Parse(asset.text);
            var map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(map);
            var accounts = new TrainerAccountStore(System.IO.Path.Combine(Application.persistentDataPath, "trainer_accounts"));
            _server = new TcpArenaServer(data, map, accounts, ArenaController.DefaultPort);
            _server.Logged += msg => Debug.Log("[ArenaServer] " + msg);
            _server.Start();
            Debug.Log($"[ArenaServer] 포트 {ArenaController.DefaultPort} 에서 대기 중.");
        }

        void OnApplicationQuit() => _server?.Dispose();
    }
}
