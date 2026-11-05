using System;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Coplaydev.Performance
{
    internal static class PlayerLoopInstaller
    {
        private struct PerformanceEarlyMarker { }
        private struct PerformanceLateMarker { }
        private static PlayerLoopSystem _original;
        private static bool _installed;

        public static void Install(PlayerLoopSystem.UpdateFunction early, PlayerLoopSystem.UpdateFunction late)
        {
            if (_installed) return;
            _original = PlayerLoop.GetCurrentPlayerLoop();
            var loop = _original;
            var earlySystem = new PlayerLoopSystem { type = typeof(PerformanceEarlyMarker), updateDelegate = early };
            var lateSystem = new PlayerLoopSystem { type = typeof(PerformanceLateMarker), updateDelegate = late };
            if (!Insert(ref loop, typeof(EarlyUpdate), earlySystem, true) || !Insert(ref loop, typeof(PostLateUpdate), lateSystem, false))
                throw new InvalidOperationException("Unable to inject performance PlayerLoop nodes.");
            PlayerLoop.SetPlayerLoop(loop);
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            PlayerLoop.SetPlayerLoop(_original);
            _installed = false;
        }

        private static bool Insert(ref PlayerLoopSystem root, Type parentType, PlayerLoopSystem child, bool first)
        {
            var systems = root.subSystemList;
            if (systems == null) return false;
            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i].type == parentType)
                {
                    var children = systems[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                    var next = new PlayerLoopSystem[children.Length + 1];
                    if (first)
                    {
                        next[0] = child;
                        Array.Copy(children, 0, next, 1, children.Length);
                    }
                    else
                    {
                        Array.Copy(children, next, children.Length);
                        next[children.Length] = child;
                    }
                    systems[i].subSystemList = next;
                    root.subSystemList = systems;
                    return true;
                }
            }
            return false;
        }
    }
}
