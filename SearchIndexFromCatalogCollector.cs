﻿using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet;
using NuGet.Indexing;
using NuGet.Services.Metadata.Catalog.Collecting;
using NuGetGallery;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace SearchIndexFromCatalog
{
    public class SearchIndexFromCatalogCollector: BatchCollector
    {
        const int MaxDocumentsPerCommit = 800;      //  The maximum number of Lucene documents in a single commit. The min size for a segment.
        const int MergeFactor = 10;                 //  Define the size of a file in a level (exponentially) and the count of files that constitue a level
        const int MaxMergeDocs = 7999;              //  Except never merge segments that have more docs than this

        Lucene.Net.Store.Directory _directory;
        string _packageTemplate;
        NuGet3.Client.Core.JsonLdPageCache _cache;

        static Dictionary<string, string> _frameworkNames = new Dictionary<string, string>();

        public SearchIndexFromCatalogCollector(Lucene.Net.Store.Directory directory, string registrationTemplate): base(100)
        {
            _directory = directory;
            _packageTemplate = registrationTemplate;
        }

        protected override async Task<bool> ProcessBatch(CollectorHttpClient client, IList<Newtonsoft.Json.Linq.JObject> items, Newtonsoft.Json.Linq.JObject context)
        {
            _cache = new NuGet3.Client.Core.JsonLdPageCache(client);

            PerfEventTracker perfTracker = new PerfEventTracker();
            TextWriter log = Console.Out;

            Task<Document>[] packages = items.Select(x => MakePackage(client, x)).Where(x => x != null).ToArray();

            await Task.WhenAll(packages);

            foreach (Task<Document> pkg in packages)
            {
                if (pkg != null && pkg.Result != null) Console.WriteLine("Package: {0}", pkg.Result.Get("Id"));
            }

            using (IndexWriter indexWriter = CreateIndexWriter(_directory, create: false))
            {
                foreach (Document doc in packages.Select(x => x.Result))
                {
                    if (doc != null)
                    {
                        Console.WriteLine("Index document: {0}", doc.Get("Id"));
                        indexWriter.AddDocument(doc);
                    }
                }
                indexWriter.Commit();
            }

            return true;
        }

        internal static IndexWriter CreateIndexWriter(Lucene.Net.Store.Directory directory, bool create)
        {
            IndexWriter indexWriter = new IndexWriter(directory, new PackageAnalyzer(), create, IndexWriter.MaxFieldLength.UNLIMITED);
            indexWriter.MergeFactor = MergeFactor;
            indexWriter.MaxMergeDocs = MaxMergeDocs;

            indexWriter.SetSimilarity(new CustomSimilarity());

            //StreamWriter streamWriter = new StreamWriter(Console.OpenStandardOutput());
            //indexWriter.SetInfoStream(streamWriter);
            //streamWriter.Flush();

            // this should theoretically work but appears to cause empty commit commitMetadata to not be saved
            //((LogMergePolicy)indexWriter.MergePolicy).SetUseCompoundFile(false);
            return indexWriter;
        }

        private async Task<Document> MakePackage(CollectorHttpClient client, JObject catalogEntry)
        {
            string resultString = await client.GetStringAsync((string)catalogEntry["@id"]);
            JObject result = JObject.Parse(resultString);

            string id = (string)result["id"];
            string version = (string)result["version"];

            string packageUrl = string.Format(_packageTemplate, id.ToLowerInvariant(), version.ToLowerInvariant());

            //try
            //{
            //    JObject package = (JObject)await _cache.Fetch(packageUrl);
            //    package["id"] = result["id"];
            //    if (((IDictionary<string,JToken>)package).ContainsKey("http://schema.nuget.org/schema#published"))
            //    {
            //        // This is to handle some strange artifacts from http://preview.nuget.org/ver3-ctp1/packageregistrations/1/...
            //        package["published"] = package["http://schema.nuget.org/schema#published"];
            //    }
                return CreateLuceneDocument(result, packageUrl);
            //}
            //catch (Exception ex)
            //{
            //    Console.WriteLine("Couldn't create document: {0} ({1})", (string)catalogEntry["url"], ex.Message);
            //    return null;
            //}
        }

        private static void Add(Document doc, string name, string value, Field.Store store, Field.Index index, Field.TermVector termVector, float boost = 1.0f)
        {
            if (value == null)
            {
                return;
            }

            Field newField = new Field(name, value, store, index, termVector);
            newField.Boost = boost;
            doc.Add(newField);
        }

        private static void Add(Document doc, string name, int value, Field.Store store, Field.Index index, Field.TermVector termVector, float boost = 1.0f)
        {
            Add(doc, name, value.ToString(CultureInfo.InvariantCulture), store, index, termVector, boost);
        }

        private static float DetermineLanguageBoost(string id, string language)
        {
            if (!string.IsNullOrWhiteSpace(language))
            {
                string languageSuffix = "." + language.Trim();
                if (id.EndsWith(languageSuffix, StringComparison.InvariantCultureIgnoreCase))
                {
                    return 0.1f;
                }
            }
            return 1.0f;
        }

        // ----------------------------------------------------------------------------------------------------------------------------------------
        private static Document CreateLuceneDocument(JObject package, string packageUrl)
        {
            Document doc = new Document();

            if (((IDictionary<string, JToken>)package).ContainsKey("supportedFrameworks"))
            {
                foreach (JToken fwk in package["supportedFrameworks"])
                {
                    string framework = (string)fwk;

                    FrameworkName frameworkName = VersionUtility.ParseFrameworkName(framework);

                    lock (_frameworkNames)
                    {
                        if (!_frameworkNames.ContainsKey(framework))
                        {
                            _frameworkNames.Add(framework, frameworkName.FullName);
                            Console.WriteLine("New framework string: {0}, {1}", framework, frameworkName.FullName);
                            using (var writer = File.AppendText("frameworks.txt"))
                            {
                                writer.WriteLine("{0}: {1}", framework, frameworkName.FullName);
                            }
                        }
                    }
                    Add(doc, "TargetFramework", framework == "any" ? "any" : frameworkName.ToString(), Field.Store.YES /* NO */, Field.Index.NO, Field.TermVector.NO);
                }
            }

            //  Query Fields

            float titleBoost = 3.0f;
            float idBoost = 2.0f;

            if (package["tags"] == null)
            {
                titleBoost += 0.5f;
                idBoost += 0.5f;
            }

            string title = (string)(package["title"] ?? package["id"]);

            Add(doc, "Id", (string)package["id"], Field.Store.YES, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS, idBoost);
            Add(doc, "IdAutocomplete", (string)package["id"], Field.Store.YES, Field.Index.ANALYZED, Field.TermVector.NO);
            Add(doc, "TokenizedId", (string)package["id"], Field.Store.NO, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS, idBoost);
            Add(doc, "ShingledId", (string)package["id"], Field.Store.NO, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS, idBoost);
            Add(doc, "Version", (string)package["version"], Field.Store.YES, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS, idBoost);
            Add(doc, "Title", title, Field.Store.YES /* NO */, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS, titleBoost);
            Add(doc, "Tags", string.Join(", ", (package["tags"] ?? new JArray()).Select(s => (string)s)), Field.Store.YES /* NO */, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS, 1.5f);
            Add(doc, "Description", (string)package["description"], Field.Store.YES /* NO */, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS);
            Add(doc, "Authors", string.Join(", ", package["authors"].Select(s => (string)s)), Field.Store.YES /* NO */, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS);

            // TODO: We don't have owners in the v3 flow
            //foreach (User owner in package.PackageRegistration.Owners)
            //{
            //    Add(doc, "Owners", owner.Username, Field.Store.NO, Field.Index.ANALYZED, Field.TermVector.WITH_POSITIONS_OFFSETS);
            //}

            //  Sorting:

            doc.Add(new NumericField("PublishedDate", Field.Store.YES, true).SetIntValue(int.Parse(package["published"].ToObject<DateTime>().ToString("yyyyMMdd"))));

            DateTime lastEdited = (DateTime)(package["lastEdited"] ?? package["published"]);
            doc.Add(new NumericField("EditedDate", Field.Store.YES, true).SetIntValue(int.Parse(lastEdited.ToString("yyyyMMdd"))));

            string displayName = String.IsNullOrEmpty((string)package["title"]) ? (string)package["id"] : (string)package["title"];
            displayName = displayName.ToLower(CultureInfo.CurrentCulture);
            Add(doc, "DisplayName", displayName, Field.Store.NO, Field.Index.NOT_ANALYZED, Field.TermVector.NO);

            // Add(doc, "IsLatest", (bool)package["isAbsoluteLatestVersion"] ? 1 : 0, Field.Store.NO, Field.Index.NOT_ANALYZED, Field.TermVector.NO);
            // Add(doc, "IsLatestStable", (bool)package["isLatestVersion"] ? 1 : 0, Field.Store.NO, Field.Index.NOT_ANALYZED, Field.TermVector.NO);

            // TODO: Looks like we don't have Listed plumbed through?
            // Add(doc, "Listed", 1 /* (bool)package["listed"] ? 1 : 0 */, Field.Store.NO, Field.Index.NOT_ANALYZED, Field.TermVector.NO);

            // TODO: There's no feed support in v3.
            //
            //if (documentData.Data.Feeds != null)
            //{
            //    foreach (string feed in documentData.Data.Feeds)
            //    {
            //        //  Store this to aid with debugging
            //        Add(doc, "CuratedFeed", feed, Field.Store.YES, Field.Index.NOT_ANALYZED, Field.TermVector.NO);
            //    }
            //}

            //  Add Package Key so we can quickly retrieve ranges of packages (in order to support the synchronization with the gallery)

            // doc.Add(new NumericField("Key", Field.Store.YES, index: true).SetIntValue(package.Key));

            // doc.Add(new NumericField("Checksum", Field.Store.YES, true).SetIntValue(documentData.Data.Checksum));

            //  Data we want to store in index - these cannot be queried

            Add(doc, "Url", packageUrl.ToString(), Field.Store.YES, Field.Index.NO, Field.TermVector.NO);

            // Add facets
            //foreach (var facet in documentData.DocFacets)
            //{
            //    Add(doc, "Facet", facet, Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS, Field.TermVector.NO);
            //}

            doc.Boost = DetermineLanguageBoost((string)package["id"], (string)package["language"]);

            return doc;
        }
    }
}
