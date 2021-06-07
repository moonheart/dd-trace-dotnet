using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static CustomDotNetTasks;

// #pragma warning disable SA1306
// #pragma warning disable SA1134
// #pragma warning disable SA1111
// #pragma warning disable SA1400
// #pragma warning disable SA1401

partial class Build
{
    [Solution("Datadog.Trace.sln")] readonly Solution Solution;
    AbsolutePath MsBuildProject => RootDirectory / "Datadog.Trace.proj";
    AbsolutePath IisSolution => TestsDirectory / "test-applications" / "aspnet" / "samples-iis.sln";

    AbsolutePath OutputDirectory => RootDirectory / "bin";
    AbsolutePath TracerHomeDirectory => TracerHome ?? (OutputDirectory / "tracer-home");
    AbsolutePath ArtifactsDirectory => Artifacts ?? (OutputDirectory / "artifacts");
    AbsolutePath WindowsTracerHomeZip => ArtifactsDirectory / "windows-tracer-home.zip";
    AbsolutePath BuildDataDirectory => RootDirectory / "build_data";

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "test";

    string TempDirectory => IsWin ? Path.GetTempPath() : "/tmp/";
    string TracerLogDirectory => IsWin
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Datadog .NET Tracer", "logs")
        : "/var/log/datadog/dotnet/";

    Project NativeProfilerProject => Solution.GetProject(Projects.ClrProfilerNative);

    [LazyPathExecutable(name: "cmake")] readonly Lazy<Tool> CMake;
    [LazyPathExecutable(name: "make")] readonly Lazy<Tool> Make;
    [LazyPathExecutable(name: "fpm")] readonly Lazy<Tool> Fpm;
    [LazyPathExecutable(name: "gzip")] readonly Lazy<Tool> GZip;

    IEnumerable<MSBuildTargetPlatform> ArchitecturesForPlatform =>
        Equals(Platform, MSBuildTargetPlatform.x64)
            ? new[] {MSBuildTargetPlatform.x64, MSBuildTargetPlatform.x86}
            : new[] {MSBuildTargetPlatform.x86};

    bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    string LinuxArchitectureIdentifier => IsArm64 ? "arm64" : Platform.ToString();

    IEnumerable<string> LinuxPackageTypes => IsAlpine ? new[] {"tar"} : new[] {"deb", "rpm", "tar"};

    IEnumerable<Project> ProjectsToPack => new []
    {
        Solution.GetProject(Projects.DatadogTrace),
        Solution.GetProject(Projects.DatadogTraceOpenTracing),
    };

    Project[] ParallelIntegrationTests => new []
    {
        Solution.GetProject(Projects.TraceIntegrationTests),
        Solution.GetProject(Projects.OpenTracingIntegrationTests),
    };

    Project[] ClrProfilerIntegrationTests => new []
    {
        Solution.GetProject(Projects.ClrProfilerIntegrationTests)
    };

    readonly IEnumerable<TargetFramework> TargetFrameworks = new []
    {
        TargetFramework.NET45,
        TargetFramework.NET461,
        TargetFramework.NETSTANDARD2_0,
        TargetFramework.NETCOREAPP3_1,
    };

    Target CreateRequiredDirectories => _ => _
        .Unlisted()
        .Executes(() =>
        {
            EnsureExistingDirectory(TracerHomeDirectory);
            EnsureExistingDirectory(ArtifactsDirectory);
            EnsureExistingDirectory(BuildDataDirectory);
        });

