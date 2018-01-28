// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.ToolPackage
{
    internal interface IToolPackageManager
    {
        DirectoryPath GetPackageRootDirectory(string packageId);

        DirectoryPath GetPackageDirectory(string packageId, string packageVersion);

        string InstallPackage(
            string packageId,
            string packageVersion = null,
            string targetFramework = null,
            FilePath? tempProjectPath = null,
            DirectoryPath? offlineFeedPath = null,
            FilePath? nugetConfig = null,
            string source = null,
            string verbosity = null);

        IEnumerable<string> GetInstalledVersions(string packageId);

        ToolConfiguration GetToolConfiguration(string packageId, string packageVersion);

        void UninstallPackage(string packageId, string packageVersion);
    }
}
