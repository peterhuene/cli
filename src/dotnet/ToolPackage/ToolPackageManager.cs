using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Transactions;
using System.Xml.Linq;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Configurer;
using Microsoft.DotNet.Tools;
using Microsoft.Extensions.EnvironmentAbstractions;
using NuGet.ProjectModel;

namespace Microsoft.DotNet.ToolPackage
{
    internal class ToolPackageManager : IToolPackageManager
    {
        private const string ToolSettingsFileName = "DotnetToolSettings.xml";
        private const string StagingDirectory = ".stage";

        private readonly IProjectRestorer _projectRestorer;
        private readonly DirectoryPath _toolsPath;

        public ToolPackageManager(DirectoryPath toolsPath, IProjectRestorer projectRestorer)
        {
            _toolsPath = toolsPath;
            _projectRestorer = projectRestorer ?? throw new ArgumentNullException(nameof(projectRestorer));
        }

        public DirectoryPath GetPackageRootDirectory(string packageId)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }
            return _toolsPath.WithSubDirectories(packageId);
        }

        public DirectoryPath GetPackageDirectory(string packageId, string packageVersion)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }
            if (packageVersion == null)
            {
                throw new ArgumentNullException(nameof(packageVersion));
            }
            return GetPackageRootDirectory(packageId).WithSubDirectories(packageVersion);
        }

        public string InstallPackage(
            string packageId,
            string packageVersion = null,
            string targetFramework = null,
            FilePath? tempProjectPath = null,
            DirectoryPath? offlineFeedPath = null,
            FilePath? nugetConfig = null,
            string source = null,
            string verbosity = null)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            if (nugetConfig != null && !File.Exists(nugetConfig.Value.Value))
            {
                throw new ToolPackageException(
                    string.Format(
                        CommonLocalizableStrings.NuGetConfigurationFileDoesNotExist,
                        Path.GetFullPath(nugetConfig.Value.Value)));
            }

            tempProjectPath = tempProjectPath ?? new DirectoryPath(Path.GetTempPath())
                .WithSubDirectories(Path.GetRandomFileName())
                .WithFile(Path.GetRandomFileName() + ".csproj");

            if (Path.GetExtension(tempProjectPath.Value.Value) != "csproj")
            {
                tempProjectPath = new FilePath(Path.ChangeExtension(tempProjectPath.Value.Value, "csproj"));
            }

            try
            {
                using (var scope = new TransactionScope())
                {
                    var packageRootDirectory = GetPackageRootDirectory(packageId).Value;

                    string rollbackDirectory = null;
                    TransactionEnlistment.Enlist(
                        rollback: () => {
                            if (!string.IsNullOrEmpty(rollbackDirectory) && Directory.Exists(rollbackDirectory))
                            {
                                Directory.Delete(rollbackDirectory, true);
                            }

                            // Delete the root if it is empty
                            if (Directory.Exists(packageRootDirectory) &&
                                !Directory.EnumerateFileSystemEntries(packageRootDirectory).Any())
                            {
                                Directory.Delete(packageRootDirectory, false);
                            }
                        });

                    var stageDirectory = _toolsPath.WithSubDirectories(StagingDirectory, Path.GetRandomFileName());
                    Directory.CreateDirectory(stageDirectory.Value);
                    rollbackDirectory = stageDirectory.Value;

                    CreateTempProject(
                        tempProjectPath: tempProjectPath.Value,
                        packageId: packageId,
                        packageVersion: packageVersion,
                        targetFramework: targetFramework ?? BundledTargetFramework.GetTargetFrameworkMoniker(),
                        restoreDirectory: stageDirectory,
                        offlineFeedPath: offlineFeedPath ?? new DirectoryPath(
                            new CliFolderPathCalculator().CliFallbackFolderPath));

                    _projectRestorer.Restore(tempProjectPath.Value, stageDirectory, nugetConfig, source, verbosity);

                    packageVersion = Path.GetFileName(
                        Directory.EnumerateDirectories(
                            stageDirectory.WithSubDirectories(packageId).Value).Single());

                    var packageDirectory = GetPackageDirectory(packageId, packageVersion);
                    if (Directory.Exists(packageDirectory.Value))
                    {
                        throw new ToolPackageException(
                            string.Format(
                                CommonLocalizableStrings.ToolPackageConflictPackageId,
                                packageId,
                                packageVersion));
                    }

                    Directory.CreateDirectory(packageRootDirectory);
                    Directory.Move(stageDirectory.Value, packageDirectory.Value);
                    rollbackDirectory = packageDirectory.Value;

                    scope.Complete();
                    return packageVersion;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new ToolPackageException(
                    string.Format(
                        CommonLocalizableStrings.FailedToInstallToolPackage,
                        packageId,
                        ex.Message),
                    ex);
            }
        }

        public IEnumerable<string> GetInstalledVersions(string packageId)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            var packageRootDirectory = GetPackageRootDirectory(packageId);
            if (!Directory.Exists(packageRootDirectory.Value))
            {
                yield break;
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(packageRootDirectory.Value))
            {
                yield return Path.GetFileName(subdirectory);
            }
        }

        public ToolConfiguration GetToolConfiguration(string packageId, string packageVersion)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }
            if (packageVersion == null)
            {
                throw new ArgumentNullException(nameof(packageVersion));
            }

            try
            {
                var packageDirectory = GetPackageDirectory(packageId, packageVersion);

                var lockFile = new LockFileFormat()
                    .Read(packageDirectory.WithFile("project.assets.json").Value);

                var library = FindLibraryInLockFile(lockFile, packageId);
                var dotnetToolSettings = FindItemInTargetLibrary(library, ToolSettingsFileName);
                if (dotnetToolSettings == null)
                {
                    throw new ToolPackageException(
                        string.Format(CommonLocalizableStrings.ToolPackageMissingSettingsFile, packageId));
                }

                var toolConfigurationPath =
                    packageDirectory
                        .WithSubDirectories(
                            packageId,
                            library.Version.ToNormalizedString())
                        .WithFile(dotnetToolSettings.Path);

                var configuration = ToolConfigurationDeserializer.Deserialize(toolConfigurationPath.Value);

                var entryPointFromLockFile = FindItemInTargetLibrary(library, configuration.ToolAssemblyEntryPoint);
                if (entryPointFromLockFile == null)
                {
                    throw new ToolPackageException(
                        string.Format(
                            CommonLocalizableStrings.ToolPackageMissingEntryPointFile,
                            packageId,
                            configuration.ToolAssemblyEntryPoint));
                }

                configuration.ToolExecutablePath = packageDirectory
                    .WithSubDirectories(
                        packageId,
                        library.Version.ToNormalizedString())
                    .WithFile(entryPointFromLockFile.Path).Value;

                return configuration;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new ToolPackageException(
                    string.Format(
                        CommonLocalizableStrings.FailedToRetrieveToolConfiguration,
                        packageId,
                        ex.Message),
                    ex);
            }
        }

        public void UninstallPackage(string packageId, string packageVersion)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            if (packageVersion == null)
            {
                throw new ArgumentNullException(nameof(packageVersion));
            }

            try
            {
                using (var scope = new TransactionScope())
                {
                    var packageRootDirectory = GetPackageRootDirectory(packageId);
                    var packageDirectory = GetPackageDirectory(packageId, packageVersion);
                    string tempPackageDirectory = null;

                    TransactionEnlistment.Enlist(
                        commit: () => {
                            if (tempPackageDirectory != null)
                            {
                                Directory.Delete(tempPackageDirectory, true);
                            }
                        },
                        rollback: () => {
                            if (tempPackageDirectory != null)
                            {
                                Directory.CreateDirectory(packageRootDirectory.Value);
                                Directory.Move(tempPackageDirectory, packageDirectory.Value);
                            }
                        });

                    if (Directory.Exists(packageDirectory.Value))
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                        Directory.Move(packageDirectory.Value, tempPath);
                        tempPackageDirectory = tempPath;
                    }

                    if (Directory.Exists(packageRootDirectory.Value) &&
                       !Directory.EnumerateFileSystemEntries(packageRootDirectory.Value).Any())
                    {
                        Directory.Delete(packageRootDirectory.Value, false);
                    }

                    scope.Complete();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new ToolPackageException(
                    string.Format(
                        CommonLocalizableStrings.FailedToUninstallToolPackage,
                        packageId,
                        ex.Message),
                    ex);
            }
        }

        private static LockFileTargetLibrary FindLibraryInLockFile(LockFile lockFile, string packageId)
        {
            return lockFile
                ?.Targets?.SingleOrDefault(t => t.RuntimeIdentifier != null)
                ?.Libraries?.SingleOrDefault(l => l.Name == packageId);
        }

        private static LockFileItem FindItemInTargetLibrary(LockFileTargetLibrary library, string targetRelativeFilePath)
        {
            return library
                ?.ToolsAssemblies
                ?.SingleOrDefault(t => LockFileMatcher.MatchesFile(t, targetRelativeFilePath));
        }

        private void CreateTempProject(
            FilePath tempProjectPath,
            string packageId,
            string packageVersion,
            string targetFramework,
            DirectoryPath restoreDirectory,
            DirectoryPath offlineFeedPath)
        {
            Directory.CreateDirectory(tempProjectPath.GetDirectoryPath().Value);

            var tempProjectContent = new XDocument(
                new XElement("Project",
                    new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement("PropertyGroup",
                        new XElement("TargetFramework", targetFramework),
                        new XElement("RestorePackagesPath", restoreDirectory.Value),
                        new XElement("RestoreProjectStyle", "DotnetToolReference"), // without it, project cannot reference tool package
                        new XElement("RestoreRootConfigDirectory", Directory.GetCurrentDirectory()), // config file probing start directory
                        new XElement("DisableImplicitFrameworkReferences", "true"), // no Microsoft.NETCore.App in tool folder
                        new XElement("RestoreFallbackFolders", "clear"), // do not use fallbackfolder, tool package need to be copied to tool folder
                        new XElement("RestoreAdditionalProjectSources", // use fallbackfolder as feed to enable offline
                            Directory.Exists(offlineFeedPath.Value) ? offlineFeedPath.Value : string.Empty),
                        new XElement("RestoreAdditionalProjectFallbackFolders", string.Empty), // block other
                        new XElement("RestoreAdditionalProjectFallbackFoldersExcludes", string.Empty),  // block other
                        new XElement("DisableImplicitNuGetFallbackFolder", "true")),  // disable SDK side implicit NuGetFallbackFolder
                     new XElement("ItemGroup",
                        new XElement("PackageReference",
                            new XAttribute("Include", packageId),
                            new XAttribute("Version", packageVersion ?? "*") // nuget will restore * for latest
                            ))
                        ));

            File.WriteAllText(tempProjectPath.Value, tempProjectContent.ToString());
        }
    }
}
