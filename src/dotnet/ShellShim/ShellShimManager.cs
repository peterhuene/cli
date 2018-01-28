// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Transactions;
using System.Xml.Linq;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.ShellShim
{
    internal class ShellShimManager : IShellShimManager
    {
        private const string LauncherExeResourceName = "Microsoft.DotNet.Tools.Launcher.Executable";
        private const string LauncherConfigResourceName = "Microsoft.DotNet.Tools.Launcher.Config";

        private readonly DirectoryPath _shimsDirectory;

        public ShellShimManager(DirectoryPath shimsDirectory)
        {
            _shimsDirectory = shimsDirectory;
        }

        public void CreateShim(FilePath targetExecutablePath, string commandName)
        {
            if (string.IsNullOrEmpty(targetExecutablePath.Value))
            {
                throw new ShellShimException(CommonLocalizableStrings.CannotCreateShimForEmptyExecutablePath);
            }
            if (string.IsNullOrEmpty(commandName))
            {
                throw new ShellShimException(CommonLocalizableStrings.CannotCreateShimForEmptyCommand);
            }

            if (ShimExists(commandName))
            {
                throw new ShellShimException(
                    string.Format(
                        CommonLocalizableStrings.ShellShimConflict,
                        commandName));
            }

            try
            {
                if (!Directory.Exists(_shimsDirectory.Value))
                {
                    Directory.CreateDirectory(_shimsDirectory.Value);
                }

                using (var scope = new TransactionScope())
                {
                    TransactionEnlistment.Enlist(
                        rollback: () => {
                            foreach (var file in GetShimFiles(commandName).Where(f => File.Exists(f.Value)))
                            {
                                File.Delete(file.Value);
                            }
                        });

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        CreateConfigFile(
                            outputPath: GetWindowsConfigPath(commandName),
                            entryPoint: targetExecutablePath,
                            runner: "dotnet");

                        using (var shim = File.Create(GetWindowsShimPath(commandName).Value))
                        using (var resource = typeof(ShellShimManager).Assembly.GetManifestResourceStream(LauncherExeResourceName))
                        {
                            resource.CopyTo(shim);
                        }
                    }
                    else
                    {
                        var script = new StringBuilder();
                        script.AppendLine("#!/bin/sh");
                        script.AppendLine($"dotnet {targetExecutablePath.ToQuotedString()} \"$@\"");

                        var shimPath = GetPosixShimPath(commandName);
                        File.WriteAllText(shimPath.Value, script.ToString());

                        SetUserExecutionPermission(shimPath);
                    }

                    scope.Complete();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new ShellShimException(
                    string.Format(
                        CommonLocalizableStrings.FailedToCreateShellShim,
                        commandName,
                        ex.Message
                    ),
                    ex);
            }
        }

        public void RemoveShim(string commandName)
        {
            try
            {
                using (var scope = new TransactionScope())
                {
                    var files = new Dictionary<string, string>();
                    TransactionEnlistment.Enlist(
                        commit: () => {
                            foreach (var value in files.Values)
                            {
                                File.Delete(value);
                            }
                        },
                        rollback: () => {
                            foreach (var kvp in files)
                            {
                                File.Move(kvp.Value, kvp.Key);
                            }
                        });

                    foreach (var file in GetShimFiles(commandName).Where(f => File.Exists(f.Value)))
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                        File.Move(file.Value, tempPath);
                        files[file.Value] = tempPath;
                    }

                    scope.Complete();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new ShellShimException(
                    string.Format(
                        CommonLocalizableStrings.FailedToRemoveShellShim,
                        commandName,
                        ex.Message
                    ),
                    ex);
            }
        }

        public bool ShimExists(string commandName)
        {
            return GetShimFiles(commandName).Any(p => File.Exists(p.Value));
        }

        internal void CreateConfigFile(FilePath outputPath, FilePath entryPoint, string runner)
        {
            XDocument config;
            using (var resource = typeof(ShellShimManager).Assembly.GetManifestResourceStream(LauncherConfigResourceName))
            {
                config = XDocument.Load(resource);
            }

            var appSettings = config.Descendants("appSettings").First();
            appSettings.Add(new XElement("add", new XAttribute("key", "entryPoint"), new XAttribute("value", entryPoint.Value)));
            appSettings.Add(new XElement("add", new XAttribute("key", "runner"), new XAttribute("value", runner ?? string.Empty)));
            config.Save(outputPath.Value);
        }

        private IEnumerable<FilePath> GetShimFiles(string commandName)
        {
            if (string.IsNullOrEmpty(commandName))
            {
                yield break;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return GetWindowsShimPath(commandName);
                yield return GetWindowsConfigPath(commandName);
            }
            else
            {
                yield return GetPosixShimPath(commandName);
            }
        }

        private FilePath GetPosixShimPath(string commandName)
        {
            return _shimsDirectory.WithFile(commandName);
        }

        private FilePath GetWindowsShimPath(string commandName)
        {
            return new FilePath(_shimsDirectory.WithFile(commandName).Value + ".exe");
        }

        private FilePath GetWindowsConfigPath(string commandName)
        {
            return new FilePath(GetWindowsShimPath(commandName) + ".config");
        }

        private static void SetUserExecutionPermission(FilePath path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            CommandResult result = new CommandFactory()
                .Create("chmod", new[] { "u+x", path.Value })
                .CaptureStdOut()
                .CaptureStdErr()
                .Execute();

            if (result.ExitCode != 0)
            {
                throw new ShellShimException(
                    string.Format(CommonLocalizableStrings.FailedSettingShimPermissions, result.StdErr));
            }
        }
    }
}
