// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.ToolPackage;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.Tools.Install.Tool
{
    internal class PackageToProjectFileAdder : IPackageToProjectFileAdder
    {
        public void Add(FilePath projectPath, string packageId)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            var argsToPassToAdd = new List<string>
            {
                projectPath.Value,
                "package",
                packageId,
                "--no-restore"
            };

            var command = new DotNetCommandFactory(alwaysRunOutOfProc: true)
                .Create("add", argsToPassToAdd)
                .CaptureStdOut()
                .CaptureStdErr();

            var result = command.Execute();
            if (result.ExitCode != 0)
            {
                throw new PackageObtainException(
                    $"Failed to add tool package '{packageId}' to project: {result.StdErr}.");
            }
        }
    }
}
