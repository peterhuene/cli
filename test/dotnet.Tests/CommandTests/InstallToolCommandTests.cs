// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.CommandLine;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.ToolPackage;
using Microsoft.DotNet.Tools;
using Microsoft.DotNet.Tools.Install.Tool;
using Microsoft.DotNet.Tools.Tests.ComponentMocks;
using Microsoft.DotNet.Tools.Test.Utilities;
using Microsoft.Extensions.DependencyModel.Tests;
using Microsoft.Extensions.EnvironmentAbstractions;
using Newtonsoft.Json;
using Xunit;
using Parser = Microsoft.DotNet.Cli.Parser;
using LocalizableStrings = Microsoft.DotNet.Tools.Install.Tool.LocalizableStrings;
using System.Runtime.InteropServices;

namespace Microsoft.DotNet.Tests.Commands
{
    public class InstallToolCommandTests
    {
        private readonly IFileSystem _fileSystemWrapper;
        private readonly ToolPackageManagerMock _toolPackageManagerMock;
        private readonly ShellShimManagerMock _shellShimManagerMock;
        private readonly EnvironmentPathInstructionMock _environmentPathInstructionMock;
        private readonly AppliedOption _appliedCommand;
        private readonly ParseResult _parseResult;
        private readonly BufferedReporter _reporter;
        private const string PathToPlaceShim = "pathToPlace";
        private const string PathToPlacePackages = PathToPlaceShim + "pkg";
        private const string PackageId = "global.tool.console.demo";
        private const string PackageVersion = "1.0.4";

        public InstallToolCommandTests()
        {
            _fileSystemWrapper = new FileSystemMockBuilder().Build();
            _toolPackageManagerMock = new ToolPackageManagerMock(_fileSystemWrapper, toolsPath: PathToPlacePackages);
            _shellShimManagerMock = new ShellShimManagerMock(new DirectoryPath(PathToPlaceShim), _fileSystemWrapper);
            _reporter = new BufferedReporter();
            _environmentPathInstructionMock =
                new EnvironmentPathInstructionMock(_reporter, PathToPlaceShim);

            ParseResult result = Parser.Instance.Parse($"dotnet install tool -g {PackageId}");
            _appliedCommand = result["dotnet"]["install"]["tool"];
            var parser = Parser.Instance;
            _parseResult = parser.ParseFrom("dotnet install", new[] {"tool", PackageId});
        }

