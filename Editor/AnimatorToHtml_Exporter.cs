#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace TohruTheDragon.AnimatorToHtml
{
    // Exports an AnimatorController to a single, self-contained interactive HTML inspector + graph app. States
    // use the Animator's 200x40 node size; states show WD / None badges; layers show index / None / warning / WD
    // (DBT-exempt) / Mask badges. Sub-machines are gray hexagons; default state + default entry are orange; other
    // transition lines are white with a coloured single arrowhead (blue = no exit time). Blend trees are graphed
    // in Unity type colours; behaviours (Parameter Driver / Play Audio / …) render as inspector-style field
    // blocks. The dopesheet has tangent-accurate live values and a draggable timeline. No external dependencies.
    //
    // EXPORT POLICY - this page ignores the user's display settings, deliberately.
    //
    // Every marker is included whether or not its on-canvas toggle is on (blend type tags, "Ex"/"Exc", the
    // broken-transition "!", Write Defaults badges and outliers, "None", the ID badge), and every one of them is
    // drawn in the colour the addon SHIPS with rather than the user's palette. The PNG and SVG exports do the
    // opposite, and should: those are pictures of your graph as you have it set up. This one is a debugging
    // artifact - it gets sent to someone else, and it is worth far more if two people looking at the same
    // controller see the same page. A marker missing because the sender had it switched off is a wrong answer,
    // and a colour that means one thing in your editor and another in theirs is worse than no colour.
    //
    // Write Defaults treats Direct Blend Trees as exempt unconditionally, for a different reason: that one is a
    // VRChat rule rather than a preference. See BuildWdStats.
    //
    // So: do NOT "restore consistency" by reintroducing a settings check here. If a marker should be optional in
    // the export, that wants its own export-side option, not the canvas's.
    public static class AnimatorToHtml_Exporter
    {
        private const float NW = 200f, NH = 40f;   // state node size (matches the Animator / live canvas)
        private const float SW = 160f, SH = 30f;   // Entry / Any State / Exit are 40px narrower, a bit shorter

        [Serializable] private class ExportData { public string name; public ParamData[] parameters; public LayerData[] layers; public GraphData[] graphs; public ClipData[] clips; public string aapIcon; }
        [Serializable] private class ParamData { public string name; public string type; public string def; public bool vrc; public bool mismatch; }
        [Serializable] private class LayerData { public string name; public int index; public float weight; public string mask; public string blending; public bool ik; public bool sync; public bool timing; public string wd; public bool none; public bool idm; public string root; public bool bad; public bool btr; public bool wdo; public string wdTip; public string noneTip; }
        [Serializable] private class GraphData { public string id; public string kind; public string title; public NodeData[] nodes; public EdgeData[] edges; }
        // ex/exc/ext: the "Ex" external-blend-tree marker, its colour and its tooltip - null unless the tree
        // lives in its own asset file and the marker is enabled for that surface. Drawn centre-left on states
        // and at the right of the header on blend tree nodes, matching the live canvases.
        // btag/btagc/btagt: the blend-tree type tag ([DIR], [S1D], …), its colour and its tooltip. Empty when the
        // node isn't a tree or the matching toggle is off - resolved C#-side so the export matches the editor.
        [Serializable] private class NodeData { public string id; public string type; public string label; public float x; public float y; public float w; public float h; public bool wd; public bool isDefault; public bool none; public bool tol; public bool attrs; public string attrTip; public string idm; public string motion; public string[] lines; public string child; public string clip; public string[] details; public BehData[] behaviours; public BtInfo bt; public string btag; public string btagc; public string btagt; public string ex; public string exc; public string ext; public bool warn; public string warnc; public string warnt; public bool aap; }
        [Serializable] private class BtInfo { public string mode; public string px; public string py; public ChildW[] children; }
        [Serializable] private class ChildW { public string cid; public float th; public float x; public float y; public string dp; public string name; }
        [Serializable] private class BehData { public string name; public string[] fields; }
        [Serializable] private class EdgeData { public string fromId; public string toId; public string kind; public string summary; public string srcPath; public string dstPath; public string label; public bool tol; public bool isStateTr; public bool po; public bool hasExit; public string exitTime; public bool fixedDur; public string duration; public string offset; public string interrupt; public bool ordered; public bool solo; public bool mute; public bool warn; public string warnTip; public CondData[] conds; }
        [Serializable] private class CondData { public string param; public string op; public string value; public bool isBool; public bool boolVal; }
        [Serializable] private class ClipData { public string id; public string name; public float length; public float fps; public int frames; public bool loop; public PropData[] props; }
        [Serializable] private class PropData { public string path; public string type; public string prop; public KeyData[] keys; }
        [Serializable] private class KeyData { public float t; public float v; public float inT; public float outT; public bool co; }

        private static int _graphSeq;
        private static List<GraphData> _graphs;
        private static Dictionary<AnimationClip, string> _clipIds;
        private static List<ClipData> _clips;
        private static Dictionary<string, AnimatorControllerParameterType> _ptypes;

        // The controller being exported. Needed deep inside the blend-tree walk to resolve the "Ex"/"Exc"
        // ownership marker, which is a question about whether a tree lives in THIS file - held as a field for
        // the same reason _ptypes is, rather than threaded through every nested local function.
        private static AnimatorController _ctrl;

        public static void Export(AnimatorController controller) => Export(controller, null);

        // layerIndices == null (or empty) → whole controller. Otherwise only those layers are graphed and only the
        // parameters those layers actually reference (conditions, blend-tree params, state speed/time/etc., AAPs,
        // Parameter Driver targets) are listed.
        public static void Export(AnimatorController controller, HashSet<int> layerIndices)
        {
            if (controller == null) { EditorUtility.DisplayDialog(Ath.Tr("dialog.htmlExport.title"), Ath.Tr("dialog.htmlExport.noController"), Ath.Tr("dialog.btn.ok")); return; }
            bool scoped = layerIndices != null && layerIndices.Count > 0;
            string suggested = Ath.Sanitize(controller.name, "Controller") + (scoped ? "_layers" : "") + ".html";
            string path = EditorUtility.SaveFilePanel("Export Controller to HTML", Ath.LastExportFolder(), suggested, "html");
            if (string.IsNullOrEmpty(path)) return;
            Ath.RememberExportFolder(path);
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                ExportToPath(controller, path, scoped ? layerIndices : null);
                // Import it when it landed inside the project, or RevealInFinder has nothing to select.
                if (path.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase)) AssetDatabase.Refresh();
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e) { Debug.LogError($"[Animator To HTML] HTML export failed: {e}"); EditorUtility.DisplayDialog(Ath.Tr("dialog.htmlExport.title"), Ath.Tr("dialog.htmlExport.failed", e.Message), Ath.Tr("dialog.btn.ok")); }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        // No dialogs, no save panel: writes the page straight to `path`. Throws on failure.
        public static void ExportToPath(AnimatorController controller, string path, HashSet<int> layerIndices = null)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            try { System.IO.File.WriteAllText(path, Build(controller, layerIndices), Encoding.UTF8); }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        private static string Build(AnimatorController c, HashSet<int> layerFilter = null)
        {
            _graphSeq = 0; _graphs = new List<GraphData>();
            _clipIds = new Dictionary<AnimationClip, string>(); _clips = new List<ClipData>();
            _ctrl = c;
            _ptypes = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in c.parameters) if (p != null && !string.IsNullOrEmpty(p.name)) _ptypes[p.name] = p.type;

            // When scoping to selected layers, gather the parameters those layers actually reference so the
            // Parameters tab lists only what's relevant. null set = whole controller (list every parameter).
            bool scoped = layerFilter != null && layerFilter.Count > 0;
            HashSet<string> usedParams = null;
            if (scoped)
            {
                usedParams = new HashSet<string>();
                var visSm = new HashSet<AnimatorStateMachine>();
                // controller.layers marshals a fresh array per read - hoist it rather than paying for one
                // per loop iteration (and another per body access).
                var scopeLayers = c.layers;
                for (int i = 0; i < scopeLayers.Length; i++)
                    if (layerFilter.Contains(i) && scopeLayers[i].stateMachine != null)
                        CollectUsedParameters(scopeLayers[i].stateMachine, usedParams, visSm);
            }

            var data = new ExportData { name = c.name };

            var pd = new List<ParamData>();
            foreach (var p in c.parameters)
            {
                if (p == null) continue;
                if (scoped && !usedParams.Contains(p.name)) continue;
                string def;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool: def = p.defaultBool ? "true" : "false"; break;
                    case AnimatorControllerParameterType.Int: def = p.defaultInt.ToString(CultureInfo.InvariantCulture); break;
                    case AnimatorControllerParameterType.Float: def = F(p.defaultFloat); break;
                    default: def = ""; break;
                }
                bool vrc = Ath.TryGetVrcBuiltInParam(p.name, out var vi);
                pd.Add(new ParamData { name = p.name, type = p.type.ToString(), def = def, vrc = vrc, mismatch = vrc && vi.Type != p.type });
            }
            data.parameters = pd.ToArray();

            // Counted once, before any graph is built: the per-state "!" outlier test reads these.
            BuildWdStats(c);

            var ld = new List<LayerData>();
            var visited = new Dictionary<AnimatorStateMachine, string>();
            var layers = c.layers; // fresh array per read - see the scope loop above
            for (int i = 0; i < layers.Length; i++)
            {
                if (scoped && !layerFilter.Contains(i)) continue;
                var layer = layers[i];
                string wd = ""; bool none = false;
                if (layer.stateMachine != null) LayerAnalyze(layer.stateMachine, out wd, out none);

                // The layer badge reports four independent faults through one "!" - a genuine Write Defaults mix,
                // a Write Defaults OUTLIER, a broken blend tree ownership link, and a broken transition -
                // matching the window's layer list, where any one of them raises the same mark on its own.
                //
                // Mix and outlier are different questions and both are asked: a mix means the layer disagrees
                // with ITSELF, an outlier that it disagrees with the CONTROLLER. A layer that is uniformly the
                // minority kind is not mixed at all, which is exactly how a whole face-tracking layer left
                // Write Defaults ON inside an OFF controller used to export looking clean.
                //
                // The tooltip accumulates EVERY fault that applies, in badge order, blank line between, so one
                // hover names all of them. An all-on WD layer contributes nothing to that list: "everything is
                // fine" is not worth saying beside something that is not. It is only used as the tooltip when
                // there is no fault at all, which is the plain green "WD" case.
                bool linkBad = layer.stateMachine != null
                               && Ath_MotionOwnership.StateMachineHasBackwardsReference(layer.stateMachine);
                bool trBad = layer.stateMachine != null
                             && LayerHasBrokenTransition(layer.stateMachine, new HashSet<AnimatorStateMachine>());
                bool wdOut = layer.stateMachine != null
                             && LayerHasWdOutlier(layer.stateMachine, new HashSet<AnimatorStateMachine>());

                string badgeTip = "";
                if (wd == "mixed") badgeTip = JoinTip(badgeTip, Ath.Tr("badge.layerWdMixed"));
                if (wdOut) badgeTip = JoinTip(badgeTip, Ath.Tr("badge.layerWdOutlier"));
                if (linkBad) badgeTip = JoinTip(badgeTip, Ath.Tr("badge.layerBrokenLink"));
                if (trBad) badgeTip = JoinTip(badgeTip, Ath.Tr("badge.layerBrokenTransition"));
                if (badgeTip.Length == 0 && wd == "on") badgeTip = Ath.Tr("badge.layerWdAllOn");

                // Where every state and machine in this layer SITS, so a transition can name its real endpoints
                // rather than the nodes it happens to be drawn between. Per layer, because a path is only
                // meaningful within one - and cleared each time so nothing leaks between layers.
                BuildPathIndex(layer.stateMachine);

                ld.Add(new LayerData
                {
                    name = layer.name, index = i, weight = i == 0 ? 1f : layer.defaultWeight,
                    mask = layer.avatarMask != null ? layer.avatarMask.name : "",
                    blending = layer.blendingMode.ToString(),
                    ik = layer.iKPass, sync = layer.syncedLayerIndex >= 0, timing = layer.syncedLayerAffectsTiming,
                    wd = wd, none = none, bad = linkBad, btr = trBad, wdo = wdOut, wdTip = badgeTip,
                    noneTip = none ? Ath.Tr("badge.layerNoneTooltip") : "",
                    idm = layer.stateMachine != null && HasIdentifier(layer.stateMachine, new HashSet<AnimatorStateMachine>()),
                    // The layer root's default state is the origin of the "(Up)" colour rule, and the only place
                    // it can be read - every nested call receives it rather than asking its own machine.
                    root = layer.stateMachine != null
                        // layerRoot is the layer's OWN machine here. Without it the root graph had no notion of
                        // "leaves the layer" at all, so nothing in it wore the dot or the tag - only the nested
                        // graphs, which are handed it further down.
                        ? BuildSmGraph(layer.stateMachine, layer.name, visited, null, null, layer.stateMachine.defaultState, layer.stateMachine) : ""
                });
            }
            data.layers = ld.ToArray();
            data.graphs = _graphs.ToArray();
            data.clips = _clips.ToArray();

            // The AAP badge as a data URI, embedded ONCE for the whole document rather than per node - the same
            // image the canvas and the other two exporters draw. Null when the icon is missing, which the page
            // falls back on exactly as the live node does, with the letters.
            data.aapIcon = Ath.AapIconDataUri();

            // The data is embedded as `const DATA=<json>` inside a <script>. JsonUtility already escapes " and \,
            // so string values can't break out of their JS string literals. Neutralise the remaining ways the raw
            // text could affect the HTML/JS parser: escape every '<' (blocks </script>, <!--, <script - no tag can
            // start or the block close early) and the U+2028/U+2029 line separators (illegal in JS string literals
            // pre-ES2019). '<' only ever appears inside JSON string values, so a blanket replace is safe.
            string json = JsonUtility.ToJson(data)
                .Replace("<", "\\u003c")
                .Replace("\u2028", "\\u2028")
                .Replace("\u2029", "\\u2029");

            var sb = new StringBuilder(1 << 17);
            sb.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'>");
            sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1'>");
            sb.Append("<title>").Append(E(c.name)).Append(" - Animator</title><style>\n").Append(LoadPageAsset("htmlExport.css"))
              .Append(GraphThemeCss()).Append("</style></head><body>");
            sb.Append("<div id='app'>");
            sb.Append("<aside id='side'><h1>").Append(E(c.name)).Append("</h1>");
            sb.Append("<div id='tabs'><button class='tab sel' data-tab='layers'>Layers</button><button class='tab' data-tab='params'>Parameters</button></div>");
            sb.Append("<input id='search' type='search' placeholder='Search…' autocomplete='off'><div id='list'></div></aside>");
            sb.Append("<div class='rz' id='rz1'></div>");
            sb.Append("<section id='main'><div id='crumbs'></div>");
            sb.Append("<div id='viewwrap'><svg id='view' xmlns='http://www.w3.org/2000/svg'><defs>");
            AppendMarker(sb, "arrow", 1, "ah"); AppendMarker(sb, "arrowblue", 1, "ahblue"); AppendMarker(sb, "arroworange", 1, "ahorange"); AppendMarker(sb, "arrowsel", 1, "ahsel");
            AppendMarker(sb, "arrowdim", 1, "ahdim");                  // a single parent-owned transition
            AppendMarker(sb, "arrowtri", 3, "ah"); AppendMarker(sb, "arrowtrisel", 3, "ahsel");
            AppendMarker(sb, "arrowtridim", 3, "ahdim");               // every transition on the edge parent-owned
            AppendMarker(sb, "arrowtrimixed", 3, "ah", "ahdim");        // mixed: the middle chevron only
            sb.Append("<pattern id='grid' width='100' height='100' patternUnits='userSpaceOnUse'>");
            sb.Append("<path d='M20 0 L20 100 M40 0 L40 100 M60 0 L60 100 M80 0 L80 100 M0 20 L100 20 M0 40 L100 40 M0 60 L100 60 M0 80 L100 80' fill='none' class='gminor'/>");
            sb.Append("<path d='M100 0 L0 0 0 100' fill='none' class='gmajor'/></pattern></defs><g id='vp'></g></svg>");
            sb.Append("<button id='theme'>Light</button><button id='fit'>Fit</button>");
            sb.Append("<div id='animwin'><div id='animhandle'></div><div id='animbar'><span id='animtitle'>Animation</span><button id='animclose'>×</button></div><div id='animbody'></div></div>");
            sb.Append("</div></section>");
            sb.Append("<div class='rz' id='rz2'></div>");
            sb.Append("<aside id='detail'><p class='hint'>Pick a layer, then click a state or transition. Middle-mouse/drag to pan, wheel to zoom, F to fit, click empty space to deselect. Double-click a sub-state-machine or blend-tree state to drill in.</p></aside>");
            sb.Append("</div><div id='pop'></div>");
            sb.Append("<script>const DATA=").Append(json).Append(";\n").Append(LoadPageAsset("htmlExport.js")).Append("</script></body></html>");
            return sb.ToString();
        }

        // `heads` is 1, or 3 for a bundle - three, because that is the graph's own default and what the native
        // Animator draws. It used to be two, which said "several" without matching either.
        //
        // `midCls`, when given, colours the MIDDLE chevron separately: the mixed-bundle grey, exactly where the
        // canvas puts it at three heads. A marker can hold several paths, so this needs no change to how edges
        // are drawn - only another marker in the defs.
        private static void AppendMarker(StringBuilder sb, string id, int heads, string cls, string midCls = null)
        {
            // refX centered so the arrowhead sits ON the point it's placed at (the mid-line vertex).
            if (heads >= 3)
            {
                sb.Append("<marker id='").Append(id).Append("' markerWidth='16' markerHeight='7' refX='8' refY='3' orient='auto'>");
                sb.Append("<path d='M0,0 L5,3 L0,6 z' class='").Append(cls).Append("'/>");
                sb.Append("<path d='M5,0 L10,3 L5,6 z' class='").Append(midCls ?? cls).Append("'/>");
                sb.Append("<path d='M10,0 L15,3 L10,6 z' class='").Append(cls).Append("'/></marker>");
                return;
            }
            sb.Append("<marker id='").Append(id).Append("' markerWidth='6' markerHeight='6' refX='3' refY='3' orient='auto'><path d='M0,0 L6,3 L0,6 z' class='").Append(cls).Append("'/></marker>");
        }

        // --- clips --------------------------------------------------------------------------------------

        private static string GetOrAddClip(AnimationClip clip)
        {
            if (clip == null) return null;
            if (_clipIds.TryGetValue(clip, out var id)) return id;
            id = "c" + _clipIds.Count;
            _clipIds[clip] = id;
            var props = new List<PropData>();
            float maxT = 0f;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.keys == null) continue;
                var src = curve.keys;
                var keys = new KeyData[src.Length];
                for (int k = 0; k < src.Length; k++)
                {
                    float inT = src[k].inTangent, outT = src[k].outTangent;
                    keys[k] = new KeyData { t = src[k].time, v = src[k].value, inT = IsFinite(inT) ? inT : 0f, outT = IsFinite(outT) ? outT : 0f, co = !IsFinite(inT) || !IsFinite(outT) };
                    if (src[k].time > maxT) maxT = src[k].time;
                }
                props.Add(new PropData { path = binding.path, type = binding.type != null ? binding.type.Name : "", prop = binding.propertyName, keys = keys });
            }
            float fps = clip.frameRate <= 0 ? 60f : clip.frameRate;
            int frames = maxT <= 0f ? 1 : Mathf.RoundToInt(maxT * fps) + 1;
            _clips.Add(new ClipData { id = id, name = clip.name, length = maxT, fps = fps, frames = frames, loop = clip.isLooping, props = props.ToArray() });
            return id;
        }

        private static bool IsFinite(float v) => !float.IsInfinity(v) && !float.IsNaN(v);

        // --- state machine graph ------------------------------------------------------------------------

        // `layerMixed` is gone: the state "!" no longer depends on its layer's status - see the note where
        // wdWarn is computed, and RefreshWdWarnings on the canvas, which dropped the same gate.
        // `layerDefault` is the state the LAYER starts at, threaded down from the root so every nested graph can
        // ask the same question the canvas asks: is the start somewhere other than here? It is the only input to
        // the "(Up)" node's colour - see the note where that node is built.
        // `layerRoot` is the machine the LAYER starts from, threaded down the recursion so a nested graph can
        // ask what reaches into it from anywhere else in the layer (see the "(Up)" incoming pass). Omitted by
        // the top-level call, where the machine being built IS the layer root.
        // Whether any of this state's transitions target the layer root - the blue dot on the canvas.
        private static bool StateGoesToLayer(AnimatorState st, AnimatorStateMachine root)
        {
            if (st == null || root == null) return false;
            foreach (var t in st.transitions)
                if (t != null && t.destinationStateMachine == root) return true;
            return false;
        }

        // The same question for a sub-state machine: it wears the dot when anything NESTED in it leaves the
        // layer, which is what the canvas asks too - the machine stands in for everything it hides. `seen` is
        // the cycle guard a hand-edited controller can need.
        private static bool SubMachineGoesToLayer(AnimatorStateMachine sm, AnimatorStateMachine root, HashSet<AnimatorStateMachine> seen)
        {
            if (sm == null || root == null || !seen.Add(sm)) return false;
            foreach (var cs in sm.states)
                if (StateGoesToLayer(cs.state, root)) return true;
            foreach (var csm in sm.stateMachines)
                if (SubMachineGoesToLayer(csm.stateMachine, root, seen)) return true;
            return false;
        }

        private static string BuildSmGraph(AnimatorStateMachine sm, string title, Dictionary<AnimatorStateMachine, string> visited, AnimatorStateMachine parent = null, string parentName = null, AnimatorState layerDefault = null, AnimatorStateMachine layerRoot = null)
        {
            if (visited.TryGetValue(sm, out var existing)) return existing;
            string gid = "g" + (_graphSeq++);
            visited[sm] = gid;

            var nodes = new List<NodeData>();
            var edges = new List<EdgeData>();
            var stateId = new Dictionary<AnimatorState, string>();
            var smId = new Dictionary<AnimatorStateMachine, string>();

            const string ENTRY = "E", ANY = "A", EXIT = "X", UP = "U";
            nodes.Add(new NodeData { id = ENTRY, type = "entry", label = "Entry", x = sm.entryPosition.x, y = sm.entryPosition.y, w = SW, h = SH, details = new[] { "Entry point" } });
            var anyNode = new NodeData { id = ANY, type = "any", label = "Any State", x = sm.anyStatePosition.x, y = sm.anyStatePosition.y, w = SW, h = SH, details = new[] { "Any State" } };
            nodes.Add(anyNode);
            nodes.Add(new NodeData { id = EXIT, type = "exit", label = "Exit", x = sm.exitPosition.x, y = sm.exitPosition.y, w = SW, h = SH, details = new[] { "Exit point" } });
            // Inside a sub-state machine, add the "(Up)" node (the parent) so transitions leaving to the parent show.
            // "(Up) Parent", worded exactly as the live canvas does (see AnimatorStateMachineCanvas_Build, which
            // builds it as "(Up) " + GetUpNodeName). An arrow glyph read as a different kind of node entirely.
            //
            // isDefault on the Up node does NOT mean "this is the default state" - it means "the layer's default
            // state is not in here, so up is the way back to it", which is what native Unity colours it for. It
            // rides the existing `def` class because the answer is the same orange, and the canvas resolves it
            // the same way (Ath_DefaultState.Contains, recursive - a default buried in a CHILD of
            // this machine is still down, not up).
            //
            // Sized NW/NH, not SW/SH: it is a hexagon like any other sub-state machine, and the canvas builds it
            // as an ordinary full-size node (CanvasNode with no specialType). Entry/Any/Exit are the narrow ones.
            if (parent != null) nodes.Add(new NodeData { id = UP, type = "up", label = string.IsNullOrEmpty(parentName) ? "(Up)" : "(Up) " + parentName, x = sm.parentStateMachinePosition.x, y = sm.parentStateMachinePosition.y, w = NW, h = NH, isDefault = layerDefault != null && !Ath_DefaultState.Contains(sm, layerDefault), details = new[] { "Back to: " + (parentName ?? "parent") } });

            int n = 0;
            foreach (var cs in sm.states)
            {
                var st = cs.state; if (st == null) continue;
                string id = "n" + (n++);
                stateId[st] = id;
                var btree = st.motion as BlendTree;
                string child = btree != null ? BuildBtGraph(btree, st.name) : null;
                string clip = st.motion is AnimationClip ac ? GetOrAddClip(ac) : null;
                string atip = StateAttrTip(st);
                bool tagged = btree != null; // always tagged here - see the export policy at the top of the file
                // "Ex" when the state's OWN motion is stored outside this controller. Shallow on purpose, and
                // never "Exc" on a state: a state has no "Ex" above it to be the child of. The state is the
                // owner reference - it is a sub-asset of this controller, so its asset path is this one's.
                bool external = btree != null && Ath_MotionOwnership.IsForeignTree(st, btree);
                // The "!" the live node shows: a Write Defaults outlier, a broken tree ownership link, or both -
                // one mark, and when both apply the tooltip carries both texts with a blank line between.
                // Not gated on the layer being mixed, matching the live canvas: a layer that is uniformly the
                // controller-wide minority is not mixed, and gating hid every state in it.
                bool wdWarn = IsWdOutlier(st);
                bool linkWarn = btree != null && Ath_MotionOwnership.SubtreeHasBackwardsReference(btree);
                string wTip = wdWarn ? Ath.Tr("badge.wdDiffersTooltip") : null;
                string lTip = linkWarn ? Ath_BrokenLinkTag.StateTooltip : null;
                nodes.Add(new NodeData
                {
                    id = id, type = "state", label = st.name, x = cs.position.x, y = cs.position.y, w = NW, h = NH,
                    wd = st.writeDefaultValues, isDefault = layerDefault != null && layerDefault == st, none = st.motion == null,
                    attrs = atip.Length > 0, attrTip = atip,
                    tol = layerRoot != null && StateGoesToLayer(st, layerRoot),
                    motion = MotionShort(st.motion), child = child, clip = clip,
                    btag = tagged ? Ath_BlendTypeTag.Tag(btree.blendType) : null,
                    btagc = tagged ? "#" + ColorUtility.ToHtmlStringRGB(Ath_BlendTypeTag.DefaultColor(btree.blendType)) : null,
                    btagt = tagged ? Ath_BlendTypeTag.Tooltip(btree.blendType) : null,
                    ex = external ? Ath_ExternalTag.Text : null,
                    exc = external ? "#" + ColorUtility.ToHtmlStringRGB(Ath_ExternalTag.DefaultColor) : null,
                    ext = external ? Ath_ExternalTag.Tooltip : null,
                    warn = wdWarn || linkWarn,
                    warnc = "#" + ColorUtility.ToHtmlStringRGB(linkWarn
                        ? Ath_BrokenLinkTag.DefaultColor : new Color(0.98f, 0.35f, 0.35f)),
                    warnt = wTip != null && lTip != null ? wTip + "\n\n" + lTip : (wTip ?? lTip ?? ""),
                    details = StateDetails(st), behaviours = StateBehaviours(st)
                });
            }
            // The default state can live inside one of these sub-state machines rather than directly here; that
            // sub-state machine is then what Entry flows into, so it carries the default styling and the
            // default edge. Same rule as the canvas - see Ath_DefaultState.
            // Two different questions, and they part company the moment a sub-state machine is duplicated.
            //
            // The COLOUR marks where the LAYER starts - one node per layer, or none in a view that doesn't hold
            // it. The ENTRY arrow points at whatever THIS machine's own defaultState is, which Unity assigns to
            // every machine that has states and a deep clone copies, so it exists in every sub-state machine.
            // Asking the second question for the first drew an orange state inside every sub-machine, and a
            // duplicate of the one holding the real default looked like a second starting point.
            //
            // Same split as the canvas - see ApplyDefaultNodeFlag versus ResolveDefaultNode.
            var defaultOwner = Ath_DefaultState.ContainingChild(sm, layerDefault);
            var entryOwner = Ath_DefaultState.ContainingChild(sm, sm.defaultState);
            foreach (var csm in sm.stateMachines)
            {
                var sub = csm.stateMachine; if (sub == null) continue;
                string id = "s" + (n++);
                smId[sub] = id;
                string child = BuildSmGraph(sub, sub.name, visited, sm, title, layerDefault, layerRoot ?? sm);
                nodes.Add(new NodeData { id = id, type = "sm", label = sub.name, x = csm.position.x, y = csm.position.y, w = NW, h = NH, child = child, isDefault = sub == defaultOwner, details = new[] { "Sub-state machine" }, behaviours = Behaviours(sub.behaviours),
                                         tol = layerRoot != null && SubMachineGoesToLayer(sub, layerRoot, new HashSet<AnimatorStateMachine>()) });
            }

            // Which DIRECT child of `sm` a nested state or machine lives under. The canvas keeps the same index
            // (RebuildOwnerIndex) and every question below is one the canvas asks of it - the two have to agree
            // or the export stops matching the screen. Empty when this machine has no children, which is the
            // common case and costs a single loop that finds nothing.
            var stateOwner = new Dictionary<AnimatorState, AnimatorStateMachine>();
            var smOwner = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            {
                var seenOwn = new HashSet<AnimatorStateMachine>(); // doubles as the cycle guard
                void IndexUnder(AnimatorStateMachine node, AnimatorStateMachine owner)
                {
                    if (node == null || !seenOwn.Add(node)) return;
                    foreach (var cs in node.states) if (cs.state != null) stateOwner[cs.state] = owner;
                    foreach (var csm in node.stateMachines)
                    {
                        var child = csm.stateMachine;
                        if (child == null || seenOwn.Contains(child)) continue;
                        smOwner[child] = owner;
                        IndexUnder(child, owner);
                    }
                }
                foreach (var csm in sm.stateMachines)
                    if (csm.stateMachine != null) IndexUnder(csm.stateMachine, csm.stateMachine);
            }

            // Where an edge LANDS in this view. Mirrors the canvas's FindTargetNode step for step.
            //
            // The two nested cases and the outside case were all missing, and each one silently dropped the
            // edge rather than drawing it somewhere sensible: a transition aimed at a state INSIDE a sub-state
            // machine (which is what Unity's own target menu offers, and the common way to point at one) simply
            // did not appear in the export at all.
            string Resolve(AnimatorTransitionBase t)
            {
                if (t.isExit) return EXIT;
                if (t.destinationState != null)
                {
                    if (stateId.TryGetValue(t.destinationState, out var sid)) return sid;
                    // Nested inside one of our sub-machines: the edge lands on that machine, exactly as a
                    // transition aimed straight at it would.
                    if (stateOwner.TryGetValue(t.destinationState, out var owner) && smId.TryGetValue(owner, out var oid)) return oid;
                    return parent != null ? UP : null; // outside this view entirely - or nowhere, at the root
                }
                if (t.destinationStateMachine != null)
                {
                    if (parent != null && t.destinationStateMachine == parent) return UP; // transition to the parent = (Up)
                    if (smId.TryGetValue(t.destinationStateMachine, out var mid)) return mid;
                    if (smOwner.TryGetValue(t.destinationStateMachine, out var owner) && smId.TryGetValue(owner, out var oid)) return oid;
                    return parent != null ? UP : null;
                }
                return null;
            }

            // Is this transition's destination INSIDE `boundary` (so it never crosses out of it)? Same rule as
            // the canvas's IsDestinationInside, including exit transitions counting as internal - leaving a
            // sub-state machine via Exit continues inside its parent, so it is not a crossing.
            bool DestinationInside(AnimatorTransitionBase t, AnimatorStateMachine boundary)
            {
                if (t is AnimatorStateTransition ast && ast.isExit) return true;
                if (t.destinationState != null)
                    return stateOwner.TryGetValue(t.destinationState, out var o) && o == boundary;
                if (t.destinationStateMachine != null)
                    return t.destinationStateMachine == boundary
                        || (smOwner.TryGetValue(t.destinationStateMachine, out var o2) && o2 == boundary);
                return false;
            }
            // `fromPath` is the transition's REAL owner, which the drawn source node often is not - see the
            // endpoint helpers. It has to be passed in because a transition cannot be asked who owns it: it is
            // found by being in somebody's list, and only the caller walking that list knows whose.
            void AddEdges(string src, IEnumerable<AnimatorTransitionBase> ts, string kind, string fromPath)
            {
                if (ts == null) return;
                foreach (var t in ts) AddOneEdge(src, t, kind, fromPath);
            }

            void AddOneEdge(string src, AnimatorTransitionBase t, string kind, string fromPath)
            {
                if (t == null) return;
                string dst = Resolve(t); if (dst == null) return;
                var st = t as AnimatorStateTransition;
                edges.Add(new EdgeData
                {
                    fromId = src, toId = dst, kind = kind, summary = TransitionSummary(t),
                    srcPath = fromPath, dstPath = TransitionDestPath(t),
                    // Parent-owned = not an AnimatorStateTransition: Unity's "selector" kind, which is every
                    // Entry transition and every transition whose source is a sub-state machine. The blend-tree
                    // and implicit-default edges are built elsewhere and leave this false, which is right -
                    // neither is a transition the dim rule is about.
                    isStateTr = st != null, po = st == null, hasExit = st != null && st.hasExitTime,
                    exitTime = st != null ? F(st.exitTime) : "0", fixedDur = st != null && st.hasFixedDuration,
                    duration = st != null ? F(st.duration) : "0", offset = st != null ? F(st.offset) : "0",
                    interrupt = st != null ? st.interruptionSource.ToString() : "None", ordered = st != null && st.orderedInterruption,
                    solo = t.solo, mute = t.mute, conds = BuildConds(t),
                    // Leaves the layer, which is what the canvas puts its "-> Layer" tag on.
                    tol = layerRoot != null && t.destinationStateMachine == layerRoot,
                    // Same question the canvas asks, from the same rules (the static overload reads the table
                    // we already built for the parameter panel), but NOT behind the canvas's toggle - see the
                    // export policy at the top of the file.
                    warn = Ath.TransitionHasWarning(t, _ptypes),
                    warnTip = Ath.TransitionWarningTextFor(t, _ptypes)
                });
            }

            // Entry → default. THIS machine's own default state (entryOwner, not defaultOwner - see above): the
            // arrow describes what actually runs when flow enters here, which is true of a duplicate too.
            // The target is that state's own node when it's directly here, otherwise the sub-state machine that
            // contains it.
            string defId = null;
            if (sm.defaultState != null && !stateId.TryGetValue(sm.defaultState, out defId))
                defId = entryOwner != null && smId.TryGetValue(entryOwner, out var ownerId) ? ownerId : null;
            if (defId != null)
                edges.Add(new EdgeData { fromId = ENTRY, toId = defId, kind = "default", summary = "Default → " + sm.defaultState.name, isStateTr = false,
                    srcPath = EntryPathOf(sm), dstPath = PathOf(sm.defaultState), exitTime = "0", duration = "0", offset = "0", interrupt = "None", conds = new CondData[0] });

            AddEdges(ENTRY, sm.entryTransitions, "entry", EntryPathOf(sm));

            // Hidden-identifier markers (muted, named Any State → Exit transitions added by other tools) get an ID
            // badge on the Any State node and are kept as non-drawn "id" edges so they're still viewable/selectable.
            var idNames = new List<string>();
            var idTrans = new List<AnimatorTransitionBase>();
            var anyReal = new List<AnimatorTransitionBase>();
            if (sm.anyStateTransitions != null)
                foreach (var t in sm.anyStateTransitions)
                {
                    if (t == null) continue;
                    if (t.mute && t.isExit && !string.IsNullOrEmpty(t.name)) { idNames.Add(t.name); idTrans.Add(t); }
                    else anyReal.Add(t);
                }
            AddEdges(ANY, anyReal, "any", "Any State"); // layer-global, so it has no machine path
            foreach (var t in idTrans)
            {
                var st = t as AnimatorStateTransition;
                edges.Add(new EdgeData { fromId = ANY, toId = EXIT, kind = "id", summary = "[ID] " + t.name,
                    srcPath = "Any State", dstPath = "Exit",
                    isStateTr = st != null, hasExit = st != null && st.hasExitTime,
                    exitTime = st != null ? F(st.exitTime) : "0", fixedDur = st != null && st.hasFixedDuration,
                    duration = st != null ? F(st.duration) : "0", offset = st != null ? F(st.offset) : "0",
                    interrupt = st != null ? st.interruptionSource.ToString() : "None", ordered = st != null && st.orderedInterruption,
                    solo = t.solo, mute = t.mute, conds = BuildConds(t) });
            }
            if (idNames.Count > 0) { anyNode.idm = string.Join(", ", idNames); anyNode.details = new[] { "Any State", "Identifier(s): " + anyNode.idm }; }
            foreach (var cs in sm.states) if (cs.state != null) AddEdges(stateId[cs.state], cs.state.transitions, "state", PathOf(cs.state));
            // A child machine's own transitions are stored on THIS machine but owned by the child - the
            // usual storage asymmetry - so the child is what the edge comes from.
            foreach (var csm in sm.stateMachines) if (csm.stateMachine != null) AddEdges(smId[csm.stateMachine], sm.GetStateMachineTransitions(csm.stateMachine), "state", PathOf(csm.stateMachine));

            // Transitions belonging to things NESTED inside a sub-state machine that leave it, drawn coming out
            // of that machine's node - the canvas's GatherSubMachineOutgoing. Without this a sub-state machine
            // exported as a node with no outgoing edges unless the machine itself owned one, while on screen it
            // visibly had them: a state buried three levels down reaching outside is still an arrow leaving the
            // machine at this level.
            void AddOutgoingFromInside(AnimatorStateMachine scope, AnimatorStateMachine boundary, string srcId,
                                       HashSet<AnimatorStateMachine> seen)
            {
                if (scope == null || !seen.Add(scope)) return;

                // Filtered by hand rather than with Linq: this file deliberately imports none of it.
                // Refilled and handed straight to AddEdges, which consumes it before returning.
                var crossing = new List<AnimatorTransitionBase>();

                foreach (var cs in scope.states)
                {
                    if (cs.state == null) continue;
                    crossing.Clear();
                    foreach (var t in cs.state.transitions)
                        if (t != null && !DestinationInside(t, boundary)) crossing.Add(t);
                    if (crossing.Count > 0) AddEdges(srcId, crossing, "state", PathOf(cs.state));
                }

                foreach (var csm in scope.stateMachines)
                {
                    var child = csm.stateMachine;
                    if (child == null) continue;
                    // A nested machine's own transitions live on its PARENT, not on it - the usual storage
                    // asymmetry. isExit is skipped explicitly: leaving `child` continues inside `boundary`, and
                    // these are plain AnimatorTransitions, which DestinationInside cannot recognise as exits.
                    var smTrans = scope.GetStateMachineTransitions(child);
                    if (smTrans != null)
                    {
                        crossing.Clear();
                        foreach (var t in smTrans)
                            if (t != null && !t.isExit && !DestinationInside(t, boundary)) crossing.Add(t);
                        if (crossing.Count > 0) AddEdges(srcId, crossing, "state", PathOf(child));
                    }
                    AddOutgoingFromInside(child, boundary, srcId, seen);
                }
            }
            foreach (var csm in sm.stateMachines)
            {
                var child = csm.stateMachine;
                if (child == null) continue;
                AddOutgoingFromInside(child, child, smId[child], new HashSet<AnimatorStateMachine>());
            }

            // Everything ELSEWHERE in the layer that reaches into this view, drawn arriving from the "(Up)"
            // node - the canvas's GatherIncomingFromOutside. Only inside a sub-state machine: the layer root
            // has no Up node, and nothing is outside it.
            //
            // Without this a nested graph looked like nothing entered it but Entry, which is the one thing a
            // reader drilling into a sub-state machine most wants to know.
            if (parent != null)
            {
                var seenIn = new HashSet<AnimatorStateMachine>();

                // Emitted one at a time rather than collected, because every one of these has a DIFFERENT
                // owner - that is the whole point of the pass - and only the walk below knows whose list each
                // was found in.
                void ConsiderIncoming(AnimatorTransitionBase t, string fromPath)
                {
                    if (t == null) return;
                    // An exit transition, or a destination-less marker, arrives nowhere in this view. Tested
                    // before Resolve, which maps isExit to the Exit node - that would draw "(Up) → Exit" for
                    // every exiting state in the layer.
                    if (t.destinationState == null && t.destinationStateMachine == null) return;
                    string dest = Resolve(t);
                    // Null means it is not in this view at all; UP means the destination is outside too, so the
                    // transition merely passes us by. Neither is an incoming edge.
                    if (dest == null || dest == UP) return;
                    AddOneEdge(UP, t, "state", fromPath);
                }

                // Walks the whole layer, skipping the subtree we are INSIDE - its transitions leave this view
                // rather than arrive in it, and they are already drawn from their own nodes here.
                void WalkOutside(AnimatorStateMachine scope, bool insideCurrent)
                {
                    if (scope == null || !seenIn.Add(scope)) return;

                    if (!insideCurrent)
                    {
                        foreach (var cs in scope.states)
                        {
                            if (cs.state == null) continue;
                            foreach (var t in cs.state.transitions) ConsiderIncoming(t, PathOf(cs.state));
                        }
                        foreach (var t in scope.entryTransitions) ConsiderIncoming(t, EntryPathOf(scope));
                        foreach (var csm in scope.stateMachines)
                        {
                            // Skip the machine we are inside: scope.GetStateMachineTransitions(sm) holds THIS
                            // view's own outgoing transitions, which leave rather than arrive.
                            if (csm.stateMachine == null || csm.stateMachine == sm) continue;
                            var smTrans = scope.GetStateMachineTransitions(csm.stateMachine);
                            if (smTrans != null) foreach (var t in smTrans) ConsiderIncoming(t, PathOf(csm.stateMachine));
                        }
                        // Any State transitions are layer-global and drawn from this view's own Any State node,
                        // so they are deliberately not gathered here - they would be drawn twice.
                    }

                    foreach (var csm in scope.stateMachines)
                        if (csm.stateMachine != null) WalkOutside(csm.stateMachine, insideCurrent || csm.stateMachine == sm);
                }

                WalkOutside(layerRoot ?? sm, false);
            }

            _graphs.Add(new GraphData { id = gid, kind = "sm", title = title, nodes = nodes.ToArray(), edges = edges.ToArray() });
            return gid;
        }

        // Controller-wide Write Defaults counts, for the per-state outlier marker. The window answers this from
        // a cache on itself (Ath.IsOffendingWdState); the exporter is static and has no window, so
        // the same rule is recomputed here - once per export - rather than left out of the page.
        private static int _wdAllOn, _wdAllOff;
        private static bool _wdAllExempt;

        private static void BuildWdStats(AnimatorController c)
        {
            _wdAllOn = _wdAllOff = 0;
            // Always exempt here, whatever the setting says. Direct Blend Trees having to stay Write Defaults ON
            // is a VRChat fact rather than a preference, and this export exists to be read by someone debugging a
            // VRChat controller - flagging every DBT as an outlier would bury the outliers that matter.
            _wdAllExempt = true;
            if (c == null) return;
            var states = new HashSet<AnimatorState>();
            foreach (var l in c.layers) Gather(l.stateMachine, new HashSet<AnimatorStateMachine>(), states);
            foreach (var st in states) { if (st.writeDefaultValues) _wdAllOn++; else _wdAllOff++; }
        }

        // Same three tests as IsOffendingWdState: there has to BE a minority, the state has to be on it, and an
        // exempt WD-on Direct Blend Tree is never flagged.
        private static bool IsWdOutlier(AnimatorState st)
        {
            if (st == null) return false;
            if (_wdAllOn == 0 || _wdAllOff == 0 || _wdAllOn == _wdAllOff) return false;
            bool offendingIsOn = _wdAllOn < _wdAllOff;
            bool wd = st.writeDefaultValues;
            if (wd != offendingIsOn) return false;
            if (_wdAllExempt && wd && MotionHasDirect(st.motion)) return false;
            return true;
        }

        private static void LayerAnalyze(AnimatorStateMachine sm, out string wd, out bool none)
        {
            var states = new HashSet<AnimatorState>();
            Gather(sm, new HashSet<AnimatorStateMachine>(), states);
            int on = 0, off = 0; bool anyNonDbtOn = false, hasNone = false;
            foreach (var st in states)
            {
                if (st.motion == null) hasNone = true;
                if (st.writeDefaultValues) { on++; if (!MotionHasDirect(st.motion)) anyNonDbtOn = true; }
                else off++;
            }
            none = hasNone;
            // Mirrors GetLayerWdStatus with Direct Blend Trees exempt: badge only for all-on ("on") or a genuine mix.
            wd = on == 0 ? "" : off == 0 ? "on" : !anyNonDbtOn ? "" : "mixed";
        }

        private static string JoinTip(string acc, string line)
            => string.IsNullOrEmpty(acc) ? line : acc + "\n\n" + line;

        // Does any state anywhere in the layer disagree with the CONTROLLER's Write Defaults majority? The
        // export's answer to SubMachineHasOffendingWdState, asked through IsWdOutlier so it carries the same
        // Direct Blend Tree exemption the rest of this file uses.
        //
        // Distinct from LayerAnalyze's "mixed": that one asks whether the layer disagrees with itself. A layer
        // can be perfectly uniform and still be entirely wrong for the controller it sits in.
        private static bool LayerHasWdOutlier(AnimatorStateMachine sm, HashSet<AnimatorStateMachine> visited)
        {
            if (sm == null || !visited.Add(sm)) return false;

            foreach (var cs in sm.states)
                if (cs.state != null && IsWdOutlier(cs.state)) return true;

            foreach (var csm in sm.stateMachines)
                if (LayerHasWdOutlier(csm.stateMachine, visited)) return true;

            return false;
        }

        // Does any transition anywhere in the layer read as broken? Mirrors the window's LayerHasBrokenTransition
        // and asks through the shared static rules, so a layer flagged in the list is flagged in an export.
        // Uncached on purpose - this runs once per layer during a one-shot export, not per row per repaint.
        private static bool LayerHasBrokenTransition(AnimatorStateMachine sm, HashSet<AnimatorStateMachine> visited)
        {
            if (sm == null || !visited.Add(sm)) return false;

            foreach (var t in sm.anyStateTransitions)
                if (Ath.TransitionHasWarning(t, _ptypes)) return true;
            foreach (var t in sm.entryTransitions)
                if (Ath.TransitionHasWarning(t, _ptypes)) return true;

            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                foreach (var t in cs.state.transitions)
                    if (Ath.TransitionHasWarning(t, _ptypes)) return true;
            }

            foreach (var csm in sm.stateMachines)
            {
                var child = csm.stateMachine;
                if (child == null) continue;
                foreach (var t in sm.GetStateMachineTransitions(child))
                    if (Ath.TransitionHasWarning(t, _ptypes)) return true;
                if (LayerHasBrokenTransition(child, visited)) return true;
            }
            return false;
        }

        private static void Gather(AnimatorStateMachine sm, HashSet<AnimatorStateMachine> vis, HashSet<AnimatorState> into)
        {
            if (sm == null || !vis.Add(sm)) return;
            foreach (var cs in sm.states) if (cs.state != null) into.Add(cs.state);
            foreach (var csm in sm.stateMachines) Gather(csm.stateMachine, vis, into);
        }

        private static bool HasIdentifier(AnimatorStateMachine sm, HashSet<AnimatorStateMachine> vis)
        {
            if (sm == null || !vis.Add(sm)) return false;
            if (sm.anyStateTransitions != null) foreach (var t in sm.anyStateTransitions) if (t != null && t.mute && t.isExit && !string.IsNullOrEmpty(t.name)) return true;
            foreach (var csm in sm.stateMachines) if (HasIdentifier(csm.stateMachine, vis)) return true;
            return false;
        }

        // `vis` guards a CYCLIC tree graph, the same way AddMotionParams below does - an unguarded walk dies on
        // a StackOverflowException that .NET cannot catch, taking the Editor down mid-export. See
        // BlendTreeCanvas.WouldCreateCycle: it refuses to create a cycle but cannot fix one that already exists.
        private static bool MotionHasDirect(Motion m, HashSet<BlendTree> vis = null)
        {
            if (!(m is BlendTree bt)) return false;
            vis = vis ?? new HashSet<BlendTree>();
            if (!vis.Add(bt)) return false;
            if (bt.blendType == BlendTreeType.Direct) return true;
            if (bt.children != null) foreach (var ch in bt.children) if (MotionHasDirect(ch.motion, vis)) return true;
            return false;
        }

        // --- used-parameter collection (scoped export) --------------------------------------------------

        private static void CollectUsedParameters(AnimatorStateMachine sm, HashSet<string> used, HashSet<AnimatorStateMachine> vis)
        {
            if (sm == null || !vis.Add(sm)) return;
            AddTransitionParams(sm.anyStateTransitions, used);
            AddTransitionParams(sm.entryTransitions, used);
            AddBehaviourParams(sm.behaviours, used);
            foreach (var cs in sm.states)
            {
                var st = cs.state; if (st == null) continue;
                AddTransitionParams(st.transitions, used);
                if (st.speedParameterActive) AddParam(used, st.speedParameter);
                if (st.timeParameterActive) AddParam(used, st.timeParameter);
                if (st.cycleOffsetParameterActive) AddParam(used, st.cycleOffsetParameter);
                if (st.mirrorParameterActive) AddParam(used, st.mirrorParameter);
                AddMotionParams(st.motion, used, new HashSet<BlendTree>());
                AddBehaviourParams(st.behaviours, used);
            }
            foreach (var csm in sm.stateMachines)
            {
                if (csm.stateMachine == null) continue;
                AddTransitionParams(sm.GetStateMachineTransitions(csm.stateMachine), used);
                CollectUsedParameters(csm.stateMachine, used, vis);
            }
        }

        private static void AddParam(HashSet<string> used, string p) { if (!string.IsNullOrEmpty(p)) used.Add(p); }

        private static void AddTransitionParams(IEnumerable<AnimatorTransitionBase> ts, HashSet<string> used)
        {
            if (ts == null) return;
            foreach (var t in ts)
            {
                if (t?.conditions == null) continue;
                foreach (var cnd in t.conditions) AddParam(used, cnd.parameter);
            }
        }

        private static void AddMotionParams(Motion m, HashSet<string> used, HashSet<BlendTree> vis)
        {
            if (m is BlendTree bt)
            {
                if (!vis.Add(bt)) return;
                if (bt.blendType != BlendTreeType.Direct)
                {
                    AddParam(used, bt.blendParameter);
                    if (bt.blendType != BlendTreeType.Simple1D) AddParam(used, bt.blendParameterY);
                }
                if (bt.children != null)
                    foreach (var ch in bt.children)
                    {
                        if (bt.blendType == BlendTreeType.Direct) AddParam(used, ch.directBlendParameter);
                        AddMotionParams(ch.motion, used, vis);
                    }
            }
            else if (m is AnimationClip clip)
            {
                // Animated Animator Parameters: curve bindings on the Animator itself (empty path) drive a parameter.
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    if (b.type == typeof(Animator) && string.IsNullOrEmpty(b.path)) AddParam(used, b.propertyName);
            }
        }

        // Behaviours reference parameters by storing the parameter name as a plain string (Parameter Driver's
        // name/source, Play Audio's parameter, Contact/Physbone behaviours, etc.). There's no generic marker that
        // says "this string field is a parameter", so we walk every string field of the behaviour and keep the
        // value only when it matches an actual controller parameter. Over-inclusion is harmless; missing one isn't.
        // This works for any behaviour type without hardcoding VRC SDK classes (keeps the zero-dependency goal).
        private static void AddBehaviourParams(StateMachineBehaviour[] behs, HashSet<string> used)
        {
            if (behs == null) return;
            foreach (var b in behs)
            {
                if (b == null) continue;
                try
                {
                    using (var so = new SerializedObject(b))
                    {
                        var p = so.GetIterator();
                        if (!p.NextVisible(true)) continue;
                        do
                        {
                            if (p.name == "m_Script") continue;
                            if (p.propertyType == SerializedPropertyType.String)
                            {
                                string v = p.stringValue;
                                if (!string.IsNullOrEmpty(v) && _ptypes.ContainsKey(v)) used.Add(v);
                            }
                        } while (p.NextVisible(true)); // descends into arrays/structs (e.g. the driver's parameter list)
                    }
                }
                catch { }
            }
        }

        private static CondData[] BuildConds(AnimatorTransitionBase t)
        {
            var list = new List<CondData>();
            if (t.conditions != null)
                foreach (var c in t.conditions)
                {
                    bool known = _ptypes.TryGetValue(c.parameter, out var pt);
                    bool isBool = (known && (pt == AnimatorControllerParameterType.Bool || pt == AnimatorControllerParameterType.Trigger))
                        || (!known && (c.mode == AnimatorConditionMode.If || c.mode == AnimatorConditionMode.IfNot));
                    string op = "", val = ""; bool bv = false;
                    if (isBool) { bv = c.mode == AnimatorConditionMode.If; op = "="; }
                    else { switch (c.mode) { case AnimatorConditionMode.Greater: op = ">"; break; case AnimatorConditionMode.Less: op = "<"; break; case AnimatorConditionMode.Equals: op = "=="; break; case AnimatorConditionMode.NotEqual: op = "!="; break; } val = F(c.threshold); }
                    list.Add(new CondData { param = c.parameter, op = op, value = val, isBool = isBool, boolVal = bv });
                }
            return list.ToArray();
        }

        private static string[] StateDetails(AnimatorState st)
        {
            var d = new List<string> { "Motion: " + MotionShort(st.motion), "Write Defaults: " + (st.writeDefaultValues ? "On" : "Off") };
            if (st.speedParameterActive && !string.IsNullOrEmpty(st.speedParameter)) d.Add("Speed ×= " + st.speedParameter);
            else if (Math.Abs(st.speed - 1f) > 0.0001f) d.Add("Speed: " + F(st.speed));
            if (st.timeParameterActive && !string.IsNullOrEmpty(st.timeParameter)) d.Add("Motion Time = " + st.timeParameter);
            if (st.cycleOffsetParameterActive && !string.IsNullOrEmpty(st.cycleOffsetParameter)) d.Add("Cycle Offset = " + st.cycleOffsetParameter);
            if (st.mirrorParameterActive && !string.IsNullOrEmpty(st.mirrorParameter)) d.Add("Mirror = " + st.mirrorParameter);
            return d.ToArray();
        }

        private static string StateAttrTip(AnimatorState st)
        {
            var l = new List<string>();
            if (st.speedParameterActive && !string.IsNullOrEmpty(st.speedParameter)) l.Add("Speed Multiplier: " + st.speedParameter);
            if (st.timeParameterActive && !string.IsNullOrEmpty(st.timeParameter)) l.Add("Motion Time: " + st.timeParameter);
            if (st.cycleOffsetParameterActive && !string.IsNullOrEmpty(st.cycleOffsetParameter)) l.Add("Cycle Offset: " + st.cycleOffsetParameter);
            if (st.mirrorParameterActive && !string.IsNullOrEmpty(st.mirrorParameter)) l.Add("Mirror: " + st.mirrorParameter);
            return string.Join(", ", l);
        }

        private static BehData[] StateBehaviours(AnimatorState st) => Behaviours(st.behaviours);

        // Taken as the array rather than the owner, because a sub-state machine carries the same
        // StateMachineBehaviour list a state does - same dump, same panel, same "B" badge.
        private static BehData[] Behaviours(StateMachineBehaviour[] behaviours)
        {
            var list = new List<BehData>();
            if (behaviours != null) foreach (var b in behaviours) if (b != null) list.Add(DumpBehaviour(b));
            return list.ToArray();
        }

        // Behaviour → inspector-style "Key|Value" fields (empty entry = section divider).
        private static BehData DumpBehaviour(StateMachineBehaviour b)
        {
            string tn = b.GetType().Name;
            var f = new List<string>();
            try { using (var so = new SerializedObject(b)) { if (tn == "VRCAvatarParameterDriver") DriverFields(so, f); else GenericFields(so, f); } }
            catch { }
            return new BehData { name = tn, fields = f.ToArray() };
        }

        private static void DriverFields(SerializedObject so, List<string> f)
        {
            var local = so.FindProperty("localOnly"); f.Add("Local Only|" + (local != null && local.boolValue ? "true" : "false"));
            var dbg = so.FindProperty("debugString"); if (dbg != null && !string.IsNullOrEmpty(dbg.stringValue)) f.Add("Debug String|" + dbg.stringValue);
            var ps = so.FindProperty("parameters"); if (ps == null || !ps.isArray) return;
            string[] tnames = { "Set", "Add", "Random", "Copy" };
            for (int i = 0; i < ps.arraySize; i++)
            {
                var el = ps.GetArrayElementAtIndex(i);
                f.Add(""); // divider between operations
                int type = RelInt(el, "type"); string name = Rel(el, "name");
                f.Add("Type|" + (type >= 0 && type < 4 ? tnames[type] : type.ToString()));
                f.Add("Destination|" + name);
                bool db = _ptypes.TryGetValue(name, out var pt) && (pt == AnimatorControllerParameterType.Bool || pt == AnimatorControllerParameterType.Trigger);
                switch (type)
                {
                    case 2:
                        f.Add("Min Value|" + Rel(el, "valueMin")); f.Add("Max Value|" + Rel(el, "valueMax"));
                        var chance = el.FindPropertyRelative("chance"); if (chance != null) f.Add("Chance|" + F(chance.floatValue));
                        break;
                    case 3:
                        f.Add("Source|" + Rel(el, "source")); if (RelBool(el, "convertRange")) f.Add("Convert Range|true");
                        break;
                    default:
                        var vp = el.FindPropertyRelative("value");
                        f.Add("Value|" + (vp == null ? "" : (db ? (vp.floatValue != 0 ? "true" : "false") : F(vp.floatValue))));
                        break;
                }
            }
        }

        private static void GenericFields(SerializedObject so, List<string> f)
        {
            var p = so.GetIterator();
            if (!p.NextVisible(true)) return;
            do
            {
                if (p.depth != 0 || p.name == "m_Script") continue;
                if (p.isArray && p.propertyType != SerializedPropertyType.String)
                {
                    f.Add(p.displayName + "|" + p.arraySize + " item(s)");
                    int cnt = Math.Min(p.arraySize, 24);
                    for (int i = 0; i < cnt; i++) { var el = p.GetArrayElementAtIndex(i); string s = Scalar(el) ?? Summarize(el); f.Add("[" + i + "]|" + (string.IsNullOrEmpty(s) ? "…" : s)); }
                }
                else { string v = Scalar(p); if (v != null) f.Add(p.displayName + "|" + v); else { string s = Summarize(p); if (!string.IsNullOrEmpty(s)) f.Add(p.displayName + "|" + s); } }
            } while (p.NextVisible(false));
        }

        private static string Summarize(SerializedProperty prop)
        {
            var parts = new List<string>(); var end = prop.GetEndProperty(); var p = prop.Copy(); bool enter = true;
            while (p.NextVisible(enter)) { enter = false; if (SerializedProperty.EqualContents(p, end)) break; string v = Scalar(p); if (v != null) parts.Add(p.name + "=" + v); if (parts.Count >= 8) break; }
            return string.Join(", ", parts);
        }

        private static string Scalar(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Integer: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float: return F(p.floatValue);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return (p.enumValueIndex >= 0 && p.enumDisplayNames != null && p.enumValueIndex < p.enumDisplayNames.Length) ? p.enumDisplayNames[p.enumValueIndex] : p.intValue.ToString();
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue != null ? p.objectReferenceValue.name : "None";
                case SerializedPropertyType.Vector2: return p.vector2Value.x + " - " + p.vector2Value.y;
                case SerializedPropertyType.Vector3: return p.vector3Value.ToString();
                default: return null;
            }
        }

        private static string Rel(SerializedProperty el, string n) { var p = el.FindPropertyRelative(n); return p == null ? "" : (Scalar(p) ?? ""); }
        private static int RelInt(SerializedProperty el, string n) { var p = el.FindPropertyRelative(n); return p == null ? 0 : (p.propertyType == SerializedPropertyType.Enum ? p.enumValueIndex : p.intValue); }
        private static bool RelBool(SerializedProperty el, string n) { var p = el.FindPropertyRelative(n); return p != null && p.boolValue; }

        // Uses the full destination PATH, not the bare name: this is the edge tooltip and the label on the
        // transition picker, and "Idle" alone does not say which machine's Idle when the edge lands on a
        // sub-state machine holding several.
        private static string TransitionSummary(AnimatorTransitionBase t) { int c = t.conditions?.Length ?? 0; return TransitionDestPath(t) + (c > 0 ? "  [" + c + " cond]" : ""); }

        // --- blend tree graph ---------------------------------------------------------------------------

        private static string BuildBtGraph(BlendTree bt, string title)
        {
            string gid = "g" + (_graphSeq++);
            var nodes = new List<NodeData>(); var edges = new List<EdgeData>();
            int idc = 0, leaf = 0;
            AddBtNode(bt, null, null, 0, nodes, edges, ref idc, ref leaf, new HashSet<BlendTree>());
            _graphs.Add(new GraphData { id = gid, kind = "bt", title = "Blend Tree - " + title, nodes = nodes.ToArray(), edges = edges.ToArray() });
            return gid;
        }

        private const float BtColW = 390f, BtRowH = 64f;

        // `parentTree` is the tree this motion hangs off, needed only to spot a broken ownership link - the
        // fault lives on the EDGE between a parent file and a child inside the controller, so a node can't
        // tell on its own. Null at the root, whose parent is a state and therefore never a fault.
        private static (string id, float y) AddBtNode(Motion m, string parentId, string edgeLabel, int depth, List<NodeData> nodes, List<EdgeData> edges, ref int idc, ref int leaf, HashSet<BlendTree> visited, string dpFromParent = null, BlendTree parentTree = null)
        {
            string id = "b" + (idc++);
            float y;
            if (m is BlendTree bt && visited.Add(bt))
            {
                var childYs = new List<float>();
                var kids = new List<ChildW>();
                // Distinct parameters shown on a Direct card: children's direct params + everything the whole
                // subtree outputs (blend/direct params, cascading through descendant trees). Mirrors the JS rows.
                var seen = new HashSet<string>();
                if (!string.IsNullOrEmpty(dpFromParent)) seen.Add(dpFromParent); // the input row already shows it
                int prows = 0;
                void AddP(string p) { if (!string.IsNullOrEmpty(p) && seen.Add(p)) prows++; }
                void Collect(string cid) // walk the rendered subtree (cyclic re-occurrences are plain nodes with no bt info, so this terminates)
                {
                    var nd = nodes.Find(n2 => n2.id == cid);
                    if (nd == null || nd.bt == null || string.IsNullOrEmpty(nd.bt.mode)) return;
                    if (nd.bt.mode == "1d") AddP(nd.bt.px);
                    else if (nd.bt.mode == "2d") { AddP(nd.bt.px); AddP(nd.bt.py); }
                    foreach (var c in nd.bt.children) { if (nd.bt.mode == "direct") AddP(c.dp); Collect(c.cid); }
                }
                if (bt.children != null)
                    foreach (var ch in bt.children)
                    {
                        string lbl = bt.blendType == BlendTreeType.Direct ? (string.IsNullOrEmpty(ch.directBlendParameter) ? "" : ch.directBlendParameter)
                            : bt.blendType == BlendTreeType.Simple1D ? F(ch.threshold) : "(" + F(ch.position.x) + ", " + F(ch.position.y) + ")";
                        var r = AddBtNode(ch.motion, id, lbl, depth + 1, nodes, edges, ref idc, ref leaf, visited,
                            bt.blendType == BlendTreeType.Direct ? (ch.directBlendParameter ?? "") : null, bt);
                        childYs.Add(r.y);
                        kids.Add(new ChildW { cid = r.id, th = ch.threshold, x = ch.position.x, y = ch.position.y, dp = ch.directBlendParameter,
                            name = ch.motion == null ? "(none)" : (ch.motion is BlendTree bb ? (string.IsNullOrEmpty(bb.name) ? "Blend Tree" : bb.name) : ch.motion.name) });
                    }
                if (bt.blendType == BlendTreeType.Direct)
                    foreach (var k in kids) { AddP(k.dp); Collect(k.cid); }
                y = childYs.Count > 0 ? Avg(childYs) : (leaf++ * BtRowH);
                string type = bt.blendType == BlendTreeType.Direct ? "btdirect" : bt.blendType == BlendTreeType.Simple1D ? "bt1d" : "bt2d";
                string[] lines = BtLines(bt);
                // Rows - Direct: one input Weight row (root master, unless a 'Weight' param row already covers it)
                // + the deduped parameter rows; 1D: just its blend parameter; 2D: two.
                int inputRows = parentId == null ? (seen.Contains("Weight") ? 0 : 1) : 1;
                int rowCount = bt.blendType == BlendTreeType.Direct ? inputRows + prows : bt.blendType == BlendTreeType.Simple1D ? 1 : 2;
                float h = 28f + rowCount * 22f;
                var info = new BtInfo { mode = bt.blendType == BlendTreeType.Direct ? "direct" : bt.blendType == BlendTreeType.Simple1D ? "1d" : "2d", px = bt.blendParameter, py = bt.blendParameterY, children = kids.ToArray() };
                bool tagged = true; // always tagged here - see the export policy at the top of the file
                // The corner markers, as the live node shows them: "!" for a broken ownership link and "Ex"/"Exc"
                // for where the tree lives, BOTH when both apply. They are not mutually exclusive - a tree that is
                // a sub-asset of another controller is foreign AND the target of a backwards reference, which is
                // exactly the case that most needs to say so. This used to pick one and drop the other, so those
                // trees showed "!" alone here while the canvas showed both.
                bool btBroken = Ath_MotionOwnership.IsBackwardsReference(parentTree, bt);
                var ownTag = Ath_ExternalTag.ResolveTag(_ctrl, parentTree, bt, canonical: true);
                bool external = ownTag.Show;
                nodes.Add(new NodeData { id = id, type = type, label = string.IsNullOrEmpty(bt.name) ? "Blend Tree" : bt.name, x = depth * BtColW, y = y, w = 300, h = h, lines = lines, details = BtDetails(bt), bt = info,
                    btag = tagged ? Ath_BlendTypeTag.Tag(bt.blendType) : null,
                    btagc = tagged ? "#" + ColorUtility.ToHtmlStringRGB(Ath_BlendTypeTag.DefaultColor(bt.blendType)) : null,
                    btagt = tagged ? Ath_BlendTypeTag.Tooltip(bt.blendType) : null,
                    ex = external ? ownTag.Text : null,
                    exc = external ? "#" + ColorUtility.ToHtmlStringRGB(ownTag.Color) : null,
                    ext = external ? ownTag.Tooltip : null,
                    // Reuses the state node's warning fields rather than adding a second set - same meaning, same
                    // mark, and blend tree nodes had no use for them.
                    warn = btBroken,
                    warnc = "#" + ColorUtility.ToHtmlStringRGB(Ath_BrokenLinkTag.DefaultColor),
                    warnt = btBroken ? Ath_BrokenLinkTag.Tooltip : "" });
            }
            else
            {
                y = leaf++ * BtRowH;
                string clip = m is AnimationClip ac ? GetOrAddClip(ac) : null;
                string label = m == null ? "(none)" : (m is BlendTree ? "Blend Tree (cyclic)" : m.name);
                // `aap` so the card can carry the same badge the live motion node does. Looping needs no field -
                // ClipData already reports it, and the state nodes' loop glyph already reads it from there.
                nodes.Add(new NodeData { id = id, type = "clip", label = label, x = depth * BtColW, y = y, w = 175, h = 42, clip = clip,
                    aap = m is AnimationClip aapClip && Ath.ClipHasAapBinding(aapClip),
                    details = new[] { m == null ? "No motion" : "Clip: " + m.name } });
            }
            if (parentId != null) edges.Add(new EdgeData { fromId = parentId, toId = id, kind = "bt", label = edgeLabel ?? "", summary = edgeLabel ?? "", conds = new CondData[0] });
            return (id, y);
        }

        private static string[] BtLines(BlendTree bt)
        {
            if (bt.blendType == BlendTreeType.Direct)
            {
                var list = new List<string>(); var seen = new HashSet<string>();
                if (bt.children != null) foreach (var ch in bt.children) if (!string.IsNullOrEmpty(ch.directBlendParameter) && seen.Add(ch.directBlendParameter)) list.Add(ch.directBlendParameter);
                if (list.Count == 0) list.Add("Direct (no params)");
                return list.ToArray();
            }
            if (bt.blendType == BlendTreeType.Simple1D) return new[] { "1D: " + bt.blendParameter };
            // Every 2D variant (Simple/Freeform Directional, Freeform Cartesian) uses two parameters.
            return new[] { "X: " + bt.blendParameter, "Y: " + bt.blendParameterY };
        }

        private static string[] BtDetails(BlendTree bt)
        {
            var d = new List<string> { "Type: " + bt.blendType };
            if (bt.blendType != BlendTreeType.Direct) { if (!string.IsNullOrEmpty(bt.blendParameter)) d.Add("X: " + bt.blendParameter); if (bt.blendType != BlendTreeType.Simple1D && !string.IsNullOrEmpty(bt.blendParameterY)) d.Add("Y: " + bt.blendParameterY); }
            else if (bt.children != null) foreach (var ch in bt.children) if (!string.IsNullOrEmpty(ch.directBlendParameter)) d.Add("• " + ch.directBlendParameter);
            d.Add((bt.children?.Length ?? 0) + " children");
            return d.ToArray();
        }

        // --- real transition endpoints -------------------------------------------------------------------
        //
        // A drawn edge runs between NODES, and a node is often not the thing the transition actually names: a
        // transition into a state three machines down is drawn to the sub-state machine that contains it, and
        // one arriving from elsewhere in the layer is drawn from the "(Up)" node. Both are the right pictures -
        // that IS where the arrow belongs at this level - but they left the detail panel describing the picture
        // instead of the transition, so selecting an edge in a machine that merely sits between the two ends
        // told you nothing about either end.
        //
        // So every edge also carries the real pair, as a path from the layer root: "Gestures / Left / Point".
        // More than Unity's own inspector shows, which gives the bare names - and worth it, because the whole
        // problem here is that a bare name doesn't say WHICH machine it is in.

        // Object -> path, for one layer at a time. Static like the rest of this exporter's scratch state, and
        // cleared per layer, because the same names in another layer would be a different place.
        private static readonly Dictionary<UnityEngine.Object, string> _nodePath = new Dictionary<UnityEngine.Object, string>();

        private static void BuildPathIndex(AnimatorStateMachine root)
        {
            _nodePath.Clear();
            if (root == null) return;

            var seen = new HashSet<AnimatorStateMachine>(); // also the cycle guard
            void Walk(AnimatorStateMachine sm, string prefix)
            {
                if (sm == null || !seen.Add(sm)) return;
                foreach (var cs in sm.states)
                    if (cs.state != null) _nodePath[cs.state] = prefix + cs.state.name;
                foreach (var csm in sm.stateMachines)
                {
                    var child = csm.stateMachine;
                    if (child == null) continue;
                    _nodePath[child] = prefix + child.name;
                    Walk(child, prefix + child.name + " / ");
                }
            }
            // The root itself is deliberately absent and contributes no prefix: every path is relative to the
            // layer, and repeating the layer name on every row would be noise.
            Walk(root, "");
        }

        private static string RawPath(UnityEngine.Object o)
        {
            if (o == null) return "(none)";
            return _nodePath.TryGetValue(o, out var p) ? p : o.name;
        }

        private static string PathOf(AnimatorState s) => s == null ? "(none)" : RawPath(s);
        private static string PathOf(AnimatorStateMachine m) => m == null ? "(none)" : RawPath(m) + " (SM)";

        // A machine's Entry node, named by the machine it belongs to - the layer root's is just "Entry".
        private static string EntryPathOf(AnimatorStateMachine sm)
        {
            if (sm == null) return "Entry";
            string p = _nodePath.TryGetValue(sm, out var found) ? found : null;
            return string.IsNullOrEmpty(p) ? "Entry" : p + " / Entry";
        }

        private static string TransitionDestPath(AnimatorTransitionBase t)
        {
            if (t == null) return "(none)";
            if (t.isExit) return "Exit";
            if (t.destinationState != null) return PathOf(t.destinationState);
            if (t.destinationStateMachine != null) return PathOf(t.destinationStateMachine);
            return "(none)";
        }
        private static string MotionShort(Motion m)
        {
            if (m == null) return "(no motion)";
            if (m is BlendTree bt) return "Blend Tree" + (string.IsNullOrEmpty(bt.name) ? "" : " “" + bt.name + "”");
            return m.name;
        }
        private static float Avg(List<float> xs) { float s = 0; foreach (var v in xs) s += v; return s / xs.Count; }
        // Shared with the blend-tree view's input-port labels, which show the same thresholds and positions.
        // One formatter so an exported file and the editor can never disagree about a rounding.
        private static string F(float v) => Ath.FormatBlendValue(v);
        // HTML/attribute escaper for the small amount of markup C# emits directly (the controller name in the
        // <title>/<h1>). Escapes quotes too so it's safe in attribute contexts, matching the JS-side esc().
        private static string E(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&#34;").Replace("'", "&#39;");

        // Graph colours, emitted after the static CSS so they override it, and computed from the SAME constants
        // the canvas draws with - dark from the shipped defaults, light from the built-in Light theme. Written
        // this way rather than hardcoded in Css because that is how the two drifted: the export's plain node fill
        // was near-black on dark and pure white on light, while the addon's is one mid grey on both.
        //
        // The canvas background is flat in the addon (Background mode "Default" is a single colour), so view1 and
        // view2 get the same value and the page's radial gradient collapses to that flat colour.
        //
        // Only the values that differ from the addon are listed. The per-type node colours already in Css -
        // default state, entry, any state, exit - were matched to these same defaults by hand and still agree.
        private static string GraphThemeCss()
        {
            return ":root{" + GraphVars(Ath.DefaultCanvasBgColorDark,
                                        Ath.DefaultGridMinorColor,
                                        Ath.DefaultGridMajorColor) + "}"
                 + "body.light{" + GraphVars(Ath.DefaultCanvasBgColorLight,
                                             Ath.DefaultGridMinorColorLight,
                                             Ath.DefaultGridMajorColorLight) + "}";
        }

        private static string GraphVars(Color canvasBg, Color gridMinor, Color gridMajor)
        {
            string bg = "#" + ColorUtility.ToHtmlStringRGB(canvasBg);
            return "--view1:" + bg + ";--view2:" + bg + ";"
                 + "--gmin:" + Rgba(gridMinor) + ";--gmaj:" + Rgba(gridMajor) + ";"
                 + "--node:#" + ColorUtility.ToHtmlStringRGB(Ath.DefaultNodeStateColor) + ";"
                 + "--nodesm:#" + ColorUtility.ToHtmlStringRGB(Ath.DefaultNodeSubSmColor) + ";"
                 + "--nodetext:#" + ColorUtility.ToHtmlStringRGB(Ath.DefaultNodeLabelColor) + ";"
                 + "--nodemname:#" + ColorUtility.ToHtmlStringRGB(Ath.DefaultNodeMotionNameColor) + ";"
                 + "--nodemnone:#" + ColorUtility.ToHtmlStringRGB(Ath.DefaultNodeMotionNoneColor) + ";";
        }

        // CSS rgba() - the grid colours carry an alpha that a #RRGGBB string would drop.
        private static string Rgba(Color c) =>
            string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})",
                Mathf.RoundToInt(c.r * 255f), Mathf.RoundToInt(c.g * 255f), Mathf.RoundToInt(c.b * 255f), c.a);

        // The page's stylesheet and behaviour live beside this file as real .css and .js rather than as verbatim
        // string constants in here. As constants every quote in them had to be written doubled, which is why the
        // 459 lines between them ended up holding exactly ONE double quote - the code was shaped around its
        // container. A stray one silently ended the literal and put the C# error a hundred lines from the actual
        // mistake, and nothing could highlight, format or check either language while it sat inside a C# string.
        //
        // Nothing is substituted into these files. They are appended verbatim and everything computed - the DATA
        // json, GraphThemeCss - is emitted around them. That is the split worth keeping: static text in a file,
        // generated text in C#. It is also why the SVG export still builds its output in code; there is no static
        // blob in it to move out.
        //
        // Read on every export rather than cached, so editing the .js and re-exporting shows the change with no
        // domain reload. An export is user-initiated and already walks the whole controller, so a 15 KB read is
        // free. A missing file throws, and Export's catch turns it into the same localized failure dialog as any
        // other export error - the alternative is quietly writing a page with no styling or no script and
        // reporting success, which is a worse failure than the compile error this replaces.
        private static string LoadPageAsset(string filename)
        {
            string root = Ath.GetAddonEditorFolder(); // resolved through this assembly's asmdef, so folder moves are safe
            if (string.IsNullOrEmpty(root))
                throw new System.IO.FileNotFoundException(
                    $"Could not locate the Animator To HTML folder, so {filename} could not be loaded.");

            string path = Ath.AbsoluteAssetPath(root + "/Export/" + filename);
            if (!System.IO.File.Exists(path))
                throw new System.IO.FileNotFoundException(
                    $"{filename} is missing from {root}/Export - the HTML export needs it. Restore it from the addon package.", path);

            return System.IO.File.ReadAllText(path);
        }
    }
}

#endif
