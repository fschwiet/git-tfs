using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Sep.Git.Tfs.Commands;
using Sep.Git.Tfs.Util;

namespace Sep.Git.Tfs.Core
{
    public class GitTfsRemote : IGitTfsRemote
    {
        private static readonly Regex isInDotGit = new Regex("(?:^|/)\\.git(?:/|$)");
        private static readonly Regex treeShaRegex = new Regex("^tree (" + GitTfsConstants.Sha1 + ")");

        private readonly Globals globals;
        private readonly TextWriter stdout;
        private readonly RemoteOptions remoteOptions;
        private long? maxChangesetId;
        private string maxCommitHash;

        Util.TfsFailTracker _failTracker;

        public GitTfsRemote(RemoteOptions remoteOptions, Globals globals, ITfsHelper tfsHelper, TextWriter stdout)
        {
            this.remoteOptions = remoteOptions;
            this.globals = globals;
            this.stdout = stdout;
            Tfs = tfsHelper;
            _failTracker = new TfsFailTracker();
        }

        public string Id { get; set; }
        public string TfsRepositoryPath { get; set; }
        public string IgnoreRegexExpression { get; set; }
        public IGitRepository Repository { get; set; }
        public ITfsHelper Tfs { get; set; }

        public long MaxChangesetId
        {
            get { InitHistory(); return maxChangesetId.Value; }
            set { maxChangesetId = value; }
        }

        public string MaxCommitHash
        {
            get { InitHistory(); return maxCommitHash; }
            set { maxCommitHash = value; }
        }

        private void InitHistory()
        {
            if (maxChangesetId == null)
            {
                var mostRecentUpdate = Repository.GetParentTfsCommits(RemoteRef).FirstOrDefault();
                if (mostRecentUpdate != null)
                {
                    MaxCommitHash = mostRecentUpdate.GitCommit;
                    MaxChangesetId = mostRecentUpdate.ChangesetId;
                }
                else
                {
                    MaxChangesetId = 0;
                }
            }
        }

        private string Dir
        {
            get
            {
                return Ext.CombinePaths(globals.GitDir, "tfs", Id);
            }
        }

        private string IndexFile
        {
            get
            {
                return Path.Combine(Dir, "index");
            }
        }

        private string WorkingDirectory
        {
            get
            {
                return Path.Combine(Dir, "workspace");
            }
        }

        public bool ShouldSkip(string path)
        {
            return IsInDotGit(path) ||
                   IsIgnored(path, IgnoreRegexExpression) ||
                   IsIgnored(path, remoteOptions.IgnoreRegex);
        }

        private bool IsIgnored(string path, string expression)
        {
            return expression != null && new Regex(expression).IsMatch(path);
        }

        private bool IsInDotGit(string path)
        {
            return isInDotGit.IsMatch(path);
        }

        public string GetPathInGitRepo(string tfsPath)
        {
            if(!tfsPath.StartsWith(TfsRepositoryPath)) return null;
            tfsPath = tfsPath.Substring(TfsRepositoryPath.Length);
            while (tfsPath.StartsWith("/"))
                tfsPath = tfsPath.Substring(1);
            return tfsPath;
        }

        public string Fetch(string maxTree)
        {
            foreach (var changeset in FetchChangesets())
            {
                AssertTemporaryIndexClean(MaxCommitHash);
                var log = Apply(MaxCommitHash, changeset);

                // Only apply changesets that affect the source (changing the table hash)
                if (log.Tree != maxTree)
                {
                    UpdateRef(Commit(log), changeset.Summary.ChangesetId);
                    maxTree = log.Tree;
                }
                else
                {
                    if (!string.IsNullOrEmpty(MaxCommitHash))
                    {
                        SetNoteToIndicateLastConsideredChangeset(MaxCommitHash, changeset.Summary.ChangesetId);

                        FlushFailRecordsToNote(MaxCommitHash, _failTracker);
                    }
                }

                DoGcIfNeeded();
            }

            return maxTree;
        }

        private IEnumerable<ITfsChangeset> FetchChangesets()
        {
            long startChangeset;

            if (MaxChangesetId == 0)
            {
                if (TfsRepositoryPath == "$/")
                    startChangeset = MaxChangesetId + 1;
                else
                    startChangeset = Tfs.GetFirstChangsetForPath(TfsRepositoryPath);
            }
            else
            {
                startChangeset = MaxChangesetId + 1;
            }

            if (MaxCommitHash != null)
            {
                long? lastChangsetChecked = GetLastConsideredChangeset(MaxCommitHash);

                if (lastChangsetChecked.HasValue)
                {
                    if (lastChangsetChecked.Value >= startChangeset)
                        startChangeset = lastChangsetChecked.Value + 1;
                }
            }

            Trace.WriteLine(RemoteRef + ": Getting changesets from " + startChangeset + " to current ...", "info");

            foreach(ITfsChangeset tfsChangeset in Tfs.GetAllChangesetsStartingAt(startChangeset, _failTracker))
            {
                tfsChangeset.Summary.Remote = this;
                yield return tfsChangeset;
            }
        }

