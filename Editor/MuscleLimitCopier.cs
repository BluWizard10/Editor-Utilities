#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BluUtilities.MuscleLimitCopier
{
    public class MuscleLimitCopier : EditorWindow
    {
        const int GroupCount = 8;
        const int G_Body = 0, G_Head = 1, G_LeftArm = 2, G_LeftFingers = 3, G_RightArm = 4, G_RightFingers = 5, G_LeftLeg = 6, G_RightLeg = 7;

        // Same group names and order as Unity's Per-Muscle Settings panel.
        static readonly string[] k_GroupLabels =
        {
            "Body", "Head", "Left Arm", "Left Fingers",
            "Right Arm", "Right Fingers", "Left Leg", "Right Leg"
        };
        static readonly string[] k_AxisLabels = { "X", "Y", "Z" };

        static string[] s_BoneNames;
        static string[] s_MuscleNames;
        static int[] s_MuscleBone;
        static int[] s_MuscleAxis;
        static int[][] s_BoneMuscles;
        static List<int>[] s_GroupMuscles;
        static Dictionary<string, int> s_MuscleIndexByName;

        static void EnsureTables()
        {
            if (s_BoneNames != null) return;

            s_BoneNames = HumanTrait.BoneName;
            s_MuscleNames = HumanTrait.MuscleName;
            int boneCount = HumanTrait.BoneCount;
            int muscleCount = HumanTrait.MuscleCount;
 
            s_MuscleBone = new int[muscleCount];
            s_MuscleAxis = new int[muscleCount];
            for (int m = 0; m < muscleCount; m++)
            {
                s_MuscleBone[m] = -1;
                s_MuscleAxis[m] = -1;
            }

            // HumanTrait.MuscleFromBone(bone, dof): dof 0/1/2 = X/Y/Z, which is the
            // same component of HumanLimit.min / max that Unity's own muscle editor edits.
            s_BoneMuscles = new int[boneCount][];
            for (int b = 0; b < boneCount; b++)
            {
                s_BoneMuscles[b] = new int[3];
                for (int d = 0; d < 3; d++)
                {
                    int m = HumanTrait.MuscleFromBone(b, d);
                    s_BoneMuscles[b][d] = m;
                    if (m >= 0 && m < muscleCount)
                    {
                        s_MuscleBone[m] = b;
                        s_MuscleAxis[m] = d;
                    }
                }
            }

            s_GroupMuscles = new List<int>[GroupCount];
            for (int g = 0; g < GroupCount; g++) s_GroupMuscles[g] = new List<int>();
            for (int m = 0; m < muscleCount; m++)
            {
                if (s_MuscleBone[m] < 0) continue;
                s_GroupMuscles[ClassifyBone(s_BoneNames[s_MuscleBone[m]])].Add(m);
            }

            s_MuscleIndexByName = new Dictionary<string, int>();
            for (int m = 0; m < muscleCount; m++) s_MuscleIndexByName[s_MuscleNames[m]] = m;
        }

        // Classified by name so it doesn't depend on HumanBodyBones' numeric values.
        static int ClassifyBone(string bone)
        {
            bool left = bone.StartsWith("Left", StringComparison.Ordinal);
            if (ContainsAny(bone, "Thumb", "Index", "Middle", "Ring", "Little")) return left ? G_LeftFingers : G_RightFingers;
            if (ContainsAny(bone, "Shoulder", "UpperArm", "LowerArm", "Hand")) return left ? G_LeftArm : G_RightArm;
            if (ContainsAny(bone, "UpperLeg", "LowerLeg", "Foot", "Toes")) return left ? G_LeftLeg : G_RightLeg;
            if (ContainsAny(bone, "Neck", "Head", "Eye", "Jaw")) return G_Head;
            return G_Body; // Hips, Spine, Chest, UpperChest
        }

        static bool ContainsAny(string s, params string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
                if (s.IndexOf(parts[i], StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        // When useDefaultValues is true, the stored min/max are not what Unity uses;
        // Unity's own muscle editor substitutes HumanTrait.GetMuscleDefaultMin/Max.
        static float EffMin(HumanLimit l, int muscle, int axis)
        {
            return l.useDefaultValues ? HumanTrait.GetMuscleDefaultMin(muscle) : l.min[axis];
        }

        static float EffMax(HumanLimit l, int muscle, int axis)
        {
            return l.useDefaultValues ? HumanTrait.GetMuscleDefaultMax(muscle) : l.max[axis];
        }
 
        static bool Approx(float a, float b)
        {
            return Mathf.Abs(a - b) < 0.0001f;
        }
 
        static string Deg(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture) + "°";
        }

        static Dictionary<string, int> IndexByHumanName(HumanBone[] human)
        {
            var map = new Dictionary<string, int>();
            if (human == null)
                return map;
            for (int i = 0; i < human.Length; i++)
                if (!string.IsNullOrEmpty(human[i].humanName))
                    map[human[i].humanName] = i;
            return map;
        }

        class SourceData
        {
            public bool valid;
            public string message;
            public MessageType messageType = MessageType.Info;
            public string assetPath;
            public HumanDescription desc;
            public Dictionary<string, int> map = new Dictionary<string, int>();
        }

        class Change
        {
            public int muscle;
            public float oldMin, oldMax, newMin, newMax;
            public bool oldWasDefault, newIsDefault;
        }

        class TargetPlan
        {
            public Object asset;
            public string path;
            public ModelImporter importer;
            public string error;
            public HumanDescription newDesc;
            public readonly List<Change> changes = new List<Change>();
            public readonly List<string> skipped = new List<string>();
            public int alreadyMatching;
            public readonly List<LimitRecord> originals = new List<LimitRecord>();
        }

        [Serializable]
        class LimitRecord
        {
            public string humanName;
            public Vector3 min, max, center;
            public float axisLength;
            public bool useDefaultValues;
 
            public static LimitRecord From(string humanName, HumanLimit l)
            {
                return new LimitRecord
                {
                    humanName = humanName,
                    min = l.min,
                    max = l.max,
                    center = l.center,
                    axisLength = l.axisLength,
                    useDefaultValues = l.useDefaultValues
                };
            }
 
            public HumanLimit ToLimit()
            {
                var l = new HumanLimit();
                l.min = min;
                l.max = max;
                l.center = center;
                l.axisLength = axisLength;
                l.useDefaultValues = useDefaultValues;
                return l;
            }
        }

        [Serializable]
        class TargetSnapshot
        {
            public string assetPath;
            public List<LimitRecord> limits = new List<LimitRecord>();
        }

        const string k_PrefsSelection = "MuscleLimitCopier.SelectedMuscles";
        const string k_PrefsMirrorDefaults = "MuscleLimitCopier.MirrorDefaults";
 
        [SerializeField] Object m_Source;
        [SerializeField] List<Object> m_Targets = new List<Object>();
        [SerializeField] bool[] m_Selected;
        [SerializeField] bool[] m_GroupFoldout = new bool[GroupCount];
        [SerializeField] bool m_MirrorDefaults = true;
        [SerializeField] string m_Filter = "";
        [SerializeField] List<TargetSnapshot> m_LastApply = new List<TargetSnapshot>();
        [SerializeField] Vector2 m_Scroll;
 
        SourceData m_Src = new SourceData();
        readonly List<TargetPlan> m_Plans = new List<TargetPlan>();
        readonly Dictionary<string, bool> m_PlanFoldout = new Dictionary<string, bool>();
        bool m_Dirty = true;

        GUIStyle m_RightMini;
        GUIStyle m_Mini;

        [MenuItem("Tools/BluWizard LABS/Muscle Limit Copier")]
        static void Open()
        {
            var w = GetWindow<MuscleLimitCopier>();
            w.titleContent = new GUIContent("Muscle Limits");
            w.minSize = new Vector2(460, 420);
            w.Show();
        }

        void OnEnable()
        {
            EnsureTables();
            if (m_Selected == null || m_Selected.Length != s_MuscleNames.Length) LoadSelection();
            if (m_GroupFoldout == null || m_GroupFoldout.Length != GroupCount) m_GroupFoldout = new bool[GroupCount];
            m_MirrorDefaults = EditorPrefs.GetBool(k_PrefsMirrorDefaults, true);
            m_Dirty = true;
        }

        void OnFocus() { m_Dirty = true; }
        void OnProjectChange() { m_Dirty = true; Repaint(); }

        void LoadSelection()
        {
            m_Selected = new bool[s_MuscleNames.Length];
            string saved = EditorPrefs.GetString(k_PrefsSelection, "");
            if (string.IsNullOrEmpty(saved)) return;
            foreach (string name in saved.Split('|'))
            {
                int m;
                if (s_MuscleIndexByName.TryGetValue(name, out m)) m_Selected[m] = true;
            }
        }

        void SelectionChanged()
        {
            var names = new List<string>();
            for (int m = 0; m < m_Selected.Length; m++)
            {
                if (m_Selected[m]) names.Add(s_MuscleNames[m]);
            }
            EditorPrefs.SetString(k_PrefsSelection, string.Join("|", names.ToArray()));
            m_Dirty = true;
        }

        static SourceData ResolveSource(Object obj)
        {
            var s = new SourceData();
            if (obj == null)
            {
                s.message = "Pick a humanoid model (FBX) or an Avatar asset to copy limits from.";
                return s;
            }
 
            s.assetPath = AssetDatabase.GetAssetPath(obj);
            var importer = AssetImporter.GetAtPath(s.assetPath) as ModelImporter;
            Avatar avatar = obj as Avatar;
            bool haveDesc = false;
 
            if (importer != null)
            {
                if (importer.animationType != ModelImporterAnimationType.Human)
                {
                    s.message = "Source: Rig > Animation Type is not Humanoid.";
                    s.messageType = MessageType.Error;
                    return s;
                }
 
                if (importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther)
                {
                    // This model doesn't build its own Avatar; read the one it actually uses.
                    avatar = importer.sourceAvatar;
                    if (avatar == null)
                    {
                        s.message = "Source copies its Avatar from another file, but no source Avatar is assigned.";
                        s.messageType = MessageType.Error;
                        return s;
                    }
                    s.message = "This model uses \"Copy From Other Avatar\"; limits are read from " + AssetDatabase.GetAssetPath(avatar) + ".";
                }
                else
                {
                    s.desc = importer.humanDescription;
                    haveDesc = true;
                }
            }
 
            if (!haveDesc)
            {
                if (avatar == null)
                {
                    s.message = "Source is not a model file or an Avatar asset.";
                    s.messageType = MessageType.Error;
                    return s;
                }
                if (!avatar.isHuman)
                {
                    s.message = "Source Avatar is not humanoid.";
                    s.messageType = MessageType.Error;
                    return s;
                }
                s.desc = avatar.humanDescription;
            }
 
            if (s.desc.human == null || s.desc.human.Length == 0)
            {
                s.message = "Source has no humanoid bone mapping. Configure its rig first.";
                s.messageType = MessageType.Error;
                return s;
            }
 
            s.map = IndexByHumanName(s.desc.human);
            s.valid = true;
            return s;
        }

        void Rebuild()
        {
            m_Dirty = false;
            m_Src = ResolveSource(m_Source);
            m_Plans.Clear();
            for (int i = 0; i < m_Targets.Count; i++)
            {
                if (m_Targets[i] != null) m_Plans.Add(BuildPlan(m_Targets[i]));
            }
        }

        TargetPlan BuildPlan(Object asset)
        {
            var plan = new TargetPlan { asset = asset, path = AssetDatabase.GetAssetPath(asset) };
            plan.importer = AssetImporter.GetAtPath(plan.path) as ModelImporter;
 
            if (plan.importer == null)
            {
                plan.error = "Not a model file. Only model importers (FBX etc.) can be written to.";
                return plan;
            }
            if (plan.importer.animationType != ModelImporterAnimationType.Human)
            {
                plan.error = "Rig > Animation Type is not Humanoid.";
                return plan;
            }
            if (plan.importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                plan.error = "Rig > Avatar Definition is not \"Create From This Model\", so this file " + "does not build its own Avatar and its limits are not used.";
                return plan;
            }
            if (!m_Src.valid)
            {
                plan.error = "Waiting for a valid source.";
                return plan;
            }
            if (plan.path == m_Src.assetPath)
            {
                plan.error = "This is the source model.";
                return plan;
            }
 
            HumanDescription desc = plan.importer.humanDescription;
            if (desc.human == null || desc.human.Length == 0)
            {
                plan.error = "No humanoid bone mapping. Configure the rig first.";
                return plan;
            }
 
            // HumanBone and HumanLimit are structs: work on a copy of the array,
            // then assign the whole description back when applying.
            var human = (HumanBone[])desc.human.Clone();
            var dstMap = IndexByHumanName(human);
 
            for (int b = 0; b < s_BoneMuscles.Length; b++)
            {
                int[] ms = s_BoneMuscles[b];
                bool anySelected = false, allSelected = true;
                for (int d = 0; d < 3; d++)
                {
                    if (ms[d] < 0) continue;
                    if (m_Selected[ms[d]]) anySelected = true;
                    else allSelected = false;
                }
                if (!anySelected) continue;
 
                string humanName = s_BoneNames[b];
                int si, di;
                if (!m_Src.map.TryGetValue(humanName, out si))
                {
                    AddSkipped(plan, ms, humanName + " is not mapped in the source");
                    continue;
                }
                if (!dstMap.TryGetValue(humanName, out di))
                {
                    AddSkipped(plan, ms, humanName + " is not mapped in this model");
                    continue;
                }
 
                HumanLimit src = m_Src.desc.human[si].limit;
                HumanBone bone = human[di];
                HumanLimit before = bone.limit;
                HumanLimit after = before;
                Vector3 mn = after.min, mx = after.max;
 
                if (m_MirrorDefaults && src.useDefaultValues && allSelected)
                {
                    // Whole bone selected and the source is on Unity's defaults:
                    // put the target on defaults too (same as the muscle panel's Reset).
                    for (int d = 0; d < 3; d++)
                    {
                        int m = ms[d];
                        if (m < 0) continue;
                        mn[d] = HumanTrait.GetMuscleDefaultMin(m);
                        mx[d] = HumanTrait.GetMuscleDefaultMax(m);
                    }
                    after.useDefaultValues = true;
                }
                else
                {
                    if (after.useDefaultValues)
                    {
                        // The use-defaults flag is per bone, not per axis. Before switching it
                        // off, write the defaults into every axis so the axes you did NOT select
                        // keep their current effective values. Unity's own muscle editor does the
                        // same when you drag a single slider on a bone that is still on defaults.
                        for (int d = 0; d < 3; d++)
                        {
                            int m = ms[d];
                            if (m < 0) continue;
                            mn[d] = HumanTrait.GetMuscleDefaultMin(m);
                            mx[d] = HumanTrait.GetMuscleDefaultMax(m);
                        }
                        // Unity documents center as [0,0,0] while defaults are on; keep that
                        // effective value once the stored values become active.
                        after.center = Vector3.zero;
                        after.useDefaultValues = false;
                    }
 
                    for (int d = 0; d < 3; d++)
                    {
                        int m = ms[d];
                        if (m < 0 || !m_Selected[m]) continue;
                        mn[d] = EffMin(src, m, d);
                        mx[d] = EffMax(src, m, d);
                    }
                }
                after.min = mn;
                after.max = mx;
 
                // Compare effective values; leave the bone untouched if nothing really changes.
                bool boneChanged = false;
                for (int d = 0; d < 3; d++)
                {
                    int m = ms[d];
                    if (m < 0) continue;
                    float oMin = EffMin(before, m, d), oMax = EffMax(before, m, d);
                    float nMin = EffMin(after, m, d), nMax = EffMax(after, m, d);
                    bool diff = !Approx(oMin, nMin) || !Approx(oMax, nMax);
                    if (diff) boneChanged = true;
                    if (!m_Selected[m]) continue;
 
                    if (diff)
                    {
                        plan.changes.Add(new Change
                        {
                            muscle = m,
                            oldMin = oMin, oldMax = oMax, newMin = nMin, newMax = nMax,
                            oldWasDefault = before.useDefaultValues,
                            newIsDefault = after.useDefaultValues
                        });
                    }
                    else
                    {
                        plan.alreadyMatching++;
                    }
                }
 
                if (!boneChanged)
                    continue;
 
                plan.originals.Add(LimitRecord.From(humanName, before));
                bone.limit = after;
                human[di] = bone;
            }
 
            desc.human = human;
            plan.newDesc = desc;
            return plan;
        }

        void AddSkipped(TargetPlan plan, int[] boneMuscles, string reason)
        {
            for (int d = 0; d < 3; d++)
            {
                int m = boneMuscles[d];
                if (m >= 0 && m_Selected[m])
                    plan.skipped.Add(s_MuscleNames[m] + " (" + reason + ")");
            }
        }

        void Apply()
        {
            Rebuild(); // re-read importers right before writing
 
            var toWrite = new List<TargetPlan>();
            int total = 0;
            foreach (var p in m_Plans)
            {
                if (p.error == null && p.changes.Count > 0)
                {
                    toWrite.Add(p);
                    total += p.changes.Count;
                }
            }
            if (toWrite.Count == 0)
            {
                ShowNotification(new GUIContent("Nothing to change"));
                return;
            }
 
            if (!EditorUtility.DisplayDialog("Muscle Limit Copier",
                    "Write " + total + " muscle limit change(s) to " + toWrite.Count +
                    " model(s) and reimport them?\n\nOnly per-muscle rotation limits are changed.",
                    "Apply", "Cancel"))
                return;
 
            m_LastApply.Clear();
            try
            {
                for (int i = 0; i < toWrite.Count; i++)
                {
                    var p = toWrite[i];
                    EditorUtility.DisplayProgressBar("Muscle Limit Copier", "Reimporting " + p.path,
                        (float)i / toWrite.Count);
 
                    var snap = new TargetSnapshot { assetPath = p.path };
                    snap.limits.AddRange(p.originals);
 
                    p.importer.humanDescription = p.newDesc;
                    p.importer.SaveAndReimport();
                    m_LastApply.Add(snap);
 
                    Debug.Log("[Muscle Limit Copier] " + p.path + ": updated " + p.changes.Count +
                              " muscle limit(s) from " + m_Src.assetPath, p.asset);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                m_Dirty = true;
                Repaint();
            }
        }

        void RevertLastApply()
        {
            if (m_LastApply.Count == 0)
                return;
            if (!EditorUtility.DisplayDialog("Muscle Limit Copier",
                    "Restore the muscle limits that the last Apply overwrote on " + m_LastApply.Count +
                    " model(s)?", "Revert", "Cancel"))
                return;
 
            try
            {
                for (int i = 0; i < m_LastApply.Count; i++)
                {
                    var snap = m_LastApply[i];
                    EditorUtility.DisplayProgressBar("Muscle Limit Copier", "Reverting " + snap.assetPath,
                        (float)i / m_LastApply.Count);
 
                    var importer = AssetImporter.GetAtPath(snap.assetPath) as ModelImporter;
                    if (importer == null)
                    {
                        Debug.LogWarning("[Muscle Limit Copier] Could not revert " + snap.assetPath +
                                         " (model importer not found).");
                        continue;
                    }
 
                    HumanDescription desc = importer.humanDescription;
                    if (desc.human == null)
                        continue;
                    var human = (HumanBone[])desc.human.Clone();
                    var map = IndexByHumanName(human);
                    foreach (var rec in snap.limits)
                    {
                        int idx;
                        if (!map.TryGetValue(rec.humanName, out idx)) continue;
                        HumanBone bone = human[idx];
                        bone.limit = rec.ToLimit();
                        human[idx] = bone;
                    }
                    desc.human = human;
                    importer.humanDescription = desc;
                    importer.SaveAndReimport();
                    Debug.Log("[Muscle Limit Copier] Reverted " + snap.limits.Count + " bone limit(s) on " +
                              snap.assetPath);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                m_LastApply.Clear();
                m_Dirty = true;
                Repaint();
            }
        }

        void OnGUI()
        {
            EnsureTables();
            if (m_Selected == null || m_Selected.Length != s_MuscleNames.Length) LoadSelection();
            if (m_RightMini == null)
            {
                m_Mini = new GUIStyle(EditorStyles.miniLabel);
                m_RightMini = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            }
            if (m_Dirty) Rebuild();
 
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            DrawSource();
            EditorGUILayout.Space();
            DrawTargets();
            EditorGUILayout.Space();
            DrawMuscleSelection();
            EditorGUILayout.Space();
            DrawPreview();
            EditorGUILayout.EndScrollView();
 
            DrawFooter();
        }

        void DrawSource()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var obj = EditorGUILayout.ObjectField(new GUIContent("Copy limits from", "A humanoid model file (FBX) or an Avatar asset."), m_Source, typeof(Object), false);
            if (EditorGUI.EndChangeCheck())
            {
                m_Source = obj;
                m_Dirty = true;
            }
            if (!string.IsNullOrEmpty(m_Src.message)) EditorGUILayout.HelpBox(m_Src.message, m_Src.messageType);
        }

        void DrawTargets()
        {
            EditorGUILayout.LabelField("Targets (humanoid model files)", EditorStyles.boldLabel);
 
            int removeAt = -1;
            for (int i = 0; i < m_Targets.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                var obj = EditorGUILayout.ObjectField(m_Targets[i], typeof(Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    m_Targets[i] = obj;
                    m_Dirty = true;
                }
                if (GUILayout.Button("✕", GUILayout.Width(22))) removeAt = i;
                EditorGUILayout.EndHorizontal();
            }
            if (removeAt >= 0)
            {
                m_Targets.RemoveAt(removeAt);
                m_Dirty = true;
            }
 
            Rect drop = GUILayoutUtility.GetRect(0, 32, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drag model files here", EditorStyles.helpBox);
            HandleDrop(drop);
 
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Add Selected", "Add the model files selected in the Project window."))) AddTargets(Selection.objects);
            using (new EditorGUI.DisabledScope(m_Targets.Count == 0))
            {
                if (GUILayout.Button("Clear"))
                {
                    m_Targets.Clear();
                    m_Dirty = true;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void HandleDrop(Rect r)
        {
            Event e = Event.current;
            if (!r.Contains(e.mousePosition))
                return;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;
 
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddTargets(DragAndDrop.objectReferences);
            }
            e.Use();
        }

        void AddTargets(Object[] objects)
        {
            int skipped = 0;
            foreach (var o in objects)
            {
                string path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is ModelImporter))
                {
                    skipped++;
                    continue;
                }
                // Store the model's main asset even if an Avatar or mesh sub-asset was dragged.
                Object main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main != null && !m_Targets.Contains(main))
                    m_Targets.Add(main);
            }
            if (skipped > 0)
                ShowNotification(new GUIContent("Skipped " + skipped + " item(s) that are not model files"));
            m_Dirty = true;
        }

        void DrawMuscleSelection()
        {
            int selectedCount = 0;
            for (int m = 0; m < m_Selected.Length; m++) if (m_Selected[m]) selectedCount++;
 
            EditorGUILayout.LabelField("Muscles to copy (" + selectedCount + " of " + m_Selected.Length + ")", EditorStyles.boldLabel);
 
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All", EditorStyles.miniButtonLeft)) SetAll(true);
            if (GUILayout.Button("None", EditorStyles.miniButtonMid)) SetAll(false);
            if (GUILayout.Button("Invert", EditorStyles.miniButtonMid))
            {
                for (int m = 0; m < m_Selected.Length; m++) m_Selected[m] = !m_Selected[m];
                SelectionChanged();
            }
            if (GUILayout.Button(new GUIContent("Fingers", "Both hands' finger muscles only."), EditorStyles.miniButtonMid)) SelectPreset(false);
            if (GUILayout.Button(new GUIContent("Hands + Fingers", "Finger muscles plus Hand Down-Up / In-Out."), EditorStyles.miniButtonRight)) SelectPreset(true);
            EditorGUILayout.EndHorizontal();
 
            m_Filter = EditorGUILayout.TextField(new GUIContent("Filter", "Show only muscles whose name contains this text."), m_Filter);
 
            bool mirror = EditorGUILayout.ToggleLeft(new GUIContent(
                "Keep \"use defaults\" when the source bone uses defaults",
                "If every muscle on a bone is selected and that bone uses Unity's default limits in the source, " +
                "the target bone is also switched to defaults (same as the Reset button in the Muscles panel) " +
                "instead of receiving the default numbers as custom values. The effective limits are the same either way."),
                m_MirrorDefaults
            );
            if (mirror != m_MirrorDefaults)
            {
                m_MirrorDefaults = mirror;
                EditorPrefs.SetBool(k_PrefsMirrorDefaults, mirror);
                m_Dirty = true;
            }
 
            EditorGUILayout.LabelField("Values shown are the source's effective min / max.", m_Mini);
 
            bool filtering = !string.IsNullOrEmpty(m_Filter);
            var visible = new List<int>();
 
            for (int g = 0; g < GroupCount; g++)
            {
                visible.Clear();
                foreach (int m in s_GroupMuscles[g])
                {
                    if (!filtering || s_MuscleNames[m].IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0) visible.Add(m);
                }
                if (visible.Count == 0) continue;
 
                int groupSel = 0;
                foreach (int m in visible)
                {
                    if (m_Selected[m]) groupSel++;
                }
 
                Rect row = EditorGUILayout.GetControlRect();
                Rect foldRect = new Rect(row.x, row.y, 14, row.height);
                Rect toggleRect = new Rect(row.x + 14, row.y, row.width - 14, row.height);
                // While filtering, groups are shown expanded without changing the saved foldout state.
                bool expanded = EditorGUI.Foldout(foldRect, m_GroupFoldout[g] || filtering, GUIContent.none, true);
                if (!filtering) m_GroupFoldout[g] = expanded;
 
                EditorGUI.showMixedValue = groupSel > 0 && groupSel < visible.Count;
                EditorGUI.BeginChangeCheck();
                bool groupOn = EditorGUI.ToggleLeft(toggleRect, k_GroupLabels[g] + "  (" + groupSel + "/" + visible.Count + ")", groupSel == visible.Count, EditorStyles.boldLabel);
                if (EditorGUI.EndChangeCheck())
                {
                    foreach (int m in visible) m_Selected[m] = groupOn;
                    SelectionChanged();
                }
                EditorGUI.showMixedValue = false;
 
                if (!expanded) continue;
 
                EditorGUI.indentLevel += 2;
                foreach (int m in visible)
                {
                    Rect r = EditorGUILayout.GetControlRect();
                    const float valueWidth = 180f;
                    Rect nameRect = new Rect(r.x, r.y, r.width - valueWidth - 4, r.height);
                    Rect valueRect = new Rect(r.xMax - valueWidth, r.y, valueWidth, r.height);
 
                    int bone = s_MuscleBone[m];
                    var label = new GUIContent(s_MuscleNames[m], "Bone: " + s_BoneNames[bone] + "   Axis: " + k_AxisLabels[s_MuscleAxis[m]]);
 
                    EditorGUI.BeginChangeCheck();
                    bool on = EditorGUI.ToggleLeft(nameRect, label, m_Selected[m]);
                    if (EditorGUI.EndChangeCheck())
                    {
                        m_Selected[m] = on;
                        SelectionChanged();
                    }
 
                    int indent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;
                    EditorGUI.LabelField(valueRect, SourceValueText(m), m_RightMini);
                    EditorGUI.indentLevel = indent;
                }
                EditorGUI.indentLevel -= 2;
            }
        }

        string SourceValueText(int m)
        {
            if (!m_Src.valid) return "";
            int bone = s_MuscleBone[m];
            int si;
            if (!m_Src.map.TryGetValue(s_BoneNames[bone], out si)) return "not mapped in source";
            HumanLimit l = m_Src.desc.human[si].limit;
            int axis = s_MuscleAxis[m];
            return Deg(EffMin(l, m, axis)) + " / " + Deg(EffMax(l, m, axis)) + (l.useDefaultValues ? "  (default)" : "");
        }
 
        void SetAll(bool on)
        {
            for (int m = 0; m < m_Selected.Length; m++) m_Selected[m] = on;
            SelectionChanged();
        }

        void SelectPreset(bool includeHands)
        {
            for (int m = 0; m < m_Selected.Length; m++) m_Selected[m] = false;
            foreach (int m in s_GroupMuscles[G_LeftFingers]) m_Selected[m] = true;
            foreach (int m in s_GroupMuscles[G_RightFingers]) m_Selected[m] = true;
            if (includeHands)
            {
                for (int m = 0; m < m_Selected.Length; m++)
                {
                    int b = s_MuscleBone[m];
                    if (b >= 0 && (s_BoneNames[b] == "LeftHand" || s_BoneNames[b] == "RightHand")) m_Selected[m] = true;
                }
            }
            SelectionChanged();
        }

        void DrawPreview()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
 
            if (!m_Src.valid)
            {
                EditorGUILayout.HelpBox("Pick a valid source to see what would change.", MessageType.None);
                return;
            }
            if (m_Plans.Count == 0)
            {
                EditorGUILayout.HelpBox("Add one or more target model files.", MessageType.None);
                return;
            }
 
            foreach (var p in m_Plans)
            {
                bool open;
                m_PlanFoldout.TryGetValue(p.path, out open);
 
                string summary;
                if (p.error != null) summary = "not writable";
                else summary = p.changes.Count + " change(s), " + p.alreadyMatching + " already match" + (p.skipped.Count > 0 ? ", " + p.skipped.Count + " skipped" : "");
 
                open = EditorGUILayout.Foldout(open, p.path + "  —  " + summary, true);
                m_PlanFoldout[p.path] = open;
 
                if (p.error != null)
                {
                    EditorGUILayout.HelpBox(p.error, MessageType.Warning);
                    continue;
                }
                if (!open) continue;
 
                EditorGUI.indentLevel++;
                if (p.changes.Count == 0 && p.skipped.Count == 0) EditorGUILayout.LabelField("Selected muscles already match the source.", m_Mini);
 
                foreach (var c in p.changes)
                {
                    Rect r = EditorGUILayout.GetControlRect();
                    const float col = 150f;
                    Rect nameRect = new Rect(r.x, r.y, r.width - col * 2 - 20, r.height);
                    Rect beforeRect = new Rect(r.xMax - col * 2 - 16, r.y, col, r.height);
                    Rect arrowRect = new Rect(r.xMax - col - 16, r.y, 16, r.height);
                    Rect afterRect = new Rect(r.xMax - col, r.y, col, r.height);
 
                    EditorGUI.LabelField(nameRect, s_MuscleNames[c.muscle]);
                    int indent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;
                    EditorGUI.LabelField(beforeRect, Deg(c.oldMin) + " / " + Deg(c.oldMax) + (c.oldWasDefault ? " (def)" : ""), m_RightMini);
                    EditorGUI.LabelField(arrowRect, "→", m_Mini);
                    EditorGUI.LabelField(afterRect, Deg(c.newMin) + " / " + Deg(c.newMax) + (c.newIsDefault ? " (def)" : ""), m_RightMini);
                    EditorGUI.indentLevel = indent;
                }
 
                foreach (string s in p.skipped) EditorGUILayout.LabelField("Skipped: " + s, m_Mini);
                EditorGUI.indentLevel--;
            }
        }

        void DrawFooter()
        {
            int total = 0, models = 0;
            foreach (var p in m_Plans)
            {
                if (p.error == null && p.changes.Count > 0)
                {
                    total += p.changes.Count;
                    models++;
                }
            }
 
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(total == 0))
            {
                string label = total == 0
                    ? "Nothing to apply"
                    : "Apply " + total + " change(s) to " + models + " model(s)";
                // Deferred so the dialog and reimport run outside this OnGUI pass.
                if (GUILayout.Button(label, GUILayout.Height(28)))
                    EditorApplication.delayCall += Apply;
            }
            using (new EditorGUI.DisabledScope(m_LastApply.Count == 0))
            {
                if (GUILayout.Button(new GUIContent("Revert Last Apply",
                        "Restores the exact limits the last Apply replaced (kept while this window stays open).")))
                    EditorApplication.delayCall += RevertLastApply;
            }
            EditorGUILayout.Space();
        }
    }
}
#endif
