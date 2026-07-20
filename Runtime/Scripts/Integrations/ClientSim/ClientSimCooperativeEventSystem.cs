using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Unity.EditorXR.ClientSim
{
    // Dispatches targets resolved by EditorXR through the one active EventSystem.
    sealed class ClientSimCooperativeEventSystem
    {
        sealed class PointerState
        {
            public EventSystem eventSystem;
            public PointerEventData data;
            public GameObject hover;
            public GameObject press;
            public bool pressed;
        }

        readonly Dictionary<int, PointerState> m_Pointers = new Dictionary<int, PointerState>();

        public bool Process(int pointerId, GameObject target, Vector2 position, bool pressed)
        {
            var system = EventSystem.current;
            if (system == null)
                return false;

            PointerState state;
            if (!m_Pointers.TryGetValue(pointerId, out state) || state.eventSystem != system)
            {
                state = new PointerState
                {
                    eventSystem = system,
                    data = new PointerEventData(system) { pointerId = pointerId }
                };
                m_Pointers[pointerId] = state;
            }

            state.data.delta = position - state.data.position;
            state.data.position = position;
            SetHover(state, target);
            if (pressed && !state.pressed)
                Press(state, target);
            else if (!pressed && state.pressed)
                Release(state, target);
            state.pressed = pressed;
            return target != null;
        }

        public void ReleaseAll()
        {
            foreach (var state in m_Pointers.Values)
            {
                if (state.pressed)
                    Release(state, null);
                if (state.hover != null)
                    ExecuteEvents.Execute(state.hover, state.data, ExecuteEvents.pointerExitHandler);
            }
            m_Pointers.Clear();
        }

        static void SetHover(PointerState state, GameObject target)
        {
            if (state.hover == target)
                return;
            if (state.hover != null)
                ExecuteEvents.Execute(state.hover, state.data, ExecuteEvents.pointerExitHandler);
            state.hover = target;
            state.data.pointerEnter = target;
            if (target != null)
                ExecuteEvents.ExecuteHierarchy(target, state.data, ExecuteEvents.pointerEnterHandler);
        }

        static void Press(PointerState state, GameObject target)
        {
            var data = state.data;
            data.pressPosition = data.position;
            data.eligibleForClick = true;
            var press = ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerDownHandler);
            if (press == null)
                press = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            state.press = press;
            data.pointerPress = press;
            data.rawPointerPress = target;
        }

        static void Release(PointerState state, GameObject target)
        {
            if (state.press != null)
                ExecuteEvents.Execute(state.press, state.data, ExecuteEvents.pointerUpHandler);
            var click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (state.data.eligibleForClick && state.press == click && click != null)
                ExecuteEvents.Execute(click, state.data, ExecuteEvents.pointerClickHandler);
            state.data.eligibleForClick = false;
            state.data.pointerPress = null;
            state.data.rawPointerPress = null;
            state.press = null;
            state.pressed = false;
        }
    }
}
