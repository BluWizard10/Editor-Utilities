using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Script designed by BluWizard LABS. https://bluwizard.net

namespace BluUtilities.LocalAudio
{
    [CustomEditor(typeof(LocalAudioPlayer))]
    internal class LocalAudioPlayerEditor : Editor
    {
        private SerializedProperty _filePath;
        private SerializedProperty _audioLink;
        private SerializedProperty _audioSource;
        private SerializedProperty _loop;
        private SerializedProperty _autoPlay;

        private LocalAudioPlayer Player => (LocalAudioPlayer)target;

        private void OnEnable()
        {
            _filePath = serializedObject.FindProperty(nameof(LocalAudioPlayer.filePath));
            _audioLink = serializedObject.FindProperty(nameof(LocalAudioPlayer.audioLink));
            _audioSource = serializedObject.FindProperty(nameof(LocalAudioPlayer.audioSource));
            _loop = serializedObject.FindProperty(nameof(LocalAudioPlayer.loop));
            _autoPlay = serializedObject.FindProperty(nameof(LocalAudioPlayer.autoPlay));
        }

        public override bool RequiresConstantRepaint()
        {
            return Player.isPlaying || Player.status == LocalAudioPlayer.Status.Loading;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawFileField();
            DrawStatus();

            if (EditorApplication.isPlaying)
                DrawPlaybackControls();

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_loop);
            EditorGUILayout.PropertyField(_autoPlay);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_audioLink);
            EditorGUILayout.PropertyField(_audioSource);
            DrawReferenceChecks();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawFileField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect fieldRect = EditorGUILayout.GetControlRect();

                // Handled before the field is drawn, so the text field cannot swallow the drop.
                AcceptDroppedFile(fieldRect);
                EditorGUI.PropertyField(fieldRect, _filePath, new GUIContent("Audio File"));

                if (GUILayout.Button("Browse...", GUILayout.Width(70)))
                {
                    string extensions = string.Join(",", LocalAudioPlayer.SupportedExtensions);
                    string picked = EditorUtility.OpenFilePanelWithFilters("Choose an audio file", FolderOf(_filePath.stringValue), new[] { "Audio files", extensions, "All files", "*" });
                    if (!string.IsNullOrEmpty(picked))
                        _filePath.stringValue = picked;

                    // The file dialog breaks up this GUI pass, so save the pick and end the pass here.
                    serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void AcceptDroppedFile(Rect area)
        {
            Event current = Event.current;
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform) || !area.Contains(current.mousePosition))
                return;

            string file = DragAndDrop.paths.FirstOrDefault(LocalAudioPlayer.IsSupported);
            DragAndDrop.visualMode = file != null ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;

            if (file != null && current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                _filePath.stringValue = file;
            }

            current.Use();
        }

        private void DrawStatus()
        {
            string path = _filePath.stringValue;

            if (!EditorApplication.isPlaying)
            {
                if (string.IsNullOrWhiteSpace(path))
                    EditorGUILayout.HelpBox("Pick an audio file with Browse, or drop one onto the Audio File field.", MessageType.None);
                else if (!LocalAudioPlayer.IsSupported(path))
                    EditorGUILayout.HelpBox($"Unity can only play these formats: .{string.Join(", .", LocalAudioPlayer.SupportedExtensions)}", MessageType.Warning);
                else
                    EditorGUILayout.HelpBox("The file plays in Play Mode.", MessageType.Info);
                return;
            }

            switch (Player.status)
            {
                case LocalAudioPlayer.Status.Loading:
                    EditorGUILayout.HelpBox(Player.statusText, MessageType.Info);
                    break;
                case LocalAudioPlayer.Status.Error:
                    EditorGUILayout.HelpBox(Player.statusText, MessageType.Error);
                    break;
                case LocalAudioPlayer.Status.Ready:
                    EditorGUILayout.LabelField(" ", Player.statusText, EditorStyles.miniLabel);
                    break;
            }
        }

        private void DrawPlaybackControls()
        {
            LocalAudioPlayer player = Player;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(player.status != LocalAudioPlayer.Status.Ready))
                {
                    bool playing = player.isPlaying;
                    GUIContent playPause = playing
                        ? new GUIContent(" Pause", EditorGUIUtility.IconContent("PauseButton").image)
                        : new GUIContent(" Play", EditorGUIUtility.IconContent("PlayButton").image);

                    if (GUILayout.Button(playPause, GUILayout.Width(80), GUILayout.Height(20)))
                    {
                        if (playing)
                            player.Pause();
                        else
                            player.Play();
                    }

                    if (GUILayout.Button("Stop", GUILayout.Width(60), GUILayout.Height(20)))
                        player.Stop();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_filePath.stringValue)))
                {
                    if (GUILayout.Button("Reload", GUILayout.Width(60), GUILayout.Height(20)))
                        player.Load();
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{LocalAudioPlayer.FormatTime(player.time)} / {LocalAudioPlayer.FormatTime(player.length)}");
            }

            using (new EditorGUI.DisabledScope(player.status != LocalAudioPlayer.Status.Ready))
            {
                EditorGUI.BeginChangeCheck();
                float seconds = GUILayout.HorizontalSlider(player.time, 0f, Mathf.Max(player.length, 0.01f));
                if (EditorGUI.EndChangeCheck())
                    player.Seek(seconds);
            }

            EditorGUILayout.Space();
        }

        private void DrawReferenceChecks()
        {
            MonoBehaviour audioLink = _audioLink.objectReferenceValue as MonoBehaviour;
            AudioSource audioSource = _audioSource.objectReferenceValue as AudioSource;

            if (audioLink != null && !LocalAudioPlayer.IsAudioLink(audioLink))
                EditorGUILayout.HelpBox($"{audioLink.GetType().Name} is not an AudioLink component.", MessageType.Warning);

            AudioSource listenedTo = LocalAudioPlayer.AudioSourceOf(audioLink);
            if (listenedTo != null && audioSource != null && listenedTo != audioSource)
                EditorGUILayout.HelpBox($"AudioLink listens to {listenedTo.name}, not this AudioSource, so it won't react to the file.", MessageType.Warning);

            if (audioSource != null)
                return;

            EditorGUILayout.HelpBox("Pick the AudioSource to play through, or an AudioLink to use its AudioSource.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                if (!GUILayout.Button("Find AudioLink in Scene"))
                    return;
            }

            Undo.RecordObject(Player, "Find AudioLink");
            Player.FindReferences();
            serializedObject.Update();
        }

        private static string FolderOf(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path.Trim().Trim('"')));
                return Directory.Exists(folder) ? folder : "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
