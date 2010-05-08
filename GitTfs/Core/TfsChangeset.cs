using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.VersionControl.Client;
using Sep.Git.Tfs.Util;

namespace Sep.Git.Tfs.Core
{
    class TfsChangeset : ITfsChangeset
    {
        private readonly TfsHelper tfs;
        private readonly Changeset changeset;
        public TfsChangesetInfo Summary { get; set; }

        public TfsChangeset(TfsHelper tfs, Changeset changeset)
        {
            this.tfs = tfs;
            this.changeset = changeset;
        }

        public LogEntry Apply(string lastCommit, GitIndexInfo index)
        {
            var initialTree = Summary.Remote.Repository.GetObjects(lastCommit);

            // If you make updates to a dir in TF, the changeset includes changes for all the children also,
            // and git doesn't really care if you add or delete empty dirs.
            var fileChanges = changeset.Changes.Where(c => c.Item.ItemType == ItemType.File);

            foreach(var change in fileChanges)
            {
                ApplyDelete(change, index, initialTree);
            }

            foreach (var change in fileChanges)
            {
                ApplyAdds(change, index, initialTree);
            }

            return MakeNewLogEntry();
        }

        void ApplyDelete(Change change, GitIndexInfo index, IDictionary<string, GitObject> initialTree)
        {
            if (change.Item.DeletionId != 0)
            {
                string oldPath = Summary.Remote.GetPathInGitRepo(GetPathBeforeRename(change.Item));

                if (oldPath != null)
                {
                    Delete(oldPath, index, initialTree);
                }
            }
        }

        private void ApplyAdds(Change change, GitIndexInfo index, IDictionary<string, GitObject> initialTree)
        {
            var pathInGitRepo = Summary.Remote.GetPathInGitRepo(change.Item.ServerItem);
            if (pathInGitRepo == null || Summary.Remote.ShouldSkip(pathInGitRepo))
                return;

            if (change.ChangeType != ChangeType.Delete)
            {
                Update(change, pathInGitRepo, index, initialTree);
            }
        }

        private string GetPathBeforeRename(Item item)
        {
            Item vcsItem = item.VersionControlServer.GetItem(item.ItemId, item.ChangesetId - 1);

            if (vcsItem != null)
                return vcsItem.ServerItem;
            else
                return null;
        }

        public void Update(Change change, string pathInGitRepo, GitIndexInfo index, IDictionary<string, GitObject> initialTree)
        {
            using (var tempFile = new TemporaryFile())
            {
                change.Item.DownloadFile(tempFile);
                index.Update(GetMode(change, initialTree, pathInGitRepo),
                             UpdateDirectoryToMatchExtantCasing(pathInGitRepo, initialTree),
                             tempFile);
            }
        }

        private string GetMode(Change change, IDictionary<string, GitObject> initialTree, string pathInGitRepo)
        {
            if(initialTree.ContainsKey(pathInGitRepo) && !change.ChangeType.IncludesOneOf(ChangeType.Add))
            {
                return initialTree[pathInGitRepo].Mode;
            }
            return Mode.NewFile;
        }

        private static readonly Regex pathWithDirRegex = new Regex("(?<dir>.*)/(?<file>[^/]+)");

        private string UpdateDirectoryToMatchExtantCasing(string pathInGitRepo, IDictionary<string, GitObject> initialTree)
        {
            string newPathTail = null;
            string newPathHead = pathInGitRepo;
            while(true)
            {
                if(initialTree.ContainsKey(newPathHead))
                {
                    return MaybeAppendPath(initialTree[newPathHead].Path, newPathTail);
                }
                var pathWithDirMatch = pathWithDirRegex.Match(newPathHead);
                if(!pathWithDirMatch.Success)
                {
                    return MaybeAppendPath(newPathHead, newPathTail);
                }
                newPathTail = MaybeAppendPath(pathWithDirMatch.Groups["file"].Value, newPathTail);
                newPathHead = pathWithDirMatch.Groups["dir"].Value;
            }
        }

        private string MaybeAppendPath(string path, object tail)
        {
            if(tail != null)
                path = path + "/" + tail;
            return path;
        }

        private void Delete(string pathInGitRepo, GitIndexInfo index, IDictionary<string, GitObject> initialTree)
        {
            if(initialTree.ContainsKey(pathInGitRepo))
            {
                index.Remove(initialTree[pathInGitRepo].Path);
                Trace.WriteLine("\tD\t" + pathInGitRepo);
            }
        }

        private LogEntry MakeNewLogEntry()
        {
            var log = new LogEntry();
            var identity = tfs.GetIdentity(changeset.Committer);
            log.CommitterName = log.AuthorName = identity.DisplayName ?? "Unknown TFS user";
            log.CommitterEmail = log.AuthorEmail = identity.MailAddress ?? changeset.Committer;
            log.Date = changeset.CreationDate;
            log.Log = changeset.Comment + Environment.NewLine;
            log.ChangesetId = changeset.ChangesetId;
            return log;
        }
    }
}
