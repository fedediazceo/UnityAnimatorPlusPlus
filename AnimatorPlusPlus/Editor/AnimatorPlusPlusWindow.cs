using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

using AnimatorConditionMode = UnityEditor.Animations.AnimatorConditionMode;
using AnimatorController = UnityEditor.Animations.AnimatorController;
using AnimatorControllerParameterType = UnityEngine.AnimatorControllerParameterType;
using AnimatorLayerBlendingMode = UnityEditor.Animations.AnimatorLayerBlendingMode;
using AnimatorState = UnityEditor.Animations.AnimatorState;
using AnimatorStateMachine = UnityEditor.Animations.AnimatorStateMachine;
using AnimatorStateTransition = UnityEditor.Animations.AnimatorStateTransition;
using AnimatorTransition = UnityEditor.Animations.AnimatorTransition;
using BlendTree = UnityEditor.Animations.BlendTree;
using TransitionInterruptionSource = UnityEditor.Animations.TransitionInterruptionSource;

namespace AnimatorPlusPlus.Editor
{
    public class AnimatorPlusPlusWindow : EditorWindow
    {
        // ── Layout ───────────────────────────────────────────────────────────────
        private const float ToolH    = 22f;   // toolbar height
        private       float SideW    = 160f;  // resizable sidebar
        private const float MinSideW = 160f;  // keeps tab buttons usable
        private const float NodeW  = 200f;
        private const float NodeH  = 40f;
        private const float ZMin   = 0.2f;
        private const float ZMax   = 2.5f;

        // ── Data types ────────────────────────────────────────────────────────────
        private class SNode
        {
            public AnimatorState state;
            public string        name;
            public Vector2       position;
            public Motion        motion;        // synced layers may override this
            public List<STrans>  transitions = new List<STrans>();
            public bool isEntry, isExit, isAnyState, isDefault;

            // ── Blend-tree view ──────────────────────────────────────────────────
            public Vector2   size;             // zero uses the default node size
            public BlendTree blendTree;        // blend-tree backing object
            public bool      isBtRoot;         // root of the current blend-tree view
            public bool      isBtChild;        // child motion in the current blend-tree view
            public int       btChildIndex = -1;

            // ── Sub-state-machine view ───────────────────────────────────────────
            public bool                 isStateMachine;   // sub-state-machine node
            public bool                 isUp;             // virtual parent-navigation node
            public AnimatorStateMachine subSM;            // target sub-state-machine
        }

        private class STrans
        {
            public SNode                   from;
            public SNode                   to;
            public AnimatorStateTransition src;
            public List<Vector2>           pts = new List<Vector2>();
            public bool                    isEntryDefault; // implicit default-entry line
            public AnimatorState           ownerState;     // the state that actually owns src (may differ from
                                                            // `from` for cross-boundary transitions drawn off a proxy node)
        }

        // Copy/paste snapshot for states.
        private struct CopyData
        {
            public string  name;
            public Motion  motion;
            public float   speed;
            public string  tag;
            public Vector2 position;
        }

        // Copy/paste snapshot for transition parameters.
        private class TransParamData
        {
            public bool  hasExitTime, hasFixedDuration, orderedInterruption, canTransitionToSelf, mute, solo;
            public float exitTime, duration, offset;
            public TransitionInterruptionSource interruptionSource;
            public List<(AnimatorConditionMode mode, float threshold, string parameter)> conditions
                = new List<(AnimatorConditionMode, float, string)>();
        }

        // ── State ─────────────────────────────────────────────────────────────────
        private List<SNode>        nodes = new List<SNode>();
        private AnimatorController ctrl;
        private int                layer = 0;

        // Blend-tree navigation: empty = layer state-machine view; otherwise we're inside the
        // active blend tree is the last item; breadcrumbs mirror this stack.
        private readonly List<BlendTree> btStack = new List<BlendTree>();
        // Parallel to btStack: the AnimatorState a level was entered from (null for nested blend trees).
        // Used by breadcrumbs to prefer the owning state name.
        private readonly List<AnimatorState> btEntryStates = new List<AnimatorState>();
        private bool   InBlendTree => btStack.Count > 0;
        private BlendTree CurrentBT => btStack.Count > 0 ? btStack[btStack.Count - 1] : null;

        private Vector2 pan  = new Vector2(100f, 100f);
        private float   zoom = 1f;
        private bool    needCenter;
        private bool    needFit;       // deferred until canvas size is known

        // Per-view remembered pan/zoom so switching layer / entering a sub-SM or blend tree doesn't reset
        // the camera. 
        private readonly Dictionary<string, (Vector2 pan, float zoom)> viewStates = new Dictionary<string, (Vector2, float)>();
        private string  currentViewKey;

        private SNode   selNode;
        private STrans  selTrans;
        private SNode   makingTransFrom;    // active transition drag source

        // Multi-selection (marquee + shift-click). selNode stays the "primary" focused node.
        private readonly HashSet<SNode>  selNodes   = new HashSet<SNode>();
        private readonly HashSet<STrans> selTranses = new HashSet<STrans>(); // marquee-selected transitions
        private readonly List<CopyData>  copyBuffer = new List<CopyData>();  // state copy buffer
        private AnimatorStateMachine      smCopyBuffer;                       // state-machine copy buffer
        private TransParamData            transParamBuffer;                   // transition parameter copy buffer

        // Marquee box selection
        private bool    boxSelecting;
        private Vector2 boxStartG;           // graph-space drag anchor
        private bool    boxAdditive;         // Shift held when the marquee started → add to current selection

        // Proxy object selected when the default entry transition is clicked, so the
        // Inspector shows the "not previewable" note instead of editable transition data.
        private DefaultEntryTransitionInfo entryInfoProxy;
        private DefaultEntryTransitionInfo EntryInfoProxy
        {
            get
            {
                if (entryInfoProxy == null)
                {
                    entryInfoProxy = ScriptableObject.CreateInstance<DefaultEntryTransitionInfo>();
                    entryInfoProxy.hideFlags = HideFlags.HideAndDontSave;
                    entryInfoProxy.name = "Default Entry Transition";
                }
                return entryInfoProxy;
            }
        }

        // Proxy selected when a marquee catches mixed element types — shows the "Narrow your selection" tool.
        private MultiSelectionInfo multiSelProxy;
        private MultiSelectionInfo MultiSelProxy
        {
            get
            {
                if (multiSelProxy == null)
                {
                    multiSelProxy = ScriptableObject.CreateInstance<MultiSelectionInfo>();
                    // Keep this editable in the Inspector.
                    multiSelProxy.hideFlags = HideFlags.DontSave;
                    multiSelProxy.name = "Selection";
                }
                return multiSelProxy;
            }
        }

        // Proxy selected when a state is clicked on a synced layer — lets the user edit only the
        // per-state override motion (the rest of the state is owned by the source layer).
        private SyncedStateInfo syncedStateProxy;
        private SyncedStateInfo SyncedStateProxy
        {
            get
            {
                if (syncedStateProxy == null)
                {
                    syncedStateProxy = ScriptableObject.CreateInstance<SyncedStateInfo>();
                    syncedStateProxy.hideFlags = HideFlags.DontSave;   // editable proxy
                    syncedStateProxy.name = "Synced State";
                }
                return syncedStateProxy;
            }
        }

        // Select the real AnimatorStateTransition(s) so Unity's NATIVE transition Inspector renders.
        // NativeTransitionBridge (driven from Tick) feeds it the controller/source context it would
        // normally get from the open Animator window, so it works (and edits conditions) without it.
        // Returns false when none have a real backing transition (e.g. only the entry-default line).
        private bool SelectTransitionInInspector(List<STrans> trs)
        {
            if (ctrl == null || trs == null)
                return false;

            var objs = new List<Object>();

            foreach (var t in trs)
            {
                if (t != null && t.src != null)
                    objs.Add(t.src);
            }

            if (objs.Count == 0)
                return false;

            Selection.objects = objs.ToArray();

            var capturedCtrl = ctrl;
            var capturedLayer = layer;

            // Run more than once because Unity may create/reuse the native inspector over multiple editor passes.
            EditorApplication.delayCall += () =>
            {
                NativeTransitionBridge.EnsureContext(capturedCtrl, capturedLayer);
                InternalEditorUtility.RepaintAllViews();

                EditorApplication.delayCall += () =>
                {
                    NativeTransitionBridge.EnsureContext(capturedCtrl, capturedLayer);
                    InternalEditorUtility.RepaintAllViews();
                };
            };

            return true;
        }

        // Inline rename state must be cleared before focus-sensitive IMGUI paths change.
        private void CancelRename()
        {
            if (paramRenameIdx < 0 && layerRenameIdx < 0) return;
            paramRenameIdx = layerRenameIdx = -1;
            renameFocusPending = renameAcquiredFocus = false;
            GUI.FocusControl(null);
        }

        private void ClearCanvasSelection(bool clearBoxSelection = true)
        {
            selNodes.Clear();
            selTranses.Clear();
            selReroutes.Clear();

            selNode = null;
            selTrans = null;

            if (clearBoxSelection)
                boxSelecting = false;
        }

        private static bool IsDeletableNode(SNode n)
        {
            if (n == null) return false;
            if (n.state != null) return true;
            return n.isStateMachine && !n.isUp && n.subSM != null;
        }

        private List<STrans> CollectSelectedTransitions()
        {
            var result = new List<STrans>();

            foreach (var t in selTranses)
                if (t?.src != null)
                    result.Add(t);

            if (selTrans?.src != null && !result.Contains(selTrans))
                result.Add(selTrans);

            return result;
        }

        // Point the Inspector at the override-motion editor for a synced-layer state.
        private void SelectSyncedState(SNode n)
        {
            if (n?.state == null) return;
            var p = SyncedStateProxy;
            p.ctrl = ctrl; p.layerIndex = layer; p.state = n.state; p.window = this;
            p.name = n.state.name;
            selNodes.Clear();
            selNodes.Add(n);
            selNode = n;
            selTrans = null;
            selTranses.Clear();
            selReroutes.Clear();
            Selection.activeObject = p;
            Repaint();
        }

        // Refresh synced-layer override motions without rebuilding selection.
        internal void RefreshSyncedMotions()
        {
            if (!ViewingSynced || ctrl == null || layer >= ctrl.layers.Length) return;
            var l = ctrl.layers[layer];
            foreach (var n in nodes)
                if (n.state != null)
                {
                    var ov = l.GetOverrideMotion(n.state);
                    n.motion = ov != null ? ov : n.state.motion;
                }
            Repaint();
        }

        private Vector2 panStart, lastMouseWin;
        private bool    draggingNode, draggingReroute, draggingPan, draggingSplit;

        // Stable key for a reroute point during selection and dragging.
        private struct RR : System.IEquatable<RR>
        {
            public STrans trans; public int idx;
            public RR(STrans t, int i) { trans = t; idx = i; }
            public bool Equals(RR o) => ReferenceEquals(trans, o.trans) && idx == o.idx;
            public override bool Equals(object o) => o is RR r && Equals(r);
            public override int GetHashCode() => (trans != null ? trans.GetHashCode() : 0) * 397 ^ idx;
        }

        private readonly HashSet<RR> selReroutes = new HashSet<RR>();   // selected reroute points

        // Absolute drag snapshot for group moves and grid snapping.
        private Vector2 dragStartG, dragAnchorStart;
        private readonly Dictionary<SNode, Vector2> dragStartPos     = new Dictionary<SNode, Vector2>();
        private readonly Dictionary<RR,    Vector2> dragStartReroute = new Dictionary<RR,    Vector2>();

        private int     sideTab    = 0;     // 0 = Layers, 1 = Parameters
        private string  paramSearch = "";
        // Inline rename (click an already-selected row again to edit its name, Explorer-style)
        private int     paramRenameIdx   = -1;
        private int     layerRenameIdx   = -1;
        private int     paramSelBefore   = -1;  // row index before ReorderableList handles input
        private bool    renameFocusPending;
        private bool    renameAcquiredFocus;    // avoids cancelling before the text field receives focus
        private bool    showSidebar  = true;  // sidebar visibility
        private bool    autoLiveLink = true;  // play-mode live link
        private bool    snapToGrid   = false; // grid snapping while dragging
        private const float GridSnap  = 10f;  // small grid cell size

        // Synced-layer view: when the selected layer syncs another, we draw THAT layer's state machine
        // (structure is shared/read-only) and only allow per-state motion overrides. -1 = not synced.
        private int     syncedFromLayer = -1;
        private bool    ViewingSynced => syncedFromLayer >= 0;
        // Index of the layer whose state machine the canvas currently shows (source when synced).
        private int     SmLayerIndex  => syncedFromLayer >= 0 ? syncedFromLayer : layer;

        // Sub-state-machine navigation: empty = the layer's root SM; each entry is one level deeper.
        private readonly List<AnimatorStateMachine> smStack = new List<AnimatorStateMachine>();
        // The layer's top-level state machine.
        private AnimatorStateMachine RootSM =>
            (ctrl != null && ctrl.layers.Length > 0) ? ctrl.layers[SmLayerIndex].stateMachine : null;
        // The state machine currently shown on the canvas (root SM, or the deepest navigated sub-SM).
        private AnimatorStateMachine CurrentSM => smStack.Count > 0 ? smStack[smStack.Count - 1] : RootSM;
        private bool InSubSM => smStack.Count > 0;

        // Cached serialized + reorderable lists
        private SerializedObject ctrlSO;
        private ReorderableList  paramRL;
        private ReorderableList  layerRL;

        // Live playback
        private Animator liveAnim;
        private int      liveHash, liveNextHash;
        private float    liveNT, liveTNT;
        private bool     liveInTrans;

        // ── Colours (tuned to match Unity Animator Pro skin exactly) ─────────────
        private static readonly Color BG        = new Color32(42,  42,  42,  255);
        private static readonly Color SideBG    = Hex("383838");
        private static readonly Color SideLine  = new Color32(22,  22,  22,  255);
        private static readonly Color GridSm    = new Color(0f, 0f, 0f, 0.12f);
        private static readonly Color GridLg    = new Color(0f, 0f, 0f, 0.26f);
        private static readonly Color NText      = new Color32(220, 220, 220, 255);
        private static readonly Color NSelBord   = new Color32(68,  148, 255, 255);

        // Hex color helper.
        private static Color Hex(string h) { ColorUtility.TryParseHtmlString("#" + h, out var c); return c; }

        // 1×1 texture for flat IMGUI backgrounds.
        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        // Apply a flat background to a GUIStyleState, clearing the @2x scaledBackgrounds
        // (which otherwise override 'background' on HiDPI screens and show a gradient).
        private static void SetBg(GUIStyleState s, Texture2D t)
        {
            s.background = t;
            s.scaledBackgrounds = System.Array.Empty<Texture2D>();
        }

        // Sidebar Layers/Parameters tab style — flat #3c3c3c background
        private static GUIStyle _tabStyle;
        private static GUIStyle TabStyle
        {
            get
            {
                if (_tabStyle == null || _tabStyle.normal.background == null)
                {
                    _tabStyle = new GUIStyle(EditorStyles.toolbarButton) { border = new RectOffset(0, 0, 0, 0), fixedHeight = ToolH, margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(7, 7, 0, 0) };
                    var idle  = MakeTex(Hex("3c3c3c"));
                    var hover = MakeTex(Hex("464646"));
                    var sel   = MakeTex(Hex("505050"));
                    // HiDPI skins may prefer scaled backgrounds unless they are cleared.
                    SetBg(_tabStyle.normal,   idle);
                    SetBg(_tabStyle.hover,    hover);
                    SetBg(_tabStyle.active,   sel);
                    SetBg(_tabStyle.onNormal, sel);
                    SetBg(_tabStyle.onHover,  sel);
                    SetBg(_tabStyle.onActive, sel);
                }
                return _tabStyle;
            }
        }

