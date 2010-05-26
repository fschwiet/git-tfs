using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sep.Git.Tfs.Util;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace Sep.Git.Tfs.Test.Util
{
    public class FakeException : Exception
    {
        readonly string _message;
        readonly string _stackTrace;

        public FakeException(string message, string stackTrace)
        {
            _message = message;
            _stackTrace = stackTrace;
        }

        public override string Message { get { return _message; } }
        public override string StackTrace { get { return _stackTrace; } }
    }

    /// <summary>
    /// Summary description for TfsFailTrackerTest
    /// </summary>
    [TestClass]
    public class TfsFailTrackerTest
    {
        [TestInitialize()]
        public void MyTestInitialize()
        {
         
        }

        [TestCleanup()]
        public void MyTestCleanup()
        {
        
        }

        [TestMethod]
        public void InitiallyEmpty()
        {
            TfsFailTracker sut = new TfsFailTracker();

            var errorNotes = sut.GetSummary();

            Assert.AreEqual(null, errorNotes);
        }

        private void Throw(Exception e)
        {
            throw e;
        }

        [TestMethod]
        public void ExceptionsUseExpectedNewline()
        {
            Exception someException = null;
            try
            {
                Throw(someException);
            }
            catch(Exception e)
            {
                someException = e;
            }

            Assert.IsTrue(someException.StackTrace.Contains("\r\n"));
        }

        [TestMethod]
        public void TrackFailureLoadingChangeset()
        {
            long changesetId = 123;

            string message = "someMessage";
            string stacktrace = "    at Foo\n    at Bar";
            FakeException someException = new FakeException(message, stacktrace);

            TfsFailTracker sut = new TfsFailTracker();

            sut.TrackFailureLoadingChangeset(changesetId, someException);

            var results = sut.GetSummary();

            string expectedResult =   "Error loading changeset " + changesetId.ToString() + "\r\n" +
                                      "    Message: " + message + "\r\n" +
                                      "        at Foo\r\n" +
                                      "        at Bar\r\n";
            Assert.AreEqual(
                expectedResult, results);
        }

        [TestMethod]
        public void CanRecordFailureForChange()
        {
            long changesetId = 123;

            ChangeType changeType = ChangeType.Rename;
            string serverPath = @"$\tfs\some\file.cs";
            string reason = "unable to delete rename source";

            TfsFailTracker sut = new TfsFailTracker();

            sut.TrackFailureLoadingChange(changesetId, changeType, serverPath, reason);

            var results = sut.GetSummary();

            var expectedResult =
                "Error loading changeset " + changesetId.ToString() + "\r\n" +
                "    " + reason + " for Rename of " + serverPath + "\r\n";

            Assert.AreEqual(expectedResult, results);
        }

        [TestMethod]
        public void FailuresRecordedForSomeChangesetShareInto()
        {
            long changesetId = 123;

            ChangeType changeType = ChangeType.Rename;
            string serverPath = @"$\tfs\some\file.cs";
            string reason = "unable to delete rename source";

            ChangeType changeType2 = ChangeType.Merge;
            string serverPath2 = @"$\tfs\some\file2.cs";
            string reason2 = "unable to foo";

            TfsFailTracker sut = new TfsFailTracker();

            sut.TrackFailureLoadingChange(changesetId, changeType, serverPath, reason);
            sut.TrackFailureLoadingChange(changesetId, changeType2, serverPath2, reason2);

            var results = sut.GetSummary();

            var expectedResult =
                "Error loading changeset " + changesetId.ToString() + "\r\n" +
                "    " + reason + " for Rename of " + serverPath + "\r\n" +
                "    " + reason2 + " for Merge of " + serverPath2 + "\r\n" ;

            Assert.AreEqual(expectedResult, results);
        }

        [TestMethod]
        public void MostExceptionsAreNotRecoverable()
        {
            bool result = TfsFailTracker.ShouldHaltOnError(new Exception());

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void RepositoryNotFoundException_isRecoverable()
        {
            bool result = TfsFailTracker.ShouldHaltOnError(new RepositoryNotFoundException());

            Assert.IsTrue(result);
        }
    }
}
