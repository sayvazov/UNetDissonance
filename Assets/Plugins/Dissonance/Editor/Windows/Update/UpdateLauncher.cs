using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Dissonance.Editor.Windows.Update
{
    [InitializeOnLoad]
    public class UpdateLauncher
    {
        #region fields and properties
        private static readonly Log Log = Logs.Create(LogCategory.Core, typeof(UpdateLauncher).Name);

        private const string CheckForNewVersionKey = "placeholder_dissonance_update_checkforlatestversion";
        private const string ForceCheckUpdateMenuItemPath = "Window/Dissonance/Check For Updates";

        private static readonly string StatePath = Path.Combine(DissonanceRootPath.BaseResourcePath, ".UpdateState.json");

        private static IEnumerator<bool> _updateCheckInProgress;
        #endregion

        [MenuItem(ForceCheckUpdateMenuItemPath, priority = 500)]
        public static void ForceUpdateCheck()
        {
            //Set the next-check time stamp to now
            var state = GetUpdateState();
            SetUpdateState(new UpdateState(state.ShownForVersion, DateTime.UtcNow));

            //Since an update has been forced then we can assume the updater is enabled
            SetUpdaterEnabled(true);
        }

        private static IEnumerable<bool> CheckUpdateVersion()
        {
            //Exit if the update check is not enabled
            if (!GetUpdaterEnabled())
                yield return false;

            //Exit if the next update isn't due yet
            var state = GetUpdateState();
            if (state.NextCheck >= DateTime.UtcNow)
                yield return false;

            //setup some helpers for later
            var random = new System.Random();
            Action failed = () => SetUpdateState(new UpdateState(state.ShownForVersion, DateTime.UtcNow + TimeSpan.FromMinutes(random.Next(10, 70))));
            Action success = () => SetUpdateState(new UpdateState(state.ShownForVersion, DateTime.UtcNow + TimeSpan.FromHours((random.Next(12, 72)))));

            //Begin downloading the manifest of all Dissonance updates
            var request = UnityWebRequest.Get(string.Format("https://placeholder-software.co.uk/dissonance/releases/latest-published.html{0}", EditorMetadata.GetQueryString()));
            request.Send();

            //Wait until request is complete
            while (!request.isDone && !request.isNetworkError)
                yield return true;

            //If it's an error give up and schedule the next check fairly soon
            if (request.isNetworkError)
            {
                request.Dispose();
                failed();
                Log.Trace("Update request failed");
                yield return false;
            }

            //Get the response bytes and discard the request
            var bytes = request.downloadHandler.data;
            request.Dispose();

            //Parse the response data. If we fail give up and schedule the next check fairly soon
            SemanticVersion latest;
            if (!TryParse(bytes, out latest) || latest == null)
            {
                failed();
                Log.Trace("Update response parsing failed");
                yield return false;
            }
            else
            {
                //Check if we've already shown the window for a greater version
                if (latest.CompareTo(state.ShownForVersion) <= 0)
                {
                    success();
                    Log.Trace("Update success, window already shown for higher version");
                    yield return false;
                }

                //Check if the new version is greater than the currently installed version
                if (latest.CompareTo(DissonanceComms.Version) <= 0)
                {
                    success();
                    Log.Trace("Update success, newer version already installed");
                    yield return false;
                }

                //Update the state so that the window does not show up again for this version
                SetUpdateState(new UpdateState(latest, DateTime.UtcNow + TimeSpan.FromHours((random.Next(12, 72)))));
                UpdateWindow.Show(latest, DissonanceComms.Version);
                Log.Trace("Update success, showing update notification window");
                success();
            }
        }

        internal static void Startup()
        {
            Windows.Startup.SafeUpdate += Update;
        }

        private static void Update()
        {
            //Exit right away if the update is not enabled
            if (!GetUpdaterEnabled())
                return;

            //Early exit if:
            // - no update in progress
            // - next update not due yet
            if (_updateCheckInProgress == null && GetUpdateState().NextCheck >= DateTime.UtcNow)
                return;

            //Start a new update check if there isn't one in progress
            if (_updateCheckInProgress == null)
                _updateCheckInProgress = CheckUpdateVersion().GetEnumerator();

            //Pump updater until it's done
            if (!_updateCheckInProgress.MoveNext() || !_updateCheckInProgress.Current)
                _updateCheckInProgress = null;
        }

        /// <summary>
        /// Try to parse the response from the server
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="parsed"></param>
        /// <returns></returns>
        private static bool TryParse(byte[] bytes, [CanBeNull] out SemanticVersion parsed)
        {
            try
            {
                // The received data is a root level array. Wrap it in an object which gives the root array a name
                var str = Encoding.UTF8.GetString(bytes);
                parsed = JsonUtility.FromJson<SemanticVersion>(str);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                parsed = null;
                return false;
            }
        }

        #region state
        /// <summary>
        /// Get the state of the updater as stored on disk
        /// </summary>
        /// <returns></returns>
        private static UpdateState GetUpdateState()
        {
            if (!File.Exists(StatePath))
            {
                // State path does not exist at all so create the default
                var state = new UpdateState(new SemanticVersion(), DateTime.UtcNow);
                SetUpdateState(state);
                return state;
            }
            else
            {
                //Read the state from the file
                using (var reader = File.OpenText(StatePath))
                {
                    var state = JsonUtility.FromJson<UpdateState>(reader.ReadToEnd());
                    return state;
                }
            }
        }

        /// <summary>
        /// Set the state of the updater as stored on disk
        /// </summary>
        /// <param name="state"></param>
        private static void SetUpdateState([CanBeNull] UpdateState state)
        {
            if (state == null)
            {
                //Clear installer state
                File.Delete(StatePath);
            }
            else
            {
                using (var writer = File.CreateText(StatePath))
                    writer.Write(JsonUtility.ToJson(state));
            }
        }
        #endregion

        #region prefs
        internal static void SetUpdaterEnabled(bool enabled)
        {
            EditorPrefs.SetBool(CheckForNewVersionKey, enabled);
        }

        internal static bool GetUpdaterEnabled()
        {
            //If pref not set yet then assume the updater is enabled
            if (!EditorPrefs.HasKey(CheckForNewVersionKey))
                return true;

            return EditorPrefs.GetBool(CheckForNewVersionKey);
        }
        #endregion

        [Serializable] private class UpdateState
        {
            [SerializeField, UsedImplicitly] private SemanticVersion _shownForVersion;
            [SerializeField, UsedImplicitly] private long _nextCheckFileTime;

            public SemanticVersion ShownForVersion
            {
                get { return _shownForVersion; }
            }

            public DateTime NextCheck
            {
                get { return DateTime.FromFileTimeUtc(_nextCheckFileTime); }
            }

            public UpdateState(SemanticVersion version, DateTime nextCheck)
            {
                _shownForVersion = version;
                _nextCheckFileTime = nextCheck.ToFileTimeUtc();
            }

            public override string ToString()
            {
                return _shownForVersion.ToString();
            }
        }
    }
}