// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Microsoft.DotNet.ShellShim;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.ShellShim.Tests
{
    internal class FilePermissionsMock : IFilePermissions
    {
        private string _error;

        public FilePermissionsMock(string error = null)
        {
            _error = error;
        }

        public string SetUserExecutionPermission(FilePath path)
        {
            return _error;
        }
    }
}
