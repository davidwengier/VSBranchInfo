using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CreateTagsForVSRelease
{
    public static class Program
    {
        private static readonly string[] s_visualStudioBranches = new[] { "rel/d16.10", "rel/d16.11", "main" };

        public static async Task Main(string[] args)
        {
            string pat;
            if (args.Length == 1)
            {
                pat = args[0];
            }
            else
            {
                pat = File.ReadAllText(FindFile("pat.txt"));
            }

            var credentials = new VssBasicCredential(string.Empty, pat);

            using var connection = new VssConnection(new Uri("https://devdiv.visualstudio.com/DefaultCollection"), credentials);
            using var dncEngConnection = new VssConnection(new Uri("https://dev.azure.com/dnceng"), credentials);

            using var gitClient = await connection.GetClientAsync<GitHttpClient>();
            using var buildClient = await connection.GetClientAsync<BuildHttpClient>();
            using var dncBuildClient = await dncEngConnection.GetClientAsync<BuildHttpClient>();

            foreach (var branch in s_visualStudioBranches)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(branch + ":");
                Console.ResetColor();
                try
                {
                    await TryGetRoslynBuildForReleaseAsync(branch, gitClient, buildClient, dncBuildClient);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: " + ex.Message);
                    Console.ResetColor();
                    Console.WriteLine();
                }
            }

            static async Task TryGetRoslynBuildForReleaseAsync(string branch, GitHttpClient gitClient, BuildHttpClient buildClient, BuildHttpClient dncBuildClient)
            {
                var commit = new GitVersionDescriptor { VersionType = GitVersionType.Branch, Version = branch };
                GitRepository vsRepository = await gitClient.GetRepositoryAsync("DevDiv", "VS");

                using var componentsJsonStream = await gitClient.GetItemContentAsync(
                    vsRepository.Id,
                    @".corext\Configs\dotnetcodeanalysis-components.json",
                    download: true,
                    versionDescriptor: commit);

                var fileContents = await new StreamReader(componentsJsonStream).ReadToEndAsync();
                var componentsJson = JObject.Parse(fileContents);

                var languageServicesUrlAndManifestName = componentsJson["Components"]?["Microsoft.CodeAnalysis.LanguageServices"]?["url"]?.ToString();

                var parts = languageServicesUrlAndManifestName?.Split(';');
                if (parts?.Length != 2)
                {
                    throw new Exception("Couldn't get URL and manifest. Got: " + parts);
                }

                if (!parts[1].EndsWith(".vsman"))
                {
                    throw new Exception("Couldn't get URL and manifest. Not a vsman file? Got: " + parts);
                }

                using var defaultConfigStream = await gitClient.GetItemContentAsync(
                    vsRepository.Id,
                    @".corext\Configs\default.config",
                    download: true,
                    versionDescriptor: commit);

                fileContents = await new StreamReader(defaultConfigStream).ReadToEndAsync();
                var defaultConfig = XDocument.Parse(fileContents);

                var packageVersion = defaultConfig.Root.Descendants("package").Where(p => p.Attribute("id")?.Value == "VS.ExternalAPIs.Roslyn").Select(p => p.Attribute("version")?.Value).FirstOrDefault();

                var buildNumber = new Uri(parts[0]).Segments.Last();
                var builds = await TryGetDevDivBuilds(buildClient, vsRepository, buildNumber);
                if (builds == null || builds.Count == 0)
                {
                    builds = await TryGetDncEngBuilds(dncBuildClient, vsRepository, buildNumber);
                }

                foreach (var build in builds)
                {
                    Console.WriteLine("Package Version: " + packageVersion);
                    Console.WriteLine("Commit Sha: " + build.SourceVersion);
                    Console.WriteLine("Source branch: " + build.SourceBranch.Replace("refs/heads/", ""));
                    Console.WriteLine();
                }

                if (!builds.Any())
                {
                    throw new Exception("Couldn't find build for package version: " + packageVersion + ", build number: " + buildNumber);
                }
            }

            static async Task<List<Build>?> TryGetDevDivBuilds(BuildHttpClient buildClient, GitRepository vsRepository, string buildNumber)
            {
                try
                {
                    var buildDefinition = (await buildClient.GetDefinitionsAsync(vsRepository.ProjectReference.Id, name: "Roslyn-Signed")).Single();
                    var builds = await buildClient.GetBuildsAsync(buildDefinition.Project.Id, definitions: new[] { buildDefinition.Id }, buildNumber: buildNumber);
                    return builds;
                }
                catch
                {
                    return null;
                }
            }

            static async Task<List<Build>> TryGetDncEngBuilds(BuildHttpClient buildClient, GitRepository vsRepository, string buildNumber)
            {
                var buildDefinition = (await buildClient.GetDefinitionsAsync("internal", name: "dotnet-roslyn CI")).Single();
                var builds = await buildClient.GetBuildsAsync(buildDefinition.Project.Id, definitions: new[] { buildDefinition.Id }, buildNumber: buildNumber);
                return builds;
            }
        }

        private static string FindFile(string filename)
        {
            DirectoryInfo dir = new DirectoryInfo(Environment.CurrentDirectory);
            while (!File.Exists(Path.Combine(dir.FullName, filename)))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir.FullName, filename);
        }
    }
}
