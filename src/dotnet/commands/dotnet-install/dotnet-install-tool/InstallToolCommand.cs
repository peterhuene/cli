// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Transactions;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.CommandLine;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Configurer;
using Microsoft.DotNet.ShellShim;
using Microsoft.DotNet.ToolPackage;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.Tools.Install.Tool
{
    internal class InstallToolCommand : CommandBase
    {
        private readonly IToolPackageManager _toolPackageManager;
        private readonly IEnvironmentPathInstruction _environmentPathInstruction;
        private readonly IShellShimManager _shellShimManager;
        private readonly IReporter _reporter;
        private readonly IReporter _errorReporter;

        private readonly string _packageId;
        private readonly string _packageVersion;
        private readonly string _configFilePath;
        private readonly string _framework;
        private readonly string _source;
        private readonly bool _global;
        private readonly string _verbosity;

        public InstallToolCommand(
            AppliedOption appliedCommand,
            ParseResult parseResult,
            IToolPackageManager toolPackageManager = null,
            IShellShimManager shellShimManager = null,
            IEnvironmentPathInstruction environmentPathInstruction = null,
            IReporter reporter = null)
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
            _global = appliedCommand.ValueOrDefault<bool>("global");
            _verbosity = appliedCommand.SingleArgumentOrDefault("verbosity");

            var cliFolderPathCalculator = new CliFolderPathCalculator();
            _toolPackageManager = toolPackageManager ?? new ToolPackageManager(
                new DirectoryPath(cliFolderPathCalculator.ToolsPackagePath)
                new ProjectRestorer(reporter));

            _environmentPathInstruction = environmentPathInstruction
                                          ?? EnvironmentPathFactory
                                              .CreateEnvironmentPathInstruction();

            _shellShimManager = shellShimManager ?? new ShellShimManager(
                new DirectoryPath(cliFolderPathCalculator.ToolsShimPath));

            _reporter = (reporter ?? Reporter.Output);
            _errorReporter = (reporter ?? Reporter.Error);
        }

        public override int Execute()
        {
            if (!_global)
            {
                throw new GracefulException(LocalizableStrings.InstallToolCommandOnlySupportGlobal);
            }

            // Check if any packages are installed
            if (_toolPackageManager.GetInstalledVersions(_packageId).FirstOrDefault() != null)
            {
                _errorReporter.WriteLine(string.Format(LocalizableStrings.ToolAlreadyInstalled, _packageId).Red());
                return 1;
            }

            try
            {
                FilePath? configFile = null;
                if (_configFilePath != null)
                {
                    configFile = new FilePath(_configFilePath);
                }

                ToolConfiguration configuration = null;
                string installedVersion = null;

                using (var scope = new TransactionScope())
                {
                    installedVersion = _toolPackageManager.InstallPackage(
                        packageId: _packageId,
                        packageVersion: _packageVersion,
                        targetFramework: _framework,
                        nugetConfig: configFile,
                        source: _source,
                        verbosity: _verbosity);

                    configuration = _toolPackageManager.GetToolConfiguration(_packageId, installedVersion);

                    _shellShimManager.CreateShim(
                        new FilePath(configuration.ToolExecutablePath),
                        configuration.CommandName);

                    scope.Complete();
                }

                _environmentPathInstruction.PrintAddPathInstructionIfPathDoesNotExist();

                _reporter.WriteLine(
                    string.Format(
                        LocalizableStrings.InstallationSucceeded,
                        configuration.CommandName,
                        _packageId,
                        installedVersion).Green());
                return 0;
            }
            catch (ToolPackageException ex)
            {
                if (Reporter.IsVerbose)
                {
                    Reporter.Verbose.WriteLine(ex.ToString().Red());
                }

                _errorReporter.WriteLine(ex.Message.Red());
                _errorReporter.WriteLine(string.Format(LocalizableStrings.ToolInstallationFailed, _packageId).Red());
                return 1;
            }
            catch (ToolConfigurationException ex)
            {
                if (Reporter.IsVerbose)
                {
                    Reporter.Verbose.WriteLine(ex.ToString().Red());
                }

                _errorReporter.WriteLine(
                    string.Format(
                        LocalizableStrings.InvalidToolConfiguration,
                        ex.Message).Red());
                _errorReporter.WriteLine(string.Format(LocalizableStrings.ToolInstallationFailedContactAuthor, _packageId).Red());
                return 1;
            }
            catch (ShellShimException ex)
            {
                if (Reporter.IsVerbose)
                {
                    Reporter.Verbose.WriteLine(ex.ToString().Red());
                }

                _errorReporter.WriteLine(
                    string.Format(
                        LocalizableStrings.FailedToCreateToolShim,
                        _packageId,
                        ex.Message).Red());
                _errorReporter.WriteLine(string.Format(LocalizableStrings.ToolInstallationFailed, _packageId).Red());
                return 1;
            }
        }
    }
}
