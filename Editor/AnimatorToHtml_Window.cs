#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TohruTheDragon.AnimatorToHtml
{
    // Drop an avatar (or any GameObject with Animators) or an Animator Controller into the field, pick a controller, export it
    // to the same interactive HTML page Animator Toolkit produces - always the whole controller, every layer and
    // every parameter.
    //
    // Avatar controllers are read from the VRC Avatar Descriptor through SerializedObject rather than the SDK
    // types, so this addon compiles in projects without the VRChat SDK too.
    public class AnimatorToHtml_Window : EditorWindow
    {
        private struct Entry
        {
            public string label;
            public AnimatorController controller;
        }

        [SerializeField] private Object _source;
        [SerializeField] private int _selected;
        private readonly List<Entry> _entries = new List<Entry>();

        [MenuItem("TohruTheDragon/Animator To HTML")]
        public static void Open()
        {
            var w = GetWindow<AnimatorToHtml_Window>();
            w.titleContent = new GUIContent("Animator To HTML");
            w.minSize = new Vector2(340, 260);
            w.Show();
        }

        private void OnEnable() => Rebuild();
        private void OnFocus() => Rebuild();
        private void OnHierarchyChange() { Rebuild(); Repaint(); }
        private void OnProjectChange() { Rebuild(); Repaint(); }

        private void SetSource(Object o)
        {
            if (o is GameObject || o is AnimatorController || o == null)
            {
                _source = o;
                _selected = 0;
                Rebuild();
            }
            else if (o is Component c) SetSource(c.gameObject);
        }

        private void Rebuild()
        {
            _entries.Clear();
            if (_source is AnimatorController ac)
                _entries.Add(new Entry { label = ac.name, controller = ac });
            else if (_source is GameObject go)
                CollectFromGameObject(go, _entries);
            if (_selected >= _entries.Count) _selected = 0;
        }

        // --- discovery ----------------------------------------------------------------------------------------

        private static void CollectFromGameObject(GameObject go, List<Entry> into)
        {
            var seen = new HashSet<AnimatorController>();

            void Add(string label, RuntimeAnimatorController rac)
            {
                var ac = ResolveController(rac, out bool isOverride);
                if (ac == null || !seen.Add(ac)) return;
                string name = isOverride ? $"{ac.name}  (base of override \"{rac.name}\")" : ac.name;
                into.Add(new Entry { label = string.IsNullOrEmpty(label) ? name : $"{label}: {name}", controller = ac });
            }

            // VRChat playable layers first, in descriptor order (Base, Additive, Gesture, Action, FX, then the
            // special Sitting / TPose / IKPose). Layers left on "Default" use the SDK's built-in controller, which
            // isn't assigned on the avatar, so they are skipped.
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp.GetType().Name != "VRCAvatarDescriptor") continue;
                using (var so = new SerializedObject(comp))
                {
                    AddDescriptorLayers(so.FindProperty("baseAnimationLayers"), Add);
                    AddDescriptorLayers(so.FindProperty("specialAnimationLayers"), Add);
                }
            }

            // Then any Animator on the object or below it (the avatar root's own Animator, plus props, etc.).
            foreach (var anim in go.GetComponentsInChildren<Animator>(true))
            {
                if (anim == null || anim.runtimeAnimatorController == null) continue;
                string where = anim.gameObject == go ? "Animator" : "Animator on " + RelativePath(go.transform, anim.transform);
                Add(where, anim.runtimeAnimatorController);
            }
        }

        private static void AddDescriptorLayers(SerializedProperty arr, System.Action<string, RuntimeAnimatorController> add)
        {
            if (arr == null || !arr.isArray) return;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                var isDefault = el.FindPropertyRelative("isDefault");
                if (isDefault != null && isDefault.boolValue) continue;
                var ctrl = el.FindPropertyRelative("animatorController");
                if (ctrl == null || !(ctrl.objectReferenceValue is RuntimeAnimatorController rac)) continue;
                var type = el.FindPropertyRelative("type");
                string label = "Layer " + i;
                if (type != null && type.propertyType == SerializedPropertyType.Enum
                    && type.enumValueIndex >= 0 && type.enumValueIndex < type.enumDisplayNames.Length)
                    label = type.enumDisplayNames[type.enumValueIndex];
                add(label, rac);
            }
        }

        private static AnimatorController ResolveController(RuntimeAnimatorController rac, out bool isOverride)
        {
            isOverride = false;
            for (int guard = 0; rac != null && guard < 16; guard++)
            {
                if (rac is AnimatorController ac) return ac;
                if (rac is AnimatorOverrideController aoc) { isOverride = true; rac = aoc.runtimeAnimatorController; }
                else return null;
            }
            return null;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) parts.Insert(0, cur.name);
            return string.Join("/", parts);
        }

        // --- GUI ----------------------------------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField(new GUIContent("Avatar / Controller",
                "An avatar or any GameObject with Animators (scene object or prefab), or an Animator Controller asset."),
                _source, typeof(Object), true);
            if (EditorGUI.EndChangeCheck()) SetSource(picked);

            EditorGUILayout.Space(8);

            if (_source == null)
            {
                EditorGUILayout.HelpBox("Drop an avatar or an Animator Controller into the field above.", MessageType.Info);
                return;
            }
            if (_entries.Count == 0)
            {
                EditorGUILayout.HelpBox(_source is GameObject
                    ? "No Animator Controllers were found on this object. Playable layers left on \"Default\" have no controller assigned to export."
                    : "Nothing to export.", MessageType.Warning);
                return;
            }

            var labels = new GUIContent[_entries.Count];
            for (int i = 0; i < _entries.Count; i++) labels[i] = new GUIContent(_entries[i].label.Replace('/', '∕')); // '/' would make a submenu
            var sel = _entries[_selected].controller;
            // A dropped controller IS the export - there is nothing to choose, so no dropdown.
            if (!(_source is AnimatorController))
            using (new EditorGUILayout.HorizontalScope())
            {
                _selected = EditorGUILayout.Popup(new GUIContent("Controller"), _selected, labels);
                sel = _entries[_selected].controller;
                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
                    EditorGUIUtility.PingObject(sel);
            }
            if (sel != null)
            {
                int layers = sel.layers.Length, prms = sel.parameters.Length;
                EditorGUILayout.LabelField($"{layers} layer{(layers == 1 ? "" : "s")}, {prms} parameter{(prms == 1 ? "" : "s")}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(sel == null))
                if (GUILayout.Button("Export to HTML…", GUILayout.Height(30)))
                    AnimatorToHtml_Exporter.Export(sel); // whole controller: every layer, every parameter
            EditorGUILayout.Space(6);
        }
    }
}

#endif