        private void UpdateRef(string commitHash, long changesetId)
        {
            MaxCommitHash = commitHash;
            MaxChangesetId = changesetId;
            Repository.CommandNoisy("update-ref", "-m", "C" + MaxChangesetId, RemoteRef, MaxCommitHash);
            Repository.CommandNoisy("update-ref", TagPrefix + "C" + MaxChangesetId, MaxCommitHash);
            LogCurrentMapping();
        }

        private void LogCurrentMapping()
        {
            stdout.WriteLine("C" + MaxChangesetId + " = " + MaxCommitHash);
        }

        private string TagPrefix
        {
            get { return "refs/tags/tfs/" + Id + "/"; }
        }

        public string RemoteRef
        {
            get { return "refs/remotes/tfs/" + Id; }
        }

        private void DoGcIfNeeded()
        {
            Trace.WriteLine("GC Countdown: " + globals.GcCountdown);
            if(--globals.GcCountdown < 0)
            {
                globals.GcCountdown = globals.GcPeriod;
                Repository.CommandNoisy("gc", "--auto");
            }
        }

        private void AssertTemporaryIndexClean(string treeish)
        {
            if(string.IsNullOrEmpty(treeish))
            {
                if (File.Exists(IndexFile)) File.Delete(IndexFile);
                return;
            }
            WithTemporaryIndex(() => AssertIndexClean(treeish));
        }

        private void AssertIndexClean(string treeish)
        {
            if (!File.Exists(IndexFile)) Repository.CommandNoisy("read-tree", treeish);
            var currentTree = Repository.CommandOneline("write-tree");
            var expectedCommitInfo = Repository.Command("cat-file", "commit", treeish);
            var expectedCommitTree = treeShaRegex.Match(expectedCommitInfo).Groups[1].Value;
            if (expectedCommitTree != currentTree)
            {
                Trace.WriteLine("Index mismatch: " + expectedCommitTree + " != " + currentTree);
                Trace.WriteLine("rereading " + treeish);
                File.Delete(IndexFile);
                Repository.CommandNoisy("read-tree", treeish);
                currentTree = Repository.CommandOneline("write-tree");
                if (expectedCommitTree != currentTree)
                {
                    throw new Exception("Unable to create a clean temporary index: trees (" + treeish + ") " + expectedCommitTree + " != " + currentTree);
                }
            }
        }

        private LogEntry Apply(string lastCommit, ITfsChangeset changeset)
        {
            LogEntry result = null;
            WithTemporaryIndex(
                () => GitIndexInfo.Do(Repository, index => result = changeset.Apply(lastCommit, index, _failTracker)));
            WithTemporaryIndex(
                () => result.Tree = Repository.CommandOneline("write-tree"));
            if(!String.IsNullOrEmpty(lastCommit)) result.CommitParents.Add(lastCommit);
            return result;
        }

        private string Commit(LogEntry logEntry)
        {
            string commitHash = null;
            WithCommitHeaderEnv(logEntry, () => commitHash = WriteCommit(logEntry));
            // TODO (maybe): StoreChangesetMetadata(commitInfo);
            return commitHash;
        }

        private string WriteCommit(LogEntry logEntry)
        {
            // TODO (maybe): encode logEntry.Log according to 'git config --get i18n.commitencoding', if specified
            //var commitEncoding = Repository.CommandOneline("config", "i18n.commitencoding");
            //var encoding = LookupEncoding(commitEncoding) ?? Encoding.UTF8;
            string commitHash = null;
            Repository.CommandInputOutputPipe((procIn, procOut) =>
                                                  {
                                                      procIn.WriteLine(logEntry.Log);
                                                      procIn.WriteLine(GitTfsConstants.TfsCommitInfoFormat, Tfs.Url,
                                                                       TfsRepositoryPath, logEntry.ChangesetId);
                                                      procIn.Close();
                                                      commitHash = ParseCommitInfo(procOut.ReadToEnd());
                                                  }, BuildCommitCommand(logEntry));
            return commitHash;
        }