        // Cog button — transparent idle, subtle hover/press highlight
        private static GUIStyle _cogStyle;
        private static GUIStyle CogStyle
        {
            get
            {
                if (_cogStyle == null || _cogStyle.hover.background == null)
                {
                    _cogStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0) };
                    SetBg(_cogStyle.normal, null);
                    SetBg(_cogStyle.hover,  MakeTex(new Color(1f, 1f, 1f, 0.22f)));
                    SetBg(_cogStyle.active, MakeTex(new Color(1f, 1f, 1f, 0.32f)));
                }
                return _cogStyle;
            }
        }

        // Bigger "+" add button — flat (no toolbar gradient/borders)
        private static GUIStyle _plusStyle;
        private static GUIStyle PlusStyle
        {
            get
            {
                if (_plusStyle == null || _plusStyle.normal.background == null)
                {
                    _plusStyle = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                        border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0), fixedHeight = ToolH - 2f
                    };
                    SetBg(_plusStyle.normal, MakeTex(Hex("3c3c3c")));
                    SetBg(_plusStyle.hover,  MakeTex(Hex("464646")));
                    SetBg(_plusStyle.active, MakeTex(Hex("505050")));
                }
                return _plusStyle;
            }
        }

        // Node fill + outline gradients (top → bottom)
        // State nodes
        private static readonly Color NFillTop   = Hex("4e5155"), NFillBot   = Hex("45484a");
        private static readonly Color NBordTop   = Hex("686b6e"), NBordBot   = Hex("45484a");
        // Default state
        private static readonly Color NDefFillTop = Hex("ca741e"), NDefFillBot = Hex("96520f");
        private static readonly Color NDefBordTop = Hex("da9d60"), NDefBordBot = Hex("96520f");
        // Entry
        private static readonly Color EntryFillTop = Hex("277b3c"), EntryFillBot = Hex("005f25");
        private static readonly Color EntryBordTop = Hex("67a375"), EntryBordBot = Hex("005f25");
        // Any State
        private static readonly Color AnyFillTop   = Hex("5da08e"), AnyFillBot   = Hex("3b6c65");
        private static readonly Color AnyBordTop   = Hex("8dbdaf"), AnyBordBot   = Hex("3b6c65");
        // Exit
        private static readonly Color ExitFillTop  = Hex("9f2f2f"), ExitFillBot  = Hex("8f0000");
        private static readonly Color ExitBordTop  = Hex("ad4f4f"), ExitBordBot  = Hex("8f0000");

        private static readonly Color ArrowCol  = new Color32(131, 131, 131, 255); // #838383
        private static readonly Color EntryLine  = Hex("4f3501"); // entry line
        private static readonly Color EntryArrow = Hex("996600"); // entry arrow
        private static readonly Color RubberCol = new Color32(68,  148, 255, 200);

        // ── Layer settings popup ──────────────────────────────────────────────────
        private class LayerSettingsPopup : PopupWindowContent
        {
            private readonly AnimatorController c;
            private readonly int idx;

            public LayerSettingsPopup(AnimatorController ctrl, int i)
            {
                c   = ctrl;
                idx = i;
            }

            public override Vector2 GetWindowSize() => new Vector2(268, 220);

            public override void OnGUI(Rect rect)
            {
                if (c == null || idx >= c.layers.Length) { if (editorWindow != null) editorWindow.Close(); return; }

                var layers = c.layers;
                var l      = layers[idx];

                GUILayout.Space(4);
                EditorGUILayout.LabelField("Layer Settings", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();

                l.name = EditorGUILayout.DelayedTextField("Name", l.name);

                // Base layer weight is fixed.
                using (new EditorGUI.DisabledGroupScope(idx == 0))
                    l.defaultWeight = EditorGUILayout.Slider("Weight", l.defaultWeight, 0f, 1f);

                l.blendingMode = (AnimatorLayerBlendingMode)EditorGUILayout.EnumPopup("Blending", l.blendingMode);
                l.avatarMask   = EditorGUILayout.ObjectField("Mask", l.avatarMask, typeof(AvatarMask), false) as AvatarMask;

                // ── Sync — only available when there is more than one layer ──────
                bool canSync  = c.layers.Length > 1;
                bool isSynced = l.syncedLayerIndex >= 0;

                using (new EditorGUI.DisabledGroupScope(!canSync))
                {
                    EditorGUI.BeginChangeCheck();
                    bool newSynced = EditorGUILayout.Toggle("Sync", isSynced && canSync);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (newSynced)
                        {
                            // Pick the first valid source layer.
                            for (int j = 0; j < c.layers.Length; j++)
                                if (j != idx) { l.syncedLayerIndex = j; break; }
                        }
                        else l.syncedLayerIndex = -1;
                    }
                }

                if (l.syncedLayerIndex >= 0)
                {
                    EditorGUI.indentLevel++;

                    // Exclude the current layer from sync targets.
                    var filteredNames   = new List<string>();
                    var filteredIndices = new List<int>();
                    for (int j = 0; j < c.layers.Length; j++)
                    {
                        if (j == idx) continue;
                        filteredNames.Add(c.layers[j].name);
                        filteredIndices.Add(j);
                    }

                    if (filteredNames.Count > 0)
                    {
                        int cur = filteredIndices.IndexOf(l.syncedLayerIndex);
                        if (cur < 0) { cur = 0; l.syncedLayerIndex = filteredIndices[0]; }

                        EditorGUI.BeginChangeCheck();
                        int chosen = EditorGUILayout.Popup("Layer", cur, filteredNames.ToArray());
                        if (EditorGUI.EndChangeCheck()) l.syncedLayerIndex = filteredIndices[chosen];
                    }

                    l.syncedLayerAffectsTiming = EditorGUILayout.Toggle("Timing", l.syncedLayerAffectsTiming);
                    EditorGUI.indentLevel--;
                }

                l.iKPass = EditorGUILayout.Toggle("IK Pass", l.iKPass);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(c, "Layer Settings");
                    layers[idx] = l;
                    c.layers    = layers;
                    // Keep Unity's layer/root-SM naming convention intact.
                    if (l.stateMachine != null && l.stateMachine.name != l.name)
                    { Undo.RecordObject(l.stateMachine, "Layer Settings"); l.stateMachine.name = l.name; }
                    EditorUtility.SetDirty(c);
                    // Sync changes can swap the displayed state machine.
                    NotifyLayerSyncChanged(c);
                }
            }

        }

        // ── Menu ─────────────────────────────────────────────────────────────────
        [MenuItem("Window/Animation/Animator ++")]
        public static void OpenWindow()
        {
            OpenWindowInternal();
        }

        private static AnimatorPlusPlusWindow OpenWindowInternal()
        {
            var gameViewType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");

            var window = gameViewType != null
                ? GetWindow<AnimatorPlusPlusWindow>("Animator ++", true, typeof(SceneView), gameViewType)
                : GetWindow<AnimatorPlusPlusWindow>("Animator ++", true, typeof(SceneView));

            window.SetWindowTitle();
            window.Show();

            return window;
        }

        private void SetWindowTitle()
        {
            var icon = EditorGUIUtility.IconContent("Animator Icon");
            titleContent = new GUIContent("Animator ++", icon.image, "Animator ++");
        }
        [MenuItem("Assets/Open in Animator ++", true)]
        private static bool ValidateOpenAnimatorPlusPlusFromAsset()
        {
            return Selection.activeObject is AnimatorController;
        }

        [MenuItem("Assets/Open in Animator ++", false, 2000)]
        private static void OpenAnimatorPlusPlusFromAsset()
        {
            var controller = Selection.activeObject as AnimatorController;
            if (controller == null)
                return;

            var window = OpenWindowInternal();
            window.LoadCtrl(controller);
            window.Repaint();
        }
        // ── Lifecycle ─────────────────────────────────────────────────────────────
        private void OnEnable()
        {
            SetWindowTitle();

            wantsMouseMove = true;
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed   += OnUndoRedo;
            LoadCtrl(Selection.activeObject as AnimatorController);
        }

        // Undo/redo may affect controller structure, layout, or reroute data.
        private void OnUndoRedo()
        {
            if (ctrl != null) LoadCtrl(ctrl, layer);
            Repaint();
        }

        private void OnDisable()
        {
            CancelRename();
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed   -= OnUndoRedo;
            if (entryInfoProxy   != null) DestroyImmediate(entryInfoProxy);
            if (multiSelProxy    != null) DestroyImmediate(multiSelProxy);
            if (syncedStateProxy != null) DestroyImmediate(syncedStateProxy);
            specialNodeBridge?.Dispose();
        }

        // Native special-node proxies allow Unity's built-in inspectors to handle Entry/Any/Exit.
        private readonly NativeSpecialNodeBridge specialNodeBridge = new NativeSpecialNodeBridge();

        // IMGUI text fields should not stay active after focus changes.
        private void OnLostFocus() => CancelRename();

        private int lastNameSig;
        private int lastStructSig;     // structural fingerprint, to catch states/transitions edited in the Inspector
        
        // Last observed root-SM name for the current layer, so a rename done in the Inspector (which edits
        // the state machine) can be propagated to the layer name without clobbering it on load.
        private string lastSMName;

        // Resolve labels from backing objects when possible.
        private string LiveNodeName(SNode n)
        {
            if (n.state != null)                        return n.state.name;
            if (n.isStateMachine && n.subSM != null)    return n.subSM.name;
            if (n.isBtRoot && n.blendTree != null)      return n.blendTree.name;
            if (n.isBtChild)                            return n.motion != null ? n.motion.name : "(None)";
            return n.name;
        }

        // Name fingerprint used to catch Inspector-side renames.
        private int NameSignature()
        {
            int h = 17;
            if (ctrl != null && layer < ctrl.layers.Length)
                h = h * 31 + (ctrl.layers[layer].name?.GetHashCode() ?? 0);
            var sm = CurrentSM;                                            // root SM name (edited in the Inspector)
            if (sm != null) h = h * 31 + (sm.name?.GetHashCode() ?? 0);
            foreach (var n in nodes)
                h = h * 31 + (LiveNodeName(n)?.GetHashCode() ?? 0);
            return h;
        }

        // Keep layer and root-SM names aligned after Inspector edits.
        private void SyncLayerNameFromSM()
        {
            var sm = CurrentSM;
            if (ctrl == null || sm == null || layer >= ctrl.layers.Length) return;
            if (sm.name == lastSMName) return;          // SM name didn't actually change since last sync
            lastSMName = sm.name;
            if (string.IsNullOrEmpty(sm.name) || ctrl.layers[layer].name == sm.name) return;
            var layers = ctrl.layers; layers[layer].name = sm.name; ctrl.layers = layers;
            EditorUtility.SetDirty(ctrl);
        }

        // Refresh cached names without rebuilding the graph.
        private void RefreshNodeNames()
        {
            foreach (var n in nodes) n.name = LiveNodeName(n);
        }

        // Structural fingerprint used to catch Inspector-side graph edits.
        private int StructSignature()
        {
            if (InBlendTree) return 0;
            var sm = CurrentSM;
            if (sm == null) return 0;
            int h = 17;
            h = h * 31 + sm.states.Length;
            h = h * 31 + sm.stateMachines.Length;
            h = h * 31 + sm.anyStateTransitions.Length;
            h = h * 31 + sm.entryTransitions.Length;
            foreach (var cs in sm.states)
                if (cs.state != null) h = h * 31 + cs.state.transitions.Length;
            return h;
        }

        private void Tick()
        {
            // Keep the native transition/state/sub-SM Inspector fed with context (re-feeds if recreated).
            var nsel = Selection.activeObject;
            if (ctrl != null && (nsel is AnimatorStateTransition || nsel is AnimatorState || nsel is AnimatorStateMachine))
                NativeTransitionBridge.EnsureContext(ctrl, layer);

            // Reflect Inspector edits that fire no event of our own. A structural change (a state or
            // transition added/removed) rebuilds the graph; otherwise a name change just re-syncs labels.
            int struc = StructSignature();
            if (struc != lastStructSig)
            {
                lastStructSig = struc;
                RebuildView();
                lastNameSig = NameSignature();
                Repaint();
            }
            else
            {
                int sig = NameSignature();
                if (sig != lastNameSig) { lastNameSig = sig; SyncLayerNameFromSM(); RefreshNodeNames(); Repaint(); }
            }

            // Repaint continuously while inside a blend tree so our sliders follow the inspector's red
            // dot live (we get no events of our own while the drag happens in the Inspector window).
            if (InBlendTree) Repaint();

            if (!Application.isPlaying || !autoLiveLink) return;
            if (liveAnim == null) liveAnim = FindLiveAnimator();
            if (liveAnim == null) return;
            PollLive();
            Repaint();
        }

        private void OnSelectionChange()
        {
            // Feed the native inspector its context before its first repaint (no flicker).
            var sel = Selection.activeObject;
            if (ctrl != null && (sel is AnimatorState || sel is AnimatorStateTransition || sel is AnimatorStateMachine))
            {
                var c = ctrl; var l = layer;
                EditorApplication.delayCall += () => NativeTransitionBridge.EnsureContext(c, l);
            }

            if (LoadFromSelection()) { Repaint(); return; }

            // Reflect an external state/transition selection (e.g. the Narrow tool) into the node view
            SyncSelectionFromInspector();
        }

        // When the window regains focus (e.g. brought to front from a hidden tab), catch up on any
        // selection that changed while it wasn't receiving OnSelectionChange.
        private void OnFocus()
        {
            if (LoadFromSelection()) Repaint();
        }

        // Load a controller from the current selection (controller asset, or a GameObject's Animator).
        // Returns true if the selection mapped to a controller (so we shouldn't also run a state sync).
        private bool LoadFromSelection()
        {
            var c = Selection.activeObject as AnimatorController;
            if (c != null) { if (c != ctrl) LoadCtrl(c); return true; }

            if (Selection.activeGameObject != null)
            {
                var a = Selection.activeGameObject.GetComponent<Animator>();
                if (a != null)
                {
                    var ac = AsController(a.runtimeAnimatorController);
                    if (ac != null)
                    {
                        if (ac != ctrl) LoadCtrl(ac);
                        liveAnim = a;           // set after LoadCtrl (which resets liveAnim)
                        return true;
                    }
                }
            }
            return false;
        }

        // Resolve the underlying AnimatorController (unwrapping AnimatorOverrideControllers)
        private static AnimatorController AsController(RuntimeAnimatorController rac)
        {
            while (rac is AnimatorOverrideController ovr) rac = ovr.runtimeAnimatorController;
            return rac as AnimatorController;
        }

        // Map the current Inspector selection back to node-view highlight (states + transitions)
        private void SyncSelectionFromInspector()
        {
            if (ctrl == null || nodes.Count == 0) return;
            // Don't let a deferred Selection change clobber the highlight mid-gesture
            if (draggingNode || draggingReroute || boxSelecting) return;
            var objs = Selection.objects;
            if (objs == null || objs.Length == 0) return;

            var newNodes = new HashSet<SNode>();
            var newTrans = new HashSet<STrans>();
            foreach (var o in objs)
            {
                if (o is AnimatorState st)
                {
                    var n = nodes.Find(x => x.state == st);
                    if (n != null) newNodes.Add(n);
                }
                else if (o is AnimatorStateTransition tr)
                {
                    foreach (var node in nodes)
                        foreach (var t in node.transitions)
                            if (t.src == tr) newTrans.Add(t);
                }
            }

            // Selection isn't ours (proxy, controller, asset, …) — leave the current highlight alone
            if (newNodes.Count == 0 && newTrans.Count == 0) return;

            selNodes.Clear();   selNodes.UnionWith(newNodes);
            selTranses.Clear(); selTranses.UnionWith(newTrans);
            selNode  = null; selTrans = null;
            if (newNodes.Count == 1 && newTrans.Count == 0) foreach (var n in newNodes) selNode = n;
            else if (newNodes.Count == 0 && newTrans.Count == 1) foreach (var t in newTrans) selTrans = t;
            Repaint();
        }

        // Push the current node multi-selection to the Inspector (multi-edit when >1, single otherwise).
        private void PushNodeSelectionToInspector()
        {
            var objs = new List<Object>();
            foreach (var n in selNodes) if (n.state != null) objs.Add(n.state);
            if (objs.Count == 0)      Selection.activeObject = null;
            else if (objs.Count == 1) Selection.activeObject = objs[0];
            else                      Selection.objects = objs.ToArray();
        }

        // Snap a graph-space position to the small grid.
        private static Vector2 SnapToGrid(Vector2 p)
            => new Vector2(Mathf.Round(p.x / GridSnap) * GridSnap, Mathf.Round(p.y / GridSnap) * GridSnap);

        // Capture the start position of every selected node + reroute so a drag moves them rigidly.
        private void SnapshotDrag(Vector2 mouseG, Vector2 anchorStart)
        {
            dragStartG = mouseG;
            dragAnchorStart = anchorStart;
            dragStartPos.Clear();
            foreach (var sn in selNodes) dragStartPos[sn] = sn.position;
            dragStartReroute.Clear();
            foreach (var rr in selReroutes)
                if (rr.trans != null && rr.idx < rr.trans.pts.Count) dragStartReroute[rr] = rr.trans.pts[rr.idx];
        }

        // True when a reroute point should render as selected (explicit set or live marquee preview).
        private bool IsRerouteSel(STrans t, int i)
        {
            if (selReroutes.Contains(new RR(t, i))) return true;
            if (boxSelecting && MarqueeRectG().Contains(t.pts[i])) return true;
            return false;
        }

        // Remove a transition's underlying object. Any State transitions live on the state machine
        // (no owning state), so they need RemoveAnyStateTransition instead of state.RemoveTransition.
        private void RemoveTransitionSrc(STrans t)
        {
            if (t?.src == null) return;
            if (t.from != null && t.from.isAnyState)
            {
                var sm = CurrentSM;
                if (sm == null) return;
                Undo.RecordObject(sm, "Delete Transition");
                sm.RemoveAnyStateTransition(t.src);
                EditorUtility.SetDirty(sm);
            }
            else
            {
                // The owning state — for a cross-boundary transition this is the real source state, not the
                // proxy (sub-SM / "(UP)") node the line is drawn off.
                var owner = t.ownerState != null ? t.ownerState : t.from?.state;
                if (owner != null)
                {
                    Undo.RecordObject(owner, "Delete Transition");
                    owner.RemoveTransition(t.src);
                    EditorUtility.SetDirty(owner);
                }
            }
        }

        private void DeleteSelection()
        {
            if (ctrl == null || InBlendTree) return;

            if (ViewingSynced)
            {
                if (selReroutes.Count > 0) DeleteSelectedReroutes();
                return;
            }

            bool hasDeletableNodes = selNodes.Any(IsDeletableNode);
            var transitionsToDelete = CollectSelectedTransitions();

            if (!hasDeletableNodes && transitionsToDelete.Count == 0)
            {
                if (selReroutes.Count > 0)
                    DeleteSelectedReroutes();

                return;
            }

            var sm = CurrentSM;
            if (sm == null) return;

            Undo.RecordObject(sm, "Delete Selection");

            foreach (var t in transitionsToDelete)
            {
                if (t.from == null) continue;
                if (selNodes.Contains(t.from)) continue;

                RemoveTransitionSrc(t);
            }

            foreach (var n in selNodes)
            {
                if (n == null) continue;

                if (n.state != null)
                {
                    sm.RemoveState(n.state);
                    continue;
                }

                if (n.isStateMachine && !n.isUp && n.subSM != null)
                    sm.RemoveStateMachine(n.subSM);
            }

            ClearCanvasSelection();

            EditorUtility.SetDirty(sm);
            RebuildView();
            SaveReroutes();
            Repaint();
        }

        // Remove every selected reroute point. Per transition, indices are deleted high→low so they stay valid.
        private void DeleteSelectedReroutes()
        {
            var byTrans = new Dictionary<STrans, List<int>>();
            foreach (var rr in selReroutes)
            {
                if (rr.trans == null) continue;
                if (!byTrans.TryGetValue(rr.trans, out var list)) { list = new List<int>(); byTrans[rr.trans] = list; }
                list.Add(rr.idx);
            }
            foreach (var kv in byTrans)
            {
                kv.Value.Sort();
                for (int k = kv.Value.Count - 1; k >= 0; k--)
                    if (kv.Value[k] >= 0 && kv.Value[k] < kv.Key.pts.Count) kv.Key.pts.RemoveAt(kv.Value[k]);
                SyncRerouteSiblings(kv.Key);   // removal applies to the whole from→to group
            }
            selReroutes.Clear();
            SaveReroutes();
        }

        // ── Load ─────────────────────────────────────────────────────────────────
        private void LoadCtrl(AnimatorController c, int layerIdx = 0)
        {
            // Track the previous controller so EnterView (below) can tell a real controller change
            // (forget remembered views, zoom-to-fit) from a same-controller reload (restore the camera).
            var prevCtrl = ctrl;

            nodes.Clear();
            CancelRename();
            specialNodeBridge?.Dispose();   // native graph cache is per-SM/structure — rebuild on reload
            btStack.Clear(); btEntryStates.Clear();   // loading a layer/controller returns to the state-machine view
            ClearCanvasSelection();
            ctrl = c;
            if (c == null || c.layers.Length == 0) { RebuildLists(); return; }

            layer = Mathf.Clamp(layerIdx, 0, c.layers.Length - 1);

            // Synced layer? Mirror the source layer's state machine (shared structure, override motions).
            syncedFromLayer = c.layers[layer].syncedLayerIndex;
            smStack.Clear();            // a fresh load returns to the layer's root state machine
            if (RootSM == null) { RebuildLists(); return; }

            BuildSMGraph();             // build nodes for the (root) state machine

            liveAnim   = FindLiveAnimator();
            lastSMName = CurrentSM != null ? CurrentSM.name : null;   // baseline for SM→layer name sync
            lastNameSig = NameSignature();                            // avoid a spurious resync on first tick
            EnterView(prevCtrl == c);   // restore this view's camera (or zoom-to-fit); new controller = fresh
            RebuildLists();
        }

        // Build the state-machine graph for the current navigation depth.
        private void BuildSMGraph()
        {
            nodes.Clear();
            var sm = CurrentSM;
            if (sm == null || ctrl == null) return;
            var curLayer = ctrl.layers[layer];
            var map = new Dictionary<AnimatorState, SNode>();

            foreach (var cs in sm.states)
            {
                // Effective motion: the synced layer's per-state override when present, else the source clip.
                Motion mot = cs.state.motion;
                if (ViewingSynced) { var ov = curLayer.GetOverrideMotion(cs.state); if (ov != null) mot = ov; }
                var n = new SNode { state = cs.state, name = cs.state.name, position = cs.position, motion = mot, isDefault = cs.state == sm.defaultState };
                nodes.Add(n); map[cs.state] = n;
            }

            var en = Spec("Entry",     sm.entryPosition,    isEntry:    true);
            var xn = Spec("Exit",      sm.exitPosition,     isExit:     true);
            var an = Spec("Any State", sm.anyStatePosition, isAnyState: true);

            // Sub-state-machine child nodes (gray beveled). Captured so cross-boundary transitions route here.
            var smNodeMap = new Dictionary<AnimatorStateMachine, SNode>();
            foreach (var css in sm.stateMachines)
            {
                if (css.stateMachine == null) continue;
                var smn = new SNode { isStateMachine = true, subSM = css.stateMachine, name = css.stateMachine.name, position = css.position };
                nodes.Add(smn); smNodeMap[css.stateMachine] = smn;
            }

            // "(UP)" node — only inside a sub-SM. Leads back to the parent (one level up).
            SNode upNode = null;
            if (InSubSM)
            {
                string parentName = smStack.Count > 1 ? smStack[smStack.Count - 2].name
                                                    : (RootSM != null ? RootSM.name : "Base Layer");
                upNode = new SNode { isUp = true, name = "(UP) " + parentName, position = sm.parentStateMachinePosition };
                nodes.Add(upNode);
            }

            // Resolve a state to its node in THIS view: the state's own node, the direct-child sub-SM node
            // that (recursively) contains it, or the "(UP)" node for anything outside this SM.
            SNode NodeForState(AnimatorState st)
            {
                if (st == null) return null;
                if (map.TryGetValue(st, out var sn)) return sn;
                foreach (var kv in smNodeMap) if (SMContainsState(kv.Key, st)) return kv.Value;
                return upNode;
            }
            SNode NodeForSM(AnimatorStateMachine s)
            {
                if (s == null) return null;
                if (smNodeMap.TryGetValue(s, out var sn)) return sn;
                foreach (var kv in smNodeMap) if (SMContainsSM(kv.Key, s)) return kv.Value;
                return upNode;
            }

            // Every transition in the layer, resolved into this view: both endpoints map to a visible node
            // (a state here, a sub-SM node, or "(UP)"). Drawn when they resolve to different nodes — so a
            // cross-boundary transition shows from BOTH sides (state→sub-SM here, "(UP)"→state inside it).
            var allStates = new List<AnimatorState>();
            CollectStates(RootSM, allStates);
            foreach (var st in allStates)
            {
                var srcNode = NodeForState(st);
                if (srcNode == null) continue;
                bool srcLocal = map.ContainsKey(st);          // exit transitions only belong to local states
                foreach (var t in st.transitions)
                {
                    SNode dstNode;
                    if (t.isExit)                              { if (!srcLocal) continue; dstNode = xn; }
                    else if (t.destinationState != null)        dstNode = NodeForState(t.destinationState);
                    else if (t.destinationStateMachine != null) dstNode = NodeForSM(t.destinationStateMachine);
                    else continue;
                    if (dstNode == null || dstNode == srcNode) continue;   // skip self-proxy loops (internal to a child)
                    srcNode.transitions.Add(new STrans { from = srcNode, to = dstNode, src = t, ownerState = st });
                }
            }

            // Automatic Entry → default-state transition (drawn whenever a default state exists)
            if (sm.defaultState != null && map.ContainsKey(sm.defaultState))
                en.transitions.Add(new STrans { from = en, to = map[sm.defaultState], isEntryDefault = true });
            foreach (var t in sm.entryTransitions)
            {
                var to = t.destinationState != null ? NodeForState(t.destinationState)
                    : t.destinationStateMachine != null ? NodeForSM(t.destinationStateMachine) : null;
                if (to != null && to != en) en.transitions.Add(new STrans { from = en, to = to });
            }
            foreach (var t in sm.anyStateTransitions)
            {
                var to = t.destinationState != null ? NodeForState(t.destinationState)
                    : t.destinationStateMachine != null ? NodeForSM(t.destinationStateMachine) : null;
                if (to != null && to != an) an.transitions.Add(new STrans { from = an, to = to, src = t });
            }

            LoadReroutes();            // re-apply persisted reroute points
        }

        // Does `a` equal or (recursively) contain sub-state-machine `b`?
        private static bool SMContainsSM(AnimatorStateMachine a, AnimatorStateMachine b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;
            foreach (var css in a.stateMachines) if (SMContainsSM(css.stateMachine, b)) return true;
            return false;
        }

        // All AnimatorStates in a state machine and its nested sub-state-machines.
        private static void CollectStates(AnimatorStateMachine sm, List<AnimatorState> list)
        {
            if (sm == null) return;
            foreach (var cs in sm.states) if (cs.state != null) list.Add(cs.state);
            foreach (var css in sm.stateMachines) CollectStates(css.stateMachine, list);
        }

        // Does `sm` contain `state` directly or in any nested sub-state-machine?
        private static bool SMContainsState(AnimatorStateMachine sm, AnimatorState state)
        {
            if (sm == null || state == null) return false;
            foreach (var cs in sm.states) if (cs.state == state) return true;
            foreach (var css in sm.stateMachines) if (SMContainsState(css.stateMachine, state)) return true;
            return false;
        }

        private SNode Spec(string name, Vector2 pos, bool isEntry = false, bool isExit = false, bool isAnyState = false)
        {
            var n = new SNode { name = name, position = pos, isEntry = isEntry, isExit = isExit, isAnyState = isAnyState };
            nodes.Add(n); return n;
        }

        // Enter a sub-state-machine: push it on the nav stack and rebuild the canvas around it.
        private void EnterSubSM(AnimatorStateMachine child)
        {
            if (child == null || InBlendTree) return;
            CancelRename();
            smStack.Add(child);
            AfterNavChanged();
        }

        // Pop one level out of the current sub-state-machine, back toward the layer's root SM.
        private void GoUpSM()
        {
            if (!InSubSM) return;
            CancelRename();
            smStack.RemoveAt(smStack.Count - 1);
            AfterNavChanged();
        }

        // Breadcrumb navigation: jump to a given sub-SM depth (0 = layer root), leaving any blend tree.
        private void GoToSMDepth(int depth)
        {
            depth = Mathf.Clamp(depth, 0, smStack.Count);
            CancelRename();
            btStack.Clear(); btEntryStates.Clear();
            while (smStack.Count > depth) smStack.RemoveAt(smStack.Count - 1);
            AfterNavChanged();
        }

        // Common path after sub-state-machine navigation changes.
        private void AfterNavChanged()
        {
            specialNodeBridge?.Dispose();   // native node cache is per-SM
            ClearCanvasSelection();
            BuildSMGraph();
            if (CurrentSM != null) Selection.activeObject = CurrentSM;
            lastSMName  = CurrentSM != null ? CurrentSM.name : null;
            lastNameSig = NameSignature();
            EnterView(true);   // restore this sub-SM's remembered camera, or zoom-to-fit on first visit
            RebuildLists();
            Repaint();
        }

        // Rebuild the canvas after a structural edit WITHOUT leaving the current sub-SM / blend-tree view
        // (unlike LoadCtrl, which resets navigation to the layer's root state machine).
        private void RebuildView()
        {
            if (InBlendTree) BuildBlendTreeGraph();
            else             BuildSMGraph();
            RebuildLists();
            lastStructSig = StructSignature();   // we just matched the live structure — don't re-trigger Tick
        }

        // Unity's own graph node object for an Entry/Exit/Any State pseudo-node, so the Inspector renders
        // the native node inspector. Null when reflection isn't available (caller falls back to CurrentSM).
        private Object SpecialNodeObject(SNode n)
        {
            if (InBlendTree || CurrentSM == null) return null;
            var kind = n.isEntry    ? NativeSpecialNodeBridge.Kind.Entry
                    : n.isAnyState ? NativeSpecialNodeBridge.Kind.AnyState
                    : n.isExit     ? NativeSpecialNodeBridge.Kind.Exit
                    : (NativeSpecialNodeBridge.Kind?)null;
            return kind.HasValue ? specialNodeBridge.GetNode(CurrentSM, kind.Value) : null;
        }

        // ── Blend-tree navigation ──────────────────────────────────────────────────
        // Blend-tree layout constants (graph units)
        private const float BtHeaderH = 20f, BtRowH = 17f, BtSliderRowH = 20f, BtPadTop = 4f, BtPadBot = 6f, BtSliderGap = 4f;
        private const float BtRootW   = NodeW * 1.6f;

        // Live/preview value for each blend parameter (used by the root's control sliders).
        private readonly Dictionary<string, float> blendPreview = new Dictionary<string, float>();

        private static bool IsBlend2D(BlendTree bt)
        {
            switch (bt.blendType)
            {
                case UnityEditor.Animations.BlendTreeType.SimpleDirectional2D:
                case UnityEditor.Animations.BlendTreeType.FreeformDirectional2D:
                case UnityEditor.Animations.BlendTreeType.FreeformCartesian2D:
                    return true;
                default: return false;
            }
        }

        // Blend parameters driving the tree (1 for 1D/Direct, 2 for 2D).
        private static List<string> BlendParams(BlendTree bt)
        {
            var l = new List<string> { bt.blendParameter };
            if (IsBlend2D(bt)) l.Add(bt.blendParameterY);
            return l;
        }

        private bool HasFloatParam(string name)
        {
            if (ctrl == null || string.IsNullOrEmpty(name)) return false;
            foreach (var p in ctrl.parameters)
                if (p.type == AnimatorControllerParameterType.Float && p.name == name) return true;
            return false;
        }

        // The control value comes from the live Animator in play mode; in edit mode it's the parameter's
        // default value — the same value the Animator/BlendTree inspector uses for its preview, so the
        // slider and the inspector stay in sync.
        private float GetBlendValue(string param)
        {
            if (string.IsNullOrEmpty(param)) return 0f;
            if (Application.isPlaying && liveAnim != null && HasFloatParam(param)) return liveAnim.GetFloat(param);
            // use the inspector preview store when available
            if (CurrentBT != null && BlendPreviewBridge.TryGet(CurrentBT, param, out var bv)) return bv;
            if (ctrl != null)
                foreach (var p in ctrl.parameters)
                    if (p.type == AnimatorControllerParameterType.Float && p.name == param) return p.defaultFloat;
            return blendPreview.TryGetValue(param, out var v) ? v : 0f;
        }

        private void SetBlendValue(string param, float v)
        {
            if (string.IsNullOrEmpty(param)) return;
            if (Application.isPlaying && liveAnim != null && HasFloatParam(param)) { liveAnim.SetFloat(param, v); return; }
            // keep the inspector preview in sync.
            if (CurrentBT != null && BlendPreviewBridge.TrySet(CurrentBT, param, v))
            {
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                return;
            }
            if (ctrl != null)
            {
                var ps = ctrl.parameters;
                for (int i = 0; i < ps.Length; i++)
                    if (ps[i].type == AnimatorControllerParameterType.Float && ps[i].name == param)
                    {
                        ps[i].defaultFloat = v;
                        ctrl.parameters = ps;          // fallback: write the controller's default value
                        EditorUtility.SetDirty(ctrl);
                        return;
                    }
            }
            blendPreview[param] = v;                    // last resort → local preview
        }

        // Per-child activation weights for the current control values (cached each frame in blend view).
        private float[] btWeights;
        private int     btSignature;   // detects Inspector edits (type / params / motions / thresholds)

        // A hash of everything that affects how the blend-tree graph is drawn, so edits made through the
        // Inspector (changing blend type, a motion field, thresholds, …) trigger a rebuild.
        private static int ComputeBtSignature(BlendTree bt)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)bt.blendType;
                h = h * 31 + (bt.blendParameter  != null ? bt.blendParameter.GetHashCode()  : 0);
                h = h * 31 + (bt.blendParameterY != null ? bt.blendParameterY.GetHashCode() : 0);
                var ch = bt.children;
                h = h * 31 + ch.Length;
                foreach (var c in ch)
                {
                    h = h * 31 + (c.motion != null ? c.motion.GetInstanceID() : 0);
                    h = h * 31 + (c.motion != null ? c.motion.name.GetHashCode() : 0);
                    h = h * 31 + c.threshold.GetHashCode();
                    h = h * 31 + c.position.GetHashCode();
                }
                return h;
            }
        }

        private float[] ComputeBlendWeights(BlendTree bt)
        {
            var ch = bt.children;
            int cc = ch.Length;
            var w  = new float[cc];
            if (cc == 0) return w;

            if (!IsBlend2D(bt))
            {
                // 1D: linear interpolation between the two adjacent thresholds around the control value.
                float v = GetBlendValue(bt.blendParameter);
                var order = new int[cc];
                for (int i = 0; i < cc; i++) order[i] = i;
                System.Array.Sort(order, (a, b) => ch[a].threshold.CompareTo(ch[b].threshold));

                if (v <= ch[order[0]].threshold)       { w[order[0]] = 1f; return w; }
                if (v >= ch[order[cc - 1]].threshold)  { w[order[cc - 1]] = 1f; return w; }
                for (int i = 0; i < cc - 1; i++)
                {
                    float t0 = ch[order[i]].threshold, t1 = ch[order[i + 1]].threshold;
                    if (v >= t0 && v <= t1)
                    {
                        float f = t1 > t0 ? (v - t0) / (t1 - t0) : 0f;
                        w[order[i]] = 1f - f; w[order[i + 1]] = f; return w;
                    }
                }
                return w;
            }

            // 2D: approximate with normalized inverse-square-distance weighting (Unity computes this
            // exactly per blend type; this is close enough for a visual activation shade).
            Vector2 p = new Vector2(GetBlendValue(bt.blendParameter), GetBlendValue(bt.blendParameterY));
            float total = 0f;
            for (int i = 0; i < cc; i++) { float wi = 1f / ((ch[i].position - p).sqrMagnitude + 1e-4f); w[i] = wi; total += wi; }
            if (total > 0f) for (int i = 0; i < cc; i++) w[i] /= total;
            return w;
        }

        private float BtWeight(int idx) => (btWeights != null && idx >= 0 && idx < btWeights.Length) ? btWeights[idx] : 0f;

        // Activation tint (gray → blue) shared by the blend links and the connector "●" dots.
        private static Color BtDotColor(float act)
            => Color.Lerp(new Color(0.55f, 0.55f, 0.55f), new Color(0.27f, 0.55f, 1f), Mathf.Clamp01(act));

        // Slider range for a blend parameter: X uses thresholds (1D) or child X positions (2D); Y uses child Y.
        private static void BlendRange(BlendTree bt, int paramIdx, out float min, out float max)
        {
            min = 0f; max = 1f;
            var ch = bt.children;
            if (ch.Length == 0) return;
            bool has = false;
            foreach (var c in ch)
            {
                float v = IsBlend2D(bt) ? (paramIdx == 0 ? c.position.x : c.position.y) : c.threshold;
                if (!has) { min = max = v; has = true; }
                else { min = Mathf.Min(min, v); max = Mathf.Max(max, v); }
            }
            if (max <= min) { min -= 1f; max += 1f; }
        }

        private void EnterBlendTree(BlendTree bt, AnimatorState ownerState = null)
        {
            if (bt == null) return;
            CancelRename();
            btStack.Add(bt);
            btEntryStates.Add(ownerState);   // null for a nested blend tree (breadcrumb falls back to bt.name)
            BuildBlendTreeGraph();
            Selection.activeObject = bt;
            EnterView(true);
            Repaint();
        }

        // depth = number of blend-tree levels to keep (0 = back to the layer state machine)
        private void ExitBlendTreeToDepth(int depth)
        {
            CancelRename();
            depth = Mathf.Max(0, depth);
            while (btStack.Count > depth) { btStack.RemoveAt(btStack.Count - 1); btEntryStates.RemoveAt(btEntryStates.Count - 1); }
            // Exiting all blend-tree levels returns to the current state machine (sub-SM preserved, not reset to root).
            if (btStack.Count == 0) { BuildSMGraph(); if (CurrentSM != null) Selection.activeObject = CurrentSM; }
            else { BuildBlendTreeGraph(); Selection.activeObject = CurrentBT; }
            EnterView(true);
            RebuildLists();
            Repaint();
        }

        // Build the compact blend-tree view.
        private void BuildBlendTreeGraph()
        {
            nodes.Clear();
            ClearCanvasSelection();
            var bt = CurrentBT;
            if (bt == null) { RebuildLists(); return; }

            var children = bt.children;
            int  cc          = children.Length;
            int  sliderCount = IsBlend2D(bt) ? 2 : 1;   // one control slider per blend parameter
            float rootH      = BtHeaderH + BtPadTop + cc * BtRowH + BtSliderGap + sliderCount * BtSliderRowH + BtPadBot;

            var root = new SNode
            {
                name      = bt.name,
                position  = Vector2.zero,
                size      = new Vector2(BtRootW, rootH),
                motion    = bt,
                blendTree = bt,
                isBtRoot  = true,
            };
            nodes.Add(root);

            // Generated blend-tree layout; child nodes are not user-movable.
            float childX     = BtRootW + NodeW * 0.45f;          // close to the main node
            float spacing    = NodeH * 1.35f;                     // > NodeH ⇒ no overlap
            float colHeight  = (Mathf.Max(cc, 1) - 1) * spacing;
            float startY     = rootH * 0.5f - colHeight * 0.5f - NodeH * 0.5f;
            for (int i = 0; i < cc; i++)
            {
                var cm = children[i];
                var n = new SNode
                {
                    name         = cm.motion != null ? cm.motion.name : "(None)",
                    position     = new Vector2(childX, startY + i * spacing),
                    motion       = cm.motion,
                    blendTree    = cm.motion as BlendTree,
                    isBtChild    = true,
                    btChildIndex = i,
                };
                nodes.Add(n);
                root.transitions.Add(new STrans { from = root, to = n });
            }

            btSignature = ComputeBtSignature(bt);   // remember this layout's signature for change detection
            RebuildLists();
        }

        // Double-click: the root navigates up a level; any node holding a blend tree enters it.
        private void HandleNodeDoubleClick(SNode node)
        {
            if (node == null) return;
            if (node.isUp) { GoUpSM(); return; }                                   // "(UP)" node → parent SM
            if (node.isStateMachine) { EnterSubSM(node.subSM); return; }           // sub-SM node → enter it
            if (node.isBtRoot) { ExitBlendTreeToDepth(btStack.Count - 1); return; }
            BlendTree bt = node.blendTree != null ? node.blendTree : node.motion as BlendTree;
            if (bt != null) { EnterBlendTree(bt, node.state); return; }   // node.state names the breadcrumb (null ⇒ nested)
            // Plain state nodes open their motion asset on double-click.
            if (node.state != null && node.motion != null) { Selection.activeObject = node.motion; Repaint(); }
        }

        // ── ReorderableLists ─────────────────────────────────────────────────────
        private void RebuildLists()
        {
            ctrlSO   = ctrl != null ? new SerializedObject(ctrl) : null;
            paramRL  = null;
            layerRL  = null;
            if (ctrlSO == null) return;

            // ── Parameter list ──────────────────────────────────────────────────
            paramRL = new ReorderableList(ctrlSO, ctrlSO.FindProperty("m_AnimatorParameters"), true, false, false, false);

            paramRL.elementHeight = 20f;
            paramRL.headerHeight  = 0f;   // no header reserved
            paramRL.footerHeight  = 0f;   // no footer space bleeding below the last element

            paramRL.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                if (ctrl == null || index >= ctrl.parameters.Length) return;
                var   p  = ctrl.parameters[index];
                float rx = rect.x + 2, ry = rect.y + 2;

                string badge; Color badgeBG;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:   badge = "f"; badgeBG = new Color(0.28f, 0.55f, 0.85f); break;
                    case AnimatorControllerParameterType.Int:     badge = "i"; badgeBG = new Color(0.35f, 0.72f, 0.35f); break;
                    case AnimatorControllerParameterType.Bool:    badge = "b"; badgeBG = new Color(0.85f, 0.55f, 0.18f); break;
                    default:                                       badge = "t"; badgeBG = new Color(0.75f, 0.28f, 0.28f); break;
                }
                var br = new Rect(rx, ry, 14, 15);
                EditorGUI.DrawRect(br, badgeBG * 0.7f);
                GUI.Label(br, badge, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, fontStyle = FontStyle.Bold });

                bool isBoolLike = p.type == AnimatorControllerParameterType.Bool ||
                                p.type == AnimatorControllerParameterType.Trigger;
                float valW   = isBoolLike ? 30f : 60f;
                var   valR   = new Rect(rect.xMax - valW - 2, ry, valW, 16);

                var nameRect = new Rect(rx + 18, ry - 1, rect.xMax - rx - 18 - valW - 6, 17);

                // Use cached ctrlSO (Updated in DrawParamsSide before DoLayoutList)
                var paramsProp = ctrlSO.FindProperty("m_AnimatorParameters");
                var paramProp  = paramsProp != null ? paramsProp.GetArrayElementAtIndex(index) : null;

                // Rename starts by clicking an already-selected row.
                if (paramRenameIdx == index)
                {
                    string ctrlName = "paramName" + index;
                    GUI.SetNextControlName(ctrlName);
                    EditorGUI.BeginChangeCheck();
                    var newName = EditorGUI.DelayedTextField(nameRect, p.name, EditorStyles.label);
                    bool committed = EditorGUI.EndChangeCheck();
                    if (renameFocusPending) { EditorGUI.FocusTextInControl(ctrlName); renameFocusPending = false; }
                    if (GUI.GetNameOfFocusedControl() == ctrlName) renameAcquiredFocus = true;
                    if (committed && paramProp != null)
                    {
                        if (newName != p.name) { paramProp.FindPropertyRelative("m_Name").stringValue = newName; ctrlSO.ApplyModifiedProperties(); }
                        paramRenameIdx = -1; RebuildLists(); return;
                    }
                    var ev = Event.current;
                    if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.keyCode == KeyCode.Escape))
                    { paramRenameIdx = -1; GUI.FocusControl(null); ev.Use(); }
                    else if (ev.type == EventType.Repaint && renameAcquiredFocus && GUI.GetNameOfFocusedControl() != ctrlName)
                        paramRenameIdx = -1;
                }
                else
                {
                    GUI.Label(nameRect, p.name, new GUIStyle(EditorStyles.label) { normal = { textColor = NText } });
                    var ev = Event.current;
                    if (ev.type == EventType.MouseDown && ev.button == 0 && nameRect.Contains(ev.mousePosition) && paramSelBefore == index)
                    {
                        paramRenameIdx = index; renameFocusPending = true; renameAcquiredFocus = false; ev.Use();
                    }
                }

                if (liveAnim != null && Application.isPlaying)
                {
                    try
                    {
                        switch (p.type)
                        {
                            case AnimatorControllerParameterType.Float:
                                EditorGUI.BeginChangeCheck();
                                float fv = EditorGUI.FloatField(valR, liveAnim.GetFloat(p.name));
                                if (EditorGUI.EndChangeCheck()) liveAnim.SetFloat(p.name, fv);
                                break;
                            case AnimatorControllerParameterType.Int:
                                EditorGUI.BeginChangeCheck();
                                int iv = EditorGUI.IntField(valR, liveAnim.GetInteger(p.name));
                                if (EditorGUI.EndChangeCheck()) liveAnim.SetInteger(p.name, iv);
                                break;
                            case AnimatorControllerParameterType.Bool:
                                EditorGUI.BeginChangeCheck();
                                bool bv = EditorGUI.Toggle(new Rect(valR.x + 6, valR.y, 16, 16), liveAnim.GetBool(p.name));
                                if (EditorGUI.EndChangeCheck()) liveAnim.SetBool(p.name, bv);
                                break;
                            case AnimatorControllerParameterType.Trigger:
                                if (GUI.Button(valR, "Fire", EditorStyles.miniButton)) liveAnim.SetTrigger(p.name);
                                break;
                        }
                    }
                    catch
                    {
                        // Parameter access can fail while the preview/live Animator changes.
                    }
                }
                else if (paramProp != null)
                {
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            EditorGUI.BeginChangeCheck();
                            float dfv = EditorGUI.FloatField(valR, p.defaultFloat);
                            if (EditorGUI.EndChangeCheck())
                            {
                                paramProp.FindPropertyRelative("m_DefaultFloat").floatValue = dfv;
                                ctrlSO.ApplyModifiedProperties();
                            }
                            break;
                        case AnimatorControllerParameterType.Int:
                            EditorGUI.BeginChangeCheck();
                            int div = EditorGUI.IntField(valR, p.defaultInt);
                            if (EditorGUI.EndChangeCheck())
                            {
                                paramProp.FindPropertyRelative("m_DefaultInt").intValue = div;
                                ctrlSO.ApplyModifiedProperties();
                            }
                            break;
                        case AnimatorControllerParameterType.Bool:
                        {
                            EditorGUI.BeginChangeCheck();
                            bool dbv = EditorGUI.Toggle(new Rect(valR.x + 6, valR.y, 16, 16), p.defaultBool);
                            if (EditorGUI.EndChangeCheck())
                            {
                                // defaultBool maps to m_DefaultBool; fall back to m_DefaultFloat if absent
                                var bp = paramProp.FindPropertyRelative("m_DefaultBool");
                                if (bp != null) bp.boolValue = dbv;
                                else paramProp.FindPropertyRelative("m_DefaultFloat").floatValue = dbv ? 1f : 0f;
                                ctrlSO.ApplyModifiedProperties();
                            }
                            break;
                        }
                        case AnimatorControllerParameterType.Trigger:
                            GUI.Label(valR, "(trigger)", new GUIStyle(EditorStyles.miniLabel)
                                { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.5f, 0.5f, 0.5f) } });
                            break;
                    }
                }

                if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
                {
                    int idx = index;
                    var m = new GenericMenu();
                    m.AddItem(new GUIContent("Delete Parameter"), false, () => DeleteParameter(idx));
                    m.ShowAsContext();
                    Event.current.Use();
                }

                // Black separator line between parameters
                EditorGUI.DrawRect(new Rect(rect.x - 20, rect.yMax - 1, rect.width + 40, 1), Color.black);
            };

            paramRL.onAddDropdownCallback = (btnRect, list) =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Float"),   false, () => AddParam("New Float",   AnimatorControllerParameterType.Float));
                menu.AddItem(new GUIContent("Int"),     false, () => AddParam("New Int",     AnimatorControllerParameterType.Int));
                menu.AddItem(new GUIContent("Bool"),    false, () => AddParam("New Bool",    AnimatorControllerParameterType.Bool));
                menu.AddItem(new GUIContent("Trigger"), false, () => AddParam("New Trigger", AnimatorControllerParameterType.Trigger));
                menu.ShowAsContext();
            };

            paramRL.onRemoveCallback = list =>
            {
                ctrlSO.Update();
                ctrlSO.FindProperty("m_AnimatorParameters").DeleteArrayElementAtIndex(list.index);
                ctrlSO.ApplyModifiedProperties();
                RebuildLists(); Repaint();
            };

            paramRL.onReorderCallbackWithDetails = (list, oldIdx, newIdx) =>
            {
                // SerializedObject already applied the reorder.
                Repaint();
            };

            // ── Layer list ──────────────────────────────────────────────────────
            layerRL = new ReorderableList(ctrlSO, ctrlSO.FindProperty("m_AnimatorLayers"), true, false, false, false);

            layerRL.elementHeight = 40f;
            layerRL.headerHeight  = 0f;   // no header reserved
            layerRL.footerHeight  = 0f;   // no footer space bleeding below the last element

            layerRL.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                if (ctrl == null || index >= ctrl.layers.Length) return;
                bool isCur = index == layer;

                if (isCur)
                    EditorGUI.DrawRect(new Rect(rect.x - 20, rect.y, rect.width + 40, rect.height), new Color(0.22f, 0.35f, 0.58f, 0.55f));

                var nameStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = isCur ? FontStyle.Bold : FontStyle.Normal,
                    normal    = { textColor = Color.white }
                };
                var lnRect = new Rect(rect.x + 2, rect.y + 3, rect.width - 28, 16);

                // Rename starts by clicking the selected layer name again.
                if (layerRenameIdx == index)
                {
                    string ctrlName = "layerName" + index;
                    GUI.SetNextControlName(ctrlName);
                    EditorGUI.BeginChangeCheck();
                    var newName = EditorGUI.DelayedTextField(lnRect, ctrl.layers[index].name, nameStyle);
                    bool committed = EditorGUI.EndChangeCheck();
                    if (renameFocusPending) { EditorGUI.FocusTextInControl(ctrlName); renameFocusPending = false; }
                    if (GUI.GetNameOfFocusedControl() == ctrlName) renameAcquiredFocus = true;
                    if (committed)
                    {
                        if (!string.IsNullOrEmpty(newName) && newName != ctrl.layers[index].name)
                        {
                            Undo.RecordObject(ctrl, "Rename Layer");
                            var layers = ctrl.layers; layers[index].name = newName; ctrl.layers = layers;
                            // Keep the layer's root state machine name in sync (Unity treats them as one).
                            var rsm = layers[index].stateMachine;
                            if (rsm != null) { Undo.RecordObject(rsm, "Rename Layer"); rsm.name = newName; if (index == layer) lastSMName = newName; }
                            EditorUtility.SetDirty(ctrl);
                        }
                        layerRenameIdx = -1; RebuildLists(); Repaint(); return;
                    }
                    var ev = Event.current;
                    if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.keyCode == KeyCode.Escape))
                    { layerRenameIdx = -1; GUI.FocusControl(null); ev.Use(); }
                    else if (ev.type == EventType.Repaint && renameAcquiredFocus && GUI.GetNameOfFocusedControl() != ctrlName)
                        layerRenameIdx = -1;
                }
                else
                {
                    GUI.Label(lnRect, ctrl.layers[index].name, nameStyle);
                    var ev = Event.current;
                    if (ev.type == EventType.MouseDown && ev.button == 0 && lnRect.Contains(ev.mousePosition) && index == layer)
                    {
                        layerRenameIdx = index; renameFocusPending = true; renameAcquiredFocus = false; ev.Use();
                    }
                }

                // Cog button
                var cogContent = EditorGUIUtility.IconContent("_Popup");
                if (cogContent.image == null) cogContent = new GUIContent("⚙");
                var cogRect = new Rect(rect.xMax - 22, rect.y + 3, 20, 16);
                if (GUI.Button(cogRect, cogContent, CogStyle))
                {
                    int capturedIdx = index;
                    PopupWindow.Show(cogRect, new LayerSettingsPopup(ctrl, capturedIdx));
                }

                // Sync badge to the left of the cog: "S" when synced, "S+T" when it also affects timing
                if (ctrl.layers[index].syncedLayerIndex >= 0)
                {
                    bool   timing  = ctrl.layers[index].syncedLayerAffectsTiming;
                    var    bStyle  = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.42f, 0.66f, 1f) } };
                    var    badge   = new GUIContent(timing ? "S+T" : "S", "Synced layer" + (timing ? " (affects timing)" : ""));
                    GUI.Label(new Rect(cogRect.x - 30, cogRect.y, 28, 16), badge, bStyle);
                }

                // Weight fill bar below the name — display only (edit via the cog menu).
                float wv    = index == 0 ? 1f : Mathf.Clamp01(ctrl.layers[index].defaultWeight);
                var   track = new Rect(rect.x + 4, rect.y + 26, rect.width - 28, 1.5f);
                EditorGUI.DrawRect(track, Hex("545454"));                                                  // track
                EditorGUI.DrawRect(new Rect(track.x, track.y, track.width * wv, track.height), Hex("8b8b8b")); // fill

                // Black separator line between layers
                EditorGUI.DrawRect(new Rect(rect.x - 20, rect.yMax - 1, rect.width + 40, 1), Color.black);

                // Click to activate layer (whole row; cog button consumes its own click)
                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition) && index != layer)
                {
                    LoadCtrl(ctrl, index);
                    // Reflect the new layer's root state machine in the Inspector so its settings /
                    // StateMachineBehaviours target THIS layer (and renaming there maps to this layer).
                    if (!InBlendTree && CurrentSM != null) Selection.activeObject = CurrentSM;
                    Event.current.Use();
                    GUIUtility.ExitGUI();
                }

                if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
                {
                    int idx = index;
                    var m = new GenericMenu();
                    if (idx > 0) m.AddItem(new GUIContent("Delete Layer"), false, () => DeleteLayer(idx));
                    else         m.AddDisabledItem(new GUIContent("Delete Layer"));
                    m.ShowAsContext();
                    Event.current.Use();
                }
            };

            layerRL.onAddCallback = list => AddLayerUndoable();

            layerRL.onRemoveCallback = list =>
            {
                if (ctrl.layers.Length <= 1 || list.index <= 0) return;   // never remove the base layer
                Undo.RecordObject(ctrl, "Delete Layer");
                ctrl.RemoveLayer(list.index);
                EditorUtility.SetDirty(ctrl);
                LoadCtrl(ctrl, Mathf.Clamp(layer, 0, ctrl.layers.Length - 1));
                Repaint();
            };

            layerRL.onReorderCallbackWithDetails = (list, oldIdx, newIdx) =>
            {
                // Commit the reordered array to the asset BEFORE LoadCtrl rebuilds the
                // SerializedObject (otherwise the fresh read reverts to the old order).
                ctrlSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(ctrl);
                int newLayer = layer;
                if      (layer == oldIdx)  newLayer = newIdx;
                else if (layer > oldIdx && layer <= newIdx) newLayer = layer - 1;
                else if (layer < oldIdx && layer >= newIdx) newLayer = layer + 1;
                LoadCtrl(ctrl, newLayer);
                Repaint();
            };
        }

        // ── Live animator ─────────────────────────────────────────────────────────
        private Animator FindLiveAnimator()
        {
            if (ctrl == null) return null;
            try
            {
                var tt = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Graphs.AnimatorControllerTool");
                if (tt != null)
                {
                    foreach (var w in Resources.FindObjectsOfTypeAll(tt))
                    {
                        foreach (var pn in new[] { "previewAnimator", "m_PreviewAnimator" })
                        {
                            var prop = tt.GetProperty(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (prop?.GetValue(w) is Animator pa && pa != null) return pa;
                            var fld  = tt.GetField(pn,  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (fld?.GetValue(w) is Animator fa && fa != null) return fa;
                        }
                    }
                }
            }
            catch
            {
                // Preview Animator access depends on Unity internals; fall back below.
            }

            var go = Selection.activeGameObject;
            if (go != null) { var sa = go.GetComponent<Animator>(); if (sa != null && sa.runtimeAnimatorController == ctrl) return sa; }
            if (Application.isPlaying)
                foreach (var a in FindObjectsByType<Animator>(FindObjectsSortMode.None))
                    if (a.runtimeAnimatorController == ctrl) return a;
            return null;
        }

        private void PollLive()
        {
            if (liveAnim == null || !liveAnim.isActiveAndEnabled) return;
            try
            {
                var cur = liveAnim.GetCurrentAnimatorStateInfo(layer);

                liveHash    = cur.shortNameHash;
                liveInTrans = liveAnim.IsInTransition(layer);

                // StateInfo.normalizedTime keeps increasing on looped clips. The stock Animator
                // shows the state bar as completed once the first pass is done, not as a repeating loop.
                liveNT = liveInTrans ? 1f : Mathf.Clamp01(cur.normalizedTime);

                if (liveInTrans)
                {
                    liveNextHash = liveAnim.GetNextAnimatorStateInfo(layer).shortNameHash;
                    liveTNT      = Mathf.Clamp01(liveAnim.GetAnimatorTransitionInfo(layer).normalizedTime);
                }
                else
                {
                    liveNextHash = 0;
                    liveTNT      = 0f;
                }
            }
            catch
            {
                // Live polling is best-effort while controllers, layers, or preview objects change.
            }
        }

        private bool IsLive(SNode n)  => autoLiveLink && Application.isPlaying && n.state != null && Animator.StringToHash(n.name) == liveHash;
        private bool IsNext(SNode n)  => autoLiveLink && Application.isPlaying && liveInTrans && n.state != null && Animator.StringToHash(n.name) == liveNextHash;
        private bool IsActiveTrans(STrans t) =>
            autoLiveLink && Application.isPlaying && liveInTrans &&
            t.from?.state != null && Animator.StringToHash(t.from.name) == liveHash &&
            t.to?.state   != null && Animator.StringToHash(t.to.name)   == liveNextHash;

        // ── Coordinates ───────────────────────────────────────────────────────────
        // Effective sidebar width — collapses to 0 when the panel is hidden so the canvas fills the window.
        private float SideWNow => showSidebar ? SideW : 0f;
        private Rect CVS => new Rect(SideWNow, ToolH, position.width - SideWNow, position.height - ToolH);
        private Rect SidebarRect => new Rect(0, ToolH, SideW, position.height - ToolH);

        private Vector2 G2C(Vector2 g)  => g * zoom + pan;
        private Vector2 C2G(Vector2 c)  => (c - pan) / zoom;
        private Vector2 W2G(Vector2 w)  => C2G(w - new Vector2(SideWNow, ToolH));
        private Vector2 G2W(Vector2 g)  => G2C(g) + new Vector2(SideWNow, ToolH);

        // A node's size in graph units ((0,0) means use the default NodeW×NodeH; blend-tree roots differ)
        private static Vector2 NodeSize(SNode n) => (n.size.x > 0f && n.size.y > 0f) ? n.size : new Vector2(NodeW, NodeH);
        private Rect NodeWinRect(SNode n) => new Rect(G2W(n.position), NodeSize(n) * zoom);

        // Node bounds in graph space (for marquee hit-testing)
        private Rect NodeGraphRect(SNode n) => new Rect(n.position, NodeSize(n));

        // Current marquee rectangle in graph space (anchor → live mouse)
        private Rect MarqueeRectG()
        {
            Vector2 cur = W2G(lastMouseWin);
            return Rect.MinMaxRect(
                Mathf.Min(boxStartG.x, cur.x), Mathf.Min(boxStartG.y, cur.y),
                Mathf.Max(boxStartG.x, cur.x), Mathf.Max(boxStartG.y, cur.y));
        }

        // True when a node should render as selected (explicit selection, transition source, or live marquee preview)
        private bool IsSel(SNode n)
        {
            if (n == makingTransFrom)   return true;
            if (selNodes.Contains(n))   return true;
            if (boxSelecting && (n.state != null || n.isStateMachine) && MarqueeRectG().Overlaps(NodeGraphRect(n))) return true;
            return false;
        }

        // True when a transition should render as selected (explicit click, marquee set, or live preview)
        private bool IsTransSel(STrans t)
        {
            if (t == selTrans)         return true;
            if (selTranses.Contains(t)) return true;
            if (boxSelecting && TransInMarquee(t, MarqueeRectG())) return true;
            return false;
        }

        // Does any segment of a transition's polyline touch the (graph-space) rect?
        private bool TransInMarquee(STrans t, Rect g)
        {
            if (t.from == null || t.to == null) return false;
            Vector2 half = new Vector2(NodeW * .5f, NodeH * .5f);
            Vector2 prev = t.from.position + half;
            foreach (var rp in t.pts)
            {
                if (SegmentIntersectsRect(prev, rp, g)) return true;
                prev = rp;
            }
            return SegmentIntersectsRect(prev, t.to.position + half, g);
        }

        private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, Rect r)
        {
            if (r.Contains(a) || r.Contains(b)) return true;
            Vector2 tl = new Vector2(r.xMin, r.yMin), tr = new Vector2(r.xMax, r.yMin);
            Vector2 br = new Vector2(r.xMax, r.yMax), bl = new Vector2(r.xMin, r.yMax);
            return SegSeg(a, b, tl, tr) || SegSeg(a, b, tr, br) ||
                SegSeg(a, b, br, bl) || SegSeg(a, b, bl, tl);
        }

        private static bool SegSeg(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2);
            float d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        // ── OnGUI ─────────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                // First visit to a view → zoom-to-fit, then remember it so we restore (not refit) next time.
                if (needFit)    { needFit = false; ZoomToFit(); StoreCurrentView(); }
                if (needCenter) { needCenter = false; Recenter(); }
            }
            lastMouseWin = e.mousePosition;

            // Keep the blend-tree graph in sync with edits made through the Inspector (add/remove child).
            if (InBlendTree && e.type == EventType.Layout)
            {
                if (CurrentBT == null) ExitBlendTreeToDepth(btStack.Count - 1);          // tree deleted → go up
                else if (ComputeBtSignature(CurrentBT) != btSignature) BuildBlendTreeGraph();  // Inspector edit → redraw
            }
            // Recompute per-child activation each frame from the current control values.
            btWeights = InBlendTree && CurrentBT != null ? ComputeBlendWeights(CurrentBT) : null;

            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), BG);

            DrawToolbar();
            DrawSidebar();
            if (showSidebar)
                EditorGUIUtility.AddCursorRect(new Rect(SideW - 3, ToolH, 6, position.height - ToolH), MouseCursor.ResizeHorizontal);

            GUI.BeginClip(CVS);
            DrawGrid();
            DrawTransitions();
            if (makingTransFrom != null) DrawRubberBand();           // behind the nodes, like a real transition
            DrawNodes();
            if (makingTransFrom != null) DrawTransTargetHighlight();  // subtle hover highlight, on top
            if (boxSelecting) DrawMarquee();
            GUI.EndClip();

            DrawCanvasFooter();
            DrawCanvasInnerShadow();
            DrawOverlay();
            HandleEvents(e);

            // Escape: cancel make-transition / marquee, or step up one blend-tree level
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (makingTransFrom != null || boxSelecting) { makingTransFrom = null; boxSelecting = false; e.Use(); Repaint(); }
                else if (InBlendTree) { ExitBlendTreeToDepth(btStack.Count - 1); e.Use(); Repaint(); }
            }

            if (GUI.changed) Repaint();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────────
        private void DrawToolbar()
        {
            // ── Left zone: side-panel tabs + visibility eye (aligned over the sidebar) ──
            GUILayout.BeginArea(new Rect(0, 0, SideW, ToolH));
            EditorGUI.DrawRect(new Rect(0, 0, SideW, ToolH), Hex("3c3c3c"));   // flat bg (no toolbar gradient)
            GUILayout.BeginHorizontal(GUILayout.Height(ToolH));

            if (GUILayout.Toggle(showSidebar && sideTab == 0, "Layers", TabStyle, GUILayout.Height(ToolH)))
                { if (sideTab != 0) CancelRename(); sideTab = 0; showSidebar = true; }
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1, ToolH, GUILayout.Width(1), GUILayout.Height(ToolH)), Color.black);
            if (GUILayout.Toggle(showSidebar && sideTab == 1, "Parameters", TabStyle, GUILayout.Height(ToolH)))
                { if (sideTab != 1) CancelRename(); sideTab = 1; showSidebar = true; }

            GUILayout.FlexibleSpace();

            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1, ToolH, GUILayout.Width(1), GUILayout.Height(ToolH)), Color.black);
            var eye = EditorGUIUtility.IconContent(showSidebar ? "animationvisibilitytoggleon" : "animationvisibilitytoggleoff");
            if (eye == null || eye.image == null) eye = new GUIContent(showSidebar ? "◉" : "○");
            eye.tooltip = "Show / hide the side panel";
            if (GUILayout.Button(eye, TabStyle, GUILayout.Width(30), GUILayout.Height(ToolH)))
                { CancelRename(); showSidebar = !showSidebar; }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ── Right zone: breadcrumb + auto live link + zoom (over the canvas) ──
            GUILayout.BeginArea(new Rect(SideW, 0, Mathf.Max(position.width - SideW, 0f), ToolH));
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (ctrl != null && ctrl.layers.Length > 0)
                DrawBreadcrumbs();

            GUILayout.FlexibleSpace();

            // Live-link target name (when active & playing)
            if (autoLiveLink && liveAnim != null && Application.isPlaying)
            {
                var ls = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.28f, 0.82f, 0.38f) } };
                GUILayout.Label("● " + liveAnim.gameObject.name, ls);
                GUILayout.Space(4);
            }

            // Snap-to-grid toggle
            var snapC = new GUIContent("Snap to grid", "Snap nodes to the grid while dragging");
            bool newSnap = GUILayout.Toggle(snapToGrid, snapC, EditorStyles.toolbarButton);
            if (newSnap != snapToGrid) { snapToGrid = newSnap; Repaint(); }

            // Zoom reset
            if (GUILayout.Button($"{zoom * 100f:F0}%", EditorStyles.toolbarButton, GUILayout.Width(46)))
                { zoom = 1f; Recenter(); }

            // Auto Live Link toggle (far right)
            var llIcon = EditorGUIUtility.IconContent("Animator Icon");
            var ll = (llIcon != null && llIcon.image != null) ? new GUIContent(llIcon.image) : new GUIContent("Live");
            ll.tooltip = "Auto Live Link — play in sync with the Game view";
            bool newLL = GUILayout.Toggle(autoLiveLink, ll, EditorStyles.toolbarButton, GUILayout.Width(34));
            if (newLL != autoLiveLink) { autoLiveLink = newLL; if (autoLiveLink) liveAnim = FindLiveAnimator(); Repaint(); }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // Animator-style breadcrumb segment: a box with a right-pointing tip so segments
        // interlock. Returns true on click. Drawn manually (the built-in skin style is unreliable).
        private static GUIStyle BreadcrumbStyle(bool selected) => new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Normal,
            normal    = { textColor = selected ? new Color(0.82f, 0.82f, 0.82f) : new Color(0.62f, 0.62f, 0.62f) }
        };

        // Animator-style breadcrumb: layer segment + one per blend-tree level, each a box with a right
        // tip. To look slotted, segments after the first overlap the previous tip and are drawn back-to-
        // front (rightmost first) so each left segment's tip sits ON TOP of the next one.
        private void DrawBreadcrumbs()
        {
            const float tip = 8f, padX = 3f;

            var texts = new System.Collections.Generic.List<string>();
            var sels  = new System.Collections.Generic.List<bool>();
            var acts  = new System.Collections.Generic.List<System.Action>();

            // Layer segment (root) — selected only when we're at the layer's root SM with no blend tree open.
            texts.Add(ctrl.layers[layer].name);
            sels.Add(!InBlendTree && !InSubSM);
            acts.Add(() => GoToSMDepth(0));

            // Sub-state-machine segments.
            for (int i = 0; i < smStack.Count; i++)
            {
                bool last = (i == smStack.Count - 1) && !InBlendTree;
                int  depth = i + 1;
                texts.Add(smStack[i].name); sels.Add(last);
                acts.Add(() => GoToSMDepth(depth));
            }

            // Blend-tree segments (deepest navigation, on top of any sub-SM).
            for (int i = 0; i < btStack.Count; i++)
            {
                bool last = i == btStack.Count - 1;
                int  d    = i + 1;
                // Owning state's name (like Unity) when entered from a state; else the blend tree's own name.
                string label = (i < btEntryStates.Count && btEntryStates[i] != null) ? btEntryStates[i].name : btStack[i].name;
                texts.Add(label); sels.Add(last);
                acts.Add(() => { if (d >= btStack.Count) Recenter(); else ExitBlendTreeToDepth(d); });
            }

            // Layout (left→right) — reserve each rect; segments past the first overlap the previous tip.
            int n = texts.Count;
            var rects  = new Rect[n];
            var styles = new GUIStyle[n];
            for (int k = 0; k < n; k++)
            {
                styles[k] = BreadcrumbStyle(sels[k]);
                float w   = styles[k].CalcSize(new GUIContent(texts[k])).x + padX * 2f + tip;
                var   lr  = GUILayoutUtility.GetRect(w, ToolH, GUILayout.Width(w));
                float ox  = k > 0 ? tip : 0f;
                rects[k]  = new Rect(lr.x - ox, -1f, w + ox, ToolH-1f);
            }

            // Draw back-to-front so the left segment's tip overlaps onto the right one.
            if (Event.current.type == EventType.Repaint)
                for (int k = n - 1; k >= 0; k--)
                    DrawBreadcrumbPoly(rects[k], texts[k], styles[k], sels[k], k > 0, tip);

            // Click: the left (top-most) segment wins in any overlap region.
            if (Event.current.type == EventType.MouseDown)
                for (int k = 0; k < n; k++)
                    if (rects[k].Contains(Event.current.mousePosition)) { Event.current.Use(); acts[k](); break; }
        }

        private void DrawBreadcrumbPoly(Rect r, string text, GUIStyle style, bool selected, bool interlock, float tip)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Vector3[] pts =
            {
                new Vector3(r.x,          r.y),
                new Vector3(r.xMax - tip, r.y),
                new Vector3(r.xMax,       r.y + r.height * 0.5f),
                new Vector3(r.xMax - tip, r.yMax),
                new Vector3(r.x,          r.yMax),
            };
            Handles.BeginGUI();
            Handles.color = hover ? Hex("3d3d3d") : (selected ? Hex("333333") : Hex("333333"));
            Handles.DrawAAConvexPolygon(pts);
            Handles.color = Color.black;
            if (!interlock) Handles.DrawAAPolyLine(1.2f, pts[0], pts[4]);   // left edge (skipped when interlocking)
            Handles.DrawAAPolyLine(1.2f, pts[1], pts[2], pts[3]);          // right tip only
            Handles.EndGUI();

            float textLeft = interlock ? tip + 6f : 0f;
            GUI.Label(new Rect(r.x + textLeft, 0f, r.width - tip - textLeft, ToolH), text, style);
        }

        // ── Sidebar ───────────────────────────────────────────────────────────────
        private void DrawSidebar()
        {
            if (!showSidebar) return;   // tabs live in the top bar; eye toggles this panel

            var sideRect = new Rect(0, ToolH, SideW, position.height - ToolH);
            EditorGUI.DrawRect(sideRect, SideBG);
            Handles.BeginGUI();
            Handles.color = SideLine;
            Handles.DrawLine(new Vector3(SideW, ToolH), new Vector3(SideW, position.height));
            Handles.EndGUI();

            GUILayout.BeginArea(sideRect);

            if (ctrl == null)
            {
                GUILayout.Space(6);
                EditorGUILayout.HelpBox("Select an AnimatorController.", MessageType.Info);
            }
            else if (sideTab == 0) DrawLayersSide();
            else                   DrawParamsSide();

            GUILayout.EndArea();
        }

        // Add a parameter (undo-able).
        private void AddParam(string name, AnimatorControllerParameterType type)
        {
            if (ctrl == null) return;
            Undo.RecordObject(ctrl, "Add Parameter");
            ctrl.AddParameter(name, type);
            EditorUtility.SetDirty(ctrl);
            RebuildLists(); Repaint();
        }

        // Add a layer (undo-able).
        private void AddLayerUndoable()
        {
            if (ctrl == null) return;
            Undo.RecordObject(ctrl, "Add Layer");
            ctrl.AddLayer("New Layer");
            EditorUtility.SetDirty(ctrl);
            RebuildLists(); Repaint();
        }

        private void DeleteLayer(int idx)
        {
            // index 0 is the base layer — not deletable (matches Unity)
            if (ctrl == null || idx <= 0 || idx >= ctrl.layers.Length) return;
            Undo.RecordObject(ctrl, "Delete Layer");
            ctrl.RemoveLayer(idx);
            EditorUtility.SetDirty(ctrl);
            LoadCtrl(ctrl, Mathf.Clamp(layer, 0, ctrl.layers.Length - 1));
            Repaint();
        }

        private void DeleteParameter(int idx)
        {
            if (ctrl == null || idx < 0 || idx >= ctrl.parameters.Length) return;
            ctrlSO.Update();
            // ApplyModifiedProperties registers its own undo step.
            ctrlSO.FindProperty("m_AnimatorParameters").DeleteArrayElementAtIndex(idx);
            ctrlSO.ApplyModifiedProperties();
            RebuildLists(); Repaint();
        }

        private void ShowAddParameterMenu()
        {
            if (ctrl == null) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Float"),   false, () => AddParam("New Float",   AnimatorControllerParameterType.Float));
            menu.AddItem(new GUIContent("Int"),     false, () => AddParam("New Int",     AnimatorControllerParameterType.Int));
            menu.AddItem(new GUIContent("Bool"),    false, () => AddParam("New Bool",    AnimatorControllerParameterType.Bool));
            menu.AddItem(new GUIContent("Trigger"), false, () => AddParam("New Trigger", AnimatorControllerParameterType.Trigger));
            menu.ShowAsContext();
        }

        private void DrawLayersSide()
        {
            if (layerRL == null) RebuildLists();
            ctrlSO?.Update();

            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true)), Hex("232323"));
            // Top bar (same height as the Parameters search bar) with a right-aligned + button
            Rect bar = GUILayoutUtility.GetRect(1, 22, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(bar, Hex("3c3c3c"));
            var addRect = new Rect(bar.xMax - 38, bar.y + 1, 36, bar.height - 2);
            if (GUI.Button(addRect, "+", PlusStyle) && ctrl != null)
                AddLayerUndoable();

            // 1px black line separating the top section from the list
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true)), Hex("232323"));

            // Supr / Delete removes the current (non-base) layer. Intercept BEFORE the list so the
            // ReorderableList doesn't swallow the key; defer the actual delete so we don't mutate the
            // list mid-layout. Skipped while inline-renaming a layer.
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace)
                && ctrl != null && layer > 0 && layer < ctrl.layers.Length
                && layerRenameIdx < 0 && SidebarRect.Contains(lastMouseWin))
            {
                int idx = layer;
                e.Use();
                EditorApplication.delayCall += () => { DeleteLayer(idx); Repaint(); };
            }

            layerRL?.DoLayoutList();
        }

        private void DrawParamsSide()
        {
            if (paramRL == null) RebuildLists();
            ctrlSO?.Update();

            // 3px black line separating the tabs from this section
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true)), Hex("232323"));

            // Search box + add (+) button
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            var newSearch = EditorGUILayout.TextField(paramSearch, EditorStyles.toolbarSearchField);
            if (newSearch != paramSearch) { paramSearch = newSearch; Repaint(); }
            if (GUILayout.Button("+", PlusStyle, GUILayout.Width(40))) ShowAddParameterMenu();
            GUILayout.EndHorizontal();

            // 1px black line separating the top section from the list
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true)), Hex("232323"));

            // Show filtered list if searching, full reorderable list otherwise
            if (!string.IsNullOrEmpty(paramSearch))
            {
                GUILayout.Space(2);
                string lowerSearch = paramSearch.ToLower();
                bool any = false;
                foreach (var p in ctrl.parameters)
                {
                    if (!p.name.ToLower().Contains(lowerSearch)) continue;
                    any = true;
                    GUILayout.BeginHorizontal(GUILayout.Height(20));
                    GUILayout.Space(6);
                    string badge; Color bg;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float:   badge = "f"; bg = new Color(0.28f, 0.55f, 0.85f); break;
                        case AnimatorControllerParameterType.Int:     badge = "i"; bg = new Color(0.35f, 0.72f, 0.35f); break;
                        case AnimatorControllerParameterType.Bool:    badge = "b"; bg = new Color(0.85f, 0.55f, 0.18f); break;
                        default:                                       badge = "t"; bg = new Color(0.75f, 0.28f, 0.28f); break;
                    }
                    var br = GUILayoutUtility.GetRect(14, 15, GUILayout.Width(14), GUILayout.Height(15));
                    br.y += 2;
                    EditorGUI.DrawRect(br, bg * 0.7f);
                    GUI.Label(br, badge, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, fontStyle = FontStyle.Bold });
                    GUILayout.Space(4);
                    GUILayout.Label(p.name, new GUIStyle(EditorStyles.label) { normal = { textColor = NText } });
                    GUILayout.EndHorizontal();
                }
                if (!any) EditorGUILayout.LabelField("No match", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                GUILayout.Space(2);
                // Capture the selection BEFORE the list processes this event, so the name callback can
                // tell a first (selecting) click from a second (rename) click on the same row.
                if (paramRL != null && Event.current.type == EventType.MouseDown) paramSelBefore = paramRL.index;
                paramRL?.DoLayoutList();

                // Supr / Delete removes the selected parameter (when the mouse is over the sidebar)
                var e = Event.current;
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace)
                    && paramRL != null && paramRL.index >= 0 && SidebarRect.Contains(lastMouseWin))
                {
                    DeleteParameter(paramRL.index); e.Use();
                }
            }
        }

        // ── Grid ─────────────────────────────────────────────────────────────────
        private void DrawGrid()
        {
            float w = CVS.width, h = CVS.height;
            DrawGridLayer(GridSnap,  GridSm, w, h);
            DrawGridLayer(100f, GridLg, w, h);
        }

        private void DrawGridLayer(float gs, Color col, float w, float h)
        {
            float step = gs * zoom;
            float xo = pan.x % step; if (xo < 0) xo += step;
            float yo = pan.y % step; if (yo < 0) yo += step;
            Handles.BeginGUI();
            Handles.color = col;
            for (float x = xo; x < w; x += step) Handles.DrawLine(new Vector3(x, 0), new Vector3(x, h));
            for (float y = yo; y < h; y += step) Handles.DrawLine(new Vector3(0, y), new Vector3(w, y));
            Handles.EndGUI();
        }

        // ── Rubber-band line for Make Transition ──────────────────────────────────
        // The valid target node under the cursor while wiring a transition (null = none / invalid).
        private SNode HoveredTransTarget()
        {
            if (makingTransFrom == null) return null;
            foreach (var n in nodes)
            {
                if (n == makingTransFrom || n.isEntry) continue;
                if (NodeWinRect(n).Contains(lastMouseWin)) return n;
            }
            return null;
        }

        private void DrawRubberBand()
        {
            // Called inside GUI.BeginClip(CVS), so coords are canvas-local
            Vector2 from    = G2C(makingTransFrom.position + NodeSize(makingTransFrom) * 0.5f);
            var     hovered = HoveredTransTarget();
            Vector2 to      = hovered != null
                ? G2C(hovered.position + NodeSize(hovered) * 0.5f)        // snap to the target's centre
                : lastMouseWin - new Vector2(SideWNow, ToolH);            // else follow the mouse

            Handles.BeginGUI();
            Handles.color = Color.white;                                  // white, like a normal transition (not blue)
            Handles.DrawAAPolyLine(3f, from, to);                         // same width as a normal transition line
            DrawArrowHead(from, to, Color.white, 5f);
            Handles.EndGUI();

            Repaint();
        }

        // Ultra-subtle white highlight on the node the transition would snap to (drawn on top of the nodes).
        private void DrawTransTargetHighlight()
        {
            var n = HoveredTransTarget();
            if (n == null) return;
            Rect hr = new Rect(G2C(n.position), NodeSize(n) * zoom);
            var  hi = new Color(1f, 1f, 1f, 0.10f);
            if (n.isStateMachine || n.isUp) FillBeveledConvex(hr, Mathf.Min(hr.height * 0.5f, 14f * zoom), hi);
            else                            FillRoundedConvex(hr, 4f * zoom, hi);
        }

        // ── Marquee selection box ──────────────────────────────────────────────────
        private void DrawMarquee()
        {
            // Called inside GUI.BeginClip(CVS) — G2C gives canvas-local coords
            Rect    g = MarqueeRectG();
            Vector2 a = G2C(new Vector2(g.xMin, g.yMin));
            Vector2 b = G2C(new Vector2(g.xMax, g.yMax));
            Rect    c = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                                        Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            EditorGUI.DrawRect(c, new Color(0.28f, 0.55f, 1f, 0.12f));
            Handles.BeginGUI();
            Handles.color = RubberCol;
            Handles.DrawAAPolyLine(1.5f,
                new Vector3(c.xMin, c.yMin), new Vector3(c.xMax, c.yMin),
                new Vector3(c.xMax, c.yMax), new Vector3(c.xMin, c.yMax),
                new Vector3(c.xMin, c.yMin));
            Handles.EndGUI();

            Repaint();
        }

        // ── Transitions ───────────────────────────────────────────────────────────
        private void DrawTransitions()
        {
            Handles.BeginGUI();

            // Group straight (no-reroute) transitions that share the same from→to pair, so parallel
            // transitions draw a single line with stacked arrowheads (Unity's "≫" multi-transition mark).
            var groupOf = BuildTransGroups();

            foreach (var node in nodes)
            {
                foreach (var t in node.transitions)
                {
                    // Blend-tree link: curved spline from the root's right edge
                    // at the motion row to the child's lower third.
                    if (t.from != null && t.from.isBtRoot)
                    {
                        int idx = t.to != null ? t.to.btChildIndex : 0;

                        Vector2 dot = new Vector2(
                            t.from.position.x + NodeSize(t.from).x,
                            t.from.position.y + BtHeaderH + BtPadTop + BtRowH * idx + BtRowH * 0.5f
                        );

                        Vector2 dst = new Vector2(
                            t.to.position.x,
                            t.to.position.y + NodeH * (2f / 3f)
                        );

                        // Gray when inactive → blue when fully active (Unity-style).
                        float  act  = Mathf.Clamp01(t.to != null ? BtWeight(t.to.btChildIndex) : 0f);
                        Color  col  = IsTransSel(t) ? NSelBord : Color.Lerp(new Color(0.55f, 0.55f, 0.55f), new Color(0.27f, 0.55f, 1f), act);
                        float  lw   = Mathf.Lerp(2f, 3.6f, act);

                        float dx = Mathf.Abs(dst.x - dot.x);
                        float tangent = Mathf.Clamp(dx * 0.5f, 35f, 120f);

                        Vector2 c1 = dot + Vector2.right * tangent;
                        Vector2 c2 = dst + Vector2.left  * tangent;

                        // Some Unity versions ignore the bezier's color arg and use Handles.color — set both.
                        Handles.color = col;
                        Handles.DrawBezier(G2C(dot), G2C(dst), G2C(c1), G2C(c2), col, null, lw);
                        continue;
                    }
                    // Multi-transition grouping: draw each from→to group once, with N (≤3) arrowheads.
                    int arrowCount = 1;
                    if (groupOf.TryGetValue(t, out var grp))
                    {
                        if (!grp.rep) continue;                 // non-representative → already drawn by the rep
                        arrowCount = Mathf.Min(grp.count, 3);
                    }

                    bool isSel    = IsTransSel(t);
                    bool isActive = IsActiveTrans(t);
                    bool isEntry  = t.from != null && t.from.isEntry;

                    Color lineCol  = (isSel || isActive) ? NSelBord : (isEntry ? EntryLine  : ArrowCol);
                    Color arrowCol = (isSel || isActive) ? lineCol  : (isEntry ? EntryArrow : Color.white);
                    float lineW    = isEntry ? 3f : 5f;

                    // Straight bidirectional pairs are slid sideways so the two directions run parallel
                    // (Unity-style) instead of overlapping; the offset drops once a reroute is added so the
                    // line follows its points. Hit-testing uses the same geometry so clicks match the draw.
                    var gpts = TransGraphPoints(node, t);
                    var pts  = new List<Vector2>(gpts.Count);
                    foreach (var gp in gpts) pts.Add(G2C(gp));

                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        Handles.color = lineCol;
                        Handles.DrawAAPolyLine(lineW, pts[i], pts[i + 1]);
                        DrawArrowHeads(pts[i], pts[i + 1], arrowCol, 4.9f, arrowCount);   // N arrows per segment
                    }

                    if (isActive)
                    {
                        Vector2 flow = PointAlongPolyline(pts, liveTNT);   // travels along the arrow (reroutes included)
                        Handles.color = new Color(0.55f, 0.82f, 1f, 0.9f);
                        Handles.DrawSolidDisc(flow, Vector3.forward, 5f * Mathf.Clamp(zoom, 0.5f, 1.5f));
                    }

                    for (int ri = 0; ri < t.pts.Count; ri++)
                    {
                        Vector2 sc  = G2C(t.pts[ri]);
                        bool    rsel = IsRerouteSel(t, ri);
                        Handles.color = rsel ? new Color(0.30f, 0.58f, 0.96f) : new Color(0.72f, 0.72f, 0.72f);
                        Handles.DrawSolidDisc(sc, Vector3.forward, rsel ? 6f : 5f);
                        Handles.color = rsel ? new Color(0.78f, 0.90f, 1f) : new Color(0.18f, 0.18f, 0.18f);
                        Handles.DrawWireDisc(sc, Vector3.forward, rsel ? 6f : 5f);
                    }
                }
            }

            Handles.EndGUI();
        }

        // Point at fraction t (0..1) along a polyline, parameterised by arc length
        private static Vector2 PointAlongPolyline(List<Vector2> pts, float t)
        {
            if (pts == null || pts.Count == 0) return Vector2.zero;
            if (pts.Count == 1) return pts[0];
            t = Mathf.Clamp01(t);

            float total = 0f;
            for (int i = 0; i < pts.Count - 1; i++) total += Vector2.Distance(pts[i], pts[i + 1]);
            if (total <= 0f) return pts[0];

            float target = t * total, acc = 0f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float seg = Vector2.Distance(pts[i], pts[i + 1]);
                if (acc + seg >= target)
                    return Vector2.Lerp(pts[i], pts[i + 1], seg > 0f ? (target - acc) / seg : 0f);
                acc += seg;
            }
            return pts[pts.Count - 1];
        }

        private void DrawArrowHead(Vector2 a, Vector2 b, Color col, float sz)
        {
            Vector2 dir = (b - a).normalized;
            if (dir.sqrMagnitude < 0.001f) return;
            DrawArrowHeadAt((a + b) * 0.5f, dir, col, sz);
        }

        private void DrawArrowHeadAt(Vector2 c, Vector2 dir, Color col, float sz)
        {
            Vector2 side = new Vector2(-dir.y, dir.x) * sz;
            Handles.color = col;
            Handles.DrawAAConvexPolygon(
                new Vector3(c.x + dir.x * sz * 1.2f,  c.y + dir.y * sz * 1.2f),
                new Vector3(c.x - dir.x * sz + side.x, c.y - dir.y * sz + side.y),
                new Vector3(c.x - dir.x * sz - side.x, c.y - dir.y * sz - side.y));
        }

        // A transition is "groupable" (merged with its parallel siblings sharing the same from→to) when it's
        // a state/any-state transition. Reroute points are kept identical across the group (SyncRerouteSiblings),
        // so the whole group draws as one line — with one set of reroute nodes — regardless of reroutes.
        // Entry / blend-tree lines are always drawn on their own.
        private static bool IsGroupable(STrans t)
            => t != null && t.src != null && t.from != null && t.to != null
            && !t.from.isBtRoot && !t.from.isEntry;

        // Keep every parallel transition (same from→to) sharing one reroute path, so a grouped multi-
        // transition shows a single set of reroute nodes. Copies `rep`'s points onto all its from→to siblings.
        private void SyncRerouteSiblings(STrans rep)
        {
            if (rep == null || rep.src == null || rep.from == null || rep.to == null) return;
            foreach (var x in rep.from.transitions)
                if (x != rep && x.src != null && x.to == rep.to)
                    x.pts = new List<Vector2>(rep.pts);
        }

        // Map each transition that belongs to a real multi-group (≥2 between the same from→to) to its
        // group size + whether it's the representative (the one that actually draws the shared line).
        private Dictionary<STrans, (int count, bool rep)> BuildTransGroups()
        {
            var groups = new Dictionary<(SNode, SNode), List<STrans>>();
            foreach (var node in nodes)
                foreach (var t in node.transitions)
                {
                    if (!IsGroupable(t)) continue;
                    var key = (t.from, t.to);
                    if (!groups.TryGetValue(key, out var list)) { list = new List<STrans>(); groups[key] = list; }
                    list.Add(t);
                }
            var map = new Dictionary<STrans, (int, bool)>();
            foreach (var kv in groups)
                if (kv.Value.Count >= 2)
                    for (int i = 0; i < kv.Value.Count; i++) map[kv.Value[i]] = (kv.Value.Count, i == 0);
            return map;
        }

        // True when a (state/any-state) transition exists from a → b (used to offset reverse pairs apart).
        private static bool HasReverse(SNode a, SNode b)
        {
            if (a == null || b == null) return false;
            foreach (var x in a.transitions) if (x.src != null && x.to == b) return true;
            return false;
        }

        // The transition's polyline in GRAPH space (node centres + reroute points), including the parallel
        // offset applied to straight bidirectional pairs. Shared by drawing and hit-testing so clicks match
        // exactly what's drawn. The offset (in graph units) becomes the screen offset once scaled by zoom.
        private List<Vector2> TransGraphPoints(SNode from, STrans t)
        {
            Vector2 half = new Vector2(NodeW * .5f, NodeH * .5f);
            var pts = new List<Vector2> { from.position + half };
            foreach (var rp in t.pts) pts.Add(rp);
            pts.Add(t.to.position + half);

            if (t.src != null && t.pts.Count == 0 && HasReverse(t.to, t.from))
            {
                Vector2 d = pts[pts.Count - 1] - pts[0];
                float   m = d.magnitude;
                if (m > 0.001f)
                {
                    Vector2 perp = new Vector2(d.y, -d.x) / m * (6f * Mathf.Clamp(zoom, 0.5f, 1.5f) / Mathf.Max(zoom, 0.0001f));
                    for (int k = 0; k < pts.Count; k++) pts[k] += perp;
                }
            }
            return pts;
        }

        // The representative (drawn) transition of a from→to group — the first groupable sibling. Reroute
        // handles belong to the rep, so clicks on a shared reroute resolve here to keep drag/draw in sync.
        private STrans GroupRep(STrans t)
        {
            if (t == null || !IsGroupable(t)) return t;
            foreach (var x in t.from.transitions)
                if (IsGroupable(x) && x.to == t.to) return x;
            return t;
        }

        // All transitions sharing the clicked transition's from→to pair (for group multi-selection).
        private List<STrans> GroupTransitions(STrans t)
        {
            var list = new List<STrans>();
            if (t == null) return list;
            if (!IsGroupable(t)) { list.Add(t); return list; }
            foreach (var x in t.from.transitions)
                if (IsGroupable(x) && x.to == t.to) list.Add(x);
            if (list.Count == 0) list.Add(t);
            return list;
        }

        // Draw `count` stacked arrowheads centred on the segment midpoint (Unity's "≫" multi-transition mark).
        private void DrawArrowHeads(Vector2 a, Vector2 b, Color col, float sz, int count)
        {
            Vector2 dir = (b - a).normalized;
            if (dir.sqrMagnitude < 0.001f) return;
            if (count <= 1) { DrawArrowHeadAt((a + b) * 0.5f, dir, col, sz); return; }
            Vector2 mid = (a + b) * 0.5f;
            float spacing = sz * 2.1f;
            float start = -(count - 1) * 0.5f * spacing;
            for (int k = 0; k < count; k++) DrawArrowHeadAt(mid + dir * (start + k * spacing), dir, col, sz);
        }

        // ── Nodes ─────────────────────────────────────────────────────────────────
        private void DrawNodes()
        {
            foreach (var n in nodes)
            {
                Vector2 sp   = G2C(n.position);
                Rect    rect = new Rect(sp, NodeSize(n) * zoom);
                float   cr   = 4f * zoom;
                int     fs   = Mathf.Max(Mathf.RoundToInt(11f * zoom), 7);

                // Subtle drop shadow under every node (light from top-left → shadow toward bottom-right).
                DrawNodeShadow(rect, cr, n.isStateMachine || n.isUp, Mathf.Min(rect.height * 0.5f, 14f * zoom));

                if (n.isBtRoot) { DrawBtRoot(n, rect, cr); continue; }

                if (n.isBtChild) { DrawBtChild(n, rect, cr, fs); continue; }

                if (n.isStateMachine || n.isUp)
                {
                    Color fTop = n.isUp ? NDefFillTop : NFillTop, fBot = n.isUp ? NDefFillBot : NFillBot;
                    Color bTop = n.isUp ? NDefBordTop : NBordTop, bBot = n.isUp ? NDefBordBot : NBordBot;
                    float bot  = Mathf.Max(1f, zoom);
                    float bev  = Mathf.Min(rect.height * 0.5f, 14f * zoom);   // angled left/right corners
                    if (IsSel(n)) DrawBeveledSelOutline(rect, bev, bot);
                    DrawBeveledOutlineGradient(rect, bev, bot, bTop, bBot);
                    DrawBeveledGradient(rect, bev, fTop, fBot);
                    DrawNLabel(n.name, rect, fs);
                    continue;
                }

                if (n.isEntry || n.isExit || n.isAnyState)
                {
                    Color fTop, fBot, bTop, bBot;
                    if (n.isEntry)      { fTop = EntryFillTop; fBot = EntryFillBot; bTop = EntryBordTop; bBot = EntryBordBot; }
                    else if (n.isExit)  { fTop = ExitFillTop;  fBot = ExitFillBot;  bTop = ExitBordTop;  bBot = ExitBordBot;  }
                    else                { fTop = AnyFillTop;    fBot = AnyFillBot;    bTop = AnyBordTop;    bBot = AnyBordBot;    }
                    float sot = Mathf.Max(1f, zoom);
                    if (IsSel(n)) DrawNodeSelOutline(rect, cr, sot);  // blue ring outside the node border
                    DrawRoundedOutlineGradient(rect, cr, sot, bTop, bBot);
                    DrawNodeFill(rect, cr, fTop, fBot);
                    DrawNLabel(n.name, rect, fs);
                    continue;
                }

                bool live = IsLive(n);
                bool next = IsNext(n);
                bool sel  = IsSel(n);

                // Only the explicit selection paints the node; play mode is shown via the
                // progress bar below, never by recolouring the node.
                float ot = Mathf.Max(1f, zoom);
                if (sel)
                    DrawNodeSelOutline(rect, cr, ot);
                if (n.isDefault)
                    DrawRoundedOutlineGradient(rect, cr, ot, NDefBordTop, NDefBordBot);
                else
                    DrawRoundedOutlineGradient(rect, cr, ot, NBordTop, NBordBot);

                // Default state keeps its amber gradient; everything else uses the normal
                // node fill. Live/next states are NOT tinted blue — only the play bar moves.
                if (n.isDefault) DrawNodeFill(rect, cr, NDefFillTop, NDefFillBot);
                else             DrawNodeFill(rect, cr, NFillTop, NFillBot);

                if (n.motion is BlendTree)
                {
                    var btStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.5f, 0.8f, 1f) }, fontSize = Mathf.Max(Mathf.RoundToInt(9f * zoom), 6) };
                    GUI.Label(new Rect(rect.xMax - 20 * zoom, rect.y + 2 * zoom, 18 * zoom, 14 * zoom), "BT", btStyle);
                }

                if (Application.isPlaying)
                {
                    if (live)
                        DrawNodeProgressBar(rect, liveNT, false);
                    else if (next)
                        DrawNodeProgressBar(rect, liveTNT, true);
                }

                Rect labelRect = rect;
                labelRect.y -= 6f * zoom;   // slightly above center, like Animator state labels
                DrawNLabel(n.name, labelRect, fs);
            }
        }

        // The blend-tree root node: gray rounded box with a (non-bold) title, one motion row per child
        // (the connector is faked by an interpunct at the row's right edge, staying inside the box), and
        // one interactive control slider per blend parameter (paramName | slider | value field).
        private void DrawBtRoot(SNode n, Rect rect, float cr)
        {
            var bt  = n.blendTree;
            float z = zoom;
            float ot = Mathf.Max(1f, z);
            if (IsSel(n)) DrawNodeSelOutline(rect, cr, ot);
            DrawRoundedOutlineGradient(rect, cr, ot, NBordTop, NBordBot);
            DrawNodeFill(rect, cr, NFillTop, NFillBot);

            // Title (not bold, no separator)
            var titleStyle = new GUIStyle(EditorStyles.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(Mathf.RoundToInt(11f * z), 7), clipping = TextClipping.Clip, normal = { textColor = NText } };
            GUI.Label(new Rect(rect.x, rect.y + 2f * z, rect.width, BtHeaderH * z), n.name, titleStyle);

            // Motion rows: name (left) + a faked connector interpunct flush to the right edge (inside the box)
            var nameStyle = new GUIStyle(EditorStyles.label)
            { alignment = TextAnchor.MiddleRight, fontSize = Mathf.Max(Mathf.RoundToInt(10f * z), 6), clipping = TextClipping.Clip, normal = { textColor = NText } };
            var dotStyle = new GUIStyle(nameStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.Max(Mathf.RoundToInt(7f * z), 5),
                clipping = TextClipping.Clip
            };
            int cc = n.transitions.Count;
            for (int i = 0; i < cc; i++)
            {
                float rowTop = rect.y + (BtHeaderH + BtPadTop + BtRowH * i) * z;
                var to = n.transitions[i].to;

                float rowX     = rect.x + 8f * z;
                float rowW     = rect.width - 14f * z;
                float dotSlotW = 10f * z;
                float textGap  = 4f * z;

                var rowR  = new Rect(rowX, rowTop, rowW, BtRowH * z);
                var textR = new Rect(rowR.x, rowR.y, rowR.width - dotSlotW - textGap, rowR.height);
                var dotR  = new Rect(rowR.xMax - dotSlotW, rowR.y, dotSlotW, rowR.height);

                GUI.Label(textR, to != null ? to.name : "(None)", nameStyle);
                dotStyle.normal.textColor = BtDotColor(BtWeight(i));   // gray → blue with activation
                GUI.Label(dotR, "●", dotStyle);
            }

            // Control sliders (one per blend parameter)
            if (bt != null)
            {
                var prms  = BlendParams(bt);
                float top = rect.y + (BtHeaderH + BtPadTop + BtRowH * cc + BtSliderGap) * z;
                for (int p = 0; p < prms.Count; p++)
                    DrawBtControlSlider(bt, p, prms[p], new Rect(rect.x + 6f * z, top + p * BtSliderRowH * z, rect.width - 12f * z, BtSliderRowH * z), z);
            }
        }

        // One interactive control-parameter slider row: name on the left, slider in the middle, value field
        // on the right. Editing updates the parameter (live in play mode, otherwise a local preview value).
        private void DrawBtControlSlider(BlendTree bt, int paramIdx, string param, Rect area, float z)
        {
            if (bt == null || area.height < 4f) return;

            float lblW = Mathf.Min(area.width * 0.34f, 70f * z);
            float fldW = Mathf.Min(area.width * 0.26f, 46f * z);
            float midY = area.y + area.height * 0.5f;

            var lblStyle = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.MiddleLeft, fontSize = Mathf.Max(Mathf.RoundToInt(9f * z), 6), clipping = TextClipping.Clip, normal = { textColor = new Color(0.78f, 0.78f, 0.78f) } };
            GUI.Label(new Rect(area.x, area.y, lblW, area.height), string.IsNullOrEmpty(param) ? "(none)" : param, lblStyle);

            BlendRange(bt, paramIdx, out float min, out float max);
            float val = GetBlendValue(param);

            var sliderR = new Rect(area.x + lblW + 4f * z, midY - 6f * z, area.width - lblW - fldW - 12f * z, 12f * z);
            var fieldR  = new Rect(area.xMax - fldW, area.y + 1f, fldW, area.height - 2f);

            EditorGUI.BeginChangeCheck();
            float ns = GUI.HorizontalSlider(sliderR, val, min, max, GUIStyle.none, GUIStyle.none);
            DrawSliderTrackHandle(sliderR, Mathf.InverseLerp(min, max, ns), z);   // rounded handle visual
            float nf = EditorGUI.FloatField(fieldR, ns);
            if (EditorGUI.EndChangeCheck()) SetBlendValue(param, nf);
        }

        // A blend-tree child node: normal gray box, motion name nudged up, and a right-justified
        // "· Blend Tree" / "· Clip" tag along the bottom-right.
        private void DrawBtChild(SNode n, Rect rect, float cr, int fs)
        {
            float ot  = Mathf.Max(1f, zoom);
            if (IsSel(n)) DrawNodeSelOutline(rect, cr, ot);

            // Gray activation shade applied to BOTH the border and the fill: ~30% darker (dark gray)
            // when inactive → normal gray when active.
            float act = BtWeight(n.btChildIndex);
            float bf  = Mathf.Lerp(0.7f, 1f, act);
            DrawRoundedOutlineGradient(rect, cr, ot,
                new Color(NBordTop.r * bf, NBordTop.g * bf, NBordTop.b * bf, 1f),
                new Color(NBordBot.r * bf, NBordBot.g * bf, NBordBot.b * bf, 1f));
            DrawNodeFill(rect, cr,
                new Color(NFillTop.r * bf, NFillTop.g * bf, NFillTop.b * bf, 1f),
                new Color(NFillBot.r * bf, NFillBot.g * bf, NFillBot.b * bf, 1f));

            // Name, nudged up to leave room for the type tag
            var nameStyle = new GUIStyle(EditorStyles.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = fs, clipping = TextClipping.Clip, normal = { textColor = NText } };
            GUI.Label(new Rect(rect.x + 4f * zoom, rect.y + 3f * zoom, rect.width - 8f * zoom, rect.height * 0.58f), n.name, nameStyle);

            // Bottom-left type tag: "Blend Tree" in gray, with the leading "●" tinted by activation.
            var tagRect  = new Rect(rect.x + 4f * zoom, rect.yMax - 14f * zoom, rect.width - 8f * zoom, 12f * zoom);
            var tagStyle = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.MiddleLeft, fontSize = Mathf.Max(Mathf.RoundToInt(8f * zoom), 6), clipping = TextClipping.Clip, normal = { textColor = new Color(0.7569f, 0.7569f, 0.7569f) } };
            GUI.Label(tagRect, "● Blend Tree", tagStyle);
            var tagDotStyle = new GUIStyle(tagStyle) { normal = { textColor = BtDotColor(act) } };
            GUI.Label(tagRect, "●", tagDotStyle);   // overlay just the dot in the activation color
        }

        // Custom rounded track + handle drawn over an invisible GUI.HorizontalSlider.
        private void DrawSliderTrackHandle(Rect sliderR, float frac, float z)
        {
            float ty = sliderR.y + sliderR.height * 0.5f;
            var track = new Rect(sliderR.x, ty - Mathf.Max(z, 1f), sliderR.width, Mathf.Max(2f * z, 2f));
            EditorGUI.DrawRect(track, Hex("545454"));
            float hr = Mathf.Max(5f * z, 4f);
            Vector2 c = new Vector2(sliderR.x + Mathf.Clamp01(frac) * sliderR.width, ty);
            Handles.BeginGUI();
            Handles.color = Hex("b0b0b0");
            Handles.DrawSolidDisc(new Vector3(c.x, c.y), Vector3.forward, hr);
            Handles.color = Hex("303030");
            Handles.DrawWireDisc(new Vector3(c.x, c.y), Vector3.forward, hr);
            Handles.EndGUI();
        }

        // Gradient fill: base colour + subtle brightness lift on top third (Unity Animator style)
        // Explicit top→bottom gradient fill, banded to follow the rounded corners.
        private void DrawNodeFill(Rect r, float rad, Color top, Color bot)
        {
            DrawRoundedGradient(r, rad, top, bot);
        }

        private void DrawNodeProgressBar(Rect nodeRect, float progress, bool isNextState)
        {
            float z      = zoom;
            float h      = Mathf.Max(4f * z, 2f);
            float padX   = Mathf.Max(5f * z, 2f);
            float padBot = Mathf.Max(3f * z, 1f);

            var track = new Rect(
                nodeRect.x + padX,
                nodeRect.yMax - h - padBot,
                nodeRect.width - padX * 2f,
                h);

            if (track.width <= 1f || track.height <= 1f)
                return;

            float border = Mathf.Min(Mathf.Max(1f * z, 0.5f), track.height * 0.35f);
            float radius = track.height * 0.5f;
            var inner = new Rect(
                track.x + border,
                track.y + border,
                track.width - border * 2f,
                track.height - border * 2f);

            if (inner.width <= 0f || inner.height <= 0f)
                return;

            DrawRounded(track, radius, Hex("202224"));
            DrawRoundedGradient(inner, Mathf.Max(0f, radius - border), Hex("35393d"), Hex("272a2d"));

            float fillW = Mathf.Clamp01(progress) * inner.width;
            if (fillW <= 0f)
                return;

            Color top = isNextState ? new Color(0.55f, 0.82f, 1f, 0.75f) : new Color32(78, 157, 255, 245);
            Color bot = isNextState ? new Color(0.30f, 0.58f, 1f, 0.75f) : new Color32(42, 121, 245, 245);

            GUI.BeginGroup(new Rect(inner.x, inner.y, fillW, inner.height));
            DrawRoundedGradient(new Rect(0f, 0f, inner.width, inner.height), Mathf.Max(0f, radius - border), top, bot);
            GUI.EndGroup();
        }

        // 2px blue selection outline OUTSIDE the node's own border, gradient-faded across its
        // Selection ring fades outward.
        // Concentric bands drawn outer(faint)→inner(opaque); the node outline (drawn afterwards,
        // smaller) covers everything up to nodeOutline, leaving just the faded 2px ring.
        private void DrawNodeSelOutline(Rect r, float rad, float nodeOutline)
        {
            float w = 2f * Mathf.Max(1f, zoom);   // ~2px ring width, scaled with zoom
            const int bands = 8;
            for (int i = bands; i >= 1; i--)
            {
                float o = nodeOutline + w * i / bands;        // offset within the ring
                float t = (i - 1f) / (bands - 1f);            // 0 = inner (at node), 1 = outer edge
                float a = 1f - t;                             // opaque inside → transparent outside
                DrawRoundedOutline(r, rad, o, new Color(NSelBord.r, NSelBord.g, NSelBord.b, a));
            }
        }

        private void DrawNLabel(string text, Rect rect, int fs)
        {
            // Scale the font with the node and clip the text to the node rect, so it neither
            // bleeds past the node (into the sidebar) at low zoom nor over-clips at high zoom.
            var s = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = Mathf.Max(1, Mathf.RoundToInt(11f * zoom)),
                wordWrap  = true,
                clipping  = TextClipping.Clip,
                normal    = { textColor = NText }
            };
            GUI.Label(rect, text, s);
        }

        // ── Canvas overlay ────────────────────────────────────────────────────────
        // Small footer strip at the bottom of the node view with the controller name (bottom-right)
        private void DrawCanvasFooter()
        {
            const float h = 18f;
            var f = new Rect(SideWNow, position.height - h, position.width - SideWNow, h);
            EditorGUI.DrawRect(f, Hex("404040"));
            if (ctrl != null && !string.IsNullOrEmpty(ctrl.name))
            {
                var st = new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(0.80f, 0.80f, 0.80f) } };
                GUI.Label(new Rect(f.x, f.y, f.width - 8, h), ctrl.name, st);
            }
        }

        // Inner shadow framing the node view edges (black → transparent inward) for a sense of depth
        private void DrawCanvasInnerShadow()
        {
            Rect a = CVS;
            a.height -= 18f;             // sit above the footer
            if (a.width <= 0 || a.height <= 0) return;
            const int n = 12;
            for (int i = 0; i < n; i++)
            {
                float t     = i / (float)(n - 1);
                float alpha = (1f - t) * (1f - t) * 0.45f;     // strongest at the edge, eased to 0
                var   c     = new Color(0f, 0f, 0f, alpha);
                EditorGUI.DrawRect(new Rect(a.x,             a.y + i,        a.width, 1), c);  // top
                EditorGUI.DrawRect(new Rect(a.x,             a.yMax - 1 - i, a.width, 1), c);  // bottom
                EditorGUI.DrawRect(new Rect(a.x + i,         a.y,            1, a.height), c);  // left
                EditorGUI.DrawRect(new Rect(a.xMax - 1 - i,  a.y,            1, a.height), c);  // right
            }
        }

        private void DrawOverlay()
        {
            if (makingTransFrom == null) return;
            // Show instruction banner at bottom of canvas
            var banner = new Rect(SideWNow + 10, position.height - 28, CVS.width - 20, 22);
            EditorGUI.DrawRect(banner, new Color(0, 0, 0, 0.65f));
            var bStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.75f, 0.85f, 1f) } };
            GUI.Label(banner, $"Click a state to create transition from  '{makingTransFrom.name}'  —  Esc to cancel", bStyle);
        }

        // ── Events ────────────────────────────────────────────────────────────────
        private void HandleEvents(Event e)
        {
            bool inCvs = CVS.Contains(e.mousePosition);
            Vector2 mouse  = e.mousePosition;
            Vector2 mouseG = W2G(mouse);

            // Sidebar resize splitter (takes priority over canvas interaction)
            if (showSidebar)
            {
                var splitRect = new Rect(SideW - 3, ToolH, 6, position.height - ToolH);
                if (e.type == EventType.MouseDown && e.button == 0 && splitRect.Contains(mouse)) { draggingSplit = true; e.Use(); return; }
                if (draggingSplit && e.type == EventType.MouseDrag)
                { SideW = Mathf.Clamp(mouse.x, MinSideW, position.width - 150f); e.Use(); Repaint(); return; }
                if (draggingSplit && e.type == EventType.MouseUp) { draggingSplit = false; e.Use(); return; }
            }

            // Blend-tree view: right-click only offers "Go Up" (no structural state-machine menu).
            // Selection / drag / double-click still flow through the normal handlers below.
            if (InBlendTree && e.type == EventType.ContextClick && CVS.Contains(e.mousePosition))
            {
                var bm = new GenericMenu();
                int depth = btStack.Count;
                bm.AddItem(new GUIContent("Go Up"), false, () => ExitBlendTreeToDepth(depth - 1));
                bm.ShowAsContext(); e.Use(); return;
            }

            // Copy / Paste (Ctrl+C / Ctrl+V, or ⌘ on macOS)
            if (e.type == EventType.KeyDown && (e.control || e.command))
            {
                if (e.keyCode == KeyCode.C && selNodes.Count > 0)
                { CopySelectionToBuffer(); e.Use(); return; }
                if (e.keyCode == KeyCode.V && copyBuffer.Count > 0)
                { PasteBufferAt(inCvs ? mouseG : W2G(CVS.center)); e.Use(); Repaint(); return; }
            }

            // Supr / Delete removes the current canvas selection (states, transitions, reroutes)
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace)
                && inCvs && (selNodes.Count > 0 || selTranses.Count > 0 || selTrans != null || selReroutes.Count > 0))
            {
                DeleteSelection(); e.Use(); Repaint(); return;
            }

            // Zoom
            if (e.type == EventType.ScrollWheel && inCvs)
            {
                float oz   = zoom;
                zoom       = Mathf.Clamp(zoom - e.delta.y * 0.05f, ZMin, ZMax);
                Vector2 cm = mouse - new Vector2(SideWNow, ToolH);
                pan        = pan * (zoom / oz) + cm * (1f - zoom / oz);
                e.Use(); Repaint(); return;
            }

            // Pan (middle mouse)
            if (e.type == EventType.MouseDown && e.button == 2 && inCvs) { draggingPan = true; panStart = mouse; e.Use(); return; }
            if (e.type == EventType.MouseDrag && draggingPan)             { pan += mouse - panStart; panStart = mouse; e.Use(); Repaint(); return; }
            if (e.type == EventType.MouseUp   && e.button == 2)          { draggingPan = false; return; }

            // Live marquee drag
            if (e.type == EventType.MouseDrag && boxSelecting) { e.Use(); Repaint(); return; }

            // Ongoing node / reroute drags (nodes + reroute points move together as one rigid group)
            if (e.type == EventType.MouseDrag && e.button == 0
                && (draggingNode || draggingReroute) && (dragStartPos.Count > 0 || dragStartReroute.Count > 0))
            {
                // Absolute move from the snapshot so the group stays rigid; snap the anchor to grid.
                Vector2 total = mouseG - dragStartG;
                if (snapToGrid)
                    total = SnapToGrid(dragAnchorStart + total) - dragAnchorStart;
                foreach (var kv in dragStartPos) kv.Key.position = kv.Value + total;
                foreach (var kv in dragStartReroute)
                {
                    var rr = kv.Key;
                    if (rr.trans != null && rr.idx < rr.trans.pts.Count) rr.trans.pts[rr.idx] = kv.Value + total;
                }
                e.Use(); Repaint(); return;
            }

            if (e.type == EventType.MouseUp && e.button == 0)
            {
                if (boxSelecting) { FinalizeMarquee(); boxSelecting = false; e.Use(); Repaint(); return; }
                if (draggingNode || draggingReroute)
                {
                    if (dragStartPos.Count     > 0) SavePositions();
                    if (dragStartReroute.Count > 0)
                    {
                        foreach (var rr in dragStartReroute.Keys) SyncRerouteSiblings(rr.trans);   // move applies to the whole group
                        SaveReroutes();
                    }
                }
                draggingNode = draggingReroute = false;
                dragStartPos.Clear(); dragStartReroute.Clear();
                return;
            }

            if (!inCvs) return;

            // Ctrl+click: insert reroute on line (not in the blend-tree view)
            if (e.type == EventType.MouseDown && e.button == 0 && (e.control || e.command) && !InBlendTree)
            {
                foreach (var node in nodes)
                foreach (var t in node.transitions)
                {
                    var gpts = TransGraphPoints(node, t);
                    for (int i = 0; i < gpts.Count - 1; i++)
                        if (HandleUtility.DistancePointLine(mouseG, gpts[i], gpts[i + 1]) < 8f / zoom)
                        {
                            t.pts.Insert(i, mouseG); SyncRerouteSiblings(t); selReroutes.Clear(); SaveReroutes(); e.Use(); Repaint(); return;
                        }
                }
                return;
            }

            // Left click
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // If making-transition mode, check if clicking a target node
                if (makingTransFrom != null)
                {
                    foreach (var node in nodes)
                    {
                        if (!NodeWinRect(node).Contains(mouse)) continue;
                        if (node == makingTransFrom || node.isEntry) continue;
                        var src = makingTransFrom;
                        makingTransFrom = null;
                        // Transitioning onto a sub-SM or the "(UP)" node crosses a boundary — pick the
                        // specific destination state from a menu (the states of that machine).
                        if (node.isStateMachine || node.isUp) ShowCrossTransitionMenu(src, node);
                        else                                  CommitTransition(src, node);
                        e.Use(); Repaint(); return;
                    }
                    // Clicked empty space — cancel
                    makingTransFrom = null;
                    e.Use(); Repaint(); return;
                }

                // Reroute handles (small targets — check first). Supports multi-selection like nodes.
                foreach (var node in nodes)
                foreach (var t0 in node.transitions)
                for (int i = 0; i < t0.pts.Count; i++)
                {
                    if (Vector2.Distance(mouse, G2W(t0.pts[i])) >= 8f) continue;
                    var t  = GroupRep(t0);          // a shared reroute belongs to the group's drawn rep
                    var rr = new RR(t, i);

                    if (e.shift)
                    {
                        // Toggle this reroute in/out of the multi-selection
                        if (!selReroutes.Add(rr)) selReroutes.Remove(rr);
                        if (!selReroutes.Contains(rr)) { e.Use(); Repaint(); return; }  // deselected → no drag
                    }
                    else if (!(selReroutes.Contains(rr) && (selReroutes.Count > 1 || selNodes.Count > 0)))
                    {
                        // Fresh single pick (not already part of a kept group) — select just this reroute
                        selReroutes.Clear(); selReroutes.Add(rr);
                        selNodes.Clear(); selNode = null;
                        selTrans = t; selTranses.Clear();
                        SelectTransitionInInspector(new List<STrans> { t });
                    }
                    // else: clicked inside an existing group — keep it

                    draggingReroute = true;
                    SnapshotDrag(mouseG, t.pts[i]);
                    e.Use(); Repaint(); return;
                }

                // Nodes take precedence over transitions — check before lines
                foreach (var node in nodes)
                {
                    if (!NodeWinRect(node).Contains(mouse)) continue;

                    // Double-click enters a blend tree (or the root navigates back up)
                    if (e.clickCount == 2) { HandleNodeDoubleClick(node); e.Use(); return; }

                    bool multi;   // true when this click should keep/produce a multi-selection
                    if (e.shift)
                    {
                        // Toggle this node in/out of the multi-selection
                        if (!selNodes.Add(node)) selNodes.Remove(node);
                        selNode = selNodes.Contains(node) ? node : null;
                        multi = true;
                    }
                    else if (selNodes.Contains(node) && (selNodes.Count > 1 || selReroutes.Count > 0))
                    {
                        // Clicking inside an existing mixed selection should keep the group intact.
                        selNode = node;
                        multi = true;
                    }
                    else
                    {
                        selNodes.Clear(); selNodes.Add(node); selNode = node;
                        multi = false;
                    }

                    selTrans = null; selTranses.Clear();
                    if (!multi) selReroutes.Clear();

                    // Blend-tree nodes can't be moved (auto-laid-out); just select. Otherwise start a drag.
                    if (!InBlendTree)
                    {
                        draggingNode = true;
                        SnapshotDrag(mouseG, node.position);   // snapshot so the group drags as a unit
                    }

                    // Keep Unity selection aligned with canvas selection.
                    if (multi) PushNodeSelectionToInspector();
                    else if (InBlendTree && selNode != null) Selection.activeObject = selNode.motion;   // blend tree / clip inspector
                    else if (ViewingSynced && selNode != null && selNode.state != null) SelectSyncedState(selNode);
                    else if (selNode != null && selNode.state != null) Selection.activeObject = selNode.state;
                    // Special nodes use Unity's native graph objects when available.
                    else if (selNode != null && (selNode.isEntry || selNode.isExit || selNode.isAnyState))
                    {
                        var nativeNode = SpecialNodeObject(selNode);
                        Selection.activeObject = nativeNode != null ? nativeNode : CurrentSM;
                    }
                    // Sub-state-machine node → select its state machine (Inspector shows its settings / behaviours).
                    else if (selNode != null && selNode.isStateMachine && selNode.subSM != null)
                        Selection.activeObject = selNode.subSM;
                    GUI.FocusControl(null); e.Use(); Repaint(); return;
                }

                // Transition lines (only reached when no node was hit)
                foreach (var node in nodes)
                foreach (var t in node.transitions)
                {
                    var gpts = TransGraphPoints(node, t);
                    for (int i = 0; i < gpts.Count - 1; i++)
                        if (HandleUtility.DistancePointLine(mouseG, gpts[i], gpts[i + 1]) < 6f / zoom)
                        {
                            selNode = null; selNodes.Clear(); selReroutes.Clear();
                            var group = GroupTransitions(t);
                            if (group.Count > 1)
                            {
                                // Multiple transitions between the same states → select them all (multi-edit).
                                selTrans = null; selTranses.Clear(); selTranses.UnionWith(group);
                                SelectTransitionInInspector(group);
                            }
                            else
                            {
                                selTrans = t; selTranses.Clear();
                                if (!SelectTransitionInInspector(new List<STrans> { t }) && t.isEntryDefault)
                                    Selection.activeObject = EntryInfoProxy;
                            }
                            e.Use(); Repaint(); return;
                        }
                }

                // Empty-space drag starts a marquee. Shift keeps the current selection and adds to it;
                // otherwise clear and show the container (blend tree / state machine).
                boxAdditive = e.shift;
                if (!boxAdditive)
                {
                    selNode = null; selTrans = null; selNodes.Clear(); selTranses.Clear(); selReroutes.Clear();
                    Selection.activeObject = InBlendTree ? (Object)CurrentBT : CurrentSM;
                }
                boxSelecting = true; boxStartG = mouseG;
                e.Use(); Repaint();
                return;
            }

            // Right-click context menu
            if (e.type == EventType.ContextClick)
            {
                Vector2 cg = mouseG;

                // ── Reroute node hit — own short menu ─────────────────────────
                foreach (var node in nodes)
                foreach (var t in node.transitions)
                for (int i = 0; i < t.pts.Count; i++)
                {
                    if (Vector2.Distance(mouse, G2W(t.pts[i])) < 10f)
                    {
                        var ct = GroupRep(t); var ci = i;
                        var rm = new GenericMenu();
                        rm.AddItem(new GUIContent("Remove Node"), false, () => { if (ci < ct.pts.Count) ct.pts.RemoveAt(ci); SyncRerouteSiblings(ct); selReroutes.Clear(); SaveReroutes(); Repaint(); });
                        rm.ShowAsContext(); e.Use(); return;
                    }
                }

                // ── Node under the cursor takes priority over transition lines behind it ──
                SNode clickedNode = null;
                foreach (var node in nodes)
                    if (NodeWinRect(node).Contains(mouse)) { clickedNode = node; break; }

                // ── Transition line hit (only when no node is under the cursor) ──
                if (clickedNode == null)
                {
                    foreach (var node in nodes)
                    foreach (var t in node.transitions)
                    {
                        var gpts = TransGraphPoints(node, t);
                        for (int i = 0; i < gpts.Count - 1; i++)
                        {
                            if (HandleUtility.DistancePointLine(mouseG, gpts[i], gpts[i + 1]) >= 7f / zoom) continue;
                            selTrans = t; selNode = null; selNodes.Clear(); selTranses.Clear();
                            SelectTransitionInInspector(new List<STrans> { t });

                            var tm = new GenericMenu();
                            var ct = t; int ci = i; Vector2 cp = mouseG;
                            tm.AddItem(new GUIContent("Add Reroute Node"), false, () => { ct.pts.Insert(ci, cp); SyncRerouteSiblings(ct); selReroutes.Clear(); SaveReroutes(); Repaint(); });
                            if (t.src != null)
                            {
                                var cts = t.src;
                                tm.AddSeparator("");
                                tm.AddItem(new GUIContent("Copy Parameters"), false, () => CopyTransParams(cts));
                                if (transParamBuffer != null) tm.AddItem(new GUIContent("Paste Parameters"), false, () => { PasteTransParams(cts); Repaint(); });
                                else                          tm.AddDisabledItem(new GUIContent("Paste Parameters"));
                            }
                            if (t.src != null)
                            {
                                tm.AddSeparator("");
                                tm.AddItem(new GUIContent("Delete Transition"), false, () =>
                                {
                                    RemoveTransitionSrc(ct);
                                    RebuildView(); Repaint();
                                });
                            }
                            tm.ShowAsContext(); e.Use(); return;
                        }
                    }
                }

                var menu   = new GenericMenu();

                if (clickedNode != null && !clickedNode.isExit && !clickedNode.isUp)
                {
                    // ── Sub-state-machine node: enter or delete ─────────────────
                    if (clickedNode.isStateMachine && !ViewingSynced)
                    {
                        var cn = clickedNode;
                        menu.AddItem(new GUIContent("Open"), false, () => EnterSubSM(cn.subSM));
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Copy"),      false, () => smCopyBuffer = cn.subSM);
                        menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateNode(cn));
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Delete"), false, () =>
                        {
                            var sm2 = CurrentSM; if (sm2 == null || cn.subSM == null) return;
                            Undo.RecordObject(sm2, "Delete Sub-State Machine");
                            sm2.RemoveStateMachine(cn.subSM);
                            EditorUtility.SetDirty(sm2); RebuildView(); Repaint();
                        });
                    }
                    // ── Node context menu (matches Unity Animator exactly) ──────
                    else if (!clickedNode.isEntry && !clickedNode.isAnyState && !clickedNode.isStateMachine)
                    {
                        var cn = clickedNode;
                        if (ViewingSynced)
                        {
                            // Synced layer: structure is read-only — only the per-state clip can change.
                            menu.AddItem(new GUIContent("Set Override Motion…"), false, () => SelectSyncedState(cn));
                            if (cn.state != null && ctrl.layers[layer].GetOverrideMotion(cn.state) != null)
                                menu.AddItem(new GUIContent("Clear Override Motion"), false, () =>
                                {
                                    Undo.RecordObject(ctrl, "Clear Override Motion");
                                    ctrl.layers[layer].SetOverrideMotion(cn.state, null);
                                    EditorUtility.SetDirty(ctrl); LoadCtrl(ctrl, layer); Repaint();
                                });
                        }
                        else
                        {
                            menu.AddItem(new GUIContent("Make Transition"), false, () =>
                            {
                                selNode = cn;
                                makingTransFrom = cn;
                                Repaint();
                            });
                            menu.AddItem(new GUIContent("Set as Layer Default State"), false, () =>
                            {
                                var sm2 = CurrentSM; if (sm2 == null) return;
                                Undo.RecordObject(sm2, "Set Default State");
                                sm2.defaultState = cn.state;
                                EditorUtility.SetDirty(sm2); RebuildView(); Repaint();
                            });
                            menu.AddSeparator("");
                            menu.AddItem(new GUIContent("Create Blend Tree in State"), false, () => CreateBlendTreeInState(cn));
                            menu.AddSeparator("");
                            menu.AddItem(new GUIContent("Copy"), false, () =>
                            {
                                // If the clicked node isn't part of the current selection, select just it
                                if (!selNodes.Contains(cn)) { selNodes.Clear(); selNodes.Add(cn); selNode = cn; }
                                CopySelectionToBuffer();
                            });
                            menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateNode(cn));
                            menu.AddSeparator("");
                            menu.AddItem(new GUIContent("Delete"), false, () =>
                            {
                                var sm2 = CurrentSM; if (sm2 == null) return;
                                Undo.RecordObject(sm2, "Delete State");
                                // Delete the whole selection if the clicked node is part of it
                                if (selNodes.Contains(cn) && selNodes.Count > 1)
                                    foreach (var sn in selNodes) { if (sn.state != null) sm2.RemoveState(sn.state); }
                                else
                                    sm2.RemoveState(cn.state);
                                EditorUtility.SetDirty(sm2); RebuildView(); Repaint();
                            });
                        }
                    }
                    else if ((clickedNode.isAnyState || clickedNode.isEntry) && !ViewingSynced)
                    {
                        var cn = clickedNode;
                        menu.AddItem(new GUIContent("Make Transition"), false, () =>
                        {
                            selNode = cn;
                            makingTransFrom = cn;
                            Repaint();
                        });
                    }
                }
                else if (ViewingSynced)
                {
                    // Synced layer: can't add states here — structure comes from the source layer.
                    menu.AddDisabledItem(new GUIContent("Synced layer — structure is read-only"));
                }
                else
                {
                    // ── Empty canvas context menu ─────────────────────────────
                    // Creates target the currently-navigated state machine (root SM or a sub-SM).
                    menu.AddItem(new GUIContent("Create State/Empty"), false, () =>
                    {
                        var sm2 = CurrentSM; if (sm2 == null) return;
                        Undo.RecordObject(sm2, "Create State");
                        sm2.AddState("New State", cg);
                        EditorUtility.SetDirty(sm2); RebuildView(); Repaint();
                    });
                    menu.AddItem(new GUIContent("Create State/From New Blend Tree"), false, () =>
                    {
                        var sm2 = CurrentSM; if (sm2 == null) return;
                        Undo.RecordObject(sm2, "Create Blend Tree State");
                        EnsureBlendParameter();
                        var st = sm2.AddState("Blend Tree", cg);
                        var bt = new BlendTree { name = "Blend Tree" };
                        AssetDatabase.AddObjectToAsset(bt, ctrl);
                        Undo.RegisterCreatedObjectUndo(bt, "Create Blend Tree State");
                        st.motion = bt;
                        EditorUtility.SetDirty(sm2);
                        AssetDatabase.SaveAssets();
                        RebuildView(); Repaint();
                    });
                    menu.AddItem(new GUIContent("Create Sub-State Machine"), false, () =>
                    {
                        var sm2 = CurrentSM; if (sm2 == null) return;
                        Undo.RecordObject(sm2, "Create Sub-State Machine");
                        sm2.AddStateMachine("New StateMachine", cg);
                        EditorUtility.SetDirty(sm2); RebuildView(); Repaint();
                    });

                    Vector2 pastePos = cg;
                    menu.AddSeparator("");
                    if (copyBuffer.Count > 0)
                        menu.AddItem(new GUIContent(copyBuffer.Count > 1 ? "Paste States" : "Paste State"), false,
                            () => PasteBufferAt(pastePos));
                    else
                        menu.AddDisabledItem(new GUIContent("Paste State"));

                    // Whole-state-machine copy/paste: copies the current SM to paste as a sub-SM elsewhere.
                    menu.AddItem(new GUIContent("Copy State Machine"), false, () => smCopyBuffer = CurrentSM);
                    if (smCopyBuffer != null)
                        menu.AddItem(new GUIContent("Paste State Machine"), false, () => PasteStateMachine(pastePos));
                    else
                        menu.AddDisabledItem(new GUIContent("Paste State Machine"));
                }

                // Transition line context menu (structural edit — not on synced layers).
                // Only when no node is under the cursor, so right-clicking a node never shows it.
                if (clickedNode == null && !ViewingSynced && selTrans?.src != null)
                {
                    var st = selTrans;
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Delete Transition"), false, () =>
                    {
                        RemoveTransitionSrc(st);
                        RebuildView(); Repaint();
                    });
                }


                menu.ShowAsContext(); e.Use();
            }
        }

        // ── Selection / Copy-Paste ─────────────────────────────────────────────────
        private void FinalizeMarquee()
        {
            Rect g = MarqueeRectG();
            if (!boxAdditive) ClearCanvasSelection(false);   // Shift adds to the existing selection
            foreach (var n in nodes)
            {
                if ((n.state != null || n.isStateMachine) && g.Overlaps(NodeGraphRect(n))) selNodes.Add(n);
                foreach (var t in n.transitions)
                {
                    if (TransInMarquee(t, g)) selTranses.Add(t);
                    for (int i = 0; i < t.pts.Count; i++)
                        if (g.Contains(t.pts[i])) selReroutes.Add(new RR(t, i));
                }
            }

            selNode = null; selTrans = null;

            // Push marquee results to Unity selection (sub-state-machine nodes select their AnimatorStateMachine).
            var stateObjs = new List<Object>();
            foreach (var n in selNodes)
            {
                if (n.state != null)                     stateObjs.Add(n.state);
                else if (n.isStateMachine && n.subSM != null) stateObjs.Add(n.subSM);
            }
            var transObjs = new List<Object>();
            foreach (var t in selTranses) if (t.src != null) transObjs.Add(t.src);

            int total = stateObjs.Count + transObjs.Count;
            if (total == 0)
            {
                // Entry-default line uses its proxy note.
                foreach (var t in selTranses) if (t.isEntryDefault) { selTrans = t; Selection.activeObject = EntryInfoProxy; }
            }
            else if (total == 1)
            {
                if (stateObjs.Count == 1) { foreach (var n in selNodes) if (n.state != null || n.isStateMachine) selNode = n; Selection.activeObject = stateObjs[0]; }
                else { var l = new List<STrans>(selTranses); foreach (var t in l) if (t.src != null) selTrans = t; SelectTransitionInInspector(l); }
            }
            else if (stateObjs.Count > 0 && transObjs.Count > 0)
            {
                // Mixed state/transition selection uses the proxy inspector.
                var info = MultiSelProxy;
                info.states.Clear();      info.states.AddRange(stateObjs);
                info.transitions.Clear(); info.transitions.AddRange(transObjs);
                Selection.activeObject = info;
            }
            else if (stateObjs.Count > 0)
            {
                Selection.objects = stateObjs.ToArray();           // states use Unity's multi-edit
            }
            else
            {
                SelectTransitionInInspector(new List<STrans>(selTranses));   // all transitions → our inspector
            }
        }

        private void CopySelectionToBuffer()
        {
            copyBuffer.Clear();
            foreach (var n in selNodes)
                if (n.state != null)
                    copyBuffer.Add(new CopyData { name = n.name, motion = n.state.motion, speed = n.state.speed, tag = n.state.tag, position = n.position });
        }

        private void PasteBufferAt(Vector2 at)
        {
            if (ctrl == null || copyBuffer.Count == 0 || ViewingSynced || InBlendTree) return;  
            var sm = CurrentSM;
            if (sm == null) return;
            Undo.RecordObject(sm, "Paste State");

            // Preserve relative layout around the paste anchor.
            Vector2 min = copyBuffer[0].position;
            foreach (var c in copyBuffer) min = Vector2.Min(min, c.position);

            var targets = new List<Vector2>();
            foreach (var c in copyBuffer)
            {
                Vector2 p  = at + (c.position - min);
                var     st = sm.AddState(c.name, p);
                st.motion = c.motion; st.speed = c.speed; st.tag = c.tag;
                targets.Add(p);
            }
            EditorUtility.SetDirty(sm);
            RebuildView();

            // Restore selection after rebuilding the graph.
            selNodes.Clear(); selNode = null;
            foreach (var p in targets)
            {
                var n = nodes.Find(x => x.state != null && (x.position - p).sqrMagnitude < 1f);
                if (n != null) selNodes.Add(n);
            }
            if (selNodes.Count == 1)
                foreach (var n in selNodes) { selNode = n; Selection.activeObject = n.state; }
            Repaint();
        }

        // ── Copy / Paste transition parameters ────────────────────────────────────
        private void CopyTransParams(AnimatorStateTransition t)
        {
            if (t == null) return;
            transParamBuffer = new TransParamData
            {
                hasExitTime = t.hasExitTime, exitTime = t.exitTime,
                hasFixedDuration = t.hasFixedDuration, duration = t.duration, offset = t.offset,
                interruptionSource = t.interruptionSource, orderedInterruption = t.orderedInterruption,
                canTransitionToSelf = t.canTransitionToSelf, mute = t.mute, solo = t.solo,
            };
            foreach (var c in t.conditions) transParamBuffer.conditions.Add((c.mode, c.threshold, c.parameter));
        }

        private void PasteTransParams(AnimatorStateTransition t)
        {
            if (t == null || transParamBuffer == null) return;
            Undo.RecordObject(t, "Paste Transition Parameters");
            t.hasExitTime = transParamBuffer.hasExitTime; t.exitTime = transParamBuffer.exitTime;
            t.hasFixedDuration = transParamBuffer.hasFixedDuration; t.duration = transParamBuffer.duration; t.offset = transParamBuffer.offset;
            t.interruptionSource = transParamBuffer.interruptionSource; t.orderedInterruption = transParamBuffer.orderedInterruption;
            t.canTransitionToSelf = transParamBuffer.canTransitionToSelf; t.mute = transParamBuffer.mute; t.solo = transParamBuffer.solo;
            foreach (var c in t.conditions) t.RemoveCondition(c);   // clear existing
            foreach (var c in transParamBuffer.conditions) t.AddCondition(c.mode, c.threshold, c.parameter);
            EditorUtility.SetDirty(t);
        }

        // ── Deep copy (states / blend trees / sub-state-machines) ──────────────────
        // Clips are shared; blend trees are duplicated so editing a copy never touches the original.
        private Motion DuplicateMotion(Motion m) => m is BlendTree bt ? DuplicateBlendTree(bt) : m;

        private BlendTree DuplicateBlendTree(BlendTree src)
        {
            if (src == null) return null;
            // Object.Instantiate asserts on assets with strong sub-asset references; CopySerialized clones
            // the data safely. Children (clips and nested trees) are still shared until deep-copied below.
            var copy = new BlendTree();
            EditorUtility.CopySerialized(src, copy);
            copy.name = src.name; copy.hideFlags = HideFlags.None;
            if (ctrl != null) AssetDatabase.AddObjectToAsset(copy, ctrl);
            var children = copy.children;
            for (int i = 0; i < children.Length; i++)
                if (children[i].motion is BlendTree childBt) children[i].motion = DuplicateBlendTree(childBt);
            copy.children = children;
            return copy;
        }

        private void CopyState(AnimatorState src, AnimatorState dst)
        {
            dst.motion = DuplicateMotion(src.motion);
            dst.speed = src.speed; dst.cycleOffset = src.cycleOffset; dst.mirror = src.mirror;
            dst.iKOnFeet = src.iKOnFeet; dst.writeDefaultValues = src.writeDefaultValues; dst.tag = src.tag;
            dst.speedParameterActive       = src.speedParameterActive;       dst.speedParameter       = src.speedParameter;
            dst.cycleOffsetParameterActive = src.cycleOffsetParameterActive; dst.cycleOffsetParameter = src.cycleOffsetParameter;
            dst.mirrorParameterActive      = src.mirrorParameterActive;      dst.mirrorParameter      = src.mirrorParameter;
            dst.timeParameterActive        = src.timeParameterActive;        dst.timeParameter        = src.timeParameter;
        }

        private static void CopyTransitionSettings(AnimatorStateTransition src, AnimatorStateTransition dst)
        {
            dst.hasExitTime = src.hasExitTime; dst.exitTime = src.exitTime;
            dst.hasFixedDuration = src.hasFixedDuration; dst.duration = src.duration; dst.offset = src.offset;
            dst.interruptionSource = src.interruptionSource; dst.orderedInterruption = src.orderedInterruption;
            dst.canTransitionToSelf = src.canTransitionToSelf; dst.mute = src.mute; dst.solo = src.solo;
            foreach (var c in src.conditions) dst.AddCondition(c.mode, c.threshold, c.parameter);
        }

        // Deep-copy state-machine contents into a newly created destination.
        private void CopyStateMachineContents(AnimatorStateMachine src, AnimatorStateMachine dst)
        {
            if (src == null || dst == null) return;
            var stateMap = new Dictionary<AnimatorState, AnimatorState>();
            var smMap    = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();

            foreach (var cs in src.states)
            {
                var ns = dst.AddState(cs.state.name, cs.position);
                CopyState(cs.state, ns);
                stateMap[cs.state] = ns;
            }
            foreach (var css in src.stateMachines)
            {
                if (css.stateMachine == null || css.stateMachine == dst) continue;   // guard self-paste recursion
                var nsm = dst.AddStateMachine(css.stateMachine.name, css.position);
                smMap[css.stateMachine] = nsm;
                CopyStateMachineContents(css.stateMachine, nsm);
            }

            dst.entryPosition = src.entryPosition; dst.exitPosition = src.exitPosition;
            dst.anyStatePosition = src.anyStatePosition; dst.parentStateMachinePosition = src.parentStateMachinePosition;
            if (src.defaultState != null && stateMap.TryGetValue(src.defaultState, out var nd)) dst.defaultState = nd;

            foreach (var cs in src.states)
            {
                var from = stateMap[cs.state];
                foreach (var tr in cs.state.transitions)
                {
                    AnimatorStateTransition nt = null;
                    if (tr.isExit) nt = from.AddExitTransition();
                    else if (tr.destinationState != null && stateMap.TryGetValue(tr.destinationState, out var ds)) nt = from.AddTransition(ds);
                    else if (tr.destinationStateMachine != null && smMap.TryGetValue(tr.destinationStateMachine, out var dm)) nt = from.AddTransition(dm);
                    if (nt != null) CopyTransitionSettings(tr, nt);
                }
            }
            foreach (var tr in src.anyStateTransitions)
            {
                AnimatorStateTransition nt = null;
                if (tr.destinationState != null && stateMap.TryGetValue(tr.destinationState, out var ds)) nt = dst.AddAnyStateTransition(ds);
                else if (tr.destinationStateMachine != null && smMap.TryGetValue(tr.destinationStateMachine, out var dm)) nt = dst.AddAnyStateTransition(dm);
                if (nt != null) CopyTransitionSettings(tr, nt);
            }
            foreach (var tr in src.entryTransitions)
            {
                AnimatorTransition nt = null;
                if (tr.destinationState != null && stateMap.TryGetValue(tr.destinationState, out var ds)) nt = dst.AddEntryTransition(ds);
                else if (tr.destinationStateMachine != null && smMap.TryGetValue(tr.destinationStateMachine, out var dm)) nt = dst.AddEntryTransition(dm);
                if (nt != null) foreach (var c in tr.conditions) nt.AddCondition(c.mode, c.threshold, c.parameter);
            }
        }

        // ── Duplicate / Copy-Paste of nodes & state machines ──────────────────────
        private void DuplicateNode(SNode n)
        {
            if (n == null || ctrl == null || ViewingSynced || InBlendTree) return;
            var sm = CurrentSM; if (sm == null) return;
            Vector2 off = new Vector2(40f, 40f);

            if (n.isStateMachine && n.subSM != null)
            {
                Undo.RecordObject(sm, "Duplicate Sub-State Machine");
                var nsm = sm.AddStateMachine(n.subSM.name, n.position + off);
                CopyStateMachineContents(n.subSM, nsm);
            }
            else if (n.state != null)
            {
                Undo.RecordObject(sm, "Duplicate State");
                var ns = sm.AddState(n.state.name, n.position + off);
                CopyState(n.state, ns);
            }
            else return;

            EditorUtility.SetDirty(sm); AssetDatabase.SaveAssets(); RebuildView(); Repaint();
        }

        private void PasteStateMachine(Vector2 at)
        {
            if (smCopyBuffer == null || ctrl == null || ViewingSynced || InBlendTree) return;
            var dst = CurrentSM; if (dst == null) return;
            Undo.RecordObject(dst, "Paste State Machine");
            var nsm = dst.AddStateMachine(smCopyBuffer.name, at);
            CopyStateMachineContents(smCopyBuffer, nsm);
            EditorUtility.SetDirty(dst); AssetDatabase.SaveAssets(); RebuildView(); Repaint();
        }

        // ── Operations ────────────────────────────────────────────────────────────
        // Transition onto a sub-SM / "(UP)" node: pick the concrete destination state from a menu listing
        // the target machine's states (recursively, as submenus). Creates a cross-boundary transition.
        private void ShowCrossTransitionMenu(SNode from, SNode target)
        {
            if (from == null || target == null || ViewingSynced || InBlendTree) return;
            AnimatorStateMachine targetSM = target.isUp
                ? (smStack.Count > 1 ? smStack[smStack.Count - 2] : RootSM)
                : target.subSM;
            if (targetSM == null) return;

            var menu = new GenericMenu();
            AddStatesSubmenu(menu, "States/", targetSM, from);
            menu.ShowAsContext();
        }

        private void AddStatesSubmenu(GenericMenu menu, string prefix, AnimatorStateMachine sm, SNode from)
        {
            if (sm == null) return;
            foreach (var cs in sm.states)
            {
                var target = cs.state;
                menu.AddItem(new GUIContent(prefix + cs.state.name), false, () => MakeCrossTransition(from, target));
            }
            foreach (var css in sm.stateMachines)
                if (css.stateMachine != null)
                    AddStatesSubmenu(menu, prefix + css.stateMachine.name + "/", css.stateMachine, from);
        }

        // Create a transition from `from` (a state / Any State / Entry node) to a specific state that lives
        // in another (parent or child) state machine. BuildSMGraph then routes its line to the sub-SM/"(UP)" node.
        private void MakeCrossTransition(SNode from, AnimatorState target)
        {
            if (from == null || target == null || ctrl == null) return;
            if (from.state != null)
            {
                Undo.RecordObject(from.state, "Add Transition");
                from.state.AddTransition(target);
                EditorUtility.SetDirty(from.state);
            }
            else if (from.isAnyState && CurrentSM != null)
            {
                Undo.RecordObject(CurrentSM, "Add AnyState Transition");
                CurrentSM.AddAnyStateTransition(target);
                EditorUtility.SetDirty(CurrentSM);
            }
            else if (from.isEntry && CurrentSM != null)
            {
                Undo.RecordObject(CurrentSM, "Add Entry Transition");
                CurrentSM.AddEntryTransition(target);
                EditorUtility.SetDirty(CurrentSM);
            }
            else return;
            RebuildView(); Repaint();
        }

        private void CommitTransition(SNode from, SNode to)
        {
            if (ctrl == null || ViewingSynced || InBlendTree) return;  
            var sm = CurrentSM;
            if (sm == null) return;

            if (from.isEntry)
            {
                if (to.state == null) return;
                Undo.RecordObject(sm, "Add Transition");
                sm.AddEntryTransition(to.state);
                EditorUtility.SetDirty(sm);
            }
            else if (from.isAnyState)
            {
                if (to.state == null) return;
                Undo.RecordObject(sm, "Add AnyState Transition");
                sm.AddAnyStateTransition(to.state);
                EditorUtility.SetDirty(sm);
            }
            else if (from.state != null)
            {
                if (to.isExit)
                {
                    Undo.RecordObject(from.state, "Add Exit Transition");
                    from.state.AddExitTransition();
                    EditorUtility.SetDirty(from.state);
                }
                else if (to.state != null)
                {
                    Undo.RecordObject(from.state, "Add Transition");
                    from.state.AddTransition(to.state);
                    EditorUtility.SetDirty(from.state);
                }
            }
            RebuildView();
        }

        // A new BlendTree's blendParameter defaults to "Blend"; create that float parameter if missing so
        // the controller doesn't warn "uses parameter 'Blend' which does not exist" (matches Unity).
        private void EnsureBlendParameter()
        {
            if (ctrl == null) return;
            foreach (var p in ctrl.parameters)
                if (p.type == AnimatorControllerParameterType.Float && p.name == "Blend") return;
            ctrl.AddParameter("Blend", AnimatorControllerParameterType.Float);
        }

        private void CreateBlendTreeInState(SNode node)
        {
            if (node.state == null || ctrl == null) return;
            EnsureBlendParameter();
            var bt = new BlendTree { name = node.name };
            AssetDatabase.AddObjectToAsset(bt, ctrl);
            Undo.RegisterCreatedObjectUndo(bt, "Create Blend Tree in State");
            Undo.RecordObject(node.state, "Create Blend Tree in State");
            var st = node.state;
            node.state.motion = bt;
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            RebuildView();
            Selection.activeObject = st;
            Repaint();
        }

        // Invoked by the layer settings popup after sync changes.
        internal static void NotifyLayerSyncChanged(AnimatorController changed)
        {
            EditorApplication.delayCall += () =>
            {
                foreach (var w in Resources.FindObjectsOfTypeAll<AnimatorPlusPlusWindow>())
                    if (w.ctrl == changed) { w.LoadCtrl(changed, w.layer); w.Repaint(); }
            };
        }

        // ── Reroute persistence ─────────────────────────────────────────────────────
        private AnimatorRerouteData FindRerouteData()
        {
            if (ctrl == null) return null;
            string path = AssetDatabase.GetAssetPath(ctrl);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
                if (a is AnimatorRerouteData d) return d;
            return null;
        }

        private AnimatorRerouteData GetOrCreateRerouteData()
        {
            var data = FindRerouteData();
            if (data != null) return data;
            if (ctrl == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(ctrl))) return null;
            data = ScriptableObject.CreateInstance<AnimatorRerouteData>();
            data.name      = "RerouteData";
            data.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(data, ctrl);
            return data;
        }

        // Re-apply persisted reroute points to the rebuilt graph.
        private void LoadReroutes()
        {
            var data = FindRerouteData();
            if (data == null) return;
            var view = CurrentSM;
            foreach (var node in nodes)
                foreach (var t in node.transitions)
                    if (t.src != null)
                    {
                        var e = data.entries.Find(x => x.transition == t.src && x.stateMachine == view);
                        if (e != null && e.points != null) t.pts = new List<Vector2>(e.points);
                    }
        }

        // Persist reroute points for the current view.
        private void SaveReroutes()
        {
            var data = GetOrCreateRerouteData();
            if (data == null) return;

            Undo.RecordObject(data, "Edit Reroute Nodes");   // restores reroute points on undo/redo

            var view = CurrentSM;
            var currentSrcs = new HashSet<AnimatorStateTransition>();
            foreach (var node in nodes)
                foreach (var t in node.transitions)
                    if (t.src != null) currentSrcs.Add(t.src);

            // Drop orphans and THIS view's entries (we re-add them below); other views' entries are kept,
            // so the same cross-boundary transition keeps independent reroute paths per state-machine view.
            data.entries.RemoveAll(e => e.transition == null || (e.stateMachine == view && currentSrcs.Contains(e.transition)));
            foreach (var node in nodes)
                foreach (var t in node.transitions)
                    if (t.src != null && t.pts.Count > 0)
                        data.entries.Add(new RerouteEntry { transition = t.src, stateMachine = view, points = new List<Vector2>(t.pts) });

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        // ── Save positions ────────────────────────────────────────────────────────
        private void SavePositions()
        {
            if (ctrl == null || ctrl.layers.Length == 0 || InBlendTree) return;   // blend-tree layout is generated
            var sm = CurrentSM;   // synced layers use the source SM layout
            if (sm == null) return;
            Undo.RecordObject(sm, "Move State");

            var en = nodes.Find(n => n.isEntry);
            var xn = nodes.Find(n => n.isExit);
            var an = nodes.Find(n => n.isAnyState);
            var up = nodes.Find(n => n.isUp);
            if (en != null) sm.entryPosition    = en.position;
            if (xn != null) sm.exitPosition     = xn.position;
            if (an != null) sm.anyStatePosition = an.position;
            if (up != null) sm.parentStateMachinePosition = up.position;

            var so   = new SerializedObject(sm);
            so.Update();
            var prop = so.FindProperty("m_ChildStates");
            if (prop != null)
            {
                for (int i = 0; i < prop.arraySize; i++)
                {
                    var elem = prop.GetArrayElementAtIndex(i);
                    var st   = elem.FindPropertyRelative("m_State").objectReferenceValue as AnimatorState;
                    if (st == null) continue;
                    var node = nodes.Find(n => n.state == st);
                    if (node != null) elem.FindPropertyRelative("m_Position").vector3Value = node.position;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            // Sub-state-machine child positions (matched by the state machine reference).
            var smProp = so.FindProperty("m_ChildStateMachines");
            if (smProp != null)
            {
                so.Update();
                for (int i = 0; i < smProp.arraySize; i++)
                {
                    var elem = smProp.GetArrayElementAtIndex(i);
                    var css  = elem.FindPropertyRelative("m_StateMachine").objectReferenceValue as AnimatorStateMachine;
                    if (css == null) continue;
                    var node = nodes.Find(n => n.isStateMachine && n.subSM == css);
                    if (node != null) elem.FindPropertyRelative("m_Position").vector3Value = node.position;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorUtility.SetDirty(sm);
        }

        // ── View memory (per-view pan/zoom) ───────────────────────────────────────
        // Identifies the current view: controller + layer + sub-SM path + blend-tree path.
        private string ViewKey()
        {
            if (ctrl == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(ctrl.GetInstanceID()).Append('|').Append(layer);
            foreach (var s in smStack) sb.Append('/').Append(s != null ? s.GetInstanceID() : 0);
            foreach (var b in btStack) sb.Append('#').Append(b != null ? b.GetInstanceID() : 0);
            return sb.ToString();
        }

        private void StoreCurrentView()
        {
            if (!string.IsNullOrEmpty(currentViewKey)) viewStates[currentViewKey] = (pan, zoom);
        }

        // Call after (re)building a view that may differ from the last one. Saves the outgoing view's
        // camera, then restores the new view's remembered camera — or queues a zoom-to-fit on first visit.
        // A controller change forgets all remembered views so each starts fresh at zoom-to-fit.
        private void EnterView(bool sameController)
        {
            if (sameController) StoreCurrentView();
            else                viewStates.Clear();

            lastStructSig = StructSignature();   // baseline so Tick doesn't rebuild on the first frame
            currentViewKey = ViewKey();
            if (viewStates.TryGetValue(currentViewKey, out var st))
            {
                pan = st.pan; zoom = st.zoom;
                needFit = needCenter = false;
            }
            else
            {
                needFit = true;   // first visit: fit on next repaint
            }
        }

        // Fit all current nodes within the canvas (adjusts zoom + pan), never magnifying past 100%.
        private void ZoomToFit()
        {
            if (nodes == null || nodes.Count == 0) { zoom = 1f; return; }
            Vector2 mn = nodes[0].position, mx = nodes[0].position;
            foreach (var n in nodes)
            {
                mn = Vector2.Min(mn, n.position);
                mx = Vector2.Max(mx, n.position + NodeSize(n));   // include node extents
            }
            Vector2 content = Vector2.Max(mx - mn, new Vector2(1f, 1f));
            Vector2 center  = (mn + mx) * 0.5f;
            Vector2 cvsSize = new Vector2(Mathf.Max(position.width - SideWNow, 400f), Mathf.Max(position.height - ToolH, 400f));
            const float pad = 90f;
            float zfit = Mathf.Min((cvsSize.x - pad) / content.x, (cvsSize.y - pad) / content.y, 1f);
            zoom = Mathf.Clamp(zfit, ZMin, ZMax);
            pan  = cvsSize * 0.5f - center * zoom;
        }

        // ── Recenter ─────────────────────────────────────────────────────────────
        private void Recenter()
        {
            if (nodes == null || nodes.Count == 0) return;
            Vector2 mn = nodes[0].position, mx = nodes[0].position;
            foreach (var n in nodes) { mn = Vector2.Min(mn, n.position); mx = Vector2.Max(mx, n.position); }
            Vector2 center  = (mn + mx) * 0.5f;
            Vector2 cvsSize = new Vector2(Mathf.Max(position.width - SideWNow, 400f), Mathf.Max(position.height - ToolH, 400f));
            pan = cvsSize * 0.5f - center * zoom;
            Repaint();
        }

        // ── Drawing primitives ────────────────────────────────────────────────────
        private void DrawRounded(Rect r, float rad, Color col)
        {
            if (r.width <= 0 || r.height <= 0) return;
            rad = Mathf.Min(rad, r.width * .5f, r.height * .5f);
            Handles.BeginGUI();
            Handles.color = col;
            EditorGUI.DrawRect(new Rect(r.x + rad, r.y,       r.width - 2*rad, r.height),       col);
            EditorGUI.DrawRect(new Rect(r.x,       r.y + rad, r.width,         r.height - 2*rad), col);
            Handles.DrawSolidDisc(new Vector3(r.x    + rad, r.y    + rad), Vector3.forward, rad);
            Handles.DrawSolidDisc(new Vector3(r.xMax - rad, r.y    + rad), Vector3.forward, rad);
            Handles.DrawSolidDisc(new Vector3(r.xMax - rad, r.yMax - rad), Vector3.forward, rad);
            Handles.DrawSolidDisc(new Vector3(r.x    + rad, r.yMax - rad), Vector3.forward, rad);
            Handles.EndGUI();
        }

        private void DrawRoundedOutline(Rect r, float rad, float thick, Color col)
        {
            float o = thick;
            DrawRounded(new Rect(r.x - o, r.y - o, r.width + o*2, r.height + o*2), rad + o, col);
        }

        // Outline with a subtle top-to-bottom vertical gradient (brighter top, darker bottom).
        private void DrawRoundedOutlineGradient(Rect r, float rad, float thick, Color top, Color bot)
        {
            float o = thick;
            DrawRoundedGradient(new Rect(r.x - o, r.y - o, r.width + o*2, r.height + o*2), rad + o, top, bot);
        }

        // Subtle soft drop shadow behind a node. Several concentric dark layers — each a SINGLE convex
        // polygon so the fill has uniform alpha (no corner double-blend / "overlapping sprite" seams) —
        // offset toward the bottom-right (light from the top-left) and growing outward fake a soft blur.
        private void DrawNodeShadow(Rect r, float rad, bool beveled, float bev)
        {
            float z = Mathf.Max(1f, zoom);
            Vector2 off = new Vector2(2.5f, 3.5f) * z;
            const int layers = 5;
            var col = new Color(0f, 0f, 0f, 0.06f);            // overlap creates the soft falloff
            for (int i = layers; i >= 1; i--)
            {
                float grow = (i - 1) * 1.4f * z;
                var   sr   = new Rect(r.x - grow + off.x, r.y - grow + off.y, r.width + grow * 2f, r.height + grow * 2f);
                if (beveled) FillBeveledConvex(sr, bev + grow, col);
                else         FillRoundedConvex(sr, rad + grow, col);
            }
        }

        // A rounded rectangle filled as one anti-aliased convex polygon (uniform alpha, no internal overlap).
        private void FillRoundedConvex(Rect r, float rad, Color col)
        {
            if (r.width <= 0 || r.height <= 0) return;
            rad = Mathf.Clamp(rad, 0.01f, Mathf.Min(r.width, r.height) * 0.5f);
            var v = new List<Vector3>(20);
            void Arc(float cx, float cy, float a0, float a1)
            {
                const int n = 4;
                for (int k = 0; k <= n; k++)
                {
                    float a = Mathf.Lerp(a0, a1, k / (float)n) * Mathf.Deg2Rad;
                    v.Add(new Vector3(cx + Mathf.Cos(a) * rad, cy + Mathf.Sin(a) * rad, 0f));
                }
            }
            Arc(r.xMax - rad, r.y + rad,    -90f,   0f);   // TR
            Arc(r.xMax - rad, r.yMax - rad,   0f,  90f);   // BR
            Arc(r.x + rad,    r.yMax - rad,  90f, 180f);   // BL
            Arc(r.x + rad,    r.y + rad,    180f, 270f);   // TL
            Handles.color = col;
            Handles.DrawAAConvexPolygon(v.ToArray());
        }

        // A beveled hexagon filled as one anti-aliased convex polygon.
        private void FillBeveledConvex(Rect r, float bev, Color col)
        {
            if (r.width <= 0 || r.height <= 0) return;
            bev = Mathf.Clamp(bev, 0f, r.width * 0.5f);
            float yc = r.y + r.height * 0.5f;
            Handles.color = col;
            Handles.DrawAAConvexPolygon(
                new Vector3(r.x + bev, r.y),    new Vector3(r.xMax - bev, r.y),
                new Vector3(r.xMax, yc),        new Vector3(r.xMax - bev, r.yMax),
                new Vector3(r.x + bev, r.yMax), new Vector3(r.x, yc));
        }

        // ── Beveled hexagon nodes (sub-state-machine / "(UP)") ────────────────────
        // A horizontally-elongated hexagon ("<====>"): flat top/bottom, the left & right edges angle out
        // to a point at the vertical middle. Filled with a top→bottom gradient via horizontal bands whose
        // left/right inset tapers from `bev` at the edges to 0 at the middle.
        private void DrawBeveledGradient(Rect r, float bev, Color top, Color bot)
        {
            if (r.width <= 0 || r.height <= 0) return;
            bev = Mathf.Clamp(bev, 0f, r.width * 0.5f);
            int   bands = Mathf.Max(4, Mathf.RoundToInt(r.height));
            float bh    = r.height / bands;
            for (int i = 0; i < bands; i++)
            {
                float yTop  = r.y + i * bh;
                float yc    = yTop + bh * 0.5f;
                float u     = (yc - r.y) / r.height;                 // 0 = top, 1 = bottom
                float inset = bev * Mathf.Abs(2f * u - 1f);          // bev at edges, 0 at the middle
                Color c     = Color.Lerp(top, bot, (i + 0.5f) / bands);
                EditorGUI.DrawRect(new Rect(r.x + inset, yTop, r.width - inset * 2f, bh + 0.5f), c);
            }
        }

        // A solid beveled hexagon expanded by `thick` on every side — the building block for borders/rings.
        private void DrawBeveledOutline(Rect r, float bev, float thick, Color col)
        {
            float o = thick;
            DrawBeveledGradient(new Rect(r.x - o, r.y - o, r.width + o * 2, r.height + o * 2), bev + o, col, col);
        }

        private void DrawBeveledOutlineGradient(Rect r, float bev, float thick, Color top, Color bot)
        {
            float o = thick;
            DrawBeveledGradient(new Rect(r.x - o, r.y - o, r.width + o * 2, r.height + o * 2), bev + o, top, bot);
        }

        // Faded 2px blue selection ring OUTSIDE the beveled node border (mirrors DrawNodeSelOutline).
        private void DrawBeveledSelOutline(Rect r, float bev, float nodeOutline)
        {
            float w = 2f * Mathf.Max(1f, zoom);
            const int bands = 8;
            for (int i = bands; i >= 1; i--)
            {
                float o = nodeOutline + w * i / bands;
                float t = (i - 1f) / (bands - 1f);
                DrawBeveledOutline(r, bev, o, new Color(NSelBord.r, NSelBord.g, NSelBord.b, 1f - t));
            }
        }

        // Rounded-rect filled with a vertical gradient, sliced into horizontal bands.
        private void DrawRoundedGradient(Rect r, float rad, Color top, Color bot)
        {
            if (r.width <= 0 || r.height <= 0) return;
            rad = Mathf.Clamp(rad, 0f, Mathf.Min(r.width, r.height) * 0.5f);

            // Bands follow the rounded outline so the gradient has no corner seams.
            int   bands = Mathf.Max(6, Mathf.RoundToInt(r.height));
            float bh    = r.height / bands;
            for (int i = 0; i < bands; i++)
            {
                float yTop = r.y + i * bh;
                float yc   = yTop + bh * 0.5f;
                Color c    = Color.Lerp(top, bot, (i + 0.5f) / bands);

                float dy    = Mathf.Min(yc - r.y, r.yMax - yc);   // distance to the nearer top/bottom edge
                float inset = dy < rad ? rad - Mathf.Sqrt(Mathf.Max(0f, rad * rad - (rad - dy) * (rad - dy))) : 0f;

                EditorGUI.DrawRect(new Rect(r.x + inset, yTop, Mathf.Max(0f, r.width - inset * 2f), bh + 0.6f), c);
            }
        }
    }

    // Lightweight selectable object whose only purpose is to show Unity's "default entry
    // transition is not previewable" note in the Inspector when that line is selected.
    public class DefaultEntryTransitionInfo : ScriptableObject { }

    [CustomEditor(typeof(DefaultEntryTransitionInfo))]
    public class DefaultEntryTransitionInfoEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Default entry transitions (displayed in orange) are not previewable. " +
                "To preview a transition please select a State transition (displayed in white)",
                MessageType.Info);
        }

        protected override void OnHeaderGUI() { }
    }

    // (AnimatorRerouteData / RerouteEntry moved to their own file AnimatorRerouteData.cs — a ScriptableObject
    //  saved as an asset must be in a file of the same name or Unity can't resolve its script on reload.)

    // Selected when a marquee catches mixed element types — holds the grouped objects so the
    // Inspector can show Unity's "Narrow your selection" tool (click a group to select just it).
    public class MultiSelectionInfo : ScriptableObject
    {
        public System.Collections.Generic.List<Object> states      = new System.Collections.Generic.List<Object>();
        public System.Collections.Generic.List<Object> transitions = new System.Collections.Generic.List<Object>();
    }

    [CustomEditor(typeof(MultiSelectionInfo))]
    public class MultiSelectionInfoEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var info = (MultiSelectionInfo)target;
            EditorGUILayout.LabelField("Narrow your selection:", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            if (info.states != null && info.states.Count > 0)
            {
                int c = info.states.Count;
                if (GUILayout.Button($"{c} Animator State" + (c > 1 ? "s" : ""), EditorStyles.miniButton))
                    Selection.objects = info.states.ToArray();
            }
            if (info.transitions != null && info.transitions.Count > 0)
            {
                int c = info.transitions.Count;
                if (GUILayout.Button($"{c} Animator State Transition" + (c > 1 ? "s" : ""), EditorStyles.miniButton))
                    Selection.objects = info.transitions.ToArray();
            }
        }

        protected override void OnHeaderGUI() { }
    }

    // Selected when a state is clicked on a synced layer. The state's structure belongs to the source
    // layer (read-only here); only the synced layer's per-state override motion is editable.
    public class SyncedStateInfo : ScriptableObject
    {
        public AnimatorController          ctrl;
        public int                         layerIndex;
        public AnimatorState               state;
        public AnimatorPlusPlusWindow   window;
    }

    [CustomEditor(typeof(SyncedStateInfo))]
    public class SyncedStateInfoEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var info = (SyncedStateInfo)target;
            if (info.ctrl == null || info.state == null || info.layerIndex < 0 || info.layerIndex >= info.ctrl.layers.Length)
            {
                EditorGUILayout.HelpBox("Synced state is no longer available.", MessageType.Info);
                return;
            }

            var layer = info.ctrl.layers[info.layerIndex];
            EditorGUILayout.LabelField(info.state.name, EditorStyles.boldLabel);
            if (layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < info.ctrl.layers.Length)
                EditorGUILayout.LabelField("Synced from", info.ctrl.layers[layer.syncedLayerIndex].name);
            EditorGUILayout.Space(2);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Source Motion", info.state.motion, typeof(Motion), false);

            var current = layer.GetOverrideMotion(info.state);
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.ObjectField("Override Motion", current, typeof(Motion), false) as Motion;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(info.ctrl, "Set Override Motion");
                var layers = info.ctrl.layers;                    // modify the array and assign it back
                layers[info.layerIndex].SetOverrideMotion(info.state, next);
                info.ctrl.layers = layers;
                EditorUtility.SetDirty(info.ctrl);
                if (info.window != null) info.window.RefreshSyncedMotions();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("This layer syncs another layer. Its states and transitions are shared and read-only here — only the animation clip can be overridden.", MessageType.None);
        }

        protected override void OnHeaderGUI() { }
    }

    // The native transition / state / sub-state-machine inspectors read their context (controller, layer,
    // transition source) from the open Animator window. We inject those fields by reflection so they work
    // with the window closed. Degrades silently if Unity's internal API changes.
    internal static class NativeTransitionBridge
    {
        static bool        s_Init;
        static System.Type s_InspBase, s_CtxType;          // AnimatorTransitionInspectorBase / TransitionEditionContext
        static FieldInfo   s_Controller, s_LayerIndex, s_Contexts;
        static ConstructorInfo s_CtxCtor;
        static System.Type s_StateType;                    // StateEditor: m_ControllerContext / m_LayerIndexContext
        static FieldInfo    s_StateCtrl, s_StateLayer;
        static MethodInfo   s_StateInit;
        static System.Type s_SMType;                       // StateMachineInspector: m_RootStateMachine / m_ActiveStateMachine
        static FieldInfo    s_SMRoot, s_SMActive;
        static MethodInfo   s_SMInit;

        static void Init()
        {
            if (s_Init) return;
            s_Init = true;
            try
            {
                const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                s_InspBase = FindType("UnityEditor.Graphs.AnimationStateMachine.AnimatorTransitionInspectorBase");
                s_CtxType  = FindType("UnityEditor.Graphs.AnimationStateMachine.TransitionEditionContext");
                if (s_InspBase != null && s_CtxType != null)
                {
                    s_Controller = s_InspBase.GetField("m_Controller",         I);
                    s_LayerIndex = s_InspBase.GetField("m_LayerIndex",         I);
                    s_Contexts   = s_InspBase.GetField("m_TransitionContexts", I);
                    s_CtxCtor    = s_CtxType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 5);
                }

                s_StateType = FindType("UnityEditor.Graphs.AnimationStateMachine.StateEditor");
                if (s_StateType != null)
                {
                    s_StateCtrl  = s_StateType.GetField("m_ControllerContext", I);
                    s_StateLayer = s_StateType.GetField("m_LayerIndexContext", I);
                    s_StateInit  = s_StateType.GetMethod("Init", I, null, System.Type.EmptyTypes, null);
                }

                s_SMType = FindType("UnityEditor.Graphs.AnimationStateMachine.StateMachineInspector");
                if (s_SMType != null)
                {
                    s_SMRoot   = s_SMType.GetField("m_RootStateMachine",   I);
                    s_SMActive = s_SMType.GetField("m_ActiveStateMachine", I);
                    s_SMInit   = s_SMType.GetMethod("Init", I, null, System.Type.EmptyTypes, null);
                }
            }
            catch { /* leave Available == false */ }
        }

        public static bool Available
        {
            get { Init(); return s_InspBase != null && s_Controller != null && s_Contexts != null && s_CtxCtor != null; }
        }

        static System.Type FindType(string full) =>
            System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType(full); } catch { return null; } })
                .FirstOrDefault(x => x != null);

        // Feed every live native transition / state / sub-SM inspector whose target belongs to `ctrl`.
        // Re-run cheaply each tick — the inspectors get recreated and would otherwise lose their context.
        public static void EnsureContext(AnimatorController ctrl, int layerIndex)
        {
            if (!Available || ctrl == null) return;

            foreach (var obj in Resources.FindObjectsOfTypeAll(s_InspBase))
            {
                var ed = obj as UnityEditor.Editor;
                if (ed == null || ed.targets == null || ed.targets.Length == 0) continue;

                var targets  = ed.targets;
                var contexts = System.Array.CreateInstance(s_CtxType, targets.Length);
                bool ok = true;
                for (int i = 0; i < targets.Length && ok; i++)
                {
                    if (FindOwner(ctrl, targets[i], out var src, out var srcSM, out var ownerSM))
                        contexts.SetValue(s_CtxCtor.Invoke(new object[] { targets[i], src, srcSM, ownerSM, ctrl }), i);
                    else ok = false;
                }
                if (!ok) continue;

                try
                {
                    s_Controller.SetValue(ed, ctrl);
                    s_LayerIndex?.SetValue(ed, layerIndex);
                    s_Contexts.SetValue(ed, contexts);
                    ed.Repaint();
                }
                catch { }
            }

            // ── Native state inspector (StateEditor) ─────────────────────────────
            if (s_StateType != null && s_StateCtrl != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(s_StateType))
                {
                    var ed = obj as UnityEditor.Editor;
                    if (ed == null || !(ed.target is AnimatorState st) || !ControllerHasState(ctrl, st)) continue;
                    try
                    {
                        bool wasStale = s_StateCtrl.GetValue(ed) as AnimatorController != ctrl;
                        s_StateCtrl.SetValue(ed, ctrl);
                        s_StateLayer?.SetValue(ed, layerIndex);
                        if (wasStale) s_StateInit?.Invoke(ed, null);   // rebuild transition/behaviour sub-editors
                        ed.Repaint();
                    }
                    catch { }
                }
            }

            // ── Native sub-state-machine inspector (StateMachineInspector) ───────
            if (s_SMType != null && s_SMRoot != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(s_SMType))
                {
                    var ed = obj as UnityEditor.Editor;
                    if (ed == null || !(ed.target is AnimatorStateMachine sm)) continue;
                    var root = FindRootContaining(ctrl, sm);
                    if (root == null) continue;
                    try
                    {
                        bool wasStale = s_SMRoot.GetValue(ed) as AnimatorStateMachine != root;
                        s_SMRoot.SetValue(ed, root);
                        s_SMActive?.SetValue(ed, sm);
                        if (wasStale) s_SMInit?.Invoke(ed, null);   // rebuild transition/behaviour sub-editors
                        ed.Repaint();
                    }
                    catch { }
                }
            }
        }

        // The layer's root state machine that contains `target` (directly or nested), for the SM inspector context.
        static AnimatorStateMachine FindRootContaining(AnimatorController ctrl, AnimatorStateMachine target)
        {
            foreach (var layer in ctrl.layers)
                if (layer.stateMachine != null && SMContainsSM(layer.stateMachine, target))
                    return layer.stateMachine;
            return null;
        }

        static bool SMContainsSM(AnimatorStateMachine sm, AnimatorStateMachine target)
        {
            if (sm == null || target == null) return false;
            if (sm == target) return true;
            foreach (var css in sm.stateMachines)
                if (SMContainsSM(css.stateMachine, target)) return true;
            return false;
        }

        static bool ControllerHasState(AnimatorController ctrl, AnimatorState st)
        {
            foreach (var layer in ctrl.layers)
                if (SMHasState(layer.stateMachine, st)) return true;
            return false;
        }

        static bool SMHasState(AnimatorStateMachine sm, AnimatorState st)
        {
            if (sm == null) return false;
            foreach (var cs in sm.states) if (cs.state == st) return true;
            foreach (var css in sm.stateMachines) if (SMHasState(css.stateMachine, st)) return true;
            return false;
        }

        // Find the state / state-machine that owns a transition, plus its owning state machine.
        static bool FindOwner(AnimatorController ctrl, Object trObj, out AnimatorState src, out AnimatorStateMachine srcSM, out AnimatorStateMachine ownerSM)
        {
            src = null; srcSM = null; ownerSM = null;
            var tr = trObj as UnityEditor.Animations.AnimatorTransitionBase;
            if (tr == null) return false;
            foreach (var layer in ctrl.layers)
                if (SearchSM(layer.stateMachine, tr, ref src, ref srcSM, ref ownerSM)) return true;
            return false;
        }

        static bool SearchSM(AnimatorStateMachine sm, UnityEditor.Animations.AnimatorTransitionBase tr,
                             ref AnimatorState src, ref AnimatorStateMachine srcSM, ref AnimatorStateMachine ownerSM)
        {
            if (sm == null) return false;
            foreach (var cs in sm.states)
                foreach (var t in cs.state.transitions)
                    if (t == tr) { src = cs.state; ownerSM = sm; return true; }
            foreach (var t in sm.anyStateTransitions)
                if (t == tr) { srcSM = sm; ownerSM = sm; return true; }
            foreach (var t in sm.entryTransitions)
                if (t == tr) { srcSM = sm; ownerSM = sm; return true; }
            foreach (var css in sm.stateMachines)
                if (SearchSM(css.stateMachine, tr, ref src, ref srcSM, ref ownerSM)) return true;
            return false;
        }
    }

    // Reflection bridge to UnityEditor.BlendTreeInspector's internal preview-value store (the "red dot").
    // Lets our control sliders and the inspector share a single value. Falls back silently (Available ==
    // false) if the internal API differs in another Unity version — callers then use their own fallback.
    internal static class BlendPreviewBridge
    {
        static bool       s_Init;
        static MethodInfo s_Get, s_Set;          // internal preview accessors
        static FieldInfo  s_CurAnimator, s_ParentBt, s_Callback;

        static void Init()
        {
            if (s_Init) return;
            s_Init = true;
            try
            {
                var t = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.BlendTreeInspector");
                if (t == null) return;
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                s_Get         = t.GetMethods(S).FirstOrDefault(m => m.Name == "GetParameterValue" && m.GetParameters().Length == 3);
                s_Set         = t.GetMethods(S).FirstOrDefault(m => m.Name == "SetParameterValue" && m.GetParameters().Length == 5);
                s_CurAnimator = t.GetField("currentAnimator",            S);
                s_ParentBt    = t.GetField("parentBlendTree",           S);
                s_Callback    = t.GetField("blendParameterInputChanged", S);
            }
            catch { /* leave Available == false */ }
        }

        public static bool Available { get { Init(); return s_Get != null && s_Set != null; } }

        static Animator  CurAnimator() { try { return s_CurAnimator?.GetValue(null) as Animator;  } catch { return null; } }
        static BlendTree ParentBt()    { try { return s_ParentBt?.GetValue(null)    as BlendTree; } catch { return null; } }

        // Read the inspector's current preview value for a blend parameter.
        public static bool TryGet(BlendTree bt, string param, out float value)
        {
            value = 0f;
            Init();
            if (s_Get == null || bt == null || string.IsNullOrEmpty(param)) return false;
            try { value = (float)s_Get.Invoke(null, new object[] { CurAnimator(), bt, param }); return true; }
            catch { return false; }
        }

        // Write the preview value (moves the red dot) and notify listeners so it refreshes.
        public static bool TrySet(BlendTree bt, string param, float value)
        {
            Init();
            if (s_Set == null || bt == null || string.IsNullOrEmpty(param)) return false;
            try
            {
                s_Set.Invoke(null, new object[] { CurAnimator(), bt, ParentBt(), param, value });
                try { (s_Callback?.GetValue(null) as System.Delegate)?.DynamicInvoke(bt); }
                catch { /* optional inspector refresh callback */ }
                return true;
            }
            catch { return false; }
        }
    }

    // Bridge to Unity's internal Animator graph nodes for Entry / Any State / Exit.
    // Falls back when the internal API is unavailable.
    internal class NativeSpecialNodeBridge
    {
        public enum Kind { Entry, AnyState, Exit }

        static System.Type s_GraphType, s_NodeType;
        static MethodInfo s_SetStateMachines, s_BuildGraph;
        static FieldInfo s_RootSM, s_EntryNode, s_AnyStateNode, s_ExitNode, s_Nodes;
        static bool s_Init;

        static void Init()
        {
            if (s_Init) return;
            s_Init = true;
            try
            {
                s_GraphType = FindType("UnityEditor.Graphs.AnimationStateMachine.Graph");
                s_NodeType  = FindType("UnityEditor.Graphs.Node");
                if (s_GraphType == null) return;

                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                s_SetStateMachines = s_GraphType.GetMethod("SetStateMachines", I);
                s_BuildGraph       = s_GraphType.GetMethod("BuildGraphFromStateMachine", I);
                s_RootSM           = s_GraphType.GetField("rootStateMachine", I);
                s_EntryNode        = s_GraphType.GetField("m_EntryNode", I);
                s_AnyStateNode     = s_GraphType.GetField("m_AnyStateNode", I);
                s_ExitNode         = s_GraphType.GetField("m_ExitNode", I);
                s_Nodes            = s_GraphType.GetField("nodes", I);
            }
            catch { /* leave Available == false */ }
        }

        public static bool Available
        {
            get { Init(); return s_GraphType != null && s_BuildGraph != null && s_EntryNode != null; }
        }

        static System.Type FindType(string full) =>
            System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType(full); } catch { return null; } })
                .FirstOrDefault(x => x != null);

        // ── per-instance cache ───────────────────────────────────────────────────
        ScriptableObject m_Graph;
        AnimatorStateMachine m_BuiltFor;

        // Returns the native node object for the given special kind, building/refreshing the backing
        // graph as needed. Null when unavailable — caller should fall back.
        public UnityEngine.Object GetNode(AnimatorStateMachine sm, Kind kind)
        {
            Init();
            if (!Available || sm == null) return null;
            if (m_Graph == null || m_BuiltFor != sm) Rebuild(sm);
            if (m_Graph == null) return null;

            var f = kind == Kind.Entry ? s_EntryNode : kind == Kind.AnyState ? s_AnyStateNode : s_ExitNode;
            try { return f?.GetValue(m_Graph) as UnityEngine.Object; }
            catch { return null; }
        }

        void Rebuild(AnimatorStateMachine sm)
        {
            Dispose();
            try
            {
                m_Graph = ScriptableObject.CreateInstance(s_GraphType);
                if (m_Graph == null) return;
                m_Graph.hideFlags = HideFlags.HideAndDontSave;

                s_RootSM?.SetValue(m_Graph, sm);
                // Internal API expects active, parent and root SMs.
                s_SetStateMachines?.Invoke(m_Graph, new object[] { sm, sm, sm });
                s_BuildGraph?.Invoke(m_Graph, new object[] { sm });
                m_BuiltFor = sm;
            }
            catch
            {
                Dispose();   // drop partial graphs and let callers fall back
            }
        }

        // Destroy the graph and every node it spawned so we don't leak ScriptableObjects across rebuilds.
        public void Dispose()
        {
            m_BuiltFor = null;
            if (m_Graph == null) return;
            try
            {
                if (s_Nodes?.GetValue(m_Graph) is IEnumerable list)
                    foreach (var n in list)
                        if (n is UnityEngine.Object uo && uo != null) UnityEngine.Object.DestroyImmediate(uo);
            }
            catch
            {
                // Internal graph node layout may differ between Unity versions.
            }

            try { UnityEngine.Object.DestroyImmediate(m_Graph); }
            catch { /* object may already be invalid during domain reload */ }
            m_Graph = null;
        }
    }
}
