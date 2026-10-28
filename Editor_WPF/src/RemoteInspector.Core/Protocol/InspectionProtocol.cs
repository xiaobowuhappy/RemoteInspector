using System;
using System.Collections.Generic;
namespace RemoteInspector.Protocol
{
    public static class InspectionProtocol
    {
        public const ushort ScreenPick = 0x0092;
        public const ushort ScreenPickResponse = 0x0093;
        public const ushort TextureOriginal = 0x0094;
    }
    [Serializable] public class ScreenPickRequest
    {
        public ushort sequence;
        public float x, y;
        public float aspect;
    }
    [Serializable] public class ScreenPickHit
    {
        public int instanceId;
        public string name, path, kind, camera;
        public float distance;
    }
    [Serializable] public class ScreenPickResponse
    {
        public ushort sequence;
        public bool success;
        public string error;
        public List<ScreenPickHit> hits = new List<ScreenPickHit>();
    }
}
