#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TohruTheDragon.AnimatorToHtml
{
    // Standalone copies of the Animator Toolkit pieces the HTML exporter reads. Everything a toolkit SETTING
    // would normally decide is fixed to the value the toolkit ships with, which is also what its own HTML export
    // uses (it deliberately ignores user settings - see the export policy in AnimatorToHtml_Exporter).
    internal static class Ath
    {
        // --- strings (Animator Toolkit's english.json) -----------------------------------------------------
        private static readonly Dictionary<string, string> s_Text = new Dictionary<string, string>
        {
            { "dialog.btn.ok", "OK" },
            { "dialog.htmlExport.title", "Export Controller to HTML" },
            { "dialog.htmlExport.noController", "No controller is open." },
            { "dialog.htmlExport.failed", "Export failed:\n{0}" },
            { "badge.blendType.cartesian2D", "2D Freeform Cartesian Blend Tree" },
            { "badge.blendType.direct", "Direct Blend Tree" },
            { "badge.blendType.freeform2D", "2D Freeform Directional Blend Tree" },
            { "badge.blendType.simple1D", "1D Blend Tree" },
            { "badge.blendType.simple2D", "2D Simple Directional Blend Tree" },
            { "badge.layerBrokenLink", "Broken blend tree ownership. A blend tree has a child that is stored in a different asset file, so this file depends on that one." },
            { "badge.layerBrokenTransition", "This layer has a broken transition, an invalid condition or missing parameter." },
            { "badge.layerNoneTooltip", "This layer has states with no motion assigned" },
            { "badge.layerWdAllOn", "All states are Write Defaults ON" },
            { "badge.layerWdMixed", "Mixed Write Defaults on this layer" },
            { "badge.layerWdOutlier", "A state is using the wrong Write Defaults value." },
            { "badge.wdDiffersTooltip", "Write Defaults differs from the rest of the controller" },
            { "tooltip.brokenTreeLink", "Broken ownership. This tree is stored inside the controller, but its parent is a separate asset file." },
            { "tooltip.brokenTreeLinkState", "Broken ownership inside this blend tree. One of its sub-trees is stored in this controller while its parent is a separate asset file." },
            { "tooltip.externalBlendTree", "External Blend Tree: stored outside the controller you are viewing, in its own .asset file, or in another asset." },
            { "tooltip.externalChildBlendTree", "External Blend Tree's Child: stored in the same external file as the \"Ex\" tree above it." },
            { "transition.warn.alwaysTaken", "No exit time and no conditions." },
            { "transition.warn.invalidComparison", "Condition on \"{0}\" ({1}) uses an invalid comparison ({2})." },
            { "transition.warn.missingParameter", "Condition uses missing parameter \"{0}\"." },
            { "transition.warn.noParameter", "A condition has no parameter set." },
        };

        public static string Tr(string key, params object[] args)
        {
            if (!s_Text.TryGetValue(key, out var s)) return key;
            return args == null || args.Length == 0 ? s : string.Format(CultureInfo.InvariantCulture, s, args);
        }

        // --- shipped default colours (AnimatorToolkit.cs) --------------------------------------------------
        public static readonly Color DefaultGridMinorColor = new Color(1f, 1f, 1f, 0.03f);
        public static readonly Color DefaultGridMajorColor = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color DefaultGridMinorColorLight = new Color(0f, 0f, 0f, 0.06f);
        public static readonly Color DefaultGridMajorColorLight = new Color(0f, 0f, 0f, 0.10f);
        public static readonly Color DefaultCanvasBgColorDark = new Color(0.12f, 0.12f, 0.12f);
        public static readonly Color DefaultCanvasBgColorLight = new Color(0.34f, 0.34f, 0.34f);
        public static readonly Color DefaultNodeStateColor = new Color(0.30f, 0.30f, 0.30f);
        public static readonly Color DefaultNodeSubSmColor = new Color(0.30f, 0.32f, 0.36f);
        public static readonly Color DefaultNodeLabelColor = new Color(0.9f, 0.9f, 0.9f);
        public static readonly Color DefaultNodeMotionNameColor = Color.white;
        public static readonly Color DefaultNodeMotionNoneColor = new Color(1f, 0.82f, 0.30f);

        // --- formatting / files ----------------------------------------------------------------------------
        public static string FormatBlendValue(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private const string LastExportFolderPref = "AnimatorToHtml_LastExportFolder";

        public static string LastExportFolder()
        {
            string dir = EditorPrefs.GetString(LastExportFolderPref, "");
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : "";
        }

        public static void RememberExportFolder(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) EditorPrefs.SetString(LastExportFolderPref, dir);
        }

        private const int MaxBaseLength = 120;
        private static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static string Sanitize(string name, string fallback = "Export")
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c < 32) continue;
                if ("\\/:*?\"<>|".IndexOf(c) >= 0) continue;
                sb.Append(c);
            }
            string cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
            if (cleaned.Length > MaxBaseLength) cleaned = cleaned.Substring(0, MaxBaseLength).TrimEnd('.', ' ');
            if (cleaned.Length == 0) return fallback;
            int dot = cleaned.IndexOf('.');
            string stem = dot >= 0 ? cleaned.Substring(0, dot) : cleaned;
            foreach (var r in Reserved)
                if (string.Equals(stem, r, StringComparison.OrdinalIgnoreCase)) return "_" + cleaned;
            return cleaned;
        }

        public static string AbsoluteAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath) && !string.IsNullOrEmpty(pkg.assetPath)
                && assetPath.StartsWith(pkg.assetPath, StringComparison.OrdinalIgnoreCase))
            {
                string rest = assetPath.Substring(pkg.assetPath.Length).TrimStart('/', '\\');
                return rest.Length == 0 ? pkg.resolvedPath : Path.Combine(pkg.resolvedPath, rest);
            }
            return Path.GetFullPath(assetPath);
        }

        // The folder holding this assembly's .asmdef ("Assets/…" or "Packages/…"), so the page assets are found
        // wherever the addon has been moved to.
        public static string GetAddonEditorFolder()
        {
            string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("TohruTheDragon.AnimatorToHtml.Editor");
            if (string.IsNullOrEmpty(asmdef)) return null;
            return Path.GetDirectoryName(asmdef)?.Replace('\\', '/');
        }

        // The AAP badge as a data URI. Animator Toolkit picks the icon that matches the editor skin; so does this.
        public static string AapIconDataUri()
        {
            try
            {
                string root = GetAddonEditorFolder();
                if (string.IsNullOrEmpty(root)) return null;
                string file = EditorGUIUtility.isProSkin ? "AAP_dark.png" : "AAP_light.png";
                string path = AbsoluteAssetPath(root + "/Export/" + file);
                if (!File.Exists(path)) path = AbsoluteAssetPath(root + "/Export/AAP_dark.png");
                if (!File.Exists(path)) return null;
                return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch { return null; }
        }

        // --- parameters ------------------------------------------------------------------------------------
        private static readonly Dictionary<string, AnimatorControllerParameterType> s_VrcBuiltIn =
            new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal)
        {
            { "IsLocal", AnimatorControllerParameterType.Bool },
            { "PreviewMode", AnimatorControllerParameterType.Int },
            { "Viseme", AnimatorControllerParameterType.Int },
            { "Voice", AnimatorControllerParameterType.Float },
            { "GestureLeft", AnimatorControllerParameterType.Int },
            { "GestureRight", AnimatorControllerParameterType.Int },
            { "GestureLeftWeight", AnimatorControllerParameterType.Float },
            { "GestureRightWeight", AnimatorControllerParameterType.Float },
            { "AngularY", AnimatorControllerParameterType.Float },
            { "VelocityX", AnimatorControllerParameterType.Float },
            { "VelocityY", AnimatorControllerParameterType.Float },
            { "VelocityZ", AnimatorControllerParameterType.Float },
            { "VelocityMagnitude", AnimatorControllerParameterType.Float },
            { "Upright", AnimatorControllerParameterType.Float },
            { "Grounded", AnimatorControllerParameterType.Bool },
            { "Seated", AnimatorControllerParameterType.Bool },
            { "AFK", AnimatorControllerParameterType.Bool },
            { "TrackingType", AnimatorControllerParameterType.Int },
            { "VRMode", AnimatorControllerParameterType.Int },
            { "MuteSelf", AnimatorControllerParameterType.Bool },
            { "InStation", AnimatorControllerParameterType.Bool },
            { "Earmuffs", AnimatorControllerParameterType.Bool },
            { "IsOnFriendsList", AnimatorControllerParameterType.Bool },
            { "AvatarVersion", AnimatorControllerParameterType.Int },
            { "IsAnimatorEnabled", AnimatorControllerParameterType.Bool },
            { "ScaleModified", AnimatorControllerParameterType.Bool },
            { "ScaleFactor", AnimatorControllerParameterType.Float },
            { "ScaleFactorInverse", AnimatorControllerParameterType.Float },
            { "EyeHeightAsMeters", AnimatorControllerParameterType.Float },
            { "EyeHeightAsPercent", AnimatorControllerParameterType.Float },
        };

        public struct VrcParamInfo { public AnimatorControllerParameterType Type; }

        public static bool TryGetVrcBuiltInParam(string name, out VrcParamInfo info)
        {
            info = default;
            if (!s_VrcBuiltIn.TryGetValue(name ?? string.Empty, out var t)) return false;
            info.Type = t;
            return true;
        }

        public static bool ClipHasAapBinding(AnimationClip clip)
        {
            if (clip == null) return false;
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
                if (b.type == typeof(Animator) && string.IsNullOrEmpty(b.path) && !string.IsNullOrEmpty(b.propertyName))
                    return true;
            return false;
        }

        // --- transition health (AnimatorToolkit_TransitionHealth.cs) ---------------------------------------
        private static bool IsHiddenIdentifierTransition(AnimatorTransitionBase t)
            => t is AnimatorStateTransition ast && ast.isExit && ast.mute && !string.IsNullOrEmpty(ast.name);

        public static bool TransitionHasWarning(AnimatorTransitionBase t, Dictionary<string, AnimatorControllerParameterType> paramTypes)
            => TransitionWarningTextFor(t, paramTypes).Length > 0;

        public static string TransitionWarningTextFor(AnimatorTransitionBase t, Dictionary<string, AnimatorControllerParameterType> paramTypes)
        {
            if (ReferenceEquals(t, null) || t == null) return "";
            if (IsHiddenIdentifierTransition(t)) return "";
            if (paramTypes == null) return "";
            var conditions = t.conditions;
            if (t is AnimatorStateTransition ast)
            {
                bool noConditions = conditions == null || conditions.Length == 0;
                if (!ast.hasExitTime && noConditions) return Tr("transition.warn.alwaysTaken");
            }
            if (conditions != null)
                foreach (var c in conditions)
                {
                    if (string.IsNullOrEmpty(c.parameter)) return Tr("transition.warn.noParameter");
                    if (!paramTypes.TryGetValue(c.parameter, out var type)) return Tr("transition.warn.missingParameter", c.parameter);
                    if (!ModeValidForType(c.mode, type)) return Tr("transition.warn.invalidComparison", c.parameter, type, c.mode);
                }
            return "";
        }

        private static bool ModeValidForType(AnimatorConditionMode mode, AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;
                case AnimatorControllerParameterType.Int:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less
                        || mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
                case AnimatorControllerParameterType.Float:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less;
                default:
                    return true;
            }
        }
    }

    internal static class Ath_DefaultState
    {
        public static AnimatorStateMachine ContainingChild(AnimatorStateMachine sm, AnimatorState state)
        {
            if (sm == null || state == null) return null;
            foreach (var cs in sm.states) if (cs.state == state) return null;
            foreach (var csm in sm.stateMachines)
                if (csm.stateMachine != null && Contains(csm.stateMachine, state)) return csm.stateMachine;
            return null;
        }

        public static bool Contains(AnimatorStateMachine sm, AnimatorState state, HashSet<AnimatorStateMachine> visited = null)
        {
            if (sm == null || state == null) return false;
            if (visited == null) visited = new HashSet<AnimatorStateMachine>();
            if (!visited.Add(sm)) return false;
            foreach (var cs in sm.states) if (cs.state == state) return true;
            foreach (var csm in sm.stateMachines) if (Contains(csm.stateMachine, state, visited)) return true;
            return false;
        }
    }

    internal static class Ath_BlendTypeTag
    {
        public static string Tag(BlendTreeType type)
        {
            switch (type)
            {
                case BlendTreeType.Direct: return "[DIR]";
                case BlendTreeType.Simple1D: return "[S1D]";
                case BlendTreeType.SimpleDirectional2D: return "[S2D]";
                case BlendTreeType.FreeformDirectional2D: return "[F2D]";
                case BlendTreeType.FreeformCartesian2D: return "[C2D]";
                default: return "[?]";
            }
        }

        public static Color DefaultColor(BlendTreeType type)
        {
            switch (type)
            {
                case BlendTreeType.Direct: return new Color(0.30f, 0.95f, 0.30f);
                case BlendTreeType.Simple1D: return new Color(1.00f, 0.95f, 0.20f);
                case BlendTreeType.SimpleDirectional2D: return new Color(0.40f, 0.95f, 0.72f);
                case BlendTreeType.FreeformDirectional2D: return new Color(0.35f, 0.92f, 1.00f);
                default: return new Color(0.32f, 0.92f, 0.84f);
            }
        }

        public static string Tooltip(BlendTreeType type)
        {
            switch (type)
            {
                case BlendTreeType.Direct: return Ath.Tr("badge.blendType.direct");
                case BlendTreeType.Simple1D: return Ath.Tr("badge.blendType.simple1D");
                case BlendTreeType.SimpleDirectional2D: return Ath.Tr("badge.blendType.simple2D");
                case BlendTreeType.FreeformDirectional2D: return Ath.Tr("badge.blendType.freeform2D");
                case BlendTreeType.FreeformCartesian2D: return Ath.Tr("badge.blendType.cartesian2D");
                default: return type.ToString();
            }
        }
    }

    internal static class Ath_BrokenLinkTag
    {
        public static readonly Color DefaultColor = new Color(0xF2 / 255f, 0x4D / 255f, 0x4D / 255f); // #F24D4D
        public static string Tooltip => Ath.Tr("tooltip.brokenTreeLink");
        public static string StateTooltip => Ath.Tr("tooltip.brokenTreeLinkState");
    }

    internal static class Ath_ExternalTag
    {
        public const string Text = "Ex";
        public const string ChildText = "Exc";
        public static readonly Color DefaultColor = new Color(0xF6 / 255f, 0x70 / 255f, 0xFF / 255f);      // #F670FF
        public static readonly Color ChildDefaultColor = new Color(0xF8 / 255f, 0x92 / 255f, 0xFF / 255f); // #F892FF
        public static string Tooltip => Ath.Tr("tooltip.externalBlendTree");

        public struct TagInfo { public bool Show; public string Text; public Color Color; public string Tooltip; }

        // Always the canonical (shipped) colours - the export never reads the user's palette.
        public static TagInfo ResolveTag(Object owner, BlendTree parentTree, BlendTree tree, bool canonical = true)
        {
            if (tree == null || !Ath_MotionOwnership.IsForeignTree(owner, tree)) return default;
            Object parentAnchor = parentTree != null ? (Object)parentTree : owner;
            if (Ath_MotionOwnership.SameAssetFile(parentAnchor, tree))
                return new TagInfo { Show = true, Text = ChildText, Color = ChildDefaultColor, Tooltip = Ath.Tr("tooltip.externalChildBlendTree") };
            return new TagInfo { Show = true, Text = Text, Color = DefaultColor, Tooltip = Tooltip };
        }
    }

    internal static class Ath_MotionOwnership
    {
        private static string PathOf(Object obj) => obj == null ? "" : (AssetDatabase.GetAssetPath(obj) ?? "");

        public static bool IsBackwardsReference(BlendTree parent, BlendTree child)
        {
            if (parent == null || child == null) return false;
            string parentPath = PathOf(parent);
            if (string.IsNullOrEmpty(parentPath)) return false;
            string childPath = PathOf(child);
            if (string.IsNullOrEmpty(childPath) || childPath == parentPath) return false;
            return childPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase);
        }

        public static bool SubtreeHasBackwardsReference(BlendTree root) => root != null && Scan(root, new HashSet<BlendTree>());

        private static bool Scan(BlendTree tree, HashSet<BlendTree> visited)
        {
            if (tree == null || !visited.Add(tree)) return false;
            foreach (var c in tree.children)
            {
                if (!(c.motion is BlendTree child)) continue;
                if (IsBackwardsReference(tree, child)) return true;
                if (Scan(child, visited)) return true;
            }
            return false;
        }

        public static bool StateMachineHasBackwardsReference(AnimatorStateMachine sm)
            => StateMachineHasBackwardsReference(sm, new HashSet<AnimatorStateMachine>());

        private static bool StateMachineHasBackwardsReference(AnimatorStateMachine sm, HashSet<AnimatorStateMachine> visited)
        {
            if (sm == null || !visited.Add(sm)) return false;
            foreach (var cs in sm.states)
                if (cs.state != null && cs.state.motion is BlendTree tree && SubtreeHasBackwardsReference(tree)) return true;
            foreach (var child in sm.stateMachines)
                if (StateMachineHasBackwardsReference(child.stateMachine, visited)) return true;
            return false;
        }

        public static bool IsForeignTree(Object owner, BlendTree tree)
        {
            if (tree == null) return false;
            string path = PathOf(tree);
            if (string.IsNullOrEmpty(path)) return false;
            string ownerPath = owner != null ? PathOf(owner) : null;
            if (string.IsNullOrEmpty(ownerPath)) return false;
            return path != ownerPath;
        }

        public static bool SameAssetFile(Object a, Object b)
        {
            string pa = PathOf(a), pb = PathOf(b);
            if (string.IsNullOrEmpty(pa) || string.IsNullOrEmpty(pb)) return false;
            return pa == pb;
        }
    }
}

#endif
