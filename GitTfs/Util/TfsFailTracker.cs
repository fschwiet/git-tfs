using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace Sep.Git.Tfs.Util
{
    public class TfsFailTracker
    {
        StringBuilder _sb = new StringBuilder();
        long? _lastChangesetWithChangeError = null;

        public void Reset()
        {
            _sb = new StringBuilder();
        }

        public string GetSummary()
        {
            if (_sb.Length == 0)
                return null;
            else
                return _sb.ToString();
        }

        void WriteIntroForChangeset(long changesetId)
        {
            _sb.AppendLine("Error loading changeset " + changesetId);
        }

        public void TrackFailureLoadingChangeset(long changesetId, Exception someException)
        {
            WriteIntroForChangeset(changesetId);
            _sb.AppendLine("    Message: " + someException.Message);

            foreach(var line in someException.StackTrace.Split('\n'))
            {
                _sb.AppendLine("    " + line);
            }
        }

        public void TrackFailureLoadingChange(long changesetId, ChangeType changeType, string serverPath, string reason)
        {
            if (changesetId != _lastChangesetWithChangeError)
                WriteIntroForChangeset(changesetId);

            _sb.AppendLine("    " + reason + " for " + changeType + " of " + serverPath);

            _lastChangesetWithChangeError = changesetId;
        }

        public static bool ShouldHaltOnError(Exception exception)
        {
            // an intermittent error that goes away
            if (exception.GetType() == typeof(RepositoryNotFoundException))
                return true;

            // unrecoverable... hmm
            if (exception.GetType() == typeof(System.ComponentModel.Win32Exception)
                && exception.Message.Contains("The filename or extension is too long"))
                return true;

            return false;
        }
    }
}
