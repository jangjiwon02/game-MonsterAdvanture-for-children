using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MonsterAdventure
{
    /// <summary>
    /// 배포한 게임의 설치·접속·진행상황을 Google 스프레드시트(Apps Script 웹앱)로 보낸다.
    /// 전부 fire-and-forget: 네트워크가 없거나 요청이 실패해도 게임 플레이에는 전혀 영향을 주지 않는다.
    /// 사용 방법: Apps Script 를 웹 앱으로 배포한 뒤 그 URL을 아래 EndpointUrl 에 붙여넣는다(비어 있으면 아무것도 안 보냄).
    /// </summary>
    public static class TelemetryClient
    {
        /// <summary>Apps Script 웹 앱 배포 URL. Assets/Resources/telemetry-endpoint.txt 첫 줄에서 읽는다
        /// (소스코드에 직접 안 적는다 — 이 저장소가 public GitHub 라서, URL을 코드에 박아 커밋하면 누구나
        /// 볼 수 있고 스프레드시트에 스팸을 보낼 수 있다). 그 파일은 .gitignore 에 있어 커밋되지 않는다.
        /// 파일이 없으면 빈 문자열이 되어 전송을 건너뛴다.</summary>
        public static readonly string EndpointUrl = LoadEndpoint();

        static string LoadEndpoint()
        {
            var asset = Resources.Load<TextAsset>("telemetry-endpoint");
            return asset != null ? asset.text.Trim() : "";
        }

        const string DeviceIdKey = "telemetry_device_id";
        const string InstallSentKey = "telemetry_install_sent";

        static GameObject _runnerObj;
        static MonoBehaviour _runner;

        static MonoBehaviour Runner
        {
            get
            {
                if (_runner == null)
                {
                    _runnerObj = new GameObject("TelemetryRunner") { hideFlags = HideFlags.HideInHierarchy };
                    UnityEngine.Object.DontDestroyOnLoad(_runnerObj);
                    _runner = _runnerObj.AddComponent<TelemetryRunnerBehaviour>();
                }
                return _runner;
            }
        }

        public static string DeviceId
        {
            get
            {
                string id = PlayerPrefs.GetString(DeviceIdKey, "");
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(DeviceIdKey, id);
                    PlayerPrefs.Save();
                }
                return id;
            }
        }

        /// <summary>이 기기에서 처음 실행됐을 때만(=한 번만) 설치 기록을 보낸다.</summary>
        public static void SendInstallIfFirstRun()
        {
            if (PlayerPrefs.GetInt(InstallSentKey, 0) == 1) return;
            PlayerPrefs.SetInt(InstallSentKey, 1);
            PlayerPrefs.Save();
            Send("install", $"\"device_id\":\"{Esc(DeviceId)}\",\"app_version\":\"{Esc(Application.version)}\"");
        }

        public static void SendSessionStart(string playerName) =>
            Send("session_start", $"\"device_id\":\"{Esc(DeviceId)}\",\"player_name\":\"{Esc(playerName)}\"");

        public static void SendSessionEnd(string playerName, double durationSeconds, double totalPlaySeconds) =>
            Send("session_end", $"\"device_id\":\"{Esc(DeviceId)}\",\"player_name\":\"{Esc(playerName)}\"," +
                $"\"duration_seconds\":{durationSeconds:F0},\"total_play_seconds\":{totalPlaySeconds:F0}");

        public static void SendProgress(string playerName, int level, int dexCount, int money, double totalPlaySeconds) =>
            Send("progress", $"\"device_id\":\"{Esc(DeviceId)}\",\"player_name\":\"{Esc(playerName)}\"," +
                $"\"level\":{level},\"dex_count\":{dexCount},\"money\":{money},\"total_play_seconds\":{totalPlaySeconds:F0}");

        static void Send(string eventName, string fieldsJson)
        {
            if (string.IsNullOrEmpty(EndpointUrl)) return;
            Runner.StartCoroutine(Post($"{{\"event\":\"{eventName}\",{fieldsJson}}}"));
        }

        static IEnumerator Post(string json)
        {
            using (var req = new UnityWebRequest(EndpointUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 8;
                yield return req.SendWebRequest();
                // 실패해도 조용히 넘어간다 — 원격 통계 전송은 절대 게임 플레이를 막으면 안 된다.
            }
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>TelemetryClient 가 쓰는, 코루틴만 돌리는 숨은 오브젝트.</summary>
    sealed class TelemetryRunnerBehaviour : MonoBehaviour { }
}
