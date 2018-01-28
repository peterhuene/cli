// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Transactions;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.ToolPackage;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.Tools.Tests.ComponentMocks
{
    internal class ToolPackageManagerMock : IToolPackageManager
    {
        public const string FakeEntrypointName = "SimulatorEntryPoint.dll";
        public const string FakeCommandName = "SimulatorCommand";

        private readonly DirectoryPath _toolsPath;
        private readonly Action _beforeInstall;
        private readonly Action _duringInstall;
        private static IFileSystem _fileSystem;
        private List<MockFeed> _mockFeeds;

        public ToolPackageManagerMock(
            IFileSystem fileSystem = null,
            bool useDefaultFeed = true,
            IEnumerable<MockFeed> additionalFeeds = null,
            Action beforeInstall = null,
            Action duringInstall = null,
            string toolsPath = null)
        {
            _toolsPath = new DirectoryPath(toolsPath ?? "toolsPath");
            _beforeInstall = beforeInstall ?? (() => {});
            _duringInstall = duringInstall ?? (() => {});
            _fileSystem = fileSystem ?? new FileSystemWrapper();
            _mockFeeds = new List<MockFeed>();

            if (useDefaultFeed)
            {
                _mockFeeds.Add(new MockFeed
                {
                    Type = MockFeedType.FeedFromLookUpNugetConfig,
                        Packages = new List<MockFeedPackage>
                        {
                            new MockFeedPackage
                            {
                                PackageId = "global.tool.console.demo",
                                Version = "1.0.4"
                            }
                        }
                });
            }

            if (additionalFeeds != null)
            {
                _mockFeeds.AddRange(additionalFeeds);
            }
        }

        public DirectoryPath GetPackageRootDirectory(string packageId)
        {
            return _toolsPath.WithSubDirectories(packageId);
        }

        public DirectoryPath GetPackageDirectory(string packageId, string packageVersion)
        {
            return GetPackageRootDirectory(packageId).WithSubDirectories(packageVersion);
        }

        public string InstallPackage(
            string packageId,
            string packageVersion = null,
            string targetFramework = null,
            FilePath? tempProjectPath = null,
            DirectoryPath? offlineFeedPath = null,
            FilePath? nugetConfig = null,
            string source = null,
            string verbosity = null)
        {
            using (var scope = new TransactionScope())
            {
                var packageRootDirectory = GetPackageRootDirectory(packageId).Value;

                string rollbackDirectory = null;
                TransactionEnlistment.Enlist(rollback: () => {
                    if (rollbackDirectory != null && _fileSystem.Directory.Exists(rollbackDirectory))
                    {
                        _fileSystem.Directory.Delete(rollbackDirectory, true);
                    }
                    if (_fileSystem.Directory.Exists(packageRootDirectory) &&
                        !_fileSystem.Directory.EnumerateFileSystemEntries(packageRootDirectory).Any())
                    {
                        _fileSystem.Directory.Delete(packageRootDirectory, false);
                    }
                });

                _beforeInstall();

                PickFeedByNugetConfig(nugetConfig);
                PickFeedBySource(source);

                var package = GetPackage(packageId, packageVersion);

                packageVersion = package.Version;
                targetFramework = targetFramework ?? "targetframework";

                var stageDirectory = _toolsPath.WithSubDirectories(".stage", Path.GetRandomFileName());
                _fileSystem.Directory.CreateDirectory(stageDirectory.Value);
                rollbackDirectory = stageDirectory.Value;

                var fakeExecutableSubDirectory = Path.Combine(
                    packageId,
                    packageVersion,
                    "morefolders",
                    "tools",
                    targetFramework);
                var fakeExecutablePath = Path.Combine(fakeExecutableSubDirectory, FakeEntrypointName);

                _fileSystem.Directory.CreateDirectory(Path.Combine(stageDirectory.Value, fakeExecutableSubDirectory));
                _fileSystem.File.CreateEmptyFile(Path.Combine(stageDirectory.Value, fakeExecutablePath));
                _fileSystem.File.WriteAllText(
                    stageDirectory.WithFile("project.assets.json").Value,
                    fakeExecutablePath);

                _duringInstall();

                var packageDirectory = GetPackageDirectory(packageId, packageVersion);
                if (_fileSystem.Directory.Exists(packageDirectory.Value))
                {
                    throw new ToolPackageException(
                        string.Format(
                            CommonLocalizableStrings.ToolPackageConflictPackageId,
                            packageId,
                            packageVersion));
                }

                _fileSystem.Directory.CreateDirectory(packageRootDirectory);
                _fileSystem.Directory.Move(stageDirectory.Value, packageDirectory.Value);
                rollbackDirectory = packageDirectory.Value;

                scope.Complete();
                return packageVersion;
            }
        }

        public IEnumerable<string> GetInstalledVersions(string packageId)
        {
            var packageRootDirectory = GetPackageRootDirectory(packageId);
            if (!_fileSystem.Directory.Exists(packageRootDirectory.Value))
            {
                yield break;
            }

            foreach (var subdirectory in _fileSystem.Directory.EnumerateFileSystemEntries(packageRootDirectory.Value))
            {
                yield return Path.GetFileName(subdirectory);
            }
        }

        public ToolConfiguration GetToolConfiguration(string packageId, string packageVersion)
        {
            try
            {
                var packageDirectory = GetPackageDirectory(packageId, packageVersion);
                return new ToolConfiguration(FakeCommandName, FakeEntrypointName)
                    {
                        ToolExecutablePath = Path.Combine(
                        packageDirectory.Value,
                        _fileSystem.File.ReadAllText(
                            Path.Combine(
                                packageDirectory.Value,
                                "project.assets.json")))
                    };
            }
            catch (IOException ex)
            {
                throw new ToolPackageException(
                    string.Format(
                        CommonLocalizableStrings.FailedToRetrieveToolConfiguration,
                        packageId,
                        ex.Message),
                    ex);
            }
        }

        public void UninstallPackage(string packageId, string packageVersion)
        {
            using (var scope = new TransactionScope())
            {
                var packageRootDirectory = GetPackageRootDirectory(packageId);
                var packageDirectory = GetPackageDirectory(packageId, packageVersion);
                string tempPackageDirectory = null;

                TransactionEnlistment.Enlist(
                    commit: () => {
                        if (tempPackageDirectory != null)
                        {
                            _fileSystem.Directory.Delete(tempPackageDirectory, true);
                        }
                    },
                    rollback: () => {
                        if (tempPackageDirectory != null)
                        {
                            _fileSystem.Directory.CreateDirectory(packageRootDirectory.Value);
                            _fileSystem.Directory.Move(tempPackageDirectory, packageDirectory.Value);
                        }
                    });

                if (_fileSystem.Directory.Exists(packageDirectory.Value))
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                    _fileSystem.Directory.Move(packageDirectory.Value, tempPath);
                    tempPackageDirectory = tempPath;
                }

                if (_fileSystem.Directory.Exists(packageRootDirectory.Value) &&
                    !_fileSystem.Directory.EnumerateFileSystemEntries(packageRootDirectory.Value).Any())
                {
                    _fileSystem.Directory.Delete(packageRootDirectory.Value, false);
                }

                scope.Complete();
            }
        }

        private MockFeedPackage GetPackage(string packageId, string packageVersion = null)
        {
            var package = _mockFeeds
                .SelectMany(f => f.Packages)
                .Where(p => MatchPackageVersion(p, packageId, packageVersion)).OrderByDescending(p => p.Version)
                .FirstOrDefault();

            if (package == null)
            {
                throw new ToolPackageException("simulated cannot find package");
            }

            return package;
        }

        private void PickFeedBySource(string source)
        {
            if (source != null)
            {
                var feed = _mockFeeds.SingleOrDefault(
                    f => f.Type == MockFeedType.Source
                         && f.Uri == source);

                if (feed != null)
                {
                    _mockFeeds = new List<MockFeed>
                    {
                        feed
                    };
                }
                else
                {
                    _mockFeeds = new List<MockFeed>();
                }
            }
        }

        private void PickFeedByNugetConfig(FilePath? nugetconfig)
        {
            if (nugetconfig != null)
            {
                if (!_fileSystem.File.Exists(nugetconfig.Value.Value))
                {
                    throw new ToolPackageException(
                        string.Format(CommonLocalizableStrings.NuGetConfigurationFileDoesNotExist,
                            Path.GetFullPath(nugetconfig.Value.Value)));
                }

                var feed = _mockFeeds.SingleOrDefault(
                    f => f.Type == MockFeedType.ExplicitNugetConfig && f.Uri == nugetconfig.Value.Value);

                if (feed != null)
                {
                    _mockFeeds = new List<MockFeed>
                    {
                        feed
                    };
                }
                else
                {
                    _mockFeeds = new List<MockFeed>();
                }
            }
        }

        private static bool MatchPackageVersion(MockFeedPackage p, string packageId, string packageVersion)
        {
            if (packageVersion == null)
            {
                return p.PackageId == packageId;
            }
            return p.PackageId == packageId && p.Version == packageVersion;
        }
    }
}
