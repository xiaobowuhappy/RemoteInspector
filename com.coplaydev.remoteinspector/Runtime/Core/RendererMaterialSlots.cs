using System;
using System.Collections.Generic;
using UnityEngine;

namespace RemoteInspector.Runtime.Core
{
    internal static class RendererMaterialSlots
    {
        internal struct Slot { public Material material; public bool visible; }
        private sealed class State
        {
            public Renderer renderer;
            public readonly List<Slot> slots = new List<Slot>();
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();

        public static Slot[] GetSlots(Renderer renderer) { return GetOrCreate(renderer).slots.ToArray(); }

        public static void SetVisible(Renderer renderer, int index, bool visible)
        {
            State state = GetOrCreate(renderer);
            ValidateIndex(state, index);
            Slot slot = state.slots[index];
            slot.visible = visible;
            state.slots[index] = slot;
            Apply(state);
        }

        public static Material GetMaterial(Renderer renderer, int index)
        {
            State state = GetOrCreate(renderer);
            ValidateIndex(state, index);
            return state.slots[index].material;
        }

        private static State GetOrCreate(Renderer renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            int id = renderer.GetInstanceID();
            State state;
            if (!States.TryGetValue(id, out state) || state.renderer == null)
            {
                state = new State { renderer = renderer };
                // 沿用 Inspector 原有的 renderer.materials 语义：操作运行时实例，
                // 避免 Queue/Keyword 修改污染共享材质。
                Material[] materials = renderer.materials ?? new Material[0];
                foreach (Material material in materials)
                    state.slots.Add(new Slot { material = material, visible = true });
                States[id] = state;
            }
            return state;
        }

        private static void Apply(State state)
        {
            var materials = new Material[state.slots.Count];
            for (int i = 0; i < state.slots.Count; i++)
                materials[i] = state.slots[i].visible ? state.slots[i].material : null;
            state.renderer.sharedMaterials = materials;
        }

        private static void ValidateIndex(State state, int index)
        {
            if (index < 0 || index >= state.slots.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "材质层索引越界");
        }
    }
}
