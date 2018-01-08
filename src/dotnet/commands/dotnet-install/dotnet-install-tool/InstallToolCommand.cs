// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.CommandLine;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Configurer;
using Microsoft.DotNet.ShellShim;
using Microsoft.DotNet.ToolPackage;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.Tools.Install.Tool
{
    public class InstallToolCommand : CommandBase
    {
        private static string _packageId;
        private static string _packageVersion;
        private static string _configFilePath;
        private static string _framework;
        private static string _source;
        private static IEnumerable<string> _forwardedArguments;

        public InstallToolCommand(
            AppliedOption appliedCommand,
            ParseResult parseResult)
            : base(parseResult)
        {
            if (appliedCommand == null)
            {
                throw new ArgumentNullException(nameof(appliedCommand));
            }

            _packageId = appliedCommand.Arguments.Single();
            _packageVersion = appliedCommand.ValueOrDefault<string>("version");
            _configFilePath = appliedCommand.ValueOrDefault<string>("configfile");
            _framework = appliedCommand.ValueOrDefault<string>("framework");
            _source = appliedCommand.ValueOrDefault<string>("source");
            _forwardedArguments = appliedCommand.OptionValuesToBeForwarded();
        }

        public override int Execute()
        {
            FilePath? configFile = null;

            if (_configFilePath != null)
            {
                configFile = new FilePath(_configFilePath);
            }

            var executablePackagePath = new DirectoryPath(new CliFolderPathCalculator().ExecutablePackagesPath);

            try
            {
                var toolConfigurationAndExecutableDirectory = ObtainPackage(configFile, executablePackagePath);

                DirectoryPath executable = toolConfigurationAndExecutableDirectory
                    .ExecutableDirectory
                    .WithSubDirectories(
                        toolConfigurationAndExecutableDirectory
                            .Configuration
                            .ToolAssemblyEntryPoint);

                var shellShimMaker = new ShellShimMaker(executablePackagePath.Value);
                var commandName = toolConfigurationAndExecutableDirectory.Configuration.CommandName;
                shellShimMaker.EnsureCommandNameUniqueness(commandName);

                shellShimMaker.CreateShim(
                    executable.Value,
                    commandName);

                EnvironmentPathFactory
                    .CreateEnvironmentPathInstruction()
                    .PrintAddPathInstructionIfPathDoesNotExist();

                Reporter.Output.WriteLine(
                    $"{Environment.NewLine}The installation succeeded. If there is no other instruction. You can type the following command in shell directly to invoke: {commandName}");
            }
            catch (PackageObtainException ex)
            {
                Reporter.Error.WriteLine($"Tool installation failed: {ex.Message}");
                return -1;
            }
            catch (ToolConfigurationException ex)
            {
                Reporter.Error.WriteLine($"Tool installation failed: {ex.Message}");
                Reporter.Error.WriteLine("Please contact the tool author for assistance.");
                return -1;
            }
            return 0;
        }

        private static ToolConfigurationAndExecutableDirectory ObtainPackage(FilePath? configFile, DirectoryPath executablePackagePath)
        {
            var toolPackageObtainer =
                new ToolPackageObtainer(
                    executablePackagePath,
                    () => new DirectoryPath(Path.GetTempPath())
                        .WithSubDirectories(Path.GetRandomFileName())
                        .WithFile(Path.GetRandomFileName() + ".csproj"),
                    new Lazy<string>(BundledTargetFramework.GetTargetFrameworkMoniker),
                    new PackageToProjectFileAdder(),
                    new ProjectRestorer());

            return toolPackageObtainer.ObtainAndReturnExecutablePath(
                packageId: _packageId,
                packageVersion: _packageVersion,
                nugetconfig: configFile,
                targetframework: _framework,
                source: _source,
                forwardedArguments: _forwardedArguments);
        }
    }
}