    Target Restore => _ => _
        .After(Clean)
        .Unlisted()
        .Executes(() =>
        {
            if (IsWin)
            {
                NuGetTasks.NuGetRestore(s => s
                    .SetTargetPath(Solution)
                    .SetVerbosity(NuGetVerbosity.Normal)
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                        o.SetPackagesDirectory(NugetPackageDirectory)));
            }
            else
            {
                DotNetRestore(s => s
                    .SetProjectFile(Solution)
                    .SetVerbosity(DotNetVerbosity.Normal)
                    // .SetTargetPlatform(Platform) // necessary to ensure we restore every project
                    .SetProperty("configuration", BuildConfiguration.ToString())
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                        o.SetPackageDirectory(NugetPackageDirectory)));
            }
        });

    Target CompileNativeSrcWindows => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            // If we're building for x64, build for x86 too
            var platforms =
                Equals(Platform, MSBuildTargetPlatform.x64)
                    ? new[] { MSBuildTargetPlatform.x64, MSBuildTargetPlatform.x86 }
                    : new[] { MSBuildTargetPlatform.x86 };

            // Can't use dotnet msbuild, as needs to use the VS version of MSBuild
            MSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetMSBuildPath()
                .SetTargets("BuildCppSrc")
                .DisableRestore()
                .SetMaxCpuCount(null)
                .CombineWith(platforms, (m, platform) => m
                    .SetTargetPlatform(platform)));
        });

    Target CompileNativeSrcLinux => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .OnlyWhenStatic(() => IsLinux)
        .Executes(() =>
        {
            var buildDirectory = NativeProfilerProject.Directory / "build";
            EnsureExistingDirectory(buildDirectory);

            CMake.Value(
                arguments: "../ -DCMAKE_BUILD_TYPE=Release",
                workingDirectory: buildDirectory);
            Make.Value(workingDirectory: buildDirectory);

            var source = buildDirectory / "bin" / $"{NativeProfilerProject.Name}.so";
            var dest = TracerHomeDirectory / $"linux-{LinuxArchitectureIdentifier}";
            CopyFileToDirectory(source, dest, FileExistsPolicy.OverwriteIfNewer);
        });

    Target CompileNativeSrcMacOs => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .OnlyWhenStatic(() => IsOsx)
        .Executes(() =>
        {
            var nativeProjectDirectory = NativeProfilerProject.Directory;
            CMake.Value(arguments: ".", workingDirectory: nativeProjectDirectory);
            Make.Value(workingDirectory: nativeProjectDirectory);
        });

    Target CompileNativeSrc => _ => _
        .Unlisted()
        .Description("Compiles the native loader")
        .DependsOn(CompileNativeSrcWindows)
        .DependsOn(CompileNativeSrcMacOs)
        .DependsOn(CompileNativeSrcLinux);

    Target CompileManagedSrc => _ => _
        .Unlisted()
        .Description("Compiles the managed code in the src directory")
        .After(CreateRequiredDirectories)
        .After(Restore)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetTargetPlatformAnyCPU()
                .SetConfiguration(BuildConfiguration)
                .DisableRestore()
                .SetTargets("BuildCsharpSrc")
            );
        });


    Target CompileNativeTestsWindows => _ => _
        .Unlisted()
        .After(CompileNativeSrc)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            // If we're building for x64, build for x86 too
            var platforms =
                Equals(Platform, MSBuildTargetPlatform.x64)
                    ? new[] { MSBuildTargetPlatform.x64, MSBuildTargetPlatform.x86 }
                    : new[] { MSBuildTargetPlatform.x86 };

            // Can't use dotnet msbuild, as needs to use the VS version of MSBuild
            MSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetMSBuildPath()
                .SetTargets("BuildCppTests")
                .DisableRestore()
                .SetMaxCpuCount(null)
                .CombineWith(platforms, (m, platform) => m
                    .SetTargetPlatform(platform)));
        });

    Target CompileNativeTestsLinux => _ => _
        .Unlisted()
        .After(CompileNativeSrc)
        .OnlyWhenStatic(() => IsLinux)
        .Executes(() =>
        {
            Logger.Error("We don't currently run unit tests on Linux");
        });

    Target CompileNativeTests => _ => _
        .Unlisted()
        .Description("Compiles the native loader unit tests")
        .DependsOn(CompileNativeTestsWindows)
        .DependsOn(CompileNativeTestsLinux);


    Target CopyIntegrationsJson => _ => _
        .Unlisted()
        .After(Clean)
        .After(CreateRequiredDirectories)
        .Executes(() =>
        {
            var source = RootDirectory / "integrations.json";
            var dest = TracerHomeDirectory;

            Logger.Info($"Copying '{source}' to '{dest}'");
            CopyFileToDirectory(source, dest, FileExistsPolicy.OverwriteIfNewer);
        });

    Target PublishManagedProfiler => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            DotNetPublish(s => s
                .SetProject(Solution.GetProject(Projects.ClrProfilerManaged))
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .EnableNoBuild()
                .EnableNoRestore()
                .CombineWith(TargetFrameworks, (p, framework) => p
                    .SetFramework(framework)
                    .SetOutput(TracerHomeDirectory / framework)));
        });

    Target PublishNativeProfilerWindows => _ => _
        .Unlisted()
        .OnlyWhenStatic(() => IsWin)
        .After(CompileNativeSrc, PublishManagedProfiler)
        .Executes(() =>
        {
            foreach (var architecture in ArchitecturesForPlatform)
            {
                var source = NativeProfilerProject.Directory / "bin" / BuildConfiguration / architecture.ToString() /
                             $"{NativeProfilerProject.Name}.dll";
                var dest = TracerHomeDirectory / $"win-{architecture}";
                Logger.Info($"Copying '{source}' to '{dest}'");
                CopyFileToDirectory(source, dest, FileExistsPolicy.OverwriteIfNewer);
            }
        });

    Target PublishNativeProfilerLinux => _ => _
        .Unlisted()
        .OnlyWhenStatic(() => IsLinux)
        .After(CompileNativeSrc, PublishManagedProfiler)
        .Executes(() =>
        {
            // TODO: Invert this, so it copies _from the bin folder instead of _into_ it
            // leaving this as-is for now to match existing pipeline
            var outputDir = NativeProfilerProject.Directory / "build" / "bin" / BuildConfiguration /
                            LinuxArchitectureIdentifier;

            EnsureCleanDirectory(outputDir);
            // copy static files
            CopyFileToDirectory(RootDirectory / "integrations.json", outputDir, FileExistsPolicy.Overwrite);
            CopyFileToDirectory(RootDirectory / "build" / "artifacts" / "createLogPath.sh", outputDir, FileExistsPolicy.Overwrite);

            // copy native file
            var buildOutputDirectory = NativeProfilerProject.Directory / "build" / "bin";
            CopyFileToDirectory(buildOutputDirectory / $"{NativeProfilerProject.Name}.so", outputDir);

            // Copy managed files
            foreach (var framework in new []{TargetFramework.NETSTANDARD2_0, TargetFramework.NETCOREAPP3_1})
            {
                EnsureCleanDirectory(outputDir / framework);
                CopyDirectoryRecursively(
                    source: TracerHomeDirectory / framework,
                    target: outputDir / framework,
                    DirectoryExistsPolicy.Merge);
            }
        });

    Target PublishNativeProfilerMacOs => _ => _
        .Unlisted()
        .OnlyWhenStatic(() => IsOsx)
        .After(CompileNativeSrc, PublishManagedProfiler)
        .Executes(() =>
        {
            GlobFiles(NativeProfilerProject.Directory / "bin" / $"{NativeProfilerProject.Name}.*")
                .ForEach(source =>
                {
                    var dest = TracerHomeDirectory / $"osx-x64";
                    CopyFileToDirectory(source, dest, FileExistsPolicy.OverwriteIfNewer);

                });
        });

    Target PublishNativeProfiler => _ => _
        .Unlisted()
        .DependsOn(PublishNativeProfilerWindows)
        .DependsOn(PublishNativeProfilerLinux)
        .DependsOn(PublishNativeProfilerMacOs);

    Target BuildMsi => _ => _
        .Unlisted()
        .Description("Builds the .msi files from the compiled tracer home directory")
        .After(BuildTracerHome)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            MSBuild(s => s
                    .SetTargetPath(Solution.GetProject(Projects.WindowsInstaller))
                    .SetConfiguration(BuildConfiguration)
                    .SetMSBuildPath()
                    .AddProperty("RunWixToolsOutOfProc", true)
                    .SetProperty("TracerHomeDirectory", TracerHomeDirectory)
                    .SetMaxCpuCount(null)
                    .CombineWith(ArchitecturesForPlatform, (o, arch) => o
                        .SetProperty("MsiOutputPath", ArtifactsDirectory / arch.ToString())
                        .SetTargetPlatform(arch)),
                degreeOfParallelism: 2);
        });


    /// <summary>
    /// This target is a bit of a hack, but means that we actually use the All CPU builds in intgration tests etc
    /// </summary>
    Target CopyPlatformlessBuildOutput => _ => _
        .Description("Copies the build output from 'All CPU' platforms to platform-specific folders")
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileDependencyLibs)
        .After(CompileManagedUnitTests)
        .Executes(() =>
        {
            var directories = RootDirectory.GlobDirectories(
                $"src/**/bin/{BuildConfiguration}",
                $"tools/**/bin/{BuildConfiguration}",
                $"test/Datadog.Trace.TestHelpers/**/bin/{BuildConfiguration}",
                $"test/test-applications/integrations/dependency-libs/**/bin/{BuildConfiguration}"
            );
            directories.ForEach(source =>
            {
                var target = source.Parent / $"{Platform}" / BuildConfiguration;
                if (DirectoryExists(target))
                {
                    Logger.Info($"Skipping '{target}' as already exists");
                }
                else
                {
                    CopyDirectoryRecursively(source, target, DirectoryExistsPolicy.Fail, FileExistsPolicy.Fail);
                }
            });
        });

    Target ZipTracerHome => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .Requires(() => Version)
        .Executes(() =>
        {
            if (IsWin)
            {
                CompressZip(TracerHomeDirectory, WindowsTracerHomeZip, fileMode: FileMode.Create);
            }
            else if (IsLinux)
            {
                var fpm = Fpm.Value;
                var gzip = GZip.Value;
                var packageName = "datadog-dotnet-apm";

                var suffix = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? string.Empty
                    : RuntimeInformation.ProcessArchitecture.ToString().ToLower();

                var workingDirectory = ArtifactsDirectory / $"linux-{LinuxArchitectureIdentifier}";
                EnsureCleanDirectory(workingDirectory);

                var publishDir = NativeProfilerProject.Directory / "build" / "bin" / BuildConfiguration /
                                 LinuxArchitectureIdentifier;
                foreach (var packageType in LinuxPackageTypes)
                {
                    var args = new []
                    {
                        "-f",
                        "-s dir",
                        $"-t {packageType}",
                        $"-n {packageName}",
                        $"-v {Version}",
                        packageType == "tar" ? "--prefix /opt/datadog" : "",
                        $"--chdir {publishDir}",
                        "netstandard2.0/",
                        "netcoreapp3.1/",
                        "Datadog.Trace.ClrProfiler.Native.so",
                        "integrations.json",
                        "createLogPath.sh",
                    };
                    var arguments = string.Join(" ", args);
                    fpm(arguments, workingDirectory: workingDirectory);
                }

                gzip($"-f {packageName}.tar", workingDirectory: workingDirectory);

                var versionedName = IsAlpine
                    ? $"{packageName}-{Version}-musl{suffix}.tar.gz"
                    : $"{packageName}-{Version}{suffix}.tar.gz";

                RenameFile(
                    workingDirectory / $"{packageName}.tar.gz",
                    workingDirectory / versionedName);
            }
        });


    Target CompileManagedUnitTests => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .SetProperty("BuildProjectReferences", false)
                .SetTargets("BuildCsharpUnitTests"));
        });

    Target RunManagedUnitTests => _ => _
        .Unlisted()
        .After(CompileManagedUnitTests)
        .Executes(() =>
        {
            var testProjects = RootDirectory.GlobFiles("test/**/*.Tests.csproj")
                .Select(x => Solution.GetProject(x))
                .ToList();

            testProjects.ForEach(EnsureResultsDirectory);

            try
            {
                DotNetTest(x => x
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatformAnyCPU()
                    .SetDDEnvironmentVariables("dd-tracer-dotnet")
                    .EnableMemoryDumps()
                    .CombineWith(testProjects, (x, project) => x
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    Target RunNativeTestsWindows => _ => _
        .Unlisted()
        .After(CompileNativeSrcWindows)
        .After(CompileNativeTestsWindows)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            var workingDirectory = TestsDirectory / "Datadog.Trace.ClrProfiler.Native.Tests" / "bin" / BuildConfiguration.ToString() / Platform.ToString();
            var exePath = workingDirectory / "Datadog.Trace.ClrProfiler.Native.Tests.exe";
            var testExe = ToolResolver.GetLocalTool(exePath);
            testExe("--gtest_output=xml", workingDirectory: workingDirectory);
        });

    Target RunNativeTestsLinux => _ => _
        .Unlisted()
        .After(CompileNativeSrcLinux)
        .After(CompileNativeTestsLinux)
        .OnlyWhenStatic(() => IsLinux)
        .Executes(() =>
        {
            Logger.Error("We don't currently run unit tests on Linux");
        });

    Target RunNativeTests => _ => _
        .Unlisted()
        .DependsOn(RunNativeTestsWindows)
        .DependsOn(RunNativeTestsLinux);

    Target CompileDependencyLibs => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .EnableNoDependencies()
                .SetTargets("BuildDependencyLibs")
            );
        });

    Target CompileRegressionDependencyLibs => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // We run linux integration tests in AnyCPU, but Windows on the specific architecture
            var platform = IsLinux ? MSBuildTargetPlatform.MSIL : Platform;

            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(platform)
                .SetTargets("BuildRegressionDependencyLibs")
            );
        });

    Target CompileRegressionSamples => _ => _
        .Unlisted()
        .After(Restore)
        .After(CopyPlatformlessBuildOutput)
        .After(CompileRegressionDependencyLibs)
        .Executes(() =>
        {
            // explicitly build the other dependency (with restore to avoid runtime identifier dependency issues)
            DotNetBuild(x => x
                .SetProjectFile(Solution.GetProject(Projects.ApplicationWithLog4Net))
                // .EnableNoRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetNoWarnDotNetCore3()
                .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                    o.SetPackageDirectory(NugetPackageDirectory)));

            var regressionsDirectory = Solution.GetProject(Projects.EntityFramework6xMdTokenLookupFailure)
                .Directory.Parent;
            var regressionLibs = GlobFiles(regressionsDirectory / "**" / "*.csproj")
                .Where(x => !x.Contains("EntityFramework6x.MdTokenLookupFailure")
                            && !x.Contains("ExpenseItDemo")
                            && !x.Contains("StackExchange.Redis.AssemblyConflict.LegacyProject")
                            && !x.Contains("dependency-libs"));

             // Allow restore here, otherwise things go wonky with runtime identifiers
             // in some target frameworks. No, I don't know why
             DotNetBuild(x => x
                 // .EnableNoRestore()
                 .EnableNoDependencies()
                 .SetConfiguration(BuildConfiguration)
                 .SetTargetPlatform(Platform)
                 .SetNoWarnDotNetCore3()
                 .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                     o.SetPackageDirectory(NugetPackageDirectory))
                 .CombineWith(regressionLibs, (x, project) => x
                     .SetProjectFile(project)));
        });

    Target CompileFrameworkReproductions => _ => _
        .Unlisted()
        .Description("Builds .NET Framework projects (non SDK-based projects)")
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CopyPlatformlessBuildOutput)
        .Requires(() => IsWin)
        .Executes(() =>
        {
            // We have to use the full MSBuild here, as dotnet msbuild doesn't copy the EDMX assets for embedding correctly
            // seems similar to https://github.com/dotnet/sdk/issues/8360
            MSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .SetMSBuildPath()
                .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetTargets("BuildFrameworkReproductions")
                .SetMaxCpuCount(null));
        });

    Target CompileIntegrationTests => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionSamples)
        .After(CompileFrameworkReproductions)
        .After(PublishIisSamples)
        .Requires(() => TracerHomeDirectory != null)
        .Executes(() =>
        {
            DotNetMSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                .SetTargets("BuildCsharpIntegrationTests")
                .SetMaxCpuCount(null));
        });

    Target CompileSamples => _ => _
        .Unlisted()
        .After(CompileDependencyLibs)
        .After(CopyPlatformlessBuildOutput)
        .After(CompileFrameworkReproductions)
        .Requires(() => TracerHomeDirectory != null)
        .Executes(() =>
        {
            // This does some "unnecessary" rebuilding and restoring
            var include = RootDirectory.GlobFiles("test/test-applications/integrations/**/*.csproj");
            var exclude = RootDirectory.GlobFiles("test/test-applications/integrations/dependency-libs/**/*.csproj");

            var projects = include.Where(projectPath =>
                projectPath switch
                {
                    _ when exclude.Contains(projectPath) => false,
                    _ when projectPath.ToString().Contains("Samples.OracleMDA") => false,
                    _ => true,
                }
            );

            DotNetBuild(config => config
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .EnableNoDependencies()
                .SetProperty("BuildInParallel", "false")
                .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                .SetProperty("ExcludeManagedProfiler", true)
                .SetProperty("ExcludeNativeProfiler", true)
                .SetProperty("LoadManagedProfilerFromProfilerDirectory", false)
                .CombineWith(projects, (s, project) => s
                    .SetProjectFile(project)));
        });

    Target PublishIisSamples => _ => _
        .Unlisted()
        .After(CompileManagedUnitTests)
        .After(CompileRegressionSamples)
        .After(CompileFrameworkReproductions)
        .Executes(() =>
        {

            var publishProfile = TestsDirectory / "test-applications" / "aspnet" / "PublishProfiles" /
                                 "FolderProfile.pubxml";
            MSBuild(x => x
                .SetMSBuildPath()
                // .DisableRestore()
                .EnableNoDependencies()
                .SetTargetPath(IisSolution)
                .SetConfiguration(BuildConfiguration)
                .SetProperty("DeployOnBuild", true)
                .SetProperty("PublishProfile", publishProfile)
                .SetMaxCpuCount(null)
            );
        });

    Target RunWindowsIntegrationTests => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .After(CompileIntegrationTests)
        .After(CompileSamples)
        .After(CompileFrameworkReproductions)
        .Requires(() => IsWin)
        .Executes(() =>
        {
            ParallelIntegrationTests.ForEach(EnsureResultsDirectory);
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);

            try
            {
                DotNetTest(config => config
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .When(!string.IsNullOrEmpty(Filter), c => c.SetFilter(Filter))
                    .CombineWith(ParallelIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)), degreeOfParallelism: 4);


                // TODO: I think we should change this filter to run on Windows by default
                // (RunOnWindows!=False|Category=Smoke)&LoadFromGAC!=True&IIS!=True
                DotNetTest(config => config
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFilter(Filter ?? "(RunOnWindows=True|Category=Smoke)&LoadFromGAC!=True&IIS!=True")
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });


    Target RunWindowsIisIntegrationTests => _ => _
        .After(BuildTracerHome)
        .After(CompileIntegrationTests)
        .After(CompileSamples)
        .After(CompileFrameworkReproductions)
        .After(PublishIisSamples)
        .Executes(() =>
        {
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);
            try
            {
                // Different filter from RunWindowsIntegrationTests
                DotNetTest(config => config
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFilter(Filter ?? "(RunOnWindows=True|Category=Smoke)&LoadFromGAC=True")
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    Target CompileSamplesLinux => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CompileManagedUnitTests)
        .Requires(() => TracerHomeDirectory != null)
        .Requires(() => Framework)
        .Executes(() =>
        {
            // There's nothing specifically linux-y here, it's just that we only build a subset of projects
            // for testing on linux.
            var sampleProjects = RootDirectory.GlobFiles("test/test-applications/integrations/*/*.csproj");
            var regressionProjects = RootDirectory.GlobFiles("test/test-applications/regression/*/*.csproj");
            var instrumentationProjects = RootDirectory.GlobFiles("test/test-applications/instrumentation/*/*.csproj");

            // These samples are currently skipped.
            var projectsToSkip = new[]
            {
                "Samples.Msmq",
                "Samples.MultiDomainHost.Runner",
                "Samples.RateLimiter", // I think we _should_ run this one (assuming it has tests)
                "Samples.SqlServer.NetFramework20",
                "Samples.TracingWithoutLimits", // I think we _should_ run this one (assuming it has tests)
                "Samples.Wcf",
                "Samples.WebRequest.NetFramework20",
                "AutomapperTest", // I think we _should_ run this one (assuming it has tests)
                "DogStatsD.RaceCondition",
                "EntityFramework6x.MdTokenLookupFailure",
                "LargePayload", // I think we _should_ run this one (assuming it has tests)
                "Log4Net.SerializationException",
                "NLog10LogsInjection.NullReferenceException",
                "Sandbox.ManualTracing",
                "StackExchange.Redis.AssemblyConflict.LegacyProject",
            };

            // These sample projects are built using RestoreAndBuildSamplesForPackageVersions
            // so no point building them now
            // TODO: Load this list dynamically
            var multiApiProjects = new[]
            {
                "Samples.MongoDB",
                "Samples.Elasticsearch",
                "Samples.Elasticsearch.V5",
                "Samples.Kafka",
                "Samples.Npgsql",
                "Samples.RabbitMQ",
                "Samples.SqlServer",
                "Samples.Microsoft.Data.SqlClient",
                "Samples.StackExchange.Redis",
                "Samples.ServiceStack.Redis",
                // "Samples.MySql", - the "non package version" is _ALSO_ tested separately
                "Samples.Microsoft.Data.Sqlite",
                "Samples.OracleMDA",
                "Samples.OracleMDA.Core",
            };

            var projectsToBuild = sampleProjects
                .Concat(regressionProjects)
                .Concat(instrumentationProjects)
                .Where(path =>
                {
                    var project = Solution.GetProject(path);
                    return project?.Name switch
                    {
                        "Samples.AspNetCoreMvc21" => Framework == TargetFramework.NETCOREAPP2_1,
                        "Samples.AspNetCoreMvc30" => Framework == TargetFramework.NETCOREAPP3_0,
                        "Samples.AspNetCoreMvc31" => Framework == TargetFramework.NETCOREAPP3_1,
                        var name when projectsToSkip.Contains(name) => false,
                        var name when multiApiProjects.Contains(name) => false,
                        _ => true,
                    };
                });

            // do the build and publish separately to avoid dependency issues

            // Always AnyCPU
            DotNetBuild(x => x
                    // .EnableNoRestore()
                    .EnableNoDependencies()
                    .SetConfiguration(BuildConfiguration)
                    .SetFramework(Framework)
                    // .SetTargetPlatform(Platform)
                    .SetNoWarnDotNetCore3()
                    .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                    .When(TestAllPackageVersions, o => o.SetProperty("TestAllPackageVersions", "true"))
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o => o.SetPackageDirectory(NugetPackageDirectory))
                    .CombineWith(projectsToBuild, (c, project) => c
                        .SetProjectFile(project)));

            // Always AnyCPU
            DotNetPublish(x => x
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .EnableNoDependencies()
                    .SetConfiguration(BuildConfiguration)
                    .SetFramework(Framework)
                    // .SetTargetPlatform(Platform)
                    .SetNoWarnDotNetCore3()
                    .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                    .When(TestAllPackageVersions, o => o.SetProperty("TestAllPackageVersions", "true"))
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o => o.SetPackageDirectory(NugetPackageDirectory))
                    .CombineWith(projectsToBuild, (c, project) => c
                        .SetProject(project)));

        });

    Target CompileMultiApiPackageVersionSamples => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CompileManagedUnitTests)
        .After(CompileSamplesLinux)
        .Requires(() => TracerHomeDirectory != null)
        .Requires(() => Framework)
        .Executes(() =>
        {
            // Build and restore for all versions
            // Annoyingly this rebuilds everything again and again.
            var targets = new [] { "RestoreSamplesForPackageVersionsOnly", "RestoreAndBuildSamplesForPackageVersionsOnly" };

            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .EnableNoDependencies()
                .SetProperty("TargetFramework", Framework.ToString())
                .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                .SetProperty("BuildInParallel", "true")
                .AddProcessEnvironmentVariable("TestAllPackageVersions", "true")
                .When(TestAllPackageVersions, o => o.SetProperty("TestAllPackageVersions", "true"))
                .CombineWith(targets, (c, target) => c.SetTargets(target))
            );
        });

    Target CompileLinuxIntegrationTests => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CompileManagedUnitTests)
        .After(CompileSamplesLinux)
        .After(CompileMultiApiPackageVersionSamples)
        .Requires(() => TracerHomeDirectory != null)
        .Requires(() => Framework)
        .Executes(() =>
        {
            // Build the actual integration test projects for Any CPU
            var integrationTestProjects = RootDirectory.GlobFiles("test/*.IntegrationTests/*.csproj");
            DotNetBuild(x => x
                    // .EnableNoRestore()
                    .EnableNoDependencies()
                    .SetConfiguration(BuildConfiguration)
                    .SetFramework(Framework)
                    // .SetTargetPlatform(Platform)
                    .SetNoWarnDotNetCore3()
                    .When(TestAllPackageVersions, o => o
                        .SetProperty("TestAllPackageVersions", "true"))
                    .AddProcessEnvironmentVariable("TestAllPackageVersions", "true")
                    .AddProcessEnvironmentVariable("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                        o.SetPackageDirectory(NugetPackageDirectory))
                    .CombineWith(integrationTestProjects, (c, project) => c
                        .SetProjectFile(project)));

            // Not sure if/why this is necessary, and we can't just point to the correct output location
            var src = TracerHomeDirectory;
            var testProject = Solution.GetProject(Projects.ClrProfilerIntegrationTests).Directory;
            var dest = testProject / "bin" / BuildConfiguration / Framework / "profiler-lib";
            CopyDirectoryRecursively(src, dest, DirectoryExistsPolicy.Merge, FileExistsPolicy.OverwriteIfNewer);

            // not sure exactly where this is supposed to go, may need to change the original build
            foreach (var linuxDir in TracerHomeDirectory.GlobDirectories("linux-*"))
            {
                CopyDirectoryRecursively(linuxDir, dest, DirectoryExistsPolicy.Merge, FileExistsPolicy.OverwriteIfNewer);
            }
        });

    Target RunLinuxIntegrationTests => _ => _
        .After(CompileLinuxIntegrationTests)
        .Description("Runs the linux integration tests")
        .Requires(() => Framework)
        .Requires(() => IsLinux)
        .Executes(() =>
        {
            ParallelIntegrationTests.ForEach(EnsureResultsDirectory);
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);

            try
            {
                // Run these ones in parallel
                // Always AnyCPU
                DotNetTest(config => config
                        .SetConfiguration(BuildConfiguration)
                        // .SetTargetPlatform(Platform)
                        .EnableNoRestore()
                        .EnableNoBuild()
                        .SetFramework(Framework)
                        .EnableMemoryDumps()
                        .When(!string.IsNullOrEmpty(Filter), x => x.SetFilter(Filter))
                        .When(TestAllPackageVersions, o => o
                            .SetProcessEnvironmentVariable("TestAllPackageVersions", "true"))
                        .CombineWith(ParallelIntegrationTests, (s, project) => s
                            .EnableTrxLogOutput(GetResultsDirectory(project))
                            .SetProjectFile(project)),
                    degreeOfParallelism: 2);

                // Run this one separately so we can tail output
                DotNetTest(config => config
                    .SetConfiguration(BuildConfiguration)
                    // .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFramework(Framework)
                    .EnableMemoryDumps()
                    .SetFilter(Filter ?? (IsArm64 ? "Category!=ArmUnsupported" : null))
                    .When(TestAllPackageVersions, o => o
                        .SetProcessEnvironmentVariable("TestAllPackageVersions", "true"))
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project))
                );
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    private AbsolutePath GetResultsDirectory(Project proj) => BuildDataDirectory / "results" / proj.Name;

    private void EnsureResultsDirectory(Project proj) => EnsureCleanDirectory(GetResultsDirectory(proj));

    private void MoveLogsToBuildData()
    {
        if (Directory.Exists(TracerLogDirectory))
        {
            CopyDirectoryRecursively(TracerLogDirectory, BuildDataDirectory / "logs",
                DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);
        }

        if (Directory.Exists(TempDirectory))
        {
            foreach (var dump in GlobFiles(TempDirectory, "coredump*"))
            {
                MoveFileToDirectory(dump, BuildDataDirectory / "dumps", FileExistsPolicy.Overwrite);
            }
        }
    }

    protected override void OnTargetStart(string target)
    {
        if (PrintDriveSpace)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                Logger.Info($"Drive space available on '{drive.Name}': {PrettyPrint(drive.AvailableFreeSpace)} / {PrettyPrint(drive.TotalSize)}");
            }
        }
        base.OnTargetStart(target);

        static string PrettyPrint(long bytes)
        {
            var power = Math.Min((int) Math.Log(bytes, 1000), 4);
            var normalised = bytes / Math.Pow(1000, power);
            return power switch
            {
                4 => $"{normalised:F}TB",
                3 => $"{normalised:F}GB",
                2 => $"{normalised:F}MB",
                1 => $"{normalised:F}KB",
                _ => $"{bytes}B",
            };
        }
    }
}