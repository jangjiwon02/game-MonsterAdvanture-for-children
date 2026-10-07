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

        /// <summary>이 기기의 설치 기록을 서버가 "받았다"고 확인해 줄 때까지, 실행할 때마다 다시 시도한다.
        /// (예전엔 보내기도 전에 '보냈음' 표시부터 해서, 전송이 꺼져 있던 빌드나 오프라인 첫 실행을 거친 기기는
        /// 영원히 설치 기록이 안 남았다.)</summary>
        public static void SendInstallIfFirstRun()
        {
            if (string.IsNullOrEmpty(EndpointUrl)) return;
            if (PlayerPrefs.GetInt(InstallSentKey, 0) == 1) return;
            Send("install", $"\"device_id\":\"{Esc(DeviceId)}\",\"app_version\":\"{Esc(Application.version)}\"",
                onSuccess: () => { PlayerPrefs.SetInt(InstallSentKey, 1); PlayerPrefs.Save(); });
        }

        public static void SendSessionStart(string playerName) =>
            Send("session_start", $"\"device_id\":\"{Esc(DeviceId)}\",\"player_name\":\"{Esc(playerName)}\"");

        public static void SendSessionEnd(string playerName, double durationSeconds, double totalPlaySeconds) =>
            Send("session_end", $"\"device_id\":\"{Esc(DeviceId)}\",\"player_name\":\"{Esc(playerName)}\"," +
                $"\"duration_seconds\":{durationSeconds:F0},\"total_play_seconds\":{totalPlaySeconds:F0}");

        public static void SendProgress(string playerName, int level, int dexCount, int money, double totalPlaySeconds) =>
            Send("progress", $"\"device_id\":\"{Esc(DeviceId)}\",\"player_name\":\"{Esc(playerName)}\"," +
                $"\"level\":{level},\"dex_count\":{dexCount},\"money\":{money},\"total_play_seconds\":{totalPlaySeconds:F0}");

        // 요청은 한 번에 하나씩 순서대로 보낸다. 동시에 여러 개를 쏘면 Apps Script 가 각각을 병렬로 처리하다가
        // '최신현황' 탭의 같은 이름 줄을 두 번 만들어 버릴 수 있다(세션 시작·진행상황이 거의 동시에 나가는 경우).
        const int MaxQueued = 30;
        sealed class Pending { public string EventName, Json; public Action OnSuccess; }
        static readonly System.Collections.Generic.Queue<Pending> Queue = new System.Collections.Generic.Queue<Pending>();
        static bool _draining;

        static void Send(string eventName, string fieldsJson, Action onSuccess = null)
        {
            if (string.IsNullOrEmpty(EndpointUrl)) return;
            if (Queue.Count >= MaxQueued) return;   // 오프라인이 오래 이어질 때 메모리가 쌓이지 않게(진행상황은 곧 다시 나간다)
            Queue.Enqueue(new Pending { EventName = eventName, Json = $"{{\"event\":\"{eventName}\",{fieldsJson}}}", OnSuccess = onSuccess });
            if (!_draining) Runner.StartCoroutine(Drain());
        }

        static IEnumerator Drain()
        {
            _draining = true;
            while (Queue.Count > 0)
            {
                var p = Queue.Dequeue();
                yield return Post(p.EventName, p.Json, p.OnSuccess);
            }
            _draining = false;
        }

        static IEnumerator Post(string eventName, string json, Action onSuccess)
        {
            using (var req = new UnityWebRequest(EndpointUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 15;   // 학교 와이파이처럼 느린 망에서도 Apps Script(리다이렉트 한 번 포함)가 끝날 시간을 준다
                yield return req.SendWebRequest();

                // 게임 플레이는 절대 막지 않는다(실패해도 조용히 넘어감). 다만 원인 추적이 되도록 로그는 남긴다 —
                // adb logcat -s Unity 로 "[Telemetry]" 를 보면 어느 기기에서 왜 실패했는지 알 수 있다.
                // Apps Script 는 처리를 끝내고 결과를 script.googleusercontent.com 으로 302 리다이렉트해서 돌려준다.
                // 리다이렉트를 끝까지 따라가 {"ok":true} 를 받았거나, 그 302 응답 자체를 받았으면 "전달됨"으로 본다
                // (계정 로그인 페이지로 튕기는 302 — 배포 접근 권한을 '전체'로 안 한 경우 — 는 googleusercontent 가 아니라서 실패로 남는다).
                string body = req.downloadHandler.text ?? "";
                string location = req.GetResponseHeader("Location") ?? "";
                bool ok = (req.responseCode == 200 && body.Contains("\"ok\":true"))
                          || (req.responseCode == 302 && location.Contains("googleusercontent.com"));
                if (ok) onSuccess?.Invoke();
                else Debug.LogWarning($"[Telemetry] {eventName} 전송 실패: result={req.result} code={req.responseCode} " +
                                      $"error={req.error} body={Trunc(req.downloadHandler.text)}");
            }
        }

        static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 120 ? s.Substring(0, 120) : s);

        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>TelemetryClient 가 쓰는, 코루틴만 돌리는 숨은 오브젝트.</summary>
    sealed class TelemetryRunnerBehaviour : MonoBehaviour { }
}