        private string[] BuildCommitCommand(LogEntry logEntry)
        {
            var tree = logEntry.Tree ?? GetTemporaryIndexTreeSha();
            tree.AssertValidSha();
            var commitCommand = new List<string> { "commit-tree", tree };
            foreach (var parent in logEntry.CommitParents)
            {
                commitCommand.Add("-p");
                commitCommand.Add(parent);
            }
            return commitCommand.ToArray();
        }

        private string GetTemporaryIndexTreeSha()
        {
            string tree = null;
            WithTemporaryIndex(() => tree = Repository.CommandOneline("write-tree"));
            return tree;
        }

        private string ParseCommitInfo(string commitTreeOutput)
        {
            return commitTreeOutput.Trim();
        }

        //private Encoding LookupEncoding(string encoding)
        //{
        //    if(encoding == null)
        //        return null;
        //    throw new NotImplementedException("Need to implement encoding lookup for " + encoding);
        //}

        private void WithCommitHeaderEnv(LogEntry logEntry, Action action)
        {
            WithTemporaryEnvironment(action, new Dictionary<string, string>
                                                 {
                                                     {"GIT_AUTHOR_NAME", logEntry.AuthorName},
                                                     {"GIT_AUTHOR_EMAIL", logEntry.AuthorEmail},
                                                     {"GIT_AUTHOR_DATE", logEntry.Date.FormatForGit()},
                                                     {"GIT_COMMITTER_DATE", logEntry.Date.FormatForGit()},
                                                     {"GIT_COMMITTER_NAME", logEntry.CommitterName ?? logEntry.AuthorName},
                                                     {"GIT_COMMITTER_EMAIL", logEntry.CommitterEmail ?? logEntry.AuthorEmail}
                                                 });
        }

        private void WithTemporaryIndex(Action action)
        {
            WithTemporaryEnvironment(() =>
                                         {
                                             Directory.CreateDirectory(Path.GetDirectoryName(IndexFile));
                                             action();
                                         }, new Dictionary<string, string> {{"GIT_INDEX_FILE", IndexFile}});
        }

        private void WithTemporaryEnvironment(Action action, IDictionary<string, string> newEnvironment)
        {
            var oldEnvironment = new Dictionary<string, string>();
            PushEnvironment(newEnvironment, oldEnvironment);
            try
            {
                action();
            }
            finally
            {
                PushEnvironment(oldEnvironment);
            }
        }

        private void PushEnvironment(IDictionary<string, string> desiredEnvironment)
        {
            PushEnvironment(desiredEnvironment, new Dictionary<string, string>());
        }

        private void PushEnvironment(IDictionary<string, string> desiredEnvironment, IDictionary<string, string> oldEnvironment)
        {
            foreach(var key in desiredEnvironment.Keys)
            {
                oldEnvironment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, desiredEnvironment[key]);
            }
        }

        public void Shelve(string shelvesetName, string head, TfsChangesetInfo parentChangeset)
        {
            Tfs.WithWorkspace(WorkingDirectory, this, parentChangeset,
                              workspace => Shelve(shelvesetName, head, parentChangeset, workspace));
        }

        private void Shelve(string shelvesetName, string head, TfsChangesetInfo parentChangeset, ITfsWorkspace workspace)
        {
            foreach (var change in Repository.GetChangedFiles(parentChangeset.GitCommit, head))
            {
                change.Apply(workspace);
            }
            workspace.Shelve(shelvesetName);
        }

        readonly Regex _regexForLastChangesetNot =
            new Regex(@"(\\n)?git-tfs-(?<id>(\d)+)-islastchangesetchecked", RegexOptions.Compiled);

        void SetNoteToIndicateLastConsideredChangeset(string commit, long changeset)
        {
            string note = Repository.GetNote(commit) ?? "";

            note = _regexForLastChangesetNot.Replace(note, "");

            string noteToAdd = "git-tfs-" + changeset + "-islastchangesetchecked";

            if (string.IsNullOrEmpty(note))
            {
                note = noteToAdd;
            }
            else
            {
                note = note + "\n" + noteToAdd;
            }

            Repository.SetNote(commit, note);
        }

        long? GetLastConsideredChangeset(string commit)
        {
            string note = Repository.GetNote(commit) ?? "";

            Match m = _regexForLastChangesetNot.Match(note);

            if (!m.Success)
                return null;
            else
                return int.Parse(m.Groups["id"].Value);
        }

        void FlushFailRecordsToNote(string commit, TfsFailTracker failTracker)
        {
            string failRecord = failTracker.GetSummary();

            if (failRecord != null)
            {
                string note = Repository.GetNote(commit);

                if (note == null)
                    note = failRecord;
                else
                    note = note + "\n" + failRecord;

                Repository.SetNote(commit, note);

                failTracker.Reset();
            }
        }
    }
}
