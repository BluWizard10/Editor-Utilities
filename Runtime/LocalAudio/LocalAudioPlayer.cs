using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

// Script designed by BluWizard LABS. https://bluwizard.net
//
// Plays an audio file from this computer through the AudioSource that AudioLink listens to, so AudioLink
// avatars can be tested in Play Mode without a video player or yt-dlp. Editor only.
//
// AudioLink is optional. It is referenced as a plain MonoBehaviour and only touched through its public
// fields and methods by reflection, so this compiles and works whether or not AudioLink is installed.
//
// When using the AudioLinkAvatar prefab, disable its EditorAudioPlayer / ytdlpPlayer object first, since
// it plays into the same AudioSource.

namespace BluUtilities.LocalAudio
{
    [AddComponentMenu("BluWizard LABS/Local Audio Player")]
    [DisallowMultipleComponent]
    public class LocalAudioPlayer : MonoBehaviour
    {
        public enum Status
        {
            NoFile,
            Loading,
            Ready,
            Error
        }

        private const string AudioLinkTypeName = "AudioLink.AudioLink";
        private const string LogPrefix = "[<color=#0092d9>BluWizard LABS</color>] [<color=#00cc00>Local Audio Player</color>]";

        // Formats Unity can decode on its own.
        private static readonly Dictionary<string, AudioType> Formats = new Dictionary<string, AudioType>
        {
            { "wav", AudioType.WAV },
            { "mp3", AudioType.MPEG },
            { "ogg", AudioType.OGGVORBIS },
            { "aif", AudioType.AIFF },
            { "aiff", AudioType.AIFF }
        };

        [Tooltip("The audio file to play: a full path, or a path relative to the project folder.")]
        [Delayed]
        public string filePath = "";

        [Tooltip("Optional. The AudioLink to feed. Its AudioSource is used when none is set below, and pausing, loading and errors show up in its media state.")]
        public MonoBehaviour audioLink;

        [Tooltip("The AudioSource to play through. Leave empty to use AudioLink's.")]
        public AudioSource audioSource;

        public bool loop = true;

        [Tooltip("Start playing as soon as the file has loaded.")]
        public bool autoPlay = true;

        public Status status { get; private set; } = Status.NoFile;

        /// <summary>Details of the loaded file, or what went wrong.</summary>
        public string statusText { get; private set; } = "";

        public AudioClip clip { get; private set; }

        public bool isPaused { get; private set; }

        public bool isPlaying => OwnsAudioSource && audioSource.isPlaying;

        public float length => clip != null ? clip.length : 0f;

        /// <summary>Playback position in seconds.</summary>
        public float time => !OwnsAudioSource ? 0f : audioSource.isPlaying || isPaused ? audioSource.time : _cuedTime;

        public static IEnumerable<string> SupportedExtensions => Formats.Keys;

        private string _loadedPath;
        private Coroutine _loading;
        private UnityWebRequest _request;
        private AudioClip _previousClip;
        private float _cuedTime;
        private bool _drivingMediaState;

        private bool OwnsAudioSource => clip != null && audioSource != null && audioSource.clip == clip;

        private void Reset()
        {
            FindReferences();
        }

        private void OnValidate()
        {
            // Dropping a GameObject on the AudioLink field picks its first MonoBehaviour, which may not be AudioLink.
            if (audioLink != null && !IsAudioLink(audioLink))
                audioLink = FirstAudioLink(audioLink.GetComponents<MonoBehaviour>()) ?? audioLink;
        }

        private void OnEnable()
        {
            FindReferences();
            Load();
        }

        private void OnDisable()
        {
            Unload();
            _loadedPath = null;
            StopDrivingMediaState();
        }

        private void Update()
        {
            if ((filePath ?? "") != _loadedPath)
                Load();

            if (OwnsAudioSource)
                audioSource.loop = loop;

            UpdateMediaState();
        }

        /// <summary>Fills in empty references: the nearest AudioLink above this object or in the open Scenes, then its AudioSource.</summary>
        public void FindReferences()
        {
            if (audioLink == null)
                audioLink = FirstAudioLink(GetComponentsInParent<MonoBehaviour>(true)) ?? FirstAudioLink(FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None));

            if (audioSource == null)
                audioSource = AudioSourceOf(audioLink);
        }

        #region Loading

        /// <summary>Loads filePath, replacing whatever was loaded before. Only works in Play Mode.</summary>
        public void Load()
        {
            if (!Application.isPlaying || !enabled || !gameObject.activeInHierarchy)
                return;

            Unload();
            _loadedPath = filePath ?? "";

            string path = ResolvePath(_loadedPath);
            if (path.Length == 0)
                return;

            if (!File.Exists(path))
            {
                Fail($"File not found: {path}");
                return;
            }

            if (!Formats.TryGetValue(ExtensionOf(path), out AudioType type))
            {
                Fail($"Unity can't play {Path.GetExtension(path)} files. Use a {DescribeFormats()} file instead.");
                return;
            }

            _loading = StartCoroutine(LoadClip(path, type));
        }

