#addin "nuget:?package=Cake.Incubator&version=5.1.0"
#load "./index.cake"

///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////
var rootPath = "../";   //根目录
var build = BuildParameters.Create(Context,rootPath);
var target = Argument("target", "Default");//指定默认执行的目标任务 
// usage: dotnet cake ./build/build.cake -target=Restore


///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(ctx =>
{
    // Executed BEFORE the first task.
    Information("Running tasks...");
});

Teardown(ctx =>
{
    // Executed AFTER the last task.
    Information("Finished running tasks.");
});

///////////////////////////////////////////////////////////////////////////////
// TASKS
///////////////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
{
	if (DirectoryExists($"{rootPath}artifacts"))
	{
		DeleteDirectory($"{rootPath}artifacts", new DeleteDirectorySettings(){
            Force = true,
            Recursive = true
        });
	}
	if (DirectoryExists($"{rootPath}publish"))
	{
		DeleteDirectory($"{rootPath}publish", new DeleteDirectorySettings(){
            Force = true,
            Recursive = true
        });
	}
});

Task("Restore")
	.IsDependentOn("Clean")
	.Does(() =>
{
	var settings = new DotNetCoreRestoreSettings
    {
		 Runtime = build.Runtime
		 //,Force = true // force all dependencies to be resolved even if the last restore was successful. This is equivalent to deleting the project.assets.json file
		 //,NoCache = true //cache packages and http requests.
    };
	foreach (var solution in build.SolutionFiles)
	{
		Information($"Restore solution: {solution.FullPath}");
		DotNetCoreRestore(solution.FullPath,settings);
	}
});

// Show GitVersion Info
// Depends on GitVersion.Tool(dotnet global tool)
Task("GitVersion")
    //.IsDependentOn("Publish")
    .Does(() =>
{
	var gitVersionResults = GitVersion(new GitVersionSettings {
    });
	build.Version = gitVersionResults.InformationalVersion;
	Information("GitVersion Info:");
	Information(gitVersionResults.Dump());
});

// build All ProjectFile
Task("Build")
	.IsDependentOn("Restore")
	.IsDependentOn("GitVersion")
    .Does(() =>
{
	var MSBuildSettings = new DotNetCoreMSBuildSettings();
	MSBuildSettings.SetVersion(build.Version);
    var settings = new DotNetCoreBuildSettings
    {
         Configuration = build.Configuration,
		 NoRestore = true,// config of 'Runtime' in Restore and Build must be same
		 Runtime = build.Runtime,
		 MSBuildSettings = MSBuildSettings
    };

	Information($"*************************build.Configuration: {build.Configuration}");
    // DotNetCoreBuild("./Kane.Blake.sln", settings);
    // DotNetCoreBuild("./src/utils/Kane.Blake.Utils.Logging/Kane.Blake.Utils.Logging.csproj", settings);
    foreach (var project in build.ProjectFiles)
	{
		DotNetCoreBuild(project.FullPath, settings);
	}
});

Task("RunTest")
	.IsDependentOn("Build")
	.Does(() =>
{
	var settings = new DotNetCoreTestSettings
    {
		NoRestore = true //// config of 'Runtime、Configuration' in RunTest and Build must be same
		,NoBuild = true
		,Runtime = build.Runtime
		,Configuration = build.Configuration
		
    };
	foreach (var testProject in build.TestProjectFiles)
	{
		DotNetCoreTest(testProject.FullPath, settings);
	}
});


// publish Web ProjectFile
Task("Publish")
	.IsDependentOn("RunTest")
	.Does(() =>
{
	// https://cakebuild.net/api/Cake.Common.Tools.DotNetCore.Publish/DotNetCorePublishSettings/
	var MSBuildSettings = new DotNetCoreMSBuildSettings();
	MSBuildSettings.SetVersion(build.Version);

	var settings = new DotNetCorePublishSettings
	{
		NoRestore = false,
		NoBuild = false,
		Configuration = build.Configuration,
		Runtime = build.Runtime, // 指定目标运行时的情况下, SelfContained 默认为true
		SelfContained = false,
		MSBuildSettings = MSBuildSettings
	};
	foreach (var project in build.WebProjectFiles)
	{
		var projectName =project.GetFilenameWithoutExtension().ToString();
		settings.OutputDirectory = $"{rootPath}publish/{projectName}";
		DotNetCorePublish(project.FullPath, settings);
	}
});


Task("Pack")
	.Does(() =>
{
	var settings = new DotNetCorePackSettings
	{
		Configuration = build.Configuration,
		IncludeSymbols = true,
		OutputDirectory = $"{rootPath}artifacts/packages"
	};
	foreach (var project in build.ProjectFiles)
	{
		DotNetCorePack(project.FullPath, settings);
	}
});


Task("Default")
    .IsDependentOn("Publish")
    .Does(() =>
{

});

RunTarget(target);