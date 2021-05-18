using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using LibGit2Sharp;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CreateTagsForVSRelease
{
    public static class Program
    {
        private static readonly string[] s_visualStudioBranches = new[] { "rel/d16.9", "rel/d16.10", "main" };

        public static async Task Main(string[] args)
        {
            var client = new SecretClient(
                vaultUri: new Uri("https://roslyninfra.vault.azure.net:443"),
                credential: new DefaultAzureCredential(includeInteractiveCredentials: true));

            var azureDevOpsSecret = await client.GetSecretAsync("vslsnap-vso-auth-token");
            using var connection = new VssConnection(
                new Uri("https://devdiv.visualstudio.com/DefaultCollection"),
                new WindowsCredential(new NetworkCredential("vslsnap", azureDevOpsSecret.Value.Value)));

            using var gitClient = await connection.GetClientAsync<GitHttpClient>();
            using var buildClient = await connection.GetClientAsync<BuildHttpClient>();

            foreach (var branch in s_visualStudioBranches)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(branch + ":");
                Console.ResetColor();
                try
                {
                    await TryGetRoslynBuildForReleaseAsync(branch, gitClient, buildClient);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: " + ex.Message);
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }

        private static async Task TryGetRoslynBuildForReleaseAsync(string branch, GitHttpClient gitClient, BuildHttpClient buildClient)
        {
            var commit = new GitVersionDescriptor { VersionType = GitVersionType.Branch, Version = branch };
            GitRepository vsRepository = await GetVSRepositoryAsync(gitClient);

            using var componentsJsonStream = await gitClient.GetItemContentAsync(
                vsRepository.Id,
                @".corext\Configs\dotnetcodeanalysis-components.json",
                download: true,
                versionDescriptor: commit);

            var fileContents = await new StreamReader(componentsJsonStream).ReadToEndAsync();
            var componentsJson = JObject.Parse(fileContents);

            var languageServicesUrlAndManifestName = (string)componentsJson["Components"]["Microsoft.CodeAnalysis.LanguageServices"]["url"];

            var parts = languageServicesUrlAndManifestName.Split(';');
            if (parts.Length != 2)
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

            var buildDefinition = (await buildClient.GetDefinitionsAsync(vsRepository.ProjectReference.Id, name: "Roslyn-Signed")).Single();
            var build = (await buildClient.GetBuildsAsync(buildDefinition.Project.Id, definitions: new[] { buildDefinition.Id }, buildNumber: buildNumber)).SingleOrDefault();

            if (build == null)
            {
                throw new Exception("Couldn't find build for package version: " + packageVersion);
            }

            Console.WriteLine("Package Version: " + packageVersion);
            Console.WriteLine("Commit Sha: " + build.SourceVersion);
        }
        
        private static async Task<GitRepository> GetVSRepositoryAsync(GitHttpClient gitClient)
        {
            return await gitClient.GetRepositoryAsync("DevDiv", "VS");
        }
    }
}
