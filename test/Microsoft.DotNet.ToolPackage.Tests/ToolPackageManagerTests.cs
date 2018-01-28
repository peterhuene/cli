// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Transactions;
using FluentAssertions;
using Microsoft.DotNet.Tools.Test.Utilities;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools;
using Microsoft.Extensions.EnvironmentAbstractions;
using Xunit;

namespace Microsoft.DotNet.ToolPackage.Tests
{
    public class ToolPackageManagerTests : TestBase
    {
        private IFileSystem _fileSystem;

        public ToolPackageManagerTests()
        {
            _fileSystem = FileSystemMockBuilder.Create().Build();
            _fileSystem.Directory.CreateDirectory(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        }

        [Fact]
        public void GivenNoFeedItThrows()
        {
            var reporter = new BufferedReporter();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager = CreateToolPackageManager(toolsPath, reporter);

            Action a = () => packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"));

            a.ShouldThrow<ToolPackageException>();

            reporter.Lines.Count.Should().Be(1);
            reporter.Lines[0].Should().Contain(TestPackageId);
        }

        [Fact]
        public void GivenOfflineFeedWhenCallItCanDownloadThePackage()
        {
            var reporter = new BufferedReporter();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager = new ToolPackageManager(
                new DirectoryPath(toolsPath),
                new ProjectRestorer(reporter));

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath(GetTestLocalFeedPath()));

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            configuration.ToolExecutablePath.Should().NotContain(GetTestLocalFeedPath(), "Executable should not be still in fallbackfolder");
            configuration.ToolExecutablePath.Should().Contain(toolsPath, "Executable should be copied to tools Path");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenNugetConfigAndPackageNameAndVersionAndTargetFrameworkWhenCallItCanDownloadThePackage(
            bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            FilePath nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"),
                nugetConfig: nugetConfigPath);

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenNugetConfigAndPackageNameAndVersionAndTargetFrameworkWhenCallItCanDownloadThePackageInTransaction(
                bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            FilePath nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            string installedVersion = null;
            using (var transactionScope = new TransactionScope())
            {
                installedVersion = packageManager.InstallPackage(
                    packageId: TestPackageId,
                    packageVersion: TestPackageVersion,
                    nugetConfig: nugetConfigPath,
                    targetFramework: _testTargetframework);

                transactionScope.Complete();
            }

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenNugetConfigAndPackageNameAndVersionAndTargetFrameworkWhenCallItCreateAssetFile(
            bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"),
                nugetConfig: nugetConfigPath);

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            /*
              From mytool.dll to project.assets.json
               .dotnet/tools/packageid/version/packageid/version/mytool.dll
                      /dependency1 package id/
                      /dependency2 package id/
                      /project.assets.json
             */
            var assetJsonPath = new FilePath(configuration.ToolExecutablePath)
                .GetDirectoryPath()
                .GetParentPath()
                .GetParentPath()
                .GetParentPath()
                .GetParentPath()
                .GetParentPath()
                .WithFile("project.assets.json").Value;

            File.Exists(assetJsonPath)
                .Should()
                .BeTrue(assetJsonPath + " should be created");

            File.Delete(assetJsonPath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAllButNoNugetConfigFilePathItCanDownloadThePackage(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var uniqueTempProjectPath = GetUniqueTempProjectPathEachTest();
            var tempProjectDirectory = uniqueTempProjectPath.GetDirectoryPath();
            var nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            Directory.CreateDirectory(tempProjectDirectory.Value);

            /*
             * In test, we don't want NuGet to keep look up, so we point current directory to nugetconfig.
             */

            Directory.SetCurrentDirectory(nugetConfigPath.GetDirectoryPath().Value);

            IToolPackageManager packageManager;
            if (testMockBehaviorIsInSync)
            {
                packageManager = new ToolPackageManagerMock(toolsPath: toolsPath);
            }
            else
            {
                packageManager = new ToolPackageManager(
                    new DirectoryPath(toolsPath),
                    new ProjectRestorer(reporter));
            }

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"));

            installedVersion.Should().Be(TestPackageVersion);

            reporter.Lines.Should().BeEmpty();

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAllButNoPackageVersionItCanDownloadThePackage(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            FilePath nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"),
                nugetConfig: nugetConfigPath);

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAllButNoTargetFrameworkItCanDownloadThePackage(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            IToolPackageManager packageManager;
            if (testMockBehaviorIsInSync)
            {
                packageManager = new ToolPackageManagerMock(additionalFeeds:
                    new List<MockFeed>
                    {
                        new MockFeed
                        {
                            Type = MockFeedType.ExplicitNugetConfig,
                            Uri = nugetConfigPath.Value,
                            Packages = new List<MockFeedPackage>
                            {
                                new MockFeedPackage
                                {
                                    PackageId = TestPackageId,
                                    Version = TestPackageVersion
                                }
                            }
                        }
                    },
                    toolsPath: toolsPath);
            }
            else
            {
                packageManager = new ToolPackageManager(
                    new DirectoryPath(toolsPath),
                    new ProjectRestorer(reporter));
            }

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"),
                nugetConfig: nugetConfigPath);

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenNonExistentNugetConfigFileItThrows(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync);

            var nonExistNugetConfigFile = new FilePath("NonExistent.file");
            Action a = () =>
            {
                packageManager.InstallPackage(
                    packageId: TestPackageId,
                    packageVersion: TestPackageVersion,
                    targetFramework: _testTargetframework,
                    tempProjectPath: GetUniqueTempProjectPathEachTest(),
                    offlineFeedPath: new DirectoryPath("no such path"),
                    nugetConfig: nonExistNugetConfigFile);
            };

            a.ShouldThrow<ToolPackageException>()
                .And
                .Message.Should().Contain(string.Format(
                    CommonLocalizableStrings.NuGetConfigurationFileDoesNotExist,
                    Path.GetFullPath(nonExistNugetConfigFile.Value)));

            reporter.Lines.Should().BeEmpty();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenASourceItCanInstallThePackageFromThatSource(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(
                    toolsPath,
                    reporter,
                    testMockBehaviorIsInSync,
                    addSourceFeedWithFilePath: GetTestLocalFeedPath());

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                packageVersion: TestPackageVersion,
                targetFramework: _testTargetframework,
                tempProjectPath: GetUniqueTempProjectPathEachTest(),
                offlineFeedPath: new DirectoryPath("no such path"),
                source: GetTestLocalFeedPath());

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            var configuration = packageManager.GetToolConfiguration(TestPackageId, installedVersion);

            File.Exists(configuration.ToolExecutablePath)
                .Should()
                .BeTrue(configuration.ToolExecutablePath + " should have the executable");

            File.Delete(configuration.ToolExecutablePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenFailedRestoreItCanRollBack(bool testMockBehaviorIsInSync)
        {
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());
            var reporter = new BufferedReporter();
            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync);

            Action a = () => {
                using (var t = new TransactionScope())
                {
                    packageManager.InstallPackage(
                        packageId: "non exist package id",
                        packageVersion: TestPackageVersion,
                        targetFramework: _testTargetframework);

                    t.Complete();
                }
            };

            a.ShouldThrow<ToolPackageException>();

            AssertRollBack(toolsPath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GiveSucessRestoreButFailedOnNextStepItCanRollBack(bool testMockBehaviorIsInSync)
        {
            var nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());
            var reporter = new BufferedReporter();
            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            void FailedStepAfterSuccessRestore() => throw new GracefulException("simulated error");

            Action a = () => {
                using (var t = new TransactionScope())
                {
                    packageManager.InstallPackage(
                        packageId: TestPackageId,
                        packageVersion: TestPackageVersion,
                        targetFramework: _testTargetframework,
                        nugetConfig: nugetConfigPath);

                    FailedStepAfterSuccessRestore();
                    t.Complete();
                }
            };

            a.ShouldThrow<GracefulException>();

            AssertRollBack(toolsPath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenSecondInstallInATransactionTheFirstInstallShouldRollback(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            Action a = () => {
                using (var t = new TransactionScope())
                {
                    Action first = () => packageManager.InstallPackage(
                        packageId: TestPackageId,
                        packageVersion: TestPackageVersion,
                        targetFramework: _testTargetframework,
                        nugetConfig: nugetConfigPath);

                    first.ShouldNotThrow();

                    packageManager.InstallPackage(
                        packageId: TestPackageId,
                        packageVersion: TestPackageVersion,
                        targetFramework: _testTargetframework,
                        nugetConfig: nugetConfigPath);

                    t.Complete();
                }
            };

            a.ShouldThrow<ToolPackageException>();

            AssertRollBack(toolsPath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenSecondInstallWithoutATransactionTheFirstShouldNotRollback(bool testMockBehaviorIsInSync)
        {
            var reporter = new BufferedReporter();
            var nugetConfigPath = WriteNugetConfigFileToPointToTheFeed();
            var toolsPath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName());

            var packageManager =
                ConstructDefaultPackageManager(toolsPath, reporter, testMockBehaviorIsInSync, nugetConfigPath.Value);

            var installedVersion = packageManager.InstallPackage(
                packageId: TestPackageId,
                nugetConfig: nugetConfigPath,
                targetFramework: _testTargetframework);

            reporter.Lines.Should().BeEmpty();

            installedVersion.Should().Be(TestPackageVersion);

            Action secondCall = () => packageManager.InstallPackage(
                packageId: TestPackageId,
                nugetConfig: nugetConfigPath,
                targetFramework: _testTargetframework);

            reporter.Lines.Should().BeEmpty();

            secondCall.ShouldThrow<ToolPackageException>();

            Directory.Exists(Path.Combine(toolsPath, TestPackageId))
                .Should().BeTrue("The result of first one is still here");

            Directory.GetDirectories(Path.Combine(toolsPath, ".stage"))
                .Should().BeEmpty("nothing in stage folder, already rolled back");
        }

        private static void AssertRollBack(string toolsPath)
        {
            if (!Directory.Exists(toolsPath))
            {
                return; // nothing at all
            }

            Directory.GetFiles(toolsPath).Should().BeEmpty();
            Directory.GetDirectories(toolsPath)
                .Should().NotContain(d => !new DirectoryInfo(d).Name.Equals(".stage"),
                "no broken folder, exclude stage folder");

            Directory.GetDirectories(Path.Combine(toolsPath, ".stage"))
                .Should().BeEmpty("nothing in stage folder");
        }

        private static readonly Func<FilePath> GetUniqueTempProjectPathEachTest = () =>
        {
            var tempProjectDirectory =
                new DirectoryPath(Path.GetTempPath()).WithSubDirectories(Path.GetRandomFileName());
            var tempProjectPath =
                tempProjectDirectory.WithFile(Path.GetRandomFileName() + ".csproj");
            return tempProjectPath;
        };

        private static IToolPackageManager CreateToolPackageManager(string toolsPath, IReporter reporter)
        {
            // TODO: mock restorer
            return new ToolPackageManager(
                new DirectoryPath(toolsPath),
                new ProjectRestorer(reporter),
                _fileSystem);
        }

        // private static IToolPackageManager ConstructDefaultPackageManager(
        //     string toolsPath,
        //     IReporter reporter,
        //     bool testMockBehaviorIsInSync = false,
        //     string addNugetConfigFeedWithFilePath = null,
        //     string addSourceFeedWithFilePath = null)
        // {
        //     if (testMockBehaviorIsInSync)
        //     {
        //         if (addNugetConfigFeedWithFilePath != null)
        //         {
        //             return new ToolPackageManagerMock(additionalFeeds:
        //                 new List<MockFeed>
        //                 {
        //                     new MockFeed
        //                     {
        //                         Type = MockFeedType.ExplicitNugetConfig,
        //                         Uri = addNugetConfigFeedWithFilePath,
        //                         Packages = new List<MockFeedPackage>
        //                         {
        //                             new MockFeedPackage
        //                             {
        //                                 PackageId = TestPackageId,
        //                                 Version = TestPackageVersion
        //                             }
        //                         }
        //                     }
        //                 }, toolsPath: toolsPath);
        //         }

        //         if (addSourceFeedWithFilePath != null)
        //         {
        //             return new ToolPackageManagerMock(additionalFeeds:
        //                 new List<MockFeed>
        //                 {
        //                     new MockFeed
        //                     {
        //                         Type = MockFeedType.Source,
        //                         Uri = addSourceFeedWithFilePath,
        //                         Packages = new List<MockFeedPackage>
        //                         {
        //                             new MockFeedPackage
        //                             {
        //                                 PackageId = TestPackageId,
        //                                 Version = TestPackageVersion
        //                             }
        //                         }
        //                     }
        //                 },
        //                 toolsPath: toolsPath);
        //         }

        //         return new ToolPackageManagerMock(toolsPath: toolsPath);
        //     }

        //     return new ToolPackageManager(new DirectoryPath(toolsPath), new ProjectRestorer(reporter));
        // }

        private static FilePath WriteNugetConfigFileToPointToTheFeed()
        {
            var nugetConfigName = "nuget.config";

            var tempPathForNugetConfigWithWhiteSpace =
                Path.Combine(Path.GetTempPath(),
                    Path.GetRandomFileName() + " " + Path.GetRandomFileName());
            Directory.CreateDirectory(tempPathForNugetConfigWithWhiteSpace);

            NuGetConfig.Write(
                directory: tempPathForNugetConfigWithWhiteSpace,
                configname: nugetConfigName,
                localFeedPath: GetTestLocalFeedPath());

            return new FilePath(Path.GetFullPath(Path.Combine(tempPathForNugetConfigWithWhiteSpace, nugetConfigName)));
        }

        private static string GetTestLocalFeedPath() => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "TestAssetLocalNugetFeed");
        private readonly string _testTargetframework = BundledTargetFramework.GetTargetFrameworkMoniker();
        private const string TestPackageVersion = "1.0.4";
        private const string TestPackageId = "global.tool.console.demo";
    }
}