        private IEnumerator LoadClip(string path, AudioType type)
        {
            status = Status.Loading;
            statusText = $"Loading {Path.GetFileName(path)}...";

            _request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
            yield return _request.SendWebRequest();

            UnityWebRequest request = _request;
            _request = null;
            _loading = null;

            AudioClip loaded = null;
            string problem = request.error;
            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    loaded = DownloadHandlerAudioClip.GetContent(request);
                }
                catch (Exception e)
                {
                    problem = e.Message;
                }
            }
            request.Dispose();

            if (loaded == null || loaded.length <= 0f || audioSource == null)
            {
                if (loaded != null)
                    Destroy(loaded);

                if (audioSource == null)
                    Fail("There is no AudioSource to play through. Assign one, or an AudioLink.");
                else
                    Fail($"Couldn't load {Path.GetFileName(path)}: {problem ?? "Unity could not decode it."}");

                yield break;
            }

            loaded.name = Path.GetFileNameWithoutExtension(path);
            clip = loaded;
            status = Status.Ready;
            statusText = $"{FormatTime(clip.length)}, {clip.frequency} Hz, {DescribeChannels(clip.channels)}";

            _previousClip = audioSource.clip;
            audioSource.Stop();
            audioSource.clip = clip;
            audioSource.loop = loop;

            if (autoPlay)
                audioSource.Play();
        }

        private void Unload()
        {
            if (_loading != null)
            {
                StopCoroutine(_loading);
                _loading = null;
            }

            if (_request != null)
            {
                _request.Abort();
                _request.Dispose();
                _request = null;
            }

            // Give the AudioSource back the way it was found.
            if (OwnsAudioSource)
            {
                audioSource.Stop();
                audioSource.clip = _previousClip;
            }

            if (clip != null)
                Destroy(clip);

            clip = null;
            _previousClip = null;
            _cuedTime = 0f;
            isPaused = false;
            status = Status.NoFile;
            statusText = "";
        }

        private void Fail(string message)
        {
            status = Status.Error;
            statusText = message;
            Debug.LogWarning($"{LogPrefix} {message}", this);
        }

        #endregion

        #region Playback

        public void Play()
        {
            if (!OwnsAudioSource)
                return;

            if (isPaused)
            {
                audioSource.UnPause();
                isPaused = false;
            }
            else if (!audioSource.isPlaying)
            {
                // Play() always starts a stopped AudioSource from the beginning, so jump to the cued spot after.
                audioSource.Play();
                audioSource.time = _cuedTime;
                _cuedTime = 0f;
            }
        }

        public void Pause()
        {
            if (!isPlaying)
                return;

            audioSource.Pause();
            isPaused = true;
        }

        public void Stop()
        {
            if (!OwnsAudioSource)
                return;

            audioSource.Stop();
            isPaused = false;
            _cuedTime = 0f;
        }

        /// <summary>Jumps to a position in seconds. While stopped, the next Play starts from there.</summary>
        public void Seek(float seconds)
        {
            if (!OwnsAudioSource)
                return;

            // Seeking right onto the last sample makes Unity complain, so stay just short of the end.
            seconds = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, clip.length - 0.05f));

            if (!audioSource.isPlaying && !isPaused)
            {
                _cuedTime = seconds;
                return;
            }

            audioSource.time = seconds;
            if (isPaused && audioSource.isPlaying)
                audioSource.Pause();
        }

        #endregion

        #region AudioLink

        public static bool IsAudioLink(Object obj)
        {
            return obj != null && obj.GetType().FullName == AudioLinkTypeName;
        }

        /// <summary>The AudioSource an AudioLink component listens to, or null.</summary>
        public static AudioSource AudioSourceOf(MonoBehaviour audioLink)
        {
            return IsAudioLink(audioLink) ? audioLink.GetType().GetField("audioSource")?.GetValue(audioLink) as AudioSource : null;
        }

        private static MonoBehaviour FirstAudioLink(MonoBehaviour[] candidates)
        {
            return Array.Find(candidates, IsAudioLink);
        }

        // AudioLink works out Playing and Stopped from the AudioSource by itself, but a paused source just
        // looks stopped to it, and it has no way of knowing about loading or errors. In those states its
        // automatic tracking is switched off and the state is set here instead.
        private void UpdateMediaState()
        {
            string state = status == Status.Loading ? "Loading"
                : status == Status.Error ? "Error"
                : isPaused ? "Paused"
                : null;

            if (state == null || !IsAudioLink(audioLink))
            {
                StopDrivingMediaState();
                return;
            }

            Type type = audioLink.GetType();
            FieldInfo automatic = type.GetField("autoSetMediaState");
            MethodInfo setPlaying = type.GetMethod("SetMediaPlaying");
            ParameterInfo[] parameters = setPlaying?.GetParameters();
            if (automatic == null || parameters == null || parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
                return;

            automatic.SetValue(audioLink, false);
            _drivingMediaState = true;

            Type states = parameters[0].ParameterType;
            if (Enum.IsDefined(states, state))
                setPlaying.Invoke(audioLink, new[] { Enum.Parse(states, state) });

            type.GetMethod("SetMediaTime")?.Invoke(audioLink, new object[] { length > 0f ? time / length : 0f });
        }

        private void StopDrivingMediaState()
        {
            if (!_drivingMediaState)
                return;

            _drivingMediaState = false;
            if (audioLink != null)
                audioLink.GetType().GetField("autoSetMediaState")?.SetValue(audioLink, true);
        }

        #endregion

        #region Helpers

        public static bool IsSupported(string path)
        {
            return Formats.ContainsKey(ExtensionOf(path));
        }

        public static string FormatTime(float seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(Mathf.Max(0f, seconds));
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }

        /// <summary>Accepts paths pasted with quotes (as Explorer's "Copy as path" does) and paths relative to the project folder.</summary>
        private static string ResolvePath(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0)
                return "";

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // Not a valid path at all; File.Exists reports it as missing.
                return path;
            }
        }

        private static string ExtensionOf(string path)
        {
            try
            {
                return (Path.GetExtension((path ?? "").Trim().Trim('"')) ?? "").TrimStart('.').ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return "";
            }
        }

        private static string DescribeFormats()
        {
            return "." + string.Join(", .", Formats.Keys);
        }

        private static string DescribeChannels(int channels)
        {
            return channels == 1 ? "mono" : channels == 2 ? "stereo" : $"{channels} channels";
        }

        #endregion
    }
}
