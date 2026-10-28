using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;
using RemoteInspector.Runtime.Collectors;

namespace RemoteInspector.Runtime.Core
{
    // Read-only inspection: never dispatch clicks or modify UICamera's hovered state.
    public static class InspectionExtensions
    {
        public static void FillTextureMetadata(ComponentCollector.NodeDetailResponse detail)
        {
            detail.texturePreviewMetadata = true;
            detail.readableTextureIds = new List<int>();
            var ids = new HashSet<int>();
            foreach (var component in detail.components ?? new List<ComponentCollector.ComponentDetailData>())
            {
                if (component.imageInfo != null) ids.Add(component.imageInfo.mainTextureInstanceId);
                if (component.uiTextureInfo != null) ids.Add(component.uiTextureInfo.mainTextureInstanceId);
                foreach (var field in component.serializedFields ?? new List<ComponentCollector.SerializedFieldData>()) ids.Add(field.textureInstanceId);
                foreach (var material in component.materials ?? new List<ComponentCollector.MaterialInfo>())
                    foreach (var field in material.properties ?? new List<ComponentCollector.ShaderPropertyData>()) ids.Add(field.textureInstanceId);
            }
            foreach (int id in ids)
            {
                if (id == 0) continue;
                var texture = TextureCache.Find(id);
                if (texture != null && texture.isReadable) detail.readableTextureIds.Add(id);
            }
        }
        public static void HandlePick(MessagePacket packet, RemoteInspectorClient client)
        {
            var response = new ScreenPickResponse { sequence = packet.sequence };
            try
            {
                var request = MessageSerializer.DeserializeBody<ScreenPickRequest>(packet);
                response = Pick(request); response.sequence = packet.sequence;
            }
            catch (Exception ex) { response.error = ex.Message; }
            client.SendPacket((Command)InspectionProtocol.ScreenPickResponse, packet.sequence, MessageSerializer.SerializeBody(response));
        }
        public static ScreenPickResponse Pick(ScreenPickRequest request)
        {
            var result = new ScreenPickResponse();
            if (request == null || float.IsNaN(request.x) || float.IsNaN(request.y) || request.x < 0 || request.x > 1 || request.y < 0 || request.y > 1)
                throw new ArgumentException("Invalid normalized screen coordinates.");
            float aspect = Screen.width / (float)Math.Max(1, Screen.height);
            if (request.aspect <= 0 || Math.Abs(aspect / request.aspect - 1) > .02f)
                throw new InvalidOperationException("Screen orientation changed. Capture a new screenshot.");
            var point = new Vector2(request.x * Screen.width, request.y * Screen.height);
            var seen = new HashSet<int>();
            Action<GameObject, string, string, float> add = (go, kind, camera, distance) =>
            {
                if (go == null || !go.activeInHierarchy || result.hits.Count >= 64 || !seen.Add(go.GetInstanceID())) return;
                var names = new List<string>(); for (var t = go.transform; t != null; t = t.parent) names.Add(t.name);
                names.Reverse();
                result.hits.Add(new ScreenPickHit { instanceId = go.GetInstanceID(), name = go.name, path = string.Join("/", names.ToArray()), kind = kind, camera = camera, distance = distance });
            };
            if (EventSystem.current != null)
            {
                var pointer = new PointerEventData(EventSystem.current) { position = point };
                var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
                foreach (var hit in hits)
                    if (hit.module is UnityEngine.UI.GraphicRaycaster) add(hit.gameObject, "uGUI", hit.module.eventCamera != null ? hit.module.eventCamera.name : "Overlay", hit.distance);
            }
            foreach (var camera in Camera.allCameras.OrderByDescending(c => c.depth))
            {
                if (!camera.isActiveAndEnabled || camera.targetTexture != null || !camera.pixelRect.Contains(point)) continue;
                var ngui = camera.GetComponent<UICamera>();
                int mask = camera.cullingMask & (ngui != null ? (int)ngui.eventReceiverMask : -1);
                float range = ngui != null && ngui.rangeDistance > 0 ? ngui.rangeDistance : camera.farClipPlane - camera.nearClipPlane;
                var ray = camera.ScreenPointToRay(point);
                var buffer = new RaycastHit[256];
                int count = camera.gameObject.scene.GetPhysicsScene().Raycast(ray.origin, ray.direction, buffer, range, mask, QueryTriggerInteraction.Collide);
                var hits = buffer.Take(count);
                foreach (var hit in hits.OrderByDescending(h => ngui != null ? NGUITools.CalculateRaycastDepth(h.collider.gameObject) : 0).ThenBy(h => h.distance))
                {
                    var go = hit.collider.gameObject;
                    if (ngui != null && !VisibleNgui(go, hit.point)) continue;
                    add(go, ngui != null ? "NGUI" : "Physics 3D", camera.name, hit.distance);
                }
                foreach (var hit in Physics2D.GetRayIntersectionAll(ray, range, mask).OrderByDescending(h => ngui != null ? NGUITools.CalculateRaycastDepth(h.collider.gameObject) : 0).ThenBy(h => h.distance))
                {
                    if (ngui != null && !VisibleNgui(hit.collider.gameObject, hit.point)) continue;
                    add(hit.collider.gameObject, ngui != null ? "NGUI 2D" : "Physics 2D", camera.name, hit.distance);
                }
            }
            result.success = true; return result;
        }
        private static bool VisibleNgui(GameObject go, Vector3 point)
        {
            var widget = go.GetComponent<UIWidget>();
            if (widget != null && (!widget.isVisible || widget.hitCheck != null && !widget.hitCheck(point))) return false;
            var rect = go.GetComponentInParent<UIRect>(); if (rect != null && rect.finalAlpha < .001f) return false;
            for (var panel = go.GetComponentInParent<UIPanel>(); panel != null; panel = panel.parentPanel)
                if (!panel.IsVisible(point)) return false;
            return NGUITools.CalculateRaycastDepth(go) != int.MaxValue;
        }
        public static void HandleOriginalTexture(MessagePacket packet, RemoteInspectorClient client)
        {
            int id = 0;
            try
            {
                var request = MessageSerializer.DeserializeBody<GetTextureRequest>(packet); id = request.instanceId;
                var tex = TextureCache.Find(id);
                if (tex == null || !tex.isReadable) throw new InvalidOperationException("Texture is missing or Read/Write is disabled.");
                if ((long)tex.width * tex.height > 16777216) throw new InvalidOperationException("Texture export is limited to 16 million pixels.");
                var bytes = tex.EncodeToPNG();
                if (bytes == null || bytes.Length == 0 || bytes.Length > 64 * 1024 * 1024) throw new InvalidOperationException("Texture encoding failed or exceeds 64 MiB.");
                const int size = 24576; int count = (bytes.Length + size - 1) / size;
                for (int i = 0; i < count; i++)
                {
                    var response = new TextureResponse { instanceId = id, width = tex.width, height = tex.height, name = tex.name,
                        chunkIndex = i, totalChunks = count, isLast = i == count - 1, status = "ok",
                        data = Convert.ToBase64String(bytes, i * size, Math.Min(size, bytes.Length - i * size)) };
                    client.SendPacket(Command.TEXTURE_RESPONSE, packet.sequence, MessageSerializer.SerializeBody(response));
                }
            }
            catch (Exception ex)
            {
                client.SendPacket(Command.TEXTURE_RESPONSE, packet.sequence, MessageSerializer.SerializeBody(new TextureResponse { instanceId = id, isLast = true, status = ex.Message }));
            }
        }
    }
}
