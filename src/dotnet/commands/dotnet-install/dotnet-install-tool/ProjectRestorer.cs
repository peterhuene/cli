// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.ToolPackage;
using Microsoft.DotNet.PlatformAbstractions;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.Tools.Install.Tool
{
    internal class ProjectRestorer : IProjectRestorer
    {
        public void Restore(
            FilePath projectPath,
            DirectoryPath assetJsonOutput,
            FilePath? nugetconfig,
            string source,
            IEnumerable<string> forwardedArguments)
        {
            var argsToPassToRestore = new List<string>()
            {
                "--runtime",
                RuntimeEnvironment.GetRuntimeIdentifier()
            };

            argsToPassToRestore.Add(projectPath.Value);
            if (nugetconfig != null)
            {
                argsToPassToRestore.Add("--configfile");
                argsToPassToRestore.Add(nugetconfig.Value.Value);
            }

            if (source != null)
            {
                argsToPassToRestore.Add("--source");
                argsToPassToRestore.Add(source);
            }

            if (forwardedArguments != null)
            {
                argsToPassToRestore.AddRange(forwardedArguments);
            }

            if (!argsToPassToRestore.Any(a => a.StartsWith("/verbosity:")))
            {
                argsToPassToRestore.Add("/verbosity:quiet");
            }

            argsToPassToRestore.Add($"/p:BaseIntermediateOutputPath={assetJsonOutput.ToQuotedString()}");

            var command = new DotNetCommandFactory(alwaysRunOutOfProc: true)
                .Create("restore", argsToPassToRestore);

            var result = command.Execute();
            if (result.ExitCode != 0)
            {
                throw new PackageObtainException($"Failed to restore project {projectPath}.");
            }
        }
    }
}
