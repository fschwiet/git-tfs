using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sep.Git.Tfs.Core;
using Sep.Git.Tfs.Test.TestHelpers;
using GitSharp;
using StructureMap;
using Repository = GitSharp.Repository;

namespace Sep.Git.Tfs.Test.Core
{
    /// <summary>
    /// Summary description for GitRepository
    /// </summary>
    [TestClass]
    public class GitRepositoryTest
    {
        TempFiles _tempFiles;
        private string _repoPath;
        private string _gitPath;
        private GitRepository _gitRepository;
        private Repository _csharpRepository;

        [TestInitialize()]
        public void MyTestInitialize() {
            _tempFiles = new TempFiles();
            _repoPath = _tempFiles.GetTemporaryDirectory();
            _gitPath = Path.Combine(_repoPath, ".git");
            Directory.CreateDirectory(_gitPath);

            Program.Initialize();
            _gitRepository = new GitRepository(ObjectFactory.GetInstance<TextWriter>(), _gitPath);
            _csharpRepository = new Repository(_gitPath);
        }


        [TestCleanup()]
        public void MyTestCleanup() {

            _tempFiles.Cleanup();
        }


        public void TestCanSetNote(string note)
        {
            TestCanSetNote(note, note);    
        }

        public void TestCanSetNote(string note, string expectdResult)
        {
            GitSharp.Commands.Init(_repoPath);

            string newFilePath = Path.Combine(_repoPath, "helloWorld");
            using (StreamWriter newFileStream = new StreamWriter(newFilePath)) {
                newFileStream.WriteLine("Hello world.");
                newFileStream.Flush();
                newFileStream.Close();
            }

            _csharpRepository.Index.Add(newFilePath);
            Commit commit = _csharpRepository.Index.CommitChanges("first commit", new Author("author", "author@gmail.com"));

            string commitName = commit.Hash;

            _gitRepository.SetNote(commitName, note);

            var result = _gitRepository.GetNote(commitName);

            Assert.AreEqual(expectdResult, result);
        }

        [TestMethod]
        public void SetPlainNote()
        {
            TestCanSetNote("someString");
        }

        [TestMethod]
        public void SetNoteWithQuotes()
        {
            TestCanSetNote("someString \\ \" \\\"");
        }

        [TestMethod]
        public void SetNoteWithNewline()
        {
            TestCanSetNote("someString\nsomeOtherString");
        }

        [TestMethod]
        public void SetNoteWithNewlineCausesWhitespaceToBeTrimmedBeforeNewline()
        {
            TestCanSetNote("someString  \n someOtherString", "someString\n someOtherString");
        }

        [TestMethod]
        public void SetNoteTrimsWhitespaceUpToFirstNewline()
        {
            TestCanSetNote("  \n  hi", "  hi");
        }

        [TestMethod]
        public void SetNoteTrimsWhitespaceUpToFirstNonwhitespace()
        {
            TestCanSetNote("  hi", "hi");
        }
    }
}
