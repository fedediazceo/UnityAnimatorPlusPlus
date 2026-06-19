using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Animations;

namespace AnimatorPlusPlus.Editor
{
    // Persistent store for reroute (repositioning) points, saved as a sub-asset of the AnimatorController.
    [System.Serializable]
    public class RerouteEntry
    {
        public AnimatorStateTransition transition;
        // The state-machine view these points belong to: a cross-boundary transition is drawn in two views
        // (sub-SM and parent) with different layouts, so each view keeps its own reroute path.
        public AnimatorStateMachine    stateMachine;
        public List<Vector2>           points = new List<Vector2>();
    }

    // A user-chosen organisational colour for a single node, keyed by its backing object
    // (an AnimatorState or a sub-AnimatorStateMachine). Stored alongside reroute data so it
    // travels with the controller asset.
    [System.Serializable]
    public class NodeColorEntry
    {
        public Object target;          // AnimatorState or AnimatorStateMachine
        public Color  color = Color.white;
    }

    public class AnimatorRerouteData : ScriptableObject
    {
        public List<RerouteEntry>    entries    = new List<RerouteEntry>();
        public List<NodeColorEntry>  nodeColors = new List<NodeColorEntry>();
    }
}
