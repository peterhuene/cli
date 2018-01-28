// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Transactions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.TestFramework;
using Microsoft.DotNet.ToolPackage;
using Microsoft.DotNet.Tools;
using Microsoft.DotNet.Tools.Test.Utilities;
using Microsoft.DotNet.Tools.Tests.ComponentMocks;
using Microsoft.Extensions.EnvironmentAbstractions;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.DotNet.ShellShim.Tests
{
    public class ShellShimManagerTests : TestBase
    {
        private readonly ITestOutputHelper _output;

        public ShellShimManagerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [WindowsOnlyTheory]
        [InlineData("my_native_app.exe", null)]
        [InlineData("./my_native_app.js", "nodejs")]
        [InlineData(@"C:\tools\my_native_app.dll", "dotnet")]
        public void GivenAnRunnerOrEntryPointItCanCreateConfig(string entryPointPath, string runner)
        {
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();
            var shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));

            var tmpFile = new FilePath(Path.Combine(cleanFolderUnderTempRoot, Path.GetRandomFileName()));

            shellShimManager.CreateConfigFile(tmpFile, new FilePath(entryPointPath), runner);

            new FileInfo(tmpFile.Value).Should().Exist();

            var generated = XDocument.Load(tmpFile.Value);

            generated.Descendants("appSettings")
                .Descendants("add")
                .Should()
                .Contain(e => e.Attribute("key").Value == "runner" && e.Attribute("value").Value == (runner ?? string.Empty))
                .And
                .Contain(e => e.Attribute("key").Value == "entryPoint" && e.Attribute("value").Value == entryPointPath);
        }

        [Fact]
        public void GivenAnExecutablePathItCanGenerateShimFile()
        {
            var outputDll = MakeHelloWorldExecutableDll();

            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();
            var shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();

            shellShimManager.CreateShim(outputDll, shellCommandName);

            var stdOut = ExecuteInShell(shellCommandName, cleanFolderUnderTempRoot);

            stdOut.Should().Contain("Hello World");
        }
        
        [Fact]
        public void GivenAnExecutablePathItCanGenerateShimFileInTransaction()
        {
            var outputDll = MakeHelloWorldExecutableDll();

            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();
            var shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();

            using (var transactionScope = new TransactionScope())
            {
                shellShimManager.CreateShim(outputDll, shellCommandName);
                transactionScope.Complete();
            }

            var stdOut = ExecuteInShell(shellCommandName, cleanFolderUnderTempRoot);

            stdOut.Should().Contain("Hello World");
        }

        [Fact]
        public void GivenAnExecutablePathDirectoryThatDoesNotExistItCanGenerateShimFile()
        {
            var outputDll = MakeHelloWorldExecutableDll();
            var extraNonExistDirectory = Path.GetRandomFileName();
            var shellShimManager = new ShellShimManager(new DirectoryPath(Path.Combine(TempRoot.Root, extraNonExistDirectory)));
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();

            Action a = () => shellShimManager.CreateShim(outputDll, shellCommandName);

            a.ShouldNotThrow<DirectoryNotFoundException>();
        }

        [Theory]
        [InlineData("arg1 arg2", new[] { "arg1", "arg2" })]
        [InlineData(" \"arg1 with space\" arg2", new[] { "arg1 with space", "arg2" })]
        [InlineData(" \"arg with ' quote\" ", new[] { "arg with ' quote" })]
        public void GivenAShimItPassesThroughArguments(string arguments, string[] expectedPassThru)
        {
            var outputDll = MakeHelloWorldExecutableDll();

            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();
            var shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();

            shellShimManager.CreateShim(outputDll, shellCommandName);

            var stdOut = ExecuteInShell(shellCommandName, cleanFolderUnderTempRoot, arguments);

            for (int i = 0; i < expectedPassThru.Length; i++)
            {
                stdOut.Should().Contain($"{i} = {expectedPassThru[i]}");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAnExistingExecutablePathItShouldReportItExists(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();
            MakeNameConflictingCommand(cleanFolderUnderTempRoot, shellCommandName);

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(cleanFolderUnderTempRoot));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAnExecutablePathWithExistingSameNameShimItRollsBack(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();

            var pathToShim = GetNewCleanFolderUnderTempRoot();
            MakeNameConflictingCommand(pathToShim, shellCommandName);

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(pathToShim));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(pathToShim));
            }

            Action a = () =>
            {
                using (var t = new TransactionScope())
                {
                    shellShimManager.CreateShim(new FilePath("dummy.dll"), shellCommandName);

                    t.Complete();
                }
            };
            a.ShouldThrow<ShellShimException>();

            Directory.GetFiles(pathToShim).Should().HaveCount(1, "there is only intent conflicted command");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAnExecutablePathErrorHappensItRollsBack(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();

            var pathToShim = GetNewCleanFolderUnderTempRoot();

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(pathToShim));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(pathToShim));
            }
               
            Action intendedError = () => throw new ToolPackageException();

            Action a = () =>
            {
                using (var t = new TransactionScope())
                {
                    shellShimManager.CreateShim(new FilePath("dummy.dll"), shellCommandName);

                    intendedError();
                    t.Complete();
                }
            };
            a.ShouldThrow<ToolPackageException>();

            Directory.GetFiles(pathToShim).Should().BeEmpty();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenANonexistentShimItReportsItDoesNotExist(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(cleanFolderUnderTempRoot));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenANonexistentShimRemoveDoesNotThrow(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(cleanFolderUnderTempRoot));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);

            shellShimManager.RemoveShim(shellCommandName);

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAnInstalledShimRemoveDeletesTheShimFiles(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(cleanFolderUnderTempRoot));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);

            shellShimManager.CreateShim(new FilePath("dummy.dll"), shellCommandName);
            shellShimManager.ShimExists(shellCommandName).Should().Be(true);

            shellShimManager.RemoveShim(shellCommandName);
            shellShimManager.ShimExists(shellCommandName).Should().Be(false);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAnInstalledShimRemoveRollsbackIfTransactionIsAborted(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(cleanFolderUnderTempRoot));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);

            shellShimManager.CreateShim(new FilePath("dummy.dll"), shellCommandName);
            shellShimManager.ShimExists(shellCommandName).Should().Be(true);

            using (var scope = new TransactionScope())
            {
                shellShimManager.RemoveShim(shellCommandName);
                shellShimManager.ShimExists(shellCommandName).Should().Be(false);
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenAnInstalledShimRemoveCommitsIfTransactionIsCompleted(bool testMockBehaviorIsInSync)
        {
            var shellCommandName = nameof(ShellShimManagerTests) + Path.GetRandomFileName();
            var cleanFolderUnderTempRoot = GetNewCleanFolderUnderTempRoot();

            IShellShimManager shellShimManager;
            if (testMockBehaviorIsInSync)
            {
                shellShimManager = new ShellShimManagerMock(new DirectoryPath(cleanFolderUnderTempRoot));
            }
            else
            {
                shellShimManager = new ShellShimManager(new DirectoryPath(cleanFolderUnderTempRoot));
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);

            shellShimManager.CreateShim(new FilePath("dummy.dll"), shellCommandName);
            shellShimManager.ShimExists(shellCommandName).Should().Be(true);

            using (var scope = new TransactionScope())
            {
                shellShimManager.RemoveShim(shellCommandName);
                shellShimManager.ShimExists(shellCommandName).Should().Be(false);
                scope.Complete();
            }

            shellShimManager.ShimExists(shellCommandName).Should().Be(false);
        }

        private static void MakeNameConflictingCommand(string pathToPlaceShim, string shellCommandName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                shellCommandName = shellCommandName + ".exe";
            }

            File.WriteAllText(Path.Combine(pathToPlaceShim, shellCommandName), string.Empty);
        }

        private string ExecuteInShell(string shellCommandName, string cleanFolderUnderTempRoot, string arguments = "")
        {
            ProcessStartInfo processStartInfo;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var file = Path.Combine(cleanFolderUnderTempRoot, shellCommandName + ".exe");
                processStartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = false,
                    Arguments = arguments,
                };
            }
            else
            {
                processStartInfo = new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = shellCommandName + " " + arguments,
                    UseShellExecute = false
                };
            }

            _output.WriteLine($"Launching '{processStartInfo.FileName} {processStartInfo.Arguments}'");
            processStartInfo.WorkingDirectory = cleanFolderUnderTempRoot;
            processStartInfo.EnvironmentVariables["PATH"] = Path.GetDirectoryName(new Muxer().MuxerPath);

            processStartInfo.ExecuteAndCaptureOutput(out var stdOut, out var stdErr);

            stdErr.Should().BeEmpty();

            return stdOut ?? "";
        }

        private static FilePath MakeHelloWorldExecutableDll()
        {
            const string testAppName = "TestAppSimple";
            const string emptySpaceToTestSpaceInPath = " ";
            TestAssetInstance testInstance = TestAssets.Get(testAppName)
                .CreateInstance(testAppName + emptySpaceToTestSpaceInPath + "test")
                .UseCurrentRuntimeFrameworkVersion()
                .WithRestoreFiles()
                .WithBuildFiles();

            var configuration = Environment.GetEnvironmentVariable("CONFIGURATION") ?? "Debug";

            FileInfo outputDll = testInstance.Root.GetDirectory("bin", configuration)
                .GetDirectories().Single()
                .GetFile($"{testAppName}.dll");

            return new FilePath(outputDll.FullName);
        }

        private static string GetNewCleanFolderUnderTempRoot()
        {
            DirectoryInfo CleanFolderUnderTempRoot = new DirectoryInfo(Path.Combine(TempRoot.Root, "cleanfolder" + Path.GetRandomFileName()));
            CleanFolderUnderTempRoot.Create();

            return CleanFolderUnderTempRoot.FullName;
        }
    }
}
