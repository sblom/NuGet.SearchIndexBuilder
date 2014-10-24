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
        static void Main(string[] args)
        {
            BuildSearchIndexFromCatalog().Wait();
        }

        static async Task BuildSearchIndexFromCatalog()
        {
            Directory dir = new Lucene.Net.Store.SimpleFSDirectory(new System.IO.DirectoryInfo("c:\\data\\lucene.index"));
            PackageIndexing.CreateNewEmptyIndex(dir);

            var catalog = new SearchIndexFromCatalogCollector(dir, new LocalFrameworksList(".\\projectframeworks.v1.json"),
                "{0}/{1}.json")
            /*{
                DependentCollections = new List<Uri> { new Uri("https://nugetsblom20140319.blob.core.windows.net/cursor-temp/") }
            }*/;

            await catalog.Run(
                new NuGet.Services.Metadata.Catalog.Collecting.CollectorHttpClient(),
                new Uri("https://nugetjuste.blob.core.windows.net/ver01/catalog/index.json"),
                CollectorCursor.None);

            return;
        }
    }
}
