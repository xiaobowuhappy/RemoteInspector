using UnityEngine;

namespace Coplaydev.Performance
{
    internal sealed class PerformanceHost : MonoBehaviour
    {
        private static PerformanceHost _instance;
        public static PerformanceHost Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("[PerformanceSDK]") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<PerformanceHost>();
                return _instance;
            }
        }

        private void OnApplicationPause(bool paused) => Performance.SetAppState(paused ? AppRunState.Paused : AppRunState.Foreground);
        private void OnApplicationFocus(bool focused) => Performance.SetAppState(focused ? AppRunState.Foreground : AppRunState.Background);
        private void OnApplicationQuit() => Performance.Shutdown();
    }
}
