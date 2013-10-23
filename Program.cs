using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using SDS.Providers.MPRRouter;
using log4net;
using log4net.Config;

namespace TestUpdateLastRefreshedDateInRentalsMongo
{
    class ListingDataProvider
    {
        public string ProviderId { get; private set; }
        public DateTime? LastUpdated { get; private set; }

        public ListingDataProvider()
        {

        }

        public ListingDataProvider(string providerId, DateTime? lastUpdated)
        {
            ProviderId = providerId;
            LastUpdated = lastUpdated;
        }
    }


    class Program
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(Program));
        private static MPRRedirect _mprRedirect;
        private static MongoClient _mongoClient;
        private static MongoServer _mongoServer;
        private static MongoDatabase _mongoDB;
        private static MongoCollection _mongoCollection;


        private static IEnumerable<ListingDataProvider> GetLastRefreshDateAndTime(DatabasePartition partition)
        {
            // Exclude community listings (-50)
            const string sql = @"SELECT SourceAbbreviation AS ProviderId, when_last_freshed AS LastUpdated 
                                 FROM [Property].[vw].[zdata_source] WITH (NOLOCK) 
                                 WHERE dataSourceID <> -50";

            using (var conn = new SqlConnection(partition.ToConnectionString(SDSDatabaseNames.Property)))
            {
                conn.Open();
                return conn.Query<ListingDataProvider>(sql).ToList();
            }
        }


        public static void UpdateLastRefreshedDateInRentalsMongo()
        {
            try
            {
                DoUpdate();
            }
            catch (Exception ex)
            {
                _log.ErrorFormat("Got exception calling update: {0}", ex);
                throw;
            }
        }

        private static void DoUpdate()
        {
        _log.Info("Updating listing.last_refreshed");
            var results = GetLastRefreshDateAndTime(_mprRedirect.DatabasePartitions.Values.First()).ToList();
            _log.DebugFormat("{0} providers found.", results.Count());

            var now = DateTime.Now.ToUniversalTime();
            foreach (var listingDataProvider in results)
            {
                if (!listingDataProvider.LastUpdated.HasValue)
                {
                    _log.DebugFormat("Data provider '{0}' has no listing data...skipping", listingDataProvider.ProviderId);
                    continue;
                }

                var lastRefreshedAsUtc = listingDataProvider.LastUpdated.Value.ToUniversalTime();
                if (listingDataProvider.LastUpdated.Value > lastRefreshedAsUtc)
                {
                    _log.WarnFormat("The last_refresh date({1}) in {0} is after Now({2}).  Changing to Now({2}).)", listingDataProvider.ProviderId, lastRefreshedAsUtc, now);
                    lastRefreshedAsUtc = now;
                }

                _log.DebugFormat("Updating {0}.", listingDataProvider.ProviderId);

                var query = Query.EQ("listing.mls.abbreviation", listingDataProvider.ProviderId);

                var update = Update.Set("listing.last_refreshed", lastRefreshedAsUtc);

                _mongoCollection.Update(query, update, UpdateFlags.Multi);

                _log.DebugFormat("Completed {0}.", listingDataProvider.ProviderId);
            }

            _log.Debug("Completed updating last_refreshed");
        }

        static void Main(string[] args)
        {
            BasicConfigurator.Configure();
            _log.Debug("Here is a debug log.");

            _mprRedirect = new MPRRedirect();

            var mongoConnection = ConfigurationManager.ConnectionStrings["RentalMongoDB"].ConnectionString;
            _mongoClient = new MongoClient(mongoConnection);
            _mongoServer = _mongoClient.GetServer();
            _mongoDB = _mongoServer.GetDatabase(ConfigurationManager.AppSettings["MongoRentalDatabase"]);
            _mongoCollection = _mongoDB.GetCollection(ConfigurationManager.AppSettings["MongoRentalCollection"]);

            UpdateLastRefreshedDateInRentalsMongo();
        }
    }
}