        [Fact]
        public void WhenRunWithPackageIdItShouldCreateValidShim()
        {
            var installToolCommand = new InstallToolCommand(_appliedCommand,
                _parseResult,
                _toolPackageManagerMock,
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute().Should().Be(0);

            // It is hard to simulate shell behavior. Only Assert shim can point to executable dll
            _fileSystemWrapper.File.Exists(ExpectedCommandPath()).Should().BeTrue();
            var deserializedFakeShim = JsonConvert.DeserializeObject<ShellShimManagerMock.FakeShim>(
                _fileSystemWrapper.File.ReadAllText(ExpectedCommandPath()));

            _fileSystemWrapper.File.Exists(deserializedFakeShim.ExecutablePath).Should().BeTrue();
        }

        [Fact]
        public void WhenRunWithPackageIdWithSourceItShouldCreateValidShim()
        {
            const string sourcePath = "http://mysouce.com";
            ParseResult result = Parser.Instance.Parse($"dotnet install tool -g {PackageId} --source {sourcePath}");
            AppliedOption appliedCommand = result["dotnet"]["install"]["tool"];
            ParseResult parseResult =
                Parser.Instance.ParseFrom("dotnet install", new[] { "tool", PackageId, "--source", sourcePath });

            var installToolCommand = new InstallToolCommand(appliedCommand,
                parseResult,
                new ToolPackageManagerMock(_fileSystemWrapper, additionalFeeds: new List<MockFeed>
                {
                    new MockFeed
                    {
                        Type = MockFeedType.Source,
                        Uri = sourcePath,
                        Packages = new List<MockFeedPackage>
                        {
                            new MockFeedPackage
                            {
                                PackageId = PackageId,
                                Version = PackageVersion
                            }
                        }
                    }
                }),
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute().Should().Be(0);

            // It is hard to simulate shell behavior. Only Assert shim can point to executable dll
            _fileSystemWrapper.File.Exists(ExpectedCommandPath())
            .Should().BeTrue();
            var deserializedFakeShim =
                JsonConvert.DeserializeObject<ShellShimManagerMock.FakeShim>(
                    _fileSystemWrapper.File.ReadAllText(ExpectedCommandPath()));
            _fileSystemWrapper.File.Exists(deserializedFakeShim.ExecutablePath).Should().BeTrue();
        }

        [Fact]
        public void WhenRunWithPackageIdItShouldShowPathInstruction()
        {
            var installToolCommand = new InstallToolCommand(_appliedCommand,
                _parseResult,
                _toolPackageManagerMock,
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute().Should().Be(0);

            _reporter.Lines.First().Should().Be("INSTRUCTION");
        }

        [Fact]
        public void GivenFailedPackageInstallWhenRunWithPackageIdItShouldFail()
        {
            var toolPackageManagerSimulatorThatThrows
                = new ToolPackageManagerMock(
                    _fileSystemWrapper, true, null,
                    () => throw new ToolPackageException("Simulated error"));
            var installToolCommand = new InstallToolCommand(
                _appliedCommand,
                _parseResult,
                toolPackageManagerSimulatorThatThrows,
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute().Should().Be(1);

            _reporter.Lines.Count.Should().Be(2);

            _reporter
                .Lines[0]
                .Should()
                .Contain("Simulated error");

            _reporter
                .Lines[1]
                .Should()
                .Contain(string.Format(LocalizableStrings.ToolInstallationFailed, PackageId));
        }

        [Fact]
        public void GivenFailedPackageInstallWhenRunWithPackageIdItShouldHaveNoBrokenFolderOnDisk()
        {
            var toolPackageManagerThatThrows
                = new ToolPackageManagerMock(
                    _fileSystemWrapper, true, null,
                    duringInstall: () => throw new ToolConfigurationException("Simulated error"),
                    toolsPath: PathToPlacePackages);
            var installToolCommand = new InstallToolCommand(
                _appliedCommand,
                _parseResult,
                toolPackageManagerThatThrows,
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute();

            _fileSystemWrapper.Directory.Exists(Path.Combine(PathToPlacePackages, PackageId)).Should().BeFalse();
        }

        [Fact]
        public void GivenCreateShimItShouldHaveNoBrokenFolderOnDisk()
        {
            _fileSystemWrapper.File.CreateEmptyFile(ExpectedCommandPath()); // Create conflict shim
            var toolPackageManagerThatThrows
                = new ToolPackageManagerMock(
                    _fileSystemWrapper, true, null,
                    toolsPath: PathToPlacePackages);
            var installToolCommand = new InstallToolCommand(
                _appliedCommand,
                _parseResult,
                toolPackageManagerThatThrows,
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute().Should().Be(1);

            _reporter
                .Lines[0]
                .Should()
                .Contain(
                    string.Format(
                        CommonLocalizableStrings.ShellShimConflict,
                        ToolPackageManagerMock.FakeCommandName));

            _fileSystemWrapper.Directory.Exists(Path.Combine(PathToPlacePackages, PackageId)).Should().BeFalse();
        }

        [Fact]
        public void GivenInCorrectToolConfigurationWhenRunWithPackageIdItShouldFail()
        {
            var toolPackageManagerSimulatorThatThrows
                = new ToolPackageManagerMock(
                    _fileSystemWrapper, true, null,
                    () => throw new ToolConfigurationException("Simulated error"));
            var installToolCommand = new InstallToolCommand(
                _appliedCommand,
                _parseResult,
                toolPackageManagerSimulatorThatThrows,
                _shellShimManagerMock,
                _environmentPathInstructionMock,
                _reporter);

            installToolCommand.Execute().Should().Be(1);

            _reporter.Lines.Count.Should().Be(2);

            _reporter
                .Lines[0]
                .Should()
                .Contain(
                    string.Format(
                        LocalizableStrings.InvalidToolConfiguration,
                        "Simulated error"));

            _reporter
                .Lines[1]
                .Should()
                .Contain(string.Format(LocalizableStrings.ToolInstallationFailedContactAuthor, PackageId));
        }

        [Fact]
        public void WhenRunWithPackageIdItShouldShowSuccessMessage()
        {
            var installToolCommand = new InstallToolCommand(
                _appliedCommand,
                _parseResult,
                _toolPackageManagerMock,
                _shellShimManagerMock,
                new EnvironmentPathInstructionMock(_reporter, PathToPlaceShim, true),
                _reporter);

            installToolCommand.Execute().Should().Be(0);

            _reporter
                .Lines
                .Single()
                .Should()
                .Contain(string.Format(
                    LocalizableStrings.InstallationSucceeded,
                    ToolPackageManagerMock.FakeCommandName,
                    PackageId,
                    PackageVersion));
        }

        private static string ExpectedCommandPath()
        {
            var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;
            return Path.Combine(
                "pathToPlace",
                ToolPackageManagerMock.FakeCommandName + extension);
        }
    }
}
