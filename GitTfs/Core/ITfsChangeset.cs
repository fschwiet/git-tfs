﻿using Sep.Git.Tfs.Util;

namespace Sep.Git.Tfs.Core
{
    public interface ITfsChangeset
    {
        TfsChangesetInfo Summary { get; }
        LogEntry Apply(string lastCommit, GitIndexInfo index, TfsFailTracker failTracker);
    }
}
