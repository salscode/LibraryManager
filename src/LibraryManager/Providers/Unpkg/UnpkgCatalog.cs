﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;
using Microsoft.Web.LibraryManager.LibraryNaming;
using Newtonsoft.Json.Linq;

namespace Microsoft.Web.LibraryManager.Providers.Unpkg
{
    internal class UnpkgCatalog : ILibraryCatalog
    {
        public const string CacheFileName = "cache.json";
        public const string LibraryFileListUrlFormat = "https://unpkg.com/{0}@{1}/?meta"; // https://unpkg.com/jquery@3.3.1/?meta
        public const string LatestLibraryVersonUrl = "https://unpkg.com/{0}/package.json"; // https://unpkg.com/jquery/package.json
        private UnpkgProvider _provider;
        private CacheService _cacheService;
        private string _cacheFile;


        public UnpkgCatalog(UnpkgProvider provider)
        {
            _provider = provider;
            // TODO: {alexgav} Do we need multiple instances of this?
            _cacheService = new CacheService(WebRequestHandler.Instance);
            _cacheFile = Path.Combine(provider.CacheFolder, CacheFileName);
        }

        public async Task<string> GetLatestVersion(string libraryName, bool includePreReleases, CancellationToken cancellationToken)
        {
            string latestVersion = null;

            try
            {
                string latestLibraryVersionUrl = string.Format(LatestLibraryVersonUrl, libraryName);

                JObject packageObject = await WebRequestHandler.Instance.GetJsonObjectViaGetAsync(latestLibraryVersionUrl, cancellationToken);

                if (packageObject != null)
                {
                    JValue versionValue = packageObject["version"] as JValue;
                    latestVersion = versionValue?.Value as string;
                }
            }
            catch(Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return latestVersion;
        }

        public async Task<ILibrary> GetLibraryAsync(string libraryName, string version, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(libraryName) || string.IsNullOrEmpty(version))
            {
                throw new InvalidLibraryException(libraryName, _provider.Id);
            }

            string libraryId = LibraryIdToNameAndVersionConverter.Instance.GetLibraryId(libraryName, version, _provider.Id);
            try
            {
                IEnumerable<string> libraryFiles = await GetLibraryFilesAsync(libraryName, version, cancellationToken);

                return new UnpkgLibrary
                {
                    Version = version,
                    Files = libraryFiles.ToDictionary(k => k, b => false),
                    Name = libraryName,
                    ProviderId = _provider.Id,
                };
            }
            catch
            {
                throw new InvalidLibraryException(libraryId, _provider.Id);
            }
        }

        private async Task<IEnumerable<string>> GetLibraryFilesAsync(string libraryName, string version, CancellationToken cancellationToken)
        {
            List<string> result = new List<string>();

            string libraryFileListUrl = string.Format(LibraryFileListUrlFormat, libraryName, version);
            JObject fileListObject = await WebRequestHandler.Instance.GetJsonObjectViaGetAsync(libraryFileListUrl, cancellationToken).ConfigureAwait(false);

            if (fileListObject != null)
            {
                GetFiles(fileListObject, result);
            }

            return result;
        }

        private void GetFiles(JObject fileObject, List<string> files)
        {
            // Parse JSON document returned by unpkg.com/libraryname@version/?meta
            // It looks something like
            // {
            //   "type" : "directory",
            //   "path" : "/"
            //   "files" : [
            //     {
            //       "type" : "directory"
            //       "path" : "/umd"
            //       "files" : [
            //         {
            //           "type" : "file",
            //           "path" : "/umd/react.development.js"
            //         },
            //         {
            //           "type" : "file",
            //           "path" : "/umd/react.production.min.js"
            //         }
            //       ]
            //     },
            //     {
            //       "type" : "file",
            //       "path" : "/index.js"
            //     }
            //   ]
            // }

            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            if (fileObject != null)
            {
                JValue type = fileObject["type"] as JValue; // will be either "file" or "directory"
                if (type.Value as string == "file")
                {
                    JValue pathValue = fileObject["path"] as JValue;
                    string path = pathValue?.Value as string;

                    if (path != null && path.Length > 0)
                    {
                        // Don't include the leading "/" in the file paths, do you get dist/jquery.js rather than /dist/jquery.js
                        // We will want the user to always specify a relative path in the "Files" array of the library entry
                        files.Add(path.Substring(1));
                    }
                }
                else if (type.Value as string == "directory")
                {
                    JArray filesArray = fileObject["files"] as JArray;
                    if (filesArray != null)
                    {
                        foreach (JObject childFileObject in filesArray)
                        {
                            GetFiles(childFileObject, files);
                        }
                    }
                }
            }
        }

        public async Task<CompletionSet> GetLibraryCompletionSetAsync(string libraryNameStart, int caretPosition)
        {
            CompletionSet completionSet = new CompletionSet
            {
                Start = 0,
                Length = libraryNameStart.Length
            };

            List<CompletionItem> completions = new List<CompletionItem>();

            (string name, string version) = LibraryIdToNameAndVersionConverter.Instance.GetLibraryNameAndVersion(libraryNameStart, _provider.Id);

            // Typing '@' after the library name should have version completion.
            int at = name.LastIndexOf('@');
            name = at > -1 ? name.Substring(0, at) : name;

            try
            {
                // library name completion
                if (caretPosition < name.Length + 1)
                {
                    IEnumerable<string> packageNames = await NpmPackageSearch.GetPackageNamesAsync(libraryNameStart, CancellationToken.None);

                    foreach (string packageName in packageNames)
                    {
                        CompletionItem completionItem = new CompletionItem
                        {
                            DisplayText = packageName,
                            InsertionText = packageName,
                        };

                        completions.Add(completionItem);
                    }
                    
                    completionSet.CompletionType = CompletionSortOrder.AsSpecified;
                }

                // library version completion
                else
                {
                    completionSet.Start = name.Length + 1;
                    completionSet.Length = version.Length;

                    NpmPackageInfo npmPackageInfo = await NpmPackageInfoCache.GetPackageInfoAsync(name, CancellationToken.None);
                    foreach (SemanticVersion semVersion in npmPackageInfo.Versions)
                    {
                        string versionText = semVersion.ToString();
                        CompletionItem completionItem = new CompletionItem
                        {
                            DisplayText = versionText,
                            InsertionText = name + "@" + versionText
                        };

                        completions.Add(completionItem);
                    }

                    completionSet.CompletionType = CompletionSortOrder.Version;
                }

                completionSet.Completions = completions;
            }
            catch (Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return completionSet;
        }

        public async Task<IReadOnlyList<ILibraryGroup>> SearchAsync(string term, int maxHits, CancellationToken cancellationToken)
        {
            List<ILibraryGroup> libraryGroups = new List<ILibraryGroup>();

            try
            {
                IEnumerable<string> packageNames = await NpmPackageSearch.GetPackageNamesAsync(term, CancellationToken.None);
                libraryGroups = packageNames.Select(packageName => new UnpkgLibraryGroup(packageName)).ToList<ILibraryGroup>();
            }
            catch (Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return libraryGroups;
        }
    }
}
