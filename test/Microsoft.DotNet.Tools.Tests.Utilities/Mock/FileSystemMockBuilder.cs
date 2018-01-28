// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.DotNet.Tools.Test.Utilities.Mock;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.Extensions.DependencyModel.Tests
{
    class FileSystemMockBuilder
    {
        private Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>();

        public string TemporaryFolder { get; set; }

        internal static IFileSystem Empty { get; } = Create().Build();

        public static FileSystemMockBuilder Create()
        {
            return new FileSystemMockBuilder();
        }

        public FileSystemMockBuilder AddFile(string name, string content = "")
        {
            _files.Add(name, Encoding.UTF8.GetBytes(content));
            return this;
        }

        public FileSystemMockBuilder AddFiles(string basePath, params string[] files)
        {
            foreach (var file in files)
            {
                AddFile(Path.Combine(basePath, file));
            }
            return this;
        }

        internal IFileSystem Build()
        {
            return new FileSystemMock(_files, TemporaryFolder);
        }

        private class FileSystemMock : IFileSystem
        {
            public FileSystemMock(Dictionary<string, byte[]> files, string temporaryFolder)
            {
                File = new FileMock(files);
                Directory = new DirectoryMock(files, temporaryFolder);
            }

            public IFile File { get; }

            public IDirectory Directory { get; }
        }

        private class FileMock : IFile
        {
            private Dictionary<string, byte[]> _files;
            
            public FileMock(Dictionary<string, byte[]> files)
            {
                _files = files;
            }

            public bool Exists(string path)
            {
                if (_files.TryGetValue(path, out var contents))
                {
                    return contents != null;
                }

                return false;
            }

            public string ReadAllText(string path)
            {
                if (_files.TryGetValue(path, out var contents))
                {
                    return Encoding.UTF8.GetString(contents);
                }

                throw new FileNotFoundException(path);
            }

            public Stream OpenRead(string path)
            {
                if (_files.TryGetValue(path, out var contents))
                {
                    return new MemoryStream(contents);
                }

                throw new FileNotFoundException(path);
            }

            public Stream OpenFile(
                string path,
                FileMode fileMode,
                FileAccess fileAccess,
                FileShare fileShare,
                int bufferSize,
                FileOptions fileOptions)
            {
                throw new NotImplementedException();
            }

            public void CreateEmptyFile(string path)
            {
                if (!DirectoryExists(path))
                {
                    throw new DirectoryNotFoundException($"directory '{Path.GetDirectoryName(path)}' was not found.");
                }
                _files.Add(path, new byte[0]);
            }

            public void WriteAllText(string path, string content)
            {
                if (!DirectoryExists(path))
                {
                    throw new DirectoryNotFoundException($"directory '{Path.GetDirectoryName(path)}' was not found.");
                }

                _files[path] = Encoding.UTF8.GetBytes(content ?? "");
            }

            public void WriteAllBytes(string path, Stream stream)
            {
                if (!DirectoryExists(path))
                {
                    throw new DirectoryNotFoundException($"directory '{Path.GetDirectoryName(path)}' was not found.");
                }

                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    _files[path] = ms.ToArray();
                }
            }

            public void Move(string source, string destination)
            {
                if (!Exists(source))
                {
                    throw new FileNotFoundException(source);
                }
                if (_files.ContainsKey(destination))
                {
                    throw new IOException($"cannot move to existing destination '{destination}'.");
                }
                if (!DirectoryExists(destination))
                {
                    throw new DirectoryNotFoundException($"destination directory '{Path.GetDirectoryName(destination)}' was not found.");
                }

                var content = _files[source];
                _files.Remove(source);
                _files[destination] = content;
            }

            public void Move(string source, string destination)
            {
                if (!Exists(source))
                {
                    throw new FileNotFoundException("source does not exist.");
                }
                if (Exists(destination))
                {
                    throw new IOException("destination exists.");
                }

                var content = _files[source];
                _files.Remove(source);
                _files[destination] = content;
            }

            public void Delete(string path)
            {
                if (!Exists(path))
                {
                    if (_files.ContainsKey(path))
                    {
                        throw new UnauthorizedAccessException($"'{path}' is a directory.");
                    }
                    return;
                }

                _files.Remove(path);
            }

            private bool DirectoryExists(string path)
            {
                var directory = Path.GetDirectoryName(path);
                if (directory != null)
                {
                    if (_files.TryGetValue(directory, out var contents))
                    {
                        return contents == null;
                    }
                }
                return false;
            }
        }

        private class DirectoryMock : IDirectory
        {
            private Dictionary<string, byte[]> _files;
            private readonly TemporaryDirectoryMock _temporaryDirectory;

            public DirectoryMock(Dictionary<string, byte[]> files, string temporaryDirectory)
            {
                _files = files;
                _temporaryDirectory = new TemporaryDirectoryMock(temporaryDirectory);
            }

            public ITemporaryDirectory CreateTemporaryDirectory()
            {
                return _temporaryDirectory;
            }

            public IEnumerable<string> EnumerateDirectories(string path)
            {
                foreach (var entry in _files.Where(kvp => kvp.Value == null && Path.GetDirectoryName(kvp.Key) == path))
                {
                    yield return entry.Key;
                }
            }

            public IEnumerable<string> EnumerateFileSystemEntries(string path)
            {
                foreach (var entry in _files.Keys.Where(k => Path.GetDirectoryName(k) == path))
                {
                    yield return entry;
                }
            }

            public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern)
            {
                if (searchPattern != "*")
                {
                    throw new NotImplementedException();
                }
                return EnumerateFileSystemEntries(path);
            }

            public string GetDirectoryFullName(string path)
            {
                throw new NotImplementedException();
            }

            public bool Exists(string path)
            {
                if (_files.TryGetValue(path, out var contents))
                {
                    return contents == null;
                }
                return false;
            }

            public void CreateDirectory(string path)
            {
                var current = path;
                while (current != null)
                {
                    if (_files.TryGetValue(path, out var contents) && contents != null)
                    {
                        throw new IOException($"'{path}' is a file.");
                    }
                    current = Path.GetDirectoryName(current);
                }

                current = path;
                while (current != null)
                {
                    _files[path] = null;
                    current = Path.GetDirectoryName(current);
                }
            }

            public void Delete(string path, bool recursive)
            {
                if (_files.TryGetValue(path, out var contents) && contents != null)
                {
                    throw new IOException($"'{path}' is a file.");
                }

                if (!recursive && Exists(path))
                {
                    if (_files.Keys.Where(k => k.StartsWith(path)).Count() > 1)
                    {
                        throw new IOException($"Directory '{path}' is not empty.");
                    }
                }

                foreach (var k in _files.Keys.Where(k => k.StartsWith(path)).ToList())
                {
                    _files.Remove(k);
                }
            }

            public void Move(string source, string destination)
            {
                if (!Exists(source))
                {
                    throw new IOException($"The source directory '{source}' does not exist.");
                }
                if (_files.ContainsKey(destination))
                {
                    throw new IOException($"The destination '{destination}' already exists.");
                }

                foreach (var kvp in _files.Where(kvp => kvp.Key.StartsWith(source)).ToList())
                {
                    var newKey = destination + kvp.Key.Substring(source.Length);

                    _files.Add(newKey, kvp.Value);
                    _files.Remove(kvp.Key);
                }
            }
        }

        private class TemporaryDirectoryMock : ITemporaryDirectoryMock
        {
            public bool DisposedTemporaryDirectory { get; private set; }

            public TemporaryDirectoryMock(string temporaryDirectory)
            {
                DirectoryPath = temporaryDirectory;
            }

            public string DirectoryPath { get; }

            public void Dispose()
            {
                DisposedTemporaryDirectory = true;
            }
        }
    }

}
