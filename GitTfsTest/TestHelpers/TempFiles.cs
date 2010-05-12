using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;


namespace Sep.Git.Tfs.Test.TestHelpers
{
    public class TempFiles
    {
        DirectoryInfo _basePath;
        int _newFileIndex;

        public TempFiles()
        {
            string basePathFilename = Path.GetTempFileName();

            if (File.Exists(basePathFilename))
                File.Delete(basePathFilename);

            if (Directory.Exists(basePathFilename))
                Directory.Delete(basePathFilename);

            Directory.CreateDirectory(basePathFilename);
            _basePath = new DirectoryInfo(basePathFilename);
            _newFileIndex = 0;
        }

        public string GetTemporaryFilename()
        {
            string newName = Path.Combine(_basePath.FullName, "tempFile" + _newFileIndex.ToString() + ".tmp");
            _newFileIndex++;

            Assert.IsFalse(File.Exists(newName));

            return newName;
        }

        public string GetTemporaryDirectory()
        {
            string newName = Path.Combine(_basePath.FullName, "tempDir" + _newFileIndex.ToString());
            _newFileIndex++;

            Assert.IsFalse(Directory.Exists(newName));

            return newName;
        }

        public void Cleanup()
        {
            Action<FileSystemInfo> clearPath = null;
            
            clearPath = delegate(FileSystemInfo fsi)
            {
                fsi.Attributes = FileAttributes.Normal;
                var di = fsi as DirectoryInfo;
                if (di != null)
                {
                    foreach (var dirInfo in di.GetFileSystemInfos())
                        clearPath(dirInfo);
                };

                fsi.Delete();
            };

            foreach (var fsi in new DirectoryInfo(_basePath.FullName).GetFileSystemInfos())
                clearPath(fsi);

            Directory.Delete(_basePath.FullName, true);

            if (Directory.Exists(_basePath.FullName))
                throw new Exception("Stray filehandle open.  Leaked temporary directory at '" + _basePath.FullName + ".");
        }
    }
}

