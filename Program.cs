using Lucene.Net.Index;
using Lucene.Net.Store;
using NuGet.Indexing;
using NuGet.Services.Metadata.Catalog.Collecting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SearchIndexFromCatalog
{
    class Program
    {
        static int Main(string[] args)
        {
            List<string> argsList = args.ToList();

            if (args.Length < 2)
            {
                Console.WriteLine(@"\
Usage:
    program.exe [--resetIndex] indexDirectory catalogPath [resolverBlobPath]
");

                return 1;
            }

            bool resetIndex = (0 < argsList.RemoveAll(arg => arg == "--resetIndex"));

            BuildSearchIndexFromCatalog(args[1], args.Length == 3 ? args[2] : null, args[0], resetIndex).Wait();

            return 0;
        }

        static async Task BuildSearchIndexFromCatalog(string catalogPath, string resolverBlobPath, string dest, bool resetIndex)
        {
            Directory dir = new Lucene.Net.Store.SimpleFSDirectory(new System.IO.DirectoryInfo(dest));

            if (resetIndex)
            {
                PackageIndexing.CreateNewEmptyIndex(dir);
            }

            var catalog = new SearchIndexFromCatalogCollector(dir, "{0}/{1}.json");

            if (resolverBlobPath != null)
            {
                catalog.DependentCollections = new List<Uri> { new Uri(resolverBlobPath) };
            }

            CollectorCursor cursor;

            //try
            //{
            //    IndexWriter writer = SearchIndexFromCatalogCollector.CreateIndexWriter(dir, false);
            //}
            //catch
            //{
                cursor = CollectorCursor.None;
            //}

            CollectorCursor finalCursor = await catalog.Run(
                new NuGet.Services.Metadata.Catalog.Collecting.CollectorHttpClient(),
                new Uri(catalogPath),
                cursor);

            return;
        }
    }
}
